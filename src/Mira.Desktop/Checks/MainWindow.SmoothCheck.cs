using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Checks;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// How smooth Mira's pages are (--smooth-check &lt;folder&gt;, with --data): Mira signed in to a Jellyfin answered inside it
    /// (<see cref="StandInJellyfin"/>: 240 films, 36 series, their pictures as JPEG, a home network's delay), its window at
    /// the size of the primary screen's work area (as maximized), then the wheel down and up the pages, the rail's pages one
    /// after the other (first visit, then again), a title's page opened, scrolled and closed, Réglages. Each phase's frames
    /// (<see cref="FrameMeter"/>) in report.txt, report.json and summary.md, captures beside them. The window stays out of
    /// sight: off screen (default), or with --smooth-placement edge its first pixel on the screen's last one, under
    /// everything (Windows draws a window wholly off screen? that is what the edge measures against), never activated.
    /// </summary>
    private async Task RunSmoothCheckAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--smooth-check") + 1]); Directory.CreateDirectory(output);
        var placement = _args.SkipWhile(x => x != "--smooth-placement").Skip(1).FirstOrDefault() ?? "offscreen";
        var lines = new List<string>(); var step = "start";
        var standIn = new StandInJellyfin();
        using var meter = new FrameMeter(Dispatcher);
        var system = SystemTimes(); var cpu = Process.GetCurrentProcess().TotalProcessorTime; var clock = Stopwatch.StartNew();
        string where = "";
        async Task Until(string what, Func<bool> test, double seconds)
        {
            step = what;
            for (var timer = Stopwatch.StartNew(); !test(); await Task.Delay(50))
                if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"{what}: not within {seconds} s.");
        }
        string? note = null;
        async Task Measure(string name, Func<Task> action, ScrollViewer? viewer = null)
        {
            step = name; note = null;
            // A moment of rest between phases: what one left moving is not counted in the next.
            await Task.Delay(400);
            meter.Begin(name, viewer);
            await action();
            var phase = meter.End();
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {phase.Frames} images en {phase.Seconds:0.00} s ({phase.Shown:P0} des rafraîchissements), {phase.MissedRefreshes} manqués, écart max {phase.GapMax:0.0} ms, travail max {phase.WorkMax:0.0} ms, présentations : {(phase.Results.Length > 0 ? phase.Results : "?")}{note}"));
            if (viewer is not null)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"  mouvement : {phase.Moved:0} px d’écran, {phase.Repeated} rafraîchissements répétés (présentations restées {(phase.PresentGaps.Length > 0 ? phase.PresentGaps : "?")}), {phase.Jerks} à-coups de vitesse (le plus fort {phase.JerkMax:P0}), dernier pixel en {phase.TailMs:0} ms ; images en retard avec : {(phase.LateCauses.Length > 0 ? phase.LateCauses : "aucune")}"));
        }
        // The wheel, and how far the list moved: a list that did not move measured nothing.
        async Task Wheel(ScrollViewer viewer, int notches, int spacing, bool down)
        {
            var from = viewer.VerticalOffset;
            await WheelAsync(viewer, notches, spacing, down);
            note = string.Create(CultureInfo.InvariantCulture, $" ; défilé de {from:0} à {viewer.VerticalOffset:0} px (sur {viewer.ScrollableHeight:0})");
        }
        async Task Shot(string name)
        {
            await Task.Delay(300);
            var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * .5), (int)Math.Ceiling(visual.ActualHeight * .5), 48, 48, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var context = backdrop.RenderOpen()) context.DrawRectangle(Background, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
            bitmap.Render(backdrop); bitmap.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".jpg")); encoder.Save(file);
        }
        try
        {
            // Never the user's own profile: its cache and its pages would take in a made-up library.
            if (Array.IndexOf(_args, "--data") < 0) throw new InvalidOperationException("--smooth-check needs --data <folder>, a profile of its own.");
            KeepFullSpeed();
            where = PlaceForMeasure(placement);
            step = "sign in";
            ServerHandler = standIn;
            await ActivateConnectionAsync(new Connection(StandInJellyfin.Server, StandInJellyfin.UserId, StandInJellyfin.UserName, StandInJellyfin.Token, _profile.DeviceId));
            await Until("Accueil loaded", () => !_catalog.Loading && PosterCards.Children.Count > 0 && ResumeCards.Children.Count > 0, 40);
            await ArtworkAsync(); await Shot("accueil");

            await Measure("Accueil : au repos", () => Task.Delay(1500));
            await Measure("Accueil : molette vers le bas", () => Wheel(LibraryScroll, 10, 75, down: true), LibraryScroll);
            await Measure("Accueil : molette vers le haut", () => Wheel(LibraryScroll, 10, 75, down: false), LibraryScroll);
            await Measure("Films : ouverture (1re fois)", async () => { await GoToPageAsync("Movie"); await Task.Delay(1200); });
            await Shot("films");
            await Measure("Films : molette rapide vers le bas", () => Wheel(LibraryScroll, 30, 45, down: true), LibraryScroll);
            await Measure("Films : molette lente vers le haut", () => Wheel(LibraryScroll, 14, 110, down: false), LibraryScroll);
            await Measure("Films : retour en haut", async () => { SmoothScroll.ToTop(LibraryScroll); await SettleAsync(LibraryScroll); }, LibraryScroll);
            // The pointer resting on the grid as the wheel turns, as a hand leaves it: the cards go by under it.
            _hoverPointerForCheck = new Point(LibraryShell.ActualWidth / 2, LibraryShell.ActualHeight / 2);
            await Measure("Films : molette, pointeur sur la grille", () => Wheel(LibraryScroll, 30, 45, down: true), LibraryScroll);
            await Measure("Films : molette lente, pointeur sur la grille", () => Wheel(LibraryScroll, 14, 110, down: false), LibraryScroll);
            _hoverPointerForCheck = null; ClosePreview();
            // A wheel that turns freely (high resolution): a twentieth of a notch every 8 ms.
            await Measure("Films : molette fine", () => FineWheel(LibraryScroll, 90, 8, down: true), LibraryScroll);
            SmoothScroll.Jump(LibraryScroll);
            await Measure("Séries : ouverture (1re fois)", async () => { await GoToPageAsync("Series"); await Task.Delay(1200); });
            await Measure("Séries : molette vers le bas", () => Wheel(LibraryScroll, 14, 60, down: true), LibraryScroll);
            await Measure("Films : ouverture (2e fois)", async () => { await GoToPageAsync("Movie"); await Task.Delay(1000); });
            await Until("a film on Films", () => PosterCards.Children.OfType<MediaCard>().Any(), 10);
            var film = PosterCards.Children.OfType<MediaCard>().First().Item;
            await Measure("Fiche : ouverture", async () => { await ShowDetailsAsync(film); await Task.Delay(1200); });
            await Shot("fiche");
            await Measure("Fiche : molette", () => Wheel(DetailScroll, 12, 70, down: true), DetailScroll);
            await Measure("Fiche : fermeture", async () => { await CloseDetailsAsync(); await Task.Delay(500); });
            await Measure("Réglages : ouverture", async () => { Settings_Click(SettingsNav, new RoutedEventArgs()); await Task.Delay(900); });
            await Shot("reglages");
            // The section with the most to scroll at this size (Lecture fits on a large screen: nothing would move).
            var longest = "playback"; var most = -1d;
            foreach (var tab in new[] { "playback", "subtitles", "appearance", "server", "updates" })
            { SelectSettingsTab(tab, animate: false); SettingsScroll.UpdateLayout(); if (SettingsScroll.ScrollableHeight > most) (longest, most) = (tab, SettingsScroll.ScrollableHeight); }
            SelectSettingsTab(longest, animate: false); await Task.Delay(400);
            await Measure("Réglages : molette", async () => { await Wheel(SettingsScroll, 10, 70, down: true); note += $" ; section {longest}"; }, SettingsScroll);
            await Measure("Réglages : fermeture", async () => { await CloseSettingsAnimatedAsync(); await Task.Delay(500); });
            await Measure("Accueil : retour", async () => { await GoToPageAsync("home"); await Task.Delay(1000); });
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex.GetType().Name + ": " + ex.Message + $" (step: {step})\n" + ex.StackTrace);
            Environment.ExitCode = 1;
        }
        finally
        {
            var busy = SystemTimes() is { } after && system is { } before && after.Total - before.Total > 0 ? 1 - (after.Idle - before.Idle) / (double)(after.Total - before.Total) : -1;
            var processor = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds / Math.Max(1e-9, clock.Elapsed.TotalSeconds);
            var c = CultureInfo.InvariantCulture;
            var header = new List<string>
            {
                $"Mira {JellyfinClient.AppVersion}, {Environment.OSVersion.VersionString}, {Environment.ProcessorCount} processeurs logiques, rendu de niveau {RenderCapability.Tier >> 16}",
                where,
                string.Create(c, $"Taux de l’écran lu par WPF : {(meter.Phases.Count > 0 ? meter.Phases.Max(x => x.RefreshHz) : meter.RefreshRate):0} Hz ; {meter.Sources}"),
                string.Create(c, $"Processeur de tout le PC pendant la mesure : {(busy < 0 ? "?" : busy.ToString("P0", c))} ; Mira : {processor:0.00} cœur"),
                $"Jellyfin de mesure : {standIn.Requests} requêtes, {standIn.Pictures} images" + (standIn.Unknown.IsEmpty ? "" : " ; inconnues : " + string.Join(", ", standIn.Unknown.Select(x => $"{x.Key} ×{x.Value}")))
            };
            var table = FrameMeter.Table(meter.Phases);
            var text = new StringBuilder().AppendJoin('\n', header).Append("\n\n").Append(table).Append('\n').AppendJoin('\n', lines).Append("\n\n");
            foreach (var phase in meter.Phases.Where(x => x.Slow.Length > 0)) text.Append(phase.Name).Append(" — lents : ").AppendJoin("; ", phase.Slow).Append('\n');
            await File.WriteAllTextAsync(Path.Combine(output, "report.txt"), text.ToString());
            await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), string.Join("  \n", header) + "\n\n" + table + (Environment.ExitCode != 0 ? "\n" + lines.Last() + "\n" : ""));
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { header, phases = meter.Phases, failure = Environment.ExitCode != 0 ? lines.Last() : null }, new JsonSerializerOptions { WriteIndented = true }));
            meter.WriteTraces(output);
            Close();
        }
    }

    /// <summary>
    /// The wheel as a hand spins it: <paramref name="notches"/> notches <paramref name="spacing"/> ms apart, on the page's
    /// content (the wheel handler finds which list it moves), then until the list stands still.
    /// </summary>
    private async Task WheelAsync(ScrollViewer viewer, int notches, int spacing, bool down)
    {
        var target = viewer.Content as UIElement ?? viewer;
        for (var i = 0; i < notches; i++)
        {
            target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, down ? -120 : 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Task.Delay(spacing);
        }
        await SettleAsync(viewer);
    }
    /// <summary>
    /// A wheel without notches (high resolution, as some mice give): <paramref name="events"/> wheel messages of a
    /// twentieth of a notch each, <paramref name="spacing"/> ms apart, then until the list stands still.
    /// </summary>
    private async Task FineWheel(ScrollViewer viewer, int events, int spacing, bool down)
    {
        var target = viewer.Content as UIElement ?? viewer;
        for (var i = 0; i < events; i++)
        {
            target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, down ? -6 : 6) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Task.Delay(spacing);
        }
        await SettleAsync(viewer);
    }
    /// <summary>Until the list has not moved for 300 ms (6 s at most).</summary>
    private static async Task SettleAsync(ScrollViewer viewer)
    {
        var last = viewer.VerticalOffset; var still = Stopwatch.StartNew();
        for (var total = Stopwatch.StartNew(); still.ElapsedMilliseconds < 300 && total.ElapsedMilliseconds < 6000; await Task.Delay(30))
            if (Math.Abs(viewer.VerticalOffset - last) > .01) { last = viewer.VerticalOffset; still.Restart(); }
    }

    /// <summary>
    /// The window at the size the primary screen's work area gives a maximized one (3840 × 2112 on a 4K screen with the
    /// taskbar), never activated, never in the taskbar: off screen, or « edge », its top-left pixel on the screen's
    /// bottom-right one, under every other window (and the taskbar).
    /// </summary>
    private string PlaceForMeasure(string placement)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromPoint(new Point32(), 1);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("The primary screen cannot be read.");
        var (width, height) = (info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var (x, y) = placement == "edge" ? (info.Monitor.Right - 1, info.Monitor.Bottom - 1) : (-30000, -30000);
        if (placement is not ("edge" or "offscreen")) throw new InvalidOperationException($"--smooth-placement: edge or offscreen, not {placement}.");
        // Moved first, under every window, then sized: reaching a screen of another scale, WPF resizes it for that scale.
        if (!SetWindowPos(handle, new IntPtr(1), x, y, 0, 0, 0x0001 | 0x0010 | 0x0200) || !SetWindowPos(handle, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010 | 0x0200))
            throw new InvalidOperationException($"SetWindowPos failed ({Marshal.GetLastWin32Error()}).");
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = $"{info.Monitor.Right - info.Monitor.Left} × {info.Monitor.Bottom - info.Monitor.Top}";
        return string.Create(CultureInfo.InvariantCulture, $"Écran principal {screen} px, fenêtre {width} × {height} px à {dpi.DpiScaleX:P0} ({ActualWidth:0} × {ActualHeight:0}), placée « {placement} »");
    }
    /// <summary>Out of sight, Windows would slow this process down (EcoQoS, coarser timers): not while it measures.</summary>
    private static void KeepFullSpeed()
    {
        var state = new PowerThrottling { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        SetProcessInformation(Process.GetCurrentProcess().Handle, 4, ref state, Marshal.SizeOf<PowerThrottling>());
    }
    private static (long Idle, long Total)? SystemTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel + user) : null;

    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)] private struct Point32 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect32 Monitor, Work; public uint Flags; }
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottling information, int size);
    [DllImport("kernel32")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("user32")] private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
