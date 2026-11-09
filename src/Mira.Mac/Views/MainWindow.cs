using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Mira.Core;
using Mira.Mac.Playback;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>What a notice says, drawn by the icon in its tile: news, something done, a failure.</summary>
public enum NoticeTone { Info, Done, Error }

/// <summary>A screen of the library: home, a grid, a title page, the settings.</summary>
public abstract class Page : UserControl
{
    protected MainWindow Shell { get; }
    /// <summary>
    /// <paramref name="underRail"/>: the page's picture runs under the rail, as on Windows (home, title page), and the
    /// page keeps its text clear of it (<see cref="MainWindow.RailWidth"/>); the others start beside it.
    /// </summary>
    protected Page(MainWindow shell, bool underRail = false) { Shell = shell; if (!underRail) Margin = new Thickness(MainWindow.RailWidth, 0, 0, 0); }
    /// <summary>The rail entry lit while this page shows.</summary>
    public virtual string Rail => "";
    /// <summary>Fills the page; called once, when it first shows.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
    /// <summary>Progress, watched or favourite marks changed (after a playback, a menu action): redraw what shows them.</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;
    /// <summary>Its rail entry (or shortcut) chosen again while it shows: nothing reloads or fades in again; its list glides back to the top.</summary>
    public virtual void ShowAgain() { if (Content is ScrollViewer scroll) GlideUp.Start(scroll); }
    /// <summary>
    /// On screen, or not: left for another page (kept hidden in the window while it may come back, see
    /// <see cref="MainWindow.Host"/>) or taken out of the window. What runs only while the page shows (a timer, the banner's
    /// turns, a field's focus) follows this rather than the visual tree, which a page kept no longer leaves.
    /// </summary>
    public event Action<bool>? Showing;
    public bool IsShowing { get; private set; }
    private void UpdateShowing(bool attached)
    {
        var showing = attached && IsVisible;
        if (showing == IsShowing) return;
        IsShowing = showing; Showing?.Invoke(showing);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); UpdateShowing(true); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); UpdateShowing(false); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateShowing(this.GetVisualRoot() is not null);
    }
    /// <summary>
    /// <paramref name="changed"/> with true once <paramref name="part"/> is on screen with its page, false once that page is
    /// put away or the part leaves it: a section's timer runs only while it can be seen.
    /// </summary>
    public static void WhileShown(Control part, Action<bool> changed)
    {
        Page? page = null; var on = false;
        void Set(bool shown) { if (shown == on) return; on = shown; changed(shown); }
        part.AttachedToVisualTree += (_, _) =>
        {
            page = part.FindAncestorOfType<Page>();
            if (page is null) { Set(true); return; }
            page.Showing += Set; Set(page.IsShowing);
        };
        part.DetachedFromVisualTree += (_, _) => { if (page is not null) page.Showing -= Set; page = null; Set(false); };
    }
}

/// <summary>
/// Mira's window: the rail of the Windows app on the left, the current page beside it, the player above everything
/// while it plays. Pages are kept in a history that Échap, ⌘[, the back button and a swipe walk (⌘] and the other swipe forward).
/// </summary>
public sealed partial class MainWindow : Window
{
    public Profile Profile { get; }
    public PlayerSettings Settings { get; }
    public Session? Session { get; private set; }
    /// <summary>The connection's speed to Jellyfin, for « Automatique » away from home: measured once an hour (<see cref="StreamQuality"/>).</summary>
    public ConnectionSpeed Speed { get; } = new();
    public string[] Args { get; }
    private readonly Grid _root = new();
    /// <summary>The pages: the one shown, and those that may come back, hidden (<see cref="Host"/>).</summary>
    private readonly Panel _host = new();
    private readonly Panel _overlay = new();
    private readonly StackPanel _railItems = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _rail;
    private readonly Border _toast;
    private readonly TextBlock _toastText = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, LineHeight = 20, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _toastAction = new() { Classes = { "primary" }, Padding = new Thickness(16, 8), Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Icon _toastIcon = new("info", 21);
    private readonly Border _toastTile = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 13, 0) };
    private readonly Border _toastTime = new() { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Ui.Brush("#3D3D43"), RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative), IsHitTestVisible = false };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    /// <summary>Jellyfin's change notifications come in bursts (a scan, a playback elsewhere): one refresh after the last.</summary>
    private readonly DispatcherTimer _serverChanged = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Avalonia.Controls.Shapes.Ellipse _avatarPhoto = new() { IsVisible = false };
    private readonly TextBlock _avatar = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#DEDEE4"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Page> _history = [];
    private readonly Dictionary<string, Button> _railButtons = [];
    /// <summary>« Regarder ensemble »: the rail's group button, above Réglages, in sight while this Mac is in a group (ShowGroupRail).</summary>
    private readonly Button _groupRail = new() { IsVisible = false };
    private readonly TextBlock _groupCount = new() { FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Ui.Brush("#121212"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Action? _toastClick;
    public PlayerPage? Player { get; private set; }
    /// <summary>The player over the whole window; the mini-player leaves the library usable.</summary>
    public bool Watching => Player is { IsMini: false };
    /// <summary>« Regarder ensemble »: the group this Mac is in, whatever plays.</summary>
    public WatchGroup Group { get; }
    /// <summary>« Tailscale intégré »: Mira's own node on the person's Tailscale network, for Jellyfin away from home.</summary>
    public BuiltInTailscale Tailnet { get; }
    /// <summary>Top room left for the macOS window buttons, which sit over Mira's own content.</summary>
    public double TitleBarInset { get; }
    /// <summary>Wide enough for the traffic lights above the brand, close to Windows' 68.</summary>
    public const double RailWidth = 76;
    /// <summary>When the window last came to the front (Environment.TickCount64): its click shows the player's controls, never pauses.</summary>
    public long ActivatedAt { get; private set; } = long.MinValue / 2;

    public MainWindow(string[] args, Profile profile)
    {
        // Before anything shows, and before the settings: each text is written in Mira's language as it is built, and the
        // defaults of a new profile (the audio and subtitle languages) follow it.
        Args = args; Profile = profile; SystemLanguage.Apply(profile.ChosenLanguage());
        Settings = profile.LoadSettings(); Spoilers.On = Settings.HideSpoilers;
        Group = new WatchGroup(this);
        Group.Changed += () => { ShowGroupRail(); Player?.RefreshMenus(); Player?.ShowGroup(); if (Current is DetailPage detail) detail.ShowTogether(); else if (Current is HomePage home) home.GroupChanged(); PartyGroupChanged(); };
        Group.Member += GroupMember;
        Tailnet = new BuiltInTailscale(this);
        Title = "Mira"; MinWidth = 960; MinHeight = 600;
        Width = Settings.WindowWidth > 0 ? Settings.WindowWidth : 1440; Height = Settings.WindowHeight > 0 ? Settings.WindowHeight : 900;
        PlaceWindow();
        if (Settings.WindowMaximized) WindowState = WindowState.Maximized;
        if (OperatingSystem.IsMacOS())
        {
            // The title bar disappears into the page, as in Apple's own apps: the traffic lights float over the rail.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 30;
            TitleBarInset = 30;
        }
        // The banners' captures (--gallery), drawn on Linux: the room the traffic lights take on a Mac, for the frame to draw them.
        else if (args.Contains("--gallery")) TitleBarInset = 30;
        _root.ColumnDefinitions = new ColumnDefinitions("Auto,*");
        // The pages take the whole width, under the rail (Page): a picture runs to the window's edge, as on Windows.
        Grid.SetColumnSpan(_host, 2); _root.Children.Add(_host);
        _rail = BuildRail();
        _root.Children.Add(_rail);
        Grid.SetColumnSpan(_overlay, 2); _root.Children.Add(_overlay);
        Grid.SetColumnSpan(_sheets, 2); _root.Children.Add(_sheets);
        _toast = BuildToast();
        Grid.SetColumnSpan(_toast, 2); _root.Children.Add(_toast);
        if (TitleBarInset > 0)
        {
            // The strip where the title bar was: drags the window, and a double click zooms it, as on any Mac window.
            var strip = new Border { Height = TitleBarInset, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Margin = new Thickness(RailWidth, 0, 0, 0) };
            strip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else BeginMoveDrag(e);
            };
            Grid.SetColumnSpan(strip, 2); _root.Children.Add(strip);
        }
        Content = _root;
        ListenToTrackpad();
        BuildMenuBar();
        _toastTimer.Tick += (_, _) => NoticeTick();
        _serverChanged.Tick += (_, _) => { _serverChanged.Stop(); _ = RefreshPagesAsync(fromServer: true); };
        _choicesSoon.Tick += async (_, _) => { _choicesSoon.Stop(); await SyncChoicesAsync(); };
        Activated += (_, _) =>
        {
            ActivatedAt = Environment.TickCount64;
            // A friend's party guest: nothing of this Mac's own account is asked of the friend's Jellyfin.
            if (InParty) return;
            if (DateTimeOffset.UtcNow - _choicesAsked > TimeSpan.FromMinutes(1)) ChoicesChanged();
            // An account made administrator (or no longer) since the sign-in: Téléchargements comes or goes when Mira is back in front.
            if (Session is { } session && DateTimeOffset.UtcNow - _rightsAsked > TimeSpan.FromMinutes(1)) _ = OfferDownloadsAsync(session);
        };
        // The player takes its keys before the control that has the focus: Space never clicks again the last button pressed,
        // and the arrows never move a slider or a page behind the video.
        // The mini-player takes its keys only when it has the focus: the library around it keeps its own.
        // A sheet over the window has the keys first: Escape closes it, nothing reaches the page or the player under it.
        AddHandler(KeyDownEvent, (_, e) => { if (CurrentSheet is { } sheet) { if (e.Key == Key.Escape) { if (!sheet.Busy) CloseSheet(sheet); e.Handled = true; } return; } if (Player is { } player && (!player.IsMini || e.Source is Visual source && player.IsVisualAncestorOf(source))) player.HandleKey(e); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Its releases always: a key held is read once where acting again would undo it, and the release of a key it took
        // is its own too: Avalonia presses a focused button when Space is released (PlayerPage.Keys.cs).
        AddHandler(KeyUpEvent, (_, e) => Player?.HandleKeyUp(e), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => Player?.ForgetHeldKeys();
        KeyDown += OnKeyDown;
        InitializeCouch();
        ListenToMediaKeys();
        InitializeNotifications();
        InitializePower();
        PointerPressed += (_, e) => { var buttons = e.GetCurrentPoint(this).Properties; if (buttons.IsXButton1Pressed) Back(); else if (buttons.IsXButton2Pressed) Forward(); };
        // A press whose release never came (a menu or the player gone from under it, the window turned full screen by a
        // double click) left the pointer captured by what remained: every turn of the wheel went there, never to the page.
        AddHandler(PointerMovedEvent, (_, e) => Unstick(e), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        MenuBase.ClosedEvent.AddClassHandler<MenuBase>((_, _) => Dispatcher.UIThread.Post(ReleasePointer));
        Closing += OnClosing;
        // Once: Avalonia says Opened again each time the window comes back from the Dock (Reopen).
        // Opened again by an update while in the Dock (--in-dock): back there at once, the account signed in meanwhile.
        Opened += async (_, _) => { if (_started) return; _started = true; StartInDock(); await StartAsync(); };
    }

    private Border BuildRail()
    {
        var brand = new Icon("brand", 32) { Foreground = Ui.Ink, Margin = new Thickness(0, 14, 0, 26) };
        foreach (var (key, icon, tip) in new[] { ("home", "home", L("Accueil", "Home")), ("search", "search", L("Rechercher · ⌘F", "Search · ⌘F")), ("Movie", "film", L("Films", "Movies")), ("Series", "series", L("Séries", "Shows")), ("library", "library", L("Toute la bibliothèque", "Whole library")), ("offline", "laptop", L("Sur ce Mac · les titres gardés pour les regarder sans connexion", "On this Mac · titles kept to watch offline")), ("downloads", "download", L("Télécharger sur le PC serveur", "Download to the server PC")) })
        {
            var button = Ui.IconButton(icon, tip, () => NavigateRoot(key), "rail", 22);
            _railButtons[key] = button; _railItems.Children.Add(button);
        }
        // Shown once Jellyfin says this account administers it, as its relay requires.
        _railButtons["downloads"].IsVisible = false;
        // Shown once this account keeps a title on this Mac.
        _railButtons["offline"].IsVisible = false;
        var settings = Ui.IconButton("settings", L("Réglages · ⌘,", "Settings · ⌘,"), () => NavigateRoot("settings"), "rail", 22);
        _railButtons["settings"] = settings;
        var avatar = new Grid { Width = 29, Height = 29, Margin = new Thickness(0, 12, 0, 0), Children = { new Border { CornerRadius = new CornerRadius(10), Background = Ui.Brush("#3D3D43"), Child = _avatar }, _avatarPhoto } };
        ToolTip.SetTip(avatar, L("Compte Jellyfin", "Jellyfin account"));
        // Its count of accounts in a corner, as the title page's « En groupe · 2 personnes ».
        _groupRail.Classes.Add("rail"); Avalonia.Automation.AutomationProperties.SetName(_groupRail, L("Groupe", "Group"));
        _groupRail.Content = new Grid
        {
            Children =
            {
                new Icon("users", 22),
                new Border { MinWidth = 16, Height = 16, Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(8), Background = Ui.Brush("#86C6A2"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, -9, 0), IsHitTestVisible = false, Child = _groupCount }
            }
        };
        _groupRail.Click += async (_, _) => await GroupMenuAsync();
        var bottom = Ui.Column(8, _groupRail, settings, avatar); bottom.HorizontalAlignment = HorizontalAlignment.Center;
        var dock = new DockPanel { Margin = new Thickness(0, TitleBarInset, 0, 18) };
        DockPanel.SetDock(brand, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(brand); dock.Children.Add(bottom); dock.Children.Add(_railItems);
        // As on Windows: the rail is the window itself (no panel of its own, no edge), only the chosen page is lit.
        return new Border { Width = RailWidth, Background = Brushes.Transparent, Child = dock };
    }
    // A notice: a card at the bottom of the window, its icon in a tile, its button, a close button, and the time it has left under it.
    private Border BuildToast()
    {
        _toastAction.Click += (_, _) => { var click = _toastClick; HideNotice(); click?.Invoke(); };
        _toastTile.Child = _toastIcon;
        AutomationProperties.SetLiveSetting(_toastText, AutomationLiveSetting.Polite);
        var close = Ui.IconButton("close", L("Masquer le message", "Hide the message"), HideNotice, "icon", 16);
        close.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { Margin = new Thickness(12, 11, 8, 11) };
        DockPanel.SetDock(_toastTile, Dock.Left); DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(_toastAction, Dock.Right);
        row.Children.Add(_toastTile); row.Children.Add(close); row.Children.Add(_toastAction); row.Children.Add(_toastText);
        var card = new Grid(); card.Children.Add(row); card.Children.Add(_toastTime);
        var toast = new Border
        {
            Background = Ui.Brush("#F21A1A1D"), BorderBrush = Ui.Brush("#3D3D43"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, 28), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Child = card, IsVisible = false, Opacity = 0, MinWidth = 380, MaxWidth = 580, MinHeight = 64,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }]
        };
        // Held under the pointer: it waits, and leaves a moment after the pointer does.
        toast.PointerEntered += (_, _) => { if (_toastTimer.IsEnabled) { _toastTimer.Stop(); RunNoticeTime(null); } };
        toast.PointerExited += (_, _) => { if (_toastOn && !_toastSticky) { _toastTimer.Interval = TimeSpan.FromSeconds(3); _toastTimer.Start(); RunNoticeTime(_toastTimer.Interval); } };
        return toast;
    }
    private bool _toastSticky;
    /// <summary>The line under the notice empties as its time runs out (none for a notice that waits).</summary>
    private void RunNoticeTime(TimeSpan? left)
    {
        _toastTime.Transitions = null;
        _toastTime.RenderTransform = TransformOperations.Parse("scaleX(1)");
        _toastTime.IsVisible = left is not null;
        if (left is not { } time) return;
        DispatcherTimer.RunOnce(() =>
        {
            _toastTime.Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = time }];
            _toastTime.RenderTransform = TransformOperations.Parse("scaleX(0)");
        }, TimeSpan.FromMilliseconds(16));
    }
    private static readonly IBrush NoticeDanger = Ui.Brush("#F3AD99"), NoticeDangerTile = Ui.Brush("#2EF3AD99"), NoticeTile = Ui.Brush("#30FFFFFF"), NoticeOnAccent = Ui.Brush("#111113"), NoticeAccent = Ui.Brush("#F5F5F7");

    /// <summary>
    /// A short message at the bottom of the window, as on Windows: said by VoiceOver, it leaves on its own once there was
    /// time to read it (<see cref="Legibility.NoticeDuration"/>), never while the pointer or the keyboard focus is on it.
    /// <paramref name="action"/> adds a button (« Réessayer », « Redémarrer »…): such a message stays until it is used or
    /// closed, as does one marked <paramref name="sticky"/> (an error that matters); sticky false lets a message with a
    /// button leave all the same (« Annuler »). A message with a button that stays is not lost under the next one: it
    /// comes back once that one is closed. Never for what the window already shows (a button that changed, a row that
    /// left): only a failure, what cannot be seen otherwise (the clipboard, work in the background or on another device,
    /// what Jellyfin does later), or a message with a button.
    /// </summary>
    public void Notice(string text, string? action = null, Action? click = null, bool? sticky = null, NoticeTone tone = NoticeTone.Info)
    {
        if (_toastOn && _toastSticky && _toastClick is { } waiting && _toastAction.Content is string label && _toastText.Text != text)
        {
            _parkedNotices.RemoveAll(parked => parked.Text == text);
            _parkedNotices.Add(new(_toastText.Text ?? "", label, waiting, _toastTone));
            if (_parkedNotices.Count > 5) _parkedNotices.RemoveAt(0);
        }
        else _parkedNotices.RemoveAll(parked => parked.Text == text);
        ShowNotice(text, action, click, sticky ?? click is not null, tone);
    }
    /// <summary>Said under the message still on screen, which keeps its button, instead of replacing it.</summary>
    public void AddNotice(string text)
    {
        if (NoticeText is { Length: > 0 } shown) ShowNotice(shown + "\n" + text, _toastAction.Content as string, _toastClick, _toastSticky, _toastTone);
        else Notice(text);
    }
    private readonly List<ParkedNotice> _parkedNotices = [];
    private sealed record ParkedNotice(string Text, string Action, Action Click, NoticeTone Tone);
    private NoticeTone _toastTone;
    /// <summary>A notice is on screen (its card still fades out a moment after it is closed).</summary>
    private bool _toastOn;
    private void ShowNotice(string text, string? action, Action? click, bool sticky, NoticeTone tone)
    {
        _toastClick = click; _toastSticky = sticky; _toastTone = tone;
        _toastAction.Content = action; _toastAction.IsVisible = action is not null && click is not null;
        _toastIcon.Kind = tone switch { NoticeTone.Done => "check", NoticeTone.Error => "warning", _ => "info" };
        _toastIcon.Foreground = tone switch { NoticeTone.Done => NoticeOnAccent, NoticeTone.Error => NoticeDanger, _ => Ui.Ink };
        _toastTile.Background = tone switch { NoticeTone.Done => NoticeAccent, NoticeTone.Error => NoticeDangerTile, _ => NoticeTile };
        // Shown before its text changes, so that VoiceOver finds it there.
        _toast.IsVisible = _toastOn = true; _toast.Opacity = 1;
        _toastText.Text = text;
        _toastTimer.Stop();
        if (!sticky) { _toastTimer.Interval = Legibility.NoticeDuration(text); _toastTimer.Start(); }
        RunNoticeTime(sticky ? null : _toastTimer.Interval);
    }
    /// <summary>Closed (its button, ×, its time run out): the message that was waiting under it comes back, with its button.</summary>
    public void HideNotice()
    {
        if (_parkedNotices.Count > 0)
        {
            var next = _parkedNotices[^1]; _parkedNotices.RemoveAt(_parkedNotices.Count - 1);
            ShowNotice(next.Text, next.Action, next.Click, true, next.Tone);
            return;
        }
        _toastTimer.Stop(); _toastSticky = _toastOn = false; _toast.Opacity = 0;
        DispatcherTimer.RunOnce(() => { if (!_toastOn) _toast.IsVisible = false; }, TimeSpan.FromMilliseconds(200));
    }
    /// <summary>The time ran out: not under the pointer (see <see cref="BuildToast"/>) nor the keyboard focus, which hold it.</summary>
    private void NoticeTick()
    {
        if (_toast.IsKeyboardFocusWithin || _toast.IsPointerOver) { RunNoticeTime(_toastTimer.Interval); return; }
        HideNotice();
    }
    public string? NoticeText => _toastOn ? _toastText.Text : null;
    /// <summary>The button of the notice shown (the checks).</summary>
    public string? NoticeAction => _toastOn && _toastAction.IsVisible ? _toastAction.Content as string : null;

    private async Task StartAsync()
    {
        if (Args.Contains("--self-check")) { await Checks.SelfCheck.RunAsync(this); return; }
        if (Args.Contains("--gallery")) { await Checks.Gallery.RunAsync(this); return; }
        if (Args.Contains("--smooth-check")) { await Checks.SmoothCheck.RunAsync(this); return; }
        // The check of « Tailscale intégré »: its stand-in Jellyfin served before anything asks it.
        var tailnetCheck = Args.Contains("--tailnet-check") ? new Checks.TailnetCheck(this) : null;
        // Signed in to « Tailscale intégré » before: its node starts first, Jellyfin may be reached through it.
        await Tailnet.StartWithMiraAsync();
        // Off the interface thread: after an update, macOS may ask before Mira reads its keychain.
        if (await Task.Run(Profile.LoadConnection) is { } connection) await ActivateAsync(connection);
        else ShowLogin();
        if (tailnetCheck is not null) { await tailnetCheck.RunAsync(); return; }
        // Once, beside what the opening already said (a cache recreated) rather than in its place.
        if (Profile.SettingsUnreadable)
        {
            var said = L("Tes réglages étaient illisibles : Mira est reparti des réglages par défaut.", "Your settings couldn’t be read: Mira went back to the default settings.")
                + (Profile.SettingsCopy is { } copy ? L($" L’ancien fichier est gardé sous le nom {Path.GetFileName(copy)}.", $" The old file is kept as {Path.GetFileName(copy)}.") : "");
            AddNotice(said);
        }
        ShowStartupScreen();
        _ = Updates.StartAsync(this);
    }

    // —— Account ——
    public void ShowLogin(string? error = null)
    {
        _rail.IsVisible = false;
        var login = new LoginPage(this, error);
        // Opened from an invitation (a mira:// link) before any account: its Jellyfin, to sign in to.
        if (_invite is { } invite) login.Server = invite.Server;
        _overlay.Children.Clear(); _overlay.Children.Add(login);
        _history.Clear(); _forward.Clear(); _kept.Clear(); Host(null);
        // A party link opened while Mira was starting, before any account: its first name is asked now.
        if (_invite is { Party: not null }) _ = OfferInviteAsync();
    }
    /// <summary>Opens the library of a signed-in account.</summary>
    public async Task ActivateAsync(Connection connection, HttpMessageHandler? handler = null)
    {
        await CloseSessionAsync();
        _handler = handler;
        JellyfinClient.DeviceName = "Mac";
        // Another server, or the same from elsewhere: its connection is measured again.
        Speed.Forget();
        try { Session = new Session(connection, Profile, handler); }
        // Even a new cache cannot be written (full disk, folder without rights): no library without it. A damaged one
        // is replaced by LibraryStore itself.
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            ShowLogin(L("Mira ne peut pas ouvrir le cache de ce compte dans son dossier de données : disque plein, dossier sans droits d’écriture ou fichier utilisé par un autre programme. Libère de la place si besoin, puis relance Mira : il rouvrira ce compte sans te redemander ton mot de passe.",
                "Mira can’t open this account’s cache in its data folder: the disk is full, the folder can’t be written to, or another program is using the file. Free up some space if needed, then open Mira again: it will reopen this account without asking for your password."));
            if (Login is { } login) login.Server = connection.Server;
            return;
        }
        if (handler is null)
        {
            var session = Session;
            // A library change may be a new episode of a series this account follows (MainWindow.Notifications.cs).
            void Changed() { _serverChanged.Stop(); _serverChanged.Start(); EpisodesMayHaveArrived(); }
            // Open again after a break: what changed meanwhile went unheard, and the group may have let this Mac go.
            var opened = false;
            void Listening(bool open) => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(session, Session)) return;
                if (open && opened) Changed();
                opened |= open; _listening = open; Group.Listening(session, open);
            });
            session.Listen(() => Dispatcher.UIThread.Post(Changed), Listening, (type, data) => Dispatcher.UIThread.Post(() => Group.Message(session, type, data)),
                command => Dispatcher.UIThread.Post(() => Remote(session, command)));
        }
        _avatar.Text = connection.UserName.Length > 0 ? connection.UserName[..1].ToUpperInvariant() : "?";
        SetPhoto(null); _ = ShowPhotoAsync();
        _overlay.Children.Clear(); _rail.IsVisible = true;
        _railButtons["downloads"].IsVisible = false;
        _ = OfferDownloadsAsync(Session);
        CollectionRights = TitleCollections.Rights.None;
        _ = LoadCollectionRightsAsync(Session);
        _ = LoadOfflineAsync(Session);
        _history.Clear(); _forward.Clear(); _kept.Clear();
        var home = new HomePage(this);
        _kept["home"] = home;
        await ShowPageAsync(home, root: true, fresh: true);
        if (Session.Store.DamagedCopy is not null) Notice(L("Le cache local de ce compte était illisible : il a été recréé.", "This account’s local cache couldn’t be read: it was created again."));
        // Opened from a mira:// link before this account was: asked now.
        if (_invite is not null) _ = OfferInviteAsync();
        ChoicesChanged();
        StartEpisodeNotices();
    }

    /// <summary>
    /// « Téléchargements »: searches and downloads sent to the PC of Jellyfin, where TorLink runs (Mira for Windows), by
    /// its administrators only, through the relay of Mira's extension. A Jellyfin without the extension (most of them, on
    /// Linux, a NAS or in Docker) never gets the entry: the page could not work there (<see cref="Mira.Core.TorLink.RemoteDownloads.Offered"/>).
    /// </summary>
    private async Task OfferDownloadsAsync(Session session)
    {
        _rightsAsked = DateTimeOffset.UtcNow;
        bool? admin = null, extension = null;
        try
        {
            admin = await session.Client.IsAdministratorAsync(session.Lifetime);
            if (admin == true) extension = await session.Client.HasExtensionAsync(session.Lifetime);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        // Unreachable or unread: the entry stays as it was, asked again the next time Mira comes in front.
        if (admin is null || admin == true && extension is null) _rightsAsked = DateTimeOffset.MinValue;
        if (!ReferenceEquals(session, Session)) return;
        var button = _railButtons["downloads"];
        button.IsVisible = Mira.Core.TorLink.RemoteDownloads.Offered(thisPc: false, admin, extension, button.IsVisible);
    }
    /// <summary>The account's rights and the extension read again now (the self-check, after changing what its Jellyfin answers).</summary>
    public Task AskRightsAsync() => Session is { } session ? OfferDownloadsAsync(session) : Task.CompletedTask;
    private DateTimeOffset _rightsAsked;
    /// <summary>« Décalage de l’audio » of the player, in seconds: kept from one title to the next until Mira quits, never saved.</summary>
    public double AudioDelay { get; set; }
    /// <summary>Mira's extension just installed from the settings: what it brings (Téléchargements) is offered at once.</summary>
    public void ExtensionInstalled() { if (Session is { } session) _ = OfferDownloadsAsync(session); }
    /// <summary>
    /// « Chercher une meilleure version » of a film or an episode (an administrator): Téléchargements, its search made, only
    /// the versions better than its file; the one chosen replaces it on the PC of Jellyfin, never kept beside it.
    /// </summary>
    public void FindBetter(MediaItem item)
    {
        if (Session is null || Watching) return;
        _ = NavigateAsync(new DownloadsPage(this, item));
    }
    /// <summary>Whether the rail offers an entry now (the self-check).</summary>
    public bool Offers(string key) => _railButtons.TryGetValue(key, out var button) && button.IsVisible;

    // —— « Tes choix gardés sur ton Jellyfin » ——
    // The languages, the intros, the tracks of each series and the titles removed from « Continuer à regarder » follow the
    // account (AccountChoices). A change here goes a moment later; the window coming forward brings what changed elsewhere.
    private readonly DispatcherTimer _choicesSoon = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTimeOffset _choicesAsked = DateTimeOffset.MinValue;
    private bool _choicesBusy, _choicesAgain;
    /// <summary>Something kept on Jellyfin changed on this Mac, or may have changed elsewhere: Mira and Jellyfin compare them shortly.</summary>
    public void ChoicesChanged()
    {
        if (Session is null || InParty) return;
        _choicesSoon.Stop(); _choicesSoon.Start();
    }
    private async Task SyncChoicesAsync()
    {
        if (Session is not { } session) return;
        if (_choicesBusy) { _choicesAgain = true; return; }
        _choicesBusy = true; _choicesAsked = DateTimeOffset.UtcNow;
        try
        {
            var update = await AccountChoices.SyncAsync(session.Client, session.Store, () => (Settings.AudioLanguage, Settings.SubtitleLanguage, Settings.AutoSkip), session.Lifetime);
            if (!ReferenceEquals(session, Session)) return;
            if (update.SettingsChanged)
            {
                if (update.AudioLanguage is { } audio) Settings.AudioLanguage = audio;
                if (update.SubtitleLanguage is { } subtitles) Settings.SubtitleLanguage = subtitles;
                if (update.AutoSkip is { } skip) Settings.AutoSkip = skip;
                Profile.SaveSettings(Settings);
                // The settings on screen show the new ones.
                if (Current is SettingsPage) { var fresh = new SettingsPage(this); Host(fresh); await fresh.LoadAsync(); }
            }
            if (update.Hidden is not null && Current is HomePage) await RefreshPagesAsync(fromServer: true);
        }
        // Jellyfin or the disk not answering now: what changed here keeps its time for the next try.
        catch (Exception ex) when (Errors.Expected(ex)) { }
        finally
        {
            _choicesBusy = false;
            if (_choicesAgain) { _choicesAgain = false; ChoicesChanged(); }
        }
    }

    // —— Invitations ——
    /// <summary>An invitation opened in Mira (a mira:// link), kept until an account of a Jellyfin is open to answer it.</summary>
    private Together.Invitation? _invite;
    /// <summary>Jellyfin's WebSocket is open for this session: GroupJoined will be heard.</summary>
    private bool _listening;
    /// <summary>
    /// « Ouvrir dans Mira » of Mira web (a mira:// link, given by macOS to App): once signed in, Mira asks before
    /// joining the group (« Rejoindre »); not signed in yet, the sign-in page gets the invitation's Jellyfin.
    /// </summary>
    public void OpenLink(string text)
    {
        if (Together.ParseAppLink(text) is not { } invite) { Notice(L("Mira ne sait pas ouvrir ce lien.", "Mira can’t open this link.")); return; }
        _inviteWait?.Cancel(); _inviteWait = null;
        _invite = invite;
        _ = OfferInviteAsync();
    }
    private async Task OfferInviteAsync()
    {
        if (_invite is not { } invite) return;
        if (invite.Party is { } code)
        {
            await OfferPartyAsync(invite, code);
            return;
        }
        if (Session is not { } session)
        {
            if (Login is not { } login) return;
            login.Server = invite.Server; Notice(Together.SignInToJoin);
            return;
        }
        for (var i = 0; !_listening && i < 40 && ReferenceEquals(session, Session); i++) await Task.Delay(250);
        if (!ReferenceEquals(session, Session) || !ReferenceEquals(_invite, invite)) return;
        _invite = null;
        List<Together.Group> groups;
        try { groups = await session.Client.SyncGroupsAsync(session.Lifetime); }
        catch (UnauthorizedAccessException) { Notice(WatchGroup.NoSyncPlay); return; }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); return; }
        if (!ReferenceEquals(session, Session)) return;
        var answer = Together.Answer(invite, groups, session.Connection.Server, Group.Active ? Group.Group?.GroupId : null);
        Activate();
        if (answer is { Kind: Together.InviteKind.Join, Group: { } group })
            Notice(answer.Text, L("Rejoindre", "Join"), () => _ = Group.EnterAsync((client, ct) => client.SyncJoinAsync(group.GroupId, ct)), sticky: true);
        else if (answer.Kind == Together.InviteKind.Waiting)
            Notice(answer.Text, L("Attendre", "Wait"), () => WaitForInvite(session, invite), sticky: true);
        else Notice(answer.Text);
    }
    /// <summary>
    /// A party link (mira://party/…, from its page): on the Jellyfin this app is signed in to, straight into the host's
    /// group, or waiting for it to open; anywhere else (another Jellyfin, no account here), its guest in Mira itself
    /// (MainWindow.Party.cs).
    /// </summary>
    private async Task OfferPartyAsync(Together.Invitation invite, string code)
    {
        if (Session is not { } session || InParty)
        {
            // Still starting: asked once the sign-in or the account shows.
            if (Session is null && Login is null) return;
            _invite = null;
            await AskPartyAsync(invite.Server, code);
            return;
        }
        _invite = null;
        string? host;
        // Not answered by this app's own Jellyfin: the party is on its own server anyway.
        try { host = await session.Client.PartyHostAsync(code, session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { host = null; }
        if (!ReferenceEquals(session, Session) || _invite is not null) return;
        Activate();
        if (host is null) { await AskPartyAsync(invite.Server, code); return; }
        if (host == session.Connection.UserName) { Notice(L("C’est ton propre lien de party : envoie-le à ceux qui veulent te rejoindre.", "This is your own party link: send it to whoever wants to join you.")); return; }
        // Jellyfin says GroupJoined through the WebSocket: open first, or the answer would go unheard.
        for (var i = 0; !_listening && i < 40 && ReferenceEquals(session, Session); i++) await Task.Delay(250);
        if (!ReferenceEquals(session, Session)) return;
        var party = Together.PartyInvitation(session.Connection.Server, host);
        List<Together.Group> groups;
        try { groups = await session.Client.SyncGroupsAsync(session.Lifetime); }
        catch (UnauthorizedAccessException) { Notice(WatchGroup.NoSyncPlay); return; }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); return; }
        if (!ReferenceEquals(session, Session)) return;
        var answer = Together.Answer(party, groups, session.Connection.Server, Group.Active ? Group.Group?.GroupId : null);
        if (answer is { Kind: Together.InviteKind.Join, Group: { } group }) await Group.EnterAsync((client, ct) => client.SyncJoinAsync(group.GroupId, ct));
        else if (answer.Kind == Together.InviteKind.Waiting) WaitForInvite(session, party);
        else Notice(answer.Text);
    }
    /// <summary>An invitation waited for (« Attendre »): its group joined once its host opens it.</summary>
    private CancellationTokenSource? _inviteWait;
    /// <summary>
    /// « Attendre »: the group of an invitation joined as soon as its host opens it (Jellyfin's groups read every few
    /// seconds, three hours at most), unless another group, another invitation or another account comes first.
    /// </summary>
    private void WaitForInvite(Session session, Together.Invitation invite)
    {
        if (!ReferenceEquals(session, Session)) return;
        _inviteWait?.Cancel();
        var wait = _inviteWait = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        wait.CancelAfter(TimeSpan.FromHours(3));
        Notice(Together.WaitingLead, L("Annuler", "Cancel"), () => wait.Cancel(), sticky: false);
        _ = WaitAsync();
        async Task WaitAsync()
        {
            Together.Group group;
            try { group = await Together.WaitForGroupAsync(ReadAsync, invite, TimeSpan.FromSeconds(4), wait.Token); }
            catch (OperationCanceledException) { return; }
            if (!ReferenceEquals(_inviteWait, wait) || !ReferenceEquals(session, Session)) return;
            _inviteWait = null;
            if (Group.Active) return;
            Activate();
            await Group.EnterAsync((client, ct) => client.SyncJoinAsync(group.GroupId, ct));
        }
        async Task<IReadOnlyList<Together.Group>?> ReadAsync(CancellationToken ct)
        {
            try { return await session.Client.SyncGroupsAsync(ct); }
            catch (UnauthorizedAccessException) { Notice(WatchGroup.NoSyncPlay); wait.Cancel(); return null; }
            catch (Exception ex) when (Errors.Expected(ex) && !ct.IsCancellationRequested) { return null; }
        }
    }
    /// <summary>« Se déconnecter »: the session ends here and on Jellyfin, and with <paramref name="erase"/> this account's data leaves this Mac.</summary>
    public async Task SignOutAsync(bool erase = false)
    {
        await StopPlaybackAsync();
        Group.Gone();
        var session = Session; Session = null; SetPhoto(null);
        // Off this Mac first, so that quitting while Jellyfin is asked leaves no session saved; the keychain may ask the
        // person, never on the UI thread.
        await Task.Run(Profile.ClearConnection);
        // Then Jellyfin, a few seconds at most: an unreachable server never holds the sign-out.
        var closed = session is not null && await session.SignOutAsync();
        var roots = OfflineRoots;
        ForgetOfflineState();
        var erased = erase && session is not null ? await AccountData.EraseAsync(Profile.DirectoryPath, session.Connection) : (bool?)null;
        // The titles kept on this Mac go with the rest of the account's data.
        if (erased is not null && session is not null) erased = await Task.Run(() => OfflineCopies.Erase(roots, session.Connection)) && erased.Value;
        var message = session is null ? null : AccountData.SignedOut(L("ce Mac", "this Mac"), closed, erased);
        // What is left to do on Jellyfin stays on the sign-in page; a toast would leave after a few seconds.
        ShowLogin(closed ? null : message);
        if (session is null) return;
        if (_overlay.Children.FirstOrDefault() is LoginPage login) login.Server = session.Connection.Server;
        if (closed) Notice(message!);
    }
    private async Task CloseSessionAsync()
    {
        Group.Gone(); _listening = false; _choicesSoon.Stop(); StopEpisodeNotices();
        ForgetOfflineState();
        if (Session is { } session) { Session = null; await session.DisposeAsync(); }
    }

    // —— Navigation ——
    public Page? Current { get; private set; }
    public LoginPage? Login => _overlay.Children.OfType<LoginPage>().FirstOrDefault();
    /// <summary>The page behind a rail entry: "home", "search", "Movie", "Series", "library", "downloads" or "settings".</summary>
    public void Go(string key) => NavigateRoot(key);
    private void NavigateRoot(string key)
    {
        if (Session is null || InParty) return;
        if (Watching) return;
        // Its own entry again while its page shows (the one the rail opened, not a page reached from it): as on Windows.
        if (_history.Count == 0 && Current is { } shown && shown.Rail == key) { shown.ShowAgain(); return; }
        // The rail starts a new history, as on the Windows app.
        _history.Clear(); _forward.Clear();
        // Left before: back as it was, then refreshed quietly.
        if (_kept.TryGetValue(key, out var kept)) { _ = ShowPageAsync(kept, root: true, fresh: false); return; }
        Page page = key switch
        {
            "home" => new HomePage(this),
            "search" => new BrowsePage(this, BrowseKind.Search),
            "Movie" => new BrowsePage(this, BrowseKind.Movies),
            "Series" => new BrowsePage(this, BrowseKind.Series),
            "library" => new BrowsePage(this, BrowseKind.All),
            "offline" => new OfflinePage(this),
            "downloads" => new DownloadsPage(this),
            _ => new SettingsPage(this)
        };
        if (KeptPages.Contains(key)) _kept[key] = page;
        _ = ShowPageAsync(page, root: true, fresh: true);
    }
    /// <summary>
    /// The rail's pages as they were left, as on Windows (PageMemory): Accueil, Recherche, Films, Séries and the library
    /// come back with their filters, search, titles and scroll, then refreshed quietly. A list reached from a title page
    /// (a person, a studio, a collection) is not a page of the rail and is not kept. Forgotten with the account.
    /// </summary>
    private readonly Dictionary<string, Page> _kept = [];
    private static readonly string[] KeptPages = ["home", "search", "Movie", "Series", "library"];
    /// <summary>
    /// <paramref name="page"/> shown (none: the login or a party hides them all). A page that may come back (one of the
    /// rail's kept, the history, the way forward) stays in the window, hidden: taken out and put back, Avalonia styled it
    /// all again (20 to 70 ms of the interface's thread for Accueil or Films, measured by --smooth-check, at each change of
    /// page). The others leave it.
    /// </summary>
    private void Host(Page? page)
    {
        // A page turn under the fingers, stopped where it was: this page shows instead.
        EndSlide(turn: false);
        var left = Current;
        if (page is not null && !_host.Children.Contains(page)) _host.Children.Add(page);
        Current = page;
        if (left is not null && left != page)
        {
            // Never the keyboard's focus in a page put away: what is typed would go to a field nobody sees.
            if (left.IsKeyboardFocusWithin) FocusManager?.ClearFocus();
            left.IsVisible = false;
        }
        if (page is not null) page.IsVisible = true;
        foreach (var gone in _host.Children.OfType<Page>().Where(x => x != page && !_kept.ContainsValue(x) && !_history.Contains(x) && !_forward.Contains(x)).ToList())
            _host.Children.Remove(gone);
    }
    /// <summary>A page reached from the one shown (a title, a person…): Back returns to the one shown.</summary>
    public Task NavigateAsync(Page page) => ShowPageAsync(page, root: false, fresh: true);
    /// <param name="root">A page of the rail: it starts a new history, as on Windows, so that its entry chosen again finds it.</param>
    /// <param name="fresh">Filled for the first time; otherwise a kept page, only refreshed.</param>
    private async Task ShowPageAsync(Page page, bool root, bool fresh)
    {
        if (!root && Current is { } current && current != page) _history.Add(current);
        _forward.Clear();
        Host(page);
        Light(page.Rail);
        page.Opacity = 0;
        page.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
        Dispatcher.UIThread.Post(() => page.Opacity = 1, DispatcherPriority.Loaded);
        try { if (fresh) await page.LoadAsync(); else await page.RefreshAsync(); _unanswered = false; }
        catch (Exception ex) when (Errors.Expected(ex)) { Say(ex); }
    }
    private void Light(string rail)
    {
        foreach (var (key, button) in _railButtons) button.Classes.Set("active", key == rail);
    }
    /// <summary>One step back: the player first, then the previous page.</summary>
    public bool Back()
    {
        if (Player is { IsMini: false } player) { player.SetMini(true); return true; }
        if (_history.Count == 0) return false;
        var previous = _history[^1]; _history.RemoveAt(_history.Count - 1);
        if (Current is { } current) _forward.Add(current);
        Host(previous); Light(previous.Rail);
        _ = previous.RefreshAsync();
        return true;
    }
    /// <summary>The rail's group button: shown with its count while this Mac is in a group, what it says in its tooltip.</summary>
    private void ShowGroupRail()
    {
        if (!Group.Active || Group.Group is not { } group || Session is not { } session) { _groupRail.IsVisible = false; return; }
        _groupCount.Text = Together.CountText(group);
        var said = Together.IndicatorText(group, session.Connection.UserName);
        ToolTip.SetTip(_groupRail, said); Avalonia.Automation.AutomationProperties.SetName(_groupRail, said);
        _groupRail.IsVisible = true;
    }
    /// <summary>The group button's menu: who is in the group, what holds it, its title, « Inviter » and « Quitter ».</summary>
    private async Task GroupMenuAsync()
    {
        if (!Group.Active || Group.Group is null || Session is not { } session) return;
        // The group's title, named: one request, left out when Jellyfin is slow to answer.
        MediaItem? title = null;
        if (Group.Queue is { } queue && Player?.Item?.Id != queue.ItemId)
        {
            try { title = await session.Client.ItemAsync(queue.ItemId, session.Lifetime).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is TimeoutException || Errors.Expected(ex)) { }
        }
        if (!Group.Active || Group.Group is not { } group || !ReferenceEquals(session, Session)) return;
        var menu = new ContextMenu();
        void Add(string label, Action? action)
        {
            var entry = new MenuItem { Header = label, IsEnabled = action is not null };
            if (action is not null) entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add(Together.GroupText(group) + (group.Participants.Count > 0 ? L(" : ", ": ") + string.Join(", ", group.Participants) : ""), null);
        if (Together.GroupStatus(group, session.Connection.UserName) is { Alert: true } status) Add(status.Text, null);
        menu.Items.Add(new Separator());
        if (Player is { IsMini: true } mini && Group.PlaysFor(mini)) Add(L("Revenir au lecteur", "Back to the player"), () => mini.SetMini(false));
        else if (title is not null) Add(L($"Voir « {Together.TitleName(title)} »", $"View “{Together.TitleName(title)}”"), () => Open(title));
        if (!InParty) Add(L("Inviter quelqu’un…", "Invite someone…"), () => _ = Group.InviteAsync());
        Add(L("Quitter le groupe", "Leave the group"), () => _ = Group.LeaveAsync());
        menu.PlacementTarget = _groupRail; menu.Placement = PlacementMode.RightEdgeAlignedBottom; menu.HorizontalOffset = 8;
        menu.Open(_groupRail);
    }
    public void Open(MediaItem item)
    {
        // A saga plays nothing by itself: its titles, in the order they came out.
        if (item.Type == "BoxSet") { _ = NavigateAsync(new BrowsePage(this, BrowseKind.All, saga: item)); return; }
        // An episode opens its series' page, at its season.
        if (item.Type == "Episode" && item.SeriesId is not null) { _ = NavigateAsync(new DetailPage(this, new MediaItem { Id = item.SeriesId, Name = item.SeriesName ?? item.Name, Type = "Series" }, item)); return; }
        _ = NavigateAsync(new DetailPage(this, item));
    }
    /// <summary>A person's titles: <paramref name="director"/> says what they did, null when it is not known (found by a search).</summary>
    public void OpenPerson(PersonInfo person, bool? director) => _ = NavigateAsync(new BrowsePage(this, BrowseKind.All, person, director));
    /// <summary>A studio's titles (« Studio Ghibli »), newest first.</summary>
    public void OpenStudio(StudioInfo studio) => _ = NavigateAsync(new BrowsePage(this, BrowseKind.All, studio: studio));
    /// <summary>Films or Séries (as the title is) filtered on a genre, as in Mira web.</summary>
    public void OpenGenre(string genre, string type) => _ = NavigateAsync(new BrowsePage(this, type == "Series" ? BrowseKind.Series : BrowseKind.Movies, genre: genre));

    // —— Playback ——
    /// <param name="groupStart">Put on by the group (<see cref="WatchGroup"/>), from there.</param>
    public async Task PlayAsync(MediaItem item, bool fromStart = false, long? groupStart = null)
    {
        if (Session is not { } session) return;
        if (MpvPlayer.FindLibrary(Settings.MpvPath) is null) { Notice(L("Le moteur de lecture mpv est introuvable. Réinstalle Mira depuis son image disque.", "The mpv video engine is missing. Reinstall Mira from its disk image.")); return; }
        // In a group, Lecture puts the title on for everyone, this Mac included, at the moment Jellyfin says.
        if (Group.Active && groupStart is null)
        {
            try { if (item.Type == "Series") item = await PlayerPage.EpisodeOfAsync(session, item); }
            catch (Exception ex) when (Errors.Expected(ex)) { Notice(ex is IOException ? ex.Message : Errors.Friendly(ex)); return; }
            // Another title than the one the group has on: asked first, rather than changed for everyone unannounced.
            if (Group.ConfirmReplace(item, fromStart)) return;
            if (Together.AsksToReplace(Group.Group, Group.Queue, item.Id)) { _ = Group.OfferReplaceAsync(item, fromStart); return; }
            if (await Group.AskAsync(item, fromStart)) return;
            groupStart = Group.Queue?.StartTicks ?? 0;
        }
        await StopPlaybackAsync();
        var player = new PlayerPage(this);
        Player = player;
        // A friend's party keeps its card under the player, in sight again when it closes.
        foreach (var shown in _overlay.Children.Where(child => !ReferenceEquals(child, _party)).ToList()) _overlay.Children.Remove(shown);
        _overlay.Children.Add(player);
        // The keyboard leaves the page under the player: Lecture, focused by its click, would take Return there.
        LeavePageFocus(player);
        if (!await player.StartAsync(item, fromStart, groupStart)) await CloseOverlayPlayerAsync(player);
    }
    public async Task StopPlaybackAsync()
    {
        if (Player is not { } player) return;
        await player.StopAsync();
        await CloseOverlayPlayerAsync(player);
    }
    /// <summary>The player shrunk or expanded: the page under it redraws its progress, the keyboard goes back to it.</summary>
    internal void MiniChanged(PlayerPage player)
    {
        if (player != Player) return;
        if (player.IsMini) { Focus(); _ = RefreshPagesAsync(); } else LeavePageFocus(player);
    }
    /// <summary>The player over the whole window: the focus is no longer left on a control of the page it covers.</summary>
    private void LeavePageFocus(PlayerPage player)
    {
        if (FocusManager?.GetFocusedElement() is Visual focused && !player.IsVisualAncestorOf(focused)) FocusManager.ClearFocus();
    }
    internal async Task CloseOverlayPlayerAsync(PlayerPage player)
    {
        if (Player != player) return;
        Player = null;
        // « Now Playing » empty, the media keys back to the other apps.
        MacNowPlaying.Clear();
        // « Image dans l’image »: its window goes with the playback (the video already let go by StopAsync).
        player.ClosePip();
        // The group plays on without this Mac.
        Group.SetAway(true);
        _overlay.Children.Remove(player);
        ReleasePointer();
        // In couch mode the window stays full screen: it is the TV's.
        if (WindowState == WindowState.FullScreen && !InCouchMode) WindowState = WindowState.Normal;
        Profile.SaveSettings(Settings);
        Focus();
        await RefreshPagesAsync();
    }
    // —— Pointer ——
    private IPointer? _mouse;
    /// <summary>A mouse capture that outlived its press: no button is down any more.</summary>
    private static bool Stale(PointerEventArgs e) => e.Pointer is { Type: PointerType.Mouse, Captured: not null }
        && e.GetCurrentPoint(null).Properties is { IsLeftButtonPressed: false, IsRightButtonPressed: false, IsMiddleButtonPressed: false };
    private void Unstick(PointerEventArgs e) { _mouse = e.Pointer; if (Stale(e)) e.Pointer.Capture(null); }
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        _mouse = e.Pointer;
        if (!Stale(e)) return;
        e.Pointer.Capture(null);
        // This turn of the wheel goes where the pointer is, as the next ones will.
        var at = e.GetPosition(this);
        if (this.InputHitTest(at) is not Avalonia.Interactivity.Interactive under) return;
        e.Handled = true;
        under.RaiseEvent(new PointerWheelEventArgs(under, e.Pointer, this, at, e.Timestamp, e.GetCurrentPoint(this).Properties, e.KeyModifiers, e.Delta));
    }
    /// <summary>A menu closed or the player gone: no capture left behind, even one outside this window (a menu's).</summary>
    internal void ReleasePointer() { if (_mouse?.Captured is not null) _mouse.Capture(null); }
    /// <summary>After a playback or a change elsewhere, the page on screen redraws its marks.</summary>
    public async Task RefreshPagesAsync(bool fromServer = false)
    {
        if (Watching && fromServer) return;
        Session?.Forget();
        if (Current is { } page)
        {
            try { await page.RefreshAsync(); _unanswered = false; }
            catch (Exception ex) when (Errors.Expected(ex)) { if (!fromServer) Say(ex); }
        }
    }
    /// <summary>A page Jellyfin did not answer since: « Tailscale intégré » joining the tailnet brings it again (<see cref="TailnetJoined"/>).</summary>
    private bool _unanswered;
    /// <summary>The page shown could not load from Jellyfin, since it last did (the checks).</summary>
    public bool Unanswered => _unanswered;
    /// <summary>
    /// An error at the bottom of the window: through « Tailscale intégré », why Jellyfin was not reached (its PC off, the
    /// node signed out…) with the button that helps (BuiltInTailscale.Says); otherwise in Mira's words.
    /// </summary>
    public void Say(Exception error)
    {
        NotAnswered(error);
        if (Tailnet.Says(Session?.Connection.Server, error) is { } said) Notice(said.Text, said.Action, said.Act, sticky: said.Action is not null);
        else Notice(Errors.Friendly(error), tone: NoticeTone.Error);
    }
    /// <summary>Jellyfin did not answer: the page reloads once « Tailscale intégré » joins (TailnetJoined).</summary>
    internal void NotAnswered(Exception error) { if (error is HttpRequestException or OperationCanceledException or TimeoutException) _unanswered = true; }
    /// <summary>« Tailscale intégré » joined the tailnet (started, back from no network): the page Jellyfin did not answer loads again.</summary>
    public void TailnetJoined()
    {
        if (!_unanswered || Session is null) return;
        _unanswered = false;
        _ = RefreshPagesAsync(fromServer: true);
    }

    // —— Shared actions ——
    public async Task SetPlayedAsync(MediaItem item, bool played)
    {
        if (Session is not { } session) return;
        try
        {
            await session.Client.SetPlayedAsync(item.Id, played);
            await Task.Run(() => session.Store.ForgetProgress(item.Id));
            item.UserData.Played = played; item.UserData.PlaybackPositionTicks = 0;
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
    }
    public async Task SetFavoriteAsync(MediaItem item, bool favorite)
    {
        if (Session is not { } session) return;
        try
        {
            await session.Client.SetFavoriteAsync(item.Id, favorite);
            item.UserData.IsFavorite = favorite;
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
    }
    /// <summary>The right-click menu of a card; <paramref name="resume"/>: one of « Continuer à regarder », which it can leave.</summary>
    public ContextMenu Menu(MediaItem item) => Menu(item, resume: false);
    public ContextMenu Menu(MediaItem item, bool resume)
    {
        var menu = new ContextMenu();
        void Add(string label, Action action) { var entry = new MenuItem { Header = label }; entry.Click += (_, _) => action(); menu.Items.Add(entry); }
        // A collection (a saga or one made by hand) plays nothing by itself: its titles do, from their page.
        if (item.Type == "BoxSet")
        {
            Add(L("Voir la collection", "See the collection"), () => Open(item));
            Add(item.UserData.IsFavorite ? L("Retirer des favoris", "Remove from favorites") : L("Ajouter aux favoris", "Add to favorites"), () => _ = SetFavoriteAsync(item, !item.UserData.IsFavorite));
            if (CollectionRights.Rename) Add(L("Renommer…", "Rename…"), () => _ = RenameCollectionAsync(item));
            if (CollectionRights.Manage) Add(L("Supprimer la collection…", "Delete collection…"), () => AskDeleteCollection(item));
            return menu;
        }
        Add(item.Progress > 0 && !item.UserData.Played ? L("Reprendre", "Resume") : L("Lecture", "Play"), () => _ = PlayAsync(item));
        if (item.Progress > 0 && !item.UserData.Played) Add(L("Lire depuis le début", "Play from the beginning"), () => _ = PlayAsync(item, fromStart: true));
        Add(L("Plus d’infos", "More info"), () => Open(item));
        Add(item.UserData.Played ? L("Marquer comme non vu", "Mark as unwatched") : L("Marquer comme vu", "Mark as watched"), () => _ = SetPlayedAsync(item, !item.UserData.Played));
        Add(item.UserData.IsFavorite ? L("Retirer des favoris", "Remove from favorites") : L("Ajouter aux favoris", "Add to favorites"), () => _ = SetFavoriteAsync(item, !item.UserData.IsFavorite));
        if (resume) Add(L("Retirer de Continuer à regarder", "Remove from Continue watching"), () => _ = HideFromResumeAsync(item));
        if (TitleCollections.Holds(item) && CollectionRights.Manage)
        {
            Add(L("Ajouter à une collection…", "Add to collection…"), () => _ = CollectAsync(item));
            if (Current is BrowsePage { Saga: { } shown }) Add(L("Retirer de la collection", "Remove from collection"), () => _ = PutInCollectionAsync(item, shown, false));
        }
        if (OfflineMenuItem(item) is { } offline) menu.Items.Add(offline);
        if (item.Type is "Movie" or "Episode" && Offers("downloads")) Add(L("Chercher une meilleure version", "Find a better version"), () => FindBetter(item));
        return menu;
    }

    /// <summary>
    /// « Retirer de Continuer à regarder », as on Windows: the film or series leaves the row until it is played again; a
    /// title in progress also loses its resume point, on Jellyfin too, so that no other device offers it. Kept with this
    /// account's choices on Jellyfin (<see cref="AccountChoices"/>); « Annuler » puts both back.
    /// </summary>
    public async Task HideFromResumeAsync(MediaItem item)
    {
        if (Session is not { } session) return;
        var group = ContinueWatching.Group(item); var ticks = item.UserData.PlaybackPositionTicks;
        var resumable = ticks > 0 && !item.UserData.Played;
        var change = Task.Run(() => Errors.TryCache(() => { session.Store.HideFromResume(group, DateTimeOffset.UtcNow); if (resumable) session.Store.ForgetProgress(item.Id); }));
        _resumeChange = change;
        await change;
        Notice(L($"« {item.DisplayTitle} » ne sera plus dans Continuer à regarder.", $"“{item.DisplayTitle}” removed from Continue watching."), L("Annuler", "Undo"), () => _ = ShowInResumeAsync(session, item, group, resumable ? ticks : 0), sticky: false);
        await RefreshPagesAsync();
        try { if (resumable) await session.Client.SetPlaybackPositionAsync(item.Id, 0, session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex) + L(" Le titre est retiré de Mira seulement.", " The title is removed from Mira only."), tone: NoticeTone.Error); }
        ChoicesChanged();
    }
    /// <summary>The last removal still being saved: « Annuler » waits for it, so that it cannot land after the undo.</summary>
    private Task _resumeChange = Task.CompletedTask;
    private async Task ShowInResumeAsync(Session session, MediaItem item, string group, long ticks)
    {
        await _resumeChange;
        if (!ReferenceEquals(session, Session)) return;
        await Task.Run(() => Errors.TryCache(() => session.Store.HideFromResume(group, null)));
        try { if (ticks > 0) await session.Client.SetPlaybackPositionAsync(item.Id, ticks, session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
        ChoicesChanged();
        await RefreshPagesAsync(fromServer: true);
    }

    // Collections made by hand (TitleCollections), as Windows and Mira web make them; only for an account Jellyfin lets
    // manage collections, never a file moved.
    /// <summary>What this account may do with collections, once Jellyfin said: a card's menu, drawn at once, reads it.</summary>
    public TitleCollections.Rights CollectionRights { get; private set; } = TitleCollections.Rights.None;
    private async Task LoadCollectionRightsAsync(Session session)
    {
        try { var rights = await session.Client.CollectionRightsAsync(); if (ReferenceEquals(session, Session)) CollectionRights = rights; }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    /// <summary>« Ajouter à une collection… »: the collections ticked, a new one named.</summary>
    public async Task CollectAsync(MediaItem item)
    {
        if (Session is not { } session) return;
        var sheet = new CollectionSheet(session, item);
        await sheet.ShowAsync(this);
        if (sheet.Said is { } said) Notice(said);
        if (sheet.Changed) await RefreshPagesAsync();
    }
    /// <summary>« Renommer… » (an administrator); its page, shown, opens again under its new name.</summary>
    public async Task RenameCollectionAsync(MediaItem collection)
    {
        if (Session is not { } session) return;
        var sheet = new CollectionSheet(session, null, collection);
        await sheet.ShowAsync(this);
        if (sheet.Said is { } said) Notice(said);
        if (sheet.NewName is { } name && Current is BrowsePage { Saga: { } shown } && shown.Id == collection.Id) { Back(); Open(new MediaItem { Id = collection.Id, Name = name, Type = "BoxSet" }); }
        else if (sheet.Changed) await RefreshPagesAsync();
    }
    /// <summary>« Supprimer la collection… », asked first: its list goes, never its titles.</summary>
    public void AskDeleteCollection(MediaItem collection) =>
        Notice(TitleCollections.DeleteQuestion(collection.Name), L("Supprimer", "Delete"), () => _ = DeleteCollectionAsync(collection), sticky: true);
    private async Task DeleteCollectionAsync(MediaItem collection)
    {
        if (Session is not { } session) return;
        try
        {
            await session.Client.DeleteCollectionAsync(collection.Id);
            if (Current is BrowsePage { Saga: { } shown } && shown.Id == collection.Id) { Back(); return; }
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
    }
    /// <summary>A title taken out of a collection from its page (« Annuler » puts it back), or put back.</summary>
    private async Task PutInCollectionAsync(MediaItem item, MediaItem collection, bool inside)
    {
        if (Session is not { } session) return;
        try
        {
            await session.Client.SetInCollectionAsync(collection.Id, [item.Id], inside);
            // Put back (« Annuler »), the title shows in its collection again: nothing more to say.
            if (!inside) Notice(TitleCollections.Removed(collection.Name), L("Annuler", "Undo"), () => _ = PutInCollectionAsync(item, collection, true), sticky: false);
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Watching || CurrentSheet is not null) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape || command && e.Key == Key.OemOpenBrackets) { if (Back()) e.Handled = true; return; }
        if (command && e.Key == Key.OemCloseBrackets) { if (Forward()) e.Handled = true; return; }
        if (command && e.Source is not TextBox && ZoomKey(e)) { e.Handled = true; return; }
        if (command && e.Key is Key.F or Key.K) { NavigateRoot("search"); e.Handled = true; return; }
        if (command && e.Key == Key.OemComma) { NavigateRoot("settings"); e.Handled = true; return; }
        // I brings the mini-player back over the window, as on Windows; never while typing.
        if (e.Key == Key.I && e.KeyModifiers == KeyModifiers.None && Player is { IsMini: true } mini && e.Source is not TextBox) { mini.SetMini(false); e.Handled = true; }
    }
    private bool _closing, _started;
    /// <summary>From the first close request: the window is hidden and the session is being closed.</summary>
    public bool IsClosing => _closing;
    /// <summary>
    /// How long quitting may wait for Jellyfin, the party's server and « Tailscale intégré » (⌘Q, « Redémarrer »): an
    /// unreachable server never keeps Mira in the Dock without a window. What was not sent waits in SQLite for the next launch.
    /// </summary>
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(4);
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // macOS asks: shutting down, restarting, logging out, the Dock's « Quitter », an update of macOS. Refused, macOS
        // would stop it all (« Mira a interrompu le redémarrage »); accepted, Mira ends as soon as this returns: nothing
        // awaited here would ever run. What must not be lost is done now, synchronously, in two seconds at most.
        if (AskedBySystem(e)) { QuitNow(); return; }
        if (_closing) return;
        // ⌘W or the red button on a Mac: the window goes, Mira stays in the Dock (MainWindow.MacWindow.cs). ⌘Q quits.
        if (StaysInDock && !_quitting && e.CloseReason == WindowCloseReason.WindowClosing) { e.Cancel = true; _ = CloseToDockAsync(); return; }
        e.Cancel = true; _closing = true;
        // Each step on its own and the close guaranteed: a full disk or a failing service must not skip the steps after
        // it, nor leave Mira running without a window. Failures are logged (errors.log).
        try
        {
            // Hidden in the Dock, it was remembered as it went.
            if (!InDock) RememberWindow();
            Hide();
            // « Image dans l’image » goes with it; closed once the playback has stopped (CloseOverlayPlayerAsync).
            Player?.Pip?.Hide();
            // What plays is stopped first, its last report queued: Jellyfin, the party's server and Tailscale are given
            // QuitWait together, then Mira goes all the same.
            var leaving = LeaveAsync();
            await Task.WhenAny(leaving, Task.Delay(QuitWait));
            if (!leaving.IsCompleted) ErrorLog.Append(Profile.DirectoryPath, new TimeoutException($"Quitting: still waiting for the network after {QuitWait.TotalSeconds} s, left behind."));
            Attempt(() => Profile.SaveSettings(Settings));
            // Last: the new version waiting beside this one replaces it once this process has ended.
            Attempt(() => Updates.InstallAtQuit(this));
        }
        finally { Close(); }
        async Task LeaveAsync()
        {
            // The last progress report still leaves while the window is already gone.
            await AttemptAsync(StopPlaybackAsync);
            Attempt(() => Profile.SaveSettings(Settings));
            // A friend's party guest leaves the friend's Jellyfin, and this Mac, with the window.
            await AttemptAsync(EndGuestSessionAsync);
            await AttemptAsync(CloseSessionAsync);
            // After the last reports to Jellyfin, which may go through it.
            await AttemptAsync(Tailnet.DisposeAsync);
        }
    }
    private void Attempt(Action step) { try { step(); } catch (Exception ex) { ErrorLog.Append(Profile.DirectoryPath, ex); } }
    private async Task AttemptAsync(Func<Task> step) { try { await step(); } catch (Exception ex) { ErrorLog.Append(Profile.DirectoryPath, ex); } }

    /// <summary>
    /// A close that macOS asked for (applicationShouldTerminate: of Avalonia's delegate, which waits for the answer), and
    /// not Mira's own ⌘Q menu item or Close(), which may take their time.
    /// </summary>
    public static bool AskedBySystem(WindowClosingEventArgs e) => !e.IsProgrammatic && e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;

    /// <summary>
    /// Quitting at once, macOS waiting: where the window was, where the title playing was (queued for Jellyfin, sent now
    /// or by the next launch, and in « Continuer à regarder »), the settings, and the new version waiting beside this
    /// one, swapped in once Mira has ended. The rest (the session, Tailscale's node, the player's engine) ends with the
    /// process: the node quits when its input closes, Jellyfin forgets the session's socket.
    /// </summary>
    public void QuitNow()
    {
        if (_quitNow) return;
        _quitNow = _closing = _quitting = true;
        Attempt(() => { if (!InDock && IsVisible) RememberWindow(); });
        Attempt(() => Player?.KeepAtQuit());
        Attempt(() => Profile.SaveSettings(Settings));
        Attempt(() => Updates.InstallAtQuit(this));
    }
    private bool _quitNow;
    /// <summary>Quit at once, as macOS asks: for the self-check, which then reads what it kept.</summary>
    public bool QuitAtOnce => _quitNow;
}
