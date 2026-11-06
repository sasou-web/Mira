using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Mode canapé » (<see cref="Couch"/>): a PC plugged into the TV becomes its player. The window full screen, the pages
/// laid out as on a 1280 × 720 window and drawn larger (<see cref="UpdateZoom"/>; the player keeps its size), the focus
/// ring always shown, the arrow keys going from control to control anywhere on the screen, Backspace stepping back, a
/// remote's keys (an HDMI-CEC adapter types arrows, Enter and Escape; its play and pause reach Windows' media controls),
/// and an Xbox controller read as those keys.
/// </summary>
public partial class MainWindow
{
    private bool InCouchMode => _settings.CouchMode;
    /// <summary>The window full screen for couch mode (not the player's own full screen, <see cref="_fullscreen"/>).</summary>
    private bool _couchScreen;
    private readonly DispatcherTimer _pad = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly PadReader _padReader = new();
    private readonly System.Diagnostics.Stopwatch _padClock = System.Diagnostics.Stopwatch.StartNew();
    private bool _padOffered, _padConnected;

    private void InitializeCouch()
    {
        CouchModeTitle.Text = Couch.Title; CouchModeDescription.Text = Couch.Description + " " + Couch.Keys;
        System.Windows.Automation.AutomationProperties.SetName(CouchModeCheck, Couch.Title);
        // After the player's own handler (in the XAML): the player keeps its arrows.
        PreviewKeyDown += CouchPreviewKeyDown;
        _pad.Tick += (_, _) => ReadPad();
        Activated += (_, _) =>
        {
            if (InCouchMode && _startupComplete) { SetCouchScreen(true); Accessible.SetFocusRingShown(this, true); }
            if (XInput.Read() is { } state) _padReader.Reset(state);
            if (_startupComplete) _pad.Start();
        };
        Deactivated += (_, _) => _pad.Stop();
    }
    /// <summary>Once Mira has opened: couch mode as it was left, and the controller listened for.</summary>
    private void StartCouch()
    {
        if (InCouchMode) ApplyCouch();
        if (IsActive) _pad.Start();
    }

    private void CouchMode_Click(object sender, RoutedEventArgs e) => SetCouchMode(CouchModeCheck.IsChecked == true);
    /// <summary>Turns couch mode on or off, at once, and keeps it for the next launches.</summary>
    private void SetCouchMode(bool on)
    {
        CouchModeCheck.IsChecked = on;
        if (on == _settings.CouchMode) return;
        _settings.CouchMode = on;
        if (!TrySaveSettings()) SetNotice(SettingsNotSaved);
        ApplyCouch();
        if (on) SetNotice(Couch.On);
    }

    private void ApplyCouch()
    {
        var on = InCouchMode;
        SetCouchScreen(on);
        _keyboardNavigation = on || _keyboardNavigation;
        Accessible.SetFocusRingShown(this, on || _keyboardNavigation);
        if (UpdateZoom()) FitPages();
        // The focus where an arrow starts from.
        if (on) Dispatcher.BeginInvoke(() => { if (!CouchScope().Any(x => x.IsKeyboardFocusWithin)) FocusFirst(); }, DispatcherPriority.Background);
    }

    /// <summary>
    /// The window over the whole screen, the taskbar's place included, as the player's full screen: its frame and its
    /// maximize button gone. The player's own full screen then only hides its window buttons (<see cref="ToggleFullscreen"/>).
    /// </summary>
    private void SetCouchScreen(bool on)
    {
        if (on == _couchScreen || _fullscreenWindow is null || _windowFrame is null) return;
        // The player's full screen first left: the window keeps a single full screen.
        if (on && _fullscreen) ToggleFullscreen();
        if (on)
        {
            _windowFrame.SetFullscreen(true);
            try { _fullscreenWindow.Enter(); }
            catch (System.ComponentModel.Win32Exception) { _windowFrame.SetFullscreen(false); return; }
            _couchScreen = true;
        }
        else
        {
            _couchScreen = false;
            _fullscreenWindow.Exit(); _windowFrame.SetFullscreen(false);
        }
        MaximizeButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PlayerWindowChrome.Visibility = on || _fullscreen || _miniPlayer || !_playing ? Visibility.Collapsed : Visibility.Visible;
        UpdateMaximizeButtons();
    }

    // —— The keys ——

    private void CouchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!InCouchMode || e.Handled || Keyboard.Modifiers != ModifierKeys.None || FromTorLinkTerminal(e)) return;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var typing = focused is TextBoxBase or System.Windows.Controls.PasswordBox;
        // In the player, as on a TV: Escape and Backspace stop it and go back to the title.
        if (_playing && !_miniPlayer && !PlayerErrorShown)
        {
            if (e.Key is Key.Escape or Key.Back && !PlayerOptionsPopup.IsOpen && !e.IsRepeat) { _ = LeavePlayerAsync(); e.Handled = true; }
            return;
        }
        if (e.Key == Key.Back && !typing) { if (GoBack()) e.Handled = true; return; }
        // Enter on a check box or a list: the box ticked, the list opened (WPF reads only Space and F4 there).
        if (e.Key == Key.Enter && focused is ToggleButton and not RadioButton && !e.IsRepeat) { Press(Key.Space); e.Handled = true; return; }
        if (e.Key == Key.Enter && focused is ComboBox { IsDropDownOpen: false } combo && !e.IsRepeat) { combo.IsDropDownOpen = true; e.Handled = true; return; }
        CardGrid.Direction? direction = e.Key switch { Key.Left => CardGrid.Direction.Left, Key.Right => CardGrid.Direction.Right, Key.Up => CardGrid.Direction.Up, Key.Down => CardGrid.Direction.Down, _ => null };
        if (direction is not { } way) return;
        var across = way is CardGrid.Direction.Left or CardGrid.Direction.Right;
        // What reads the arrows itself keeps them: the text being typed (← →), a slider along its axis, an open list or menu.
        if (typing && across) return;
        if (focused is Slider slider && (slider.Orientation == Orientation.Horizontal) == across) return;
        if (focused is ComboBoxItem or MenuItem || focused is ComboBox { IsDropDownOpen: true } || InAnotherWindow(focused)) return;
        _keyboardNavigation = true; Accessible.SetFocusRingShown(this, true);
        if (Move(way)) e.Handled = true;
    }

    /// <summary>A popup's or another window's control (a menu, a list opened): its own keys.</summary>
    private bool InAnotherWindow(DependencyObject? focused) =>
        focused is Visual visual && PresentationSource.FromVisual(visual) is { } source && source != PresentationSource.FromVisual(this);

    /// <summary>
    /// Where the arrows go: the screen over everything shown (a confirmation, « Nouveautés », sign-in…), else the page on
    /// top (a title, Réglages, Téléchargements, Guide, else the library) and the rail beside it.
    /// </summary>
    private List<FrameworkElement> CouchScope()
    {
        foreach (var over in new FrameworkElement[] { ConfirmOverlay, CollectionOverlay, IdentifyOverlay, InviteOverlay, WhatsNewOverlay, PartyOverlay, WelcomeOverlay, LoginOverlay })
            if (over.Visibility == Visibility.Visible && over.IsHitTestVisible) return [over];
        var scope = new List<FrameworkElement>();
        if (new FrameworkElement[] { GuideOverlay, SettingsOverlay, DetailOverlay, TorLinkOverlay }.FirstOrDefault(x => x.Visibility == Visibility.Visible && x.IsHitTestVisible) is { } page) scope.Add(page);
        else if (LibraryShell.IsVisible) scope.Add(LibraryShell);
        if (NavigationRail.IsVisible) scope.Add(NavigationRail);
        return scope;
    }

    /// <summary>The controls of <paramref name="scope"/> the focus can go to, where they are drawn in the window.</summary>
    private List<(FrameworkElement Control, CardGrid.Box Box)> CouchTargets(List<FrameworkElement> scope)
    {
        var found = new List<(FrameworkElement, CardGrid.Box)>();
        var root = (Visual)Content;
        foreach (var top in scope) Walk(top);
        return found;
        void Walk(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                if (VisualTreeHelper.GetChild(node, i) is not FrameworkElement child || !child.IsVisible) continue;
                if (child.Focusable && child.IsEnabled && KeyboardNavigation.GetIsTabStop(child) && child is not (ScrollViewer or ItemsControl and not ComboBox) && child.ActualWidth >= 1 && child.ActualHeight >= 1)
                {
                    var rect = child.TransformToAncestor(root).TransformBounds(new Rect(child.RenderSize));
                    found.Add((child, new CardGrid.Box(rect.X, rect.Y, rect.Width, rect.Height)));
                    // A card's own buttons are reached by its menu, as with Tab.
                    if (child is ButtonBase) continue;
                }
                Walk(child);
            }
        }
    }

    /// <summary>One arrow: the focus to the nearest control that way (<see cref="Couch.Next"/>), brought into view.</summary>
    private bool Move(CardGrid.Direction way)
    {
        var targets = CouchTargets(CouchScope());
        if (targets.Count == 0) return false;
        var focused = Keyboard.FocusedElement as DependencyObject;
        var from = focused is null ? -1 : targets.FindIndex(x => ReferenceEquals(x.Control, focused) || focused is Visual visual && visual.IsDescendantOf(x.Control));
        // The focus somewhere else (on the window, under an overlay now closed, in the video): it starts again on this screen.
        if (from < 0) return FocusFirst(targets);
        if (Couch.Next([.. targets.Select(x => x.Box)], targets[from].Box, way) is { } next) Give(targets[next].Control);
        return true;
    }
    private bool FocusFirst() => FocusFirst(CouchTargets(CouchScope()));
    private bool FocusFirst(List<(FrameworkElement Control, CardGrid.Box Box)> targets)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        var shown = targets.Where(x => x.Box.Left < width && x.Box.Top < height && x.Box.Left + x.Box.Width > 0 && x.Box.Top + x.Box.Height > 0).ToList();
        // The page's own first, before the rail beside it.
        var page = shown.Where(x => !x.Control.IsDescendantOf(NavigationRail)).ToList();
        var pool = page.Count > 0 ? page : shown;
        if (Couch.First([.. pool.Select(x => x.Box)]) is not { } first) return false;
        Give(pool[first].Control);
        return true;
    }
    private static void Give(FrameworkElement control) { control.Focus(); control.BringIntoView(); }

    // —— A game controller ——

    private void ReadPad()
    {
        if (XInput.Read() is not { } state)
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
                if (!_padOffered && _client is not null && !ValidationRun) { _padOffered = true; SetNotice(Couch.ControllerFound, Couch.TurnOn, () => SetCouchMode(true), sticky: false); }
                return;
            }
        }
        if (!InCouchMode) return;
        _pad.Interval = TimeSpan.FromMilliseconds(16);
        foreach (var button in _padReader.Read(state, _padClock.Elapsed))
            if (Couch.For(button, playing: _playing && !_miniPlayer) is { } action) Do(action);
    }

    /// <summary>A controller's button, done as its key would be (<see cref="Couch.For"/>).</summary>
    private void Do(Couch.Action action)
    {
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
            case Couch.Action.JumpBack: if (_playing) { SeekRelative(-LongJump); ShowPlayerControls(); } break;
            case Couch.Action.JumpForward: if (_playing) { SeekRelative(LongJump); ShowPlayerControls(); } break;
            case Couch.Action.Search: if (CouchScope().Contains(NavigationRail)) { SearchNav_Click(this, new()); } break;
            case Couch.Action.PreviousPage or Couch.Action.NextPage: TurnRail(action == Couch.Action.NextPage ? 1 : -1); break;
        }
    }
    /// <summary>
    /// A key as the keyboard sends it: down then up, each one's preview first, through the window (or the list opened) to what has the focus
    /// (else to the window itself: the video has it). The player and the pages read it as a key typed.
    /// </summary>
    private void Press(Key key)
    {
        // An open list keeps its keys: they go to its item, in its own popup.
        var target = Keyboard.FocusedElement ?? this;
        if (PresentationSource.FromVisual(target as Visual ?? this) is not { } source) return;
        bool Raise(RoutedEvent routed)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = routed };
            target.RaiseEvent(args);
            return args.Handled;
        }
        if (!Raise(Keyboard.PreviewKeyDownEvent)) Raise(Keyboard.KeyDownEvent);
        if (!Raise(Keyboard.PreviewKeyUpEvent)) Raise(Keyboard.KeyUpEvent);
    }
    /// <summary>LB and RB: the rail's previous or next page, its buttons in their order, those shown.</summary>
    private void TurnRail(int step)
    {
        if (!CouchScope().Contains(NavigationRail)) return;
        var buttons = RailButtons.Where(x => x.IsVisible && x.IsEnabled).ToList();
        var at = ShownRailButton() is { } shown ? buttons.IndexOf(shown) : -1;
        var next = at < 0 ? 0 : Math.Clamp(at + step, 0, buttons.Count - 1);
        if (next == at) return;
        buttons[next].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, buttons[next]));
        Dispatcher.BeginInvoke(() => FocusFirst(), DispatcherPriority.Background);
    }
}
