using Avalonia;
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
using static Mira.Core.Lang;
using CoreGrid = Mira.Core.CardGrid;

namespace Mira.Mac.Views;

/// <summary>
/// « Mode canapé » (<see cref="Couch"/>): a Mac plugged into the TV becomes its player. The window full screen, the pages
/// laid out as on a 1280 × 720 window and drawn larger (the player keeps its own size: its picture is mpv's), the focus
/// always shown, the arrow keys going from control to control anywhere on the screen, Backspace stepping back, and a
/// game controller paired with this Mac read as those keys.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>« Mode canapé » on (Réglages → Apparence).</summary>
    public bool InCouchMode => Settings.CouchMode;
    /// <summary>The pages' zoom: <see cref="Couch.Zoom"/> in couch mode, else 1.</summary>
    public double CouchZoom { get; private set; } = 1;
    private readonly DispatcherTimer _pad = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly PadReader _padReader = new();
    private readonly System.Diagnostics.Stopwatch _padClock = System.Diagnostics.Stopwatch.StartNew();
    private bool _padOffered, _padConnected;
    /// <summary>A controller is connected (read at the last look): for the self-check.</summary>
    public bool PadConnected => _padConnected;
    private WindowState _beforeCouch = WindowState.Normal;
    /// <summary>
    /// The focus seen from the couch: a white ring drawn over everything around what has it, following it as the page
    /// scrolls (the Fluent theme's controls draw no focus adorner of their own).
    /// </summary>
    private readonly Border _couchRing = new() { BorderBrush = new SolidColorBrush(Color.Parse("#F5F5F7")), BorderThickness = new Thickness(3), IsHitTestVisible = false, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    /// <summary>Where the ring is drawn, in the window: for the self-check.</summary>
    public Rect? CouchRing => _couchRing.IsVisible ? new Rect(_couchRing.Margin.Left, _couchRing.Margin.Top, _couchRing.Width, _couchRing.Height) : null;

    private void InitializeCouch()
    {
        // After the sheets' and the player's own handler: the player keeps its arrows, a sheet its Escape.
        AddHandler(KeyDownEvent, CouchKeyDown, RoutingStrategies.Tunnel);
        // The ring under the notice, over the pages, the sheets and the player.
        Grid.SetColumnSpan(_couchRing, 2); _root.Children.Insert(_root.Children.IndexOf(_toast), _couchRing);
        AddHandler(GotFocusEvent, (_, _) => PlaceRing(), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(PlaceRing), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => PlaceRing(), RoutingStrategies.Bubble, handledEventsToo: true);
        LayoutUpdated += (_, _) => PlaceRing();
        SizeChanged += (_, _) => FitCouch();
        _overlay.Children.CollectionChanged += (_, _) => FitCouch();
        // The controller is read 60 times a second while one is connected and Mira is in front; otherwise asked for every 2 s.
        _pad.Tick += (_, _) => ReadPad();
        Activated += (_, _) => { if (MacGamepad.Read() is { } state) _padReader.Reset(state); _pad.Start(); };
        Deactivated += (_, _) => _pad.Stop();
        Opened += (_, _) => { if (InCouchMode) ApplyCouch(); _pad.Start(); };
    }

    /// <summary>Turns couch mode on or off, at once, and keeps it for the next launches.</summary>
    public void SetCouchMode(bool on)
    {
        if (on == Settings.CouchMode) return;
        Settings.CouchMode = on; Profile.SaveSettings(Settings);
        if (on) _beforeCouch = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
        ApplyCouch();
        if (on) Notice(Couch.On);
    }

    private void ApplyCouch()
    {
        var on = InCouchMode;
        Classes.Set("couch", on);
        if (on && WindowState != WindowState.FullScreen) WindowState = WindowState.FullScreen;
        else if (!on && WindowState == WindowState.FullScreen && Player is not { IsMini: false }) WindowState = _beforeCouch;
        FitCouch(); PlaceRing();
        // The focus where an arrow starts from, on the page shown.
        if (on) Dispatcher.UIThread.Post(() => { if (FocusManager?.GetFocusedElement() is null) FocusFirst(); }, DispatcherPriority.Background);
    }

    /// <summary>
    /// The zoom drawn: the pages, the rail, the sheets and the login laid out on the window's size divided by it, then
    /// drawn that much larger. The player and the mini-player keep theirs (mpv draws its picture at the window's pixels).
    /// </summary>
    private void FitCouch()
    {
        var zoom = InCouchMode ? Couch.Zoom(ClientSize.Width, ClientSize.Height) : 1;
        CouchZoom = zoom;
        Zoom(_host, fill: true); Zoom(_rail, fill: true); Zoom(_sheets, fill: true);
        foreach (var child in _overlay.Children.OfType<Control>()) Zoom(child, fill: child is not PlayerPage, skip: child is PlayerPage);
        // The notice grows from its bottom edge, where it sits.
        _toast.RenderTransformOrigin = new RelativePoint(.5, 1, RelativeUnit.Relative);
        _toast.RenderTransform = zoom == 1 ? null : new ScaleTransform(zoom, zoom);

        void Zoom(Control control, bool fill, bool skip = false)
        {
            if (zoom == 1 || skip)
            {
                if (control.RenderTransform is null) return;
                control.RenderTransform = null;
                if (fill) { control.ClearValue(HeightProperty); if (control != _rail) control.ClearValue(WidthProperty); control.ClearValue(HorizontalAlignmentProperty); control.ClearValue(VerticalAlignmentProperty); }
                return;
            }
            control.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
            control.RenderTransform = new ScaleTransform(zoom, zoom);
            if (!fill) return;
            // Laid out on the window divided by the zoom, from its top left corner, then scaled to fill it.
            control.HorizontalAlignment = HorizontalAlignment.Left; control.VerticalAlignment = VerticalAlignment.Top;
            control.Height = ClientSize.Height / zoom;
            if (control != _rail) control.Width = ClientSize.Width / zoom;
        }
    }

    /// <summary>The ring around the control focused, outside it by 4 points of the page, its corners rounded as the control's.</summary>
    private void PlaceRing()
    {
        var focused = FocusManager?.GetFocusedElement() as Control;
        // Not over the full screen player (its own controls show where they are), nor around nothing.
        if (!InCouchMode || focused is null || focused == this || !focused.IsEffectivelyVisible || Player is { IsMini: false } player && player.IsVisualAncestorOf(focused) || BoxOf(focused) is not { } box)
        {
            _couchRing.IsVisible = false;
            return;
        }
        var gap = 4 * CouchZoom + _couchRing.BorderThickness.Left;
        var margin = new Thickness(box.Left - gap, box.Top - gap, 0, 0);
        var (width, height) = (box.Width + 2 * gap, box.Height + 2 * gap);
        if (_couchRing.IsVisible && _couchRing.Margin == margin && _couchRing.Width == width && _couchRing.Height == height) return;
        var radius = (focused is TemplatedControl templated ? templated.CornerRadius.TopLeft : 8) * CouchZoom + gap;
        _couchRing.Margin = margin; _couchRing.Width = width; _couchRing.Height = height;
        _couchRing.CornerRadius = new CornerRadius(radius);
        _couchRing.IsVisible = true;
    }

    // —— The keys ——

    private void CouchKeyDown(object? sender, KeyEventArgs e)
    {
        if (!InCouchMode || e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        var focused = FocusManager?.GetFocusedElement() as Control;
        // Backspace steps back as Escape does, except while typing (it erases).
        if (e.Key == Key.Back && focused is not TextBox)
        {
            if (Player is { IsMini: false } || CurrentSheet is not null) return;
            if (Back()) e.Handled = true;
            return;
        }
        CoreGrid.Direction? direction = e.Key switch { Key.Left => CoreGrid.Direction.Left, Key.Right => CoreGrid.Direction.Right, Key.Up => CoreGrid.Direction.Up, Key.Down => CoreGrid.Direction.Down, _ => null };
        if (direction is not { } way || Player is { IsMini: false }) return;
        // What reads the arrows itself keeps them: the text being typed (← →), a slider along its axis, an open list or menu.
        if (focused is TextBox && way is CoreGrid.Direction.Left or CoreGrid.Direction.Right) return;
        if (focused is Slider slider && (slider.Orientation == Orientation.Horizontal) == (way is CoreGrid.Direction.Left or CoreGrid.Direction.Right)) return;
        if (focused is ComboBox { IsDropDownOpen: true } || focused?.FindAncestorOfType<Popup>(includeSelf: true) is not null || focused is ComboBoxItem or MenuItem || TopLevel.GetTopLevel(focused) is { } top && top != this) return;
        if (Move(way)) e.Handled = true;
    }

    /// <summary>Where the arrows go: the sheet shown, else a screen over the pages (sign-in, a party), else the page and the rail.</summary>
    private List<Control> CouchScope()
    {
        if (CurrentSheet is { } sheet) return [sheet];
        if (_overlay.Children.OfType<Control>().LastOrDefault(x => x.IsVisible && x is not PlayerPage) is { } over) return [over];
        var scope = new List<Control>();
        if (Current is { IsVisible: true } page) scope.Add(page);
        if (_rail.IsVisible) scope.Add(_rail);
        return scope;
    }

    /// <summary>The controls of <paramref name="scope"/> the focus can go to, where they are drawn in the window.</summary>
    private List<(Control Control, CoreGrid.Box Box)> CouchTargets(List<Control> scope)
    {
        var found = new List<(Control, CoreGrid.Box)>();
        foreach (var root in scope)
            foreach (var control in root.GetVisualDescendants().OfType<Control>())
            {
                if (!control.Focusable || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled || !KeyboardNavigation.GetIsTabStop(control)) continue;
                if (control is ScrollViewer or ItemsControl and not ComboBox || control.Bounds.Width < 1 || control.Bounds.Height < 1) continue;
                if (BoxOf(control) is { } box) found.Add((control, box));
            }
        return found;
    }
    private CoreGrid.Box? BoxOf(Control control)
    {
        if (control.TransformToVisual(this) is not { } matrix) return null;
        var rect = new Rect(control.Bounds.Size).TransformToAABB(matrix);
        return new CoreGrid.Box(rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>One arrow: the focus to the nearest control that way (<see cref="Couch.Next"/>), brought into view.</summary>
    public bool Move(CoreGrid.Direction way)
    {
        var targets = CouchTargets(CouchScope());
        if (targets.Count == 0) return false;
        var focused = FocusManager?.GetFocusedElement() as Control;
        // The focus somewhere else (on the window, under a sheet now closed, on a page gone): it starts again on this screen.
        var from = focused is null ? -1 : targets.FindIndex(x => x.Control == focused || x.Control.IsVisualAncestorOf(focused));
        if (from < 0) return FocusFirst(targets);
        if (Couch.Next([.. targets.Select(x => x.Box)], targets[from].Box, way) is not { } next) return true;
        Give(targets[next].Control);
        return true;
    }

    /// <summary>The first control of the screen in reading order, among those in view.</summary>
    public bool FocusFirst() => FocusFirst(CouchTargets(CouchScope()));
    private bool FocusFirst(List<(Control Control, CoreGrid.Box Box)> targets)
    {
        var view = new CoreGrid.Box(0, 0, ClientSize.Width, ClientSize.Height);
        var shown = targets.Where(x => x.Box.Left < view.Width && x.Box.Top < view.Height && x.Box.Left + x.Box.Width > 0 && x.Box.Top + x.Box.Height > 0).ToList();
        // The page's own first, before the rail beside it.
        var page = shown.Where(x => !_rail.IsVisualAncestorOf(x.Control)).ToList();
        var pool = page.Count > 0 ? page : shown;
        if (Couch.First([.. pool.Select(x => x.Box)]) is not { } first) return false;
        Give(pool[first].Control);
        return true;
    }
    private static void Give(Control control)
    {
        control.Focus(NavigationMethod.Directional);
        control.BringIntoView();
    }

    // —— A game controller ——

    private void ReadPad()
    {
        if (MacGamepad.Read() is not { } state)
        {
            if (_padConnected) { _padConnected = false; _pad.Interval = TimeSpan.FromSeconds(2); }
            return;
        }
        if (!_padConnected)
        {
            _padConnected = true; _padReader.Reset(state);
            // Off: offered once per launch, with a button; on: read from now on.
            if (!InCouchMode)
            {
                if (!_padOffered && Session is not null) { _padOffered = true; Notice(Couch.ControllerFound, Couch.TurnOn, () => SetCouchMode(true), sticky: false); }
                return;
            }
        }
        if (!InCouchMode) return;
        _pad.Interval = TimeSpan.FromMilliseconds(16);
        foreach (var button in _padReader.Read(state, _padClock.Elapsed))
            if (Couch.For(button, playing: Player is { IsMini: false }) is { } action) Do(action);
    }

    /// <summary>A controller's button, done as its key would be (<see cref="Couch.For"/>).</summary>
    public void Do(Couch.Action action)
    {
        // Someone is there, though macOS saw no input (« Tu regardes toujours ? »).
        Player?.Touched();
        switch (action)
        {
            case Couch.Action.Up: Press(Key.Up); break;
            case Couch.Action.Down: Press(Key.Down); break;
            case Couch.Action.Left: Press(Key.Left); break;
            case Couch.Action.Right: Press(Key.Right); break;
            case Couch.Action.Select: Press(Key.Enter); break;
            case Couch.Action.Back: Press(Key.Escape); break;
            case Couch.Action.PlayPause: Press(Key.Space); break;
            case Couch.Action.Subtitles: Press(Key.C); break;
            case Couch.Action.Skip: Press(Key.S); break;
            case Couch.Action.JumpBack: Press(Key.Left, KeyModifiers.Shift); break;
            case Couch.Action.JumpForward: Press(Key.Right, KeyModifiers.Shift); break;
            case Couch.Action.Search: if (CurrentSheet is null) { NavigateRoot("search"); } break;
            case Couch.Action.PreviousPage or Couch.Action.NextPage: TurnRail(action == Couch.Action.NextPage ? 1 : -1); break;
        }
    }
    /// <summary>
    /// A key as the keyboard sends it: down then up, to what has the focus (else the window), through the window's tunnel
    /// first, as macOS's own: the player and the sheets read it before the control.
    /// </summary>
    private void Press(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var target = FocusManager?.GetFocusedElement() as Interactive ?? this;
        var down = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target };
        target.RaiseEvent(down);
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyUpEvent, Key = key, KeyModifiers = modifiers, Source = target });
    }
    /// <summary>LB and RB: the rail's previous or next page, its entries in their order, those shown.</summary>
    private void TurnRail(int step)
    {
        if (CurrentSheet is not null || Player is { IsMini: false }) return;
        var keys = _railButtons.Where(x => x.Value.IsVisible).Select(x => x.Key).ToList();
        var at = Current is { } page ? keys.IndexOf(page.Rail) : -1;
        var next = at < 0 ? 0 : Math.Clamp(at + step, 0, keys.Count - 1);
        if (next == at) return;
        NavigateRoot(keys[next]);
        Dispatcher.UIThread.Post(() => FocusFirst(), DispatcherPriority.Background);
    }
}
