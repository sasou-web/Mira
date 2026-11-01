using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mira.Desktop.Services;

/// <summary>
/// What a transition costs (leaving the player, the mini-player): the frames WPF drew and the gaps between them, the
/// work of the interface's thread that held a frame back (by operation and by step of Mira's), garbage collections.
/// Off unless asked: <c>MIRA_FRAME_PROBE=&lt;file&gt;</c> appends one line per transition to that file (a real PC,
/// a real library), the player check reads <see cref="Last"/>. Nothing is hooked when off.
/// </summary>
internal sealed class FrameProbe
{
    public static string? LogPath { get; } = Environment.GetEnvironmentVariable("MIRA_FRAME_PROBE") is { Length: > 0 } path ? path : null;
    /// <summary>The player check measures without a log file.</summary>
    public static bool Forced { get; set; }
    public static string? Last { get; private set; }
    private static FrameProbe? _current;
    private static readonly FieldInfo? OperationMethod = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly string _label;
    private readonly Dispatcher _dispatcher;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<double> _frames = [];
    private readonly List<(string Name, double At, double Ms)> _work = [];
    private readonly List<(string Name, double At, double Ms)> _steps = [];
    private readonly Dictionary<DispatcherOperation, double> _started = [];
    private TimeSpan _lastRendering;
    private readonly int _gen0 = GC.CollectionCount(0), _gen1 = GC.CollectionCount(1), _gen2 = GC.CollectionCount(2);
    private readonly TimeSpan _pause = GC.GetTotalPauseDuration();
    private bool _done;

    private FrameProbe(string label, Dispatcher dispatcher) { _label = label; _dispatcher = dispatcher; }

    /// <summary>Starts measuring for <paramref name="ms"/> (a probe still running ends first, written as it is).</summary>
    public static void Begin(string label, int ms = 2500)
    {
        if (LogPath is null && !Forced) return;
        _current?.End();
        var probe = _current = new FrameProbe(label, Dispatcher.CurrentDispatcher);
        CompositionTarget.Rendering += probe.Rendering;
        probe._dispatcher.Hooks.OperationStarted += probe.Started;
        probe._dispatcher.Hooks.OperationCompleted += probe.Completed;
        probe._dispatcher.Hooks.OperationAborted += probe.Completed;
        _ = probe.EndLaterAsync(ms);
    }
    /// <summary>A part of Mira's own work, timed while a probe runs; nothing otherwise.</summary>
    public static Step Time(string name) => new(_current is { _done: false } probe ? probe : null, name);
    /// <summary>A moment worth placing among the frames (the fade has ended, Jellyfin has answered).</summary>
    public static void Mark(string name) { if (_current is { _done: false } probe) probe._steps.Add((name, probe._clock.Elapsed.TotalMilliseconds, 0)); }
    /// <summary>Work done on another thread (mpv released), placed when it ends.</summary>
    public static void Finished(string name, double ms)
    {
        if (_current is not { _done: false } probe) return;
        probe._dispatcher.BeginInvoke(() => { if (!probe._done) probe._steps.Add((name, probe._clock.Elapsed.TotalMilliseconds - ms, ms)); });
    }

    public readonly struct Step(FrameProbe? probe, string name) : IDisposable
    {
        private readonly double _at = probe?._clock.Elapsed.TotalMilliseconds ?? 0;
        public void Dispose()
        {
            if (probe is null || probe._done) return;
            probe._steps.Add((name, _at, probe._clock.Elapsed.TotalMilliseconds - _at));
        }
    }

    private void Rendering(object? sender, EventArgs e)
    {
        // Raised more than once for one frame: one frame per rendering time.
        if (e is RenderingEventArgs args) { if (args.RenderingTime == _lastRendering) return; _lastRendering = args.RenderingTime; }
        _frames.Add(_clock.Elapsed.TotalMilliseconds);
    }
    private void Started(object? sender, DispatcherHookEventArgs e) => _started[e.Operation] = _clock.Elapsed.TotalMilliseconds;
    private void Completed(object? sender, DispatcherHookEventArgs e)
    {
        if (!_started.Remove(e.Operation, out var at)) return;
        var ms = _clock.Elapsed.TotalMilliseconds - at;
        if (ms >= 8) _work.Add((Describe(e.Operation), at, ms));
    }
    private static string Describe(DispatcherOperation operation)
    {
        // The method the operation calls, with its class (« MediaContext.RenderMessageHandlerCore »).
        var method = (OperationMethod?.GetValue(operation) as Delegate)?.Method;
        var name = method is null ? "?" : $"{method.DeclaringType?.FullName?.Split('.')[^1]}.{method.Name}";
        return $"{name} ({operation.Priority})";
    }
    private async Task EndLaterAsync(int ms) { await Task.Delay(ms); End(); }

    private void End()
    {
        if (_done) return; _done = true;
        if (ReferenceEquals(_current, this)) _current = null;
        CompositionTarget.Rendering -= Rendering;
        _dispatcher.Hooks.OperationStarted -= Started; _dispatcher.Hooks.OperationCompleted -= Completed; _dispatcher.Hooks.OperationAborted -= Completed;
        var line = Summary();
        Last = line;
        if (LogPath is { } path) { try { File.AppendAllText(path, line + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private string Summary()
    {
        var c = CultureInfo.InvariantCulture; var text = new StringBuilder();
        var gaps = _frames.Zip(_frames.Skip(1), (a, b) => (At: a, Ms: b - a)).ToList();
        var typical = gaps.Count == 0 ? 0 : gaps.Select(x => x.Ms).Order().ElementAt(gaps.Count / 2);
        var late = gaps.Where(x => x.Ms > Math.Max(25, typical * 1.8)).OrderByDescending(x => x.Ms).Take(8).ToList();
        text.Append(c, $"{DateTimeOffset.Now:HH:mm:ss} {_label}: {_clock.Elapsed.TotalMilliseconds:0} ms, {_frames.Count} frames, typical {typical:0.0} ms, ");
        text.Append(c, $"longest {(gaps.Count == 0 ? 0 : gaps.Max(x => x.Ms)):0} ms, {gaps.Count(x => x.Ms > Math.Max(25, typical * 1.8))} late");
        if (_frames.Count > 0) text.Append(c, $" (first frame at {_frames[0]:0} ms)");
        if (late.Count > 0) text.Append(" [").AppendJoin(", ", late.Select(x => string.Create(c, $"{x.Ms:0} at {x.At:0}"))).Append(']');
        text.Append("; work: ").AppendJoin(", ", _work.OrderByDescending(x => x.Ms).Take(10).Select(x => string.Create(c, $"{x.Name} {x.Ms:0} at {x.At:0}")));
        text.Append("; steps: ").AppendJoin(", ", _steps.OrderBy(x => x.At).Select(x => x.Ms > 0 ? string.Create(c, $"{x.Name} {x.Ms:0.0} at {x.At:0}") : string.Create(c, $"{x.Name} at {x.At:0}")));
        text.Append(c, $"; GC {GC.CollectionCount(0) - _gen0}/{GC.CollectionCount(1) - _gen1}/{GC.CollectionCount(2) - _gen2}, pause {(GC.GetTotalPauseDuration() - _pause).TotalMilliseconds:0} ms");
        text.Append(c, $"; render tier {RenderCapability.Tier >> 16}");
        return text.ToString();
    }
}
