using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// The mini-player, as on Windows (MainWindow.MiniPlayer.cs): « Retour », Échap or I shrink the player against the window's
// right edge, the library usable around it. Tucked away by itself, a sliver of the video showing, it slides out while the
// pointer rests on it and back once the pointer has left (MiniDock); dragged up or down, it rests at that height. Its title,
// « Agrandir » and « Fermer » show while the pointer is on it; a click on the picture or I brings the whole player back.
public sealed partial class PlayerPage
{
    private const double MiniWidth = MiniDock.Width, MiniHeight = MiniDock.Height, MiniRadius = 12, MiniGap = MiniDock.Gap;
    private Border _frame = null!;
    private Grid _miniChrome = null!;
    private readonly TextBlock _miniTitle = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Icon _miniPauseIcon = new("pause", 19);
    private readonly ProgressLine _miniProgress = new() { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private bool _mini, _miniDragging, _miniTucked;
    private Point? _miniPress;
    /// <summary>Where it rests down the window, as a share of the way from under the title bar to the bottom.</summary>
    private double _miniShare = 1;
    private readonly MiniDock _dock = new();
    private readonly Stopwatch _dockClock = Stopwatch.StartNew();
    private readonly DispatcherTimer _dockTimer = new() { Interval = MiniDock.Poll };
    /// <summary>The pointer in the window, in the coordinates of the page the player is laid out on; null outside it.</summary>
    private Point? _pointer;
    /// <summary>A click on the mini-player brought the whole player back: the presses of the same double click go nowhere.</summary>
    private long _swallowPressesUntil;
    private static readonly Easing OutEasing = Spline(MiniDock.OutCurve), BackEasing = Spline(MiniDock.BackCurve);
    private static SplineEasing Spline((double X1, double Y1, double X2, double Y2) c) => new(c.X1, c.Y1, c.X2, c.Y2);
    private static readonly ITransform Rest = TransformOperations.Parse("translateX(0px)");
    /// <summary>Tucked away against the edge, a sliver of it showing (or on its way there).</summary>
    public bool IsMiniTucked => _mini && _miniTucked;
    /// <summary>The pointer as the self-check puts it; null, the real one.</summary>
    public Func<bool>? MiniPointerForCheck { get; set; }
    /// <summary>In a corner of the window, the library usable around it.</summary>
    public bool IsMini => _mini;

    /// <summary>The frame that rounds the mini-player, and its own controls over the picture.</summary>
    private Control BuildMini(Control content)
    {
        var pip = Ui.IconButton("pip", L("Image dans l’image : la vidéo dans sa propre fenêtre, au-dessus des autres", "Picture in picture: the video in its own window, above the others"), () => _ = EnterPipAsync(), "player", 17, L("Image dans l’image", "Picture in picture"));
        var expand = Ui.IconButton("expand", L("Agrandir le lecteur · I", "Expand the player · I"), () => SetMini(false), "player", 17);
        var close = Ui.IconButton("close", L("Fermer le mini-lecteur", "Close the mini player"), () => _ = _shell.StopPlaybackAsync(), "player", 17);
        foreach (var button in new[] { pip, expand, close }) { button.Width = button.Height = 32; button.CornerRadius = new CornerRadius(16); }
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), VerticalAlignment = VerticalAlignment.Top, Height = 52 };
        top.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#B0000000"), 0), new GradientStop(Color.Parse("#00000000"), 1) }
        };
        _miniTitle.Margin = new Thickness(14, 0, 8, 6);
        top.Children.Add(_miniTitle);
        pip.Margin = new Thickness(0, 0, 2, 6); expand.Margin = new Thickness(0, 0, 0, 6); close.Margin = new Thickness(2, 0, 8, 6);
        Grid.SetColumn(pip, 1); top.Children.Add(pip); Grid.SetColumn(expand, 2); top.Children.Add(expand); Grid.SetColumn(close, 3); top.Children.Add(close);
        var play = new Button { Content = _miniPauseIcon, Classes = { "player" }, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Ui.Brush("#80000000") };
        play.Click += (_, _) => TogglePause();
        Speak(play, _miniPauseIcon, kind => kind == "play" ? L("Lecture", "Play") : L("Pause", "Pause"), L("Espace", "Space"));
        _pauseIcon.PropertyChanged += (_, e) => { if (e.Property == Icon.KindProperty) _miniPauseIcon.Kind = _pauseIcon.Kind; };
        _miniChrome = new Grid { IsVisible = false, Opacity = 0, Children = { top, play } };
        _miniChrome.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }];
        var frame = new Grid { Children = { content, _miniChrome, _miniProgress } };
        _miniProgress.IsVisible = false;
        _frame = new Border { Child = frame };
        return _frame;
    }
    /// <summary>Shrinks the player to its corner, or brings it back over the whole window.</summary>
    public void SetMini(bool mini)
    {
        // The video in the small window: expanding brings it back first (PlayerPage.Pip.cs), which comes here again.
        if (!mini && _pip is not null && !_pipMoving) { _ = ReturnFromPipAsync(); return; }
        if (mini == _mini || _stopped && mini) return;
        if (mini && _shell.WindowState == WindowState.FullScreen && !_shell.InCouchMode) ToggleFullscreen();
        foreach (var button in new[] { _tracks, _more, _episodesButton }) (button.Flyout as Avalonia.Controls.Flyout)?.Hide();
        _mini = mini; _miniDragging = _miniTucked = false; _miniPress = null; Transitions = null; RenderTransform = null;
        _chrome.IsVisible = _skipHost.IsVisible = !mini;
        _miniChrome.IsVisible = _miniProgress.IsVisible = mini;
        // Clipped only in its corner, to round it: over the whole window the video's OpenGL surface goes to the screen
        // as it did before the mini-player (0.8.6), with nothing between.
        _frame.ClipToBounds = mini;
        _frame.CornerRadius = new CornerRadius(mini ? MiniRadius : 0);
        _frame.BoxShadow = mini ? BoxShadows.Parse("0 12 36 0 #99000000") : default;
        _frame.BorderThickness = new Thickness(mini ? 1 : 0); _frame.BorderBrush = Ui.Brush("#30FFFFFF");
        if (mini) { PlaceMini(); RenderTransform = Rest; _dock.Arrive(_dockClock.Elapsed); StartDock(); }
        else { _dockTimer.Stop(); Width = Height = double.NaN; Margin = default; HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch; }
        _miniTitle.Text = _session.Item is { } item ? item.Type == "Episode" ? $"{item.DisplayTitle} · {_subtitle.Text}" : item.DisplayTitle : "";
        Cursor = Cursor.Default;
        ShowGroup(); UpdateSkip(); ShowControls();
        _shell.MiniChanged(this);
    }
    /// <summary>Against the right edge, clear of the title bar, at the height it was left at.</summary>
    private void PlaceMini()
    {
        Width = MiniWidth; Height = MiniHeight;
        HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, MiniDock.Top(_miniShare, MiniTop, ShellHeight), MiniGap, 0);
    }
    private double MiniTop => _shell.TitleBarInset + 12;
    /// <summary>The height of what the player is laid out on (the window under its title bar's inset, or all of it).</summary>
    private double ShellHeight => Parent is Control parent && parent.Bounds.Height > 0 ? parent.Bounds.Height : _shell.Bounds.Height;
    private double ShellWidth => Parent is Control parent && parent.Bounds.Width > 0 ? parent.Bounds.Width : _shell.Bounds.Width;
    /// <summary>Tucked away, it moves right by all but the sliver; out, it rests where layout puts it.</summary>
    private static double TuckShift => MiniDock.Left(false, 0) - MiniDock.Left(true, 0);

    /// <summary>The self-check: the dock at work with the pointer it puts, as just shrunk.</summary>
    public void DockForCheck(Func<bool>? pointer)
    {
        MiniPointerForCheck = pointer;
        if (pointer is null) { _dockTimer.Stop(); return; }
        _dock.Arrive(_dockClock.Elapsed); StartDock();
    }
    private void StartDock()
    {
        // Only in use, and never under the other checks: they find the mini-player out, as they leave it.
        if (!_shell.IsCheckRun || MiniPointerForCheck is not null) _dockTimer.Start();
    }
    private void DockTick()
    {
        if (!_mini || _stopped || _pip is not null || !IsVisible) { _dockTimer.Stop(); return; }
        var held = _miniDragging || _miniPress is not null;
        if (_dock.Update(_dockClock.Elapsed, PointerOnMini(), held)) SlideMini(_dock.Out);
    }
    /// <summary>The pointer on what shows of the mini-player (<see cref="MiniDock.Catches"/>).</summary>
    private bool PointerOnMini()
    {
        if (MiniPointerForCheck is { } check) return check();
        if (_pointer is not { } point) return false;
        return MiniDock.Catches(point.X, point.Y, _miniTucked, MiniDock.Top(_miniShare, MiniTop, ShellHeight), ShellWidth);
    }
    private void ShellPointerMoved(object? sender, PointerEventArgs e)
    {
        _pointer = Parent is Visual parent ? e.GetPosition(parent) : null;
        // At once, rather than at the clock's next tick: what the pointer does is felt under it.
        if (_mini && _dockTimer.IsEnabled) DockTick();
    }
    private void ShellPointerExited(object? sender, PointerEventArgs e) => _pointer = null;
    /// <summary>Listens to the pointer anywhere in the window while the player is in it (the sliver is too thin to wait for it to arrive).</summary>
    private void FollowPointer(bool on)
    {
        _shell.RemoveHandler(PointerMovedEvent, ShellPointerMoved); _shell.PointerExited -= ShellPointerExited; _shell.SizeChanged -= ShellResized;
        if (!on) return;
        _shell.AddHandler(PointerMovedEvent, ShellPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _shell.PointerExited += ShellPointerExited; _shell.SizeChanged += ShellResized;
    }
    private void ShellResized(object? sender, SizeChangedEventArgs e) { if (_mini && !_miniDragging) Dispatcher.UIThread.Post(PlaceMini, DispatcherPriority.Loaded); }
    /// <summary>Slides out, or back against the edge, on Avalonia's compositor: retaken from where it is when the pointer changes its mind.</summary>
    private void SlideMini(bool shown)
    {
        _miniTucked = !shown;
        if (!shown) ShowMiniControls(false);
        Transitions = MacMotion.Reduced ? null : [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(shown ? MiniDock.OutMs : MiniDock.BackMs), Easing = shown ? OutEasing : BackEasing }];
        RenderTransform = shown ? Rest : TransformOperations.Parse(FormattableString.Invariant($"translateX({TuckShift}px)"));
        if (shown && PointerOnMini()) ShowControls();
    }
    private void ShowMiniControls(bool shown) => _miniChrome.Opacity = shown && !_miniTucked ? 1 : 0;
    private void MiniProgress()
    {
        if (!_mini) return;
        _miniProgress.Value = _session.Duration > 0 ? _session.Position / _session.Duration : 0;
        _miniProgress.InvalidateVisual();
    }
    /// <summary>A press on the picture: a click expands, a drag moves it up or down the edge.</summary>
    /// <param name="twice">The second of two presses on the picture (PlayerPage's own count, not Avalonia's).</param>
    private void MiniPressed(PointerPressedEventArgs e, bool twice)
    {
        if (twice) { _click.Stop(); _miniPress = null; ExpandMiniByClick(); return; }
        _miniPress = e.GetPosition(_shell); e.Pointer.Capture(_videoHost);
    }
    private void MiniMoved(PointerEventArgs e)
    {
        if (_miniPress is not { } press) return;
        var point = e.GetPosition(_shell);
        if (!_miniDragging && Math.Abs(point.X - press.X) + Math.Abs(point.Y - press.Y) < 6) return;
        if (!_miniDragging) { _miniDragging = true; _click.Stop(); _miniTucked = false; Transitions = null; ShowMiniControls(false); }
        // Up and down freely; across, only towards the edge it rests against, and not past it.
        var x = Math.Clamp(point.X - press.X, -(ShellWidth - MainWindow.RailWidth - 2 * MiniGap - MiniWidth), TuckShift);
        var top = MiniDock.Top(_miniShare, MiniTop, ShellHeight);
        var y = Math.Clamp(point.Y - press.Y, MiniTop - top, Math.Max(MiniTop, ShellHeight - MiniGap - MiniHeight) - top);
        RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translate({x}px, {y}px)"));
    }
    private void MiniReleased(PointerReleasedEventArgs e)
    {
        if (_miniPress is not { } press) return;
        _miniPress = null; e.Pointer.Capture(null);
        if (!_miniDragging) { ExpandMiniByClick(); return; }
        _miniDragging = false;
        // Let go: against the right edge again, at that height, gliding there from where it was let go.
        var point = e.GetPosition(_shell);
        var top = MiniDock.Top(_miniShare, MiniTop, ShellHeight);
        var x = Math.Clamp(point.X - press.X, -(ShellWidth - MainWindow.RailWidth - 2 * MiniGap - MiniWidth), TuckShift);
        var landed = Math.Clamp(top + point.Y - press.Y, MiniTop, Math.Max(MiniTop, ShellHeight - MiniGap - MiniHeight));
        _miniShare = MiniDock.Share(landed, MiniTop, ShellHeight);
        var settled = MiniDock.Top(_miniShare, MiniTop, ShellHeight);
        PlaceMini();
        RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translate({x}px, {landed - settled}px)"));
        _dock.Show(_dockClock.Elapsed);
        Dispatcher.UIThread.Post(() => { if (_mini && !_miniDragging) SlideMini(true); }, DispatcherPriority.Render);
    }
    private void ExpandMiniByClick()
    {
        _swallowPressesUntil = Environment.TickCount64 + 500;
        SetMini(false);
    }
    /// <summary>The second press of a double click lands on the whole player: it goes nowhere.</summary>
    private bool SwallowsPress() => Environment.TickCount64 < _swallowPressesUntil;
}
