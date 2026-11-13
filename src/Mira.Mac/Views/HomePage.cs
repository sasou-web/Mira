using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Accueil: a banner of titles to (re)start, then Continuer à regarder, the latest additions, the favourites and the
/// latest titles of a library, in the order chosen in the settings (« Accueil à ta main », <see cref="HomeLayout"/>).
/// A « Regarder ensemble » group open on Jellyfin is a line under the banner, to join it, while this Mac is in none.
/// </summary>
public sealed class HomePage : Page
{
    public override string Rail => "home";
    private readonly StackPanel _rows = new() { Spacing = 38, Margin = new Thickness(0, 0, 0, 56) };
    /// <summary>Nothing at all to show: where videos go, and a way to look again once they are there.</summary>
    private readonly EmptyState _empty = new() { Margin = new Thickness(56, 120, 56, 60) };
    private readonly Grid _hero = new() { ClipToBounds = true };
    private readonly Image _heroImage = new() { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _heroText = new() { Spacing = 18, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(56 + MainWindow.RailWidth, 0, 40, 66), MaxWidth = 720 };
    private readonly StackPanel _heroSide = new() { Spacing = 14, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 56, 70), Width = 420 };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(MainWindow.RailWidth, 0, 0, 20) };
    private readonly DispatcherTimer _rotate = new();
    /// <summary>As on Windows: 6.5 s a title, counted only while the banner turns, the bar of the title shown filling under it.</summary>
    private readonly CarouselTimeline _heroTime = new(6.5);
    private readonly System.Diagnostics.Stopwatch _heroRun = new();
    private Border? _heroFill;
    private string? _heroShownId;
    private readonly ScrollViewer _scroll;
    private List<MediaItem> _heroItems = [];
    // Jellyfin's groups, read at each refresh of Home and when this Mac leaves a group, and the line of the one shown.
    private readonly Border _groupLine;
    private readonly TextBlock _groupName = new() { FontSize = 14.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _groupPeople = new() { FontSize = 14.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _groupJoin;
    private List<Together.Group> _groups = [];
    private string? _groupFor, _groupShown;
    private int _heroIndex, _heroVersion;
    /// <summary>Titles shown, for the self-check.</summary>
    public IReadOnlyList<MediaItem> Continue { get; private set; } = [];
    public IReadOnlyList<MediaItem> Latest { get; private set; } = [];

    public HomePage(MainWindow shell) : base(shell, underRail: true)
    {
        _heroImage.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(420) }];
        _hero.Children.Add(new Border { Background = Ui.Brush("#0F0F0F") });
        _hero.Children.Add(_heroImage);
        _hero.Children.Add(new Border { Background = Gradient(0, 0.5, 1, 0.5, ("#F0000000", 0), ("#9A000000", 0.42), ("#00000000", 0.75)) });
        _hero.Children.Add(new Border { Background = Gradient(0.5, 0, 0.5, 1, ("#00000000", 0.45), ("#C0000000", 0.82), ("#FF000000", 1)) });
        _hero.Children.Add(_heroText); _hero.Children.Add(_heroSide); _hero.Children.Add(_dots);
        // Held still while it is read or used, as on Windows: the pointer on its words or its bars, the keyboard in it, the page scrolled past it.
        foreach (var part in new Control[] { _heroText, _heroSide, _dots }) { part.PointerEntered += (_, _) => UpdateClock(); part.PointerExited += (_, _) => UpdateClock(); }
        _hero.GotFocus += (_, _) => UpdateClock(); _hero.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => UpdateClock());
        // Its time up: the next title, from the start of its own time.
        _rotate.Tick += (_, _) => { if (Turns) { _heroShownId = null; ShowHero(_heroIndex + 1); } else UpdateClock(); };
        _groupJoin = Ui.Action(L("Rejoindre", "Join"), null, "primary", () => _ = JoinGroupAsync());
        _groupJoin.Margin = new Thickness(18, 0, 0, 0);
        DockPanel.SetDock(_groupJoin, Dock.Right);
        var groupText = Ui.Row(14, new Icon("users", 20) { Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center }, Ui.Row(0, _groupName, _groupPeople));
        groupText.VerticalAlignment = VerticalAlignment.Center;
        _groupLine = new Border
        {
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760, Margin = new Thickness(56, 0, 56, 30), Padding = new Thickness(18, 10, 10, 10),
            CornerRadius = new CornerRadius(12), Background = Ui.Brush("#111111"), BorderBrush = Ui.Brush("#272727"), BorderThickness = new Thickness(1),
            Child = new DockPanel { Children = { _groupJoin, groupText } }
        };
        _groupFor = Shell.Group.Active ? Shell.Group.Group?.GroupId : null;
        // The banner runs under the rail; the rest of the page starts beside it.
        foreach (var part in new Control[] { _groupLine, _empty, _rows }) part.Margin += new Thickness(MainWindow.RailWidth, 0, 0, 0);
        var page = new StackPanel { Spacing = 8 };
        page.Children.Add(_hero); page.Children.Add(_groupLine); page.Children.Add(_empty); page.Children.Add(_rows);
        _scroll = new ScrollViewer { Content = page };
        Content = _scroll;
        SizeChanged += (_, e) => _hero.Height = Math.Clamp(e.NewSize.Height * 0.7, 440, 760);
        // The banner turns only while Accueil shows (kept hidden behind another page, it waits).
        Showing += _ => UpdateClock();
        _scroll.ScrollChanged += (_, _) => { if (_heroRun.IsRunning != Runs) UpdateClock(); };
    }
    /// <summary>
    /// The banner turns by itself: more than one title, « Animer l’accueil » on (Réglages → Apparence, as on Windows) and
    /// the Mac's « Réduire les animations » off.
    /// </summary>
    private bool Turns => _heroItems.Count > 1 && Shell.Settings.HeroAutoPlay && !MacMotion.Reduced;
    /// <summary>For the self-check: whether the banner is turning now.</summary>
    public bool Turning => _rotate.IsEnabled;
    /// <summary>For the self-check: the time the title shown has had so far, in seconds.</summary>
    public double HeroElapsed => _heroTime.Elapsed + _heroRun.Elapsed.TotalSeconds;
    /// <summary>For the self-check: the banner's bars, and its titles.</summary>
    public (int Bars, int Titles) HeroBars => (_dots.Children.Count, _heroItems.Count);
    /// <summary>For the self-check: the page scrolled to <paramref name="y"/>.</summary>
    public void ScrollForCheck(double y) => _scroll.Offset = new Vector(0, Math.Clamp(y, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
    /// <summary>Turning now: Accueil shown, nobody reading or using the banner, the page not scrolled past it.</summary>
    private bool Runs => Turns && IsShowing && _hero.IsVisible && !Held;
    private bool Held => _heroText.IsPointerOver || _heroSide.IsPointerOver || _dots.IsPointerOver || _scroll.Offset.Y > _hero.Bounds.Height * .65
        || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control focused && _hero.IsVisualAncestorOf(focused) && focused.Classes.Contains(":focus-visible");
    /// <summary>
    /// The banner's clock started or held, as Windows' <c>UpdateHeroClock</c>: the time already counted kept (a pointer that
    /// passes does not give the title its 6.5 s again), the bar run by a transition to its end, or held where it is.
    /// <paramref name="bars"/>: the bars were just made again, their fill to set even when the clock does not change.
    /// </summary>
    private void UpdateClock(bool bars = false)
    {
        var run = Runs;
        if (!bars && run == _heroRun.IsRunning && run == _rotate.IsEnabled) return;
        if (_heroRun.IsRunning) { _heroTime.Advance(_heroRun.Elapsed.TotalSeconds, paused: false); _heroRun.Reset(); }
        _rotate.Stop();
        var left = TimeSpan.FromSeconds(Math.Max(.05, _heroTime.Duration - _heroTime.Elapsed));
        if (run) { _rotate.Interval = left; _rotate.Start(); _heroRun.Start(); }
        RunFill(run ? left : null);
    }
    private static ITransform Fill(double share) => TransformOperations.Parse(FormattableString.Invariant($"scaleX({Math.Clamp(share, 0, 1):0.####})"));
    /// <summary>The bar of the title shown, where its time is, then run to its end over <paramref name="left"/>.</summary>
    private void RunFill(TimeSpan? left)
    {
        if (_heroFill is not { } fill) return;
        fill.Transitions = null;
        fill.RenderTransform = Fill(Turns ? _heroTime.Progress : 1);
        if (left is not { } time) return;
        DispatcherTimer.RunOnce(() =>
        {
            if (fill != _heroFill || !_heroRun.IsRunning) return;
            fill.Transitions = [new TransformOperationsTransition { Property = RenderTransformProperty, Duration = time - _heroRun.Elapsed }];
            fill.RenderTransform = Fill(1);
        }, TimeSpan.FromMilliseconds(16));
    }
    private static LinearGradientBrush Gradient(double x1, double y1, double x2, double y2, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(x1, y1, RelativeUnit.Relative), EndPoint = new RelativePoint(x2, y2, RelativeUnit.Relative) };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        return brush;
    }

    public override Task LoadAsync() => RefreshAsync();
    public override async Task RefreshAsync()
    {
        if (Shell.Session is not { } session) return;
        _ = LoadGroupsAsync(session);
        var store = session.Store;
        // The last known home first, so the page never opens empty; then Jellyfin's answer.
        if (_heroItems.Count == 0 && await Task.Run(() => Errors.TryCache(() => (store.Load<ItemsResult>("home"), store.Load<List<MediaItem>>("resume")))) is ({ } cachedHome, var cachedResume))
            Render(session, cachedResume ?? [], cachedHome.Items, [], [], []);
        var client = session.Client;
        var resume = client.ResumeAsync(); var next = client.NextUpAsync();
        // What this account played lately on any device (the iPhone, a TV…): where each film or series stands in « Continuer à regarder ».
        var played = TryAsync(() => client.HistoryAsync());
        // Every title « Ajoutés récemment » shows, not just its first 30: the grid stays as long under the reader. Home's rows
        // list every film, even in a collection, on a server that groups them (Films and Séries follow it).
        var latest = client.BrowseAsync(limit: Math.Max(LatestPage, _latestShown), everyTitle: true);
        var favorites = client.BrowseAsync(favorites: true, limit: 30, everyTitle: true);
        var libraries = TryAsync(() => client.LibrariesAsync());
        // The collections only while their row is shown.
        var collections = HomeLayout.Rows(Shell.Settings.HomeRows, []).Any(x => x.Key == HomeLayout.Collections && x.Shown)
            ? TryAsync(() => client.CollectionsAsync(30)) : Task.FromResult<List<MediaItem>?>(null);
        await Task.WhenAll(resume, next, latest, favorites, libraries, collections, played);
        // « Nouveau dans Animes »: only the libraries whose row is shown are asked.
        var views = libraries.Result ?? [];
        var wanted = HomeLayout.Rows(Shell.Settings.HomeRows, views).Where(x => x.Shown && x.LibraryId is not null).Select(x => x.LibraryId!).ToList();
        var lists = await Task.WhenAll(wanted.Select(id => TryAsync(() => client.BrowseAsync(parentId: id, limit: 30, everyTitle: true))));
        var shelves = wanted.Zip(lists).ToDictionary(x => x.First, x => x.Second?.Items ?? []);
        if (collections.Result is { } found) shelves[HomeLayout.Collections] = found;
        var seasons = Artwork.Seasons(resume.Result.Items.Concat(next.Result.Items)).ToList();
        var thumbs = seasons.Count > 0 ? await TryAsync(() => client.SeasonThumbsAsync(seasons)) ?? [] : [];
        var (merged, history, hidden) = await Task.Run(() =>
        {
            // Without the cache (full disk, locked database), Jellyfin's answer as it is.
            var history = Errors.TryCache(store.RecentPlayback) ?? [];
            var merged = Errors.TryCache(() => store.MergeResume(resume.Result.Items, history)) ?? resume.Result.Items;
            foreach (var item in merged.Concat(next.Result.Items))
                if (item.Type == "Episode" && item.SeasonId is { } season && thumbs.TryGetValue(season, out var tag)) item.SeasonThumbImageTag = tag;
            Errors.TryCache(() =>
            {
                store.ApplyLocalProgress(merged); store.ApplyLocalProgress(next.Result.Items); store.ApplyLocalProgress(latest.Result.Items); store.ApplyLocalProgress(favorites.Result.Items);
                foreach (var shelf in shelves.Values) store.ApplyLocalProgress(shelf);
                store.Save("home", latest.Result); store.Save("resume", merged);
            });
            return (merged, ContinueWatching.History(history.Concat(played.Result ?? [])), Errors.TryCache(store.HiddenFromResume) ?? []);
        });
        var row = ContinueWatching.WithoutHidden(ContinueWatching.Order(merged, next.Result.Items, history), hidden, history);
        _latestTotal = latest.Result.TotalRecordCount;
        Render(session, row, latest.Result.Items, favorites.Result.Items, views, shelves);
    }
    private static async Task<T?> TryAsync<T>(Func<Task<T>> load) where T : class
    {
        try { return await load(); } catch (Exception ex) when (Errors.Expected(ex)) { return null; }
    }

    /// <summary>
    /// The rows drawn, by what they show: a refresh that brings the same (Accueil come back to, Jellyfin's answer after
    /// the last known home) keeps them as they are, cards and pictures, instead of drawing the page again whole.
    /// </summary>
    private Dictionary<string, Control> _drawn = [];
    private void Render(Session session, List<MediaItem> resume, List<MediaItem> latest, List<MediaItem> favorites, List<LibrarySection> libraries, Dictionary<string, List<MediaItem>> shelves)
    {
        Continue = resume; Latest = latest;
        var rows = new List<Control>(); var drawn = new Dictionary<string, Control>();
        Control Poster(MediaItem x) => Sized(Cards.Poster(x, session.Images, Shell.Open, Shell.Menu));
        void Add(string key, Func<Control> make)
        {
            if (!_drawn.TryGetValue(key, out var control) || !drawn.TryAdd(key, control)) drawn[key] = control = make();
            rows.Add(control);
        }
        foreach (var row in HomeLayout.Rows(Shell.Settings.HomeRows, libraries).Where(x => x.Shown))
        {
            var items = row.Key switch
            {
                HomeLayout.Continue => resume,
                HomeLayout.Latest => latest,
                HomeLayout.Favorites => favorites,
                HomeLayout.Collections => shelves.GetValueOrDefault(HomeLayout.Collections) ?? [],
                _ => shelves.GetValueOrDefault(row.LibraryId ?? "") ?? []
            };
            // « Ajoutés récemment » is the grid under the rows, as on Windows: it goes on with « Afficher plus ».
            if (items.Count == 0 || row.Key == HomeLayout.Latest) continue;
            Add(row.Key + "|" + row.Title + "|" + string.Join('\n', items.Select(Cards.Key)),
                () => new CardRow(row.Title, row.Key == HomeLayout.Continue ? items.Select(x => (Control)Cards.Wide(x, session.Images, Shell.Open, 320, x => Shell.Menu(x, resume: true))) : items.Select(Poster)));
        }
        if (latest.Count > 0 && HomeLayout.Rows(Shell.Settings.HomeRows, libraries).Any(x => x.Key == HomeLayout.Latest && x.Shown))
            Add("latest|" + string.Join('\n', latest.Select(Cards.Key)), () => LatestGrid(session, latest));
        _drawn = drawn;
        Ui.Place(_rows, rows);
        if (resume.Count == 0 && latest.Count == 0 && favorites.Count == 0 && shelves.Values.All(x => x.Count == 0))
            _empty.Show("folder", L("Ta bibliothèque est vide", "Your library is empty"), L("Range tes films et séries dans les dossiers de Jellyfin : ils apparaîtront ici.", "Put your movies and shows in Jellyfin’s folders: they’ll show up here."),
                L("Actualiser", "Refresh"), () => _ = Shell.RefreshPagesAsync());
        else _empty.Hide();
        // The banner: what is being watched first, then the latest titles with a backdrop.
        var heroes = resume.Take(2).Concat(latest.Where(x => x.BackdropImageTags.Length > 0)).DistinctBy(x => ContinueWatching.Group(x)).Take(5).ToList();
        if (heroes.Select(x => x.Id).SequenceEqual(_heroItems.Select(x => x.Id)) && heroes.Count > 0) { _heroItems = heroes; ShowHero(_heroIndex, animate: false); return; }
        _heroItems = heroes; ShowHero(0);
    }
    /// <summary>The latest titles in a grid of posters, 30 more at each « Afficher plus de titres » while Jellyfin has more.</summary>
    private Control LatestGrid(Session session, List<MediaItem> latest)
    {
        var grid = new CardGrid { MinItemWidth = 180 };
        foreach (var item in latest) grid.Children.Add(Cards.Poster(item, session.Images, Shell.Open, Shell.Menu, CardGrid.Pixels));
        _latestShown = grid.Children.Count;
        var title = new DockPanel { Margin = new Thickness(0, 0, 6, 22) };
        var count = new TextBlock { FontSize = 12, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
        void Counted() => count.Text = Lang.Count(Math.Max(_latestTotal, grid.Children.Count), "titre", "titres", "title", "titles");
        Counted();
        DockPanel.SetDock(count, Dock.Right); title.Children.Add(count); title.Children.Add(Ui.SectionTitle(L("Ajoutés récemment", "Recently added")));
        Button? more = null;
        more = Ui.Action(L("Afficher plus de titres", "Show more titles"), null, "", () => _ = MoreAsync());
        more.HorizontalAlignment = HorizontalAlignment.Center; more.Margin = new Thickness(0, 26, 0, 0);
        more.IsVisible = grid.Children.Count < _latestTotal;
        async Task MoreAsync()
        {
            if (!ReferenceEquals(session, Shell.Session)) return;
            more!.IsEnabled = false;
            try
            {
                var next = await session.Client.BrowseAsync(start: grid.Children.Count, limit: LatestPage, ct: session.Lifetime, everyTitle: true);
                await Task.Run(() => Errors.TryCache(() => session.Store.ApplyLocalProgress(next.Items)));
                foreach (var item in next.Items) grid.Children.Add(Cards.Poster(item, session.Images, Shell.Open, Shell.Menu, CardGrid.Pixels));
                _latestShown = grid.Children.Count;
                _latestTotal = next.Items.Count == 0 ? grid.Children.Count : next.TotalRecordCount;
                Counted();
                more.IsVisible = grid.Children.Count < _latestTotal;
            }
            catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
            finally { more.IsEnabled = true; }
        }
        var section = Ui.Column(0, title, grid, more);
        section.Margin = new Thickness(56, 0, 56, 0);
        return section;
    }
    private int _latestTotal;
    /// <summary>« Ajoutés récemment » at first, and each « Afficher plus de titres ».</summary>
    private const int LatestPage = 30;
    /// <summary>
    /// The titles « Ajoutés récemment » shows, 30 more at each « Afficher plus de titres »: a refresh (Home come back to,
    /// the player closed, a change on the server) asks for all of them again, as Windows' grid keeps every page loaded.
    /// Asked for the first 30 only, a new title or a mark changed among them drew the grid again with those 30: it
    /// shrank under the reader, who lost their place.
    /// </summary>
    private int _latestShown;
    /// <summary>Down to the grid (the self-check's capture).</summary>
    public void ScrollToEnd() => _scroll.ScrollToEnd();
    public void ScrollToHome() => _scroll.ScrollToHome();

    private async Task LoadGroupsAsync(Session session)
    {
        if (await Shell.Group.OpenGroupsAsync(session) is not { } groups || !ReferenceEquals(session, Shell.Session)) return;
        _groups = groups; ShowGroupLine();
    }
    /// <summary>Joined or left a group (<see cref="WatchGroup.Changed"/>): the line goes, or comes back with the groups as they are now.</summary>
    public void GroupChanged()
    {
        var current = Shell.Group.Active ? Shell.Group.Group?.GroupId : null;
        ShowGroupLine();
        if (current == _groupFor) return;
        _groupFor = current;
        if (current is null && Shell.Session is { } session) _ = LoadGroupsAsync(session);
    }
    /// <summary>« Groupe de Marc · 2 personnes » with « Rejoindre »: the first group open on Jellyfin, none while in one.</summary>
    private void ShowGroupLine()
    {
        var open = Shell.Session is null ? null : Together.HomeGroup(_groups, Shell.Group.Active ? Shell.Group.Group?.GroupId : null);
        _groupLine.IsVisible = open is not null;
        _groupShown = open?.GroupId;
        if (open is null) return;
        _groupName.Text = open.GroupName; _groupPeople.Text = " · " + Together.People(Math.Max(1, open.Participants.Count));
        Avalonia.Automation.AutomationProperties.SetName(_groupJoin, L($"Rejoindre « {open.GroupName} »", $"Join “{open.GroupName}”"));
    }
    /// <summary>The group joined, and its title put on here once it plays one (<see cref="WatchGroup"/>).</summary>
    private async Task JoinGroupAsync()
    {
        if (Shell.Session is not { } session || _groupShown is not { } id) return;
        _groupJoin.IsEnabled = false;
        try
        {
            // It may have ended since Home read it: Jellyfin would answer a join with nothing to say.
            List<Together.Group> groups;
            try { groups = await session.Client.SyncGroupsAsync(session.Lifetime); }
            catch (UnauthorizedAccessException) { Shell.Notice(WatchGroup.NoSyncPlay); return; }
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); return; }
            if (!ReferenceEquals(session, Shell.Session)) return;
            _groups = groups; ShowGroupLine();
            if (groups.FirstOrDefault(g => Together.SameGroup(g.GroupId, id)) is not { } group) { Shell.Notice(L("Ce groupe n’existe plus.", "This group no longer exists.")); return; }
            await Shell.Group.EnterAsync((client, ct) => client.SyncJoinAsync(group.GroupId, ct));
        }
        finally { _groupJoin.IsEnabled = true; }
    }
    private static Control Sized(Control card) { card.Width = 176; return card; }

    private void ShowHero(int index, bool animate = true)
    {
        if (Shell.Session is not { } session) return;
        _heroText.Children.Clear(); _heroSide.Children.Clear(); _dots.Children.Clear();
        // Without a banner, the group's line keeps clear of the window's buttons.
        _hero.IsVisible = _heroItems.Count > 0; _groupLine.Margin = new Thickness(56 + MainWindow.RailWidth, _hero.IsVisible ? 0 : 44 + Shell.TitleBarInset, 56, 30);
        if (_heroItems.Count == 0) { _heroImage.Source = null; return; }
        _heroIndex = (index % _heroItems.Count + _heroItems.Count) % _heroItems.Count;
        var item = _heroItems[_heroIndex];
        // Another title: its 6.5 s from the start. The same one drawn again (a refresh of Accueil): its time goes on.
        if (item.Id != _heroShownId) { _heroShownId = item.Id; _heroTime.Reset(); _heroRun.Reset(); _rotate.Stop(); }
        var version = ++_heroVersion;
        if (animate) _heroImage.Opacity = 0;
        _ = LoadHeroArtAsync(session, item, version);

        // Its own line height: a tighter one cuts the letters that go below the line (« g », « p »).
        var title = new TextBlock { Text = item.DisplayTitle, FontSize = 53, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(0, -6, 0, -6) };
        _heroText.Children.Add(title);
        var tags = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        if (item.Type == "Episode") tags.Children.Add(Ui.Tag(Cards.EpisodeCode(item)));
        if (item.ProductionYear is { } year) tags.Children.Add(Ui.Tag(year.ToString()));
        tags.Children.Add(Ui.Tag(Ui.Kind(item)));
        if (item.Type != "Series" && item.DurationLabel.Length > 0) tags.Children.Add(Ui.Tag(item.Progress > 0 ? Ui.Remaining(item) : item.DurationLabel));
        if (item.CommunityRating is { } rating) tags.Children.Add(Ui.Tag("★ " + Number(rating, "0.0")));
        _heroText.Children.Add(tags);
        var resume = item.Resumable;
        var heart = Ui.IconButton(item.UserData.IsFavorite ? "heart-filled" : "heart", item.UserData.IsFavorite ? L("Retirer des favoris", "Remove from favorites") : L("Ajouter aux favoris", "Add to favorites"), () => _ = Shell.SetFavoriteAsync(item, !item.UserData.IsFavorite), "icon", 24);
        heart.Width = heart.Height = 46;
        _heroText.Children.Add(Ui.Row(12,
            Ui.Action(resume ? L("Reprendre", "Resume") : L("Lecture", "Play"), "play", "primary", () => _ = Shell.PlayAsync(item)),
            Ui.Action(L("Plus d’infos", "More info"), "info", "", () => Shell.Open(item)), heart));
        if (!string.IsNullOrWhiteSpace(item.Overview) && !Spoilers.Hides(item))
            _heroSide.Children.Add(new TextBlock { Text = OverviewText.Plain(item.Overview, flat: true), FontSize = 14.5, LineHeight = 22, Foreground = Ui.Brush("#D8D8DD"), TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.WordEllipsis });
        if (item.Genres.Length > 0)
        {
            var genres = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            foreach (var genre in item.Genres.Take(3)) genres.Children.Add(Ui.Tag(genre));
            _heroSide.Children.Add(genres);
        }
        for (var i = 0; i < _heroItems.Count && _heroItems.Count > 1; i++)
        {
            var target = i;
            // Windows' bars (CarouselProgress): those before the title shown full, its own filling with its time, the next ones empty.
            var fill = new Border { CornerRadius = new CornerRadius(2.5), Background = Ui.Brush("#F5F5F7"), RenderTransformOrigin = new RelativePoint(0, .5, RelativeUnit.Relative), RenderTransform = Fill(i < _heroIndex && Turns || i == _heroIndex ? 1 : 0) };
            if (i == _heroIndex) _heroFill = fill;
            var bar = new Grid { Width = 34, Height = 5, IsHitTestVisible = false, Children = { new Border { CornerRadius = new CornerRadius(2.5), Background = Ui.Brush("#73AFAFB8") }, fill } };
            var dot = new Button { Width = 44, Height = 32, Padding = new Thickness(0), Background = Brushes.Transparent, Content = bar, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            // The bar alone, no wash or tooltip under the pointer, as on Windows.
            dot.Classes.Add("bare");
            Avalonia.Automation.AutomationProperties.SetName(dot, L($"Afficher « {_heroItems[i].DisplayTitle} »{(i == _heroIndex ? ", affiché" : "")}", $"Show “{_heroItems[i].DisplayTitle}”{(i == _heroIndex ? ", shown" : "")}"));
            // The pointer is still on the bars: the title chosen stays until it leaves them.
            dot.Click += (_, _) => ShowHero(target);
            _dots.Children.Add(dot);
        }
        if (_heroItems.Count < 2) _heroFill = null;
        UpdateClock(bars: true);
    }
    private async Task LoadHeroArtAsync(Session session, MediaItem item, int version)
    {
        var art = await session.Images.BackdropAsync(item, 1920);
        if (version != _heroVersion) return;
        _heroImage.Source = art; _heroImage.Opacity = art is null ? 0 : 1;
        var logo = await session.Images.LogoAsync(item);
        if (version != _heroVersion || logo is null || _heroText.Children.FirstOrDefault() is not TextBlock title) return;
        _heroText.Children[0] = new Image { Source = logo, MaxHeight = 130, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
        Avalonia.Automation.AutomationProperties.SetName(_heroText.Children[0], title.Text ?? "");
    }
}
