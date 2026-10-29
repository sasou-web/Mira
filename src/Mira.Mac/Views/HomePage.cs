using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    private readonly StackPanel _heroText = new() { Spacing = 18, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(56, 0, 40, 66), MaxWidth = 720 };
    private readonly StackPanel _heroSide = new() { Spacing = 14, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 56, 70), Width = 420 };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 26) };
    private readonly DispatcherTimer _rotate = new() { Interval = TimeSpan.FromSeconds(9) };
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

    public HomePage(MainWindow shell) : base(shell)
    {
        _heroImage.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(420) }];
        _hero.Children.Add(new Border { Background = Ui.Brush("#0E0E10") });
        _hero.Children.Add(_heroImage);
        _hero.Children.Add(new Border { Background = Gradient(0, 0.5, 1, 0.5, ("#F0070708", 0), ("#9A070708", 0.42), ("#00070708", 0.75)) });
        _hero.Children.Add(new Border { Background = Gradient(0.5, 0, 0.5, 1, ("#00070708", 0.45), ("#C0070708", 0.82), ("#FF070708", 1)) });
        _hero.Children.Add(_heroText); _hero.Children.Add(_heroSide); _hero.Children.Add(_dots);
        _hero.PointerEntered += (_, _) => _rotate.Stop();
        _hero.PointerExited += (_, _) => { if (_heroItems.Count > 1) _rotate.Start(); };
        _rotate.Tick += (_, _) => ShowHero(_heroIndex + 1);
        _groupJoin = Ui.Action(L("Rejoindre", "Join"), null, "primary", () => _ = JoinGroupAsync());
        _groupJoin.Margin = new Thickness(18, 0, 0, 0);
        DockPanel.SetDock(_groupJoin, Dock.Right);
        var groupText = Ui.Row(14, new Icon("users", 20) { Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center }, Ui.Row(0, _groupName, _groupPeople));
        groupText.VerticalAlignment = VerticalAlignment.Center;
        _groupLine = new Border
        {
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760, Margin = new Thickness(56, 0, 56, 30), Padding = new Thickness(18, 10, 10, 10),
            CornerRadius = new CornerRadius(12), Background = Ui.Brush("#111112"), BorderBrush = Ui.Brush("#26262A"), BorderThickness = new Thickness(1),
            Child = new DockPanel { Children = { _groupJoin, groupText } }
        };
        _groupFor = Shell.Group.Active ? Shell.Group.Group?.GroupId : null;
        var page = new StackPanel { Spacing = 8 };
        page.Children.Add(_hero); page.Children.Add(_groupLine); page.Children.Add(_empty); page.Children.Add(_rows);
        _scroll = new ScrollViewer { Content = page };
        Content = _scroll;
        SizeChanged += (_, e) => _hero.Height = Math.Clamp(e.NewSize.Height * 0.7, 440, 760);
        DetachedFromVisualTree += (_, _) => _rotate.Stop();
        AttachedToVisualTree += (_, _) => { if (_heroItems.Count > 1) _rotate.Start(); };
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
        var latest = client.BrowseAsync(limit: 30);
        var favorites = client.BrowseAsync(favorites: true, limit: 30);
        var libraries = TryAsync(() => client.LibrariesAsync());
        // The collections only while their row is shown.
        var collections = HomeLayout.Rows(Shell.Settings.HomeRows, []).Any(x => x.Key == HomeLayout.Collections && x.Shown)
            ? TryAsync(() => client.CollectionsAsync(30)) : Task.FromResult<List<MediaItem>?>(null);
        await Task.WhenAll(resume, next, latest, favorites, libraries, collections);
        // « Nouveau dans Animes »: only the libraries whose row is shown are asked.
        var views = libraries.Result ?? [];
        var wanted = HomeLayout.Rows(Shell.Settings.HomeRows, views).Where(x => x.Shown && x.LibraryId is not null).Select(x => x.LibraryId!).ToList();
        var lists = await Task.WhenAll(wanted.Select(id => TryAsync(() => client.BrowseAsync(parentId: id, limit: 30))));
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
            return (merged, history, Errors.TryCache(store.HiddenFromResume) ?? []);
        });
        var row = ContinueWatching.WithoutHidden(ContinueWatching.Order(merged, next.Result.Items, history), hidden, history);
        Render(session, row, latest.Result.Items, favorites.Result.Items, views, shelves);
    }
    private static async Task<T?> TryAsync<T>(Func<Task<T>> load) where T : class
    {
        try { return await load(); } catch (Exception ex) when (Errors.Expected(ex)) { return null; }
    }

    private void Render(Session session, List<MediaItem> resume, List<MediaItem> latest, List<MediaItem> favorites, List<LibrarySection> libraries, Dictionary<string, List<MediaItem>> shelves)
    {
        Continue = resume; Latest = latest;
        _rows.Children.Clear();
        Control Poster(MediaItem x) => Sized(Cards.Poster(x, session.Images, Shell.Open, Shell.Menu));
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
            if (items.Count == 0) continue;
            _rows.Children.Add(new CardRow(row.Title, row.Key == HomeLayout.Continue ? items.Select(x => (Control)Cards.Wide(x, session.Images, Shell.Open, 320, Shell.Menu)) : items.Select(Poster)));
        }
        if (resume.Count == 0 && latest.Count == 0 && favorites.Count == 0 && shelves.Values.All(x => x.Count == 0))
            _empty.Show("folder", L("Ta bibliothèque est vide", "Your library is empty"), L("Range tes films et séries dans les dossiers de Jellyfin : ils apparaîtront ici.", "Put your movies and shows in Jellyfin’s folders: they’ll show up here."),
                L("Actualiser", "Refresh"), () => _ = Shell.RefreshPagesAsync());
        else _empty.Hide();
        // The banner: what is being watched first, then the latest titles with a backdrop.
        var heroes = resume.Take(2).Concat(latest.Where(x => x.BackdropImageTags.Length > 0)).DistinctBy(x => ContinueWatching.Group(x)).Take(5).ToList();
        if (heroes.Select(x => x.Id).SequenceEqual(_heroItems.Select(x => x.Id)) && heroes.Count > 0) { _heroItems = heroes; ShowHero(_heroIndex, animate: false); return; }
        _heroItems = heroes; ShowHero(0);
        _rotate.Stop(); if (heroes.Count > 1 && IsAttachedToVisualTree()) _rotate.Start();
    }
    private bool IsAttachedToVisualTree() => this.GetVisualRoot() is not null;

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
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); return; }
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
        _hero.IsVisible = _heroItems.Count > 0; _groupLine.Margin = new Thickness(56, _hero.IsVisible ? 0 : 44 + Shell.TitleBarInset, 56, 30);
        if (_heroItems.Count == 0) { _heroImage.Source = null; return; }
        _heroIndex = (index % _heroItems.Count + _heroItems.Count) % _heroItems.Count;
        var item = _heroItems[_heroIndex];
        var version = ++_heroVersion;
        if (animate) _heroImage.Opacity = 0;
        _ = LoadHeroArtAsync(session, item, version);

        var title = new TextBlock { Text = item.DisplayTitle, FontSize = 58, FontWeight = FontWeight.ExtraBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, LineHeight = 64 };
        _heroText.Children.Add(title);
        var tags = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        if (item.Type == "Episode") tags.Children.Add(Ui.Tag(Cards.EpisodeCode(item)));
        if (item.ProductionYear is { } year) tags.Children.Add(Ui.Tag(year.ToString()));
        tags.Children.Add(Ui.Tag(Ui.Kind(item)));
        if (item.Type != "Series" && item.DurationLabel.Length > 0) tags.Children.Add(Ui.Tag(item.Progress > 0 ? Ui.Remaining(item) : item.DurationLabel));
        if (item.CommunityRating is { } rating) tags.Children.Add(Ui.Tag("★ " + Number(rating, "0.0")));
        _heroText.Children.Add(tags);
        var resume = item.Progress > 0 && !item.UserData.Played;
        var heart = Ui.IconButton(item.UserData.IsFavorite ? "heart-filled" : "heart", item.UserData.IsFavorite ? L("Retirer des favoris", "Remove from favorites") : L("Ajouter aux favoris", "Add to favorites"), () => _ = Shell.SetFavoriteAsync(item, !item.UserData.IsFavorite), "icon", 24);
        heart.Width = heart.Height = 46;
        _heroText.Children.Add(Ui.Row(12,
            Ui.Action(resume ? L("Reprendre", "Resume") : L("Lecture", "Play"), "play", "primary", () => _ = Shell.PlayAsync(item)),
            Ui.Action(L("Plus d’infos", "More info"), "info", "", () => Shell.Open(item)), heart));
        if (!string.IsNullOrWhiteSpace(item.Overview))
            _heroSide.Children.Add(new TextBlock { Text = item.Overview, FontSize = 14.5, LineHeight = 22, Foreground = Ui.Brush("#D8D8DD"), TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.WordEllipsis });
        if (item.Genres.Length > 0)
        {
            var genres = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            foreach (var genre in item.Genres.Take(3)) genres.Children.Add(Ui.Tag(genre));
            _heroSide.Children.Add(genres);
        }
        for (var i = 0; i < _heroItems.Count && _heroItems.Count > 1; i++)
        {
            var target = i;
            var dot = new Button { Width = 46, Height = 14, Padding = new Thickness(0), Background = Brushes.Transparent, Content = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = Ui.Brush(i == _heroIndex ? "#F5F5F7" : "#55FFFFFF") } };
            ToolTip.SetTip(dot, _heroItems[i].DisplayTitle);
            Avalonia.Automation.AutomationProperties.SetName(dot, L($"Afficher « {_heroItems[i].DisplayTitle} »{(i == _heroIndex ? ", affiché" : "")}", $"Show “{_heroItems[i].DisplayTitle}”{(i == _heroIndex ? ", shown" : "")}"));
            dot.Click += (_, _) => { ShowHero(target); _rotate.Stop(); _rotate.Start(); };
            _dots.Children.Add(dot);
        }
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
