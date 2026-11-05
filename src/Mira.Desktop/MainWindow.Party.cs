using System.Net;
using System.Windows;
using System.Windows.Input;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Ta party » of another Jellyfin (a mira://party/… link whose Jellyfin is not the one this PC is signed in to): joined
/// as its guest, with no account there (<see cref="JellyfinClient.OpenPartyAsync"/>). Mira is then that party's, as it
/// would be another account's: <see cref="_client"/> is the guest's session, which the extension's gateway lets reach
/// the host's group and its title only; PartyOverlay covers the library, the player opens above it when the group plays.
/// « Quitter la party » ends the guest's session and brings this PC's own account back (or the sign-in). The first name
/// is kept for the next party (<see cref="PlayerSettings.PartyName"/>).
/// </summary>
public partial class MainWindow
{
    private enum PartyStage { Name, Opening, Waiting, In, Ended }
    /// <summary>The party asked for (its Jellyfin, its code, who invites once its page said), until its guest opens.</summary>
    private (string Server, string Code, string? Host)? _partyAsked;
    /// <summary>The guest Mira is now, of a friend's party: <see cref="_client"/> is its session.</summary>
    private PartyGuest? _partyGuest;
    private PartyStage _partyStage;
    private CancellationTokenSource? _partyWait;
    private bool _partyBusy, _partyLeaving;
    private bool InParty => _partyGuest is not null;
    /// <summary>
    /// This PC's own account, for what runs on its own (backups, the server PC's downloads, the graphics card, keeping
    /// awake): none while Mira is a party's guest, whose Jellyfin answers its party only.
    /// </summary>
    private JellyfinClient? OwnClient => _partyGuest is null ? _client : null;

    /// <summary>The first name asked for a friend's party; who invites named once its page says (it answers without an account).</summary>
    private async Task AskPartyAsync(string server, string code)
    {
        if (_partyGuest is { } current && Together.GuestOrigin(current.Connection) == server.TrimEnd('/') && _partyAsked?.Code == code) { RevealParty(); return; }
        _partyAsked = (server, code, null);
        PartyNameBox.Text = _settings.PartyName; PartyError.Text = "";
        ShowParty(PartyStage.Name);
        RevealParty();
        PartyNameBox.Focus(); PartyNameBox.SelectAll();
        string? host;
        try
        {
            using var page = new JellyfinClient(new(server, "", "", "", Together.GuestDevice(_profile.DeviceId)));
            host = await page.PartyHostAsync(code);
        }
        // Not reached now: asked anyway, joining says why.
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (_partyAsked is not { } asked || asked.Code != code || _partyStage != PartyStage.Name) return;
        if (host is null) { _partyAsked = null; ShowParty(PartyStage.Ended); return; }
        _partyAsked = asked with { Host = host };
        ShowParty(PartyStage.Name);
    }
    private void RevealParty()
    {
        RestoreFromWindows();
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        if (PartyOverlay.Visibility != Visibility.Visible) Motion.Reveal(PartyOverlay);
    }
    private void ShowParty(PartyStage stage)
    {
        _partyStage = stage;
        var host = _partyGuest?.Host is { Length: > 0 } guestHost ? guestHost : _partyAsked?.Host;
        var named = host is { Length: > 0 } ? host : L("la personne qui t’invite", "whoever invited you");
        PartyNameForm.Visibility = stage is PartyStage.Name or PartyStage.Opening ? Visibility.Visible : Visibility.Collapsed;
        PartyNameBox.IsEnabled = stage == PartyStage.Name;
        PartyMembers.Visibility = Visibility.Collapsed;
        if (stage != PartyStage.Name) PartyError.Text = "";
        string? primary = null, secondary;
        switch (stage)
        {
            case PartyStage.Name or PartyStage.Opening:
                PartyHeading.Text = Together.PartyAsk(host); PartyLead.Text = Together.PartyAskLead;
                primary = stage == PartyStage.Opening ? L("Tu entres dans la soirée…", "Joining the party…") : L("Rejoindre la party", "Join the party");
                secondary = L("Annuler", "Cancel");
                break;
            case PartyStage.Waiting:
                PartyHeading.Text = Together.WaitingText(named); PartyLead.Text = Together.WaitingLead;
                if (_partyWait is null) primary = L("Chercher à nouveau", "Look again");
                secondary = L("Quitter la party", "Leave the party");
                break;
            case PartyStage.In:
                PartyHeading.Text = Together.PartyIn(named); PartyLead.Text = Together.PartyInLead(named);
                if (_group is { } group)
                {
                    PartyGroupText.Text = Together.GroupText(group); PartyMembersText.Text = Together.Members(group);
                    PartyMembers.Visibility = Visibility.Visible;
                }
                // The group's title plays on while this PC closed its player: back into it where the group is.
                if (_groupQueue is not null && !_playing) primary = L("Revenir au titre du groupe", "Back to the group’s title");
                secondary = L("Quitter la party", "Leave the party");
                break;
            default:
                PartyHeading.Text = Together.PartyOver; PartyLead.Text = Together.PartyEnded;
                secondary = L("Fermer", "Close");
                break;
        }
        PartyPrimary.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        PartyPrimary.IsEnabled = stage != PartyStage.Opening;
        PartyPrimaryText.Text = primary ?? "";
        PartySecondary.Content = secondary;
    }
    /// <summary>The overlay as the group is now (joined, its members, its title), while Mira is a party's guest.</summary>
    private void ShowPartyState()
    {
        if (!InParty || _partyLeaving || _partyStage == PartyStage.Ended) return;
        ShowParty(_group is null ? PartyStage.Waiting : PartyStage.In);
    }
    private void PartyName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; _ = OpenPartyGuestAsync();
    }
    private async void PartyPrimary_Click(object sender, RoutedEventArgs e)
    {
        switch (_partyStage)
        {
            case PartyStage.Name: await OpenPartyGuestAsync(); break;
            case PartyStage.Waiting when _client is { } client: await JoinPartyGroupAsync(client); break;
            case PartyStage.In: await BackToGroupTitleAsync(); break;
        }
    }
    private async void PartySecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_partyGuest is null) { _partyAsked = null; await Motion.HideAsync(PartyOverlay); if (LoginOverlay.Visibility == Visibility.Visible) FocusLogin(); return; }
        await LeavePartyAsync();
    }

    /// <summary>« Rejoindre la party »: a guest of its own for this PC, under the first name typed; then Mira is theirs.</summary>
    private async Task OpenPartyGuestAsync()
    {
        if (_partyAsked is not { } asked || _partyBusy || _partyStage != PartyStage.Name) return;
        var name = Together.PartyName(PartyNameBox.Text);
        if (name.Length == 0) { PartyError.Text = L("Écris ton prénom : c’est lui que le groupe voit.", "Type your first name: it’s what the group sees."); PartyNameBox.Focus(); return; }
        _partyBusy = true; PartyError.Text = "";
        ShowParty(PartyStage.Opening);
        PartyGuest guest;
        try
        {
            using var opener = new JellyfinClient(new(asked.Server, "", "", "", Together.GuestDevice(_profile.DeviceId)));
            guest = await opener.OpenPartyAsync(asked.Code, name);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _partyBusy = false;
            if (_partyAsked?.Code != asked.Code) return;
            // Taken back or made anew by its host: nothing more to try with this link.
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound } && ex is RelayRefusedException) { _partyAsked = null; ShowParty(PartyStage.Ended); return; }
            ShowParty(PartyStage.Name);
            PartyError.Text = ex is RelayRefusedException ? ex.Message : Friendly(ex);
            return;
        }
        _partyBusy = false;
        if (_partyAsked?.Code != asked.Code) return;
        if (_settings.PartyName != name) { _settings.PartyName = name; TrySaveSettings(); }
        await BecomeGuestAsync(guest);
    }
    /// <summary>
    /// Mira as the party's guest: this PC's account let go as for another account (its session stays on its Jellyfin,
    /// and comes back on leaving), the guest's WebSocket opened, then the host's group joined, or waited for.
    /// </summary>
    private async Task BecomeGuestAsync(PartyGuest guest)
    {
        await EndGuestSessionAsync();
        await DisconnectServicesAsync();
        _demo = false; DemoBadge.Visibility = Visibility.Collapsed;
        _partyGuest = guest;
        var client = _client = new JellyfinClient(guest.Connection);
        _speed.Forget();
        _connectionLifetime = new(); _listening = false; _lastRefresh = DateTimeOffset.MinValue; _offlineSince = null;
        // Nothing of the library under the party: a page left open would ask the guest's Jellyfin for what it never gives.
        LoginOverlay.Visibility = DetailOverlay.Visibility = GuideOverlay.Visibility = TorLinkOverlay.Visibility = Visibility.Collapsed;
        if (SettingsOverlay.Visibility == Visibility.Visible) { AutoSaveSettings(); SettingsOverlay.Visibility = Visibility.Collapsed; }
        ServerLabel.Text = guest.Connection.UserName + " · " + L("invité", "guest"); AvatarLetter.Text = guest.Connection.UserName[..1].ToUpperInvariant(); SetPhoto(null);
        _ = ListenSafelyAsync(client, _connectionLifetime.Token);
        ShowParty(PartyStage.Waiting);
        await JoinPartyGroupAsync(client);
    }
    /// <summary>The host's group joined as soon as it is open (read every few seconds, three hours at most).</summary>
    private async Task JoinPartyGroupAsync(JellyfinClient client)
    {
        if (_partyGuest is not { } guest || !ReferenceEquals(client, _client) || _connectionLifetime.IsCancellationRequested || _partyLeaving) return;
        if (_group is not null) { ShowPartyState(); return; }
        _partyWait?.Cancel();
        var wait = _partyWait = CancellationTokenSource.CreateLinkedTokenSource(_connectionLifetime.Token);
        wait.CancelAfter(TimeSpan.FromHours(3));
        ShowParty(PartyStage.Waiting);
        // Jellyfin says GroupJoined through the WebSocket: open first, or the answer would go unheard.
        for (var i = 0; !_listening && i < 40 && ReferenceEquals(client, _client); i++) await Task.Delay(250);
        Together.Group group;
        try { group = await Together.WaitForGroupAsync(ReadAsync, Together.PartyInvitation(client.Connection.Server, guest.Host), TimeSpan.FromSeconds(4), wait.Token); }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_partyWait, wait)) { _partyWait = null; ShowPartyState(); }
            return;
        }
        if (!ReferenceEquals(_partyWait, wait) || !ReferenceEquals(client, _client)) return;
        _partyWait = null;
        if (_group is null) await EnterGroupAsync(c => c.SyncJoinAsync(group.GroupId), invite: false);
        ShowPartyState();
        async Task<IReadOnlyList<Together.Group>?> ReadAsync(CancellationToken ct)
        {
            try { return await client.SyncGroupsAsync(ct); }
            // The host took the link back (the guest's account is gone with it), or made a new one.
            catch (UnauthorizedAccessException) { _ = PartyEndedAsync(client); return null; }
            catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { return null; }
        }
    }
    /// <summary>The guest's WebSocket closed: still a guest when the group can still be read, else the party is over for this link.</summary>
    private async Task CheckPartyAsync(JellyfinClient client)
    {
        if (!ReferenceEquals(client, _client) || !InParty) return;
        try { await client.SyncGroupsAsync(_connectionLifetime.Token); }
        catch (UnauthorizedAccessException) { await PartyEndedAsync(client); }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
    private async Task PartyEndedAsync(JellyfinClient client)
    {
        if (!ReferenceEquals(client, _client) || !InParty || _partyStage == PartyStage.Ended) return;
        _partyWait?.Cancel(); _partyWait = null;
        ShowParty(PartyStage.Ended);
        if (_playing) { await StopPlaybackAsync(); ReturnToLibrary(); }
        RevealParty();
    }
    /// <summary>« Revenir au titre du groupe »: the player opened again on the group's title, where the group is.</summary>
    private async Task BackToGroupTitleAsync()
    {
        if (_client is not { } client || _groupQueue is not { } queue || _playing) return;
        PartyPrimary.IsEnabled = false;
        try { await PlayAsync(await client.ItemAsync(queue.ItemId, _connectionLifetime.Token)); }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
        finally { PartyPrimary.IsEnabled = true; }
    }
    /// <summary>« Quitter la party »: out of the group, the guest's session ended, then this PC's own account again.</summary>
    private async Task LeavePartyAsync()
    {
        if (_partyLeaving) return;
        _partyAsked = null;
        await EndGuestSessionAsync();
        await Motion.HideAsync(PartyOverlay);
        if (_client is not null) return;
        if (_profile.LoadConnection() is { } own) { await ActivateConnectionAsync(own); return; }
        ServerLabel.Text = ""; AvatarLetter.Text = "M";
        ShowSignInForm(); SignInHeading(); Motion.Reveal(LoginOverlay); FocusLogin(); FindServer();
    }
    /// <summary>The guest's session over (its group left, then signed out of the friend's Jellyfin) and let go.</summary>
    private async Task EndGuestSessionAsync()
    {
        if (_partyGuest is null) return;
        _partyLeaving = true;
        try
        {
            _partyWait?.Cancel(); _partyWait = null;
            if (_client is { } client)
            {
                if (_group is not null)
                {
                    try { await client.SyncLeaveAsync(_connectionLifetime.Token); }
                    catch (Exception ex) when (IsExpected(ex)) { }
                }
                await client.LogoutAsync();
            }
            await DisconnectServicesAsync();
            _partyGuest = null;
        }
        finally { _partyLeaving = false; }
    }
}
