using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Regarder ensemble »: Jellyfin's SyncPlay, as in Mira web (together.js). A group plays one title at the same moment
/// on each of its devices: Lecture puts a title on for the group, and pause, play, seeks and the next episode go to
/// Jellyfin, which tells each device what to do and when, on its clock (<see cref="Together"/>). One group at a time,
/// joined or left from the title page, left from the player's ⋮ too. Its messages come through Jellyfin's WebSocket.
/// </summary>
public partial class MainWindow
{
    private const string NoSyncPlay = "Jellyfin ne permet pas à ce compte de regarder ensemble (tableau de bord → Utilisateurs → SyncPlay).";
    private Together.Group? _group;
    /// <summary>The group's title, as Jellyfin's last PlayQueue put it on.</summary>
    private Together.Entry? _groupQueue;
    /// <summary>Jellyfin's clock minus this PC's, in milliseconds.</summary>
    private long _groupOffset;
    private TaskCompletionSource? _groupJoined;
    private readonly DispatcherTimer _groupClock = new() { Interval = TimeSpan.FromMinutes(1) };
    // Arrow keys pressed in a row are one seek for the group.
    private readonly DispatcherTimer _groupSeekSoon = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _groupPlaySoon = new();
    private double _groupPlayAt;
    /// <summary>Loaded or moved for the group: Jellyfin hears « ready » once the picture is there.</summary>
    private bool _groupReadyPending;
    /// <summary>Until then, a seek that ends is one the group asked for, not one to send it.</summary>
    private DateTimeOffset _groupQuietUntil;
    private DateTimeOffset? _groupStallSince;
    private bool _groupStalled;
    /// <summary>The player closed here: Jellyfin is told not to wait for this PC, or every seek would hold the others.</summary>
    private bool _groupAway;
    /// <summary>The title asked of the group when the episode before it counts as watched (credits skipped).</summary>
    private string? _groupCompletes;
    // In order: a Ready sent after its Buffering must arrive after it.
    private Task _groupSends = Task.CompletedTask;

    private bool InGroup => _group is not null && !_demo && _client is not null;
    private bool PlayingForGroup => InGroup && _playing && _groupQueue is { } queue && _session.Item?.Id == queue.ItemId;

    private void InitializeTogether()
    {
        _groupClock.Tick += async (_, _) => await SyncGroupClockAsync();
        _groupSeekSoon.Tick += (_, _) => { _groupSeekSoon.Stop(); if (PlayingForGroup) SendGroup("Seek", new JsonObject { ["PositionTicks"] = Ticks(_session.Position) }); };
        _groupPlaySoon.Tick += (_, _) => { _groupPlaySoon.Stop(); if (PlayingForGroup) { MoveForGroup(_groupPlayAt); QuietPause(false); } };
    }

    /// <summary>SyncPlayGroupUpdate and SyncPlayCommand of Jellyfin's WebSocket, on the UI thread.</summary>
    private void GroupMessage(JellyfinClient client, string type, JsonElement data)
    {
        if (!ReferenceEquals(client, _client) || _demo) return;
        if (type == "SyncPlayCommand") { GroupCommand(data); return; }
        var body = data.TryGetProperty("Data", out var d) ? d : default;
        var kind = data.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (kind)
        {
            case "GroupJoined":
                if (Together.ParseGroup(body) is not { } group) return;
                _group = group; _groupJoined?.TrySetResult(); _groupJoined = null;
                _groupClock.Start(); _ = SyncGroupClockAsync(); GroupChanged();
                break;
            case "UserJoined" or "UserLeft" when _group is not null:
                if (body.ValueKind == JsonValueKind.String) SetNotice($"{body.GetString()} {(kind == "UserJoined" ? "a rejoint" : "a quitté")} le groupe.");
                _ = RefreshGroupAsync(client);
                break;
            case "GroupLeft": GroupGone(); break;
            case "NotInGroup" or "GroupDoesNotExist": GroupGone(_group is null ? null : "Le groupe n’existe plus."); break;
            case "LibraryAccessDenied": SetNotice("Un membre du groupe n’a pas accès à ce titre dans Jellyfin."); break;
            case "PlayQueue": _ = GroupQueueAsync(client, body); break;
        }
    }
    /// <summary>Jellyfin names each account once, whatever its devices: its own list, rather than a count kept here.</summary>
    private async Task RefreshGroupAsync(JellyfinClient client)
    {
        if (_group is not { } group) return;
        try
        {
            if (await client.SyncGroupAsync(group.GroupId, _connectionLifetime.Token) is { } info && _group?.GroupId == info.GroupId)
            { _group = _group with { Participants = info.Participants }; GroupChanged(); }
        }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
    /// <summary>Out of the group here (left, ended, signed out): the player plays on, alone.</summary>
    private void GroupGone(string? notice = null)
    {
        var was = _group is not null;
        _group = null; _groupQueue = null; _groupOffset = 0; _groupAway = false; _groupReadyPending = false; _groupStallSince = null; _groupStalled = false; _groupCompletes = null;
        _groupClock.Stop(); _groupSeekSoon.Stop(); _groupPlaySoon.Stop();
        _groupJoined?.TrySetResult(); _groupJoined = null;
        if (was) GroupChanged();
        if (notice is not null) SetNotice(notice);
    }
    private void GroupChanged()
    {
        ShowTogetherButton();
        if (PlayerOptionsPopup.IsOpen && MoreMenu.Visibility == Visibility.Visible) ShowGroupSection();
    }
    /// <summary>Opened again after a break: Jellyfin may have let the session go, with its place in the group.</summary>
    private void RejoinGroup(JellyfinClient client)
    {
        if (_group is not { } group || !ReferenceEquals(client, _client)) return;
        _ = RejoinAsync();
        async Task RejoinAsync() { try { await client.SyncJoinAsync(group.GroupId, _connectionLifetime.Token); } catch (Exception ex) when (IsExpected(ex)) { } }
    }

    private async Task SyncGroupClockAsync()
    {
        if (_client is not { } client || _group is null) return;
        var samples = new List<Together.ClockSample>();
        for (var i = 0; i < 3; i++)
        {
            try { if (await client.UtcTimeAsync(_connectionLifetime.Token) is { } s) samples.Add(new(s.Sent, s.Received, s.ServerIn, s.ServerOut)); }
            catch (Exception ex) when (IsExpected(ex)) { break; }
        }
        if (Together.ClockOffset(samples) is not { } best || _group is null || !ReferenceEquals(client, _client)) return;
        _groupOffset = best.Offset;
        // Jellyfin waits for the slowest device of the group before it says when to play.
        SendGroup("Ping", new JsonObject { ["Ping"] = Math.Max(1, best.Trip) });
    }
    private void SendGroup(string what, JsonObject? body = null)
    {
        if (_group is null || _client is not { } client) return;
        var ct = _connectionLifetime.Token;
        _groupSends = SendAfterAsync(_groupSends);
        async Task SendAfterAsync(Task previous)
        {
            await previous;
            try { await client.SyncSendAsync(what, body, ct); }
            catch (Exception ex) when (IsExpected(ex)) { }
        }
    }
    /// <summary>This PC's state for the group's title: Buffering holds the others, Ready lets them go.</summary>
    private void GroupReport(string what, double seconds, bool playing)
    {
        if (_groupQueue is not { } queue) return;
        var when = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _groupOffset).UtcDateTime;
        SendGroup(what, new JsonObject { ["When"] = when.ToString("o", CultureInfo.InvariantCulture), ["PositionTicks"] = Ticks(seconds), ["IsPlaying"] = playing, ["PlaylistItemId"] = queue.PlaylistItemId });
    }
    private void SetGroupAway(bool away)
    {
        if (_group is null || _groupAway == away) return;
        _groupAway = away;
        SendGroup("SetIgnoreWait", new JsonObject { ["IgnoreWait"] = away });
    }
    private static long Ticks(double seconds) => (long)Math.Round(Math.Max(0, seconds) * 1e7);

    /// <summary>The group's title: put on here too, paused where it starts until Jellyfin says when to play.</summary>
    private async Task GroupQueueAsync(JellyfinClient client, JsonElement data)
    {
        if (Together.QueueItem(data) is not { } next) { _groupQueue = null; return; }
        // Shuffle, repeat or the list changed around the same title: nothing to load again.
        if (_groupQueue?.PlaylistItemId == next.PlaylistItemId) return;
        _groupQueue = next; SetGroupAway(false);
        var start = next.StartTicks / 1e7;
        GroupReport("Buffering", start, false);
        if (_playing && _loaded && _session.Item?.Id == next.ItemId)
        {
            // The same title put on again by another device: it waits there with the others.
            QuietPause(true);
            if (!MoveForGroup(start, ready: true)) GroupReport("Ready", start, false);
            return;
        }
        try
        {
            var item = await client.ItemAsync(next.ItemId, _connectionLifetime.Token);
            for (var i = 0; _playBusy && i < 50; i++) await Task.Delay(100);
            // Another title asked meanwhile, or another account.
            if (!ReferenceEquals(client, _client) || _groupQueue?.PlaylistItemId != next.PlaylistItemId) return;
            // The episode that ended here already opened it, for the group (AskGroupAsync).
            if (_playing && _session.Item?.Id == item.Id) return;
            var completes = _groupCompletes == item.Id; _groupCompletes = null;
            await PlayAsync(item, keepMini: _playing && _miniPlayer, completePrevious: completes, groupStart: next.StartTicks);
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
    }
    /// <summary>
    /// Lecture in a group: Jellyfin puts the title on for every device, this one included (GroupQueueAsync). True when
    /// the group took it; false when it plays here, at the group's title the player had left.
    /// </summary>
    private async Task<bool> AskGroupAsync(MediaItem item, bool fromStart, bool completePrevious)
    {
        if (_groupQueue?.ItemId == item.Id)
        {
            // Every device reaches the end of an episode: the next one is put on once. The player closed on the
            // group's title opens again where the group is (Jellyfin moves it there once it is ready).
            if (_session.Item?.Id == item.Id && _playing) return true;
            return false;
        }
        var start = PlaybackSession.StartAt(item, remember: _settings.RememberPosition && !fromStart);
        if (completePrevious) _groupCompletes = item.Id;
        try { await _client!.SyncQueueAsync(item.Id, Ticks(start), _connectionLifetime.Token); }
        catch (UnauthorizedAccessException) { SetNotice(NoSyncPlay); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        // An episode that ended waits in the player for the next one, unless Jellyfin never sends it.
        if (PlayerShell.Visibility == Visibility.Visible && !_playing) { SetPlayerLoading(true); _ = LeaveEmptyPlayerAsync(); }
        return true;
    }
    private async Task LeaveEmptyPlayerAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (!_playing && !_playBusy && PlayerShell.Visibility == Visibility.Visible && !PlayerErrorShown) { SetPlayerLoading(false); ReturnToLibrary(); }
    }

    /// <summary>A command of the group, for the title playing here only: the Stop sent on joining names none.</summary>
    private void GroupCommand(JsonElement data)
    {
        if (!PlayingForGroup || _groupQueue is not { } queue || _mpv is null) return;
        if (!data.TryGetProperty("PlaylistItemId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != queue.PlaylistItemId) return;
        if (Together.CommandPlan(data, _groupOffset, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is not { } plan) return;
        _groupPlaySoon.Stop();
        switch (plan.Action)
        {
            case "play":
                _groupPlayAt = plan.Position;
                if (plan.Wait > 0) { _groupPlaySoon.Interval = TimeSpan.FromMilliseconds(plan.Wait); _groupPlaySoon.Start(); }
                else { MoveForGroup(plan.Position); QuietPause(false); }
                break;
            case "pause": QuietPause(true); MoveForGroup(plan.Position); break;
            case "seek":
                QuietPause(true);
                if (!MoveForGroup(plan.Position, ready: true)) GroupReport("Ready", plan.Position, false);
                break;
            case "stop": _ = StopForGroupAsync(); break;
        }
    }
    private async Task StopForGroupAsync()
    {
        if (!_playing) return;
        await StopPlaybackAsync(); ReturnToLibrary();
        if (!_demo) await RefreshAsync(quiet: true);
    }
    /// <summary>Paused or played for the group: not a press of this PC's to send it back.</summary>
    private void QuietPause(bool pause)
    {
        if (_mpv is null) return;
        _mpv.Set("pause", pause ? "yes" : "no"); _session.Paused = pause; ShowPauseState(pause);
        SeekBar.SetPlayback(_session.Position, _session.Duration, !pause, _mpv.Number("speed", 1)); UpdateWindowsPlayback();
    }
    /// <summary>To where the group is, when it is far enough to be seen; true when it moves.</summary>
    private bool MoveForGroup(double seconds, bool ready = false)
    {
        if (_mpv is null || !_loaded || !Together.NeedsSeek(_mpv.Number("time-pos", _session.Position), seconds)) return false;
        _groupQuietUntil = DateTimeOffset.UtcNow.AddSeconds(2);
        if (!TryCommand("seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return false;
        _groupReadyPending |= ready; HoldPosition(seconds);
        return true;
    }

    /// <summary>mpv played again after a load or a seek: ready for the group, or a seek of this PC's that the group follows.</summary>
    private void GroupRestarted()
    {
        if (!PlayingForGroup || _mpv is null) return;
        if (_groupReadyPending) { _groupReadyPending = false; GroupReport("Ready", _mpv.Number("time-pos", _session.Position), !_mpv.Flag("pause")); return; }
        // The bar dragged, the keys, « Passer l’intro », a chapter: sent once the pointer is released.
        if (_seeking || DateTimeOffset.UtcNow < _groupQuietUntil) return;
        _groupSeekSoon.Stop(); _groupSeekSoon.Start();
    }
    /// <summary>Paused or played here (button, Espace, media keys, the timer): the group does it, at the same moment.</summary>
    private void GroupPauseChanged(bool paused)
    {
        if (!PlayingForGroup || !_loaded) return;
        if (paused) { SendGroup("Pause"); return; }
        QuietPause(true); SendGroup("Unpause");
    }
    /// <summary>Waiting for data more than a moment: the others wait too, then all go on together.</summary>
    private void WatchGroupStall()
    {
        if (!PlayingForGroup || !_loaded || _mpv is null) return;
        if (_mpv.Flag("paused-for-cache"))
        {
            _groupStallSince ??= DateTimeOffset.UtcNow;
            if (!_groupStalled && DateTimeOffset.UtcNow - _groupStallSince > TimeSpan.FromSeconds(1.5)) { _groupStalled = true; GroupReport("Buffering", _session.Position, true); }
            return;
        }
        _groupStallSince = null;
        if (_groupStalled) { _groupStalled = false; GroupReport("Ready", _session.Position, !_mpv.Flag("pause")); }
    }

    /// <summary>The title page's button: « Regarder ensemble », or the group this PC is in.</summary>
    private void ShowTogetherButton()
    {
        TogetherButton.Visibility = _demo ? Visibility.Collapsed : Visibility.Visible;
        TogetherButton.Content = ActionLabel("users", _group is { } group ? "Ensemble · " + Together.People(Math.Max(1, group.Participants.Count)) : "Regarder ensemble");
    }
    private async void Together_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _demo) return;
        var menu = new ContextMenu { PlacementTarget = TogetherButton, Placement = PlacementMode.Bottom };
        if (_group is { } group)
        {
            menu.Items.Add(new MenuItem { Header = Together.GroupText(group) + (group.Participants.Count > 0 ? " : " + string.Join(", ", group.Participants) : ""), Tag = "users", IsEnabled = false });
            menu.Items.Add(MenuAction("Inviter quelqu’un…", "link", () => { ShowInvite(); return Task.CompletedTask; }));
            menu.Items.Add(MenuAction("Quitter le groupe", "close", LeaveGroupAsync));
        }
        else
        {
            List<Together.Group> groups;
            try { groups = await client.SyncGroupsAsync(_connectionLifetime.Token); }
            catch (UnauthorizedAccessException) { SetNotice(NoSyncPlay); return; }
            catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); return; }
            if (!ReferenceEquals(client, _client) || DetailOverlay.Visibility != Visibility.Visible) return;
            foreach (var g in groups)
                menu.Items.Add(MenuAction($"Rejoindre « {g.GroupName} » · {Together.People(Math.Max(1, g.Participants.Count))}", "users", () => EnterGroupAsync(c => c.SyncJoinAsync(g.GroupId), invite: false)));
            if (groups.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction("Créer un groupe et inviter…", "plus", () => EnterGroupAsync(c => c.SyncNewAsync(Together.GroupName(c.Connection.UserName)), invite: true)));
        }
        menu.IsOpen = true;
    }
    /// <summary>« Créer un groupe » (then its invitation: a new group is empty) or « Rejoindre … »: done once Jellyfin says GroupJoined.</summary>
    private async Task EnterGroupAsync(Func<JellyfinClient, Task> start, bool invite)
    {
        if (_client is not { } client) return;
        var joined = _groupJoined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await start(client);
            await Task.WhenAny(joined.Task, Task.Delay(TimeSpan.FromSeconds(6)));
            if (_group is null && ReferenceEquals(client, _client)) SetNotice("Jellyfin n’a pas répondu. Réessaie dans un instant.");
            else if (_group is { } group && invite) ShowInvite();
            else if (_group is { } joinedGroup) SetNotice($"Tu es dans « {joinedGroup.GroupName} » : Regarder lance le titre pour tout le groupe, au même moment chez chacun.");
        }
        catch (UnauthorizedAccessException) { SetNotice(NoSyncPlay); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        finally { if (ReferenceEquals(_groupJoined, joined)) _groupJoined = null; }
    }
    private async Task LeaveGroupAsync()
    {
        if (_client is { } client)
        {
            try { await client.SyncLeaveAsync(_connectionLifetime.Token); }
            catch (Exception ex) when (IsExpected(ex)) { }
        }
        GroupGone();
    }
    /// <summary>
    /// « Inviter quelqu'un »: the link that opens Mira web on the group (#/ensemble/…), with what the friend needs. A
    /// Jellyfin on this PC is given at its Tailscale address when there is one: a friend reaches it at home and away.
    /// </summary>
    private void ShowInvite()
    {
        if (_group is not { } group || _client is not { } client) return;
        var server = client.Connection.Server;
        var here = Uri.TryCreate(server, UriKind.Absolute, out var uri) && LocalNetwork.IsThisPc(uri.Host);
        var link = Together.InviteLink(Together.InviteServer(server, here, here ? LocalNetwork.ThisPcOnTailnet() : null, here ? LocalNetwork.ThisPc() : null), group.GroupId);
        InviteLead.Text = $"Envoie ce lien à ton ami pour qu’il rejoigne « {group.GroupName} ».";
        InviteLink.Text = link;
        InviteSteps.Children.Clear();
        var steps = Together.InviteSteps(link);
        for (var i = 0; i < steps.Count; i++)
            InviteSteps.Children.Add(new TextBlock { Text = $"{i + 1}. {steps[i]}", FontSize = 13, LineHeight = 19, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        InviteCopy.Content = ActionLabel("link", "Copier le lien");
        Motion.Reveal(InviteOverlay, 260, 0); Motion.Reveal(InviteCard, 360, 16);
        _ = Dispatcher.BeginInvoke(() => InviteCopy.Focus(), DispatcherPriority.Input);
    }
    private void InviteCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(InviteLink.Text); SetNotice("Lien copié : envoie-le à ton ami."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetNotice("Le presse-papiers est occupé : réessaie."); }
    }
    private async void InviteClose_Click(object sender, RoutedEventArgs e) => await CloseInviteAsync();
    private async Task CloseInviteAsync() { await Motion.HideAsync(InviteOverlay); if (DetailOverlay.Visibility == Visibility.Visible) TogetherButton.Focus(); }
    /// <summary>The player's ⋮: the group and « Quitter », while in one.</summary>
    private void ShowGroupSection()
    {
        TogetherSection.Visibility = _group is null ? Visibility.Collapsed : Visibility.Visible;
        if (_group is not { } group) return;
        TogetherStatus.Text = Together.GroupText(group);
        TogetherStatus.ToolTip = group.Participants.Count > 0 ? string.Join(", ", group.Participants) : null;
    }
    private async void TogetherLeave_Click(object sender, RoutedEventArgs e) { await LeaveGroupAsync(); ShowGroupSection(); }
}
