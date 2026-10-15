using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow : Window
{
    private readonly LocalProfile _profile;
    private PlayerSettings _settings;
    private JellyfinClient? _client;
    private LibraryStore? _store;
    private SyncService? _sync;
    private ImageCache? _images;
    private MetadataCache? _metadata;
    private bool _keyboardNavigation;
    private CancellationTokenSource _connectionLifetime = new();
    private CancellationTokenSource? _browseCancellation;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _externalRefresh = new() { Interval = TimeSpan.FromMilliseconds(600) };
    /// <summary>Checks every 45 s whether the page needs a refresh Jellyfin's WebSocket did not ask for (see Cadence.RefreshDue).</summary>
    private readonly DispatcherTimer _fallbackRefresh = new() { Interval = Cadence.RefreshUnheard };
    /// <summary>Jellyfin's WebSocket is open: library changes are heard as they happen.</summary>
    private bool _listening;
    /// <summary>When the last refresh that reached Jellyfin started.</summary>
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    /// <summary>When a refresh last failed to reach Jellyfin; null once one reaches it again (see SyncStatus).</summary>
    private DateTimeOffset? _offlineSince;
    /// <summary>The list shown and what Jellyfin gave for it, « Continuer à regarder » included.</summary>
    private readonly Catalog _catalog = new();
    private List<MediaItem> _episodes = [];
    private MediaItem? _hero, _detail;
    private bool _demo, _initializing = true;
    private bool _startupComplete;
    private int _viewVersion, _detailVersion;
    private string? _testMedia;
    private readonly string[] _args;
    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    public MainWindow(string[] args)
    {
        _args = args;
        var dataArg = Array.IndexOf(args, "--data");
        _profile = new LocalProfile(dataArg >= 0 && dataArg + 1 < args.Length ? args[dataArg + 1] : null);
        _settings = _profile.LoadSettings();
        Motion.Reduced = _settings.ReduceMotion; Motion.FollowsWindows = !ValidationRun;
        // Before the pages load: they read these colours once.
        FollowHighContrast(); _textScale = TextScale();
        InitializeComponent();
        // After InitializeComponent: the XAML's 1480 × 930 would otherwise replace the size chosen here.
        FitToScreen();
        InitializeFilters();
        InitializePreview();
        InitializePlayerControls();
#if MIRA_VALIDATION
        InitializePlayerProbe();
#endif
        InitializeWindowsIntegration();
        InitializeTorLink();
        InitializeUpdates();
        InitializePower();
        InitializeHeroClock();
        InitializeSnapLayouts();
        // Windows' text size is read again each time Mira comes back to the front: changed in Settings meanwhile.
        Activated += (_, _) => { UpdateHeroClock(); _textScale = TextScale(); if (UpdateZoom()) FitPages(); }; Deactivated += (_, _) => { UpdateHeroClock(); ClosePreview(); };
        // Windows' animation effects turned on or off: the banner's rotation follows at once.
        SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateHeroClock(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Tab) _keyboardNavigation = true; };
        PreviewMouseDown += (_, e) =>
        {
            _keyboardNavigation = false;
            // The banner paused for keyboard focus resumes once the mouse is used again.
            if (Hero.IsKeyboardFocusWithin) UpdateHeroClock();
            // The mouse's "back" side button steps back like Escape.
            if (e.ChangedButton == MouseButton.XButton1 && GoBack()) e.Handled = true;
        };
        _noticeTimer.Tick += NoticeTick;
        StateChanged += (_, _) =>
        {
            UpdateMaximizeButtons();
            // Minimized: the artwork kept for reuse goes (what is on the page stays), and comes back from the disk.
            if (WindowState == WindowState.Minimized) _images?.ReleaseMemory();
        };
        PosterCards.IsVisibleChanged += (_, _) => ScheduleVisibleArtwork();
        DpiChanged += (_, _) => { if (_images is { } cache) FitArtwork(cache); };
        LocationChanged += (_, _) => TrackNormalCorner(); SizeChanged += (_, _) => TrackNormalCorner();
        SettingsVersion.Text = $"Mira {JellyfinClient.AppVersion} · Jellyfin + mpv";
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefreshAsync(); };
        _externalRefresh.Tick += async (_, _) =>
        {
            _externalRefresh.Stop();
            // A library change may be a title TorLink just brought in.
            _ = CheckTorLinkAvailabilityAsync();
            EpisodesMayHaveArrived();
            if (!_playing && !_demo) { _metadata?.Clear(); _heroSeries.Clear(); await RefreshAsync(quiet: true); }
        };
        _fallbackRefresh.Tick += async (_, _) =>
        {
            if (!_demo && !_playing && _client is not null && IsActive && Cadence.RefreshDue(_listening, _lastRefresh, DateTimeOffset.UtcNow)) await RefreshAsync(quiet: true);
        };
        _playerTimer.Tick += PlayerTick;
        InitializeEpisodeNotices(); InitializeTogether();
        VolumeSlider.Value = VolumeBoost.Clamp(_settings.Volume); ShowVolumeLevel();
        var testArg = Array.IndexOf(args, "--test-media");
        if (testArg >= 0 && testArg + 1 < args.Length) _testMedia = args[testArg + 1];
        Loaded += OnLoaded;
        _initializing = false;
    }
    /// <summary>Validation runs set their own sizes, off screen: nothing is remembered from them.</summary>
    private bool ValidationRun => _args.Any(x => x is "--visual-check" or "--player-check" or "--public-gallery" or "--windows-check" or "--torlink-check" or "--offscreen");
    /// <summary>
    /// The size the window had when Mira last closed, or the default one (1480 × 930), kept within the screen it opens
    /// on, with that screen's scale: a 1080p screen at 125 % has about 1536 × 826 usable, at 150 % about 1280 × 688 (see
    /// <see cref="ScreenFit"/>). Where it was when that screen is still there, otherwise centred on the screen of the
    /// pointer; maximized only when it was maximized on closing.
    /// </summary>
    private void FitToScreen()
    {
        if (ValidationRun) return;
        var size = _settings.WindowWidth > 0 && _settings.WindowHeight > 0 ? new ScreenFit.Extent(_settings.WindowWidth, _settings.WindowHeight) : new(Width, Height);
        var remembered = _settings.WindowX is { } x && _settings.WindowY is { } y ? (x, y) : ((double X, double Y)?)null;
        if (ScreenFit.Place(size, new(MinWidth, MinHeight), Screens.All(), remembered, Screens.Pointer()) is not { } placement) return;
        var fit = placement.Fit;
        (MinWidth, MinHeight, Width, Height) = (fit.Minimum.Width, fit.Minimum.Height, fit.Size.Width, fit.Size.Height);
        // Placed in device pixels once the window exists, before it shows. Maximized afterwards: WPF applies it on showing,
        // on the screen the window is then on (set before, the window would be created maximized wherever Windows puts it).
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) =>
        {
            Screens.Move(new System.Windows.Interop.WindowInteropHelper(this).Handle, placement.Window);
            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        };
    }
    /// <summary>Where the window was last seen neither maximized, minimized nor fullscreen (device pixels).</summary>
    private (double X, double Y)? _normalCorner;
    private void TrackNormalCorner()
    {
        if (ValidationRun || _fullscreenWindow is { Active: true } || !IsVisible) return;
        if (Screens.NormalCorner(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } corner) _normalCorner = corner;
    }
    /// <summary>The window as the person left it; fullscreen and the mini-player are passing states, not a size to reopen at.</summary>
    private void RememberWindow()
    {
        if (ValidationRun || _fullscreen || _miniPlayer) return;
        var maximized = WindowState == WindowState.Maximized || WindowState == WindowState.Minimized && _lastVisibleState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Size(ActualWidth, ActualHeight) : RestoreBounds.Size;
        // Maximized or minimized, RestoreBounds is the size it returns to.
        if (bounds.IsEmpty || !(bounds.Width > 0 && bounds.Height > 0) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return;
        (_settings.WindowWidth, _settings.WindowHeight, _settings.WindowMaximized) = (Math.Round(bounds.Width), Math.Round(bounds.Height), maximized);
        TrackNormalCorner();
        if (_normalCorner is { } corner) (_settings.WindowX, _settings.WindowY) = corner;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_args.Contains("--demo")) await ShowDemoAsync();
        else if (_profile.LoadConnection() is { } connection) await ActivateConnectionAsync(connection);
        else { LoginOverlay.Visibility = Visibility.Visible; _ = RefreshTorLinkLibrariesAsync(); if (_invite is not null) _ = OfferInviteAsync(); }
        await RevealStartupAsync();
#if MIRA_VALIDATION
        if (await RunRequestedCheckAsync()) return;
#endif
        _fallbackRefresh.Start();
        StartKeepingAwake();
        _ = RefreshEngineAsync();
        ShowStartupScreen();
        _ = NoticeStaleRuleAsync();
        _ = NoticeTailscaleExpiryAsync();
        _ = KeepMiraWebCurrentAsync();
        // Once, after the first refresh, which would otherwise hide it at once, and beside what the start already said
        // (a cache recreated, an update that failed, Jellyfin offline) rather than in its place.
        if (_profile.SettingsUnreadable) AddNotice("Tes réglages étaient illisibles : Mira est reparti des réglages par défaut." + (_profile.SettingsCopy is { } copy ? $" L’ancien fichier est gardé sous le nom {Path.GetFileName(copy)}." : ""));
        if (_testMedia is not null && _args.Contains("--autoplay")) await PlayAsync(DemoLibrary.Items()[0]);
    }
    private Task? _startupRevealTask;
    private Task RevealStartupAsync() => _startupRevealTask ??= RevealStartupCoreAsync();
    private async Task RevealStartupCoreAsync()
    {
        // Warm only the first screen. A slow/offline image must never lock navigation.
        await Dispatcher.InvokeAsync(LoadVisibleArtwork, DispatcherPriority.Loaded);
        var artwork = PosterCards.Children.OfType<MediaCard>().Take(8)
            .Concat(ResumeCards.Children.OfType<MediaCard>().Take(5)).Select(x => x.ImageReady).Append(_heroReady);
        try { await Task.WhenAll(artwork).WaitAsync(TimeSpan.FromMilliseconds(900)); }
        catch (TimeoutException) { }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Motion.HideAsync(StartupVeil, 220);
        _startupComplete = true;
    }
    private async Task ShowDemoAsync()
    {
        await DisconnectServicesAsync();
        _demo = true; LoginOverlay.Visibility = Visibility.Collapsed; DemoBadge.Visibility = Visibility.Visible;
        ServerLabel.Text = "Mode démonstration"; SyncLabel.Text = "○  Données fictives · rien n’est envoyé";
        AvatarLetter.Text = "M";
        LibrariesPanel.Children.Clear(); AddLibraryButton("Films & séries", null);
        _catalog.View = "home"; _catalog.ParentId = null; SearchBox.Text = "";
        PopulateFilters(new CatalogFilters { Genres = DemoLibrary.Items().SelectMany(x => x.Genres).Distinct().ToArray(), Years = DemoLibrary.Items().Select(x => x.ProductionYear ?? 0).Where(x => x > 0).Distinct().ToArray() });
        RenderDemo();
        // Without Jellyfin, only folders chosen in Réglages → TorLink receive downloads.
        _ = RefreshTorLinkLibrariesAsync();
    }
    private void Login_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !ConnectButton.IsEnabled) return;
        e.Handled = true; Connect_Click(ConnectButton, new());
    }
    /// <summary>The http:// address on the Internet whose warning was shown: the next click on it signs in anyway.</summary>
    private string? _warnedPlainHttp;
    private void ServerBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Another address is another question: the warning given for the previous one no longer answers it.
        if (_warnedPlainHttp is null || !ConnectButton.IsEnabled || ServerBox.Text == _warnedPlainHttp) return;
        _warnedPlainHttp = null; ConnectText.Text = "Se connecter"; LoginError.Text = "";
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false; ConnectText.Text = "Connexion…"; LoginError.Text = "";
        JellyfinClient? client = null;
        try
        {
            if (string.IsNullOrWhiteSpace(UsernameBox.Text)) throw new UserInputException("Indique ton nom d’utilisateur Jellyfin.");
            // "192.168.1.20", "nas:8096" or the address of Jellyfin's web page: find the server before sending the password.
            ConnectText.Text = "Recherche du serveur…";
            var server = await ServerAddress.DiscoverAsync(ServerBox.Text);
            ServerBox.Text = server.Address;
            if (server.Address != _warnedPlainHttp && await ServerAddress.ExposesPasswordAsync(server.Address)) { _warnedPlainHttp = server.Address; LoginError.Text = ServerAddress.PlainHttpWarning; return; }
            ConnectText.Text = "Connexion…";
            client = new JellyfinClient(new(server.Address, "", UsernameBox.Text.Trim(), "", _profile.DeviceId));
            var connection = await client.LoginAsync(UsernameBox.Text.Trim(), PasswordBox.Password);
            // Another account or server replaces the saved session: the old one ends on its Jellyfin too, since nothing
            // here could revoke it afterwards. The same account on the same server is replaced by Jellyfin itself.
            var previous = _profile.LoadConnection();
            _profile.SaveConnection(connection); PasswordBox.Clear(); _warnedPlainHttp = null;
            await ActivateConnectionAsync(connection);
            OfferGuide("Connecté. Voici l’essentiel.");
            if (previous is not null && (previous.Server != connection.Server || previous.UserId != connection.UserId)) _ = EndReplacedSessionAsync(previous);
        }
        catch (Exception ex) when (IsExpected(ex)) { LoginError.Text = Friendly(ex); }
        finally { client?.Dispose(); ConnectButton.IsEnabled = true; ConnectText.Text = _warnedPlainHttp == ServerBox.Text ? "Se connecter quand même" : "Se connecter"; }
    }
    private async Task EndReplacedSessionAsync(Connection previous)
    {
        bool closed;
        using (var old = new JellyfinClient(previous)) closed = await old.LogoutAsync();
        if (!closed) AddNotice($"Ton ancienne session ({previous.UserName}) n’a pas pu être fermée sur son serveur Jellyfin : retire ce PC de ses appareils (Tableau de bord, Appareils).");
    }
    private async Task ActivateConnectionAsync(Connection connection)
    {
        await DisconnectServicesAsync();
        _demo = false; DemoBadge.Visibility = Visibility.Collapsed;
        var client = _client = new JellyfinClient(connection);
        LibraryStore store; SyncService sync;
        // Opened (and checked page by page) off the UI thread: a large cache on a slow disk takes a moment.
        try { (store, sync) = await Task.Run(() => { var opened = new LibraryStore(_profile.DirectoryPath, AccountData.LibraryProfile(connection)); return (opened, new SyncService(client, opened)); }); }
        // Even a new cache cannot be written (full disk, data folder read-only): no library without it. A damaged one
        // is replaced by LibraryStore itself.
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            if (!ReferenceEquals(_client, client)) { client.Dispose(); return; }
            _client.Dispose(); _client = null;
            ServerBox.Text = connection.Server; UsernameBox.Text = connection.UserName; LoginError.Text = CacheUnavailable;
            LogoutButton.Visibility = Visibility.Visible; BackToLibrary.Visibility = Visibility.Collapsed; ShowSignInForm();
            if (LoginOverlay.Visibility != Visibility.Visible) Motion.Reveal(LoginOverlay);
            FocusLogin(); _ = RefreshTorLinkLibrariesAsync(); return;
        }
        // Another account or the demo took over meanwhile: this one stops here.
        if (!ReferenceEquals(_client, client)) { await sync.DisposeAsync(); client.Dispose(); return; }
        _store = store;
        var damaged = _store.DamagedCopy is not null;
        _catalog.Clear(); _episodes = []; _detail = null; _hero = null; _returnToDetail = null; _seasonThumbs.Clear();
        LibrariesPanel.Children.Clear(); DetailOverlay.Visibility = Visibility.Collapsed;
        // Never leave the previous account's posters on screen while this one loads.
        _catalog.Loading = true; RenderLibrary();
        _images = new ImageCache(_client, _profile.DirectoryPath); FitArtwork(_images); _sync = sync;
        _metadata = new MetadataCache(_client); ++_heroVersion;
        _sync.Changed += SyncChanged;
        _connectionLifetime = new(); _listening = false; _lastRefresh = DateTimeOffset.MinValue; _offlineSince = null;
        if (!_args.Contains("--visual-check")) _ = ListenSafelyAsync(_client, _connectionLifetime.Token);
        if (!_args.Contains("--visual-check")) StartEpisodeNotices();
        // Opened from a mira:// link before this account was: asked now.
        if (_invite is not null) _ = OfferInviteAsync();
        _ = LoadOfflineAsync(client);
        ServerLabel.Text = connection.UserName + " · Jellyfin"; AvatarLetter.Text = connection.UserName[..1].ToUpperInvariant();
        SettingsServerAddress.Text = connection.Server;
        LoginOverlay.Visibility = Visibility.Collapsed; _catalog.View = "home"; _catalog.ParentId = null; SearchBox.Text = "";
        try
        {
            var (cached, cachedResume, history, hidden) = await Task.Run(() => TryCache(() => (store.Load<ItemsResult>("home"), store.Load<List<MediaItem>>("resume"), store.RecentPlayback(), store.HiddenFromResume())));
            // All null when the cache could not be read.
            if (ReferenceEquals(store, _store)) { _catalog.RecentPlayback = history ?? []; _catalog.HiddenResume = hidden ?? []; }
            if (cached is not null && ReferenceEquals(store, _store)) { _catalog.Items = cached.Items; _catalog.TotalCount = cached.TotalRecordCount; _catalog.Resume = cachedResume ?? []; _catalog.Loading = false; RenderLibrary(); }
            await RefreshAsync();
            var libraries = await _client.LibrariesAsync(); LibrariesPanel.Children.Clear(); _homeLibraries = libraries;
            _filterBusy = true; LibraryFilter.Items.Clear(); LibraryFilter.Items.Add(new ComboBoxItem { Content = "Toutes les bibliothèques", Tag = "" }); LibraryFilter.SelectedIndex = 0;
            foreach (var library in libraries.Where(x => !LibrarySection.Groups(x.CollectionType))) AddLibraryButton(library.Name, library.Id);
            _filterBusy = false;
            PopulateFilters(await _client.FiltersAsync());
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        SyncChanged();
        // Said once the first refresh is done, which would otherwise hide it at once.
        if (damaged) SetNotice("Le cache local de ce compte était illisible : il a été recréé. Les envois de progression qui n’étaient pas encore partis sont perdus.", null, null, sticky: true);
        _ = RefreshTorLinkLibrariesAsync();
    }
    private async Task ListenSafelyAsync(JellyfinClient client, CancellationToken ct)
    {
        void Changed() { _externalRefresh.Stop(); _externalRefresh.Start(); }
        // Open again after a break: what changed meanwhile went unheard.
        void Listening(bool open) => Dispatcher.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested) return;
            var reopened = open && !_listening && _lastRefresh != DateTimeOffset.MinValue; _listening = open;
            if (reopened) { Changed(); RejoinGroup(client); }
        });
        void SyncPlay(string type, JsonElement data) => Dispatcher.BeginInvoke(() => { if (!ct.IsCancellationRequested) GroupMessage(client, type, data); });
        try { await client.ListenAsync(() => Dispatcher.BeginInvoke(Changed), ct, Listening, SyncPlay); }
        catch (OperationCanceledException) { }
    }
    /// <summary>Artwork decoded for the sharpest and widest of the screens connected now, and for the pages' zoom.</summary>
    private void FitArtwork(ImageCache cache)
    {
        var screens = Screens.All();
        cache.Scale = screens.Select(x => x.Scale).Where(x => double.IsFinite(x) && x > 0).DefaultIfEmpty(1).Max() * _zoom;
        cache.ScreenPixels = (int)Math.Round(screens.Select(x => x.WorkArea.Width).Where(x => double.IsFinite(x) && x > 0).DefaultIfEmpty(1920).Max());
    }
    private async Task DisconnectServicesAsync()
    {
        if (_playing) { await StopPlaybackAsync(); ReturnToLibrary(); }
        _heroClock.Stop(); _heroClockAttached = false; _heroCandidates = [];
        _heroSeries.Clear();
        _metadata = null; ClosePreview(); ++_detailVersion; ++_heroVersion; _hero = null;
        _connectionLifetime.Cancel(); _browseCancellation?.Cancel(); StopEpisodeNotices(); GroupGone(); ForgetOfflineState();
        if (_sync is not null) { _sync.Changed -= SyncChanged; await _sync.DisposeAsync(); _sync = null; }
        _client?.Dispose(); _client = null; _images = null; _store = null; _listening = false;
        _catalog.RecentPlayback = []; _catalog.HiddenResume = []; _catalog.Shelves = []; _homeLibraries = [];
        // Another account, the demo or none: this one's offline state is not theirs.
        _offlineShown = false; OfflineBadge.Visibility = Visibility.Collapsed;
    }
    private void AddLibraryButton(string name, string? id)
    {
        var button = new Button { Content = name, Style = (Style)FindResource("NavButton"), ToolTip = name };
        button.Click += async (_, _) => { _catalog.View = "library"; ResetFilters(); _filterBusy = true; _catalog.ParentId = id; LibraryFilter.SelectedItem = LibraryFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == (id ?? "")); _filterBusy = false; await NavigateLibraryAsync(); };
        LibrariesPanel.Children.Add(button);
        if (id is not null) LibraryFilter.Items.Add(new ComboBoxItem { Content = name, Tag = id });
    }
    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        _catalog.View = (string)((Button)sender).Tag; ResetFilters();
        await NavigateLibraryAsync();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null) SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SearchClear is not null) SearchClear.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_initializing || _filterBusy) return;
        _browseCancellation?.Cancel(); _searchTimer.Stop(); _searchTimer.Start();
    }
    private bool _scanning;
    /// <summary>
    /// « Actualiser » asks Jellyfin to look through its folders (what an added file needs), shows how far it is, then
    /// reloads the list and says what changed. Without administrator rights, only the list is reloaded, and Mira says why.
    /// </summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        _scanning = true; var before = _catalog.TotalCount; var view = (_catalog.View, _catalog.Favorites, _catalog.ParentId, SearchBox.Text); var shown = Task.Delay(700);
        var ct = _connectionLifetime.Token;
        SpinRefresh(true); RefreshLabel.Text = "Analyse…";
        try
        {
            var scanned = false;
            if (!_demo && _client is { } client)
            {
                var progress = new Progress<double?>(p => RefreshLabel.Text = p is { } share ? $"Analyse… {share * 100:0} %" : "Analyse…");
                scanned = await client.ScanAndWaitAsync(progress, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), ct);
                RefreshLabel.Text = "Chargement…";
            }
            await RefreshAsync(quiet: true); await shown;
            // Counted only on the list it started from: another view has another total.
            var added = view == (_catalog.View, _catalog.Favorites, _catalog.ParentId, SearchBox.Text) ? _catalog.TotalCount - before : 0;
            SetNotice(_demo ? "Démo à jour."
                : !scanned ? "Liste rechargée. Seul un administrateur de Jellyfin peut lui faire analyser les dossiers : un nouveau fichier apparaît après sa prochaine analyse."
                : added > 0 ? $"Bibliothèque à jour : {added} nouveau{(added > 1 ? "x" : "")} titre{(added > 1 ? "s" : "")}."
                : "Bibliothèque à jour : Jellyfin a vérifié tous tes dossiers.");
        }
        // Only Mira's own cancellation (another account, closing): HttpClient's 12 s limit is one too, and must be said.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        finally { _scanning = false; SpinRefresh(false); RefreshLabel.Text = "Actualiser"; }
    }
    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await RefreshAsync(more: true);
    private async Task RefreshAsync(bool more = false, bool quiet = false)
    {
        _searchTimer.Stop();
        if (_demo) { RenderDemo(); return; }
        // « Sur ce PC » asks Jellyfin nothing; a copy waiting for it is tried again.
        if (_catalog.View == "offline") { await ShowOfflinePageAsync(); PumpOffline(); return; }
        if (_client is null || _store is null) return;
        _browseCancellation?.Cancel(); _browseCancellation = new CancellationTokenSource();
        var ct = _browseCancellation.Token; var version = ++_viewVersion;
        var search = SearchBox.Text.Trim(); var home = _catalog.IsHome(search);
        var filters = home ? new CatalogQuery() : CurrentQuery();
        var key = _catalog.Key(search, filters);
        var client = _client; var store = _store;
        LoadMore.IsEnabled = false; var startOffset = LibraryScroll.VerticalOffset; var started = DateTimeOffset.UtcNow;
        if (!quiet && !more) { SyncLabel.Text = "↻  Actualisation…"; SpinRefresh(true); }
        try
        {
            // A background refresh keeps every page already loaded instead of shrinking the grid back to one page.
            var limit = quiet && !more ? Math.Max(JellyfinClient.PageSize, _catalog.Items.Count) : JellyfinClient.PageSize;
            var browse = client.BrowseAsync(_catalog.Types, _catalog.ParentId, search, more ? _catalog.Items.Count : 0, !home && _catalog.Favorites, ct, filters, limit);
            var people = !more && _catalog.SearchesPeople(search) ? client.PersonsAsync(search, ct) : null;
            Task<ItemsResult>? resume = home ? client.ResumeAsync(ct) : null;
            Task<ItemsResult>? next = home ? client.NextUpAsync(ct) : null;
            var shelving = home ? LoadShelvesAsync(client, ct) : null;
            var result = await browse;
            // The people are a bonus: the titles show even if Jellyfin does not answer for them.
            List<MediaItem> found = [];
            if (people is not null) try { found = await people; } catch (Exception ex) when (!ct.IsCancellationRequested && IsExpected(ex)) { }
            if (home) { await Task.WhenAll(resume!, next!, shelving!); await _seasonThumbs.LoadAsync(client, resume!.Result.Items.Concat(next!.Result.Items), ct); }
            ct.ThrowIfCancellationRequested(); if (version != _viewVersion) return;
            var resumeItems = home ? resume!.Result.Items : null;
            var shelves = home ? shelving!.Result : null;
            List<MediaItem>? recent = null; var seasonThumbs = _seasonThumbs.Snapshot();
            // SQLite work stays off the UI thread; the page only redraws when it is done.
            await Task.Run(() => TryCache(() =>
            {
                store.ApplyLocalProgress(result.Items);
                if (resumeItems is not null) { recent = store.RecentPlayback(); resumeItems = store.MergeResume(resumeItems, recent); _seasonThumbs.Apply(resumeItems, seasonThumbs); store.ApplyLocalProgress(resumeItems); store.ApplyLocalProgress(next!.Result.Items); foreach (var shelf in shelves!.Values) store.ApplyLocalProgress(shelf); store.Save("home", result); store.Save("resume", resumeItems); }
                else if (!more && search.Length == 0) store.Save(key, result);
            }), CancellationToken.None);
            ct.ThrowIfCancellationRequested(); if (version != _viewVersion) return;
            _catalog.Receive(result, more);
            if (!more) _catalog.People = found;
            if (home) { _catalog.Resume = resumeItems!; _catalog.NextUp = next!.Result.Items; _catalog.Shelves = shelves!; _catalog.RecentPlayback = ContinueWatching.History(_catalog.RecentPlayback.Concat(recent ?? [])); }
            _catalog.Loading = false; _lastRefresh = started; _offlineSince = null; RenderLibrary();
            // Jellyfin answers: the reports a failure left waiting go now rather than at their next try.
            if (_sync is { PendingCount: > 0 } sync) sync.Wake();
            PumpOffline();
            // Back to the top for a new list, unless the reader has already started scrolling it.
            if (!quiet && !more && Math.Abs(LibraryScroll.VerticalOffset - startOffset) < 1) SmoothScroll.Jump(LibraryScroll, 0);
            if (!quiet) HideNotice(transientOnly: true); SyncChanged();
        }
        // Only a refresh Mira cancelled: a server silent for 12 s ends the same way, and gets the offline notice below.
        // A background refresh keeps the page on screen instead: Jellyfin is often just slow then (a library scan).
        catch (OperationCanceledException) when (ct.IsCancellationRequested || quiet && !_catalog.Loading) { }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (version != _viewVersion) return;
            if (ex is UnauthorizedAccessException)
            {
                ServerBox.Text = client.Connection.Server; UsernameBox.Text = client.Connection.UserName; LoginError.Text = Friendly(ex);
                LogoutButton.Visibility = Visibility.Visible; BackToLibrary.Visibility = Visibility.Collapsed; ShowSignInForm();
                if (LoginOverlay.Visibility != Visibility.Visible) Motion.Reveal(LoginOverlay);
                FocusLogin();
            }
            else
            {
                var cached = more ? null : await Task.Run(() => TryCache(() => { var page = store.Load<ItemsResult>(home ? "home" : key); if (page is not null) TryCache(() => store.ApplyLocalProgress(page.Items)); return page; }));
                if (version != _viewVersion) return;
                if (cached is not null) { _catalog.Items = cached.Items; _catalog.TotalCount = cached.TotalRecordCount; }
                // Kept until a refresh, or a report delivered after this failure, reaches Jellyfin again: the sync
                // status no longer replaces it.
                var wasOffline = _sync is { } current && SyncStatus.Offline(current.LastSynced, _offlineSince);
                _offlineSince = DateTimeOffset.Now;
                // Said once as Jellyfin stops answering, not at each try (every 45 s): « Hors connexion » then stays in
                // the title bar until it answers again.
                if (!wasOffline) _ = SayUnansweredAsync(client, ex, Friendly(ex) + " Les informations déjà chargées restent disponibles.");
                SyncChanged();
                _catalog.Loading = false; RenderLibrary();
            }
        }
        finally { if (version == _viewVersion) { LoadMore.IsEnabled = true; SpinRefresh(false); } }
    }
    /// <summary>The refresh icon turns while a requested refresh is running.</summary>
    private void SpinRefresh(bool active)
    {
        // A list reload during a scan (Jellyfin reports each addition) leaves the scan's spinner turning.
        if (!active && _scanning) return;
        if (RefreshIcon.RenderTransform is not RotateTransform spin) { spin = new RotateTransform(); RefreshIcon.RenderTransformOrigin = new Point(.5, .5); RefreshIcon.RenderTransform = spin; }
        if (active && !Motion.Reduced) spin.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        else { spin.BeginAnimation(RotateTransform.AngleProperty, null); spin.Angle = 0; }
    }
    private void RenderDemo()
    {
        _catalog.ShowDemo(_demoItems ??= DemoLibrary.Items(), SearchBox.Text, CurrentQuery());
        RenderLibrary();
    }
    private List<MediaItem>? _demoItems;
    private static readonly Brush SyncWarning = Palette.Get("Warning"), SyncFine = Palette.Get("Success");
    private static Brush Frozen(string hex) { var brush = Brush(hex); brush.Freeze(); return brush; }
    /// <summary>Offline first: neither a delivered report nor the retry loop replaces it before Jellyfin answers again.</summary>
    private void SyncChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_sync is null || _demo) return;
        var (text, warning) = SyncStatus.Describe(_sync.PendingCount, _sync.Error, _sync.LastSynced, _offlineSince);
        if (SyncLabel.Text != text) SyncLabel.Text = text;
        var brush = warning ? SyncWarning : SyncFine;
        if (!ReferenceEquals(SyncLabel.Foreground, brush)) SyncLabel.Foreground = brush;
        ShowOffline(SyncStatus.Offline(_sync.LastSynced, _offlineSince), text);
    });
    private readonly DispatcherTimer _noticeTimer = new();
    private Action? _noticeAction;
    /// <summary>The message on screen stays until closed (see <see cref="SetNotice(string, string?, Action?, bool?)"/>).</summary>
    private bool _noticeSticky;
    private void SetNotice(string message) => SetNotice(message, null, null);
    /// <summary>Said under a message still on screen, which keeps its button, instead of replacing it.</summary>
    private void AddNotice(string message)
    {
        if (Notice.Visibility == Visibility.Visible && Notice.IsHitTestVisible && NoticeText.Text.Length > 0) SetNotice(NoticeText.Text + "\n" + message, NoticeAction.Content as string, _noticeAction, _noticeSticky);
        else SetNotice(message);
    }
    /// <summary>
    /// Messages slide in, are said by screen readers, then leave on their own once there was time to read them (see
    /// <see cref="Legibility.NoticeDuration"/>), never while the pointer or the keyboard focus is on them.
    /// <paramref name="action"/> adds a button, such as "Redémarrer" for a downloaded update: such a message stays until
    /// it is used or closed, as does one marked <paramref name="sticky"/> (an error that matters); sticky false lets a
    /// message with a button leave all the same (« Annuler »).
    /// </summary>
    private void SetNotice(string message, string? action, Action? onAction, bool? sticky = null)
    {
        _noticeAction = onAction; NoticeAction.Content = action;
        NoticeAction.Visibility = onAction is null ? Visibility.Collapsed : Visibility.Visible;
        // Shown before its text changes, so that a screen reader finds it there.
        if (Notice.Visibility != Visibility.Visible || !Notice.IsHitTestVisible) Motion.Reveal(Notice, 220, 10);
        NoticeText.Text = message;
        _noticeSticky = sticky ?? onAction is not null;
        _noticeTimer.Stop();
        if (!_noticeSticky) { _noticeTimer.Interval = Legibility.NoticeDuration(message); _noticeTimer.Start(); }
        Accessible.Say(NoticeText);
    }
    /// <param name="transientOnly">Leaves a message that stays until closed (an update ready, an error with « Réessayer »).</param>
    private void HideNotice(bool transientOnly = false)
    {
        if (transientOnly && _noticeSticky) return;
        _noticeTimer.Stop(); _noticeSticky = false; if (Notice.Visibility == Visibility.Visible) _ = Motion.HideAsync(Notice, 180);
    }
    private void NoticeAction_Click(object sender, RoutedEventArgs e) { var action = _noticeAction; HideNotice(); action?.Invoke(); }
    private void NoticeTick(object? sender, EventArgs e) { if (Notice.IsMouseOver || Notice.IsKeyboardFocusWithin) return; HideNotice(); }
    private static bool IsExpected(Exception e) => ExpectedErrors.Are(e);
    private static string Friendly(Exception ex) => ex switch
    {
        // Mira's own messages are written for you; .NET's (a file refused, a wrong argument) are not, nor in French.
        UnauthorizedAccessException => FromMira(ex) ? ex.Message : "Windows refuse à Mira l’accès à l’un de ses fichiers : vérifie que son dossier de données n’est pas en lecture seule.",
        UserInputException => ex.Message,
        ServerDiscoveryException => ex.Message,
        OperationCanceledException => "Jellyfin met trop de temps à répondre.",
        HttpRequestException { StatusCode: { } status } => JellyfinClient.StatusMessage(status),
        HttpRequestException => "Le serveur Jellyfin est momentanément inaccessible.",
        SqliteException => StorageUnavailable,
        _ => "L’opération n’a pas abouti. Vérifie la connexion et réessaie."
    };
    /// <summary>Thrown by Mira's own code, with a message written for the person using it.</summary>
    private static bool FromMira(Exception ex) => ex.TargetSite?.DeclaringType?.Assembly is { } assembly && (assembly == typeof(JellyfinClient).Assembly || assembly == typeof(MainWindow).Assembly);
    private const string CacheUnavailable = "Mira ne peut pas ouvrir le cache de ce compte dans son dossier de données : disque plein, dossier en lecture seule ou fichier utilisé par un autre programme. Libère de la place si besoin, puis relance Mira : il rouvrira ce compte sans te redemander ton mot de passe.";
    private const string StorageUnavailable = "Le stockage local de Mira est indisponible (disque plein ou fichier occupé) : libère de la place sur le disque, ou relance Mira.";
    /// <summary>The local cache is a bonus: a full disk, a locked or damaged database leaves Jellyfin's answer, or nothing, on screen.</summary>
    private static T? TryCache<T>(Func<T> read) { try { return read(); } catch (Exception ex) when (IsCacheFailure(ex)) { return default; } }
    private static void TryCache(Action write) { try { write(); } catch (Exception ex) when (IsCacheFailure(ex)) { } }
    private static bool IsCacheFailure(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException;
    private async void Demo_Click(object sender, RoutedEventArgs e) => await ShowDemoAsync();
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        ClosePreview(); ServerBox.Text = _client?.Connection.Server ?? "http://127.0.0.1:8096"; UsernameBox.Text = _client?.Connection.UserName ?? "";
        LoginArt.Source = _demo ? null : HeroImage.Source;
        LogoutButton.Visibility = _profile.LoadConnection() is null ? Visibility.Collapsed : Visibility.Visible; EraseAccountData.IsChecked = false;
        BackToLibrary.Visibility = _client is not null || _demo ? Visibility.Visible : Visibility.Collapsed;
        LoginError.Text = ""; ShowSignInForm(); Motion.Reveal(LoginOverlay); UpdateHeroClock(); FocusLogin();
    }
    /// <summary>The first empty field gets the caret, so typing can start at once.</summary>
    private void FocusLogin() => Dispatcher.BeginInvoke(() =>
    {
        if (LoginOverlay.Visibility != Visibility.Visible) return;
        if (string.IsNullOrWhiteSpace(UsernameBox.Text)) UsernameBox.Focus(); else PasswordBox.Focus();
    }, DispatcherPriority.Input);
    private async void CloseAccount_Click(object sender, RoutedEventArgs e) { if (_signingOut) return; PasswordBox.Clear(); await Motion.HideAsync(LoginOverlay); UpdateHeroClock(); }
    /// <summary>
    /// « Déconnecter ce compte »: the session ends on this PC and on Jellyfin, and with the switch beside it, this
    /// account's data leaves this PC (other accounts' and the settings stay).
    /// </summary>
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        // The saved session: also when the library could not open (no client then), or in the demo.
        var connection = _profile.LoadConnection(); var erase = EraseAccountData.IsChecked == true;
        // Nothing else on this screen until it is done: a sign-in meanwhile would be undone by what follows.
        _signingOut = true; LoginOverlay.IsEnabled = false; LogoutButton.Content = "Déconnexion…";
        try
        {
            // Off this PC first, so that closing Mira while Jellyfin is asked leaves no session saved.
            _profile.ClearConnection();
            await DisconnectServicesAsync();
            // After the last progress reports, which still need the session; a few seconds at most, answered or not.
            var closed = false;
            if (connection is not null) using (var client = new JellyfinClient(connection)) closed = await client.LogoutAsync();
            PasswordBox.Clear(); _catalog.Items = []; _catalog.Resume = []; _catalog.NextUp = []; _hero = null; _catalog.TotalCount = 0;
            var erased = erase && connection is not null ? await AccountData.EraseAsync(_profile.DirectoryPath, connection) : (bool?)null;
            // Its titles kept on this PC too, wherever they were put.
            if (erased is not null && connection is not null) { var roots = OfflineRoots; erased &= await Task.Run(() => OfflineCopies.Erase(roots, connection)); }
            RenderLibrary(); _ = RefreshTorLinkLibrariesAsync();
            LoginArt.Source = null;
            LogoutButton.Visibility = BackToLibrary.Visibility = Visibility.Collapsed;
            LoginError.Text = connection is null ? "Session déconnectée sur ce PC." : AccountData.SignedOut("ce PC", closed, erased); FocusLogin();
        }
        finally { _signingOut = false; LoginOverlay.IsEnabled = true; LogoutButton.Content = "Déconnecter ce compte"; EraseAccountData.IsChecked = false; FocusLogin(); }
    }
    private bool _signingOut;
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    /// <summary>The maximize button shows "restore" once the window fills the screen.</summary>
    private void UpdateMaximizeButtons()
    {
        var maximized = WindowState == WindowState.Maximized;
        foreach (var (button, icon) in new[] { (MaximizeButton, MaximizeIcon), (PlayerMaximizeButton, PlayerMaximizeIcon) })
        {
            icon.Kind = maximized ? "restore" : "maximize";
            var label = maximized ? "Restaurer la fenêtre" : "Agrandir la fenêtre";
            button.ToolTip = label; System.Windows.Automation.AutomationProperties.SetName(button, label);
        }
    }
    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        // Escape first clears the search, then (a second press) goes back as usual.
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; }
    }
    private void SearchClear_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
