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
/// The Mac's trackpad gestures in the library, as in Apple's own apps: two fingers sideways turn the page back or forward
/// (when the Mac swipes between pages that way, natural scrolling or not) and the page follows them, as in Safari; three
/// fingers too when the Mac gives them that swipe; a pinch changes the size of the grids' posters (⌘+ / ⌘− / ⌘0 for the
/// keyboard); a firm press on a Force Touch trackpad opens a card's menu. Scrolling itself, momentum included, is macOS's
/// own, passed on point for point, each gesture to the page or to the row under the pointer, never both. The player has
/// its own (<see cref="PlayerPage"/>).
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<Page> _forward = [];
    private readonly TrackpadSwipe _swipe = new();
    private readonly TrackpadPinch _pinch = new();
    /// <summary>Some time after the last event of a gesture: the fingers left without momentum, a pinched size is saved.</summary>
    private readonly DispatcherTimer _gestureEnd = new() { Interval = TimeSpan.FromMilliseconds(Trackpad.Gap + 60) };
    private bool _zoomed, _forceHeld;
    private Point? _pointerAt;

    /// <summary>Called by the window once its content is built.</summary>
    private void ListenToTrackpad()
    {
        MacTrackpad.Refresh();
        CardGrid.Zoom = double.IsFinite(Settings.GridZoom) && Settings.GridZoom > 0 ? Math.Clamp(Settings.GridZoom, Trackpad.ZoomMin, Trackpad.ZoomMax) : 1;
        _slideSheet.Children.Add(new Border { Background = Ui.Brush("#070708") });
        // The shadow the page on top casts on the one under it, at its left edge.
        _slideSheet.Children.Add(new Border
        {
            Width = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-28, 0, 0, 0),
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#00000000"), 0), new GradientStop(Color.Parse("#59000000"), 1) } }
        });
        _slideSheet.RenderTransform = _sheetShift;
        _host.Children.Add(_slideDim); _host.Children.Add(_slideSheet);
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
        if (Watching || _forward.Count == 0 || Current is SettingsPage) return false;
        var next = _forward[^1]; _forward.RemoveAt(_forward.Count - 1);
        if (Current is { } current) _history.Add(current);
        Host(next); Light(next.Rail);
        _ = next.RefreshAsync();
        return true;
    }
    /// <summary>For the gestures and the self-check: a page to go back (-1) or forward (1) to.</summary>
    public bool CanGo(int way) => !Watching && Current is not SettingsPage && (way < 0 ? _history.Count > 0 : _forward.Count > 0);
    /// <summary>
    /// The way the trackpad's gesture goes, once its first points of travel decided it (until then
    /// <see cref="SwipeAxis.Undecided"/>): a row of cards takes a gesture sideways whole, and leaves the page one up or
    /// down whole, never a share of each event to each (<see cref="CardRow"/>).
    /// </summary>
    public SwipeAxis GestureAxis => _swipe.Axis;

    private void SwipeWheel(object? sender, PointerWheelEventArgs e)
    {
        // The cards under a still pointer do not lift as the page scrolls them by (Cards.Hover).
        Ui.ScrolledAt = Environment.TickCount64;
        if (Session is null || Login is not null) return;
        var (x, y) = Trackpad.Finger(e.Delta.X, e.Delta.Y, MacTrackpad.Natural);
        var time = (long)e.Timestamp;
        var phase = MacTrackpad.Phase(e);
        // A new gesture: the last one's page, still under the fingers if they rested, goes where it was going, at once.
        if (_swipe.Starts(time, phase)) { LetGo(rested: true); EndSlide(_slideCommit); }
        // Decided sideways: the page turns unless the Mac swipes otherwise, the player is up, there is no page that way,
        // or a row under the pointer can still scroll that way (at its end, the swipe turns the page, as Safari's over a
        // carousel). Only the fingers' own travel counts: the momentum of a flick never turns the page by itself.
        if (_swipe.Add(x, y, time, phase) && (!MacTrackpad.SwipesBetweenPages || Watching || InPlayer(e.Source) || !CanGo(_swipe.Way) || ScrollsSideways(e.Source, MacTrackpad.Natural ? _swipe.X : -_swipe.X)))
            _swipe.Claim();
        _gestureEnd.Stop(); _gestureEnd.Start();
        if (_swipe.Way == 0 || _swipe.Claimed) return;
        // A page turn: the whole gesture is its own, momentum included (a row of the page that comes never scrolls with it).
        e.Handled = true;
        if (_swipe.Done) return;
        if (phase is ScrollPhase.Momentum or ScrollPhase.Ended) { LetGo(rested: false); return; }
        if (_slideTop is null && _swipe.Turn > 0) StartSlide(_swipe.Way);
        if (_slideTop is not null) Follow();
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
    private void ThreeFingerSwipe(object? sender, PointerDeltaEventArgs e)
    {
        if (Session is null || Login is not null) return;
        // A positive deltaX is the swipe that goes back (as Firefox and Chromium read it).
        if (e.Delta.X > 0) Back(); else if (e.Delta.X < 0) Forward(); else return;
        e.Handled = true;
    }

    // —— The page that follows the fingers ——
    /// <summary>
    /// A page turn under way, as Safari's: the page on top slides with the fingers (back: the page shown, off to the right;
    /// forward: the next one, in from the right), the one under it comes a third as fast from the left, dimmed, the top
    /// one's edge casting a shadow on it. Nothing changes in the history until the turn is done (<see cref="EndSlide"/>).
    /// </summary>
    private Page? _slideTop, _slideUnder;
    private int _slideWay;
    /// <summary>Where the page on top is: 0 in place, 1 off the window's right edge.</summary>
    private double _slideAt;
    /// <summary>Let go: where the page on top glides to, from where, and whether the page turns.</summary>
    private double? _slideGoal;
    private double _slideFrom, _slideDuration;
    private bool _slideCommit, _slideRunning;
    private TimeSpan? _slideClock;
    private readonly TranslateTransform _topShift = new(), _underShift = new(), _sheetShift = new();
    /// <summary>Behind the page on top, which has no background of its own: the window's, and its shadow.</summary>
    private readonly Panel _slideSheet = new() { IsHitTestVisible = false, IsVisible = false };
    private readonly Border _slideDim = new() { Background = Brushes.Black, IsHitTestVisible = false, IsVisible = false };
    /// <summary>For the self-check: a page turn on screen.</summary>
    public bool Sliding => _slideTop is not null;

    private void StartSlide(int way)
    {
        if (Current is not { } current || !CanGo(way)) return;
        var other = way < 0 ? _history[^1] : _forward[^1];
        if (!_host.Children.Contains(other)) _host.Children.Add(other);
        (_slideTop, _slideUnder) = way < 0 ? (current, other) : (other, current);
        _slideWay = way; _slideGoal = null;
        _slideUnder.ZIndex = 1; _slideDim.ZIndex = 2; _slideSheet.ZIndex = 3; _slideTop.ZIndex = 4;
        _slideUnder.RenderTransform = _underShift; _slideTop.RenderTransform = _topShift;
        _slideDim.IsVisible = _slideSheet.IsVisible = true;
        Place(way < 0 ? 0 : 1);
        other.IsVisible = true;
    }
    /// <summary>The page on top where the fingers took it: one point of the page for one point of travel.</summary>
    private void Follow()
    {
        var width = _host.Bounds.Width;
        if (width <= 0) return;
        // Avalonia's wheel units are 50 points of the trackpad.
        var share = Math.Min(1, _swipe.Turn * 50 / width);
        Place(_slideWay < 0 ? share : 1 - share);
    }
    private void Place(double at)
    {
        var width = _host.Bounds.Width;
        _slideAt = Math.Clamp(at, 0, 1);
        _topShift.X = _sheetShift.X = _slideAt * width;
        _underShift.X = -(1 - _slideAt) * width / 3;
        _slideDim.Opacity = .3 * (1 - _slideAt);
    }
    /// <summary>The fingers left (or rested): the page glides to its turn, or back where it was.</summary>
    private void LetGo(bool rested)
    {
        var way = _swipe.Release(rested);
        if (_slideTop is null || _slideGoal is not null) return;
        _slideCommit = way != 0;
        // Back done: the page shown is off to the right; forward done: the next one in place. Otherwise the other way.
        var goal = _slideWay < 0 == _slideCommit ? 1 : 0;
        _slideGoal = goal; _slideFrom = _slideAt; _slideClock = null;
        var distance = Math.Abs(goal - _slideAt);
        if (distance < .002 || MacMotion.Reduced) { EndSlide(_slideCommit); return; }
        // Quicker over a short way, never slower than a third of a second.
        _slideDuration = Math.Clamp(distance * 420, 140, 320);
        if (_slideRunning) return;
        _slideRunning = true;
        RequestAnimationFrame(SlideFrame);
    }
    private void SlideFrame(TimeSpan now)
    {
        if (_slideGoal is not { } goal) { _slideRunning = false; return; }
        _slideClock ??= now;
        var t = Math.Min(1, (now - _slideClock.Value).TotalMilliseconds / _slideDuration);
        // Eased out, as macOS's own: fast from the fingers, slowing into place.
        Place(_slideFrom + (goal - _slideFrom) * (1 - Math.Pow(1 - t, 3)));
        if (t < 1) { RequestAnimationFrame(SlideFrame); return; }
        _slideRunning = false;
        EndSlide(_slideCommit);
    }
    /// <summary>
    /// The page turn over: done (<paramref name="turn"/>, the history moves as by ⌘[ or ⌘]), or not (the page that was
    /// coming hidden again). Also when a page shows otherwise meanwhile (<see cref="Host"/>): nothing turns.
    /// </summary>
    private void EndSlide(bool turn)
    {
        if (_slideTop is not { } top || _slideUnder is not { } under) return;
        var way = _slideWay;
        var other = way < 0 ? under : top;
        _slideTop = _slideUnder = null; _slideGoal = null;
        foreach (var page in new[] { top, under }) { page.RenderTransform = null; page.ZIndex = 0; }
        _slideDim.IsVisible = _slideSheet.IsVisible = false;
        if (turn && CanGo(way)) { if (way < 0) Back(); else Forward(); }
        else if (other != Current) other.IsVisible = false;
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
    /// <summary>
    /// The grids of the page take a new poster size, the page kept where it was; false when nothing changed. Their posters
    /// change size only with their number on a line: until then, nothing is laid out again; then each poster on screen
    /// glides from where it was to its new place and size, as in Apple's Photos.
    /// </summary>
    public bool ZoomGrids(double zoom)
    {
        if (Current is not { } page || Math.Abs(zoom - CardGrid.Zoom) < 1e-4) return false;
        var grids = page.GetVisualDescendants().OfType<CardGrid>().ToList();
        if (grids.Count == 0) return false;
        if (grids.All(x => x.ColumnsAt(zoom) == x.ColumnsAt(CardGrid.Zoom))) { CardGrid.Zoom = zoom; return true; }
        var scroll = grids[0].FindAncestorOfType<ScrollViewer>();
        var share = scroll is { Extent.Height: > 0 } ? scroll.Offset.Y / scroll.Extent.Height : 0;
        // Where each poster shows now (a glide still under way included), in the page's scroller.
        var frame = (Visual?)scroll ?? page;
        var before = new Dictionary<Control, Rect>();
        foreach (var card in grids.SelectMany(x => x.Children)) if (Glided(card) is var poster && Shown(poster, frame) is { } rect) before[poster] = rect;
        Glides.Stop();
        CardGrid.Zoom = zoom;
        foreach (var grid in grids) grid.InvalidateMeasure();
        UpdateLayout();
        if (scroll is not null) { scroll.Offset = new Vector(scroll.Offset.X, share * scroll.Extent.Height); UpdateLayout(); }
        if (MacMotion.Reduced) return true;
        var moves = new List<(Control Card, Matrix From)>();
        foreach (var (poster, was) in before)
        {
            if (Shown(poster, frame) is not { } now || now.Width <= 0) continue;
            var scale = was.Width / now.Width;
            moves.Add((poster, Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(was.X - now.X, was.Y - now.Y)));
        }
        Glides.Start(this, moves);
        return true;
    }
    /// <summary>
    /// What glides of a card: its content, never the button itself. Avalonia's theme gives a button its own transition on
    /// <see cref="Visual.RenderTransformProperty"/> (75 ms, for its press), which shows any other transform as none while
    /// it runs: set at each frame, the button's would never show, and its last one would linger after the glide.
    /// </summary>
    public static Control Glided(Control card) => card is ContentControl { Content: Control content } ? content : card;
    /// <summary>A poster's place in <paramref name="frame"/>, its glide included; null when off screen.</summary>
    private static Rect? Shown(Control card, Visual frame)
    {
        if (card.TransformToVisual(frame) is not { } matrix) return null;
        var rect = new Rect(card.Bounds.Size).TransformToAABB(matrix);
        return rect.Intersects(new Rect(frame.Bounds.Size)) ? rect : null;
    }
    /// <summary>The posters of a pinch, each eased from its old place to its new one, frame by frame.</summary>
    private static class Glides
    {
        private static List<(Control Poster, Matrix From)> _moves = [];
        private static TimeSpan? _start;
        private static int _run;
        private const double Duration = 240;
        /// <summary>The frames of the last glide: for the self-check.</summary>
        public static int Frames { get; private set; }
        public static void Stop()
        {
            foreach (var (poster, _) in _moves) poster.RenderTransform = null;
            _moves = []; _run++;
        }
        public static void Start(TopLevel top, List<(Control Poster, Matrix From)> moves)
        {
            _moves = moves; _start = null; Frames = 0;
            var run = ++_run;
            foreach (var (poster, from) in moves) { poster.RenderTransformOrigin = RelativePoint.TopLeft; poster.RenderTransform = new MatrixTransform(from); }
            void Frame(TimeSpan now)
            {
                if (run != _run) return;
                _start ??= now; Frames++;
                var t = Math.Min(1, (now - _start.Value).TotalMilliseconds / Duration);
                var left = Math.Pow(1 - t, 3);
                foreach (var (poster, from) in _moves)
                    poster.RenderTransform = t >= 1 ? null : new MatrixTransform(new Matrix(1 + (from.M11 - 1) * left, 0, 0, 1 + (from.M22 - 1) * left, from.M31 * left, from.M32 * left));
                if (t < 1) top.RequestAnimationFrame(Frame); else _moves = [];
            }
            top.RequestAnimationFrame(Frame);
        }
    }
    /// <summary>The frames the last pinch's posters glided through: for the self-check.</summary>
    public static int GlideFrames => Glides.Frames;
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
        // No momentum came: the fingers stood still, then left (macOS's « ended » never reaches Mira).
        LetGo(rested: true);
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
