using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Mira.Checks;
using Mira.Core;
using Mira.Mac.Views;

namespace Mira.Mac.Checks;

/// <summary>
/// How smooth Mira for Mac's pages are (--smooth-check &lt;folder&gt;, with --data), as Mira for Windows measures it:
/// signed in to the Jellyfin answered inside Mira that Windows' check uses (<see cref="StandInJellyfin"/>: 240 films, 36
/// series, their pictures as JPEG, a home network's delay), then the trackpad down and up the pages, the rail's pages
/// (first visit, then again), a title's page opened, scrolled and left, Réglages. Each phase's frames in report.txt,
/// report.json and summary.md: those of Avalonia's render loop (asked at each frame, as an animation does, so that it
/// draws at the screen's pace), their gaps, and the layout passes in them. Runs on Linux too (xvfb-run), drawn by the
/// processor: its numbers say what the interface's thread does, not how fast a Mac's graphics draw.
/// </summary>
public static class SmoothCheck
{
    public static async Task RunAsync(MainWindow window)
    {
        var args = window.Args;
        string Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        var output = Path.GetFullPath(Arg("--smooth-check") is { Length: > 0 } folder && !folder.StartsWith("--", StringComparison.Ordinal) ? folder : "mira-smooth");
        Directory.CreateDirectory(output);
        var lines = new List<string>(); var step = "start";
        var standIn = new StandInJellyfin();
        using var meter = new Meter(window);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime; var clock = Stopwatch.StartNew();
        async Task Until(string what, Func<bool> test, double seconds)
        {
            step = what;
            for (var timer = Stopwatch.StartNew(); !test(); await Task.Delay(50))
                if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"{what}: not within {seconds} s.");
        }
        string? note = null;
        async Task Measure(string name, Func<Task> action)
        {
            step = name; note = null;
            // A moment of rest between phases: what one left moving is not counted in the next.
            await Task.Delay(400);
            meter.Begin(name);
            await action();
            var phase = meter.End();
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {phase.Frames} images en {phase.Seconds:0.00} s ({phase.Shown:P0} des rafraîchissements), {phase.MissedRefreshes} manqués, écart max {phase.GapMax:0.0} ms, mises en page {phase.Layouts} ({phase.LayoutTotal:0.0} ms, max {phase.LayoutMax:0.0} ms){note}"));
        }
        // A click, timed as it holds the interface's thread: what it builds before the page can draw.
        async Task Click(Action action)
        {
            var started = Stopwatch.GetTimestamp(); action(); meter.Clicked(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await Task.Yield();
        }
        ScrollViewer Scroll() => window.Current?.Content as ScrollViewer
            ?? window.Current?.GetVisualDescendants().OfType<ScrollViewer>().Where(x => x.IsEffectivelyVisible).OrderByDescending(x => x.Extent.Height - x.Viewport.Height).FirstOrDefault()
            ?? throw new InvalidOperationException($"No list to scroll on {window.Current?.GetType().Name}.");
        async Task Swipe(int flicks, bool down)
        {
            var scroll = Scroll(); var from = scroll.Offset.Y;
            await TrackpadAsync(window, scroll, flicks, down);
            note = string.Create(CultureInfo.InvariantCulture, $" ; défilé de {from:0} à {scroll.Offset.Y:0} px (sur {Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height):0})");
        }
        int Cards() => window.Current?.GetVisualDescendants().OfType<Button>().Count(x => x.Classes.Contains("card")) ?? 0;
        try
        {
            // Never the person's own profile: its cache and its pages would take in a made-up library.
            if (Array.IndexOf(args, "--data") < 0) throw new InvalidOperationException("--smooth-check needs --data <folder>, a profile of its own.");
            window.WindowState = WindowState.Normal; window.Width = 1470; window.Height = 920;
            step = "sign in";
            await window.ActivateAsync(new Connection(StandInJellyfin.Server, StandInJellyfin.UserId, StandInJellyfin.UserName, StandInJellyfin.Token, window.Profile.DeviceId), standIn);
            await Until("Accueil loaded", () => window.Current is HomePage && Cards() > 10, 40);
            await Task.Delay(2500);

            await Measure("Accueil : au repos", () => Task.Delay(1500));
            await Measure("Accueil : trackpad vers le bas", () => Swipe(4, down: true));
            await Measure("Accueil : trackpad vers le haut", () => Swipe(4, down: false));
            await Measure("Films : ouverture (1re fois)", async () => { await Click(() => window.Go("Movie")); await Task.Delay(1200); });
            await Until("films on Films", () => window.Current is BrowsePage { Items.Count: > 0 } && Cards() > 10, 20);
            await Measure("Films : trackpad rapide vers le bas", () => Swipe(8, down: true));
            await Measure("Films : trackpad vers le haut", () => Swipe(3, down: false));
            await Measure("Séries : ouverture (1re fois)", async () => { await Click(() => window.Go("Series")); await Task.Delay(1200); });
            await Measure("Séries : trackpad vers le bas", () => Swipe(4, down: true));
            await Measure("Films : ouverture (2e fois)", async () => { await Click(() => window.Go("Movie")); await Task.Delay(1000); });
            await Until("a film on Films", () => window.Current is BrowsePage { Items.Count: > 0 }, 10);
            var film = ((BrowsePage)window.Current!).Items[0];
            await Measure("Fiche : ouverture", async () => { await Click(() => window.Open(film)); await Task.Delay(1200); });
            await Measure("Fiche : trackpad", () => Swipe(3, down: true));
            await Measure("Fiche : retour", async () => { await Click(() => window.Back()); await Task.Delay(800); });
            await Measure("Réglages : ouverture", async () => { await Click(() => window.Go("settings")); await Task.Delay(900); });
            await Measure("Accueil : retour", async () => { await Click(() => window.Go("home")); await Task.Delay(1000); });
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex.GetType().Name + ": " + ex.Message + $" (step: {step})\n" + ex.StackTrace);
            Environment.ExitCode = 1;
        }
        finally
        {
            var processor = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds / Math.Max(1e-9, clock.Elapsed.TotalSeconds);
            var c = CultureInfo.InvariantCulture;
            var header = new List<string>
            {
                $"Mira {JellyfinClient.AppVersion} pour Mac, {RuntimeInformationText()}, {Environment.ProcessorCount} processeurs logiques",
                string.Create(c, $"Fenêtre {window.Bounds.Width:0} × {window.Bounds.Height:0} à {window.RenderScaling:P0} ; rendu : {meter.Sources}"),
                string.Create(c, $"Rythme de l’écran estimé : {meter.Hz:0} Hz ; Mira : {processor:0.00} cœur"),
                $"Jellyfin de mesure : {standIn.Requests} requêtes, {standIn.Pictures} images" + (standIn.Unknown.IsEmpty ? "" : " ; inconnues : " + string.Join(", ", standIn.Unknown.Select(x => $"{x.Key} ×{x.Value}")))
            };
            var table = Meter.Table(meter.Phases);
            var text = new StringBuilder().AppendJoin('\n', header).Append("\n\n").Append(table).Append('\n').AppendJoin('\n', lines).Append('\n');
            await File.WriteAllTextAsync(Path.Combine(output, "report.txt"), text.ToString());
            await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), string.Join("  \n", header) + "\n\n" + table + (Environment.ExitCode != 0 ? "\n" + lines.Last() + "\n" : ""));
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { header, phases = meter.Phases, failure = Environment.ExitCode != 0 ? lines.Last() : null }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(text.ToString());
            window.Close();
        }
    }
    private static string RuntimeInformationText() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    /// <summary>
    /// Two fingers on a trackpad, as macOS sends them to Avalonia: a flick's events every 8 ms, each a few points (a
    /// precise delta, a fiftieth of a point per unit), slowing down as momentum does; then until the list stands still.
    /// </summary>
    private static async Task TrackpadAsync(MainWindow window, ScrollViewer scroll, int flicks, bool down)
    {
        var target = scroll.Content as Control ?? scroll;
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var at = new Point(target.Bounds.Width / 2, Math.Min(400, target.Bounds.Height / 2));
        for (var flick = 0; flick < flicks; flick++)
        {
            for (double speed = 46; speed > 1.5; speed *= .93)
            {
                target.RaiseEvent(new PointerWheelEventArgs(target, pointer, window, at, (ulong)Environment.TickCount64, PointerPointProperties.None, KeyModifiers.None, new Vector(0, (down ? -speed : speed) / 50)));
                await Task.Delay(8);
            }
            await Task.Delay(60);
        }
        var last = scroll.Offset.Y; var still = Stopwatch.StartNew();
        for (var total = Stopwatch.StartNew(); still.ElapsedMilliseconds < 300 && total.ElapsedMilliseconds < 6000; await Task.Delay(30))
            if (Math.Abs(scroll.Offset.Y - last) > .01) { last = scroll.Offset.Y; still.Restart(); }
    }

    /// <summary>
    /// The frames of the phases: one per call of Avalonia's render loop (<see cref="TopLevel.RequestAnimationFrame"/>,
    /// asked again at each, which keeps it drawing at the screen's pace), its gap to the one before (two refreshes apart:
    /// one shown twice, a stutter), and the layout passes Avalonia timed meanwhile (its LayoutManager says how long each
    /// took, through a property it keeps for its own diagnostics: read by reflection).
    /// </summary>
    private sealed class Meter : IDisposable
    {
        public sealed record Phase(string Name, double Seconds, double RefreshHz, int Frames, double Shown, int MissedRefreshes, double OnTime,
            double GapP50, double GapP95, double GapMax, int Layouts, double LayoutTotal, double LayoutMax, double Click, int Gen0, int Gen1, int Gen2, double GcPause, double From, double To);
        private readonly TopLevel _top;
        private readonly object? _layoutManager;
        private readonly PropertyInfo? _layoutTimed;
        private readonly List<double> _rest = [];
        private Recording? _phase;
        private long _last;
        private bool _running = true;
        public List<Phase> Phases { get; } = [];
        public string Sources { get; }
        /// <summary>The screen's pace, from the gaps of frames at rest (60 until measured).</summary>
        public double Hz { get; private set; } = 60;

        public Meter(TopLevel top)
        {
            _top = top;
            _layoutManager = typeof(TopLevel).GetProperty("LayoutManager", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(top);
            _layoutTimed = _layoutManager?.GetType().GetProperty("LayoutPassTimed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var layouts = "non mesurées";
            try
            {
                if (_layoutTimed?.PropertyType is { IsGenericType: true } handler && handler.GetGenericArguments()[0] is var timing)
                {
                    var pass = Expression.Parameter(timing);
                    var call = Expression.Call(Expression.Constant(this), typeof(Meter).GetMethod(nameof(Layout), BindingFlags.Instance | BindingFlags.NonPublic)!, Expression.Property(pass, "Elapsed"));
                    _layoutTimed.SetValue(_layoutManager, Expression.Lambda(handler, call, pass).Compile());
                    layouts = "mesurées";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TargetInvocationException or MemberAccessException) { layouts = "illisibles"; }
            var compositor = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(top)?.Compositor;
            Sources = $"mises en page {layouts}, compositeur {(compositor is null ? "?" : compositor.GetType().Name)}";
            _last = Stopwatch.GetTimestamp();
            _top.RequestAnimationFrame(Frame);
        }
        public void Dispose()
        {
            _running = false;
            try { _layoutTimed?.SetValue(_layoutManager, null); } catch (TargetInvocationException) { }
        }
        public void Begin(string name) { _phase = new Recording(name); }
        public void Clicked(double ms) { if (_phase is { } p) p.Click += ms; }
        public Phase End()
        {
            var p = _phase ?? throw new InvalidOperationException("No phase under way.");
            _phase = null;
            var seconds = Stopwatch.GetElapsedTime(p.Started).TotalSeconds;
            if (p.Name.EndsWith("au repos", StringComparison.Ordinal) && p.Gaps.Count > 10) Hz = Math.Round(1000 / Percentile(p.Gaps, .5));
            var period = 1000 / Hz;
            var missed = (int)p.Gaps.Sum(g => Math.Max(0, Math.Round(g / period) - 1));
            var onTime = p.Gaps.Count == 0 ? 1 : p.Gaps.Count(g => g <= period * 1.5) / (double)p.Gaps.Count;
            var phase = new Phase(p.Name, Math.Round(seconds, 3), Hz, p.Gaps.Count, Math.Round(p.Gaps.Count / Math.Max(1e-9, seconds * Hz), 3), missed, Math.Round(onTime, 4),
                Percentile(p.Gaps, .5), Percentile(p.Gaps, .95), Percentile(p.Gaps, 1), p.Layouts.Count, Math.Round(p.Layouts.Sum(), 2), Percentile(p.Layouts, 1), Math.Round(p.Click, 2),
                GC.CollectionCount(0) - p.Gen0, GC.CollectionCount(1) - p.Gen1, GC.CollectionCount(2) - p.Gen2, Math.Round((GC.GetTotalPauseDuration() - p.Paused).TotalMilliseconds, 1),
                Since(p.Started), Since(Stopwatch.GetTimestamp()));
            Phases.Add(phase);
            return phase;
        }
        private void Frame(TimeSpan _)
        {
            var now = Stopwatch.GetTimestamp();
            _phase?.Gaps.Add((now - _last) * 1000d / Stopwatch.Frequency);
            _last = now;
            if (_running) _top.RequestAnimationFrame(Frame);
        }
        private void Layout(TimeSpan elapsed) => _phase?.Layouts.Add(elapsed.TotalMilliseconds);
        /// <summary>Seconds since Mira started: where a phase lies in a trace of the process (dotnet-trace).</summary>
        private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        private static double Since(long timestamp) => Math.Round((DateTime.UtcNow - Stopwatch.GetElapsedTime(timestamp) - ProcessStart).TotalSeconds, 3);
        private static double Percentile(List<double> values, double rank)
        {
            if (values.Count == 0) return 0;
            var sorted = values.Order().ToList();
            return Math.Round(sorted[Math.Clamp((int)Math.Ceiling(rank * sorted.Count) - 1, 0, sorted.Count - 1)], 2);
        }
        public static string Table(IEnumerable<Phase> phases)
        {
            var c = CultureInfo.InvariantCulture; var text = new StringBuilder();
            text.AppendLine("| Phase | s | Hz | images | montrées | rafraîchissements manqués | à l'heure | écart p50 / p95 / max (ms) | mises en page | dont (ms) total / max | clic (ms) | GC 0/1/2 (pauses ms) |");
            text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var p in phases)
                text.AppendLine(string.Create(c, $"| {p.Name} | {p.Seconds:0.0} | {p.RefreshHz:0} | {p.Frames} | {p.Shown:P0} | {p.MissedRefreshes} | {p.OnTime:P1} | {p.GapP50:0.0} / {p.GapP95:0.0} / {p.GapMax:0.0} | {p.Layouts} | {p.LayoutTotal:0.0} / {p.LayoutMax:0.0} | {p.Click:0.0} | {p.Gen0}/{p.Gen1}/{p.Gen2} ({p.GcPause:0.0}) |"));
            return text.ToString();
        }
        private sealed class Recording(string name)
        {
            public readonly string Name = name;
            public readonly long Started = Stopwatch.GetTimestamp();
            public readonly List<double> Gaps = [], Layouts = [];
            public double Click;
            public readonly int Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2);
            public readonly TimeSpan Paused = GC.GetTotalPauseDuration();
        }
    }
}
