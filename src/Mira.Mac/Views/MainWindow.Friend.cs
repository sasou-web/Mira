using Mira.Core;
using Mira.Core.Family;
using Mira.Core.Tailscale;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// A friend's invite opened in Mira on the Mac, as on Windows (MainWindow.Friend.cs there; ogsas, 2026-11-20: maquette A
/// « Le lien »): mira://join#… brings this Mac onto the friend's tailnet through the home's own node, which reaches nothing
/// but that Jellyfin, then waits for the friend to let it in (« Laisser entrer »). Said before anything: what the friend
/// sees of this Mac. Then a session of the account the friend made (device id « mira-friend-… »).
/// </summary>
public sealed partial class MainWindow
{
    private const string FriendDevicePrefix = "mira-friend-";
    private HomeLink? _friendLink;
    private FriendWait? _friendWaiting;
    private CancellationTokenSource? _friendPoll;

    /// <summary>A friend's invite opened: its page, nothing done before « Start watching ».</summary>
    private void OpenFriendLink(HomeLink link)
    {
        // One node of the kind per Mac: a home joined by a link holds it.
        if (_home is { ThroughTailnet: true }) { ShowFriend(null, FriendText.InHomeAlready); return; }
        _friendLink = link;
        ShowFriend(null, null);
    }

    private void ShowFriend(FriendWait? waiting, string? error, bool ended = false, string? endedLine = null)
    {
        var asking = waiting is null && !ended && _friendLink is not null;
        RevealFamily().ShowFriend(
            ended ? error ?? FriendText.Refused : waiting is not null ? L("Presque prêt", "Almost there") : FriendText.Invited,
            waiting is not null ? FriendText.WaitingForHost : ended ? endedLine ?? FriendText.UsedLine : FriendText.PrivateLine,
            ended ? null : error, asking, address: waiting is null && Session is null, close: asking ? FriendText.NotNow : L("Fermer", "Close"));
    }

    /// <summary>« Start watching »: the node on the friend's tailnet by the link's key, the invite used, then the wait.</summary>
    private async Task FriendStartAsync()
    {
        if (_familyBusy || _friendLink is not { } link || _family is not { } view) return;
        var saved = await Task.Run(Profile.LoadConnection);
        var other = saved is not null && saved.DeviceId.StartsWith(FriendDevicePrefix, StringComparison.Ordinal) && saved.Server != link.Address;
        if (other && !await ConfirmAsync(L("Changer de serveur ?", "Switch servers?"), L("Ce Mac quittera le serveur de ton autre ami. Il faudra une nouvelle invitation pour y revenir.", "This Mac will leave your other friend’s server. Coming back will take a new invite."), L("Continuer", "Continue"), L("Annuler", "Cancel"))) return;
        _familyBusy = true; view.SetFriendBusy(true); view.FriendError(null);
        try
        {
            if (!Uri.TryCreate(link.Address, UriKind.Absolute, out var address)) { view.FriendError(HomeText.LinkBroken); return; }
            // The node's state is logged in on the other friend's tailnet: a key would not move it.
            if (other)
            {
                await FamilyNode.LeaveAsync();
                try { await Task.Run(Profile.ClearConnection); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                await CloseSessionAsync();
            }
            Busy(FriendText.Reaching);
            string? code;
            try { code = await FamilyNode.StartAsync(address.Host, link.Key); }
            finally { Busy(null); }
            if (code is not null)
            {
                // A spent key: the invite was used, or it ended.
                if (code == TailnetCodes.NeedsLogin) { _friendLink = null; ShowFriend(null, FriendText.Used, ended: true); }
                else view.FriendError(HomeText.Node(code, L("ton ami", "your friend")));
                return;
            }
            FriendWait waiting;
            using (var client = HomeClient(link.Address))
                waiting = new FriendWait(client.Connection.Server, await client.FriendRedeemAsync(link.Key, Profile.DeviceId, HomeText.DeviceName("Mira", MacName())));
            _friendLink = null;
            try { await Task.Run(() => Profile.SaveFriendWait(waiting)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            WaitForFriend(waiting);
        }
        catch (FriendRefusedException ex) when (ex.State is "used" or "ended")
        {
            _friendLink = null;
            ShowFriend(null, ex.State == "used" ? FriendText.Used : FriendText.Ended, ended: true, ex.State == "used" ? FriendText.UsedLine : FriendText.EndedLine);
        }
        catch (FriendRefusedException ex) { view.FriendError(ex.Message); }
        catch (Exception ex) when (Errors.Expected(ex)) { view.FriendError(L("Le serveur de ton ami n’a pas répondu. Réessaie dans un instant.", "Your friend’s server didn’t answer. Try again in a moment.")); }
        finally { _familyBusy = false; view.SetFriendBusy(false); }
    }

    /// <summary>Waiting for the friend to let this Mac in: asked every few seconds, and at each start of Mira.</summary>
    private void WaitForFriend(FriendWait waiting)
    {
        _friendWaiting = waiting;
        ShowFriend(waiting, null);
        _friendPoll?.Cancel();
        var poll = _friendPoll = new CancellationTokenSource();
        _ = PollFriendAsync(waiting, poll.Token);
    }

    private async Task PollFriendAsync(FriendWait waiting, CancellationToken ct)
    {
        var quiet = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Connection? connection;
                using (var client = HomeClient(waiting.Server)) connection = await client.FriendWaitAsync(waiting.Wait, ct);
                if (connection is not null)
                {
                    if (!ct.IsCancellationRequested) await FriendInAsync(connection);
                    return;
                }
                quiet = 0;
                if (Family is { Shown: FamilyView.Stage.Friend } view) view.FriendError(null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (FriendRefusedException ex) when (ex.State is "refused" or "ended")
            {
                await ForgetFriendWaitAsync();
                if (Family is not null) ShowFriend(null, ex.State == "ended" ? FriendText.Ended : FriendText.Refused, ended: true);
                else Notice(FriendText.Refused, tone: NoticeTone.Error);
                return;
            }
            catch (Exception ex) when (Errors.Expected(ex))
            {
                // The friend's PC asleep, a network change: said after a while, still asked.
                if (++quiet >= 4 && Family is { Shown: FamilyView.Stage.Friend } view) view.FriendError(L("Le serveur de ton ami ne répond pas pour l’instant. Mira réessaie.", "Your friend’s server isn’t answering right now. Mira keeps trying."));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(quiet > 0 ? 10 : 4), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Let in: the session saved and opened as any other, the page gone.</summary>
    private async Task FriendInAsync(Connection connection)
    {
        _friendPoll?.Cancel(); _friendPoll = null; _friendWaiting = null;
        try { await Task.Run(Profile.ClearFriendWait); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var previous = await Task.Run(Profile.LoadConnection);
        await Task.Run(() => Profile.SaveConnection(connection));
        HideFamily();
        await ActivateAsync(connection, _handler);
        if (previous is not null && !previous.DeviceId.StartsWith(FriendDevicePrefix, StringComparison.Ordinal) && !previous.DeviceId.StartsWith(FamilyDevicePrefix, StringComparison.Ordinal)
            && (previous.Server != connection.Server || previous.UserId != connection.UserId)) _ = EndReplacedSessionAsync(previous);
    }

    private async Task ForgetFriendWaitAsync()
    {
        _friendPoll?.Cancel(); _friendPoll = null; _friendWaiting = null;
        try { await Task.Run(Profile.ClearFriendWait); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // The node was only for this server: off the tailnet, unless a session still goes through it.
        if ((await Task.Run(Profile.LoadConnection))?.DeviceId.StartsWith(FriendDevicePrefix, StringComparison.Ordinal) != true) await FamilyNode.LeaveAsync();
    }

    /// <summary>« Not now » (the invite stays usable) or « Close »: back to what was there; a wait goes on at the next start.</summary>
    private Task LeaveFriendPageAsync()
    {
        _friendPoll?.Cancel(); _friendPoll = null;
        _friendLink = null;
        if (_family is { } view) _overlay.Children.Remove(view);
        if (Session is not null) _rail.IsVisible = true;
        else if (Login is null) ShowLogin();
        return Task.CompletedTask;
    }

    /// <summary>« I have an address instead »: the usual sign-in.</summary>
    private async Task FriendAddressAsync()
    {
        await LeaveFriendPageAsync();
        if (Session is not null) OpenAccount();
        else Login?.FocusServer();
    }

    /// <summary>
    /// At Mira's start: a wait left open goes on (its page shown), and a friend's session first brings its node up (the
    /// friend's Jellyfin is only reached through it). True when the start is taken care of here.
    /// </summary>
    private async Task<bool> OpenFriendAtStartAsync()
    {
        FriendWait? waiting;
        try { waiting = await Task.Run(Profile.LoadFriendWait); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { waiting = null; }
        if (waiting is not null && Uri.TryCreate(waiting.Server, UriKind.Absolute, out var waitAt))
        {
            ShowLogin();
            WaitForFriend(waiting);
            _ = FamilyNode.StartAsync(waitAt.Host, null);
            return true;
        }
        if (await Task.Run(Profile.LoadConnection) is not { } saved || !saved.DeviceId.StartsWith(FriendDevicePrefix, StringComparison.Ordinal) || !Uri.TryCreate(saved.Server, UriKind.Absolute, out var at)) return false;
        // Signed out without a key: the friend took this Mac off their network.
        if (await FamilyNode.StartAsync(at.Host, null) == TailnetCodes.NeedsLogin) Notice(FriendText.Removed, tone: NoticeTone.Error);
        var activating = ActivateAsync(saved);
        await OpenWhenReadyAsync(activating);
        await activating;
        return true;
    }
}
