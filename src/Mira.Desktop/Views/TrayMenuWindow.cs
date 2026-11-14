using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop.Views;

/// <summary>What the card of the menu shows: the title playing, where it is, and its picture (null until it comes).</summary>
internal sealed record TrayCard(string Title, string Line, double Position, double Duration, bool Ready, bool Paused, bool CanNext, ImageSource? Picture);

/// <summary>
/// The menu of Mira's icon in the notification area (« Carte », 2026-11-14), in the style of Mira's menus (« Écrin »): the
/// card of what plays (picture, title, progress, stop, −10 s, play or pause, +10 s, next episode), then Open Mira,
/// Minimize to the notification area and Quit Mira; what shows is <see cref="TrayMenuPlan.For"/>. A small borderless
/// window of its own, above every other, out of the taskbar, placed by <see cref="TrayMenuPlan.Place"/> on the screen of
/// the click; gone when it loses the foreground, on Échap, on a click outside it, or once a line of the app is chosen
/// (the playback buttons keep it open, as a remote does). Buttons still, plain fills, every one named for Narrator.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    /// <summary>Transparent room around the menu for its shadow (device-independent pixels).</summary>
    private const double Room = 28;
    private readonly Action<DesktopCommand> _chosen;
    private readonly Squircle _menu, _picture;
    private readonly StackPanel _card;
    private readonly Border _rule;
    private readonly TextBlock _title, _line, _elapsed, _total;
    private readonly ColumnDefinition _played = new(), _left = new();
    private readonly Button _stop, _back, _toggle, _forward, _next, _open, _minimize, _quit;
    private readonly Icon _toggleIcon;
    private (double X, double Y) _point;
    private ScreenFit.Screen? _screen;
    private bool _closing;

    public TrayMenuWindow(Action<DesktopCommand> chosen)
    {
        _chosen = chosen;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; ShowActivated = true; SizeToContent = SizeToContent.WidthAndHeight; Title = "Mira";
        // The implicit style of Window does not reach a class of its own: its font and text mode set here.
        FontFamily = (FontFamily)FindResource("AppFont"); TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        AutomationProperties.SetName(this, "Mira");

        _picture = new Squircle { Height = 171, CornerRadius = new CornerRadius(9), Background = Palette.Get("Raised") };
        _title = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        _line = new TextBlock { FontSize = 12.5, Foreground = Palette.Get("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var words = new StackPanel { Margin = new Thickness(10, 12, 10, 0), Children = { _title, _line } };

        // The bar: what is played in the ink of the text over the line's grey, both columns sized by their share.
        var bar = new Grid { Height = 4, ColumnDefinitions = { _played, _left } };
        bar.Children.Add(new Squircle { CornerRadius = new CornerRadius(2), Background = Palette.Get("Line") });
        var fill = new Squircle { CornerRadius = new CornerRadius(2), Background = Palette.Get("Text") };
        Grid.SetColumnSpan(bar.Children[0], 2); bar.Children.Add(fill);
        _elapsed = Clock(); _total = Clock();
        var times = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_total, Dock.Right); times.Children.Add(_elapsed); times.Children.Add(_total);
        var progress = new StackPanel { Margin = new Thickness(10, 12, 10, 0), Children = { bar, times } };

        _stop = Transport("stop", 15, L("Arrêter la lecture", "Stop playback"), DesktopCommand.Stop, "Muted");
        _back = Transport("rewind", 23, L("Reculer de 10 secondes", "Back 10 seconds"), DesktopCommand.Back, "TextBody");
        _forward = Transport("forward", 23, L("Avancer de 10 secondes", "Forward 10 seconds"), DesktopCommand.Forward, "TextBody");
        _next = Transport("skip", 20, L("Épisode suivant", "Next episode"), DesktopCommand.Next, "Muted");
        _toggleIcon = new Icon { Kind = "pause", Width = 19, Height = 19 };
        _toggle = new Button { Style = (Style)FindResource("PrimaryButton"), Width = 46, Height = 46, Padding = new Thickness(0), Margin = new Thickness(14, 0, 14, 0), Content = _toggleIcon };
        Motion.SetRadius(_toggle, new CornerRadius(23));
        _toggle.Click += (_, _) => _chosen(DesktopCommand.TogglePause);
        // Stop on the left, next on the right (kept in place when absent), the three of the middle centred between them.
        var middle = new StackPanel { Orientation = Orientation.Horizontal, Children = { _back, _toggle, _forward } };
        var transport = new Grid { Margin = new Thickness(6, 8, 6, 2) };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Grid.SetColumn(middle, 2); Grid.SetColumn(_next, 4);
        transport.Children.Add(_stop); transport.Children.Add(middle); transport.Children.Add(_next);

        _card = new StackPanel { Margin = new Thickness(1, 1, 1, 3), Children = { _picture, words, progress, transport } };
        _rule = new Border { Style = (Style)FindResource("PlayerMenuRule") };
        _open = Row("mark", 18, L("Ouvrir Mira", "Open Mira"), DesktopCommand.Show, strong: true);
        _minimize = Row("minus", 17, L("Réduire dans la zone de notification", "Minimize to the notification area"), DesktopCommand.Hide);
        _quit = Row("exit", 17, L("Quitter Mira", "Quit Mira"), DesktopCommand.Quit);
        var lines = new StackPanel { Children = { _card, _rule, _open, _minimize, _quit } };
        KeyboardNavigation.SetDirectionalNavigation(lines, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetTabNavigation(lines, KeyboardNavigationMode.Cycle);

        // « Écrin »: the surface of the menus, its edge, rounded 14, rows reaching 6 px from it.
        _menu = new Squircle { Style = (Style)FindResource("PopoverWindow"), CornerRadius = Palette.Menu, Padding = new Thickness(6), Width = TrayMenuPlan.Width, Child = lines };
        TextElement.SetForeground(_menu, Palette.Get("Text"));
        // Its shadow, a shape of its own under it: an effect over the menu would draw its text without ClearType.
        var shadow = new Squircle
        {
            CornerRadius = Palette.Menu, Background = Palette.Get("Bg"), IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 7, Direction = 270, Opacity = .6 }
        };
        Content = new Grid { Margin = new Thickness(Room), Children = { shadow, _menu } };

        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
        // A click on the shadow, beside the menu: as a click elsewhere.
        PreviewMouseDown += (_, e) => { if (e.OriginalSource is DependencyObject source && !IsWithin(source, _menu)) Dismiss(); };
        // Its size changes with what it shows (a playback that ends, an episode to go to): kept at its place.
        SizeChanged += (_, _) => { if (IsVisible) Place(); };
    }

    /// <summary>Shown now.</summary>
    public bool IsOpen => IsVisible;
    /// <summary>The menu's visible box on the screen (its shadow left out), in device pixels.</summary>
    public ScreenFit.Pixels Shown
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect) || _screen is not { } screen) return default;
            var room = Math.Round(Room * screen.Scale);
            return new(rect.Left + room, rect.Top + room, rect.Right - rect.Left - 2 * room, rect.Bottom - rect.Top - 2 * room);
        }
    }

    /// <summary>Opens the menu for a click at <paramref name="point"/> (device pixels) on <paramref name="screen"/>.</summary>
    public void Open((double X, double Y) point, ScreenFit.Screen screen, TrayCard? card, bool windowShown)
    {
        if (_closing) return;
        _point = point; _screen = screen;
        Refresh(card, windowShown);
        var handle = new WindowInteropHelper(this).EnsureHandle();
        // Put on its screen before it shows (WPF scales it there), then once more at its size there.
        Place();
        Show(); Activate();
        // Without the foreground, a click elsewhere would not take it away.
        SetForegroundWindow(handle);
        UpdateLayout(); Place();
        (_card.Visibility == Visibility.Visible && _toggle.IsEnabled ? _toggle : _open).Focus();
    }

    /// <summary>Shows the state of now: the card during a playback, « Next episode » and « Minimize » only when they apply.</summary>
    public void Refresh(TrayCard? card, bool windowShown)
    {
        var parts = TrayMenuPlan.For(card is not null, card?.CanNext == true, windowShown);
        _card.Visibility = _rule.Visibility = parts.Card ? Visibility.Visible : Visibility.Collapsed;
        _minimize.Visibility = parts.Minimize ? Visibility.Visible : Visibility.Collapsed;
        if (card is not null)
        {
            _title.Text = card.Title; _line.Text = card.Line;
            if (!ReferenceEquals((_picture.Background as ImageBrush)?.ImageSource, card.Picture))
                _picture.Background = card.Picture is null ? Palette.Get("Raised") : new ImageBrush(card.Picture) { Stretch = Stretch.UniformToFill };
            Progress(card.Position, card.Duration);
            _toggleIcon.Kind = card.Paused ? "play" : "pause";
            AutomationProperties.SetName(_toggle, card.Paused ? L("Reprendre la lecture", "Resume playback") : L("Mettre en pause", "Pause"));
            // Absent, « Next episode » keeps its place: the three buttons of the middle stay centred.
            _next.Visibility = parts.Next ? Visibility.Visible : Visibility.Hidden;
            _stop.IsEnabled = _back.IsEnabled = _toggle.IsEnabled = _forward.IsEnabled = card.Ready;
            _next.IsEnabled = card.Ready && card.CanNext;
        }
        // The focus on a button gone (a playback stopped from here): the first line of the app takes it.
        if (IsActive && !IsKeyboardFocusWithin) _open.Focus();
    }

    /// <summary>Only the bar and its times: each second of a playback.</summary>
    public void Progress(double position, double duration)
    {
        var (share, elapsed, total) = TrayMenuPlan.Progress(position, duration);
        _played.Width = new GridLength(share, GridUnitType.Star); _left.Width = new GridLength(1 - share, GridUnitType.Star);
        _elapsed.Text = elapsed; _total.Text = total;
    }

    public void Dismiss() { if (IsVisible) Hide(); }
    /// <summary>Closed for good, with Mira's icon.</summary>
    public void Destroy() { _closing = true; Close(); }
    /// <summary>Alt+F4 only hides it: the window is made once and kept.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing) { e.Cancel = true; Dismiss(); }
        base.OnClosing(e);
    }

    /// <summary>Moves the window so that the menu sits where <see cref="TrayMenuPlan.Place"/> says, at its size on that screen.</summary>
    private void Place()
    {
        if (_screen is not { } screen) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        ((UIElement)Content).Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = ((UIElement)Content).DesiredSize;
        var scale = screen.Scale;
        var box = TrayMenuPlan.Place(_point.X, _point.Y, screen.WorkArea, Math.Round((size.Width - 2 * Room) * scale), Math.Round((size.Height - 2 * Room) * scale), scale);
        var room = Math.Round(Room * scale);
        SetWindowPos(handle, IntPtr.Zero, (int)(box.Left - room), (int)(box.Top - room), 0, 0, NoSize | NoZOrder | NoActivate | NoOwnerZOrder);
    }

    private Button Transport(string kind, double size, string name, DesktopCommand command, string ink)
    {
        var button = new Button
        {
            Style = (Style)FindResource("PlayerMenuChip"), Width = 38, Height = 38, Foreground = Palette.Get(ink),
            Content = new Icon { Kind = kind, Width = size, Height = size }
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => _chosen(command);
        return button;
    }
    private Button Row(string kind, double size, string label, DesktopCommand command, bool strong = false)
    {
        var icon = new Icon { Kind = kind, Width = size, Height = size, Foreground = Palette.Get(strong ? "Text" : "TextSecondary"), Margin = new Thickness(0, 0, 13, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = label, FontWeight = strong ? FontWeights.Bold : FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(icon, Dock.Left);
        var button = new Button { Style = (Style)FindResource("PlayerMenuItem"), Content = new DockPanel { Children = { icon, text } } };
        AutomationProperties.SetName(button, label);
        // The app's lines close the menu first: the window that comes back, or Mira leaving, takes the foreground.
        button.Click += (_, _) => { Dismiss(); _chosen(command); };
        return button;
    }
    private TextBlock Clock() => new() { FontSize = 12, Foreground = Palette.Get("Faint"), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? at = source; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (ReferenceEquals(at, ancestor)) return true;
        return false;
    }

    private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
