using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Playback;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>A screen of the library: home, a grid, a title page, the settings.</summary>
public abstract class Page : UserControl
{
    protected MainWindow Shell { get; }
    protected Page(MainWindow shell) => Shell = shell;
    /// <summary>The rail entry lit while this page shows.</summary>
    public virtual string Rail => "";
    /// <summary>Fills the page; called once, when it first shows.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
    /// <summary>Progress, watched or favourite marks changed (after a playback, a menu action): redraw what shows them.</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;
}

/// <summary>
/// Mira's window: the rail of the Windows app on the left, the current page beside it, the player above everything
/// while it plays. Pages are kept in a history that Échap, ⌘[ and the back button walk.
/// </summary>
public sealed class MainWindow : Window
{
    public Profile Profile { get; }
    public PlayerSettings Settings { get; }
    public Session? Session { get; private set; }
    public string[] Args { get; }
    private readonly Grid _root = new();
    private readonly ContentControl _host = new();
    private readonly Panel _overlay = new();
    private readonly StackPanel _railItems = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _rail;
    private readonly Border _toast;
    private readonly TextBlock _toastText = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
    private readonly Button _toastAction = new() { Classes = { "quiet" }, Padding = new Thickness(12, 6), Margin = new Thickness(12, 0, 0, 0) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    /// <summary>Jellyfin's change notifications come in bursts (a scan, a playback elsewhere): one refresh after the last.</summary>
    private readonly DispatcherTimer _serverChanged = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _avatar = new() { FontSize = 15, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Page> _history = [];
    private readonly Dictionary<string, Button> _railButtons = [];
    private Action? _toastClick;
    public PlayerPage? Player { get; private set; }
    /// <summary>Top room left for the macOS window buttons, which sit over Mira's own content.</summary>
    public double TitleBarInset { get; }

    public MainWindow(string[] args, Profile profile)
    {
        Args = args; Profile = profile; Settings = profile.LoadSettings();
        Title = "Mira"; MinWidth = 960; MinHeight = 600;
        Width = Settings.WindowWidth > 0 ? Settings.WindowWidth : 1440; Height = Settings.WindowHeight > 0 ? Settings.WindowHeight : 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (Settings.WindowMaximized) WindowState = WindowState.Maximized;
        if (OperatingSystem.IsMacOS())
        {
            // The title bar disappears into the page, as in Apple's own apps: the traffic lights float over the rail.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 30;
            TitleBarInset = 30;
        }
        _root.ColumnDefinitions = new ColumnDefinitions("Auto,*");
        _rail = BuildRail();
        _root.Children.Add(_rail);
        Grid.SetColumn(_host, 1); _root.Children.Add(_host);
        Grid.SetColumnSpan(_overlay, 2); _root.Children.Add(_overlay);
        _toast = BuildToast();
        Grid.SetColumnSpan(_toast, 2); _root.Children.Add(_toast);
        if (TitleBarInset > 0)
        {
            // The strip where the title bar was: drags the window, and a double click zooms it, as on any Mac window.
            var strip = new Border { Height = TitleBarInset, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Margin = new Thickness(84, 0, 0, 0) };
            strip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else BeginMoveDrag(e);
            };
            Grid.SetColumnSpan(strip, 2); _root.Children.Add(strip);
        }
        Content = _root;
        _toastTimer.Tick += (_, _) => HideNotice();
        _serverChanged.Tick += (_, _) => { _serverChanged.Stop(); _ = RefreshPagesAsync(fromServer: true); };
        KeyDown += OnKeyDown;
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed) Back(); };
        Closing += OnClosing;
        Opened += async (_, _) => await StartAsync();
    }

    private Border BuildRail()
    {
        var brand = new Icon("brand", 34) { Foreground = Ui.Ink, Margin = new Thickness(0, 6, 0, 22) };
        foreach (var (key, icon, tip) in new[] { ("home", "home", "Accueil"), ("search", "search", "Rechercher · ⌘F"), ("Movie", "film", "Films"), ("Series", "series", "Séries"), ("library", "library", "Toute la bibliothèque") })
        {
            var button = Ui.IconButton(icon, tip, () => NavigateRoot(key), "rail", 23);
            _railButtons[key] = button; _railItems.Children.Add(button);
        }
        var settings = Ui.IconButton("settings", "Réglages · ⌘,", () => NavigateRoot("settings"), "rail", 23);
        _railButtons["settings"] = settings;
        var avatar = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Ui.Brush("#2A2A2E"), Child = _avatar, Margin = new Thickness(0, 6, 0, 0) };
        ToolTip.SetTip(avatar, "Compte Jellyfin");
        var bottom = Ui.Column(8, settings, avatar); bottom.HorizontalAlignment = HorizontalAlignment.Center;
        var dock = new DockPanel { Margin = new Thickness(0, 18 + TitleBarInset, 0, 22) };
        DockPanel.SetDock(brand, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(brand); dock.Children.Add(bottom); dock.Children.Add(_railItems);
        return new Border { Width = 84, Background = Ui.Brush("#0A0A0B"), BorderBrush = Ui.Brush("#151517"), BorderThickness = new Thickness(0, 0, 1, 0), Child = dock };
    }
    private Border BuildToast()
    {
        _toastAction.Click += (_, _) => { var click = _toastClick; HideNotice(); click?.Invoke(); };
        var row = new DockPanel();
        DockPanel.SetDock(_toastAction, Dock.Right);
        row.Children.Add(_toastAction); row.Children.Add(_toastText);
        return new Border
        {
            Background = Ui.Brush("#F21A1A1D"), BorderBrush = Ui.Brush("#2E2E33"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 12), Margin = new Thickness(0, 0, 0, 28), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Child = row, IsVisible = false, Opacity = 0, MaxWidth = 720,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }]
        };
    }

    /// <summary>A short message at the bottom of the window, with an optional action (« Réessayer », « Installer »…).</summary>
    public void Notice(string text, string? action = null, Action? click = null)
    {
        _toastText.Text = text; _toastClick = click;
        _toastAction.Content = action; _toastAction.IsVisible = action is not null;
        _toast.IsVisible = true; _toast.Opacity = 1;
        _toastTimer.Stop(); _toastTimer.Interval = TimeSpan.FromSeconds(action is null ? 5 : 9); _toastTimer.Start();
    }
    private void HideNotice() { _toastTimer.Stop(); _toast.Opacity = 0; DispatcherTimer.RunOnce(() => { if (_toast.Opacity == 0) _toast.IsVisible = false; }, TimeSpan.FromMilliseconds(200)); }
    public string? NoticeText => _toast.IsVisible ? _toastText.Text : null;

    private async Task StartAsync()
    {
        if (Args.Contains("--self-check")) { await Checks.SelfCheck.RunAsync(this); return; }
        if (Profile.LoadConnection() is { } connection) await ActivateAsync(connection);
        else ShowLogin();
        _ = Updates.CheckAsync(this);
    }

    // —— Account ——
    public void ShowLogin(string? error = null)
    {
        _rail.IsVisible = false;
        var login = new LoginPage(this, error);
        _overlay.Children.Clear(); _overlay.Children.Add(login);
        _history.Clear(); _host.Content = null;
    }
    /// <summary>Opens the library of a signed-in account.</summary>
    public async Task ActivateAsync(Connection connection, HttpMessageHandler? handler = null)
    {
        await CloseSessionAsync();
        JellyfinClient.DeviceName = "Mac";
        Session = new Session(connection, Profile, handler);
        if (handler is null) Session.Listen(() => Dispatcher.UIThread.Post(() => { _serverChanged.Stop(); _serverChanged.Start(); }));
        _avatar.Text = connection.UserName.Length > 0 ? connection.UserName[..1].ToUpperInvariant() : "?";
        _overlay.Children.Clear(); _rail.IsVisible = true;
        _history.Clear();
        await NavigateAsync(new HomePage(this));
        if (Session.Store.DamagedCopy is not null) Notice("Le cache local de ce compte était illisible : il a été recréé.");
    }
    public async Task SignOutAsync()
    {
        await StopPlaybackAsync();
        Profile.ClearConnection();
        var server = Session?.Connection.Server;
        await CloseSessionAsync();
        ShowLogin();
        if (_overlay.Children.FirstOrDefault() is LoginPage login && server is not null) login.Server = server;
    }
    private async Task CloseSessionAsync()
    {
        if (Session is { } session) { Session = null; await session.DisposeAsync(); }
    }

    // —— Navigation ——
    public Page? Current => _host.Content as Page;
    public LoginPage? Login => _overlay.Children.OfType<LoginPage>().FirstOrDefault();
    /// <summary>The page behind a rail entry: "home", "search", "Movie", "Series", "library" or "settings".</summary>
    public void Go(string key) => NavigateRoot(key);
    private void NavigateRoot(string key)
    {
        if (Session is null) return;
        if (Player is not null) return;
        // The rail starts a new history, as on the Windows app.
        _history.Clear();
        Page page = key switch
        {
            "home" => new HomePage(this),
            "search" => new BrowsePage(this, BrowseKind.Search),
            "Movie" => new BrowsePage(this, BrowseKind.Movies),
            "Series" => new BrowsePage(this, BrowseKind.Series),
            "library" => new BrowsePage(this, BrowseKind.All),
            _ => new SettingsPage(this)
        };
        _ = NavigateAsync(page);
    }
    public async Task NavigateAsync(Page page)
    {
        if (Current is { } current && current != page) _history.Add(current);
        _host.Content = page;
        Light(page.Rail);
        page.Opacity = 0;
        page.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
        Dispatcher.UIThread.Post(() => page.Opacity = 1, DispatcherPriority.Loaded);
        try { await page.LoadAsync(); }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex)); }
    }
    private void Light(string rail)
    {
        foreach (var (key, button) in _railButtons) button.Classes.Set("active", key == rail);
    }
    /// <summary>One step back: the player first, then the previous page.</summary>
    public bool Back()
    {
        if (Player is not null) { _ = StopPlaybackAsync(); return true; }
        if (_history.Count == 0) return false;
        var previous = _history[^1]; _history.RemoveAt(_history.Count - 1);
        _host.Content = previous; Light(previous.Rail);
        _ = previous.RefreshAsync();
        return true;
    }
    public void Open(MediaItem item)
    {
        // An episode opens its series' page.
        if (item.Type == "Episode" && item.SeriesId is not null) item = new MediaItem { Id = item.SeriesId, Name = item.SeriesName ?? item.Name, Type = "Series" };
        _ = NavigateAsync(new DetailPage(this, item));
    }
    public void OpenPerson(PersonInfo person, bool director) => _ = NavigateAsync(new BrowsePage(this, BrowseKind.All, person, director));

    // —— Playback ——
    public async Task PlayAsync(MediaItem item, bool fromStart = false)
    {
        if (Session is null) return;
        if (MpvPlayer.FindLibrary(Settings.MpvPath) is null) { Notice("Le moteur de lecture mpv est introuvable. Réinstalle Mira depuis son image disque."); return; }
        await StopPlaybackAsync();
        var player = new PlayerPage(this);
        Player = player;
        _overlay.Children.Clear(); _overlay.Children.Add(player);
        if (!await player.StartAsync(item, fromStart)) await CloseOverlayPlayerAsync(player);
    }
    public async Task StopPlaybackAsync()
    {
        if (Player is not { } player) return;
        await player.StopAsync();
        await CloseOverlayPlayerAsync(player);
    }
    internal async Task CloseOverlayPlayerAsync(PlayerPage player)
    {
        if (Player != player) return;
        Player = null;
        _overlay.Children.Remove(player);
        if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal;
        Profile.SaveSettings(Settings);
        Focus();
        await RefreshPagesAsync();
    }
    /// <summary>After a playback or a change elsewhere, the page on screen redraws its marks.</summary>
    public async Task RefreshPagesAsync(bool fromServer = false)
    {
        if (Player is not null && fromServer) return;
        Session?.Forget();
        if (Current is { } page)
        {
            try { await page.RefreshAsync(); }
            catch (Exception ex) when (Errors.Expected(ex)) { if (!fromServer) Notice(Errors.Friendly(ex)); }
        }
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
            Notice($"« {item.DisplayTitle} » {(played ? "marqué comme vu" : "marqué comme non vu")}.");
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex)); }
    }
    public async Task SetFavoriteAsync(MediaItem item, bool favorite)
    {
        if (Session is not { } session) return;
        try
        {
            await session.Client.SetFavoriteAsync(item.Id, favorite);
            item.UserData.IsFavorite = favorite;
            Notice(favorite ? $"« {item.DisplayTitle} » est dans tes favoris." : $"« {item.DisplayTitle} » n’est plus dans tes favoris.");
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex)); }
    }
    /// <summary>The right-click menu of a card.</summary>
    public ContextMenu Menu(MediaItem item)
    {
        var menu = new ContextMenu();
        void Add(string label, Action action) { var entry = new MenuItem { Header = label }; entry.Click += (_, _) => action(); menu.Items.Add(entry); }
        Add(item.Progress > 0 && !item.UserData.Played ? "Reprendre" : "Lecture", () => _ = PlayAsync(item));
        if (item.Progress > 0 && !item.UserData.Played) Add("Lire depuis le début", () => _ = PlayAsync(item, fromStart: true));
        Add("Plus d’infos", () => Open(item));
        Add(item.UserData.Played ? "Marquer comme non vu" : "Marquer comme vu", () => _ = SetPlayedAsync(item, !item.UserData.Played));
        Add(item.UserData.IsFavorite ? "Retirer des favoris" : "Ajouter aux favoris", () => _ = SetFavoriteAsync(item, !item.UserData.IsFavorite));
        return menu;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Player is { } player) { player.HandleKey(e); return; }
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape || command && e.Key == Key.OemOpenBrackets) { if (Back()) e.Handled = true; return; }
        if (command && e.Key is Key.F or Key.K) { NavigateRoot("search"); e.Handled = true; return; }
        if (command && e.Key == Key.OemComma) { NavigateRoot("settings"); e.Handled = true; }
    }
    private bool _closing;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true; _closing = true;
        if (WindowState is WindowState.Normal) (Settings.WindowWidth, Settings.WindowHeight) = (Math.Round(Width), Math.Round(Height));
        Settings.WindowMaximized = WindowState == WindowState.Maximized;
        Hide();
        // The last progress report still leaves while the window is already gone.
        await StopPlaybackAsync();
        Profile.SaveSettings(Settings);
        await CloseSessionAsync();
        Close();
    }
}
