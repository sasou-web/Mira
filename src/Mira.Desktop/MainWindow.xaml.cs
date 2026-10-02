using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private bool _catalogLoading, _keyboardNavigation;
    private CancellationTokenSource _connectionLifetime = new();
    private CancellationTokenSource? _browseCancellation;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _externalRefresh = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _fallbackRefresh = new() { Interval = TimeSpan.FromSeconds(45) };
    private List<MediaItem> _items = [], _resume = [], _nextUp = [], _episodes = [];
    private MediaItem? _hero, _detail;
    private string _view = "home";
    /// <summary>Only the favourites of the current view (films, series, library, search): a filter like the others.</summary>
    private bool _favorites;
    private string? _parentId;
    private int _totalCount;
    private bool _demo, _initializing = true;
    private bool _startupComplete;
    private int _viewVersion, _detailVersion;
    private string? _testMedia;
    private readonly string[] _args;
    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    public MainWindow(string[] args)
    {
        _args = args;
        FitToScreen();
        var dataArg = Array.IndexOf(args, "--data");
        _profile = new LocalProfile(dataArg >= 0 && dataArg + 1 < args.Length ? args[dataArg + 1] : null);
        _settings = _profile.LoadSettings();
        Motion.Reduced = _settings.ReduceMotion;
        InitializeComponent();
        InitializeFilters();
        InitializePreview();
        InitializePlayerControls();
        InitializePlayerProbe();
        InitializeWindowsIntegration();
        InitializeTorLink();
        InitializeUpdates();
        Activated += (_, _) => UpdateHeroClock(); Deactivated += (_, _) => { UpdateHeroClock(); ClosePreview(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Tab) _keyboardNavigation = true; };
        PreviewMouseDown += (_, e) =>
        {
            _keyboardNavigation = false;
            // The mouse's "back" side button steps back like Escape.
            if (e.ChangedButton == MouseButton.XButton1 && GoBack()) e.Handled = true;
        };
        _noticeTimer.Tick += NoticeTick;
        StateChanged += (_, _) => UpdateMaximizeButtons();
        SettingsVersion.Text = $"Mira {JellyfinClient.AppVersion} · Jellyfin + mpv";
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefreshAsync(); };
        _externalRefresh.Tick += async (_, _) =>
        {
            _externalRefresh.Stop();
            // A library change may be a title TorLink just brought in.
            _ = CheckTorLinkAvailabilityAsync();
            if (!_playing && !_demo) { _metadata?.Clear(); _heroSeries.Clear(); await RefreshAsync(quiet: true); }
        };
        _fallbackRefresh.Tick += async (_, _) => { if (!_demo && !_playing && _client is not null && IsActive) await RefreshAsync(quiet: true); };
        _playerTimer.Tick += PlayerTick;
        VolumeSlider.Value = _settings.Volume;
        var testArg = Array.IndexOf(args, "--test-media");
        if (testArg >= 0 && testArg + 1 < args.Length) _testMedia = args[testArg + 1];
        Loaded += OnLoaded;
        _initializing = false;
    }
    /// <summary>
    /// The default size (1480 × 930) is taller than a 1080p screen at 125 % (about 1536 × 826 usable) and wider and
    /// taller than one at 150 % (about 1280 × 688): there Mira opens maximized instead of overflowing the screen.
    /// </summary>
    private void FitToScreen()
    {
        // Validation runs set their own sizes, off screen.
        if (_args.Any(x => x is "--visual-check" or "--player-check" or "--public-gallery" or "--windows-check" or "--torlink-check" or "--offscreen")) return;
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        MinWidth = Math.Min(MinWidth, area.Width); MinHeight = Math.Min(MinHeight, area.Height);
        if (Width <= area.Width - 40 && Height <= area.Height - 40) return;
        // Restored later, the window keeps a size the screen can hold.
        Width = Math.Max(MinWidth, Math.Floor(area.Width * .9)); Height = Math.Max(MinHeight, Math.Floor(area.Height * .9));
        WindowState = WindowState.Maximized;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_args.Contains("--demo")) await ShowDemoAsync();
        else if (_profile.LoadConnection() is { } connection) await ActivateConnectionAsync(connection);
        else { LoginOverlay.Visibility = Visibility.Visible; _ = RefreshTorLinkLibrariesAsync(); }
        await RevealStartupAsync();
        if (_args.Contains("--public-gallery")) { await RunPublicGalleryAsync(); return; }
        if (_args.Contains("--visual-check")) { await RunVisualCheckAsync(); return; }
        if (_args.Contains("--player-check")) { await RunPlayerCheckAsync(); return; }
        if (_args.Contains("--windows-check")) { await RunWindowsCheckAsync(); return; }
        if (_args.Contains("--torlink-check")) { await RunTorLinkCheckAsync(); return; }
        _fallbackRefresh.Start();
        ShowStartupScreen();
        if (_testMedia is not null && _args.Contains("--autoplay")) await PlayAsync(DemoLibrary.Items()[0]);
    }
    private Task? _startupRevealTask;
    private Task RevealStartupAsync() => _startupRevealTask ??= RevealStartupCoreAsync();
    private async Task RevealStartupCoreAsync()
    {
        // Warm only the first screen. A slow/offline image must never lock navigation.
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
        _view = "home"; _parentId = null; SearchBox.Text = "";
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
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false; ConnectText.Text = "Connexion…"; LoginError.Text = "";
        JellyfinClient? client = null;
        try
        {
            if (string.IsNullOrWhiteSpace(UsernameBox.Text)) throw new ArgumentException("Indique ton nom d’utilisateur Jellyfin.");
            // "192.168.1.20", "nas:8096" or the address of Jellyfin's web page: find the server before sending the password.
            ConnectText.Text = "Recherche du serveur…";
            var server = await ServerAddress.DiscoverAsync(ServerBox.Text);
            ServerBox.Text = server.Address; ConnectText.Text = "Connexion…";
            client = new JellyfinClient(new(server.Address, "", UsernameBox.Text.Trim(), "", _profile.DeviceId));
            var connection = await client.LoginAsync(UsernameBox.Text.Trim(), PasswordBox.Password);
            _profile.SaveConnection(connection); PasswordBox.Clear();
            await ActivateConnectionAsync(connection);
            OfferGuide("Connecté. Voici l’essentiel.");
        }
        catch (Exception ex) when (IsExpected(ex)) { LoginError.Text = Friendly(ex); }
        finally { client?.Dispose(); ConnectButton.IsEnabled = true; ConnectText.Text = "Se connecter"; }
    }
    private async Task ActivateConnectionAsync(Connection connection)
    {
        await DisconnectServicesAsync();
        _demo = false; DemoBadge.Visibility = Visibility.Collapsed;
        _client = new JellyfinClient(connection); _store = new LibraryStore(_profile.DirectoryPath, connection.Server + "|" + connection.UserId);
        var damaged = _store.DamagedCopy is not null;
        _items = []; _resume = []; _nextUp = []; _recentPlayback = []; _episodes = []; _detail = null; _hero = null; _totalCount = 0; _returnToDetail = null; _seasonThumbs.Clear();
        LibrariesPanel.Children.Clear(); DetailOverlay.Visibility = Visibility.Collapsed;
        // Never leave the previous account's posters on screen while this one loads.
        _catalogLoading = true; RenderLibrary();
        _images = new ImageCache(_client, _profile.DirectoryPath); _sync = new SyncService(_client, _store);
        _metadata = new MetadataCache(_client); _heroRequestedId = null; ++_heroVersion;
        _sync.Changed += SyncChanged;
        _connectionLifetime = new();
        if (!_args.Contains("--visual-check")) _ = ListenSafelyAsync(_client, _connectionLifetime.Token);
        ServerLabel.Text = connection.UserName + " · Jellyfin"; AvatarLetter.Text = connection.UserName[..1].ToUpperInvariant();
        SettingsServerAddress.Text = connection.Server;
        LoginOverlay.Visibility = Visibility.Collapsed; _view = "home"; _parentId = null; SearchBox.Text = "";
        try
        {
            var store = _store;
            var (cached, cachedResume, history) = await Task.Run(() => (store.Load<ItemsResult>("home"), store.Load<List<MediaItem>>("resume"), store.RecentPlayback()));
            if (ReferenceEquals(store, _store)) _recentPlayback = history;
            if (cached is not null && ReferenceEquals(store, _store)) { _items = cached.Items; _totalCount = cached.TotalRecordCount; _resume = cachedResume ?? []; _catalogLoading = false; RenderLibrary(); }
            await RefreshAsync();
            var libraries = await _client.LibrariesAsync(); LibrariesPanel.Children.Clear();
            _filterBusy = true; LibraryFilter.Items.Clear(); LibraryFilter.Items.Add(new ComboBoxItem { Content = "Toutes les bibliothèques", Tag = "" }); LibraryFilter.SelectedIndex = 0;
            foreach (var library in libraries) AddLibraryButton(library.Name, library.Id);
            _filterBusy = false;
            PopulateFilters(await _client.FiltersAsync());
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        SyncChanged();
        // Said once the first refresh is done, which would otherwise hide it at once.
        if (damaged) SetNotice("Le cache local de ce compte était illisible : il a été recréé. Les envois de progression qui n’étaient pas encore partis sont perdus.");
        _ = RefreshTorLinkLibrariesAsync();
    }
    private async Task ListenSafelyAsync(JellyfinClient client, CancellationToken ct)
    {
        try { await client.ListenAsync(() => Dispatcher.BeginInvoke(() => { _externalRefresh.Stop(); _externalRefresh.Start(); }), ct); }
        catch (OperationCanceledException) { }
    }
    private async Task DisconnectServicesAsync()
    {
        if (_playing) { await StopPlaybackAsync(); ReturnToLibrary(); }
        CompositionTarget.Rendering -= HeroFrame; _heroClockAttached = false; _heroCandidates = [];
        _heroSeries.Clear();
        _metadata = null; ClosePreview(); ++_detailVersion; ++_heroVersion; _heroRequestedId = null; _hero = null;
        _connectionLifetime.Cancel(); _browseCancellation?.Cancel();
        if (_sync is not null) { _sync.Changed -= SyncChanged; await _sync.DisposeAsync(); _sync = null; }
        _client?.Dispose(); _client = null; _images = null; _store = null;
        _recentPlayback = [];
    }
    private void AddLibraryButton(string name, string? id)
    {
        var button = new Button { Content = name, Style = (Style)FindResource("NavButton"), ToolTip = name };
        button.Click += async (_, _) => { _view = "library"; ResetFilters(); _filterBusy = true; _parentId = id; LibraryFilter.SelectedItem = LibraryFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == (id ?? "")); _filterBusy = false; await NavigateLibraryAsync(); };
        LibrariesPanel.Children.Add(button);
        if (id is not null) LibraryFilter.Items.Add(new ComboBoxItem { Content = name, Tag = id });
    }
    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        _view = (string)((Button)sender).Tag; ResetFilters();
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
        _scanning = true; var before = _totalCount; var view = (_view, _favorites, _parentId, SearchBox.Text); var shown = Task.Delay(700);
        SpinRefresh(true); RefreshLabel.Text = "Analyse…";
        try
        {
            var scanned = false;
            if (!_demo && _client is { } client)
            {
                var progress = new Progress<double?>(p => RefreshLabel.Text = p is { } share ? $"Analyse… {share * 100:0} %" : "Analyse…");
                scanned = await client.ScanAndWaitAsync(progress, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), _connectionLifetime.Token);
                RefreshLabel.Text = "Chargement…";
            }
            await RefreshAsync(quiet: true); await shown;
            // Counted only on the list it started from: another view has another total.
            var added = view == (_view, _favorites, _parentId, SearchBox.Text) ? _totalCount - before : 0;
            SetNotice(_demo ? "Démo à jour."
                : !scanned ? "Liste rechargée. Seul un administrateur de Jellyfin peut lui faire analyser les dossiers : un nouveau fichier apparaît après sa prochaine analyse."
                : added > 0 ? $"Bibliothèque à jour : {added} nouveau{(added > 1 ? "x" : "")} titre{(added > 1 ? "s" : "")}."
                : "Bibliothèque à jour : Jellyfin a vérifié tous tes dossiers.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        finally { _scanning = false; SpinRefresh(false); RefreshLabel.Text = "Actualiser"; }
    }
    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await RefreshAsync(more: true);
    private async Task RefreshAsync(bool more = false, bool quiet = false)
    {
        _searchTimer.Stop();
        if (_demo) { RenderDemo(); return; }
        if (_client is null || _store is null) return;
        _browseCancellation?.Cancel(); _browseCancellation = new CancellationTokenSource();
        var ct = _browseCancellation.Token; var version = ++_viewVersion;
        var search = SearchBox.Text.Trim(); var home = _view == "home" && search.Length == 0;
        var filters = home ? new CatalogQuery() : CurrentQuery();
        var key = CatalogKey(search, filters);
        var client = _client; var store = _store;
        LoadMore.IsEnabled = false; var startOffset = LibraryScroll.VerticalOffset;
        if (!quiet && !more) { SyncLabel.Text = "↻  Actualisation…"; SpinRefresh(true); }
        try
        {
            // A background refresh keeps every page already loaded instead of shrinking the grid back to one page.
            var limit = quiet && !more ? Math.Max(JellyfinClient.PageSize, _items.Count) : JellyfinClient.PageSize;
            var browse = client.BrowseAsync(_view is "Movie" or "Series" ? _view : "Movie,Series", _parentId, search, more ? _items.Count : 0, !home && _favorites, ct, filters, limit);
            Task<ItemsResult>? resume = home ? client.ResumeAsync(ct) : null;
            Task<ItemsResult>? next = home ? client.NextUpAsync(ct) : null;
            var result = await browse;
            if (home) { await Task.WhenAll(resume!, next!); await LoadSeasonThumbsAsync(client, resume!.Result.Items.Concat(next!.Result.Items), ct); }
            ct.ThrowIfCancellationRequested(); if (version != _viewVersion) return;
            var resumeItems = home ? resume!.Result.Items : null;
            List<MediaItem>? recent = null; var seasonThumbs = new Dictionary<string, string?>(_seasonThumbs);
            // SQLite work stays off the UI thread; the page only redraws when it is done.
            await Task.Run(() =>
            {
                store.ApplyLocalProgress(result.Items);
                if (resumeItems is not null) { recent = store.RecentPlayback(); resumeItems = store.MergeResume(resumeItems, recent); ApplySeasonThumbs(resumeItems, seasonThumbs); store.ApplyLocalProgress(resumeItems); store.ApplyLocalProgress(next!.Result.Items); store.Save("home", result); store.Save("resume", resumeItems); }
                else if (!more && search.Length == 0) store.Save(key, result);
            }, CancellationToken.None);
            ct.ThrowIfCancellationRequested(); if (version != _viewVersion) return;
            if (more) _items.AddRange(result.Items.Where(x => _items.All(old => old.Id != x.Id))); else _items = result.Items;
            _totalCount = result.TotalRecordCount;
            if (home) { _resume = resumeItems!; _nextUp = next!.Result.Items; _recentPlayback = ContinueWatching.History(_recentPlayback.Concat(recent!)); }
            _catalogLoading = false; RenderLibrary();
            // Back to the top for a new list, unless the reader has already started scrolling it.
            if (!quiet && !more && Math.Abs(LibraryScroll.VerticalOffset - startOffset) < 1) SmoothScroll.Jump(LibraryScroll, 0);
            if (!quiet) HideNotice(); SyncChanged();
        }
        catch (OperationCanceledException) { }
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
                var cached = await Task.Run(() => store.Load<ItemsResult>(home ? "home" : key));
                if (cached is not null && !more) { _items = cached.Items; _totalCount = cached.TotalRecordCount; store.ApplyLocalProgress(_items); RenderLibrary(); }
                SetNotice(Friendly(ex) + " Les informations déjà chargées restent disponibles."); SyncLabel.Text = "○  Hors connexion · cache local";
                _catalogLoading = false; RenderLibrary();
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
        var all = _demoItems ??= DemoLibrary.Items(); _resume = all.Where(x => x.Progress > 0).ToList(); _nextUp = [];
        _items = all.Where(x => (_view is not ("Movie" or "Series") || x.Type == _view) && (!_favorites || x.UserData.IsFavorite) &&
            (SearchBox.Text.Length == 0 || x.Name.Contains(SearchBox.Text, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (_view != "home")
        {
            var query = CurrentQuery();
            _items = _items.Where(x => (query.Genre is null || x.Genres.Contains(query.Genre)) && (query.Year is null || x.ProductionYear == query.Year) && (query.Played is null || x.UserData.Played == query.Played)).ToList();
            _items = query.Sort switch { "title" => _items.OrderBy(x => x.Name).ToList(), "year" => _items.OrderByDescending(x => x.ProductionYear).ToList(), "rating" => _items.OrderByDescending(x => x.CommunityRating).ToList(),
                "played" => _items.OrderByDescending(x => x.UserData.LastPlayedDate ?? DateTimeOffset.MinValue).ThenByDescending(x => x.Progress).ToList(), _ => _items };
        }
        _totalCount = _items.Count; RenderLibrary();
    }
    private List<MediaItem>? _demoItems;
    private void SyncChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_sync is null || _demo) return;
        var pending = _sync.PendingCount;
        var error = _sync.Error;
        SyncLabel.Text = pending > 0
            ? $"◌  {pending} envoi{(pending > 1 ? "s" : "")} en attente{(error is not null ? " · " + error : "")}"
            // A local storage failure matters even with nothing waiting: the next reports will not be kept.
            : error is not null ? "◌  " + error
            : _sync.LastSynced is { } t ? $"●  Synchronisé à {t:HH:mm:ss}" : "●  Connecté à Jellyfin";
        SyncLabel.Foreground = pending > 0 || error is not null ? Brush("#D5B787") : Brush("#91BAA7");
    });
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Action? _noticeAction;
    private void SetNotice(string message) => SetNotice(message, null, null);
    /// <summary>Messages slide in, then leave on their own after a few seconds (unless the pointer is on them).
    /// <paramref name="action"/> adds a button, such as "Redémarrer" for a downloaded update.</summary>
    private void SetNotice(string message, string? action, Action? onAction)
    {
        _noticeAction = onAction; NoticeAction.Content = action;
        NoticeAction.Visibility = onAction is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Text = message;
        if (Notice.Visibility != Visibility.Visible || !Notice.IsHitTestVisible) Motion.Reveal(Notice, 220, 10);
        _noticeTimer.Stop(); _noticeTimer.Start();
    }
    private void HideNotice() { _noticeTimer.Stop(); if (Notice.Visibility == Visibility.Visible) _ = Motion.HideAsync(Notice, 180); }
    private void NoticeAction_Click(object sender, RoutedEventArgs e) { var action = _noticeAction; HideNotice(); action?.Invoke(); }
    private void NoticeTick(object? sender, EventArgs e) { if (Notice.IsMouseOver) return; HideNotice(); }
    private static bool IsExpected(Exception e) => e is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or System.Text.Json.JsonException;
    private static string Friendly(Exception ex) => ex switch { UnauthorizedAccessException => ex.Message, ArgumentException => ex.Message, ServerDiscoveryException => ex.Message, OperationCanceledException => "Jellyfin met trop de temps à répondre.", HttpRequestException h when h.StatusCode is not null => h.Message, HttpRequestException => "Le serveur Jellyfin est momentanément inaccessible.", _ => "L’opération n’a pas abouti. Vérifie la connexion et réessaie." };
    private async void Demo_Click(object sender, RoutedEventArgs e) => await ShowDemoAsync();
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        ClosePreview(); ServerBox.Text = _client?.Connection.Server ?? "http://127.0.0.1:8096"; UsernameBox.Text = _client?.Connection.UserName ?? "";
        LoginArt.Source = _demo ? null : HeroImage.Source;
        LogoutButton.Visibility = _profile.LoadConnection() is null ? Visibility.Collapsed : Visibility.Visible;
        BackToLibrary.Visibility = _client is not null || _demo ? Visibility.Visible : Visibility.Collapsed;
        LoginError.Text = ""; ShowSignInForm(); Motion.Reveal(LoginOverlay); UpdateHeroClock(); FocusLogin();
    }
    /// <summary>The first empty field gets the caret, so typing can start at once.</summary>
    private void FocusLogin() => Dispatcher.BeginInvoke(() =>
    {
        if (LoginOverlay.Visibility != Visibility.Visible) return;
        if (string.IsNullOrWhiteSpace(UsernameBox.Text)) UsernameBox.Focus(); else PasswordBox.Focus();
    }, DispatcherPriority.Input);
    private async void CloseAccount_Click(object sender, RoutedEventArgs e) { PasswordBox.Clear(); await Motion.HideAsync(LoginOverlay); UpdateHeroClock(); }
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectServicesAsync(); _profile.ClearConnection(); PasswordBox.Clear(); _items = []; _resume = []; _nextUp = []; _hero = null; _totalCount = 0;
        RenderLibrary(); _ = RefreshTorLinkLibrariesAsync();
        LoginArt.Source = null;
        LogoutButton.Visibility = BackToLibrary.Visibility = Visibility.Collapsed; LoginError.Text = "Session déconnectée sur ce PC."; FocusLogin();
    }
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
