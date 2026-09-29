using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Core;
using Mira.Desktop.Playback;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// Player fixture additions for 0.4.4: icons, shortcuts, timeline, passages, idle pause and the mini-player.
public partial class MainWindow
{
    private async Task RunPlayerV044ChecksAsync(string output, List<string> checks, Action<bool, string> require)
    {
        var mpv = _mpv!;
        // Windows only plays its open, close and minimize animations for windows with a caption style;
        // the custom chrome must still cover that caption completely.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var outer); GetClientRect(hwnd, out var inner);
        require((GetWindowLong(hwnd, -16) & 0x00C00000) == 0x00C00000 && inner.Right - inner.Left == outer.Right - outer.Left && inner.Bottom - inner.Top == outer.Bottom - outer.Top,
            "window keeps the caption style for Windows animations, with no native title bar visible");
        foreach (var button in new Button[] { PlayerBackButton, PauseButton, MuteButton, PlayerMoreButton, PlayerTracksButton, MiniPlayerButton, FullscreenButton })
        {
            var icon = FindVisual<Icon>(button);
            if (icon is null || LayoutInformation.GetLayoutClip(icon) is not null) throw new InvalidOperationException($"{button.Name} icon is cropped");
            var centre = icon.TranslatePoint(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2), button);
            if (Math.Abs(centre.X - button.ActualWidth / 2) > .75 || Math.Abs(centre.Y - button.ActualHeight / 2) > .75) throw new InvalidOperationException($"{button.Name} icon is off-centre: {centre}");
        }
        checks.Add("PASS player icons are laid out uncropped and centred in their buttons");
        if (_args.Contains("--perf-probe"))
        {
            mpv.Set("pause", "yes"); ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30); await Task.Delay(400);
            var probe = new Border { Width = 40, Height = 40, Background = Brushes.Gray, Opacity = .5 };
            ((Panel)Root).Children.Add(probe);
            var mainTarget = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)).CompositionTarget;
            var overlayTarget = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(PlayerSurface)).CompositionTarget;
            checks.Add($"INFO render tier {RenderCapability.Tier >> 16}; main {mainTarget.RenderMode}; overlay {overlayTarget.RenderMode}; overlay layered {((GetWindowLong(PlayerOverlayLayer.Handle, -20) & 0x80000) != 0)}");
            checks.Add($"INFO main window animation {await MeasureFpsAsync(probe)}");
            ((Panel)Root).Children.Remove(probe);
            checks.Add($"INFO overlay animation {PlayerSurface.ActualWidth:0}x{PlayerSurface.ActualHeight:0} DIP: {await MeasureFpsAsync((UIElement)PauseButton.Template.FindName("Hover", PauseButton))}");
            // Mouse moves arrive at 125–1000 Hz; each one calls ShowPlayerControls.
            var moves = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(4) };
            var moved = 0; moves.Tick += (_, _) => { ++moved; ShowPlayerControls(); }; moves.Start();
            var withMoves = await MeasureFpsAsync((UIElement)PauseButton.Template.FindName("Hover", PauseButton));
            moves.Stop(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30);
            checks.Add($"INFO overlay animation while the pointer moves ({moved} moves): {withMoves}");
            var sw = System.Diagnostics.Stopwatch.StartNew(); for (var i = 0; i < 200; i++) mpv.Number("time-pos"); var poll = sw.Elapsed.TotalMilliseconds / 200;
            mpv.Set("pause", "no"); await Task.Delay(300);
            var worst = 0d; sw.Restart(); for (var i = 0; i < 200; i++) { var one = System.Diagnostics.Stopwatch.StartNew(); mpv.Number("time-pos"); worst = Math.Max(worst, one.Elapsed.TotalMilliseconds); await Task.Yield(); }
            mpv.Set("pause", "yes");
            checks.Add($"INFO mpv synchronous property read: paused {poll:0.000} ms, playing worst {worst:0.00} ms");
        }

        mpv.Set("pause", "no"); await Task.Delay(350);
        var first = SeekBar.DisplayedPosition; await Task.Delay(45); var second = SeekBar.DisplayedPosition;
        require(second > first && Math.Abs(second - mpv.Number("time-pos")) < .4, $"timeline advances between mpv polls ({first:0.000} -> {second:0.000} s)");

        ShowPlayerControls(); await Task.Delay(60);
        var before = mpv.Number("time-pos"); var wasPaused = mpv.Flag("pause");
        var space = Press(PlayerTracksButton, Key.Space); await Task.Delay(150); mpv.Poll();
        require(space && mpv.Flag("pause") != wasPaused && Math.Abs(mpv.Number("time-pos") - before) < 2 && !PlayerOptionsPopup.IsOpen, "Space toggles pause even when the event targets the subtitles button");
        mpv.Set("pause", "yes"); await Task.Delay(120); mpv.Poll();
        var start = mpv.Number("time-pos"); _position = start;
        var right = Press(PauseButton, Key.Right); await Task.Delay(400); mpv.Poll();
        var expected = Math.Min(start + 10, _duration - .5);
        require(right && Math.Abs(mpv.Number("time-pos") - expected) < .6, $"Right arrow seeks 10 s from any control ({start:0.0} -> {mpv.Number("time-pos"):0.0} s)");

        ShowPlayerControls(); _paused = true; _lastPlayerInteraction = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5); UpdatePlayerControls();
        require(!_controlsVisible && PlayerSurface.Cursor == Cursors.None, "paused controls and cursor leave after idling, for clean screenshots");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) require(PlayerOverlayLayer.CaptureExcluded, "player controls are excluded from screen captures");
        else checks.Add("INFO capture exclusion needs Windows 10 2004 or later");
        ShowPlayerControls(); await Task.Delay(250);

        if (SeekBar.Chapters.Count > 0)
        {
            checks.Add("INFO chapters " + string.Join(", ", SeekBar.Chapters.Select(c => $"{c.Start:0.#} s {c.Title}")) + "; passages " + string.Join(", ", _segments.Select(s => $"{s.Kind} {s.Start:0.#}-{s.End:0.#} s")));
            require(_segments.Any(s => s.Kind == SkipKind.Intro) && _segments.Any(s => s.Kind == SkipKind.Outro), "named file chapters become opening and ending passages");
            var intro = _segments.First(s => s.Kind == SkipKind.Intro);
            TryCommand("seek", (intro.Start + 1).ToString(CultureInfo.InvariantCulture), "absolute+exact"); HoldPosition(intro.Start + 1);
            await Task.Delay(350); ShowPlayerControls(); UpdateSkip(); await Task.Delay(300);
            require(_skipShown && SkipButton.IsVisible && SkipText.Text.StartsWith("Passer", StringComparison.Ordinal), $"skip button offers « {SkipText.Text} » during the opening");
            CaptureElement(output, "player-controls", PlayerSurface);
            Skip_Click(this, new()); await Task.Delay(450); mpv.Poll();
            require(Math.Abs(mpv.Number("time-pos") - intro.End) < .6 && !_skipShown, $"skip jumps to the end of the opening ({mpv.Number("time-pos"):0.0} s)");
            // Credits with a following episode: a countdown that the viewer can cancel.
            var outro = _segments.First(s => s.Kind == SkipKind.Outro);
            var autoNext = _settings.AutoNext; _settings.AutoNext = true; _nextItem = DemoLibrary.Items()[1]; _activeSkip = null;
            TryCommand("seek", (outro.Start + .5).ToString(CultureInfo.InvariantCulture), "absolute+exact"); HoldPosition(outro.Start + .5);
            await Task.Delay(350); ShowPlayerControls(); UpdateSkip(); await Task.Delay(260);
            require(_skipShown && SkipText.Text.StartsWith("Épisode suivant dans", StringComparison.Ordinal) && SkipCancel.IsVisible, $"credits offer « {SkipText.Text} » with a cancel button");
            SkipCancel_Click(this, new()); UpdateSkip(); await Task.Delay(200);
            require(_autoNextCancelled && SkipText.Text == "Épisode suivant" && _countdownEnd is null, "cancelling keeps the episode and stops the countdown");
            _settings.AutoNext = autoNext; _nextItem = null; _activeSkip = null; _autoNextCancelled = false;
            TryCommand("seek", "2", "absolute+exact"); HoldPosition(2); await Task.Delay(300); UpdateSkip();
        }
        else { checks.Add("INFO test clip has no chapters: passages not exercised"); CaptureElement(output, "player-controls", PlayerSurface); }
    }
    private async Task RunMiniPlayerChecksAsync(string output, List<string> checks, Action<bool, string> require, MpvEngine? engine)
    {
        var corner = MiniCorner(true, true);
        require(Near(PlayerShell.Margin, corner) && MiniFrame.IsVisible && PlayerShell.Clip is not null && MiniOutline.IsVisible, "mini-player rests rounded and shadowed in the bottom-right corner");
        CaptureElement(output, "mini-player", PlayerSurface);
        if (_args.Contains("--perf-probe")) { ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30); await Task.Delay(300); }
        if (_args.Contains("--perf-probe")) checks.Add($"INFO mini overlay animation {PlayerSurface.ActualWidth:0}x{PlayerSurface.ActualHeight:0} DIP: {await MeasureFpsAsync((UIElement)PauseButton.Template.FindName("Hover", PauseButton))}");
        var bounds = MiniBounds();
        PlaceMini(new Point(bounds.Left + 30, bounds.Top + 20)); _miniTrail.Clear(); _miniDragging = true; EndMiniDrag(); await Task.Delay(500);
        require(!_miniRight && !_miniBottom && Near(PlayerShell.Margin, MiniCorner(false, false)) && ReferenceEquals(engine, _mpv), "a released mini-player glides to the nearest corner");
        PlaceMini(new Point(Root.ActualWidth - MiniWidth * .5, bounds.Top + 20)); _miniTrail.Clear(); _miniDragging = true; EndMiniDrag(); await Task.Delay(500);
        require(_miniStowed && MiniTab.IsVisible && PlayerShell.Margin.Left >= Root.ActualWidth && !PlayerOverlayLayer.IsOpen && ReferenceEquals(engine, _mpv), "pushed past the right edge, the mini-player tucks away behind a handle and keeps playing");
        ShowPlayerControls();
        require(!PlayerOverlayLayer.IsOpen, "a tucked mini-player keeps its controls closed");
        UnstowMini(); await Task.Delay(500);
        require(!_miniStowed && _miniRight && Near(PlayerShell.Margin, MiniCorner(true, false)) && PlayerOverlayLayer.IsOpen && !MiniTab.IsVisible, "the handle brings the mini-player back to the right edge");
        _miniBottom = true; KeepMiniInPlace(); await Task.Delay(100);
        checks.Add("INFO mini-player drag, snapping and tuck-away checked with the native video kept alive");
    }
    [System.Runtime.InteropServices.DllImport("user32")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    private bool HasCaptionStyle() => (GetWindowLong(new System.Windows.Interop.WindowInteropHelper(this).Handle, -16) & 0x00C00000) == 0x00C00000;
    /// <summary>Distinct composition frames per second while a small element animates continuously.</summary>
    private static async Task<string> MeasureFpsAsync(UIElement animated)
    {
        var frames = new HashSet<TimeSpan>();
        EventHandler tick = (_, e) => frames.Add(((RenderingEventArgs)e).RenderingTime);
        CompositionTarget.Rendering += tick;
        animated.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(.1, .9, TimeSpan.FromMilliseconds(250)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        await Task.Delay(300); frames.Clear();
        var process = System.Diagnostics.Process.GetCurrentProcess(); process.Refresh(); var cpu = process.TotalProcessorTime;
        await Task.Delay(1500);
        process.Refresh(); var used = (process.TotalProcessorTime - cpu).TotalMilliseconds / 1500 * 100;
        var fps = frames.Count / 1.5;
        CompositionTarget.Rendering -= tick; animated.BeginAnimation(UIElement.OpacityProperty, null);
        return $"{fps:0} fps, CPU {used:0} % d’un cœur";
    }
    private static bool Near(Thickness margin, Point point) => Math.Abs(margin.Left - point.X) < 1 && Math.Abs(margin.Top - point.Y) < 1;
    private static bool Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("The control is not displayed.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
    /// <summary>Renders overlay controls for review, over a neutral grey or over a video frame letterboxed like mpv
    /// does (the native video is not part of WPF).</summary>
    private static void CaptureElement(string directory, string name, FrameworkElement element, BitmapSource? background = null)
    {
        var dpi = VisualTreeHelper.GetDpi(element); var size = new Size(element.ActualWidth, element.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background is null ? new SolidColorBrush(Color.FromRgb(62, 66, 74)) : Brushes.Black, null, new Rect(size));
            if (background is { PixelWidth: > 0, PixelHeight: > 0 })
            {
                var scale = Math.Min(size.Width / background.PixelWidth, size.Height / background.PixelHeight);
                var frame = new Size(background.PixelWidth * scale, background.PixelHeight * scale);
                dc.DrawImage(background, new Rect(new Point((size.Width - frame.Width) / 2, (size.Height - frame.Height) / 2), frame));
            }
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
}
