using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// A title's page: its banner, Lecture or Reprendre, favourites and watched, the synopsis, genres, cast and direction
/// (each name leads to that person's titles), the episodes of a series by season, and similar titles.
/// </summary>
public sealed class DetailPage : Page
{
    private MediaItem _item;
    private readonly Image _backdrop = new() { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
    private readonly Grid _header = new() { ClipToBounds = true };
    private readonly StackPanel _top = new() { Spacing = 18, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(56, 0, 56, 34), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _body = new() { Spacing = 36, Margin = new Thickness(56, 8, 56, 64) };
    private readonly ComboBox _seasons = new() { MinWidth = 200 };
    private readonly StackPanel _episodes = new() { Spacing = 10 };
    private readonly TextBlock _episodeHeading = new() { FontSize = 21, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    private List<MediaItem> _allEpisodes = [];
    private MediaItem? _next;
    private bool _filling;
    public override string Rail => "";
    public MediaItem Item => _item;
    public IReadOnlyList<MediaItem> Episodes => _allEpisodes;
    public IEnumerable<string> SeasonLabels => _seasons.Items.OfType<ComboBoxItem>().Select(x => x.Content as string ?? "");

    public DetailPage(MainWindow shell, MediaItem item) : base(shell)
    {
        _item = item;
        _backdrop.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(380) }];
        _header.Children.Add(new Border { Background = Ui.Brush("#0E0E10") });
        _header.Children.Add(_backdrop);
        _header.Children.Add(new Border { Background = Gradient(0, 0.5, 1, 0.5, ("#F2070708", 0), ("#A0070708", 0.45), ("#20070708", 0.8)) });
        _header.Children.Add(new Border { Background = Gradient(0.5, 0, 0.5, 1, ("#00070708", 0.35), ("#FF070708", 1)) });
        var back = Ui.IconButton("back", "Retour · Échap", () => Shell.Back(), "icon", 24);
        back.HorizontalAlignment = HorizontalAlignment.Left; back.VerticalAlignment = VerticalAlignment.Top; back.Margin = new Thickness(40, 26 + shell.TitleBarInset, 0, 0);
        back.Width = back.Height = 46; back.Background = Ui.Brush("#66000000");
        _header.Children.Add(_top); _header.Children.Add(back);
        var page = new StackPanel();
        page.Children.Add(_header); page.Children.Add(_body);
        Content = new ScrollViewer { Content = page };
        SizeChanged += (_, e) => _header.Height = Math.Clamp(e.NewSize.Height * 0.66, 430, 720);
        _seasons.SelectionChanged += (_, _) => { if (!_filling) RenderEpisodes(); };
        Avalonia.Automation.AutomationProperties.SetName(_seasons, "Saison");
    }
    private static LinearGradientBrush Gradient(double x1, double y1, double x2, double y2, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(x1, y1, RelativeUnit.Relative), EndPoint = new RelativePoint(x2, y2, RelativeUnit.Relative) };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        return brush;
    }

    public override async Task LoadAsync()
    {
        RenderTop();
        await RefreshAsync();
    }
    public override async Task RefreshAsync()
    {
        if (Shell.Session is not { } session) return;
        session.Forget();
        var full = await session.ItemAsync(_item.Id);
        await Task.Run(() => session.Store.ApplyLocalProgress([full]));
        _item = full;
        if (_backdrop.Source is null) _ = LoadArtAsync(session);
        if (_item.Type == "Series")
        {
            var episodes = await session.EpisodesAsync(_item.Id);
            _next = await session.NextEpisodeAsync(_item.Id);
            await Task.Run(() => session.Store.ApplyLocalProgress(episodes.Items));
            _allEpisodes = episodes.Items;
            if (_next is not null) _next = _allEpisodes.FirstOrDefault(x => x.Id == _next.Id) ?? _next;
        }
        RenderTop(); RenderBody(session);
        _ = LoadSimilarAsync(session);
    }
    private async Task LoadArtAsync(Session session)
    {
        var art = await session.Images.BackdropAsync(_item, 1920);
        if (art is not null) { _backdrop.Source = art; _backdrop.Opacity = 1; }
        if (await session.Images.LogoAsync(_item) is { } logo && _top.Children.Count > 1 && _top.Children[1] is TextBlock title)
        {
            _top.Children[1] = new Image { Source = logo, MaxHeight = 140, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 6) };
            Avalonia.Automation.AutomationProperties.SetName(_top.Children[1], title.Text ?? "");
        }
    }

    /// <summary>The episode Lecture opens on a series: Jellyfin's next one, else one in progress, else the first unwatched.</summary>
    private MediaItem? SeriesTarget => _next ?? _allEpisodes.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played) ?? _allEpisodes.FirstOrDefault(x => !x.UserData.Played) ?? _allEpisodes.FirstOrDefault();

    private void RenderTop()
    {
        var logo = _top.Children.Count > 1 && _top.Children[1] is Image ? _top.Children[1] : null;
        _top.Children.Clear();
        _top.Children.Add(new TextBlock { Text = (Ui.Kind(_item) + (_item.ProductionYear is { } y ? " · " + y : "")).ToUpperInvariant(), Classes = { "label" }, FontSize = 12.5, Foreground = Ui.Brush("#D0D0D6") });
        _top.Children.Add(logo ?? new TextBlock { Text = _item.Name, FontSize = 54, FontWeight = FontWeight.ExtraBold, TextWrapping = TextWrapping.Wrap, LineHeight = 60 });
        var meta = new List<string>();
        if (_item.ProductionYear is { } year) meta.Add(year.ToString());
        if (_item.Type == "Series") { if (_item.ChildCount is > 0) meta.Add(_item.ChildCount == 1 ? "1 saison" : $"{_item.ChildCount} saisons"); }
        else if (_item.DurationLabel.Length > 0) meta.Add(_item.DurationLabel);
        if (_item.CommunityRating is { } rating) meta.Add($"★ {rating:0.0}");
        if (!string.IsNullOrWhiteSpace(_item.OfficialRating)) meta.Add(_item.OfficialRating!);
        _top.Children.Add(new TextBlock { Text = string.Join("  ·  ", meta), FontSize = 16, Foreground = Ui.Brush("#DADADF") });

        var actions = new WrapPanel { ItemSpacing = 12, LineSpacing = 12 };
        if (_item.Type == "Series")
        {
            var target = SeriesTarget;
            var label = target is null ? "Lecture" : (target.Progress > 0 && !target.UserData.Played ? "Reprendre" : "Regarder") + $" · S{target.ParentIndexNumber ?? 1} É{target.IndexNumber ?? 1}";
            actions.Children.Add(Ui.Action(label, "play", "primary", () => { if (SeriesTarget is { } episode) _ = Shell.PlayAsync(episode); else _ = Shell.PlayAsync(_item); }));
        }
        else
        {
            var resume = _item.Progress > 0 && !_item.UserData.Played;
            actions.Children.Add(Ui.Action(resume ? "Reprendre · " + Ui.Remaining(_item).ToLowerInvariant() : "Lecture", "play", "primary", () => _ = Shell.PlayAsync(_item)));
            if (resume) actions.Children.Add(Ui.Action("Depuis le début", "refresh", "", () => _ = Shell.PlayAsync(_item, fromStart: true)));
        }
        actions.Children.Add(Ui.Action(_item.UserData.IsFavorite ? "Dans tes favoris" : "Ajouter aux favoris", _item.UserData.IsFavorite ? "heart-filled" : "heart", "", () => _ = Shell.SetFavoriteAsync(_item, !_item.UserData.IsFavorite)));
        actions.Children.Add(Ui.Action(_item.UserData.Played ? "Vu" : "Marquer comme vu", "check", _item.UserData.Played ? "chip on" : "", () => _ = Shell.SetPlayedAsync(_item, !_item.UserData.Played)));
        _top.Children.Add(actions);
    }

    private void RenderBody(Session session)
    {
        _body.Children.Clear();
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,48,*") };
        var left = new StackPanel { Spacing = 18 };
        if (!string.IsNullOrWhiteSpace(_item.Overview)) left.Children.Add(new TextBlock { Text = _item.Overview, FontSize = 16, LineHeight = 26, Foreground = Ui.Brush("#C9C9CF"), TextWrapping = TextWrapping.Wrap });
        columns.Children.Add(left);
        var side = new StackPanel { Spacing = 22 }; Grid.SetColumn(side, 2);
        if (_item.Genres.Length > 0) side.Children.Add(Section("GENRES", new TextBlock { Text = string.Join("  ·  ", _item.Genres), FontSize = 15, TextWrapping = TextWrapping.Wrap }));
        if (_item.CastPeople.Length > 0) side.Children.Add(Section("AVEC", People(_item.CastPeople, director: false)));
        if (_item.DirectorPeople.Length > 0) side.Children.Add(Section(_item.DirectorPeople.Length > 1 ? "RÉALISATION" : "RÉALISATION", People(_item.DirectorPeople, director: true)));
        columns.Children.Add(side);
        _body.Children.Add(columns);
        if (_item.Type == "Series") _body.Children.Add(EpisodeSection());
    }
    private static Control Section(string title, Control content) => Ui.Column(8, new TextBlock { Text = title, Classes = { "label" } }, content);
    /// <summary>Names as links to their titles in the library; plain text for a person Jellyfin gave no id.</summary>
    private Control People(IEnumerable<PersonInfo> people, bool director)
    {
        var panel = new WrapPanel { ItemSpacing = 4, LineSpacing = 6 };
        var list = people.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var person = list[i]; var last = i == list.Count - 1;
            Control name;
            if (person.Id is { Length: > 0 })
            {
                var link = new Button { Content = person.Name, Classes = { "link" } };
                ToolTip.SetTip(link, director ? $"Les titres réalisés par {person.Name}" : $"Les titres avec {person.Name}");
                link.Click += (_, _) => Shell.OpenPerson(person, director);
                name = link;
            }
            else name = new TextBlock { Text = person.Name, FontSize = 15 };
            panel.Children.Add(last ? name : Ui.Row(0, name, new TextBlock { Text = ",", FontSize = 15, Margin = new Thickness(0, 0, 4, 0) }));
        }
        return panel;
    }

    private Control EpisodeSection()
    {
        var previous = (_seasons.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
        var pages = EpisodePages.From(_allEpisodes);
        _filling = true;
        _seasons.Items.Clear();
        foreach (var page in pages) _seasons.Items.Add(new ComboBoxItem { Content = page.Label, Tag = page });
        // The page shown before a refresh, else the one of the next episode, of an episode in progress, of the first unwatched.
        var resume = (previous is null ? null : pages.FirstOrDefault(x => x.Season == previous.Season && x.Skip == previous.Skip))
            ?? EpisodePages.Holding(pages, _allEpisodes, SeriesTarget);
        _seasons.SelectedItem = _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, resume)) ?? _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _filling = false;
        RenderEpisodes();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_seasons, Dock.Right);
        if (_seasons.Parent is Panel old) old.Children.Remove(_seasons);
        header.Children.Add(_seasons); header.Children.Add(_episodeHeading);
        if (_episodes.Parent is Panel holder) holder.Children.Remove(_episodes);
        return Ui.Column(14, header, _episodes);
    }
    private void RenderEpisodes()
    {
        _episodes.Children.Clear();
        if (Shell.Session is not { } session) return;
        var page = (_seasons.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
        var episodes = page is null ? [] : EpisodePages.Episodes(_allEpisodes, page);
        _episodeHeading.Text = $"Épisodes  ·  {(page is null ? 0 : _allEpisodes.Count(x => (x.ParentIndexNumber ?? 1) == page.Season))}";
        if (episodes.Count == 0) { _episodes.Children.Add(new TextBlock { Text = "Aucun épisode disponible dans cette bibliothèque.", Foreground = Ui.Muted }); return; }
        foreach (var episode in episodes) _episodes.Children.Add(EpisodeRow(session, episode));
    }
    private Button EpisodeRow(Session session, MediaItem episode)
    {
        var isNext = _next?.Id == episode.Id;
        var image = new Image { Stretch = Stretch.UniformToFill };
        var thumb = new Grid { Width = 176, Height = 99, ClipToBounds = true };
        thumb.Children.Add(new Border { Background = Ui.Brush("#222225") });
        thumb.Children.Add(image);
        thumb.Children.Add(new Border { Background = Ui.Brush("#33000000") });
        thumb.Children.Add(new Icon("play", 22) { Foreground = Brushes.White });
        if (episode.Progress > 0 && !episode.UserData.Played) thumb.Children.Add(new ProgressLine { Value = episode.Progress, Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 8, 7) });
        var art = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = thumb };
        var title = episode.IndexNumber is { } number ? $"{number:00}   {episode.Name}" : episode.Name;
        var copy = Ui.Column(6,
            new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
            new TextBlock { Text = episode.Overview ?? "", FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis, LineHeight = 19 });
        copy.VerticalAlignment = VerticalAlignment.Center; copy.Margin = new Thickness(20, 0);
        var state = episode.UserData.Played ? "✓ Vu" : isNext ? episode.Progress > 0 ? "En cours" : "À suivre" : null;
        var status = Ui.Column(8, new TextBlock { Text = episode.DurationLabel, FontSize = 12.5, Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Right });
        if (state is not null) status.Children.Add(new TextBlock { Text = state, FontSize = 12, Foreground = Ui.Brush(episode.UserData.Played ? "#A0C7A8" : "#DADAE0"), HorizontalAlignment = HorizontalAlignment.Right });
        status.VerticalAlignment = VerticalAlignment.Center; status.Width = 90;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(art); Grid.SetColumn(copy, 1); grid.Children.Add(copy); Grid.SetColumn(status, 2); grid.Children.Add(status);
        var button = new Button { Content = grid, Classes = { "row" } };
        if (isNext) button.Background = Ui.Brush("#18181B");
        button.Click += (_, _) => _ = Shell.PlayAsync(episode);
        button.ContextMenu = Shell.Menu(episode);
        Avalonia.Automation.AutomationProperties.SetName(button, $"Épisode {episode.IndexNumber}, {episode.Name}, {episode.DurationLabel}{(episode.UserData.Played ? ", vu" : isNext ? ", à suivre" : "")}");
        Ui.LoadWhenVisible(button, image, () => session.Images.EpisodeAsync(episode, 352));
        return button;
    }

    private async Task LoadSimilarAsync(Session session)
    {
        try
        {
            var similar = (await session.SimilarAsync(_item.Id)).Items.Where(x => x.Id != _item.Id).ToList();
            if (similar.Count == 0) return;
            await Task.Run(() => session.Store.ApplyLocalProgress(similar));
            if (_body.Children.OfType<CardRow>().FirstOrDefault() is { } old) _body.Children.Remove(old);
            var row = new CardRow("Titres similaires", similar.Select(x => { var card = Cards.Poster(x, session.Images, Shell.Open, Shell.Menu); card.Width = 168; return (Control)card; }), sidePadding: 0);
            _body.Children.Add(row);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
}
