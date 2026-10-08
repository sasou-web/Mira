using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>Accueil: a banner of titles to (re)start, then Continuer à regarder, the latest additions and the favourites.</summary>
public sealed class HomePage : Page
{
    public override string Rail => "home";
    private readonly StackPanel _rows = new() { Spacing = 38, Margin = new Thickness(0, 0, 0, 56) };
    private readonly Grid _hero = new() { ClipToBounds = true };
    private readonly Image _heroImage = new() { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _heroText = new() { Spacing = 18, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(56, 0, 40, 66), MaxWidth = 720 };
    private readonly StackPanel _heroSide = new() { Spacing = 14, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 56, 70), Width = 420 };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 26) };
    private readonly DispatcherTimer _rotate = new() { Interval = TimeSpan.FromSeconds(9) };
    private readonly ScrollViewer _scroll;
    private List<MediaItem> _heroItems = [];
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
        var page = new StackPanel { Spacing = 8 };
        page.Children.Add(_hero); page.Children.Add(_rows);
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
        var store = session.Store;
        // The last known home first, so the page never opens empty; then Jellyfin's answer.
        if (_heroItems.Count == 0 && await Task.Run(() => (store.Load<ItemsResult>("home"), store.Load<List<MediaItem>>("resume"))) is ({ } cachedHome, var cachedResume))
            Render(session, cachedResume ?? [], cachedHome.Items, []);
        var client = session.Client;
        var resume = client.ResumeAsync(); var next = client.NextUpAsync();
        var latest = client.BrowseAsync(limit: 30);
        var favorites = client.BrowseAsync(favorites: true, limit: 30);
        await Task.WhenAll(resume, next, latest, favorites);
        var seasons = Artwork.Seasons(resume.Result.Items.Concat(next.Result.Items)).ToList();
        var thumbs = seasons.Count > 0 ? await TryAsync(() => client.SeasonThumbsAsync(seasons)) ?? [] : [];
        var (merged, history, hidden) = await Task.Run(() =>
        {
            var history = store.RecentPlayback();
            var merged = store.MergeResume(resume.Result.Items, history);
            foreach (var item in merged.Concat(next.Result.Items))
                if (item.Type == "Episode" && item.SeasonId is { } season && thumbs.TryGetValue(season, out var tag)) item.SeasonThumbImageTag = tag;
            store.ApplyLocalProgress(merged); store.ApplyLocalProgress(next.Result.Items); store.ApplyLocalProgress(latest.Result.Items); store.ApplyLocalProgress(favorites.Result.Items);
            store.Save("home", latest.Result); store.Save("resume", merged);
            return (merged, history, store.HiddenFromResume());
        });
        var row = ContinueWatching.WithoutHidden(ContinueWatching.Order(merged, next.Result.Items, history), hidden, history);
        Render(session, row, latest.Result.Items, favorites.Result.Items);
    }
    private static async Task<T?> TryAsync<T>(Func<Task<T>> load) where T : class
    {
        try { return await load(); } catch (Exception ex) when (Errors.Expected(ex)) { return null; }
    }

    private void Render(Session session, List<MediaItem> resume, List<MediaItem> latest, List<MediaItem> favorites)
    {
        Continue = resume; Latest = latest;
        _rows.Children.Clear();
        if (resume.Count > 0) _rows.Children.Add(new CardRow("Continuer à regarder", resume.Select(x => (Control)Cards.Wide(x, session.Images, Shell.Open, 320, Shell.Menu))));
        if (latest.Count > 0) _rows.Children.Add(new CardRow("Ajouts récents", latest.Select(x => (Control)Sized(Cards.Poster(x, session.Images, Shell.Open, Shell.Menu)))));
        if (favorites.Count > 0) _rows.Children.Add(new CardRow("Tes favoris", favorites.Select(x => (Control)Sized(Cards.Poster(x, session.Images, Shell.Open, Shell.Menu)))));
        if (resume.Count == 0 && latest.Count == 0)
            _rows.Children.Add(new Border { Margin = new Thickness(56, 0), Child = Ui.Column(8, Ui.Text("Ta bibliothèque est vide pour l’instant", 20, FontWeight.Bold), Ui.Text("Ajoute des films ou des séries dans les dossiers de Jellyfin : ils apparaîtront ici.", 14.5, color: Ui.Muted)) });
        // The banner: what is being watched first, then the latest titles with a backdrop.
        var heroes = resume.Take(2).Concat(latest.Where(x => x.BackdropImageTags.Length > 0)).DistinctBy(x => ContinueWatching.Group(x)).Take(5).ToList();
        if (heroes.Select(x => x.Id).SequenceEqual(_heroItems.Select(x => x.Id)) && heroes.Count > 0) { _heroItems = heroes; ShowHero(_heroIndex, animate: false); return; }
        _heroItems = heroes; ShowHero(0);
        _rotate.Stop(); if (heroes.Count > 1 && IsAttachedToVisualTree()) _rotate.Start();
    }
    private bool IsAttachedToVisualTree() => this.GetVisualRoot() is not null;
    private static Control Sized(Control card) { card.Width = 176; return card; }

    private void ShowHero(int index, bool animate = true)
    {
        if (Shell.Session is not { } session) return;
        _heroText.Children.Clear(); _heroSide.Children.Clear(); _dots.Children.Clear();
        if (_heroItems.Count == 0) { _hero.Height = 220; _heroImage.Source = null; return; }
        _heroIndex = (index % _heroItems.Count + _heroItems.Count) % _heroItems.Count;
        var item = _heroItems[_heroIndex];
        var version = ++_heroVersion;
        if (animate) _heroImage.Opacity = 0;
        _ = LoadHeroArtAsync(session, item, version);

        var title = new TextBlock { Text = item.DisplayTitle, FontSize = 58, FontWeight = FontWeight.ExtraBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, LineHeight = 64 };
        _heroText.Children.Add(title);
        var tags = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        if (item.Type == "Episode") tags.Children.Add(Ui.Tag($"S{item.ParentIndexNumber ?? 1} · É{item.IndexNumber ?? 1}"));
        if (item.ProductionYear is { } year) tags.Children.Add(Ui.Tag(year.ToString()));
        tags.Children.Add(Ui.Tag(Ui.Kind(item)));
        if (item.Type != "Series" && item.DurationLabel.Length > 0) tags.Children.Add(Ui.Tag(item.Progress > 0 ? Ui.Remaining(item) : item.DurationLabel));
        if (item.CommunityRating is { } rating) tags.Children.Add(Ui.Tag($"★ {rating:0.0}"));
        _heroText.Children.Add(tags);
        var resume = item.Progress > 0 && !item.UserData.Played;
        var heart = Ui.IconButton(item.UserData.IsFavorite ? "heart-filled" : "heart", item.UserData.IsFavorite ? "Retirer des favoris" : "Ajouter aux favoris", () => _ = Shell.SetFavoriteAsync(item, !item.UserData.IsFavorite), "icon", 24);
        heart.Width = heart.Height = 46;
        _heroText.Children.Add(Ui.Row(12,
            Ui.Action(resume ? "Reprendre" : "Lecture", "play", "primary", () => _ = Shell.PlayAsync(item)),
            Ui.Action("Plus d’infos", "info", "", () => Shell.Open(item)), heart));
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
