using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop.Playback;

/// <summary>
/// « Image dans l’image »: the video alone in a small window above every other, outside Mira's window. The video is the
/// player's own native window (<see cref="VideoHost"/>), moved here and back by MainWindow.PictureInPicture.cs: mpv goes on
/// drawing it at this window's size, its subtitles included. The controls are in a layered child window over it
/// (<see cref="PlayerOverlay"/>), as in the player, since WPF cannot draw over the native video: the title, back to Mira
/// and close at the top; ten seconds back, play or pause, ten seconds on in the middle; the time and the subtitles at the
/// bottom, over a thin progress line that stays. A click plays or pauses, a double click goes back to Mira; dragged by the
/// picture, resized by its edges and corners, the video's shape kept (<see cref="PictureInPicture"/>).
/// </summary>
internal sealed class PipWindow : Window
{
    private readonly ScreenFit.Pixels _opening;
    private readonly Grid _root = new() { Background = Brushes.Black }, _video = new();
    private readonly Grid _surface, _top, _bottom, _track;
    private readonly StackPanel _middle;
    private readonly TextBlock _title, _time;
    private readonly Icon _playIcon = new() { Kind = "pause", Width = 26, Height = 26 };
    private readonly Button _play, _subtitles;
    private readonly Border _subtitlesOn, _progress;
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(250) }, _click = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private long _lastMotion;
    private bool _shown = true, _closable, _paused, _keyboard;
    private double _fraction;
    private string _timeText = "";
    // The pointer held on the picture: a click, a move or a resize from one edge.
    private enum Gesture { None, Pressed, Moving, Resizing }
    private Gesture _gesture;
    private PictureInPicture.Edge _edge;
    private NativePoint _from;
    private ScreenFit.Pixels _start;
    private ScreenFit.Screen? _screen;
    private bool _clickPlays;

    /// <summary>The controls' layered window over the video.</summary>
    public PlayerOverlay Controls { get; }
    /// <summary>The video's shape, width over height (<see cref="PictureInPicture.Aspect"/>), kept while an edge is pulled.</summary>
    public double Aspect { get; set; } = 16d / 9;
    /// <summary>The video's height in device-independent pixels: what the subtitles are sized to.</summary>
    public double VideoHeight => _root.ActualHeight;
    public bool ControlsShown => _shown;
    /// <summary>« Revenir dans Mira », a double click or I.</summary>
    public event Action? Back;
    /// <summary>« Fermer », or Alt+F4: the playback stops.</summary>
    public event Action? Stop;
    public event Action? PlayPause, Subtitles;
    /// <summary>Seconds to jump, backwards when negative.</summary>
    public event Action<double>? Seek;
    /// <summary>A key pressed while it is the active window: the player's shortcuts, read by MainWindow.</summary>
    public event Action<KeyEventArgs>? Key;
    /// <summary>Moved or resized by the person: where it is now is kept for the next time.</summary>
    public event Action? Placed;
    /// <summary>A new size: the subtitles follow.</summary>
    public event Action? Resized;

    /// <param name="at">Where it opens, in device pixels (<see cref="PictureInPicture.Open"/>).</param>
    public PipWindow(ScreenFit.Pixels at)
    {
        _opening = at;
        Title = L("Mira · image dans l’image", "Mira · picture in picture");
        Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black; UseLayoutRounding = true; SnapsToDevicePixels = true;
        // Placed in device pixels before it shows (SourceInitialized); until then, off every screen.
        Left = Top = -32000; Width = PictureInPicture.Width; Height = Math.Round(PictureInPicture.Width * 9 / 16);
        if (Application.Current?.MainWindow is { } main && !ReferenceEquals(main, this)) Icon = main.Icon;
        // No caption nor frame of its own: DWM's shadow, rounded corners and outline, as for Mira's window (WindowFrame).
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(1), UseAeroCaptionButtons = false });
        _ = new WindowFrame(this);

        _title = new TextBlock { Foreground = Palette.Get("Text"), FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        var back = Round("pip-exit", 32, 17, L("Revenir dans Mira", "Back to Mira"), L("Revenir dans Mira · I", "Back to Mira · I"), () => Back?.Invoke());
        var close = Round("close", 32, 17, L("Fermer le lecteur", "Close the player"), L("Fermer le lecteur", "Close the player"), () => Stop?.Invoke());
        close.Margin = new Thickness(2, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Children = { back, close } };
        DockPanel.SetDock(buttons, Dock.Right);
        // Clear of the edges' grip: a press there resizes, never on a button.
        _top = new Grid { Height = 52, VerticalAlignment = VerticalAlignment.Top, Children = { Shade(top: true), new DockPanel { Margin = new Thickness(12, 10, 10, 0), VerticalAlignment = VerticalAlignment.Top, Children = { buttons, _title } } } };

        var rewind = Round("rewind", 40, 21, L("Reculer de 10 secondes", "Back 10 seconds"), L("Reculer de 10 s · ←", "Back 10 s · ←"), () => Seek?.Invoke(-10));
        _play = Round("pause", 52, 26, "Pause", L("Pause · Espace", "Pause · Space"), () => PlayPause?.Invoke());
        _play.Content = _playIcon; _play.Background = Palette.Get("Pill"); _play.Margin = new Thickness(14, 0, 14, 0);
        var forward = Round("forward", 40, 21, L("Avancer de 10 secondes", "Forward 10 seconds"), L("Avancer de 10 s · →", "Forward 10 s · →"), () => Seek?.Invoke(10));
        _middle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { rewind, _play, forward } };

        _time = new TextBlock { Foreground = Palette.Get("Text"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
        // The subtitles' button carries a bar under its glyph while they show, as on video sites.
        _subtitlesOn = new Border { Height = 2, Width = 14, CornerRadius = new CornerRadius(1), Background = Palette.Get("Accent"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5), IsHitTestVisible = false };
        _subtitles = Round("subtitles", 32, 18, L("Sous-titres", "Subtitles"), L("Sous-titres · C", "Subtitles · C"), () => Subtitles?.Invoke());
        _subtitles.Content = new Grid { Children = { new Icon { Kind = "subtitles", Width = 18, Height = 18 }, _subtitlesOn } };
        _subtitles.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_subtitles, Dock.Right);
        _bottom = new Grid { Height = 52, VerticalAlignment = VerticalAlignment.Bottom, Children = { Shade(top: false), new DockPanel { Margin = new Thickness(12, 0, 10, 10), VerticalAlignment = VerticalAlignment.Bottom, Children = { _subtitles, _time } } } };

        _progress = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = Palette.Get("Accent") };
        _track = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Palette.Get("Hairline"), IsHitTestVisible = false, Children = { _progress } };
        _track.SizeChanged += (_, _) => DrawProgress(force: true);

        // Nearly transparent rather than transparent: the layered window takes the pointer only where it draws.
        _surface = new Grid { Background = Palette.Tint("Bg", 1), Cursor = Cursors.Arrow, Focusable = true, FocusVisualStyle = null, Children = { _top, _middle, _bottom, _track } };
        TextElement.SetFontFamily(_surface, (FontFamily)FindResource("AppFont"));
        KeyboardNavigation.SetTabNavigation(_surface, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(_surface, L("Vidéo en image dans l’image", "Video in picture in picture"));
        _surface.MouseMove += PointerMoved;
        _surface.MouseLeftButtonDown += PointerPressed;
        _surface.MouseLeftButtonUp += PointerReleased;
        _surface.LostMouseCapture += (_, _) => EndGesture();
        _surface.MouseLeave += (_, _) => { if (_gesture == Gesture.None) _lastMotion = 0; };
        _surface.PreviewKeyDown += (_, e) => { _keyboard = true; ShowControls(); Key?.Invoke(e); };
        PreviewKeyDown += (_, e) => { _keyboard = true; ShowControls(); Key?.Invoke(e); };

        Controls = new PlayerOverlay { PlacementTarget = _root, Child = new AdornerDecorator { Child = _surface } };
        _root.Children.Add(_video); _root.Children.Add(Controls);
        _root.SizeChanged += (_, _) => { Controls.Reposition(); Resized?.Invoke(); };
        Content = _root;

        _idle.Tick += (_, _) => HideWhenIdle();
        _click.Tick += (_, _) => { _click.Stop(); PlayPause?.Invoke(); };
        SourceInitialized += (_, _) => Screens.Move(Handle, _opening);
        Loaded += (_, _) => { Controls.IsOpen = true; Controls.Reposition(); KeepControlsOnTop(); ShowControls(); };
    }
    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>The player's video, moved here: drawn under the controls.</summary>
    public void Hold(VideoHost video)
    {
        if (!_video.Children.Contains(video)) _video.Children.Add(video);
        // The video's window joins this one on top of its children: the controls go back above it, once the move is
        // done and once the window has settled (its controls' window is made only when it has loaded).
        Dispatcher.BeginInvoke(() => { Controls.Reposition(); KeepControlsOnTop(); }, DispatcherPriority.Render);
        Dispatcher.BeginInvoke(KeepControlsOnTop, DispatcherPriority.Background);
    }
    /// <summary>The controls' window back above the video's when anything sits above it (GW_HWNDPREV): a window moved in goes on top.</summary>
    private void KeepControlsOnTop()
    {
        if (Controls.Handle is var controls && controls != IntPtr.Zero && GetWindow(controls, 3) != IntPtr.Zero) Controls.Raise();
    }
    /// <summary>The video taken back, before this window closes: closing it with the video inside would destroy mpv's window.</summary>
    public void Release(VideoHost video) => _video.Children.Remove(video);

    /// <summary>Closed for good, by Mira (the video taken back first).</summary>
    public void Dismiss()
    {
        _closable = true; _idle.Stop(); _click.Stop(); EndGesture();
        Controls.IsOpen = false;
        Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 or the system menu: as « Fermer ».
        if (!_closable) { e.Cancel = true; Dispatcher.BeginInvoke(() => Stop?.Invoke()); }
        base.OnClosing(e);
    }

    public void SetTitle(string title) { if (_title.Text != title) _title.Text = title; }
    /// <summary>What the play button does and says: play while paused, pause while playing.</summary>
    public void SetPaused(bool paused)
    {
        if (paused == _paused) return;
        _paused = paused;
        Motion.Swap(_playIcon, paused ? "play" : "pause");
        // The play triangle's weight sits left of its box: nudged so it looks centred in the circle.
        _playIcon.Margin = paused ? new Thickness(3, 0, 0, 0) : new Thickness(0);
        _play.ToolTip = paused ? L("Lecture · Espace", "Play · Space") : L("Pause · Espace", "Pause · Space");
        AutomationProperties.SetName(_play, paused ? L("Lecture", "Play") : "Pause");
        ShowControls();
    }
    /// <summary>The time, written only while the controls show (each new text repaints the layered window), and the progress line.</summary>
    public void SetTime(string text, double fraction)
    {
        _timeText = text;
        if (_shown && _time.Text != text) _time.Text = text;
        _fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        DrawProgress(force: false);
    }
    /// <summary>The subtitles' button: null without subtitles, else whether they show.</summary>
    public void SetSubtitles(bool? shown)
    {
        var visibility = shown is null ? Visibility.Collapsed : Visibility.Visible;
        if (_subtitles.Visibility != visibility) _subtitles.Visibility = visibility;
        var bar = shown == true ? Visibility.Visible : Visibility.Hidden;
        if (_subtitlesOn.Visibility == bar) return;
        _subtitlesOn.Visibility = bar;
        AutomationProperties.SetName(_subtitles, shown == true ? L("Sous-titres : affichés", "Subtitles: on") : L("Sous-titres : masqués", "Subtitles: off"));
        _subtitles.ToolTip = shown == true ? L("Masquer les sous-titres · C", "Hide subtitles · C") : L("Afficher les sous-titres · C", "Show subtitles · C");
    }
    /// <summary>Only when its width changes by a whole pixel: the layered window is sent again at each change.</summary>
    private void DrawProgress(bool force)
    {
        var width = Math.Round(_track.ActualWidth * _fraction);
        if (force || Math.Abs(width - _progress.Width) >= 1) _progress.Width = width;
    }

    public void ShowControls()
    {
        _lastMotion = Environment.TickCount64;
        KeepControlsOnTop();
        if (!_idle.IsEnabled) _idle.Start();
        if (_shown) return;
        _shown = true; _time.Text = _timeText;
        foreach (var panel in new UIElement[] { _top, _middle, _bottom }) { panel.IsHitTestVisible = true; Motion.Fade(panel, 1, 140); }
    }
    /// <summary>For the player check: the controls gone at once, as once the pointer left.</summary>
    internal void HideControlsNow() { _lastMotion = 0; HideWhenIdle(); }
    /// <summary>Gone 2.5 s after the pointer last moved over it, at once once it left; kept while a key works them or a press lasts.</summary>
    private void HideWhenIdle()
    {
        if (!_shown) { _idle.Stop(); return; }
        if (_gesture != Gesture.None || _keyboard && _surface.IsKeyboardFocusWithin && !_surface.IsKeyboardFocused) return;
        var idle = Environment.TickCount64 - _lastMotion;
        if (idle < (_surface.IsMouseOver ? 2500 : 600)) return;
        _shown = false; _idle.Stop();
        foreach (var panel in new UIElement[] { _top, _middle, _bottom }) { panel.IsHitTestVisible = false; Motion.Fade(panel, 0, 240); }
    }

    private void PointerMoved(object sender, MouseEventArgs e)
    {
        _keyboard = false;
        if (_gesture == Gesture.None)
        {
            var point = e.GetPosition(_surface);
            var cursor = PictureInPicture.EdgeAt(point.X, point.Y, _surface.ActualWidth, _surface.ActualHeight) switch
            {
                PictureInPicture.Edge.Left or PictureInPicture.Edge.Right => Cursors.SizeWE,
                PictureInPicture.Edge.Top or PictureInPicture.Edge.Bottom => Cursors.SizeNS,
                PictureInPicture.Edge.TopLeft or PictureInPicture.Edge.BottomRight => Cursors.SizeNWSE,
                PictureInPicture.Edge.TopRight or PictureInPicture.Edge.BottomLeft => Cursors.SizeNESW,
                _ => Cursors.Arrow
            };
            if (_surface.Cursor != cursor) _surface.Cursor = cursor;
            ShowControls();
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { EndGesture(); return; }
        if (!GetCursorPos(out var now)) return;
        double dx = now.X - _from.X, dy = now.Y - _from.Y;
        if (_gesture == Gesture.Pressed)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 5) return;
            _gesture = Gesture.Moving; _click.Stop();
        }
        if (_gesture == Gesture.Moving) SetWindowPos(Handle, IntPtr.Zero, (int)Math.Round(_start.Left + dx), (int)Math.Round(_start.Top + dy), 0, 0, NoSize | NoZOrder | NoActivate | NoOwnerZOrder);
        else if (_screen is { } screen) Place(PictureInPicture.Resize(_start, _edge, dx, dy, Aspect, screen), direct: true);
        _lastMotion = Environment.TickCount64;
    }
    /// <summary>
    /// A press on the picture or its edges (the buttons take their own). Never a pause when the press brings the window
    /// to the front: it only shows the controls, as in Mira's window.
    /// </summary>
    private void PointerPressed(object sender, MouseButtonEventArgs e)
    {
        var activating = Controls.TakeActivatingClick();
        _surface.Focus(); e.Handled = true; ShowControls();
        if (e.ClickCount >= 2) { _click.Stop(); EndGesture(); Back?.Invoke(); return; }
        if (!GetCursorPos(out _from)) return;
        _start = Bounds(); _screen = PictureInPicture.ScreenOf(_start, Screens.All());
        var point = e.GetPosition(_surface);
        var edge = PictureInPicture.EdgeAt(point.X, point.Y, _surface.ActualWidth, _surface.ActualHeight);
        if (edge is { } pulled && _screen is not null) { _gesture = Gesture.Resizing; _edge = pulled; } else _gesture = Gesture.Pressed;
        _clickPlays = !activating && edge is null;
        if (!_surface.CaptureMouse()) _gesture = Gesture.None;
    }
    private void PointerReleased(object sender, MouseButtonEventArgs e)
    {
        if (_gesture == Gesture.None) return;
        e.Handled = true;
        var clicked = _gesture == Gesture.Pressed && _clickPlays;
        EndGesture();
        // Played or paused after the double click's delay, unless a second click comes.
        if (clicked) { _click.Stop(); _click.Start(); }
    }
    private void EndGesture()
    {
        var gesture = _gesture;
        _gesture = Gesture.None;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        // Let go partly off the screens (moved there, or an edge pulled past the screen's): back inside the one showing most of it.
        if (gesture is Gesture.Moving or Gesture.Resizing) Place(PictureInPicture.Settle(Bounds(), Screens.All()));
        if (gesture is Gesture.Moving or Gesture.Resizing) Placed?.Invoke();
    }

    /// <summary>Where it is, in device pixels.</summary>
    public ScreenFit.Pixels Bounds() => GetWindowRect(Handle, out var r) ? new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : _opening;
    /// <param name="direct">Within one screen (an edge pulled): one move; otherwise moved then sized, for a screen of another scale.</param>
    public void Place(ScreenFit.Pixels at, bool direct = false)
    {
        if (Handle == IntPtr.Zero || at == Bounds()) return;
        if (direct) SetWindowPos(Handle, IntPtr.Zero, (int)at.Left, (int)at.Top, (int)at.Width, (int)at.Height, NoZOrder | NoActivate | NoOwnerZOrder);
        else Screens.Move(Handle, at);
    }
    /// <summary>The video's shape changed (another title): as wide, the new height, kept on its screen.</summary>
    public void Refit()
    {
        if (_gesture != Gesture.None || PictureInPicture.ScreenOf(Bounds(), Screens.All()) is not { } screen) return;
        Place(PictureInPicture.Refit(Bounds(), Aspect, screen));
    }
    /// <summary>The middle of a window, in device pixels: where the small window opens the first time.</summary>
    public static (double X, double Y)? Middle(IntPtr window) => window != IntPtr.Zero && GetWindowRect(window, out var r) ? ((r.Left + r.Right) / 2d, (r.Top + r.Bottom) / 2d) : null;

    private Button Round(string icon, double size, double glyph, string name, string tip, Action click)
    {
        var button = new Button { Style = (Style)FindResource("PlayerButton"), Width = size, Height = size, Content = new Icon { Kind = icon, Width = glyph, Height = glyph }, ToolTip = tip };
        Motion.SetRadius(button, new CornerRadius(size / 2));
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => { click(); ShowControls(); };
        return button;
    }
    /// <summary>The dark shade behind the top or bottom controls, as in the player.</summary>
    private static Border Shade(bool top)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(top ? Color.FromArgb(0xB0, 0, 0, 0) : Color.FromArgb(0, 0, 0, 0), 0));
        brush.GradientStops.Add(new GradientStop(top ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(0xC8, 0, 0, 0), 1));
        brush.Freeze();
        return new Border { Background = brush, IsHitTestVisible = false };
    }

    private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
}
