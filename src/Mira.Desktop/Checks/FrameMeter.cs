using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mira.Desktop;

/// <summary>
/// The frames of --smooth-check, phase by phase. A frame is one CompositionTarget.Rendering: its RenderingTime is the
/// refresh of the screen it aims at, so two frames two refreshes apart mean the screen showed the previous one twice (a
/// stutter). Beside it, the interface thread's work for each frame (the render operation: animations, layout, the scene
/// sent to the render thread), the other operations that held that thread, and what the render thread says it presented
/// (MediaContext's last presentation and its result, read by reflection: no public API gives them). Measuring keeps WPF
/// drawing at the screen's rate even when nothing moves, as a scroll or an animation does: a frame held back always shows.
/// </summary>
internal sealed class FrameMeter : IDisposable
{
    public sealed record Phase(string Name, double Seconds, double RefreshHz, int Frames, double Shown, int MissedRefreshes, double OnTime,
        double GapP50, double GapP95, double GapP99, double GapMax, double WorkP50, double WorkP95, double WorkMax, double Busy,
        double LongestOther, string? LongestOtherName, int Presented, double PresentP95, double PresentMax, string Results,
        double FirstFrame, int Gen0, int Gen1, int Gen2, double GcPause, string[] Slow);

    private static readonly Type? MediaContextType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext");
    private static readonly MethodInfo? From = MediaContextType?.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Dispatcher)]);
    private static FieldInfo? Field(string name) => MediaContextType?.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    // _displayRefreshRate is only kept when the render thread presents on its own vsync; through the DWM (every Windows
    // since 8), the screen's rate is _animationRenderRate, the rate WPF then paces its frames at.
    private static readonly FieldInfo? PresentedAt = Field("_lastPresentationTime"), Result = Field("_lastPresentationResults"), Refresh = Field("_displayRefreshRate"), Pace = Field("_animationRenderRate");
    private static readonly FieldInfo? OperationMethod = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Dispatcher _dispatcher;
    private readonly object? _context;
    private readonly Stack<(DispatcherOperation Operation, long At)> _running = new();
    private DispatcherOperation? _frameOperation;
    private TimeSpan _lastRendering = TimeSpan.MinValue;
    private long _lastPresented;
    private Recording? _phase;
    public List<Phase> Phases { get; } = [];
    /// <summary>What reflection found of WPF's private state (presentations unread without it).</summary>
    public string Sources => $"MediaContext {(_context is null ? "missing" : "found")}, presentation time {(PresentedAt is null ? "missing" : "found")}, result {(Result is null ? "missing" : "found")}, refresh rate {(Refresh is null ? "missing" : "found")}, pace {(Pace is null ? "missing" : "found")}";
    /// <summary>The screen's refresh rate as WPF knows it (0 before its first presentation).</summary>
    public int RefreshRate
    {
        get
        {
            if (_context is null) return 0;
            if (Refresh?.GetValue(_context) is int hz && hz > 0) return hz;
            return Result?.GetValue(_context)?.ToString() == "MIL_PRESENTATION_DWM" && Pace?.GetValue(_context) is int pace && pace > 0 ? pace : 0;
        }
    }

    public FrameMeter(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        try { _context = From?.Invoke(null, [dispatcher]); } catch (TargetInvocationException) { }
        CompositionTarget.Rendering += Rendering;
        dispatcher.Hooks.OperationStarted += Started; dispatcher.Hooks.OperationCompleted += Completed; dispatcher.Hooks.OperationAborted += Aborted;
    }
    public void Dispose()
    {
        CompositionTarget.Rendering -= Rendering;
        _dispatcher.Hooks.OperationStarted -= Started; _dispatcher.Hooks.OperationCompleted -= Completed; _dispatcher.Hooks.OperationAborted -= Aborted;
    }

    public void Begin(string name) { _phase = new Recording(name); }
    public Phase End()
    {
        var p = _phase ?? throw new InvalidOperationException("No phase under way.");
        _phase = null;
        var seconds = Elapsed(p.Started) / 1000;
        var hz = p.Refresh > 0 ? p.Refresh : RefreshRate > 0 ? RefreshRate : 60;
        var period = 1000d / hz;
        var missed = (int)p.Gaps.Sum(g => Math.Max(0, Math.Round(g / period) - 1));
        var onTime = p.Gaps.Count == 0 ? 1 : p.Gaps.Count(g => g <= period * 1.5) / (double)p.Gaps.Count;
        var phase = new Phase(p.Name, Math.Round(seconds, 3), hz, p.Gaps.Count, Math.Round(p.Gaps.Count / Math.Max(1e-9, seconds * hz), 3), missed, Math.Round(onTime, 4),
            Percentile(p.Gaps, .5), Percentile(p.Gaps, .95), Percentile(p.Gaps, .99), Percentile(p.Gaps, 1),
            Percentile(p.Work, .5), Percentile(p.Work, .95), Percentile(p.Work, 1), Math.Round(p.Busy / Math.Max(1e-9, seconds * 1000), 4),
            Math.Round(p.Longest, 2), p.LongestName, p.Presents.Count, Percentile(p.Presents, .95), Percentile(p.Presents, 1),
            string.Join(", ", p.Results.OrderByDescending(x => x.Value).Select(x => $"{x.Key.Replace("MIL_PRESENTATION_", "", StringComparison.Ordinal)} {x.Value}")),
            p.FirstFrameAt == 0 ? -1 : Math.Round((p.FirstFrameAt - p.Started) * 1000d / Stopwatch.Frequency, 2),
            GC.CollectionCount(0) - p.Gen0, GC.CollectionCount(1) - p.Gen1, GC.CollectionCount(2) - p.Gen2, Math.Round((GC.GetTotalPauseDuration() - p.Pause).TotalMilliseconds, 2),
            [.. p.Slow.OrderByDescending(x => x.Ms).Take(6).Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Name} {x.Ms:0.0} ms at {x.At:0} ms"))]);
        Phases.Add(phase);
        return phase;
    }
    private static double Percentile(List<double> values, double rank)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        return Math.Round(sorted[Math.Clamp((int)Math.Ceiling(rank * sorted.Count) - 1, 0, sorted.Count - 1)], 2);
    }
    private static double Elapsed(long since) => (Stopwatch.GetTimestamp() - since) * 1000d / Stopwatch.Frequency;

    private void Rendering(object? sender, EventArgs e)
    {
        // Raised more than once for one frame: one frame per rendering time.
        if (e is not RenderingEventArgs args || args.RenderingTime == _lastRendering) return;
        var previous = _lastRendering; _lastRendering = args.RenderingTime;
        // The operation running now is the frame's render operation: its length is this frame's work.
        _frameOperation = _running.Count > 0 ? _running.Peek().Operation : null;
        long presented = 0;
        if (_context is not null && PresentedAt?.GetValue(_context) is long at) presented = at;
        var p = _phase;
        if (p is not null)
        {
            if (p.FirstFrameAt == 0) p.FirstFrameAt = Stopwatch.GetTimestamp();
            if (previous != TimeSpan.MinValue) p.Gaps.Add((args.RenderingTime - previous).TotalMilliseconds);
            if (presented != 0 && presented != _lastPresented && _lastPresented != 0) p.Presents.Add((presented - _lastPresented) * 1000d / Stopwatch.Frequency);
            if (_context is not null)
            {
                var result = Result?.GetValue(_context)?.ToString() ?? "?";
                p.Results[result] = p.Results.GetValueOrDefault(result) + 1;
                if (RefreshRate is var hz && hz > 0) p.Refresh = hz;
            }
        }
        if (presented != 0) _lastPresented = presented;
    }
    private void Started(object? sender, DispatcherHookEventArgs e) => _running.Push((e.Operation, Stopwatch.GetTimestamp()));
    // An operation aborted before it ran never started: only one that is running is taken off.
    private void Aborted(object? sender, DispatcherHookEventArgs e) { if (_running.Count > 0 && _running.Peek().Operation == e.Operation) Completed(sender, e); }
    private void Completed(object? sender, DispatcherHookEventArgs e)
    {
        // Operations end in the reverse order they started (a nested frame runs its own inside one).
        if (_running.Count == 0 || _running.Peek().Operation != e.Operation) return;
        var (operation, at) = _running.Pop();
        var ms = Elapsed(at);
        var frame = ReferenceEquals(operation, _frameOperation);
        if (frame) _frameOperation = null;
        if (_phase is not { } p) return;
        if (_running.Count == 0) p.Busy += ms;
        if (frame) p.Work.Add(ms);
        else if (ms > p.Longest) (p.Longest, p.LongestName) = (ms, Describe(operation));
        if (ms >= 8) p.Slow.Add((frame ? "frame" : Describe(operation), Elapsed(p.Started) - ms, ms));
    }
    private static string Describe(DispatcherOperation operation)
    {
        // The method the operation calls, with its class (« MediaContext.RenderMessageHandler »); an await's continuation says whose.
        var method = OperationMethod?.GetValue(operation) as Delegate;
        var name = method is null ? "?" : $"{method.Method.DeclaringType?.Name}.{method.Method.Name}";
        if (method?.Target is { } target && target.GetType().Name.Contains('<', StringComparison.Ordinal)) name = target.GetType().Name;
        return $"{name} ({operation.Priority})";
    }

    /// <summary>The phases as a table: what the report and the job's summary show.</summary>
    public static string Table(IEnumerable<Phase> phases)
    {
        var c = CultureInfo.InvariantCulture; var text = new StringBuilder();
        text.AppendLine("| Phase | s | Hz | images | montrées | rafraîchissements manqués | à l'heure | écart p95 / max (ms) | travail p95 / max (ms) | fil occupé | plus longue autre (ms) | présentées | 1re image (ms) | GC 0/1/2 |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var p in phases)
            text.AppendLine(string.Create(c, $"| {p.Name} | {p.Seconds:0.0} | {p.RefreshHz:0} | {p.Frames} | {p.Shown:P0} | {p.MissedRefreshes} | {p.OnTime:P1} | {p.GapP95:0.0} / {p.GapMax:0.0} | {p.WorkP95:0.0} / {p.WorkMax:0.0} | {p.Busy:P0} | {p.LongestOther:0.0} | {p.Presented} | {p.FirstFrame:0} | {p.Gen0}/{p.Gen1}/{p.Gen2} |"));
        return text.ToString();
    }

    private sealed class Recording(string name)
    {
        public readonly string Name = name;
        public readonly long Started = Stopwatch.GetTimestamp();
        public long FirstFrameAt;
        public readonly List<double> Gaps = [], Work = [], Presents = [];
        public readonly Dictionary<string, int> Results = [];
        public readonly List<(string Name, double At, double Ms)> Slow = [];
        public double Busy, Longest;
        public string? LongestName;
        public int Refresh;
        public readonly int Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2);
        public readonly TimeSpan Pause = GC.GetTotalPauseDuration();
    }
}
