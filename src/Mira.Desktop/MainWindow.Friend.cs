using System.Windows;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.Tailscale;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// A friend's invite opened in Mira (ogsas, 2026-11-20: maquette A « Le lien »): mira://join#… from miracinema.app/join,
/// a key that brings this PC onto the friend's tailnet through the home's own node (<see cref="FamilyNode"/>, which
/// reaches nothing but that Jellyfin), and an invitation the friend's extension holds until they let this device in
/// (« Laisser entrer »). Said before anything: what the friend sees of this device. Then a session of the account the
/// friend made, saved as any other (device id « mira-friend-… », whose node starts with Mira).
/// </summary>
public partial class MainWindow
{
    private const string FriendDevicePrefix = "mira-friend-";
    /// <summary>The invite on the page; null once used (then <see cref="_friendWaiting"/>).</summary>
    private HomeLink? _friendLink;
    private FriendWait? _friendWaiting;
    private CancellationTokenSource? _friendPoll;

    /// <summary>A friend's invite opened (mira://join#…): the page of maquette A, nothing done before « Start watching ».</summary>
    private void OpenFriendLink(HomeLink link)
    {
        RestoreFromWindows();
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        // One node of the kind per PC: a home joined by a link holds it.
        if (_home is { ThroughTailnet: true }) { ShowFriend(null, FriendText.InHomeAlready); return; }
        _friendLink = link;
        ShowFriend(null, null);
    }

    /// <summary>The page in one of its states: the invite (no wait yet), waiting for the friend, or an ending said.</summary>
    private void ShowFriend(FriendWait? waiting, string? error, bool ended = false)
    {
        AttachFamily();
        var asking = waiting is null && !ended && _friendLink is not null;
        FriendHeading.Text = ended ? error ?? FriendText.Refused : waiting is not null ? L("Presque prêt", "Almost there") : FriendText.Invited;
        FriendLine.Text = waiting is not null ? FriendText.WaitingForHost : ended ? FriendText.UsedLine : FriendText.PrivateLine;
        FriendSeen.Text = FriendText.Visible;
        FriendPrivacy.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
        FriendError.Text = ended ? "" : error ?? "";
        FriendStartText.Text = FriendText.Start;
        FriendStart.Visibility = asking && error is null ? Visibility.Visible : Visibility.Collapsed;
        FriendStart.IsEnabled = true;
        FriendNotNow.Content = asking ? FriendText.NotNow : L("Fermer", "Close");
        FriendAddress.Content = FriendText.HaveAddress;
        FriendAddress.Visibility = waiting is null && _client is null ? Visibility.Visible : Visibility.Collapsed;
        ShowFamilyPage(FamilyFriend);
        Dispatcher.BeginInvoke(() => (FriendStart.IsVisible ? FriendStart : FriendNotNow).Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>« Start watching »: the node on the friend's tailnet by the link's key, the invite used, then the wait.</summary>
    private async void FriendStart_Click(object sender, RoutedEventArgs e)
    {
        if (_familyBusy || _friendLink is not { } link) return;
        // Already a friend of another server: its node leaves for this one.
        var other = _profile.LoadConnection() is { } saved && saved.DeviceId.StartsWith(FriendDevicePrefix, StringComparison.Ordinal) && saved.Server != link.Address;
        if (other && !await ConfirmAsync(L("Changer de serveur ?", "Switch servers?"), L("Cet appareil quittera le serveur de ton autre ami. Il faudra une nouvelle invitation pour y revenir.", "This device will leave your other friend’s server. Coming back will take a new invite."), L("Continuer", "Continue"), L("Annuler", "Cancel"))) return;
        _familyBusy = true; FriendStart.IsEnabled = false; FriendError.Text = "";
        try
        {
            if (!Uri.TryCreate(link.Address, UriKind.Absolute, out var address)) { FriendError.Text = HomeText.LinkBroken; return; }
            // The node's state is logged in on the other friend's tailnet: a key would not move it.
            if (other) { await FamilyNode.LeaveAsync(); try { _profile.ClearConnection(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { } await DisconnectServicesAsync(); }
            Busy(FriendText.Reaching);
            string? code;
            try { code = await FamilyNode.StartAsync(address.Host, link.Key); }
            finally { Busy(null); }
            if (code is not null)
            {
                // A spent key: the invite was used, or it ended.
                if (code == TailnetCodes.NeedsLogin) { _friendLink = null; ShowFriend(null, FriendText.Used, ended: true); }
                else FriendError.Text = HomeText.Node(code, L("ton ami", "your friend"));
                return;
            }
            using var client = HomeClient(link.Address);
            var wait = await client.FriendRedeemAsync(link.Key, _profile.DeviceId, HomeText.DeviceName("Mira", Environment.MachineName));
            var waiting = new FriendWait(client.Connection.Server, wait);
            _friendLink = null;
            try { _profile.SaveFriendWait(waiting); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
            WaitForFriend(waiting);
        }
        catch (FriendRefusedException ex) when (ex.State is "used" or "ended")
        {
            _friendLink = null;
            ShowFriend(null, ex.State == "used" ? FriendText.Used : FriendText.Ended, ended: true);
            FriendLine.Text = ex.State == "used" ? FriendText.UsedLine : FriendText.EndedLine;
        }
        catch (FriendRefusedException ex) { FriendError.Text = ex.Message; }
        catch (Exception ex) when (IsExpected(ex)) { FriendError.Text = L("Le serveur de ton ami n’a pas répondu. Réessaie dans un instant.", "Your friend’s server didn’t answer. Try again in a moment."); }
        finally { _familyBusy = false; FriendStart.IsEnabled = true; }
    }

    /// <summary>Waiting for the friend to let this device in: asked every few seconds while the page shows, and at each start of Mira.</summary>
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
                using var client = HomeClient(waiting.Server);
                if (await client.FriendWaitAsync(waiting.Wait, ct) is { } connection)
                {
                    if (ct.IsCancellationRequested) return;
                    await FriendInAsync(connection);
                    return;
                }
                quiet = 0;
                if (FamilyFriend.IsVisible) FriendError.Text = "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (FriendRefusedException ex) when (ex.State is "refused" or "ended")
            {
                await ForgetFriendWaitAsync();
                if (FamilyOverlay.Visibility == Visibility.Visible) ShowFriend(null, ex.State == "ended" ? FriendText.Ended : FriendText.Refused, ended: true);
                else SetNotice(FriendText.Refused);
                FriendLine.Text = FriendText.UsedLine;
                return;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // The friend's PC asleep, a network change: said after a while, still asked.
                // The node signed out of the friend's tailnet: this device was taken off it (refused long ago).
                if (FamilyNode.State == TailnetStates.NeedsLogin)
                {
                    await ForgetFriendWaitAsync();
                    if (FamilyOverlay.Visibility == Visibility.Visible) ShowFriend(null, FriendText.Refused, ended: true);
                    else SetNotice(FriendText.Refused);
                    FriendLine.Text = FriendText.UsedLine;
                    return;
                }
                if (++quiet >= 4 && FamilyFriend.IsVisible) FriendError.Text = L("Le serveur de ton ami ne répond pas pour l’instant. Mira réessaie.", "Your friend’s server isn’t answering right now. Mira keeps trying.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(quiet > 0 ? 10 : 4), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Let in: the session saved and opened as any other, the page gone.</summary>
    private async Task FriendInAsync(Connection connection)
    {
        _friendPoll?.Cancel(); _friendPoll = null; _friendWaiting = null;
        try { _profile.ClearFriendWait(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        var previous = _profile.LoadConnection();
        _profile.SaveConnection(connection);
        if (LoginOverlay.Visibility == Visibility.Visible) LoginOverlay.Visibility = Visibility.Collapsed;
        await HideFamilyAsync();
        await ActivateConnectionAsync(connection);
        // Signed in with an account of its own before: that session ends on its Jellyfin, nothing here could revoke it later.
        if (previous is not null && !previous.DeviceId.StartsWith(FriendDevicePrefix, StringComparison.Ordinal) && !previous.DeviceId.StartsWith(FamilyNodePrefix, StringComparison.Ordinal)
            && (previous.Server != connection.Server || previous.UserId != connection.UserId)) _ = EndReplacedSessionAsync(previous);
    }

    private async Task ForgetFriendWaitAsync()
    {
        _friendPoll?.Cancel(); _friendPoll = null; _friendWaiting = null;
        try { _profile.ClearFriendWait(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        // The node was only for this server: off the tailnet, unless a session still goes through it.
        if (_profile.LoadConnection() is not { } saved || !saved.DeviceId.StartsWith(FriendDevicePrefix, StringComparison.Ordinal)) await FamilyNode.LeaveAsync();
    }

    /// <summary>« Not now » (the invite stays usable) or « Close »: back to what was there; a wait goes on at the next start.</summary>
    private async void FriendNotNow_Click(object sender, RoutedEventArgs e) => await LeaveFriendPageAsync();

    private async Task LeaveFriendPageAsync()
    {
        _friendPoll?.Cancel(); _friendPoll = null;
        _friendLink = null;
        await HideFamilyAsync();
        if (_client is null && LoginOverlay.Visibility != Visibility.Visible) { Motion.Reveal(LoginOverlay); ShowSignInForm(); FocusLogin(); }
    }

    /// <summary>« I have an address instead »: the usual sign-in.</summary>
    private async void FriendAddress_Click(object sender, RoutedEventArgs e)
    {
        await LeaveFriendPageAsync();
        if (_client is not null) Account_Click(this, new());
    }

    /// <summary>
    /// At Mira's start: a wait left open goes on (its page shown), and a friend's session first brings its node up (the
    /// friend's Jellyfin is only reached through it). True when the start is taken care of here.
    /// </summary>
    private async Task<bool> OpenFriendAtStartAsync()
    {
        FriendWait? waiting;
        try { waiting = _profile.LoadFriendWait(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { waiting = null; }
        if (waiting is not null && Uri.TryCreate(waiting.Server, UriKind.Absolute, out var waitAt))
        {
            LoginOverlay.Visibility = Visibility.Visible;
            WaitForFriend(waiting);
            _ = FamilyNode.StartAsync(waitAt.Host, null);
            return true;
        }
        if (_profile.LoadConnection() is not { } saved || !saved.DeviceId.StartsWith(FriendDevicePrefix, StringComparison.Ordinal) || !Uri.TryCreate(saved.Server, UriKind.Absolute, out var at)) return false;
        var code = await FamilyNode.StartAsync(at.Host, null);
        // Signed out without a key: the friend took this device off their network.
        if (code == TailnetCodes.NeedsLogin) { SetNotice(FriendText.Removed); }
        await ActivateConnectionAsync(saved);
        return true;
    }
}
