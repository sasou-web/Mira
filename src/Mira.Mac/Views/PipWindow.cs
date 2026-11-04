using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Image dans l’image », as on Windows: the video alone in a small window of its own, outside Mira's window, above the
/// other apps' windows and on every desktop. macOS draws its rounded corners and its shadow and resizes it from its
/// edges, the video's shape kept (contentAspectRatio); a drag on the picture moves it, a click pauses, a double click
/// goes back to Mira. Its controls show while the pointer is on it. The picture is the player's: PlayerPage.Pip.cs moves
/// mpv's view here and back, its subtitles drawn by mpv in it.
/// </summary>
public sealed class PipWindow : Window
{
    /// <summary>Where the player puts mpv's view.</summary>
    public Panel VideoHost { get; } = new() { Background = Brushes.Black };
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly Grid _top, _bottom;
    private readonly StackPanel _middle;
    private readonly DockPanel _topRow, _bottomRow;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") } };
    private readonly Icon _playIcon = new("pause", 26);
    private readonly Button _back, _close, _rewind, _play, _forward, _subtitles;
    // Under the subtitles' glyph while they show, as on video sites.
    private readonly Border _subtitlesOn = new() { Height = 2, Width = 14, CornerRadius = new CornerRadius(1), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5), IsHitTestVisible = false };
    private readonly ProgressLine _progress = new() { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _click = new() { Interval = TimeSpan.FromMilliseconds(230) };
    private long _lastMotion, _activatedAt = long.MinValue / 2;
    private bool _shown = true, _paused, _closable, _moving, _keyboard;
    private bool? _subtitlesShown = false;
    private Point? _press;
    private PixelPoint _pressScreen, _pressPosition;
    private double _aspect;

    /// <summary>« Revenir dans Mira », a double click, or I: the whole player in Mira's window.</summary>
    public event Action? Back;
    /// <summary>« Fermer »: the playback stops.</summary>
    public event Action? Stop;
    public event Action? PlayPause, Subtitles;
    /// <summary>Seconds to jump, backwards when negative.</summary>
    public event Action<double>? Seek;
    /// <summary>A key pressed while it is in front: the player's shortcuts (PlayerPage.PipKey).</summary>
    public event Action<KeyEventArgs>? KeyPressed;
    /// <summary>Moved by the person: where it is now is kept for the next time.</summary>
    public event Action? Placed;
    /// <summary>A new size: the subtitles follow.</summary>
    public event Action? VideoResized;

    /// <param name="at">Where it opens, in the units of the windows' positions (<see cref="ScreensOf"/>).</param>
    /// <param name="scale">Those units in one device-independent pixel there.</param>
    public PipWindow(ScreenFit.Pixels at, double scale, double aspect)
    {
        _aspect = aspect;
        Title = L("Mira · image dans l’image", "Mira · picture in picture");
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; CanResize = true; CanMinimize = false; CanMaximize = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)Math.Round(at.Left), (int)Math.Round(at.Top));
        Width = at.Width / scale; Height = at.Height / scale;
        if (OperatingSystem.IsMacOS())
        {
            // The window of macOS without its title bar nor its buttons: the content to its rounded edges, its shadow and
            // its resizing kept.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 0;
        }

        _back = Round("pip-exit", 32, 17, L("Revenir dans Mira · I", "Back to Mira · I"), () => Back?.Invoke());
        _close = Round("close", 32, 17, L("Fermer le lecteur", "Close the player"), () => Stop?.Invoke());
        _close.Margin = new Thickness(2, 0, 0, 0);
        var buttons = Ui.Row(0, _back, _close);
        DockPanel.SetDock(buttons, Dock.Right);
        // Sizes and margins are set by FitControls, at the window's size.
        _topRow = new DockPanel { VerticalAlignment = VerticalAlignment.Top, Children = { buttons, _title } };
        _top = new Grid { VerticalAlignment = VerticalAlignment.Top, Background = Shade(top: true), Children = { _topRow } };

        _rewind = Round("rewind", 40, 21, L("Reculer de 10 s · ←", "Back 10 s · ←"), () => Seek?.Invoke(-10));
        _play = Round("pause", 52, 26, L("Pause · Espace", "Pause · Space"), () => PlayPause?.Invoke());
        _play.Content = _playIcon; _play.Background = Ui.Brush("#80000000");
        _forward = Round("forward", 40, 21, L("Avancer de 10 s · →", "Forward 10 s · →"), () => Seek?.Invoke(10));
        _middle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { _rewind, _play, _forward } };

        _subtitles = Round("subtitles", 32, 18, L("Afficher les sous-titres · C", "Show subtitles · C"), () => Subtitles?.Invoke());
        _subtitles.Content = new Grid { Children = { new Icon("subtitles", 18), _subtitlesOn } };
        _subtitles.IsVisible = false; _subtitlesOn.IsVisible = false;
        DockPanel.SetDock(_subtitles, Dock.Right);
        _bottomRow = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { _subtitles, _time } };
        _bottom = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Background = Shade(top: false), Children = { _bottomRow } };
        FitControls(Fit, force: true);
        _root.SizeChanged += (_, e) => FitControls(PictureInPicture.ControlsFor(e.NewSize.Width, e.NewSize.Height));

        foreach (var part in new Control[] { _top, _middle, _bottom }) part.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
        _root.Children.Add(VideoHost);
        _root.Children.Add(_top); _root.Children.Add(_middle); _root.Children.Add(_bottom); _root.Children.Add(_progress);
        AutomationProperties.SetName(_root, L("Vidéo en image dans l’image", "Video in picture in picture"));
        Content = _root;

        _root.PointerPressed += Pressed;
        _root.PointerMoved += Moved;
        _root.PointerReleased += Released;
        _root.PointerCaptureLost += (_, _) => EndGesture();
        _root.PointerExited += (_, _) => { if (_press is null) _lastMotion = 0; };
        // Before a focused button: Space never clicks again the last button pressed.
        AddHandler(KeyDownEvent, (_, e) => { _keyboard = true; ShowControls(); KeyPressed?.Invoke(e); }, RoutingStrategies.Tunnel);
        VideoHost.SizeChanged += (_, _) => VideoResized?.Invoke();
        Activated += (_, _) => _activatedAt = Environment.TickCount64;
        _idle.Tick += (_, _) => HideWhenIdle();
        _click.Tick += (_, _) => { _click.Stop(); PlayPause?.Invoke(); };
        Opened += (_, _) => { Native(); ShowControls(); };
    }
    private static LinearGradientBrush Shade(bool top) => new()
    {
        StartPoint = new RelativePoint(0, top ? 0 : 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, top ? 1 : 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#B0000000"), 0), new GradientStop(Color.Parse("#00000000"), 1) }
    };
    /// <summary>
    /// The controls at the sizes of <paramref name="fit"/>, as on Windows: in a small window, lighter bars, buttons and
    /// spaces, the title and the 10 s jumps left out when too narrow; the text and the corners' glyphs the same distance
    /// from every edge.
    /// </summary>
    private void FitControls(PictureInPicture.Controls fit, bool force = false)
    {
        if (fit == Fit && !force) return;
        Fit = fit;
        const double margin = PictureInPicture.Controls.Margin;
        _top.Height = _bottom.Height = fit.Bar;
        _topRow.Margin = new Thickness(margin, margin, margin, 0);
        _bottomRow.Margin = new Thickness(margin, 0, margin, margin);
        _title.Margin = new Thickness(fit.Inset - margin, 0, 8, 0); _time.Margin = new Thickness(fit.Inset - margin, 0, 0, 0);
        _title.FontSize = fit.TextSize; _time.FontSize = fit.TextSize - .5;
        _title.Opacity = fit.Title ? 1 : 0;
        foreach (var corner in new[] { _back, _close }) Size(corner, fit.Corner, fit.CornerGlyph);
        // The subtitles' glyph a pixel larger, its bar just under it.
        var subtitles = fit.CornerGlyph + 1;
        Size(_subtitles, fit.Corner, subtitles);
        _subtitlesOn.Width = subtitles - 4; _subtitlesOn.Margin = new Thickness(0, 0, 0, (fit.Corner - subtitles) / 2 - 2);
        Size(_play, fit.Play, fit.PlayGlyph);
        if (_paused) _playIcon.Margin = new Thickness(fit.PlayGlyph * 3 / 26, 0, 0, 0);
        _play.Margin = new Thickness(fit.Spacing, 0, fit.Spacing, 0);
        foreach (var jump in new[] { _rewind, _forward }) { Size(jump, fit.Jump, fit.JumpGlyph); jump.IsVisible = fit.Jumps; }
        if (!force) ControlsChanged?.Invoke();
    }
    private static void Size(Button button, double size, double glyph)
    {
        button.Width = button.Height = size; button.CornerRadius = new CornerRadius(size / 2);
        if ((button.Content as Icon ?? (button.Content as Panel)?.Children.OfType<Icon>().FirstOrDefault()) is { } icon) icon.Width = icon.Height = glyph;
    }
    private static Button Round(string icon, double size, double glyph, string tip, Action click)
    {
        var button = Ui.IconButton(icon, tip, click, "player", glyph);
        button.Width = button.Height = size; button.CornerRadius = new CornerRadius(size / 2);
        return button;
    }

    /// <summary>The video's height here, in device-independent pixels (the subtitles' size follows it).</summary>
    public double VideoHeight => VideoHost.Bounds.Height;
    /// <summary>Its controls on the picture, for the self-check.</summary>
    public bool ControlsShown => _shown;
    /// <summary>How its controls fit its size now (<see cref="PictureInPicture.ControlsFor"/>).</summary>
    public PictureInPicture.Controls Fit { get; private set; } = PictureInPicture.ControlsFor(PictureInPicture.Width, PictureInPicture.Width * 9 / 16);
    /// <summary>The controls shown or hidden, or fitted to another size: the subtitles go above them or back down.</summary>
    public event Action? ControlsChanged;
    /// <summary>The video's shape, width over height.</summary>
    public double Aspect => _aspect;

    /// <summary>Closed by the player only (the video taken back first); the person's close request stops the playback.</summary>
    public void Dismiss()
    {
        _closable = true; _idle.Stop(); _click.Stop();
        Close();
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closable) { e.Cancel = true; Dispatcher.UIThread.Post(() => Stop?.Invoke()); }
        base.OnClosing(e);
    }

    public void SetTitle(string title) { if (_title.Text != title) _title.Text = title; }
    /// <summary>What the play button does and says: play while paused, pause while playing.</summary>
    public void SetPaused(bool paused)
    {
        if (paused == _paused) return;
        _paused = paused;
        _playIcon.Kind = paused ? "play" : "pause";
        // The play triangle's weight sits left of its box: nudged so it looks centred in the circle.
        _playIcon.Margin = paused ? new Thickness(Fit.PlayGlyph * 3 / 26, 0, 0, 0) : default;
        var tip = paused ? L("Lecture · Espace", "Play · Space") : L("Pause · Espace", "Pause · Space");
        ToolTip.SetTip(_play, tip); AutomationProperties.SetName(_play, tip.Split(" · ")[0]);
        ShowControls();
    }
    public void SetTime(string text, double fraction)
    {
        if (_time.Text != text) _time.Text = text;
        var value = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        // Redrawn once the line moves by a pixel.
        if (Math.Abs(value - _progress.Value) * _progress.Bounds.Width < 1) return;
        _progress.Value = value; _progress.InvalidateVisual();
    }
    /// <summary>The subtitles' button: hidden without subtitles, else whether they show.</summary>
    public void SetSubtitles(bool? shown)
    {
        if (shown == _subtitlesShown) return;
        _subtitlesShown = shown;
        _subtitles.IsVisible = shown is not null; _subtitlesOn.IsVisible = shown == true;
        var tip = shown == true ? L("Masquer les sous-titres · C", "Hide subtitles · C") : L("Afficher les sous-titres · C", "Show subtitles · C");
        ToolTip.SetTip(_subtitles, tip);
        AutomationProperties.SetName(_subtitles, shown == true ? L("Sous-titres : affichés", "Subtitles: on") : L("Sous-titres : masqués", "Subtitles: off"));
    }

    public void ShowControls()
    {
        _lastMotion = Environment.TickCount64;
        if (!_idle.IsEnabled) _idle.Start();
        if (_shown) return;
        _shown = true;
        foreach (var part in new Control[] { _top, _middle, _bottom }) { part.Opacity = 1; part.IsHitTestVisible = true; }
        ControlsChanged?.Invoke();
    }
    /// <summary>For the self-check: the controls gone at once, as once the pointer left.</summary>
    internal void HideControlsNow() { _lastMotion = 0; HideWhenIdle(); }
    /// <summary>Gone 2.5 s after the pointer last moved over it, at once once it left; kept while a press lasts or a button has the keyboard.</summary>
    private void HideWhenIdle()
    {
        if (!_shown) { _idle.Stop(); return; }
        if (_press is not null || _keyboard && FocusManager?.GetFocusedElement() is Button) return;
        var idle = Environment.TickCount64 - _lastMotion;
        if (idle < (_root.IsPointerOver ? 2500 : 600)) return;
        _shown = false; _idle.Stop();
        foreach (var part in new Control[] { _top, _middle, _bottom }) { part.Opacity = 0; part.IsHitTestVisible = false; }
        ControlsChanged?.Invoke();
    }

    // —— Moved by a drag on the picture; a click pauses, a double click goes back to Mira ——
    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        _keyboard = false; ShowControls();
        if (e.ClickCount >= 2) { _click.Stop(); _press = null; Back?.Invoke(); return; }
        _press = e.GetPosition(this); _moving = false;
        _pressScreen = this.PointToScreen(_press.Value); _pressPosition = Position;
        e.Pointer.Capture(_root);
    }
    private void Moved(object? sender, PointerEventArgs e)
    {
        _keyboard = false; ShowControls();
        if (_press is not { } press) return;
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) { EndGesture(); return; }
        // Measured on the screen, not in the window, which moves under the pointer.
        var now = this.PointToScreen(e.GetPosition(this));
        var (dx, dy) = (now.X - _pressScreen.X, now.Y - _pressScreen.Y);
        if (!_moving)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 5) return;
            _moving = true; _click.Stop();
        }
        Position = new PixelPoint(_pressPosition.X + dx, _pressPosition.Y + dy);
    }
    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is null) return;
        var moved = _moving;
        // The click that brought it to the front only shows the controls, never pauses: as in Mira's window.
        var activating = Environment.TickCount64 - _activatedAt < 400;
        EndGesture();
        e.Pointer.Capture(null);
        if (!moved && !activating) _click.Start();
    }
    private void EndGesture()
    {
        if (_press is null) return;
        _press = null;
        if (!_moving) return;
        _moving = false;
        // Let go partly off the screen: back inside the one showing most of it.
        if (ScreensOf(this) is { Count: > 0 } screens && Placement() is { } window)
        {
            var settled = PictureInPicture.Settle(window, screens);
            if (settled != window) Position = new PixelPoint((int)settled.Left, (int)settled.Top);
        }
        Placed?.Invoke();
    }

    // —— Where it is ——
    /// <summary>
    /// The screens' work areas in the units of the windows' positions, and how many of those units make a
    /// device-independent pixel on each: macOS counts in points (one per device-independent pixel, whatever the screen's
    /// backing scale), X11 (where the checks run) in pixels. Measured on <paramref name="level"/> rather than assumed.
    /// </summary>
    public static List<ScreenFit.Screen> ScreensOf(TopLevel level)
    {
        var points = UnitsPerPixel(level) is var units && Math.Abs(units - 1) < .05;
        return level.Screens?.All.Select(s => new ScreenFit.Screen(new ScreenFit.Pixels(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height), points ? 1 : s.Scaling)).ToList() ?? [];
    }
    private static double UnitsPerPixel(TopLevel level) => (level.PointToScreen(new Point(100, 0)).X - level.PointToScreen(default).X) / 100d;
    /// <summary>Its place and size in the units of the windows' positions; null before it shows.</summary>
    public ScreenFit.Pixels? Placement()
    {
        if (!IsVisible || Bounds.Width <= 0) return null;
        var units = UnitsPerPixel(this);
        return new ScreenFit.Pixels(Position.X, Position.Y, Math.Round(Bounds.Width * units), Math.Round(Bounds.Height * units));
    }
    /// <summary>Placed and sized (<see cref="PictureInPicture"/>), its limits on that screen kept for macOS's own resizing.</summary>
    public void Place(ScreenFit.Pixels at, ScreenFit.Screen screen)
    {
        var (minimum, maximum) = PictureInPicture.Widths(screen, _aspect);
        MinWidth = 0; MinHeight = 0; MaxWidth = double.PositiveInfinity; MaxHeight = double.PositiveInfinity;
        Position = new PixelPoint((int)Math.Round(at.Left), (int)Math.Round(at.Top));
        Width = at.Width / screen.Scale; Height = at.Height / screen.Scale;
        MinWidth = Math.Floor(minimum / screen.Scale); MinHeight = Math.Floor(MinWidth / _aspect);
        MaxWidth = Math.Ceiling(maximum / screen.Scale); MaxHeight = Math.Ceiling(MaxWidth / _aspect);
    }
    /// <summary>Another shape (the next title, or the video's once known): as wide, held by its side nearest the screen's edge.</summary>
    public void Refit(double aspect)
    {
        _aspect = aspect;
        if (Placement() is { } window && ScreensOf(this) is { Count: > 0 } screens && PictureInPicture.ScreenOf(window, screens) is { } screen)
            Place(PictureInPicture.Refit(window, aspect, screen), screen);
        Native();
    }

    // —— macOS ——
    /// <summary>
    /// What Avalonia does not say: the video's shape kept as macOS resizes it from its edges, and the window on every
    /// desktop, over a full-screen app too, rather than on the one where it opened.
    /// </summary>
    private void Native()
    {
        if (!OperatingSystem.IsMacOS() || TryGetPlatformHandle() is not { } handle) return;
        var window = handle is IMacOSTopLevelPlatformHandle mac ? mac.NSWindow : handle.HandleDescriptor == "NSWindow" ? handle.Handle : IntPtr.Zero;
        if (window == IntPtr.Zero) return;
        Cocoa.KeepShape(window, _aspect);
        Cocoa.OnEveryDesktop(window);
    }
    private static unsafe class Cocoa
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private static readonly IntPtr Send = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
        private static readonly IntPtr RegisterName = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "sel_registerName");
        [StructLayout(LayoutKind.Sequential)] private struct Size { public double Width, Height; }
        // NSWindowCollectionBehavior: on every Space, beside a full-screen window; the flags macOS refuses together with
        // these (an exception, which would end Mira) taken off first.
        private const nuint CanJoinAllSpaces = 1 << 0, MoveToActiveSpace = 1 << 1, FullScreenPrimary = 1 << 7, FullScreenAuxiliary = 1 << 8, FullScreenNone = 1 << 9;
        private static IntPtr Selector(string name)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* text = bytes) return ((delegate* unmanaged<byte*, IntPtr>)RegisterName)(text);
        }
        public static void KeepShape(IntPtr window, double aspect) =>
            ((delegate* unmanaged<IntPtr, IntPtr, Size, void>)Send)(window, Selector("setContentAspectRatio:"), new Size { Width = Math.Round(aspect * 1000), Height = 1000 });
        public static void OnEveryDesktop(IntPtr window)
        {
            var behaviour = ((delegate* unmanaged<IntPtr, IntPtr, nuint>)Send)(window, Selector("collectionBehavior"));
            var wanted = behaviour & ~(MoveToActiveSpace | FullScreenPrimary | FullScreenNone) | CanJoinAllSpaces | FullScreenAuxiliary;
            if (wanted != behaviour) ((delegate* unmanaged<IntPtr, IntPtr, nuint, void>)Send)(window, Selector("setCollectionBehavior:"), wanted);
        }
    }
}
