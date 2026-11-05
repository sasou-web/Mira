using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// The Mac's trackpad gestures in the library, as in Apple's own apps: two fingers sideways go back and forward (when
/// the Mac swipes between pages that way, natural scrolling or not), three fingers too when the Mac gives them that
/// swipe, a pinch changes the size of the grids' posters (⌘+ / ⌘− / ⌘0 for the keyboard), a firm press on a Force Touch
/// trackpad opens a card's menu. Scrolling itself, momentum included, is macOS's own, passed on point for point. The
/// player has its own (<see cref="PlayerPage"/>).
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<Page> _forward = [];
    private readonly TrackpadSwipe _swipe = new();
    private readonly TrackpadPinch _pinch = new();
    /// <summary>Some time after the last event of a gesture: the hint leaves, a pinched size is saved.</summary>
    private readonly DispatcherTimer _gestureEnd = new() { Interval = TimeSpan.FromMilliseconds(Trackpad.Gap + 60) };
    private readonly Icon _swipeIcon = new("back", 22) { Foreground = Ui.Ink };
    private readonly TranslateTransform _swipeShift = new();
    private Border? _swipeHint;
    private bool _zoomed, _forceHeld;
    private Point? _pointerAt;

    /// <summary>Called by the window once its content is built.</summary>
    private void ListenToTrackpad()
    {
        MacTrackpad.Refresh();
        CardGrid.Zoom = double.IsFinite(Settings.GridZoom) && Settings.GridZoom > 0 ? Math.Clamp(Settings.GridZoom, Trackpad.ZoomMin, Trackpad.ZoomMax) : 1;
        _swipeHint = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Background = Ui.Brush("#F22C2C31"), BorderBrush = Ui.Brush("#3D3D43"), BorderThickness = new Thickness(1),
            Child = _swipeIcon, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false, RenderTransform = _swipeShift,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }]
        };
        Grid.SetColumnSpan(_swipeHint, 2); _root.Children.Add(_swipeHint);
        _gestureEnd.Tick += (_, _) => GestureEnded();
        AddHandler(PointerWheelChangedEvent, SwipeWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(Gestures.PointerTouchPadGestureMagnifyEvent, Magnified, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(Gestures.PointerTouchPadGestureSwipeEvent, ThreeFingerSwipe, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, e) => _pointerAt = e.GetPosition(this), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, e) => { _pointerAt = e.GetPosition(this); _forceHeld = false; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // The release after a firm press: never a click as well, which would open the title under the menu.
        AddHandler(PointerReleasedEvent, (_, e) => { if (!_forceHeld) return; _forceHeld = false; e.Handled = true; e.Pointer.Capture(null); }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Activated += (_, _) => MacTrackpad.Refresh();
        MacTrackpad.Failed = ex => ErrorLog.Append(Profile.DirectoryPath, ex);
        MacTrackpad.ForceClicked += ForceClicked;
        Closed += (_, _) => MacTrackpad.ForceClicked -= ForceClicked;
        MacTrackpad.ListenForForceClick();
    }

    // —— Back and forward ——
    /// <summary>The page left by <see cref="Back"/>, shown again (⌘], a swipe the other way).</summary>
    public bool Forward()
    {
        if (Watching || _forward.Count == 0) return false;
        var next = _forward[^1]; _forward.RemoveAt(_forward.Count - 1);
        if (Current is { } current) _history.Add(current);
        Host(next); Light(next.Rail);
        _ = next.RefreshAsync();
        return true;
    }
    /// <summary>For the hint and the self-check: a page to go back (-1) or forward (1) to.</summary>
    public bool CanGo(int way) => !Watching && (way < 0 ? _history.Count > 0 : _forward.Count > 0);

    private void SwipeWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Session is null || Login is not null) return;
        var (x, y) = Trackpad.Finger(e.Delta.X, e.Delta.Y, MacTrackpad.Natural);
        var time = (long)e.Timestamp;
        // Decided sideways: the page changes unless the Mac swipes otherwise, the player is up, or a row under the pointer
        // can still scroll that way (at its end, the swipe goes back, as Safari's over a carousel).
        if (_swipe.Add(x, y, time) && (!MacTrackpad.SwipesBetweenPages || Watching || InPlayer(e.Source) || ScrollsSideways(e.Source, MacTrackpad.Natural ? _swipe.X : -_swipe.X)))
            _swipe.Claim();
        ShowSwipe(_swipe.Progress);
        _gestureEnd.Stop(); _gestureEnd.Start();
        var way = _swipe.Swiped();
        if (way != 0 && CanGo(way)) { if (way < 0) Back(); else Forward(); }
    }
    private bool InPlayer(object? source) => Player is { } player && source is Visual visual && (visual == player || player.IsVisualAncestorOf(visual));
    /// <summary>A scroller under the pointer that can still move with the content going right (positive) or left.</summary>
    private static bool ScrollsSideways(object? source, double contentRight)
    {
        if (source is not Visual visual) return false;
        foreach (var scroll in visual.GetSelfAndVisualAncestors().OfType<ScrollViewer>())
        {
            if (scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            var maximum = scroll.Extent.Width - scroll.Viewport.Width;
            if (maximum <= 1) continue;
            if (contentRight > 0 ? scroll.Offset.X > 1 : scroll.Offset.X < maximum - 1) return true;
        }
        return false;
    }
    /// <summary>A round arrow slides in from the side the page will come from, lit once letting go would not stop it.</summary>
    private void ShowSwipe(double progress)
    {
        if (_swipeHint is null) return;
        var way = progress > 0 ? -1 : 1;
        if (progress == 0 || !CanGo(way)) { _swipeHint.Opacity = 0; return; }
        var amount = Math.Abs(progress);
        _swipeIcon.Kind = way < 0 ? "back" : "next";
        _swipeHint.HorizontalAlignment = way < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        // From just outside the page to 20 points inside it (beside the rail on the left).
        _swipeShift.X = way < 0 ? RailWidth - 48 + amount * 68 : 48 - amount * 68;
        var lit = amount >= 1;
        _swipeHint.Background = lit ? Brushes.White : Ui.Brush("#F22C2C31");
        _swipeIcon.Foreground = lit ? Ui.Brush("#0D0D0F") : Ui.Ink;
        _swipeHint.Opacity = Math.Min(1, .25 + amount);
    }
    private void ThreeFingerSwipe(object? sender, PointerDeltaEventArgs e)
    {
        if (Session is null || Login is not null) return;
        // A positive deltaX is the swipe that goes back (as Firefox and Chromium read it).
        if (e.Delta.X > 0) Back(); else if (e.Delta.X < 0) Forward(); else return;
        e.Handled = true;
    }

    // —— Pinch ——
    private void Magnified(object? sender, PointerDeltaEventArgs e)
    {
        if (Session is null || Login is not null) return;
        _pinch.Add(e.Delta.X, (long)e.Timestamp);
        _gestureEnd.Stop(); _gestureEnd.Start();
        e.Handled = true;
        if (Player is { } player && (Watching || InPlayer(e.Source)))
        {
            if (_pinch.Pinched() is var way and not 0) player.Pinched(way);
            return;
        }
        if (ZoomGrids(Trackpad.Zoom(CardGrid.Zoom, e.Delta.X))) _zoomed = true;
    }
    /// <summary>The grids of the page take a new poster size, the page kept where it was; false when nothing changed.</summary>
    public bool ZoomGrids(double zoom)
    {
        if (Current is not { } page || Math.Abs(zoom - CardGrid.Zoom) < 1e-4) return false;
        var grids = page.GetVisualDescendants().OfType<CardGrid>().ToList();
        if (grids.Count == 0) return false;
        var scroll = grids[0].FindAncestorOfType<ScrollViewer>();
        var share = scroll is { Extent.Height: > 0 } ? scroll.Offset.Y / scroll.Extent.Height : 0;
        CardGrid.Zoom = zoom;
        foreach (var grid in grids) grid.InvalidateMeasure();
        if (scroll is not null) Dispatcher.UIThread.Post(() => scroll.Offset = new Vector(scroll.Offset.X, share * scroll.Extent.Height), DispatcherPriority.Loaded);
        return true;
    }
    /// <summary>⌘+, ⌘− and ⌘0: the pinch for the keyboard.</summary>
    private bool ZoomKey(KeyEventArgs e)
    {
        double? zoom = e.Key switch
        {
            Key.OemPlus or Key.Add => Trackpad.Zoom(CardGrid.Zoom, .15),
            Key.OemMinus or Key.Subtract => Trackpad.Zoom(CardGrid.Zoom, -.13),
            Key.D0 or Key.NumPad0 => 1,
            _ => null
        };
        if (zoom is not { } chosen) return false;
        if (ZoomGrids(chosen)) SaveZoom();
        return true;
    }
    private void SaveZoom() { Settings.GridZoom = CardGrid.Zoom; Profile.SaveSettings(Settings); }
    private void GestureEnded()
    {
        _gestureEnd.Stop();
        if (_swipeHint is not null) _swipeHint.Opacity = 0;
        if (_zoomed) { _zoomed = false; SaveZoom(); }
    }

    // —— Force click ——
    /// <summary>A firm press on a card or an episode opens its menu, as a right click (the system's « look up » has nothing to look up here).</summary>
    private void ForceClicked()
    {
        if (!IsActive || Watching || _pointerAt is not { } at || this.InputHitTest(at) is not Visual under) return;
        foreach (var control in under.GetSelfAndVisualAncestors().OfType<Control>())
        {
            if (control.ContextMenu is { } menu) menu.Open(control);
            else if (control.ContextFlyout is { } flyout) flyout.ShowAt(control);
            else continue;
            _forceHeld = true;
            return;
        }
    }
}
