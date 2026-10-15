using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// « Regarder ensemble »: Jellyfin's SyncPlay, as in the Windows app (MainWindow.Together.cs) and Mira web (together.js).
/// A group plays one title at the same moment on each of its devices: Lecture puts a title on for the group, and pause,
/// play, seeks and the next episode go to Jellyfin, which tells each device what to do and when, on its clock
/// (<see cref="Together"/>). One group at a time, joined or left from the title page, left from the player's ⋮ too. The
/// group outlives the player (each playback has its own <see cref="PlayerPage"/>): it lives with the window. Jellyfin
/// lets a device go with its socket (Wi-Fi lost, the Mac asleep): back, this Mac joins the group again, or makes it again
/// under the same name when it made it and the group ended empty, so that the invitation sent still finds it. In the
/// player, a pill says what the group is doing (<see cref="PlayerPage.ShowGroup"/>).
/// </summary>
public sealed class WatchGroup
{
    public const string NoSyncPlay = "Jellyfin ne permet pas à ce compte de regarder ensemble (tableau de bord → Utilisateurs → SyncPlay).";
    private readonly MainWindow _shell;
    /// <summary>The account the group belongs to: messages of an account signed out since are ignored.</summary>
    private Session? _session;
    /// <summary>The group's title, as Jellyfin's last PlayQueue put it on.</summary>
    public Together.Entry? Queue { get; private set; }
    public Together.Group? Group { get; private set; }
    /// <summary>Jellyfin's clock minus this Mac's, in milliseconds.</summary>
    public long Offset { get; private set; }
    private TaskCompletionSource? _joined;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    /// <summary>The player closed here: Jellyfin is told not to wait for this Mac, or every seek would hold the others.</summary>
    private bool _away;
    private bool _listening;
    // In order: a Ready sent after its Buffering must arrive after it.
    private Task _sends = Task.CompletedTask;
    /// <summary>The group was made on this Mac: made again if Jellyfin ends it (<see cref="Together.RejoinPlan"/>).</summary>
    private bool _made;
    /// <summary>Back in the group: Jellyfin sends its title again, and holds the others until this Mac is ready.</summary>
    private bool _rejoined;
    /// <summary>Going back to the group after Jellyfin let this Mac go, once at a time.</summary>
    private Task? _recovering;
    /// <summary>The group changed (joined, left, someone came): the title page and the player's ⋮ show it again.</summary>
    public event Action? Changed;
    /// <summary>Someone came or went (« Marc a rejoint le groupe »): the player's pill says it, a notice otherwise.</summary>
    public event Action<string>? Member;

    public WatchGroup(MainWindow shell)
    {
        _shell = shell;
        _clock.Tick += async (_, _) => await SyncClockAsync();
    }
    public bool Active => Group is not null && _session is { } session && ReferenceEquals(session, _shell.Session);
    /// <summary>The player plays the group's title: its commands apply to it.</summary>
    public bool PlaysFor(PlayerPage player) => Active && Queue is { } queue && ReferenceEquals(_shell.Player, player) && player.Item?.Id == queue.ItemId;

    /// <summary>SyncPlayGroupUpdate and SyncPlayCommand of Jellyfin's WebSocket, on the UI thread.</summary>
    public void Message(Session session, string type, JsonElement data)
    {
        if (!ReferenceEquals(session, _shell.Session)) return;
        if (type == "SyncPlayCommand") { if (_shell.Player is { } player && PlaysFor(player)) player.GroupCommand(data); return; }
        var body = data.TryGetProperty("Data", out var d) ? d : default;
        var kind = data.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        switch (kind)
        {
            case "GroupJoined":
                if (Together.ParseGroup(body) is not { } group) return;
                // The same group again (back after a break): its title stays until Jellyfin sends it again. Jellyfin
                // takes this Mac as a newcomer, which it does not tell to wait or not: said again with the title.
                var again = Active && Group is { } was && Together.SameGroup(was.GroupId, group.GroupId);
                if (!again) Queue = null;
                _rejoined = again && Queue is not null; _away = false;
                _session = session; Group = group; _joined?.TrySetResult(); _joined = null;
                _clock.Start(); _ = SyncClockAsync(); Changed?.Invoke();
                break;
            case "UserJoined" or "UserLeft" when Group is not null:
                if (body.ValueKind == JsonValueKind.String && body.GetString() is { Length: > 0 } name) Member?.Invoke(Together.MemberText(name, kind == "UserJoined"));
                _ = RefreshAsync(session);
                break;
            // Idle, Waiting (for a device to load), Paused or Playing: never who caused it (Together.GroupStatus).
            case "StateUpdate" when Group is not null:
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.String)
                { Group = Group with { State = state.GetString()! }; Changed?.Invoke(); }
                break;
            case "GroupLeft": Gone(); break;
            // Jellyfin let this Mac go (its session ended): back in the group, or out of it when there is none to go back to.
            case "NotInGroup" or "GroupDoesNotExist":
                if (Group is null) Gone(); else Recover(session);
                break;
            case "LibraryAccessDenied": _shell.Notice("Un membre du groupe n’a pas accès à ce titre dans Jellyfin."); break;
            case "PlayQueue": _ = QueueAsync(session, body); break;
        }
    }
    /// <summary>The WebSocket opened again after a break: Jellyfin may have let the session go, with its place in the group.</summary>
    public void Listening(Session session, bool open)
    {
        if (!ReferenceEquals(session, _shell.Session)) return;
        var reopened = open && !_listening; _listening = open;
        if (reopened) Recover(session);
    }
    /// <summary>
    /// Back in the group after Jellyfin let this Mac go (its session ended with the WebSocket): joined again where it is,
    /// or made again under its name when this Mac made it and it ended empty (<see cref="Together.RejoinPlan"/>). Out of
    /// it when there is none to go back to, or when Jellyfin twice says nothing; still in it while Jellyfin cannot be
    /// reached (the socket's next opening tries again).
    /// </summary>
    private void Recover(Session session)
    {
        if (!Active || Group is not { } lost || !ReferenceEquals(session, _session) || _recovering is { IsCompleted: false }) return;
        _recovering = RecoverAsync();
        async Task RecoverAsync()
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var joined = _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    var plan = Together.RejoinPlan(await session.Client.SyncGroupsAsync(session.Lifetime), lost, _made);
                    if (!ReferenceEquals(session, _shell.Session) || !ReferenceEquals(Group, lost)) return;
                    if (plan is null) { Gone("Le groupe n’existe plus."); return; }
                    await (plan.Join is { } id ? session.Client.SyncJoinAsync(id, session.Lifetime) : session.Client.SyncNewAsync(plan.Create!, session.Lifetime));
                    await Task.WhenAny(joined.Task, Task.Delay(TimeSpan.FromSeconds(6), session.Lifetime));
                }
                catch (Exception ex) when (Errors.Expected(ex)) { return; }
                finally { if (ReferenceEquals(_joined, joined)) _joined = null; }
                if (!ReferenceEquals(Group, lost)) return;
            }
            Gone("Le groupe n’existe plus.");
        }
    }
    /// <summary>Jellyfin names each account once, whatever its devices: its own list, rather than a count kept here.</summary>
    private async Task RefreshAsync(Session session)
    {
        if (Group is not { } group) return;
        try
        {
            if (await session.Client.SyncGroupAsync(group.GroupId, session.Lifetime) is { } info && Group?.GroupId == info.GroupId)
            { Group = Group with { Participants = info.Participants }; Changed?.Invoke(); }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    /// <summary>Out of the group here (left, ended, signed out): the player plays on, alone.</summary>
    public void Gone(string? notice = null)
    {
        var was = Group is not null;
        Group = null; Queue = null; Offset = 0; _away = false; _session = null; _listening = false; _made = false; _rejoined = false;
        _clock.Stop(); _joined?.TrySetResult(); _joined = null;
        if (was) Changed?.Invoke();
        if (notice is not null) _shell.Notice(notice);
    }

    private async Task SyncClockAsync()
    {
        if (!Active || _session is not { } session) return;
        var samples = new List<Together.ClockSample>();
        for (var i = 0; i < 3; i++)
        {
            try { if (await session.Client.UtcTimeAsync(session.Lifetime) is { } s) samples.Add(new(s.Sent, s.Received, s.ServerIn, s.ServerOut)); }
            catch (Exception ex) when (Errors.Expected(ex)) { break; }
        }
        if (Together.ClockOffset(samples) is not { } best || !Active || !ReferenceEquals(session, _session)) return;
        Offset = best.Offset;
        // Jellyfin waits for the slowest device of the group before it says when to play.
        Send("Ping", new JsonObject { ["Ping"] = Math.Max(1, best.Trip) });
    }
    public void Send(string what, JsonObject? body = null)
    {
        if (!Active || _session is not { } session) return;
        var ct = session.Lifetime;
        _sends = SendAfterAsync(_sends);
        async Task SendAfterAsync(Task previous)
        {
            await previous;
            try { await session.Client.SyncSendAsync(what, body, ct); }
            catch (Exception ex) when (Errors.Expected(ex)) { }
        }
    }
    /// <summary>This Mac's state for the group's title: Buffering holds the others, Ready lets them go.</summary>
    public void Report(string what, double seconds, bool playing)
    {
        if (Queue is not { } queue) return;
        var when = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Offset).UtcDateTime;
        Send(what, new JsonObject { ["When"] = when.ToString("o", CultureInfo.InvariantCulture), ["PositionTicks"] = Ticks(seconds), ["IsPlaying"] = playing, ["PlaylistItemId"] = queue.PlaylistItemId });
    }
    public void SetAway(bool away)
    {
        if (!Active || _away == away) return;
        _away = away;
        Send("SetIgnoreWait", new JsonObject { ["IgnoreWait"] = away });
    }
    public static long Ticks(double seconds) => (long)Math.Round(Math.Max(0, seconds) * 1e7);

    /// <summary>The group's title: put on here too, paused where it starts until Jellyfin says when to play.</summary>
    private async Task QueueAsync(Session session, JsonElement data)
    {
        if (Together.QueueItem(data) is not { } next) { if (Queue is not null) { Queue = null; Changed?.Invoke(); } return; }
        var same = Queue?.PlaylistItemId == next.PlaylistItemId;
        // Shuffle, repeat or the list changed around the same title: nothing to load again. Unless this Mac is back in the
        // group: Jellyfin holds the others until it is ready there, or told that its player is closed.
        if (same && !_rejoined) return;
        _rejoined = false;
        Queue = next;
        // Lecture's label on the title page says whether it joins the group's title or replaces it.
        if (!same) Changed?.Invoke();
        if (same && !(_shell.Player is { } shown && shown.Item?.Id == next.ItemId && !shown.IsStopped)) { SetAway(true); return; }
        SetAway(false);
        var start = next.StartTicks / 1e7;
        Report("Buffering", start, false);
        // The same title put on again by another device: it waits there with the others.
        if (_shell.Player is { IsFileLoaded: true } player && player.Item?.Id == next.ItemId) { player.WaitForGroup(start); return; }
        try
        {
            var item = await session.Client.ItemAsync(next.ItemId, session.Lifetime);
            // Another title asked meanwhile, or another account.
            if (!ReferenceEquals(session, _shell.Session) || Queue?.PlaylistItemId != next.PlaylistItemId) return;
            // The episode that ended here already opened it, for the group.
            if (_shell.Player is { } playing && playing.Item?.Id == item.Id && !playing.IsStopped) return;
            // The next episode in the open player, as when one ends alone; otherwise the player opens for it.
            if (_shell.Player is { } open) await open.PlayForGroupAsync(item, next.StartTicks);
            else await _shell.PlayAsync(item, groupStart: next.StartTicks);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _shell.Notice(Errors.Friendly(ex)); }
    }
    /// <summary>
    /// Lecture in a group: Jellyfin puts the title on for every device, this one included (QueueAsync). True when the
    /// group took it; false when it plays here, at the group's title the player had left.
    /// </summary>
    public async Task<bool> AskAsync(MediaItem item, bool fromStart)
    {
        if (!Active || _session is not { } session) return false;
        if (Queue?.ItemId == item.Id)
        {
            // Every device reaches the end of an episode: the next one is put on once. The player closed on the
            // group's title opens again where the group is (Jellyfin moves it there once it is ready).
            return _shell.Player is { } player && player.Item?.Id == item.Id && !player.IsStopped;
        }
        var start = PlaybackSession.StartAt(item, remember: _shell.Settings.RememberPosition && !fromStart);
        try { await session.Client.SyncQueueAsync(item.Id, Ticks(start), session.Lifetime); }
        catch (UnauthorizedAccessException) { _shell.Notice(NoSyncPlay); }
        catch (Exception ex) when (Errors.Expected(ex)) { _shell.Notice(Errors.Friendly(ex)); }
        return true;
    }

    /// <summary>
    /// « Remplacer « Dune » pour tout le groupe ? », with « Remplacer »: the title the group has on (named by one request)
    /// is replaced for everyone only once asked.
    /// </summary>
    public async Task OfferReplaceAsync(MediaItem item, bool fromStart)
    {
        if (!Active || _session is not { } session || Queue is not { } queue) return;
        string? name = null;
        try { name = Together.TitleName(await session.Client.ItemAsync(queue.ItemId, session.Lifetime)); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        if (!Active || !ReferenceEquals(queue, Queue)) return;
        _shell.Notice(Together.ReplaceQuestion(name) + " Le groupe le regarde en ce moment.", "Remplacer", () => _ = AskAsync(item, fromStart), sticky: true);
    }
    /// <summary>« Créer un groupe » (<paramref name="made"/>) or « Rejoindre … »: done once Jellyfin says GroupJoined.</summary>
    public async Task EnterAsync(Func<JellyfinClient, CancellationToken, Task> start, bool invite = false, bool made = false)
    {
        if (_shell.Session is not { } session) return;
        _made = made;
        var joined = _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await start(session.Client, session.Lifetime);
            await Task.WhenAny(joined.Task, Task.Delay(TimeSpan.FromSeconds(6)));
            if (Group is null && ReferenceEquals(session, _shell.Session)) _shell.Notice("Jellyfin n’a pas répondu. Réessaie dans un instant.");
            // A new group is empty: its invitation comes at once, the way to bring someone in.
            else if (Group is not null && invite) await InviteAsync();
            else if (Group is { } group) _shell.Notice($"Tu es dans « {group.GroupName} » : Lecture lance le titre pour tout le groupe, au même moment chez chacun.");
        }
        catch (UnauthorizedAccessException) { _shell.Notice(NoSyncPlay); }
        catch (Exception ex) when (Errors.Expected(ex)) { _shell.Notice(Errors.Friendly(ex)); }
        finally { if (ReferenceEquals(_joined, joined)) _joined = null; }
    }
    /// <summary>
    /// « Inviter quelqu'un »: the link that opens Mira web on the group. A Jellyfin on this Mac is given at its
    /// Tailscale address when there is one; another keeps the address this Mac reaches it at.
    /// </summary>
    public async Task InviteAsync()
    {
        if (!Active || Group is not { } group || _session is not { } session) return;
        var server = session.Connection.Server;
        var here = Uri.TryCreate(server, UriKind.Absolute, out var uri) && LocalNetwork.IsThisPc(uri.Host);
        var link = Together.InviteLink(Together.InviteServer(server, here, here ? LocalNetwork.ThisPcOnTailnet() : null, here ? LocalNetwork.ThisPc() : null), group.GroupId, Together.GroupHost(group.GroupName));
        await new InviteWindow(group, link, text => _shell.Notice(text)).ShowDialog(_shell);
    }
    public async Task LeaveAsync()
    {
        if (_session is { } session && ReferenceEquals(session, _shell.Session))
        {
            try { await session.Client.SyncLeaveAsync(session.Lifetime); }
            catch (Exception ex) when (Errors.Expected(ex)) { }
        }
        Gone();
    }
}
