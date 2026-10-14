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
/// group outlives the player (each playback has its own <see cref="PlayerPage"/>): it lives with the window.
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
    /// <summary>The group changed (joined, left, someone came): the title page and the player's ⋮ show it again.</summary>
    public event Action? Changed;

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
                _session = session; Group = group; _joined?.TrySetResult(); _joined = null;
                _clock.Start(); _ = SyncClockAsync(); Changed?.Invoke();
                break;
            case "UserJoined" or "UserLeft" when Group is not null:
                if (body.ValueKind == JsonValueKind.String) _shell.Notice($"{body.GetString()} {(kind == "UserJoined" ? "a rejoint" : "a quitté")} le groupe.");
                _ = RefreshAsync(session);
                break;
            case "GroupLeft": Gone(); break;
            case "NotInGroup" or "GroupDoesNotExist": Gone(Group is null ? null : "Le groupe n’existe plus."); break;
            case "LibraryAccessDenied": _shell.Notice("Un membre du groupe n’a pas accès à ce titre dans Jellyfin."); break;
            case "PlayQueue": _ = QueueAsync(session, body); break;
        }
    }
    /// <summary>The WebSocket opened again after a break: Jellyfin may have let the session go, with its place in the group.</summary>
    public void Listening(Session session, bool open)
    {
        if (!ReferenceEquals(session, _shell.Session)) return;
        var reopened = open && !_listening; _listening = open;
        if (!reopened || !Active || Group is not { } group) return;
        _ = RejoinAsync();
        async Task RejoinAsync() { try { await session.Client.SyncJoinAsync(group.GroupId, session.Lifetime); } catch (Exception ex) when (Errors.Expected(ex)) { } }
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
        Group = null; Queue = null; Offset = 0; _away = false; _session = null; _listening = false;
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
        if (Together.QueueItem(data) is not { } next) { Queue = null; return; }
        // Shuffle, repeat or the list changed around the same title: nothing to load again.
        if (Queue?.PlaylistItemId == next.PlaylistItemId) return;
        Queue = next; SetAway(false);
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

    /// <summary>« Créer un groupe » or « Rejoindre … »: done once Jellyfin says GroupJoined.</summary>
    public async Task EnterAsync(Func<JellyfinClient, CancellationToken, Task> start)
    {
        if (_shell.Session is not { } session) return;
        var joined = _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await start(session.Client, session.Lifetime);
            await Task.WhenAny(joined.Task, Task.Delay(TimeSpan.FromSeconds(6)));
            if (Group is null && ReferenceEquals(session, _shell.Session)) _shell.Notice("Jellyfin n’a pas répondu. Réessaie dans un instant.");
            else if (Group is { } group) _shell.Notice($"Tu es dans « {group.GroupName} » : Lecture lance le titre pour tout le groupe, au même moment chez chacun.");
        }
        catch (UnauthorizedAccessException) { _shell.Notice(NoSyncPlay); }
        catch (Exception ex) when (Errors.Expected(ex)) { _shell.Notice(Errors.Friendly(ex)); }
        finally { if (ReferenceEquals(_joined, joined)) _joined = null; }
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
