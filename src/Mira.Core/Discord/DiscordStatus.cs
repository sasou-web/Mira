using System.Text.Json.Nodes;

namespace Mira.Core.Discord;

/// <summary>Where the card stands, as Réglages → Discord says it.</summary>
public enum DiscordState
{
    /// <summary>The setting is off: Mira does not even look for Discord.</summary>
    Off,
    /// <summary>On, nothing to show yet (nothing plays, or the title is kept off Discord).</summary>
    Idle,
    /// <summary>Something to show, and no Discord app answers on this computer: asked again every 30 s.</summary>
    Looking,
    /// <summary>The card is on the profile.</summary>
    Live,
    /// <summary>« Pause my Discord status »: nothing shown until it is turned back on or Mira closes.</summary>
    HiddenForNow,
}

/// <summary>
/// Keeps the Discord app's card as Mira's playback is (Windows and Mac): each tick of the player hands it what plays
/// (<see cref="Update"/>); it works the card out (<see cref="DiscordPresence.Card"/>) and sends it only when it changed
/// (a title, a pause, a seek, a speed, a setting), the latest one alone, at most 5 in 20 s (Discord's limit). Discord
/// absent: tried again every 30 s while there is something to show; nothing to show, no connection at all. A card that
/// would name the server or the person (<see cref="DiscordPresence.Leaks"/>) is never sent: it is cleared instead.
/// </summary>
public sealed class DiscordStatus : IAsyncDisposable
{
    private readonly string _app;
    private readonly Func<CancellationToken, Task<DiscordIpc?>> _connect;
    private readonly TimeSpan _retry, _window;
    private readonly int _most;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Queue<DateTimeOffset> _sent = new();
    private Task? _loop;
    private DiscordIpc? _ipc;
    private DateTimeOffset _triedAt = DateTimeOffset.MinValue;
    // What should be on the card, and what is: their JSON, the times left out, and the times apart.
    private JsonObject? _wanted;
    private string? _wantedText;
    private long? _wantedStart;
    private bool _wantedChanged, _hasSent;
    private string? _itemId;
    private DateTimeOffset? _pausedSince;
    private bool _enabled, _hidden;
    private DiscordState _state = DiscordState.Off;

    /// <param name="app">« Windows » or « Mac », said by the badge.</param>
    /// <param name="connect">The Discord app's pipe; the checks give a stand-in.</param>
    public DiscordStatus(string app, Func<CancellationToken, Task<DiscordIpc?>>? connect = null, TimeSpan? retry = null, int most = 5, TimeSpan? window = null)
    {
        _app = app; _connect = connect ?? (ct => DiscordIpc.ConnectAsync(DiscordPresence.ApplicationId, ct));
        _retry = retry ?? TimeSpan.FromSeconds(30); _most = most; _window = window ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>The names that must never leave in an address: the server's host, the account's name, the PC's (<see cref="DiscordPresence.Leaks"/>).</summary>
    public Func<IEnumerable<string?>> Private { get; set; } = () => [Environment.MachineName];
    /// <summary>The card last worked out (what friends see, or are about to): the preview of Réglages shows it.</summary>
    public DiscordCard? Card { get; private set; }
    /// <summary>What Discord said of a card it refused, or why the pipe closed, for the line of Réglages; null while all is well.</summary>
    public string? Error { get; private set; }
    /// <summary>A card held back because it named the server or the person: said once in the errors' log by the app.</summary>
    public IReadOnlyList<string> Withheld { get; private set; } = [];
    public DiscordState State { get { lock (_gate) return _state; } }
    /// <summary>Raised when <see cref="State"/> or <see cref="Card"/> changes, on any thread.</summary>
    public event Action? Changed;

    /// <summary>« Pause my Discord status » (Réglages, the player's ⋮, the notification area's menu): kept until turned back on or Mira closes.</summary>
    public bool HiddenForNow
    {
        get { lock (_gate) return _hidden; }
        set { lock (_gate) _hidden = value; }
    }

    /// <summary>What plays now (null: nothing), with this device's settings: the card follows, sent when it changed.</summary>
    public void Update(DiscordSettings settings, DiscordPlayback? playback, DateTimeOffset now)
    {
        DiscordCard? card = null;
        bool changed;
        lock (_gate)
        {
            _enabled = settings.Enabled;
            if (playback?.Item.Id != _itemId) { _itemId = playback?.Item.Id; _pausedSince = null; }
            if (playback is { Paused: true }) _pausedSince ??= now; else _pausedSince = null;
            if (_enabled && !_hidden)
                card = playback is null ? DiscordPresence.Browsing(settings) : DiscordPresence.Card(settings, playback, now, now - (_pausedSince ?? now), _app);
            var activity = card?.Activity();
            long? start = activity?["timestamps"]?["start"]?.GetValue<long>();
            var timeless = activity?.DeepClone().AsObject();
            timeless?.Remove("timestamps");
            var text = timeless?.ToJsonString();
            // The same card a second later moves its times by the clock alone: sent again only when they drift (a seek, a speed).
            var moved = (start is null) != (_wantedStart is null) || start is { } s && _wantedStart is { } w && Math.Abs(s - w) > 2000;
            changed = text != _wantedText || moved || (Card is null) != (card is null);
            if (changed) { _wanted = activity; _wantedText = text; _wantedStart = start; _wantedChanged = true; }
            Card = card;
            var state = !_enabled ? DiscordState.Off : _hidden ? DiscordState.HiddenForNow : card is null ? DiscordState.Idle : _ipc is { Closed: false } ? DiscordState.Live : _state == DiscordState.Live ? DiscordState.Live : DiscordState.Looking;
            if (state != _state) { _state = state; changed = true; }
        }
        if (!changed) return;
        _loop ??= Task.Run(RunAsync);
        Wake();
        Changed?.Invoke();
    }

    private void Wake() { lock (_gate) if (_wake.CurrentCount == 0) _wake.Release(); }

    private async Task RunAsync()
    {
        var ct = _life.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                JsonObject? wanted; bool enabled, fresh;
                lock (_gate) { wanted = _wanted; enabled = _enabled; fresh = _wantedChanged; }
                // A card to show that changed or whose pipe broke; a card to take away; off with a pipe still open.
                var due = enabled ? wanted is not null ? fresh || _ipc is not { Closed: false } : fresh && _hasSent : _ipc is not null;
                if (!due) { await _wake.WaitAsync(ct); continue; }
                if (!enabled)
                {
                    // Turned off: the card goes, and so does the pipe.
                    await DropAsync(); _hasSent = false;
                    continue;
                }
                if (_ipc is not { Closed: false })
                {
                    if (_ipc is not null) { await _ipc.DisposeAsync(); _ipc = null; }
                    var wait = _triedAt + _retry - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) { await _wake.WaitAsync(wait, ct); continue; }
                    _triedAt = DateTimeOffset.UtcNow;
                    _ipc = await _connect(ct);
                    if (_ipc is null) { Say(DiscordState.Looking); continue; }
                    _ipc.Broken += Wake;
                }
                var now = DateTimeOffset.UtcNow;
                while (_sent.Count > 0 && now - _sent.Peek() >= _window) _sent.Dequeue();
                if (_sent.Count >= _most) { await _wake.WaitAsync(_sent.Peek() + _window - now, ct); continue; }
                var snapshot = wanted;
                if (wanted is not null)
                {
                    var leaks = DiscordPresence.Leaks(wanted, Private(), wanted["buttons"]?.AsArray().Select(x => x?["url"]?.GetValue<string>()).FirstOrDefault(x => DiscordPresence.PartyButton(x) is not null));
                    if (leaks.Count > 0) { Withheld = leaks; wanted = null; }
                }
                // Done with this one only if no newer card came meanwhile (it is sent next, by itself).
                lock (_gate) if (ReferenceEquals(_wanted, snapshot)) _wantedChanged = false;
                if (!await _ipc.SetActivityAsync(wanted, ct)) { lock (_gate) _wantedChanged = true; Say(DiscordState.Looking); continue; }
                _sent.Enqueue(now);
                _hasSent = wanted is not null;
                Error = _ipc.Error;
                Say(wanted is null ? DiscordState.Idle : DiscordState.Live);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Never the end of the loop: the card is tried again a moment later.
                Error = ex.Message;
                if (_ipc is not null) { try { await _ipc.DisposeAsync(); } catch (Exception) { } _ipc = null; }
                Say(DiscordState.Looking);
                try { await Task.Delay(_retry, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private void Say(DiscordState state)
    {
        lock (_gate)
        {
            if (!_enabled) state = DiscordState.Off;
            else if (_hidden) state = DiscordState.HiddenForNow;
            else if (Card is null) state = DiscordState.Idle;
            if (state == _state) return;
            _state = state;
        }
        Changed?.Invoke();
    }

    private async Task DropAsync()
    {
        if (_ipc is null) return;
        var ipc = _ipc; _ipc = null;
        await ipc.DisposeAsync();
    }

    /// <summary>Mira closes: the card is cleared and the pipe closed.</summary>
    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();
        if (_loop is not null) try { await _loop; } catch (OperationCanceledException) { }
        await DropAsync();
        _life.Dispose(); _wake.Dispose();
    }
}
