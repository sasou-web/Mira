using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Regarder ensemble »: Jellyfin's SyncPlay, as in Mira web (together.js). A group plays one title at the same moment
/// on each of its devices: Lecture puts a title on for the group, and pause, play, seeks and the next episode go to
/// Jellyfin, which tells each device what to do and when, on its clock (<see cref="Together"/>). One group at a time,
/// joined or left from the title page, joined from Home too (<see cref="ShowHomeGroup"/>), left from the player's ⋮.
/// Its messages come through Jellyfin's WebSocket.
/// Jellyfin lets a device go with its socket (Wi-Fi lost, the PC asleep): back, this PC joins the group again, or makes
/// it again under the same name when it made it and the group ended empty, so that the invitation sent still finds it.
/// In the player, a pill says what the group is doing (<see cref="UpdateGroupPill"/>).
/// </summary>
public partial class MainWindow
{
    private static string NoSyncPlay => L("Jellyfin ne permet pas à ce compte de regarder ensemble (tableau de bord → Utilisateurs → SyncPlay).", "Jellyfin doesn’t let this account watch together (Dashboard → Users → SyncPlay).");
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
    /// <summary>The group was made on this PC: made again if Jellyfin ends it (<see cref="Together.RejoinPlan"/>).</summary>
    private bool _groupMade;
    /// <summary>Back in the group: Jellyfin sends its title again, and holds the others until this PC is ready.</summary>
    private bool _groupRejoined;
    /// <summary>Going back to the group after Jellyfin let this PC go, once at a time.</summary>
    private Task? _groupRecovering;
    /// <summary>An invitation waited for (« Attendre »): its group joined once its host opens it.</summary>
    private CancellationTokenSource? _inviteWait;
    /// <summary>« Marc a rejoint le groupe », said by the player's pill until <see cref="_groupMemberUntil"/>.</summary>
    private string? _groupMember;
    private DateTimeOffset _groupMemberUntil;
    private bool _groupPillShown;

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
                // The same group again (back after a break): its title stays until Jellyfin sends it again. Jellyfin
                // takes this PC as a newcomer, which it does not tell to wait or not: said again with the title.
                var again = _group is { } was && Together.SameGroup(was.GroupId, group.GroupId);
                if (!again) _groupQueue = null;
                _groupRejoined = again && _groupQueue is not null; _groupAway = false;
                _group = group; _groupJoined?.TrySetResult(); _groupJoined = null;
                _groupClock.Start(); _ = SyncGroupClockAsync(); GroupChanged();
                break;
            case "UserJoined" or "UserLeft" when _group is not null:
                if (body.ValueKind == JsonValueKind.String && body.GetString() is { Length: > 0 } name) GroupMember(name, kind == "UserJoined");
                _ = RefreshGroupAsync(client);
                break;
            // Idle, Waiting (for a device to load), Paused or Playing: never who caused it (Together.GroupStatus).
            case "StateUpdate" when _group is not null:
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.String)
                { _group = _group with { State = state.GetString()! }; GroupChanged(); }
                break;
            case "GroupLeft": GroupGone(); break;
            // Jellyfin let this PC go (its session ended): back in the group, or out of it when there is none to go back to.
            case "NotInGroup" or "GroupDoesNotExist":
                if (_group is null) GroupGone(); else RecoverGroup(client);
                break;
            case "LibraryAccessDenied": SetNotice(L("Un membre du groupe n’a pas accès à ce titre dans Jellyfin.", "Someone in the group can’t access this title in Jellyfin.")); break;
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
        _groupMade = false; _groupRejoined = false;
        _groupClock.Stop(); _groupSeekSoon.Stop(); _groupPlaySoon.Stop();
        _groupJoined?.TrySetResult(); _groupJoined = null;
        if (was) GroupChanged();
        if (notice is not null) SetNotice(notice);
    }
    private void GroupChanged()
    {
        ShowTogetherButton(); UpdateGroupPill(); ShowHomeGroup();
        // Out of a group: Home's line comes back with the groups as they are now (the one left may be over).
        if (_group?.GroupId != _homeGroupFor) { _homeGroupFor = _group?.GroupId; if (_group is null && _client is { } client) _ = LoadHomeGroupsAsync(client); }
        if (DetailOverlay.Visibility == Visibility.Visible && _detail is { } detail) ShowDetailPlayLabel(detail);
        if (PlayerOptionsPopup.IsOpen && MoreMenu.Visibility == Visibility.Visible) ShowGroupSection();
    }
    /// <summary>
    /// Someone came or went: said by the player's pill while a title plays (a message of the window would sit under the
    /// video), by a message otherwise. Mira minimized or behind another window, a Windows notification says who came;
    /// clicked, it brings Mira back where it was (the title page, ready for Lecture).
    /// </summary>
    private void GroupMember(string name, bool joined)
    {
        var said = Together.MemberText(name, joined);
        if (joined && _group is { } group && (WindowState == WindowState.Minimized || !IsActive))
            Toasts.Show(group.GroupName, said + ".", null, () => Dispatcher.BeginInvoke(RestoreFromWindows), "together", TimeSpan.FromHours(1));
        if (!_playing) { SetNotice(said + "."); return; }
        _groupMember = said; _groupMemberUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        UpdateGroupPill();
    }
    /// <summary>
    /// The player's pill, top center, as in Mira web (player/group-pill.js): « Ensemble · … » with the controls; a wait
    /// or a pause for the group, and someone who came or went, in sight whatever they do. Loading here (opening, a seek
    /// for the group, data awaited): Jellyfin's wait may be for this PC rather than for the others.
    /// </summary>
    private void UpdateGroupPill()
    {
        var status = InGroup && _playing && !_miniPlayer ? Together.GroupStatus(_group!, _client!.Connection.UserName, !_loaded || _groupReadyPending || _groupStallSince is not null) : null;
        var said = status is null ? null : DateTimeOffset.UtcNow < _groupMemberUntil ? _groupMember : null;
        if (status is not null && GroupPillText.Text != (said ?? status.Text)) GroupPillText.Text = said ?? status.Text;
        var show = status is not null && (said is not null || status.Alert || _controlsVisible);
        if (show == _groupPillShown) return;
        _groupPillShown = show;
        if (show) Motion.Reveal(GroupPill, 200, 6); else _ = Motion.HideAsync(GroupPill, 160);
    }
    /// <summary>Opened again after a break: Jellyfin may have let the session go, with its place in the group.</summary>
    private void RejoinGroup(JellyfinClient client) => RecoverGroup(client);
    /// <summary>
    /// Back in the group after Jellyfin let this PC go (its session ended with the WebSocket): joined again where it is,
    /// or made again under its name when this PC made it and it ended empty (<see cref="Together.RejoinPlan"/>). Out of
    /// it when there is none to go back to, or when Jellyfin twice says nothing; still in it while Jellyfin cannot be
    /// reached (the socket's next opening tries again).
    /// </summary>
    private void RecoverGroup(JellyfinClient client)
    {
        if (_group is not { } lost || !ReferenceEquals(client, _client) || _groupRecovering is { IsCompleted: false }) return;
        _groupRecovering = RecoverAsync();
        async Task RecoverAsync()
        {
            var ct = _connectionLifetime.Token;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var joined = _groupJoined = new(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    var plan = Together.RejoinPlan(await client.SyncGroupsAsync(ct), lost, _groupMade);
                    if (!ReferenceEquals(client, _client) || !ReferenceEquals(_group, lost)) return;
                    if (plan is null) { GroupGone(L("Le groupe n’existe plus.", "The group no longer exists.")); return; }
                    await (plan.Join is { } id ? client.SyncJoinAsync(id, ct) : client.SyncNewAsync(plan.Create!, ct));
                    await Task.WhenAny(joined.Task, Task.Delay(TimeSpan.FromSeconds(6), ct));
                }
                catch (Exception ex) when (IsExpected(ex)) { return; }
                finally { if (ReferenceEquals(_groupJoined, joined)) _groupJoined = null; }
                if (!ReferenceEquals(_group, lost)) return;
            }
            GroupGone(L("Le groupe n’existe plus.", "The group no longer exists."));
        }
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
        if (Together.QueueItem(data) is not { } next) { if (_groupQueue is not null) { _groupQueue = null; GroupChanged(); } return; }
        var same = _groupQueue?.PlaylistItemId == next.PlaylistItemId;
        // Shuffle, repeat or the list changed around the same title: nothing to load again. Unless this PC is back in the
        // group: Jellyfin holds the others until it is ready there, or told that its player is closed.
        if (same && !_groupRejoined) return;
        _groupRejoined = false;
        _groupQueue = next;
        // Lecture's label on the title page says whether it joins the group's title or replaces it.
        if (!same) GroupChanged();
        if (same && !(_playing && _session.Item?.Id == next.ItemId)) { SetGroupAway(true); return; }
        SetGroupAway(false);
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
    /// the group took it, or when it asks first (<paramref name="chosen"/> on a page while the group has another title
    /// on); false when it plays here, at the group's title the player had left.
    /// </summary>
    private async Task<bool> AskGroupAsync(MediaItem item, bool fromStart, bool completePrevious, bool chosen)
    {
        // The same title chosen again while « Remplacer » is offered for it: that is the answer.
        if (chosen && _replaceOffered is { } offered && offered.Id == item.Id && DateTimeOffset.UtcNow <= offered.Until) { _replaceOffered = null; HideNotice(); }
        else if (chosen && Together.AsksToReplace(_group, _groupQueue, item.Id)) { _ = OfferReplaceAsync(item, fromStart); return true; }
        if (_groupQueue?.ItemId == item.Id)
        {
            // Every device reaches the end of an episode: the next one is put on once. The player closed on the
            // group's title opens again where the group is (Jellyfin moves it there once it is ready).
            if (_session.Item?.Id == item.Id && _playing) return true;
            return false;
        }
        var start = PlaybackSession.StartAt(item, remember: _settings.RememberPosition && !fromStart, back: _settings.ResumeBack);
        if (completePrevious) _groupCompletes = item.Id;
        try { await _client!.SyncQueueAsync(item.Id, Ticks(start), _connectionLifetime.Token); }
        catch (UnauthorizedAccessException) { SetNotice(NoSyncPlay); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        // An episode that ended waits in the player for the next one, unless Jellyfin never sends it.
        if (PlayerShell.Visibility == Visibility.Visible && !_playing) { SetPlayerLoading(true); _ = LeaveEmptyPlayerAsync(); }
        return true;
    }
    /// <summary>
    /// « Remplacer « Dune » pour tout le groupe ? », with « Remplacer »: the title the group has on (named by one request)
    /// is replaced for everyone only once asked.
    /// </summary>
    private (string Id, DateTimeOffset Until)? _replaceOffered;
    private async Task OfferReplaceAsync(MediaItem item, bool fromStart)
    {
        if (_client is not { } client || _groupQueue is not { } queue) return;
        string? name = null;
        try { name = Together.TitleName(await client.ItemAsync(queue.ItemId, _connectionLifetime.Token)); }
        catch (Exception ex) when (IsExpected(ex)) { }
        if (!ReferenceEquals(client, _client) || !ReferenceEquals(queue, _groupQueue) || _group is null) return;
        _replaceOffered = (item.Id, DateTimeOffset.UtcNow.AddMinutes(2));
        SetNotice(Together.ReplaceQuestion(name) + L(" Le groupe le regarde en ce moment.", " The group is watching it right now."), L("Remplacer", "Replace"), () => _ = PlayAsync(item, fromStart: fromStart));
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
        TogetherButton.Content = ActionLabel("users", _group is { } group ? L("Ensemble · ", "Together · ") + Together.People(Math.Max(1, group.Participants.Count)) : L("Regarder ensemble", "Watch together"));
    }
    private async void Together_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _demo) return;
        var menu = new ContextMenu { PlacementTarget = TogetherButton, Placement = PlacementMode.Bottom };
        if (_group is { } group)
        {
            menu.Items.Add(new MenuItem { Header = Together.GroupText(group) + (group.Participants.Count > 0 ? L(" : ", ": ") + string.Join(", ", group.Participants) : ""), Tag = "users", IsEnabled = false });
            menu.Items.Add(MenuAction(L("Inviter quelqu’un…", "Invite someone…"), "link", () => { ShowInvite(); return Task.CompletedTask; }));
            menu.Items.Add(MenuAction(L("Quitter le groupe", "Leave the group"), "close", LeaveGroupAsync));
        }
        else
        {
            List<Together.Group> groups;
            try { groups = await client.SyncGroupsAsync(_connectionLifetime.Token); }
            catch (UnauthorizedAccessException) { SetNotice(NoSyncPlay); return; }
            catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); return; }
            if (!ReferenceEquals(client, _client) || DetailOverlay.Visibility != Visibility.Visible) return;
            foreach (var g in groups)
                menu.Items.Add(MenuAction(L($"Rejoindre « {g.GroupName} » · {Together.People(Math.Max(1, g.Participants.Count))}", $"Join “{g.GroupName}” · {Together.People(Math.Max(1, g.Participants.Count))}"), "users", () => EnterGroupAsync(c => c.SyncJoinAsync(g.GroupId), invite: false)));
            if (groups.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction(L("Créer un groupe et inviter…", "Create a group and invite…"), "plus", () => EnterGroupAsync(c => c.SyncNewAsync(Together.GroupName(c.Connection.UserName)), invite: true, made: true)));
        }
        menu.IsOpen = true;
    }
    /// <summary>
    /// « Créer un groupe » (<paramref name="made"/>, then its invitation: a new group is empty) or « Rejoindre … »: done
    /// once Jellyfin says GroupJoined.
    /// </summary>
    private async Task EnterGroupAsync(Func<JellyfinClient, Task> start, bool invite, bool made = false)
    {
        if (_client is not { } client) return;
        _inviteWait?.Cancel(); _inviteWait = null;
        _groupMade = made;
        var joined = _groupJoined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await start(client);
            await Task.WhenAny(joined.Task, Task.Delay(TimeSpan.FromSeconds(6)));
            if (_group is null && ReferenceEquals(client, _client)) SetNotice(L("Jellyfin n’a pas répondu. Réessaie dans un instant.", "Jellyfin didn’t answer. Try again in a moment."));
            else if (_group is { } group && invite) ShowInvite();
            else if (_group is { } joinedGroup) SetNotice(L($"Tu es dans « {joinedGroup.GroupName} » : Regarder lance le titre pour tout le groupe, au même moment chez chacun.", $"You’re in “{joinedGroup.GroupName}”: Watch starts the title for the whole group, at the same moment for everyone."));
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
        var link = Together.InviteLink(ReachableServer(client), group.GroupId, Together.GroupHost(group.GroupName));
        InviteLead.Text = L($"Envoie ce lien à ton ami pour qu’il rejoigne « {group.GroupName} ».", $"Send this link to your friend so they can join “{group.GroupName}”.");
        InviteLink.Text = link;
        InviteSteps.Children.Clear();
        var steps = Together.InviteSteps(link);
        for (var i = 0; i < steps.Count; i++)
            InviteSteps.Children.Add(new TextBlock { Text = $"{i + 1}. {steps[i]}", FontSize = 13, LineHeight = 19, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        InviteCopy.Content = ActionLabel("link", L("Copier le lien", "Copy link"));
        Motion.Reveal(InviteOverlay, 260, 0); Motion.Reveal(InviteCard, 360, 16);
        _ = ShowInviteAddFriendAsync(client);
        _ = Dispatcher.BeginInvoke(() => InviteCopy.Focus(), DispatcherPriority.Input);
    }
    /// <summary>
    /// Where a friend reaches this Jellyfin: on this PC, its Tailscale address when there is one (at home and away),
    /// else its address at home; another server, the account's own address.
    /// </summary>
    private static string ReachableServer(JellyfinClient client)
    {
        var server = client.Connection.Server;
        var here = Uri.TryCreate(server, UriKind.Absolute, out var uri) && LocalNetwork.IsThisPc(uri.Host);
        return Together.InviteServer(server, here, here ? LocalNetwork.ThisPcOnTailnet() : null, here ? LocalNetwork.ThisPc() : null).TrimEnd('/');
    }
    private void InviteCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(InviteLink.Text); SetNotice(L("Lien copié : envoie-le à ton ami.", "Link copied: send it to your friend.")); }
        catch (System.Runtime.InteropServices.ExternalException) { SetNotice(L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again.")); }
    }
    private async void InviteClose_Click(object sender, RoutedEventArgs e) => await CloseInviteAsync();
    private async Task CloseInviteAsync() { await Motion.HideAsync(InviteOverlay); if (DetailOverlay.Visibility == Visibility.Visible) TogetherButton.Focus(); }
    /// <summary>An invitation opened in Mira (a mira:// link), kept until an account of a Jellyfin is open to answer it.</summary>
    private Together.Invitation? _invite;
    /// <summary>
    /// « Ouvrir dans Mira » of Mira web (a mira:// link, App): once signed in, Mira asks before joining the group
    /// (« Rejoindre »); not signed in yet, the sign-in form gets the invitation's Jellyfin.
    /// </summary>
    internal void OpenLink(string text)
    {
        if (Together.ParseAppLink(text) is not { } invite) { SetNotice(L("Mira ne sait pas ouvrir ce lien.", "Mira can’t open this link.")); return; }
        _inviteWait?.Cancel(); _inviteWait = null;
        _invite = invite;
        _ = OfferInviteAsync();
    }
    private async Task OfferInviteAsync()
    {
        if (_invite is not { } invite) return;
        if (_client is not { } client || _demo)
        {
            if (LoginOverlay.Visibility != Visibility.Visible) return;
            ServerBox.Text = invite.Server; ShowSignInForm();
            SetNotice(Together.SignInToJoin);
            return;
        }
        // Jellyfin says GroupJoined through the WebSocket: open first, or the answer would go unheard.
        for (var i = 0; !_listening && i < 40 && ReferenceEquals(client, _client); i++) await Task.Delay(250);
        if (!ReferenceEquals(client, _client) || !ReferenceEquals(_invite, invite)) return;
        _invite = null;
        List<Together.Group> groups;
        try { groups = await client.SyncGroupsAsync(_connectionLifetime.Token); }
        catch (UnauthorizedAccessException) { SetNotice(NoSyncPlay); return; }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); return; }
        if (!ReferenceEquals(client, _client)) return;
        var answer = Together.Answer(invite, groups, client.Connection.Server, _group?.GroupId);
        RestoreFromWindows();
        if (answer is { Kind: Together.InviteKind.Join, Group: { } group })
            SetNotice(answer.Text, L("Rejoindre", "Join"), () => _ = EnterGroupAsync(c => c.SyncJoinAsync(group.GroupId), invite: false));
        else if (answer.Kind == Together.InviteKind.OtherServer)
            SetNotice(answer.Text, L("Ouvrir dans le navigateur", "Open in browser"), () => OpenInBrowser(Together.InviteLink(invite.Server, invite.GroupId, invite.Host)));
        else if (answer.Kind == Together.InviteKind.Waiting)
            SetNotice(answer.Text, L("Attendre", "Wait"), () => WaitForInvite(client, invite));
        else SetNotice(answer.Text);
    }
    /// <summary>
    /// « Attendre »: the group of an invitation joined as soon as its host opens it (Jellyfin's groups read every few
    /// seconds, three hours at most), unless another group, another invitation or another account comes first.
    /// </summary>
    private void WaitForInvite(JellyfinClient client, Together.Invitation invite)
    {
        if (!ReferenceEquals(client, _client)) return;
        _inviteWait?.Cancel();
        var wait = _inviteWait = CancellationTokenSource.CreateLinkedTokenSource(_connectionLifetime.Token);
        wait.CancelAfter(TimeSpan.FromHours(3));
        SetNotice(Together.WaitingLead, L("Annuler", "Cancel"), () => wait.Cancel(), sticky: false);
        _ = WaitAsync();
        async Task WaitAsync()
        {
            Together.Group group;
            try { group = await Together.WaitForGroupAsync(ReadAsync, invite, TimeSpan.FromSeconds(4), wait.Token); }
            catch (OperationCanceledException) { return; }
            if (!ReferenceEquals(_inviteWait, wait) || !ReferenceEquals(client, _client)) return;
            _inviteWait = null;
            if (_group is not null) return;
            RestoreFromWindows();
            await EnterGroupAsync(c => c.SyncJoinAsync(group.GroupId), invite: false);
        }
        async Task<IReadOnlyList<Together.Group>?> ReadAsync(CancellationToken ct)
        {
            try { return await client.SyncGroupsAsync(ct); }
            catch (UnauthorizedAccessException) { SetNotice(NoSyncPlay); wait.Cancel(); return null; }
            catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { return null; }
        }
    }
    private void OpenInBrowser(string link)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { SetNotice(L("Windows n’a pas pu ouvrir le navigateur : ", "Windows couldn’t open the browser: ") + link); }
    }
    /// <summary>Jellyfin's groups as Home last read them, for the account that read them.</summary>
    private (JellyfinClient? Client, List<Together.Group> Groups) _homeGroups = (null, []);
    /// <summary>The account Jellyfin does not let watch together (403 on its groups): Home asks no more.</summary>
    private JellyfinClient? _homeGroupsRefused;
    /// <summary>The group this PC was in when Home last showed its line.</summary>
    private string? _homeGroupFor;
    /// <summary>
    /// Home's line, as in Mira web: Jellyfin's groups read at each refresh of Home (<see cref="RefreshAsync"/>) and when
    /// this PC leaves a group; none for an account Jellyfin refuses, asked again only once signed in again.
    /// </summary>
    private async Task LoadHomeGroupsAsync(JellyfinClient client)
    {
        if (_demo || ReferenceEquals(client, _homeGroupsRefused)) return;
        List<Together.Group> groups;
        try { groups = await client.SyncGroupsAsync(_connectionLifetime.Token); }
        catch (UnauthorizedAccessException) { _homeGroupsRefused = client; groups = []; }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (!ReferenceEquals(client, _client)) return;
        _homeGroups = (client, groups); ShowHomeGroup();
    }
    /// <summary>« Groupe de Marc · 2 personnes » with « Rejoindre », on Home while this PC is in no group.</summary>
    private void ShowHomeGroup()
    {
        var home = _catalog.IsHome(SearchBox.Text.Trim()) && !_demo && _client is not null && ReferenceEquals(_homeGroups.Client, _client);
        var open = home ? Together.HomeGroup(_homeGroups.Groups, _group?.GroupId) : null;
        HomeGroup.Visibility = open is null ? Visibility.Collapsed : Visibility.Visible;
        if (open is null) return;
        HomeGroupName.Text = open.GroupName; HomeGroupPeople.Text = " · " + Together.People(Math.Max(1, open.Participants.Count));
        HomeGroupJoin.Tag = open.GroupId;
        System.Windows.Automation.AutomationProperties.SetName(HomeGroupJoin, L($"Rejoindre « {open.GroupName} »", $"Join “{open.GroupName}”"));
    }
    /// <summary>The group joined, and its title put on here once it plays one (GroupQueueAsync).</summary>
    private async void HomeGroupJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || HomeGroupJoin.Tag is not string id) return;
        HomeGroupJoin.IsEnabled = false;
        try
        {
            // It may have ended since Home read it: Jellyfin would answer a join with nothing to say.
            List<Together.Group> groups;
            try { groups = await client.SyncGroupsAsync(_connectionLifetime.Token); }
            catch (UnauthorizedAccessException) { SetNotice(NoSyncPlay); return; }
            catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); return; }
            if (!ReferenceEquals(client, _client)) return;
            _homeGroups = (client, groups); ShowHomeGroup();
            if (groups.FirstOrDefault(g => Together.SameGroup(g.GroupId, id)) is not { } group) { SetNotice(L("Ce groupe n’existe plus.", "This group no longer exists.")); return; }
            await EnterGroupAsync(c => c.SyncJoinAsync(group.GroupId), invite: false);
        }
        finally { HomeGroupJoin.IsEnabled = true; }
    }

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
