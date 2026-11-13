using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Desktop.Views;

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
        double FirstFrame, int Gen0, int Gen1, int Gen2, double GcPause, string[] Slow,
        int Repeated, string PresentGaps, int Jerks, double JerkMax, double TailMs, double Moved, string LateCauses);

    private static readonly Type? MediaContextType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext");
    private static readonly MethodInfo? From = MediaContextType?.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Dispatcher)]);
    private static FieldInfo? Field(string name) => MediaContextType?.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    // _displayRefreshRate is only kept when the render thread presents on its own vsync; through the DWM (every Windows
    // since 8), the screen's rate is _animationRenderRate, the rate WPF then paces its frames at.
    private static readonly FieldInfo? PresentedAt = Field("_lastPresentationTime"), Result = Field("_lastPresentationResults"), Refresh = Field("_displayRefreshRate"), Pace = Field("_animationRenderRate");
    private static readonly FieldInfo? OperationMethod = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? OperationArgs = typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);

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

    /// <summary>A phase begins; <paramref name="viewer"/>, if any, is the list whose motion its frames trace.</summary>
    public void Begin(string name, ScrollViewer? viewer = null)
    {
        _phase = new Recording(name) { Viewer = viewer, Scale = viewer is null ? 1 : VisualTreeHelper.GetDpi(viewer).DpiScaleY };
        FrameEvents.Take();
    }
    public Phase End()
    {
        var p = _phase ?? throw new InvalidOperationException("No phase under way.");
        _phase = null;
        var seconds = Elapsed(p.Started) / 1000;
        var hz = p.Refresh > 0 ? p.Refresh : RefreshRate > 0 ? RefreshRate : 60;
        var period = 1000d / hz;
        var missed = (int)p.Gaps.Sum(g => Math.Max(0, Math.Round(g / period) - 1));
        var onTime = p.Gaps.Count == 0 ? 1 : p.Gaps.Count(g => g <= period * 1.5) / (double)p.Gaps.Count;
        if (p.Viewer is { } watched && p.Frames.Count > 0) p.Frames[^1].Offset = watched.VerticalOffset;
        var motion = Motion(p.Frames, period, p.Scale);
        var phase = new Phase(p.Name, Math.Round(seconds, 3), hz, p.Gaps.Count, Math.Round(p.Gaps.Count / Math.Max(1e-9, seconds * hz), 3), missed, Math.Round(onTime, 4),
            Percentile(p.Gaps, .5), Percentile(p.Gaps, .95), Percentile(p.Gaps, .99), Percentile(p.Gaps, 1),
            Percentile(p.Work, .5), Percentile(p.Work, .95), Percentile(p.Work, 1), Math.Round(p.Busy / Math.Max(1e-9, seconds * 1000), 4),
            Math.Round(p.Longest, 2), p.LongestName, p.Presents.Count, Percentile(p.Presents, .95), Percentile(p.Presents, 1),
            string.Join(", ", p.Results.OrderByDescending(x => x.Value).Select(x => $"{x.Key.Replace("MIL_PRESENTATION_", "", StringComparison.Ordinal)} {x.Value}")),
            p.FirstFrameAt == 0 ? -1 : Math.Round((p.FirstFrameAt - p.Started) * 1000d / Stopwatch.Frequency, 2),
            GC.CollectionCount(0) - p.Gen0, GC.CollectionCount(1) - p.Gen1, GC.CollectionCount(2) - p.Gen2, Math.Round((GC.GetTotalPauseDuration() - p.Pause).TotalMilliseconds, 2),
            [.. p.Slow.OrderByDescending(x => x.Ms).Take(6).Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Name} {x.Ms:0.0} ms at {x.At:0} ms"))],
            motion.Repeated, motion.PresentGaps, motion.Jerks, motion.JerkMax, motion.TailMs, motion.Moved, motion.LateCauses);
        Phases.Add(phase); _traces.Add((p.Name, p.Frames));
        return phase;
    }

    private readonly List<(string Name, List<Frame> Frames)> _traces = [];
    /// <summary>Each phase's frames, one line each (frames-NN.csv): when, the gap since the one before, the work of the interface's thread, where the list stood, the presentation's gap, what changed.</summary>
    public void WriteTraces(string folder)
    {
        var c = CultureInfo.InvariantCulture;
        for (var i = 0; i < _traces.Count; i++)
        {
            var text = new StringBuilder("# ").Append(_traces[i].Name).Append('\n').Append("ms,gap_ms,work_ms,offset,present_gap_ms,hover,images,pictures\n");
            foreach (var f in _traces[i].Frames)
                text.Append(string.Create(c, $"{f.At:0.00},{f.Gap:0.00},{f.Work:0.00},{f.Offset:0.###},{f.Present:0.00},{f.Hover},{f.Images},{f.Pictures}\n"));
            File.WriteAllText(Path.Combine(folder, string.Create(c, $"frames-{i + 1:00}.csv")), text.ToString());
        }
    }

    private sealed record Moving(int Repeated, string PresentGaps, int Jerks, double JerkMax, double TailMs, double Moved, string LateCauses);
    /// <summary>
    /// What the eye sees of the frames: refreshes that showed a frame again (by the presentations, else by the frames'
    /// own gaps), and the list's motion — a jerk is a change of speed from one frame to the next of more than a quarter
    /// of it and more than a pixel of the screen (a curve that starts again at each notch makes one at each), the
    /// time its last pixel took to cover — and what came with the frames that came late.
    /// </summary>
    private static Moving Motion(List<Frame> frames, double period, double scale)
    {
        var c = CultureInfo.InvariantCulture;
        // Presentations: how many refreshes each one stayed on the screen, while the list moves (from its first move to its
        // last): one that stood while it was still, before the first notch or once it stopped, repeated nothing.
        var moves = Enumerable.Range(1, Math.Max(0, frames.Count - 1)).Where(i => !double.IsNaN(frames[i].Offset) && !double.IsNaN(frames[i - 1].Offset) && frames[i].Offset != frames[i - 1].Offset).ToList();
        var span = moves.Count == 0 ? frames : frames.GetRange(moves[0], moves[^1] - moves[0] + 1);
        var presents = span.Where(f => !double.IsNaN(f.Present)).Select(f => Math.Max(1, (int)Math.Round(f.Present / period))).ToList();
        var repeated = presents.Count > 0 ? presents.Sum(n => n - 1) : span.Skip(1).Sum(f => Math.Max(0, (int)Math.Round(f.Gap / period) - 1));
        var histogram = presents.Count == 0 ? "" : string.Join(" ", presents.GroupBy(n => Math.Min(n, 4)).OrderBy(g => g.Key).Select(g => $"{(g.Key == 4 ? "4+" : g.Key.ToString(c))}×{g.Count()}"));
        // Speed in screen pixels per refresh, each frame's move spread over the refreshes it stood for.
        var speeds = new List<(int Index, double Speed)>();
        double? last = null; var moved = 0d;
        for (var i = 0; i < frames.Count; i++)
        {
            var at = frames[i].Offset;
            if (double.IsNaN(at)) continue;
            if (last is { } before && i > 0)
            {
                var step = (at - before) * scale; moved += Math.Abs(step);
                speeds.Add((i, step / Math.Max(1, Math.Round(frames[i].Gap / period))));
            }
            last = at;
        }
        int jerks = 0; var jerkMax = 0d;
        for (var k = 1; k < speeds.Count; k++)
        {
            var (a, b) = (speeds[k - 1].Speed, speeds[k].Speed);
            if (a == 0 || b == 0 || speeds[k].Index != speeds[k - 1].Index + 1) continue;
            var change = Math.Abs(b - a); var larger = Math.Max(Math.Abs(a), Math.Abs(b));
            if (change > Math.Max(1, .25 * larger)) jerks++;
            if (larger >= 2) jerkMax = Math.Max(jerkMax, change / larger);
        }
        // The last pixel: from the frame left within a pixel of where the list ends to the last that moved it.
        var tail = 0d;
        var lastMove = speeds.FindLastIndex(x => x.Speed != 0);
        if (lastMove >= 0 && !double.IsNaN(frames[^1].Offset))
        {
            var end = frames[^1].Offset; var stop = frames[speeds[lastMove].Index].At; var near = stop;
            for (var k = lastMove; k >= 0 && Math.Abs(end - frames[speeds[k].Index].Offset) * scale <= 1; k--) near = frames[speeds[k].Index].At;
            tail = stop - near;
        }
        // The frames that came late, and what came with them (or the frame before).
        var causes = new Dictionary<string, int>();
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i].Gap <= period * 1.5) continue;
            var (f, before) = (frames[i], frames[i - 1]);
            var found = false;
            void Note(bool when, string what) { if (!when) return; causes[what] = causes.GetValueOrDefault(what) + 1; found = true; }
            Note(f.Hover + before.Hover > 0, "survol");
            Note(f.Images + before.Images > 0, "images");
            Note(f.Pictures + before.Pictures > 0, "images figées");
            Note(before.Work > period, "fil de l’interface");
            if (!found) causes["?"] = causes.GetValueOrDefault("?") + 1;
        }
        var why = string.Join(", ", causes.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"));
        return new Moving(repeated, histogram, jerks, Math.Round(jerkMax, 2), Math.Round(tail, 1), Math.Round(moved), why);
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
            if (p.FirstFrameAt == 0) { p.FirstFrameAt = Stopwatch.GetTimestamp(); p.FirstRendering = args.RenderingTime; }
            // The list's place drawn by the previous frame: this handler runs before the list's own step of this frame.
            if (p.Viewer is { } viewer && p.Frames.Count > 0) p.Frames[^1].Offset = viewer.VerticalOffset;
            var (hover, images, pictures) = FrameEvents.Take();
            var frame = new Frame { At = (args.RenderingTime - p.FirstRendering).TotalMilliseconds, Hover = hover, Images = images, Pictures = pictures };
            p.Frames.Add(frame);
            if (previous != TimeSpan.MinValue) { frame.Gap = (args.RenderingTime - previous).TotalMilliseconds; p.Gaps.Add(frame.Gap); }
            // How long each presentation stood, from the phase's own first: the one before it may date from long before.
            if (presented != 0 && presented != _lastPresented)
            {
                if (p.LastPresent != 0) { frame.Present = (presented - p.LastPresent) * 1000d / Stopwatch.Frequency; p.Presents.Add(frame.Present); }
                p.LastPresent = presented;
            }
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
        if (frame) { p.Work.Add(ms); if (p.Frames.Count > 0) p.Frames[^1].Work = ms; }
        else if (ms > p.Longest) (p.Longest, p.LongestName) = (ms, Describe(operation));
        if (ms >= 8) p.Slow.Add((frame ? "frame" : Describe(operation), Elapsed(p.Started) - ms, ms));
    }
    private static string Describe(DispatcherOperation operation)
    {
        // The method the operation calls, with its class (« MediaContext.RenderMessageHandler »). A lambda says the method
        // it was written in (« MainWindow.<ScheduleMoreCheck>b__529_0 »); a helper of the compiler's or of WPF that only
        // calls a delegate given to it (an await's continuation) says that delegate's; a continuation, its async method.
        var method = OperationMethod?.GetValue(operation) as Delegate;
        if (method is null) return $"? ({operation.Priority})";
        if (Generated(method.Method.DeclaringType) && OperationArgs?.GetValue(operation) is Delegate given) method = given;
        return $"{Name(method)} ({operation.Priority})";
        static bool Generated(Type? type) => type?.Name.StartsWith('<') == true;
        static string Name(Delegate method)
        {
            // An async method's continuation runs its state machine (« <ShowDetailsAsync>d__81 » in MainWindow).
            if (method.Target?.GetType() is { IsGenericType: true } box && box.GetGenericArguments().LastOrDefault(t => Generated(t) && t.DeclaringType is not null) is { } machine)
                return $"{machine.DeclaringType!.Name}.{machine.Name}";
            var type = method.Method.DeclaringType;
            while (Generated(type) && type!.DeclaringType is not null) type = type.DeclaringType;
            return $"{type?.Name}.{method.Method.Name}";
        }
    }

    /// <summary>The phases as a table: what the report and the job's summary show.</summary>
    public static string Table(IEnumerable<Phase> phases)
    {
        var c = CultureInfo.InvariantCulture; var text = new StringBuilder();
        text.AppendLine("| Phase | s | Hz | images | montrées | rafraîchissements manqués | à l'heure | écart p95 / max (ms) | travail p95 / max (ms) | fil occupé | plus longue autre (ms) | présentées | répétées à l'écran | à-coups (max) | dernier pixel (ms) | 1re image (ms) | GC 0/1/2 | pauses GC (ms) |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var p in phases)
            text.AppendLine(string.Create(c, $"| {p.Name} | {p.Seconds:0.0} | {p.RefreshHz:0} | {p.Frames} | {p.Shown:P0} | {p.MissedRefreshes} | {p.OnTime:P1} | {p.GapP95:0.0} / {p.GapMax:0.0} | {p.WorkP95:0.0} / {p.WorkMax:0.0} | {p.Busy:P0} | {p.LongestOther:0.0} | {p.Presented} | {p.Repeated} | {p.Jerks} ({p.JerkMax:P0}) | {p.TailMs:0} | {p.FirstFrame:0} | {p.Gen0}/{p.Gen1}/{p.Gen2} | {p.GcPause:0.0} |"));
        return text.ToString();
    }

    /// <summary>One frame of a phase: when (its refresh, from the phase's first), the list's place it drew, what came with it.</summary>
    private sealed class Frame
    {
        public double At, Gap = double.NaN, Work = double.NaN, Offset = double.NaN, Present = double.NaN;
        public int Hover, Images, Pictures;
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
        public ScrollViewer? Viewer;
        public double Scale = 1;
        public TimeSpan FirstRendering;
        public long LastPresent;
        public readonly List<Frame> Frames = [];
        public readonly int Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2);
        public readonly TimeSpan Pause = GC.GetTotalPauseDuration();
    }
}
