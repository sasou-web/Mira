using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Mira.Core;
using Mira.Mac.Playback;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

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
}

/// <summary>
/// Mira's window: the rail of the Windows app on the left, the current page beside it, the player above everything
/// while it plays. Pages are kept in a history that Échap, ⌘[ and the back button walk.
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
    private readonly ContentControl _host = new();
    private readonly Panel _overlay = new();
    private readonly StackPanel _railItems = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _rail;
    private readonly Border _toast;
    private readonly TextBlock _toastText = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
    private readonly Button _toastAction = new() { Classes = { "quiet" }, Padding = new Thickness(12, 6), Margin = new Thickness(12, 0, 0, 0) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    /// <summary>Jellyfin's change notifications come in bursts (a scan, a playback elsewhere): one refresh after the last.</summary>
    private readonly DispatcherTimer _serverChanged = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock _avatar = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#DEDEE4"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Page> _history = [];
    private readonly Dictionary<string, Button> _railButtons = [];
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
        Settings = profile.LoadSettings();
        Group = new WatchGroup(this);
        Group.Changed += () => { Player?.RefreshMenus(); Player?.ShowGroup(); if (Current is DetailPage detail) detail.ShowTogether(); else if (Current is HomePage home) home.GroupChanged(); };
        Group.Member += said => { if (Player is { } player) player.ShowMember(said); else Notice(said + "."); };
        Tailnet = new BuiltInTailscale(this);
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
        // The pages take the whole width, under the rail (Page): a picture runs to the window's edge, as on Windows.
        Grid.SetColumnSpan(_host, 2); _root.Children.Add(_host);
        _rail = BuildRail();
        _root.Children.Add(_rail);
        Grid.SetColumnSpan(_overlay, 2); _root.Children.Add(_overlay);
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
        _toastTimer.Tick += (_, _) => HideNotice();
        _serverChanged.Tick += (_, _) => { _serverChanged.Stop(); _ = RefreshPagesAsync(fromServer: true); };
        _choicesSoon.Tick += async (_, _) => { _choicesSoon.Stop(); await SyncChoicesAsync(); };
        Activated += (_, _) =>
        {
            ActivatedAt = Environment.TickCount64; if (DateTimeOffset.UtcNow - _choicesAsked > TimeSpan.FromMinutes(1)) ChoicesChanged();
            // An account made administrator (or no longer) since the sign-in: Téléchargements comes or goes when Mira is back in front.
            if (Session is { } session && DateTimeOffset.UtcNow - _rightsAsked > TimeSpan.FromMinutes(1)) _ = OfferDownloadsAsync(session);
        };
        // The player takes its keys before the control that has the focus: Space never clicks again the last button pressed,
        // and the arrows never move a slider or a page behind the video.
        // The mini-player takes its keys only when it has the focus: the library around it keeps its own.
        AddHandler(KeyDownEvent, (_, e) => { if (Player is { } player && (!player.IsMini || e.Source is Visual source && player.IsVisualAncestorOf(source))) player.HandleKey(e); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += OnKeyDown;
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed) Back(); };
        Closing += OnClosing;
        Opened += async (_, _) => await StartAsync();
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
        var avatar = new Border { Width = 29, Height = 29, CornerRadius = new CornerRadius(10), Background = Ui.Brush("#3D3D43"), Child = _avatar, Margin = new Thickness(0, 12, 0, 0) };
        ToolTip.SetTip(avatar, L("Compte Jellyfin", "Jellyfin account"));
        var bottom = Ui.Column(8, settings, avatar); bottom.HorizontalAlignment = HorizontalAlignment.Center;
        var dock = new DockPanel { Margin = new Thickness(0, TitleBarInset, 0, 18) };
        DockPanel.SetDock(brand, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(brand); dock.Children.Add(bottom); dock.Children.Add(_railItems);
        // As on Windows: the rail is the window itself (no panel of its own, no edge), only the chosen page is lit.
        return new Border { Width = RailWidth, Background = Brushes.Transparent, Child = dock };
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

    /// <summary>
    /// A short message at the bottom of the window, with an optional action (« Réessayer », « Installer »…); one marked
    /// <paramref name="sticky"/> stays half a minute, time to read it and choose (« Rejoindre » of an invitation).
    /// </summary>
    public void Notice(string text, string? action = null, Action? click = null, bool sticky = false)
    {
        _toastText.Text = text; _toastClick = click;
        _toastAction.Content = action; _toastAction.IsVisible = action is not null;
        _toast.IsVisible = true; _toast.Opacity = 1;
        _toastTimer.Stop(); _toastTimer.Interval = TimeSpan.FromSeconds(sticky ? 30 : action is null ? 5 : 9); _toastTimer.Start();
    }
    public void HideNotice() { _toastTimer.Stop(); _toast.Opacity = 0; DispatcherTimer.RunOnce(() => { if (_toast.Opacity == 0) _toast.IsVisible = false; }, TimeSpan.FromMilliseconds(200)); }
    public string? NoticeText => _toast.IsVisible ? _toastText.Text : null;
    /// <summary>The button of the notice shown (the checks).</summary>
    public string? NoticeAction => _toast.IsVisible && _toastAction.IsVisible ? _toastAction.Content as string : null;

    private async Task StartAsync()
    {
        if (Args.Contains("--self-check")) { await Checks.SelfCheck.RunAsync(this); return; }
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
            Notice(NoticeText is { } shown ? shown + "\n" + said : said);
        }
        _ = Updates.CheckAsync(this);
    }

    // —— Account ——
    public void ShowLogin(string? error = null)
    {
        _rail.IsVisible = false;
        var login = new LoginPage(this, error);
        // Opened from an invitation (a mira:// link) before any account: its Jellyfin, to sign in to.
        if (_invite is { } invite) login.Server = invite.Server;
        _overlay.Children.Clear(); _overlay.Children.Add(login);
        _history.Clear(); _host.Content = null;
    }
    /// <summary>Opens the library of a signed-in account.</summary>
    public async Task ActivateAsync(Connection connection, HttpMessageHandler? handler = null)
    {
        await CloseSessionAsync();
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
            void Changed() { _serverChanged.Stop(); _serverChanged.Start(); }
            // Open again after a break: what changed meanwhile went unheard, and the group may have let this Mac go.
            var opened = false;
            void Listening(bool open) => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(session, Session)) return;
                if (open && opened) Changed();
                opened |= open; _listening = open; Group.Listening(session, open);
            });
            session.Listen(() => Dispatcher.UIThread.Post(Changed), Listening, (type, data) => Dispatcher.UIThread.Post(() => Group.Message(session, type, data)));
        }
        _avatar.Text = connection.UserName.Length > 0 ? connection.UserName[..1].ToUpperInvariant() : "?";
        _overlay.Children.Clear(); _rail.IsVisible = true;
        _railButtons["downloads"].IsVisible = false;
        _ = OfferDownloadsAsync(Session);
        CollectionRights = TitleCollections.Rights.None;
        _ = LoadCollectionRightsAsync(Session);
        _ = LoadOfflineAsync(Session);
        _history.Clear();
        await NavigateAsync(new HomePage(this));
        if (Session.Store.DamagedCopy is not null) Notice(L("Le cache local de ce compte était illisible : il a été recréé.", "This account’s local cache couldn’t be read: it was created again."));
        // Opened from a mira:// link before this account was: asked now.
        if (_invite is not null) _ = OfferInviteAsync();
        ChoicesChanged();
    }

    /// <summary>
    /// « Téléchargements »: searches and downloads sent to the PC of Jellyfin, where TorLink runs (Mira for Windows), by
    /// its administrators only, as the relay of the extension.
    /// </summary>
    private async Task OfferDownloadsAsync(Session session)
    {
        _rightsAsked = DateTimeOffset.UtcNow;
        bool? admin;
        // Unreachable or unread: the entry stays as it was, asked again the next time Mira comes in front.
        try { admin = await session.Client.IsAdministratorAsync(session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { _rightsAsked = DateTimeOffset.MinValue; return; }
        if (admin is null) _rightsAsked = DateTimeOffset.MinValue;
        else if (ReferenceEquals(session, Session)) _railButtons["downloads"].IsVisible = admin == true;
    }
    private DateTimeOffset _rightsAsked;
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
        if (Session is null) return;
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
                if (Current is SettingsPage) { var fresh = new SettingsPage(this); _host.Content = fresh; await fresh.LoadAsync(); }
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
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex)); return; }
        if (!ReferenceEquals(session, Session)) return;
        var answer = Together.Answer(invite, groups, session.Connection.Server, Group.Active ? Group.Group?.GroupId : null);
        Activate();
        if (answer is { Kind: Together.InviteKind.Join, Group: { } group })
            Notice(answer.Text, L("Rejoindre", "Join"), () => _ = Group.EnterAsync((client, ct) => client.SyncJoinAsync(group.GroupId, ct)), sticky: true);
        else if (answer.Kind == Together.InviteKind.OtherServer)
            Notice(answer.Text, L("Ouvrir dans le navigateur", "Open in the browser"), () => Updates.Open(new Uri(Together.InviteLink(invite.Server, invite.GroupId, invite.Host)), plainHttp: true), sticky: true);
        else if (answer.Kind == Together.InviteKind.Waiting)
            Notice(answer.Text, L("Attendre", "Wait"), () => WaitForInvite(session, invite), sticky: true);
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
        Notice(Together.WaitingLead, L("Annuler", "Cancel"), () => wait.Cancel());
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
        var session = Session; Session = null;
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
        Group.Gone(); _listening = false; _choicesSoon.Stop();
        ForgetOfflineState();
        if (Session is { } session) { Session = null; await session.DisposeAsync(); }
    }

    // —— Navigation ——
    public Page? Current => _host.Content as Page;
    public LoginPage? Login => _overlay.Children.OfType<LoginPage>().FirstOrDefault();
    /// <summary>The page behind a rail entry: "home", "search", "Movie", "Series", "library", "downloads" or "settings".</summary>
    public void Go(string key) => NavigateRoot(key);
    private void NavigateRoot(string key)
    {
        if (Session is null) return;
        if (Watching) return;
        // The rail starts a new history, as on the Windows app.
        _history.Clear();
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
        try { await page.LoadAsync(); _unanswered = false; }
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
        _host.Content = previous; Light(previous.Rail);
        _ = previous.RefreshAsync();
        return true;
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
        _overlay.Children.Clear(); _overlay.Children.Add(player);
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
        if (player.IsMini) { Focus(); _ = RefreshPagesAsync(); }
    }
    internal async Task CloseOverlayPlayerAsync(PlayerPage player)
    {
        if (Player != player) return;
        Player = null;
        // The group plays on without this Mac.
        Group.SetAway(true);
        _overlay.Children.Remove(player);
        if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal;
        Profile.SaveSettings(Settings);
        Focus();
        await RefreshPagesAsync();
    }
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
        if (error is HttpRequestException or OperationCanceledException or TimeoutException) _unanswered = true;
        if (Tailnet.Says(Session?.Connection.Server, error) is { } said) Notice(said.Text, said.Action, said.Act, sticky: said.Action is not null);
        else Notice(Errors.Friendly(error));
    }
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
            Notice(played ? L($"« {item.DisplayTitle} » marqué comme vu.", $"“{item.DisplayTitle}” marked as watched.") : L($"« {item.DisplayTitle} » marqué comme non vu.", $"“{item.DisplayTitle}” marked as unwatched."));
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
            Notice(favorite ? L($"« {item.DisplayTitle} » est dans tes favoris.", $"“{item.DisplayTitle}” is in your favorites.") : L($"« {item.DisplayTitle} » n’est plus dans tes favoris.", $"“{item.DisplayTitle}” is no longer in your favorites."));
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex)); }
    }
    /// <summary>The right-click menu of a card.</summary>
    public ContextMenu Menu(MediaItem item)
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
        if (TitleCollections.Holds(item) && CollectionRights.Manage)
        {
            Add(L("Ajouter à une collection…", "Add to collection…"), () => _ = CollectAsync(item));
            if (Current is BrowsePage { Saga: { } shown }) Add(L("Retirer de la collection", "Remove from collection"), () => _ = PutInCollectionAsync(item, shown, false));
        }
        if (OfflineMenuItem(item) is { } offline) menu.Items.Add(offline);
        if (item.Type is "Movie" or "Episode" && Offers("downloads")) Add(L("Chercher une meilleure version", "Find a better version"), () => FindBetter(item));
        return menu;
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
        var window = new CollectionWindow(session, item);
        await window.ShowDialog(this);
        if (window.Said is { } said) Notice(said);
        if (window.Changed) await RefreshPagesAsync();
    }
    /// <summary>« Renommer… » (an administrator); its page, shown, opens again under its new name.</summary>
    public async Task RenameCollectionAsync(MediaItem collection)
    {
        if (Session is not { } session) return;
        var window = new CollectionWindow(session, null, collection);
        await window.ShowDialog(this);
        if (window.Said is { } said) Notice(said);
        if (window.NewName is { } name && Current is BrowsePage { Saga: { } shown } && shown.Id == collection.Id) { Back(); Open(new MediaItem { Id = collection.Id, Name = name, Type = "BoxSet" }); }
        else if (window.Changed) await RefreshPagesAsync();
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
            Notice(TitleCollections.Deleted(collection.Name));
            if (Current is BrowsePage { Saga: { } shown } && shown.Id == collection.Id) { Back(); return; }
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex)); }
    }
    /// <summary>A title taken out of a collection from its page (« Annuler » puts it back), or put back.</summary>
    private async Task PutInCollectionAsync(MediaItem item, MediaItem collection, bool inside)
    {
        if (Session is not { } session) return;
        try
        {
            await session.Client.SetInCollectionAsync(collection.Id, [item.Id], inside);
            if (inside) Notice(TitleCollections.Added(collection.Name));
            else Notice(TitleCollections.Removed(collection.Name), L("Annuler", "Undo"), () => _ = PutInCollectionAsync(item, collection, true));
            await RefreshPagesAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex)); }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Watching) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape || command && e.Key == Key.OemOpenBrackets) { if (Back()) e.Handled = true; return; }
        if (command && e.Key is Key.F or Key.K) { NavigateRoot("search"); e.Handled = true; return; }
        if (command && e.Key == Key.OemComma) { NavigateRoot("settings"); e.Handled = true; return; }
        // I brings the mini-player back over the window, as on Windows; never while typing.
        if (e.Key == Key.I && e.KeyModifiers == KeyModifiers.None && Player is { IsMini: true } mini && e.Source is not TextBox) { mini.SetMini(false); e.Handled = true; }
    }
    private bool _closing;
    /// <summary>From the first close request: the window is hidden and the session is being closed.</summary>
    public bool IsClosing => _closing;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true; _closing = true;
        // Each step on its own and the close guaranteed: a full disk or a failing service must not skip the steps after
        // it, nor leave Mira running without a window. Failures are logged (errors.log).
        try
        {
            if (WindowState is WindowState.Normal) (Settings.WindowWidth, Settings.WindowHeight) = (Math.Round(Width), Math.Round(Height));
            Settings.WindowMaximized = WindowState == WindowState.Maximized;
            Hide();
            // The last progress report still leaves while the window is already gone.
            await AttemptAsync(StopPlaybackAsync);
            Attempt(() => Profile.SaveSettings(Settings));
            await AttemptAsync(CloseSessionAsync);
            // After the last reports to Jellyfin, which may go through it.
            await AttemptAsync(Tailnet.DisposeAsync);
        }
        finally { Close(); }
        void Attempt(Action step) { try { step(); } catch (Exception ex) { ErrorLog.Append(Profile.DirectoryPath, ex); } }
        async Task AttemptAsync(Func<Task> step) { try { await step(); } catch (Exception ex) { ErrorLog.Append(Profile.DirectoryPath, ex); } }
    }
}
