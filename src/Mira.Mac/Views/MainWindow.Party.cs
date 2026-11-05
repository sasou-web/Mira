using System.Net;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Ta party » of another Jellyfin (a mira://party/… link whose Jellyfin is not the one this Mac is signed in to): joined
/// as its guest, with no account there (<see cref="JellyfinClient.OpenPartyAsync"/>), as on Windows (MainWindow.Party.cs).
/// Mira is then that party's, as it would be another account's: <see cref="Session"/> is the guest's, which the
/// extension's gateway lets reach the host's group and its title only; the party's card covers the window, the player
/// opens above it when the group plays. « Quitter la party » ends the guest's session, erases what this Mac kept of it,
/// and brings this Mac's own account back (or the sign-in). The first name is kept for the next party
/// (<see cref="PlayerSettings.PartyName"/>).
/// </summary>
public sealed partial class MainWindow
{
    private enum PartyStage { Name, Opening, Waiting, In, Ended }
    /// <summary>The party asked for (its Jellyfin, its code, who invites once its page said), until its guest opens.</summary>
    private (string Server, string Code, string? Host)? _partyAsked;
    /// <summary>The guest Mira is now, of a friend's party: <see cref="Session"/> is its session.</summary>
    private PartyGuest? _partyGuest;
    private PartyStage _partyStage;
    private PartyView? _party;
    private CancellationTokenSource? _partyWait;
    private bool _partyBusy, _partyLeaving;
    /// <summary>The way to Jellyfin of the account signed in last: the self-check's stand-in server, for the friend's too.</summary>
    private HttpMessageHandler? _handler;
    /// <summary>This Mac's own account, let go while a guest and opened again on leaving.</summary>
    private Connection? _ownConnection;
    /// <summary>This Mac is a friend's party guest: no library, no rail, nothing of this Mac's own account runs.</summary>
    public bool InParty => _partyGuest is not null;
    /// <summary>The party's card on screen (the self-check).</summary>
    public PartyView? Party => _party;

    /// <summary>The first name asked for a friend's party; who invites named once its page says (it answers without an account).</summary>
    private async Task AskPartyAsync(string server, string code)
    {
        if (_partyGuest is { } current && Together.GuestOrigin(current.Connection) == server.TrimEnd('/') && _partyAsked?.Code == code) { RevealParty(); return; }
        _partyAsked = (server, code, null);
        var view = RevealParty();
        view.FirstName = Settings.PartyName; view.ShowError(null);
        ShowParty(PartyStage.Name);
        Dispatcher.UIThread.Post(view.FocusName, DispatcherPriority.Loaded);
        string? host;
        try
        {
            using var page = new JellyfinClient(new(server, "", "", "", Together.GuestDevice(Profile.DeviceId)), _handler);
            host = await page.PartyHostAsync(code);
        }
        // Not reached now: asked anyway, joining says why.
        catch (Exception ex) when (Errors.Expected(ex)) { return; }
        if (_partyAsked is not { } asked || asked.Code != code || _partyStage != PartyStage.Name) return;
        if (host is null) { _partyAsked = null; ShowParty(PartyStage.Ended); return; }
        _partyAsked = asked with { Host = host };
        ShowParty(PartyStage.Name);
    }
    /// <summary>The party's card over the window, above the sign-in or the library, under the player.</summary>
    private PartyView RevealParty()
    {
        Activate();
        if (_party is null) _party = new PartyView(this, PartyPrimary, PartySecondary);
        if (!_overlay.Children.Contains(_party))
        {
            // Under the player when one plays (the group's title), above the rest.
            var at = Player is { } player && _overlay.Children.IndexOf(player) is >= 0 and var index ? index : _overlay.Children.Count;
            _overlay.Children.Insert(at, _party);
        }
        return _party;
    }
    private void HideParty() { if (_party is { } view) _overlay.Children.Remove(view); }
    private void ShowParty(PartyStage stage)
    {
        _partyStage = stage;
        if (_party is not { } view) return;
        var host = _partyGuest?.Host is { Length: > 0 } guestHost ? guestHost : _partyAsked?.Host;
        var named = host is { Length: > 0 } ? host : L("la personne qui t’invite", "whoever invited you");
        if (stage != PartyStage.Name) view.ShowError(null);
        switch (stage)
        {
            case PartyStage.Name or PartyStage.Opening:
                view.Show(Together.PartyAsk(host), Together.PartyAskLead, true, stage == PartyStage.Name, null,
                    stage == PartyStage.Opening ? L("Tu entres dans la soirée…", "Joining the party…") : L("Rejoindre la party", "Join the party"), stage == PartyStage.Name, L("Annuler", "Cancel"));
                break;
            case PartyStage.Waiting:
                view.Show(Together.WaitingText(named), Together.WaitingLead, false, false, null, _partyWait is null ? L("Chercher à nouveau", "Look again") : null, true, L("Quitter la party", "Leave the party"));
                break;
            case PartyStage.In:
                // The group's title plays on while this Mac closed its player: back into it where the group is.
                var back = Group.Queue is not null && Player is null ? L("Revenir au titre du groupe", "Back to the group’s title") : null;
                view.Show(Together.PartyIn(named), Together.PartyInLead(named), false, false, Group.Active ? Group.Group : null, back, true, L("Quitter la party", "Leave the party"));
                break;
            default:
                view.Show(Together.PartyOver, Together.PartyEnded, false, false, null, null, false, L("Fermer", "Close"));
                break;
        }
    }
    /// <summary>The card as the group is now (joined, its members, its title), while Mira is a party's guest.</summary>
    private void ShowPartyState()
    {
        if (!InParty || _partyLeaving || _partyStage == PartyStage.Ended) return;
        ShowParty(Group.Active ? PartyStage.In : PartyStage.Waiting);
    }
    /// <summary>The group changed while a guest: shown, or waited for again when the host's group is gone.</summary>
    private void PartyGroupChanged()
    {
        if (!InParty || _partyLeaving) return;
        if (!Group.Active && Session is { } session && _partyWait is null && _partyStage != PartyStage.Ended) _ = JoinPartyGroupAsync(session);
        else ShowPartyState();
    }
    private void PartyPrimary()
    {
        switch (_partyStage)
        {
            case PartyStage.Name: _ = OpenPartyGuestAsync(); break;
            case PartyStage.Waiting when Session is { } session: _ = JoinPartyGroupAsync(session); break;
            case PartyStage.In: _ = BackToGroupTitleAsync(); break;
        }
    }
    private void PartySecondary()
    {
        if (_partyGuest is null) { _partyAsked = null; HideParty(); return; }
        _ = LeavePartyAsync();
    }

    /// <summary>« Rejoindre la party »: a guest of its own for this Mac, under the first name typed; then Mira is theirs.</summary>
    private async Task OpenPartyGuestAsync()
    {
        if (_partyAsked is not { } asked || _partyBusy || _partyStage != PartyStage.Name || _party is not { } view) return;
        var name = Together.PartyName(view.FirstName);
        if (name.Length == 0) { view.ShowError(L("Écris ton prénom : c’est lui que le groupe voit.", "Type your first name: it’s what the group sees.")); view.FocusName(); return; }
        _partyBusy = true; view.ShowError(null);
        ShowParty(PartyStage.Opening);
        PartyGuest guest;
        try
        {
            JellyfinClient.DeviceName = "Mac";
            using var opener = new JellyfinClient(new(asked.Server, "", "", "", Together.GuestDevice(Profile.DeviceId)), _handler);
            guest = await opener.OpenPartyAsync(asked.Code, name);
        }
        catch (Exception ex) when (Errors.Expected(ex))
        {
            _partyBusy = false;
            if (_partyAsked?.Code != asked.Code) return;
            // Taken back or made anew by its host: nothing more to try with this link.
            if (ex is RelayRefusedException { StatusCode: HttpStatusCode.NotFound }) { _partyAsked = null; ShowParty(PartyStage.Ended); return; }
            ShowParty(PartyStage.Name);
            view.ShowError(ex is RelayRefusedException ? ex.Message : Errors.Friendly(ex));
            return;
        }
        _partyBusy = false;
        if (_partyAsked?.Code != asked.Code) return;
        if (Settings.PartyName != name) { Settings.PartyName = name; Profile.SaveSettings(Settings); }
        await BecomeGuestAsync(guest);
    }
    /// <summary>
    /// Mira as the party's guest: this Mac's account let go as for another account (its session stays on its Jellyfin,
    /// and comes back on leaving), the guest's WebSocket opened, then the host's group joined, or waited for.
    /// </summary>
    private async Task BecomeGuestAsync(PartyGuest guest)
    {
        await StopPlaybackAsync();
        await EndGuestSessionAsync();
        if (Session is { } own) _ownConnection = own.Connection;
        await CloseSessionAsync();
        Speed.Forget();
        Session session;
        try { session = new Session(guest.Connection, Profile, _handler); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // The guest's cache cannot be written (full disk): its session ended at once, this Mac's own account back.
            using (var client = new JellyfinClient(guest.Connection, _handler)) await client.LogoutAsync();
            ShowParty(PartyStage.Name);
            _party?.ShowError(L("Mira ne peut rien écrire dans son dossier de données (disque plein ?) : libère de la place, puis réessaie.", "Mira can’t write to its data folder (full disk?): free up some space, then try again."));
            await BringOwnAccountBackAsync(keepParty: true);
            return;
        }
        _partyGuest = guest; Session = session;
        // Nothing of the library under the party: a page left open would ask the guest's Jellyfin for what it never gives.
        _rail.IsVisible = false; _railButtons["downloads"].IsVisible = false;
        _history.Clear(); _forward.Clear(); Host(null);
        if (Login is { } login) _overlay.Children.Remove(login);
        _avatar.Text = guest.Connection.UserName[..1].ToUpperInvariant(); SetPhoto(null);
        void Listening(bool open) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(session, Session)) return;
            _listening = open; Group.Listening(session, open);
            if (!open) _ = CheckPartyAsync(session);
        });
        // The stand-in server of the self-check has no WebSocket: its messages are given by the check.
        if (_handler is null) session.Listen(() => { }, Listening, (type, data) => Dispatcher.UIThread.Post(() => Group.Message(session, type, data)));
        ShowParty(PartyStage.Waiting);
        await JoinPartyGroupAsync(session);
    }
    /// <summary>The host's group joined as soon as it is open (read every few seconds, three hours at most).</summary>
    private async Task JoinPartyGroupAsync(Session session)
    {
        if (_partyGuest is not { } guest || !ReferenceEquals(session, Session) || session.Lifetime.IsCancellationRequested || _partyLeaving) return;
        if (Group.Active) { ShowPartyState(); return; }
        _partyWait?.Cancel();
        var wait = _partyWait = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        wait.CancelAfter(TimeSpan.FromHours(3));
        ShowParty(PartyStage.Waiting);
        // Jellyfin says GroupJoined through the WebSocket: open first, or the answer would go unheard.
        for (var i = 0; !_listening && i < 40 && ReferenceEquals(session, Session); i++) await Task.Delay(250);
        Together.Group group;
        try { group = await Together.WaitForGroupAsync(ReadAsync, Together.PartyInvitation(session.Connection.Server, guest.Host), TimeSpan.FromSeconds(4), wait.Token); }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_partyWait, wait)) { _partyWait = null; ShowPartyState(); }
            return;
        }
        if (!ReferenceEquals(_partyWait, wait) || !ReferenceEquals(session, Session)) return;
        _partyWait = null;
        if (!Group.Active) await Group.EnterAsync((client, ct) => client.SyncJoinAsync(group.GroupId, ct));
        ShowPartyState();
        async Task<IReadOnlyList<Together.Group>?> ReadAsync(CancellationToken ct)
        {
            try { return await session.Client.SyncGroupsAsync(ct); }
            // The host took the link back (the guest's account is gone with it), or made a new one.
            catch (UnauthorizedAccessException) { _ = PartyEndedAsync(session); return null; }
            catch (Exception ex) when (Errors.Expected(ex) && !ct.IsCancellationRequested) { return null; }
        }
    }
    /// <summary>The guest's WebSocket closed: still a guest when the group can still be read, else the party is over for this link.</summary>
    private async Task CheckPartyAsync(Session session)
    {
        if (!ReferenceEquals(session, Session) || !InParty) return;
        try { await session.Client.SyncGroupsAsync(session.Lifetime); }
        catch (UnauthorizedAccessException) { await PartyEndedAsync(session); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    private async Task PartyEndedAsync(Session session)
    {
        if (!ReferenceEquals(session, Session) || !InParty || _partyStage == PartyStage.Ended) return;
        _partyWait?.Cancel(); _partyWait = null;
        ShowParty(PartyStage.Ended);
        await StopPlaybackAsync();
        RevealParty();
    }
    /// <summary>« Revenir au titre du groupe »: the player opened again on the group's title, where the group is.</summary>
    private async Task BackToGroupTitleAsync()
    {
        if (Session is not { } session || Group.Queue is not { } queue || Player is not null) return;
        try { await PlayAsync(await session.Client.ItemAsync(queue.ItemId, session.Lifetime)); }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
    }
    /// <summary>« Quitter la party »: out of the group, the guest's session ended and erased, then this Mac's own account again.</summary>
    public async Task LeavePartyAsync()
    {
        if (_partyLeaving) return;
        _partyAsked = null;
        await StopPlaybackAsync();
        await EndGuestSessionAsync();
        HideParty();
        if (Session is null) await BringOwnAccountBackAsync(keepParty: false);
    }
    /// <summary>This Mac's own account (kept while a guest), or the sign-in when there is none.</summary>
    private async Task BringOwnAccountBackAsync(bool keepParty)
    {
        // Off the interface thread, as at launch: macOS may ask before Mira reads its keychain.
        var own = _ownConnection ?? await Task.Run(Profile.LoadConnection);
        _ownConnection = null;
        if (own is not null) await ActivateAsync(own, _handler);
        else ShowLogin();
        if (keepParty) RevealParty();
    }
    /// <summary>The guest's session over (its group left, then signed out of the friend's Jellyfin), its cache erased.</summary>
    private async Task EndGuestSessionAsync()
    {
        if (_partyGuest is not { } guest) return;
        _partyLeaving = true;
        try
        {
            _partyWait?.Cancel(); _partyWait = null;
            if (Session is { } session && session.Connection == guest.Connection)
            {
                if (Group.Active)
                {
                    try { await session.Client.SyncLeaveAsync(session.Lifetime); }
                    catch (Exception ex) when (Errors.Expected(ex)) { }
                }
                Group.Gone(); _listening = false;
                Session = null;
                await session.SignOutAsync();
            }
            _partyGuest = null;
            await AccountData.EraseAsync(Profile.DirectoryPath, guest.Connection);
        }
        finally { _partyLeaving = false; }
    }
}
