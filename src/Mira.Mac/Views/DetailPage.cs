using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// A title's page, as Windows': it opens on its picture (the whole window for a film, most of it for a series), its logo
/// or name, catchphrase, facts and summary on it, then a large Lecture and the round buttons beside it; under the picture,
/// the cast with their photos, the genres, direction and studios (each one leads to its titles), the episodes of a series
/// by season (a season marked watched at once), its saga, its studio's other titles and similar titles.
/// </summary>
public sealed partial class DetailPage : Page
{
    private MediaItem _item;
    private readonly Image _backdrop = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private readonly Grid _header = new() { ClipToBounds = true, Height = 600 };
    /// <summary>What sits on the picture, at its bottom left: kind, logo or name, catchphrase, facts, summary, buttons.</summary>
    private readonly StackPanel _top = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(56 + MainWindow.RailWidth, 0, 56, 56) };
    private readonly StackPanel _body = new() { Spacing = 36, Margin = new Thickness(56, 8, 56, 64) };
    private readonly ComboBox _seasons = new() { MinWidth = 200 };
    private readonly StackPanel _episodes = new() { Spacing = 0 };
    /// <summary>The seasons as pills (EpisodeTiles): two to ten pages; more, the list.</summary>
    private readonly WrapPanel _pills = new() { IsVisible = false, Margin = new Thickness(0, 2, 0, 8) };
    /// <summary>The tiles of the season shown (stills, hollow ones for those the library lacks), sized again with the page.</summary>
    private readonly List<(Control Tile, Control Picture)> _tiles = [];
    private WrapPanel? _tileGrid;
    private double _tileWidth;
    private readonly TextBlock _episodeHeading = new() { FontSize = 24, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Its logo from Jellyfin once loaded: it takes the place of its name.</summary>
    private Avalonia.Media.Imaging.Bitmap? _logo;
    private bool _overviewOpen;
    /// <summary>The height of the page on screen: the picture's own follows it.</summary>
    private double _viewport;
    /// <summary>« Marquer la saison comme vue », beside the season list when the series has another season.</summary>
    private readonly Button _seasonMark = new() { Classes = { "chip", "quiet" }, IsVisible = false, Margin = new Thickness(0, 0, 10, 0) };
    private bool _marking;
    private List<MediaItem> _allEpisodes = [];
    private MediaItem? _next;
    /// <summary>The file of the episode Lecture plays on a series: its picture shows in the page (« 1080p »).</summary>
    private (string Id, MediaSource? Source)? _targetFile;
    private bool _filling;
    /// <summary>An episode a search found: the series' page opens at its season, the episode set apart.</summary>
    private readonly MediaItem? _found;
    /// <summary>« Suivre une série »: known once the relay answered for an administrator; the series as it follows it, null when it does not.</summary>
    private bool _followKnown;
    private RelayFollow? _follow;
    // This series' episodes from TheTVDB (EpisodeShelf): missing and coming ones at their place; the lines opened.
    private List<MediaItem> _virtuals = [];
    private readonly HashSet<string> _shelfOpen = [];
    public override string Rail => "";
    public MediaItem Item => _item;
    public IReadOnlyList<MediaItem> Episodes => _allEpisodes;
    /// <summary>The series' episodes the library lacks (TheTVDB's, through Jellyfin), once read.</summary>
    public IReadOnlyList<MediaItem> Missing => _virtuals;
    /// <summary>For the self-check: the list of episodes on the page, as many rows as the season shown.</summary>
    public bool EpisodesShown => TopLevel.GetTopLevel(_episodes) is not null && _episodes.Children.Count > 0;
    /// <summary>For the self-check: the seasons as pills, the tiles of the season shown, the top of the grid brought into view.</summary>
    public bool PillsShown => _pills.IsVisible && _pills.Children.Count > 1;
    public int TilesShown => _tiles.Count;
    public void ShowEpisodesForCheck() => Avalonia.Threading.Dispatcher.UIThread.Post(() => _episodeHeading.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
    public IEnumerable<string> SeasonLabels => _seasons.Items.OfType<ComboBoxItem>().Select(x => x.Content as string ?? "");

    public DetailPage(MainWindow shell, MediaItem item, MediaItem? atEpisode = null) : base(shell, underRail: true)
    {
        _item = item; _found = atEpisode;
        _backdrop.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(380) }];
        _header.Children.Add(new Border { Background = Ui.Brush("#0F0F0F") });
        _header.Children.Add(_backdrop);
        // Shaded from the left and the bottom, where the text and the buttons sit, as on Windows.
        _header.Children.Add(new Border { Background = Gradient(0, 0.5, 1, 0.5, ("#F0000000", 0), ("#C4000000", 0.3), ("#40000000", 0.62), ("#00000000", 0.86)) });
        _header.Children.Add(new Border { Background = Gradient(0.5, 0, 0.5, 1, ("#66000000", 0), ("#00000000", 0.2), ("#00000000", 0.5), ("#A6000000", 0.8), ("#FF000000", 1)) });
        var back = Ui.IconButton("back", L("Retour · Échap", "Back · Esc"), () => Shell.Back(), "icon", 24);
        back.HorizontalAlignment = HorizontalAlignment.Left; back.VerticalAlignment = VerticalAlignment.Top; back.Margin = new Thickness(44 + MainWindow.RailWidth, 30 + shell.TitleBarInset, 0, 0);
        back.Width = back.Height = 46;
        _header.Children.Add(_top); _header.Children.Add(back);
        var page = new StackPanel();
        // The picture runs under the rail, as on Windows; the text keeps clear of it.
        _body.Margin += new Thickness(MainWindow.RailWidth, 0, 0, 0);
        page.Children.Add(_header); page.Children.Add(_body);
        Content = new ScrollViewer { Content = page };
        SizeChanged += (_, e) => { _viewport = e.NewSize.Height; SizeHero(); };
        _top.SizeChanged += (_, e) => { if (e.HeightChanged) SizeHero(); };
        _seasons.SelectionChanged += (_, _) => { if (!_filling) { RenderEpisodes(); ShowOffline(); } };
        _episodes.SizeChanged += (_, e) => { if (e.WidthChanged && _tiles.Count > 0 && EpisodeTiles.Width(e.NewSize.Width) != _tileWidth) LayoutTiles(); };
        // « Garder sur ce Mac »: the button and the episodes follow the copies as they come.
        void OfflineUpdated() { ShowOffline(); if (_item.Type == "Series" && _allEpisodes.Count > 0) RenderEpisodes(); }
        void OfflineProgressed() => ShowOffline();
        AttachedToVisualTree += (_, _) => { Shell.OfflineUpdated += OfflineUpdated; Shell.OfflineProgressed += OfflineProgressed; };
        DetachedFromVisualTree += (_, _) => { Shell.OfflineUpdated -= OfflineUpdated; Shell.OfflineProgressed -= OfflineProgressed; };
        _seasonMark.Click += (_, _) => _ = MarkSeasonAsync();
        Avalonia.Automation.AutomationProperties.SetName(_seasons, L("Saison", "Season"));
    }
    private static LinearGradientBrush Gradient(double x1, double y1, double x2, double y2, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(x1, y1, RelativeUnit.Relative), EndPoint = new RelativePoint(x2, y2, RelativeUnit.Relative) };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        return brush;
    }
    /// <summary>
    /// The picture as tall as the window for a film, most of it for a series (its episodes lead below the picture); never
    /// shorter than what sits on it (a summary opened, a small window).
    /// </summary>
    private void SizeHero()
    {
        var view = _viewport > 0 ? _viewport : 760;
        var wanted = _item.Type == "Series" ? Math.Max(470, view * .72) : Math.Max(470, view);
        _header.Height = Math.Max(wanted, _top.Bounds.Height + _top.Margin.Bottom + 110 + Shell.TitleBarInset);
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
        await Task.Run(() => Errors.TryCache(() => session.Store.ApplyLocalProgress([full])));
        _item = full;
        if (_backdrop.Source is null) _ = LoadArtAsync(session);
        if (_item.Type == "Series")
        {
            var episodes = await session.EpisodesAsync(_item.Id);
            _next = await session.NextEpisodeAsync(_item.Id);
            await Task.Run(() => Errors.TryCache(() => session.Store.ApplyLocalProgress(episodes.Items)));
            _allEpisodes = episodes.Items;
            if (_next is not null) _next = _allEpisodes.FirstOrDefault(x => x.Id == _next.Id) ?? _next;
        }
        RenderTop(); RenderBody(session);
        _ = LoadSimilarAsync(session);
        if (_item.Type == "Series") { _ = LoadFollowAsync(session); _ = LoadCalendarAsync(session); _ = LoadMissingAsync(session); }
        if (!_admin) _ = CheckAdministratorAsync(session);
        if (_item.Type == "Series" && SeriesTarget is { } target && _targetFile?.Id != target.Id) await LoadTargetFileAsync(session, target);
    }
    private static string FavoriteLabel(bool on) => on ? L("Favori", "Favorite") : L("Ajouter aux favoris", "Add to favorites");
    private static string FavoriteTip(bool on) => on ? L("Dans tes favoris. Clique pour le retirer", "In your favorites. Click to remove it") : FavoriteLabel(false);
    private string WatchedLabel(bool on) => on ? _item.Type == "Series" ? L("Vue", "Watched") : L("Vu", "Watched")
        : _item.Type == "Series" ? L("Marquer la série comme vue", "Mark show as watched") : L("Marquer comme vu", "Mark as watched");
    private string WatchedTip(bool on) => on ? L("Vu. Clique pour le marquer comme non vu", "Watched. Click to mark it as unwatched") : WatchedLabel(false);
    /// <summary>
    /// A state button of the page (Favori, Vu, Suivre, Garder sur ce Mac), as on Windows: the same rounded square on or
    /// off, its glyph alone telling which (<see cref="StateIcon"/>: an outline off, solid white on, swapped at once).
    /// Never a word that comes, an outline or a new width: a click on the page moves nothing.
    /// </summary>
    /// <param name="name">What VoiceOver calls it, its state included (« Favori », « Ajouter aux favoris »).</param>
    /// <param name="tip">Its tooltip, also its help for VoiceOver: what it is and what a click does.</param>
    /// <param name="progress">How far a copy is, drawn as a ring, while it comes.</param>
    private static void ShowState(Button button, string kind, bool on, string name, string tip, double? progress = null)
    {
        if (button.Content is not StateIcon glyph) button.Content = glyph = new StateIcon(kind) { VerticalAlignment = VerticalAlignment.Center };
        glyph.Show(kind, on, progress);
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        Avalonia.Automation.AutomationProperties.SetHelpText(button, tip != name ? tip : null);
    }
    /// <summary>A state button of the line: its glyph alone, in a rounded square (<see cref="ActionLine"/>).</summary>
    private static Button StateButton(string kind, Action click)
    {
        var button = new Button { Content = new StateIcon(kind) { VerticalAlignment = VerticalAlignment.Center }, Classes = { "state" }, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }
    /// <summary>A button of the line with words: its icon, its words beside it (hidden while it is round).</summary>
    private static Button LineButton(string icon, string label, Action click)
    {
        var button = new Button { Content = Words(icon, label, false), Classes = { "state" }, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }
    /// <summary>An icon, its words beside it, and a green dot when on and live (a group).</summary>
    private static StackPanel Words(string icon, string label, bool dot)
    {
        var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 9 };
        content.Children.Add(new Icon(icon, 18) { VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (dot) content.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = Ui.Brush("#86C6A2"), Margin = new Thickness(1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return content;
    }
    private void ShowFavorite(bool on) => ShowState(_favorite!, "favorite", on, FavoriteLabel(on), FavoriteTip(on));
    private void ShowWatched(bool on) => ShowState(_watched!, "watched", on, WatchedLabel(on), WatchedTip(on));
    // Favori and Vu change at the click, before Jellyfin answers (their glyph alone: the page does not move); a refusal
    // puts them back.
    private bool _favoriteBusy, _watchedBusy;
    private async Task FlipFavoriteAsync()
    {
        if (_favoriteBusy) return; _favoriteBusy = true;
        var item = _item; var want = !item.UserData.IsFavorite;
        try
        {
            _settled = true; ShowFavorite(want);
            await Shell.SetFavoriteAsync(item, want);
            if (item.UserData.IsFavorite != want && ReferenceEquals(item, _item)) RenderTop();
        }
        finally { _favoriteBusy = false; }
    }
    private async Task FlipWatchedAsync()
    {
        if (_watchedBusy) return; _watchedBusy = true;
        var item = _item; var want = !item.UserData.Played;
        try
        {
            _settled = true; ShowWatched(want);
            await Shell.SetPlayedAsync(item, want);
            if (item.UserData.Played != want && ReferenceEquals(item, _item)) RenderTop();
        }
        finally { _watchedBusy = false; }
    }
    private async Task LoadTargetFileAsync(Session session, MediaItem episode)
    {
        try
        {
            var full = await session.ItemAsync(episode.Id);
            if (SeriesTarget?.Id != episode.Id) return;
            _targetFile = (episode.Id, full.OwnSource); RenderTop();
        }
        // Only a detail of the page: without it, the rest is complete.
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    private async Task LoadArtAsync(Session session)
    {
        var art = await session.Images.BackdropAsync(_item, 1920, ownPage: true);
        if (art is not null) { _backdrop.Source = art; _backdrop.Opacity = 1; }
        if (await session.Images.LogoAsync(_item) is not { } logo) return;
        _logo = logo;
        var index = _top.Children.IndexOf(_title);
        if (index >= 0) _top.Children[index] = Title();
    }
    /// <summary>Its name in large type, the page's heading; its logo instead once Jellyfin gave one.</summary>
    private TextBlock _title = new();
    private Control Title()
    {
        if (_logo is { } logo)
        {
            var image = new Image { Source = logo, MaxWidth = 520, MaxHeight = 160, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform, Margin = new Thickness(0, 2, 0, 4) };
            Avalonia.Automation.AutomationProperties.SetName(image, _item.DisplayTitle);
            Avalonia.Automation.AutomationProperties.SetHeadingLevel(image, 1);
            return image;
        }
        // Its own line height: a tighter one (Windows' 70) cuts the letters that go below the line (« g », « p »).
        _title = new TextBlock { Text = _item.DisplayTitle, FontSize = 68, FontWeight = FontWeight.ExtraBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -10, 0, -12) };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(_title, 1);
        return _title;
    }

    /// <summary>The episode Lecture opens on a series: Jellyfin's next one, else one in progress, else the first unwatched.</summary>
    private MediaItem? SeriesTarget => _next ?? EpisodePages.Start(_allEpisodes);

    /// <summary>Lecture picks up where it stopped: started, not finished, and Mira set to remember.</summary>
    private bool Resumes(MediaItem item) => item.Resumable && Shell.Settings.RememberPosition;

    /// <summary>
    /// A click changed the page (Favori, Vu): from then on, until the player opens, it keeps where everything is, as on
    /// Windows. Words and glyphs change, never places: « Depuis le début » stays (it still plays from the start), the
    /// progress line fades out keeping its room, Lecture never gets narrower.
    /// </summary>
    private bool _settled;
    /// <summary>The line of buttons, made once for the page and changed where it is, its buttons too: a click never rebuilds it.</summary>
    private ActionLine? _line;
    private Button? _play, _restart, _together, _favorite, _watched, _followButton, _trailer, _fix;
    /// <summary>What « Depuis le début » plays: the film, or the episode Lecture picks up.</summary>
    private MediaItem? _restartFrom;
    private string? _togetherShown;
    private ProgressLine? _progressLine;

    /// <summary>
    /// Kind, logo or name, catchphrase, facts and summary drawn again; the line of buttons and the progress line under it
    /// stay where they are, changed in place.
    /// </summary>
    private void RenderTop()
    {
        var line = _line ??= Actions();
        foreach (var child in _top.Children.Where(x => !ReferenceEquals(x, line) && !ReferenceEquals(x, _progressLine)).ToList()) _top.Children.Remove(child);
        var at = 0;
        void Add(Control control) => _top.Children.Insert(at++, control);
        var kind = Ui.Kind(_item).ToUpperInvariant();
        Add(new TextBlock { Text = _item.ProductionYear is { } y ? $"{kind}  ·  {y}" : kind, FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = Ui.Brush("#E0E0E0"), Margin = new Thickness(1, 0, 0, 14) });
        Add(Title());
        if (_item.Taglines?.Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) is { } tagline)
            Add(new TextBlock { Text = tagline, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#E0E0E0"), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 12, 0, 0) });
        var meta = new List<string>();
        if (_item.ProductionYear is { } year) meta.Add(year.ToString());
        if (_item.Type == "Series") { if (_item.ChildCount is > 0) meta.Add(Count(_item.ChildCount.Value, "saison", "saisons", "season", "seasons")); }
        else if (_item.DurationLabel.Length > 0) meta.Add(_item.DurationLabel);
        if (_item.CommunityRating is { } rating and > 0) meta.Add("★ " + Number(rating, "0.0"));
        if (!string.IsNullOrWhiteSpace(_item.OfficialRating)) meta.Add(_item.OfficialRating!);
        // The picture of the file Lecture plays and when it ends: the film's, or the episode's it picks up at.
        var played = _item.Type == "Series" ? SeriesTarget : _item;
        if ((_item.Type != "Series" ? MediaQuality.Label(_item) : _targetFile is { } known && known.Id == played?.Id ? MediaQuality.Label(known.Source) : null) is { } file) meta.Add(file);
        if (MediaQuality.EndsAt(played, DateTime.Now, played is not null && Resumes(played)) is { } end) meta.Add(end);
        Add(new TextBlock { Text = string.Join("  ·  ", meta), FontSize = 14.5, Foreground = Ui.Brush("#C7C7C7"), Margin = new Thickness(1, 16, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        // The summary on three lines over the picture, « Lire la suite » for the rest.
        var summary = OverviewText.Plain(string.IsNullOrWhiteSpace(_item.Overview) ? L("Aucun résumé disponible pour ce titre.", "No summary available for this title.") : _item.Overview);
        var overview = new TextBlock { Text = summary, FontSize = 15, LineHeight = 24, Foreground = Ui.Brush("#C7C7C7"), TextWrapping = TextWrapping.Wrap, MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Left, MaxLines = _overviewOpen ? 0 : 3, TextTrimming = TextTrimming.WordEllipsis, Margin = new Thickness(1, 16, 0, 0) };
        Add(overview);
        // Three lines of the summary's width hold about 300 characters: a longer one gets « Lire la suite ».
        if (summary.Length > 260)
        {
            string MoreText() => _overviewOpen ? L("Réduire", "Show less") : L("Lire la suite", "Read more");
            var more = new Button { Classes = { "quiet" }, Content = MoreText(), Padding = new Thickness(0, 4), Margin = new Thickness(1, 4, 0, 0), FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Left };
            more.Click += (_, _) => { _overviewOpen = !_overviewOpen; overview.MaxLines = _overviewOpen ? 0 : 3; more.Content = MoreText(); };
            Add(more);
        }
        if (line.Parent is null) _top.Children.Add(line);
        ShowActions(); ShowProgress();
        SizeHero();
    }
    /// <summary>
    /// How far the film was watched, under its buttons and as wide as the page, as on Windows; on a settled page, a line
    /// that goes fades out and keeps its room.
    /// </summary>
    private void ShowProgress()
    {
        if (_item.Type != "Series" && Resumes(_item))
        {
            if (_progressLine is null)
            {
                _progressLine = new ProgressLine { Height = 5, Margin = new Thickness(2, 18, 0, 0), Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }] };
                _top.Children.Add(_progressLine);
            }
            _progressLine.Value = _item.Progress; _progressLine.IsVisible = true; _progressLine.Opacity = 1; _progressLine.InvalidateVisual();
            return;
        }
        if (_progressLine is not { IsVisible: true } line) return;
        if (_settled) line.Opacity = 0; else line.IsVisible = false;
    }

    /// <summary>
    /// Lecture, then « Depuis le début », « Regarder ensemble », Favori, Vu, « Suivre… », « Garder sur ce Mac » and « ⋮ »,
    /// in Windows' order, made once for the page: Lecture and « Regarder ensemble » with their words, the others the
    /// same rounded squares on or off, their words in their tooltip (<see cref="ShowActions"/> sets what each shows).
    /// </summary>
    private ActionLine Actions()
    {
        var line = new ActionLine { Margin = new Thickness(0, 22, 0, 0) };
        _play = new Button { Classes = { "primary" }, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CornerRadius = ActionLine.Corners };
        _play.Click += (_, _) =>
        {
            _settled = false;
            if (_item.Type == "Series" && SeriesTarget is { } episode) _ = Shell.PlayAsync(episode); else _ = Shell.PlayAsync(_item);
        };
        line.Children.Add(_play);
        _restart = LineButton("refresh", L("Depuis le début", "From the beginning"), () => { _settled = false; if (_restartFrom is { } from) _ = Shell.PlayAsync(from, fromStart: true); });
        ToolTip.SetTip(_restart, L("Depuis le début", "From the beginning"));
        line.Children.Add(_restart);
        Button? together = null;
        together = _together = LineButton("users", L("Regarder ensemble", "Watch together"), () => _ = TogetherMenuAsync(together!));
        ActionLine.SetKeep(together, true); ActionLine.SetShrink(together, 5);
        line.Children.Add(together);
        _favorite = StateButton("favorite", () => _ = FlipFavoriteAsync()); line.Children.Add(_favorite);
        _watched = StateButton("watched", () => _ = FlipWatchedAsync()); line.Children.Add(_watched);
        Button? follow = null;
        follow = _followButton = StateButton("follow", () => FollowMenu(follow!)); line.Children.Add(follow);
        _offlineButton = StateButton("offline", OfflineClicked); line.Children.Add(_offlineButton);
        // « Bande-annonce »: kept on the server, it plays in Mira; on the web, it opens in the browser, its tooltip saying so before the click.
        _trailer = Ui.IconButton("trailer", Trailers.Label, () => _ = TrailerAsync(), "state", 18, Trailers.Label);
        _trailer.VerticalAlignment = VerticalAlignment.Center; _trailer.IsVisible = false;
        line.Children.Add(_trailer);
        // « ⋮ » as on Windows: what an administrator rarely needs stays out of the row of buttons.
        Button? fix = null;
        fix = _fix = Ui.IconButton("more", L("Identifier le titre, actualiser ses informations", "Identify the title, refresh its details"), () => FixMenu(fix!), "state", 18, L("Plus d’actions sur ce titre", "More actions for this title"));
        fix.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(fix);
        return line;
    }
    /// <summary>What each button of the line shows now, changed where it is.</summary>
    private void ShowActions()
    {
        var inGroup = Shell.Group.Active;
        var play = _play!;
        // On a settled page, Lecture keeps at least the width it has on screen, whatever its new words.
        var floor = _settled ? Math.Max(play.MinWidth, play.Bounds.Width) : 0;
        MediaItem? restart = null;
        if (_item.Type == "Series")
        {
            // « Reprendre · S01 E07 · 12 min restantes »: the button says which episode it opens.
            var target = SeriesTarget;
            var code = target is null ? "" : $"S{target.ParentIndexNumber ?? 1:00} E{target.IndexNumber ?? 1:00}";
            var resume = target is not null && Resumes(target);
            var label = target is null ? L("Regarder", "Watch") : GroupPlayLabel(target, code) ?? L($"{(resume ? "Reprendre" : "Regarder")} · {code}", $"{(resume ? "Resume" : "Watch")} · {code}");
            var detail = resume && !inGroup ? MediaQuality.Left(target) : null;
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            if (detail is not null) text.Inlines!.Add(new Avalonia.Controls.Documents.Run("  ·  " + detail) { FontWeight = FontWeight.Normal, FontSize = 12.5 });
            // Never narrower than « Reprendre · S01 E01 », held by an invisible copy: new words after a click move nothing beside it.
            var room = new TextBlock { Text = L("Reprendre · S01 E01", "Resume · S01 E01"), Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
            play.Content = Ui.Row(9, new Icon("play", 18) { VerticalAlignment = VerticalAlignment.Center }, new Grid { Children = { room, text } });
            play.Height = ActionLine.Round; play.Padding = new Thickness(25, 0); play.MinWidth = floor;
            Avalonia.Automation.AutomationProperties.SetName(play, detail is null ? label : $"{label}, {detail}");
            if (resume) restart = target;
        }
        else
        {
            // A film's page leads with its Lecture: large, what it plays, then the time left (or its length) under it.
            var resume = Resumes(_item);
            var label = GroupPlayLabel(_item, "") ?? (resume ? L("Reprendre", "Resume") : _item.UserData.Played ? L("Revoir", "Watch again") : L("Regarder", "Watch"));
            var detail = resume && !inGroup ? MediaQuality.Left(_item) : _item.DurationLabel is { Length: > 0 } length ? length : null;
            var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = label, FontSize = 18, FontWeight = FontWeight.Bold });
            if (detail is not null) text.Children.Add(new TextBlock { Text = detail, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Opacity = .72, Margin = new Thickness(0, 1, 0, 0) });
            var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            content.Children.Add(new Icon("play", 24) { VerticalAlignment = VerticalAlignment.Center }); content.Children.Add(text);
            play.Content = content; play.Padding = new Thickness(32, 15, 36, 15); play.MinWidth = Math.Max(250, floor);
            Avalonia.Automation.AutomationProperties.SetName(play, detail is null ? label : $"{label}, {detail}");
            if (resume) restart = _item;
        }
        // « Depuis le début » beside Lecture when it picks up; on a settled page, it stays where it is.
        var restartButton = _restart!;
        restartButton.IsVisible = restart is not null || _settled && restartButton.IsVisible;
        _restartFrom = restart ?? (restartButton.IsVisible ? _restartFrom : null);
        Avalonia.Automation.AutomationProperties.SetName(restartButton, L("Depuis le début", "From the beginning"));
        // « Regarder ensemble » keeps its words beside Lecture; in a group, its count and a live dot.
        var group = inGroup ? Shell.Group.Group : null;
        var togetherLabel = group is null ? L("Regarder ensemble", "Watch together") : L("En groupe · ", "In a group · ") + Together.People(Math.Max(1, group.Participants.Count));
        var together = _together!;
        if (_togetherShown != togetherLabel + (group is null ? "" : "·")) { together.Content = Words("users", togetherLabel, group is not null); _togetherShown = togetherLabel + (group is null ? "" : "·"); }
        var togetherTip = group is null
            ? L("Regarder ce titre au même moment que d’autres comptes de ce Jellyfin, chacun chez soi", "Watch this title at the same moment as other accounts on this Jellyfin, each at home")
            : L($"En groupe · {Together.GroupText(group)}. Clique pour inviter ou quitter le groupe", $"In a group · {Together.GroupText(group)}. Click to invite or leave the group");
        ToolTip.SetTip(together, togetherTip);
        Avalonia.Automation.AutomationProperties.SetName(together, group is null ? togetherLabel : togetherTip);
        Avalonia.Automation.AutomationProperties.SetHelpText(together, group is null ? togetherTip : null);
        ShowFavorite(_item.UserData.IsFavorite); ShowWatched(_item.UserData.Played);
        var follow = _followButton!;
        follow.IsVisible = _followKnown;
        if (_followKnown)
            // « Suivre… » off, « Suivie » on (its calendar solid, with a tick): the state of the series seen at a glance.
            ShowState(follow, "follow", _follow is not null, _follow is null ? L("Suivre…", "Follow…") : L("Suivie", "Following"), _follow is null
                ? L("Suivre les nouveaux épisodes : le PC serveur les cherche et les télécharge tout seul, avec TorLink", "Follow new episodes: the server PC looks for them and downloads them on its own, with TorLink")
                : string.Join(" · ", new[] { L("Suivie", "Following"), RemoteLines.FollowNote(_follow.Note), RemoteLines.Follow(_follow, DateTimeOffset.UtcNow) }.Where(x => x.Length > 0)));
        ShowOffline();
        var trailer = _trailer!;
        trailer.IsVisible = Trailers.Source(_item) != TrailerSource.None;
        if (trailer.IsVisible) { ToolTip.SetTip(trailer, Trailers.Tip(_item)); Avalonia.Automation.AutomationProperties.SetName(trailer, Trailers.Tip(_item)); }
        _fix!.IsVisible = _admin && _item.Type is "Movie" or "Series";
    }
    /// <summary>For the self-check: Lecture.</summary>
    public Button? PlayButton => _play;
    /// <summary>For the self-check: the buttons beside Lecture, whether each shows its words, and whether a state button is on.</summary>
    public IEnumerable<(string Name, bool Words, bool On)> ActionButtons() => (_line?.Children.OfType<Button>().Skip(1) ?? [])
        .Where(x => x.IsVisible).Select(x => (Avalonia.Automation.AutomationProperties.GetName(x) ?? "", ActionLine.ShowsWords(x), x.Content is StateIcon { On: true }));
    /// <summary>For the self-check: where Lecture, each button and the words over the picture are, in the page.</summary>
    public IReadOnlyDictionary<string, Rect> TopPlaces()
    {
        var places = new Dictionary<string, Rect>();
        var index = 0;
        foreach (var control in _top.Children.Where(x => x.IsVisible && !ReferenceEquals(x, _line) && !ReferenceEquals(x, _progressLine)))
            if (control.TranslatePoint(new Point(), this) is { } at) places[$"{control.GetType().Name} {++index}"] = new Rect(at, control.Bounds.Size);
        foreach (var button in _line?.Children.OfType<Button>().Where(x => x.IsVisible) ?? [])
            if (button.TranslatePoint(new Point(), this) is { } at) places[Avalonia.Automation.AutomationProperties.GetName(button) is { Length: > 0 } name && !ReferenceEquals(button, _play) ? button.Content is StateIcon icon ? icon.Kind : name : "Lecture"] = new Rect(at, button.Bounds.Size);
        return places;
    }
    /// <summary>For the self-check: a click on Favori or Vu, as the pointer makes it.</summary>
    public void ClickForCheck(string kind) => (kind == "favorite" ? _favorite : _watched)?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    /// <summary>For the self-check: the picture's height, and the cast shown under it.</summary>
    public double HeroHeight => _header.Bounds.Height;
    public int CastShown => _castCount;
    public void ShowCastForCheck() => _cast?.BringIntoView();
    /// <summary>For the self-check: a season of the list shown, and the end of its episodes brought into view.</summary>
    public bool ShowSeasonForCheck(string label)
    {
        if (_seasons.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Content as string == label) is not { } item) return false;
        _seasons.SelectedItem = item;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (_tileGrid?.Children.OfType<Control>().LastOrDefault() ?? _episodes.Children.OfType<Control>().LastOrDefault())?.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
        return true;
    }

    // —— « Garder sur ce Mac », as Windows' « Garder sur ce PC » ——
    private Button? _offlineButton;
    /// <summary>What the button acts on: the film, or every episode of the season shown (not only a slice of a long one).</summary>
    private List<MediaItem> OfflineTargets()
    {
        if (_item.Type == "Series")
        {
            var season = ((_seasons.SelectedItem as ComboBoxItem)?.Tag as EpisodePage)?.Season ?? 1;
            return _allEpisodes.Where(x => (x.ParentIndexNumber ?? 1) == season && MainWindow.CanKeep(x)).ToList();
        }
        return MainWindow.CanKeep(_item) ? [_item] : [];
    }
    private int SeasonShown() => ((_seasons.SelectedItem as ComboBoxItem)?.Tag as EpisodePage)?.Season ?? 1;
    /// <summary>Keep the film or the season shown; how far it is; or, kept, a click to remove it.</summary>
    private void ShowOffline()
    {
        if (_offlineButton is not { } button) return;
        var targets = OfflineTargets();
        button.IsVisible = targets.Count > 0 && Shell.Session is not null;
        if (!button.IsVisible) return;
        var copies = targets.Select(x => Shell.CopyOf(x.Id)).OfType<OfflineCopy>().ToList();
        var complete = copies.Count(x => x.Complete);
        // Off, an arrow in a circle; coming, a ring of how far it is (its words in the tooltip); kept, a solid disc.
        if (copies.Count == 0)
        {
            var keep = _item.Type == "Series" ? L($"Garder la saison {SeasonShown()} sur ce Mac ({Lang.Count(targets.Count, "épisode", "épisodes", "episode", "episodes")}), pour la regarder sans connexion", $"Keep season {SeasonShown()} on this Mac ({Lang.Count(targets.Count, "épisode", "épisodes", "episode", "episodes")}), to watch it offline") : L("Garder ce film sur ce Mac, pour le regarder sans connexion", "Keep this movie on this Mac, to watch it offline");
            ShowState(button, "offline", false, keep, keep);
        }
        else if (complete == targets.Count)
            ShowState(button, "offline", true, L("Sur ce Mac", "On this Mac"), L($"Sur ce Mac · {OfflineCopies.SizeText(copies.Sum(x => x.TotalSize))}. Clique pour le retirer", $"On this Mac · {OfflineCopies.SizeText(copies.Sum(x => x.TotalSize))}. Click to remove it"));
        else
        {
            var running = copies.FirstOrDefault(x => !x.Complete && Shell.CopyState(x.Item.Id) is { } state && state.Any(char.IsDigit));
            var label = _item.Type == "Series" ? $"{complete}/{targets.Count}" + (running is null ? "" : " · " + Shell.CopyState(running.Item.Id)) : Shell.CopyState(_item.Id) ?? "";
            ShowState(button, "offline", false, L($"Arrive sur ce Mac · {label}", $"Coming to this Mac · {label}"), L($"Arrive sur ce Mac · {label}. Clique pour l’arrêter", $"Coming to this Mac · {label}. Click to stop it"),
                targets.Sum(x => Shell.CopyDone(x.Id)) / targets.Count);
        }
    }
    private void OfflineClicked()
    {
        if (_offlineButton is not { } button) return;
        var targets = OfflineTargets(); if (targets.Count == 0) return;
        var copies = targets.Select(x => Shell.CopyOf(x.Id)).OfType<OfflineCopy>().ToList();
        if (copies.Count == 0) { _ = Shell.KeepOfflineAsync(targets); return; }
        var menu = new ContextMenu();
        void Add(string label, Action action) { var entry = new MenuItem { Header = label }; entry.Click += (_, _) => action(); menu.Items.Add(entry); }
        var missing = targets.Count - copies.Count;
        if (missing > 0) Add(L($"Garder {(missing == 1 ? "l’épisode qui manque" : $"les {missing} épisodes qui manquent")}", $"Keep {(missing == 1 ? "the missing episode" : $"the {missing} missing episodes")}"), () => _ = Shell.KeepOfflineAsync(targets));
        var paused = copies.Where(x => Shell.CopyPaused(x.Item.Id)).ToList();
        if (paused.Count > 0) Add(L("Reprendre", "Resume"), () => Shell.ResumeOffline(paused));
        var whole = _item.Type == "Series" ? L("la saison", "the season") : L("le film", "the movie");
        Add(copies.All(x => x.Complete) ? L($"Retirer {whole} de ce Mac · {OfflineCopies.SizeText(copies.Sum(x => x.TotalSize))}", $"Remove {whole} from this Mac · {OfflineCopies.SizeText(copies.Sum(x => x.TotalSize))}") : L("Arrêter et retirer de ce Mac", "Stop and remove from this Mac"), () => _ = Shell.ForgetOfflineAsync(copies));
        Ui.OpenUnder(menu, button);
    }

    /// <summary>
    /// « Bande-annonce » (<see cref="Trailers"/>): the trailer kept on the server in Mira's player, from Jellyfin alone;
    /// one on the web in the browser, never in Mira.
    /// </summary>
    private async Task TrailerAsync()
    {
        var item = _item;
        switch (Trailers.Source(item))
        {
            case TrailerSource.Jellyfin:
                if (Shell.Session is not { } session) return;
                try
                {
                    var trailers = await session.Client.LocalTrailersAsync(item.Id, session.Lifetime);
                    if (trailers.FirstOrDefault() is { } trailer) await Shell.PlayAsync(trailer);
                    else Shell.Notice(L("Jellyfin ne trouve plus la bande-annonce de ce titre.", "Jellyfin can no longer find this title’s trailer."));
                }
                catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
                break;
            case TrailerSource.Web when Trailers.WebLink(item) is { } link:
                Updates.Open(link);
                break;
        }
    }

    /// <summary>« Nouveaux épisodes »: for an administrator, when the extension follows series.</summary>
    private async Task LoadFollowAsync(Session session)
    {
        try
        {
            if (await session.Client.IsAdministratorAsync(session.Lifetime) != true) return;
            var follows = await session.Client.TorLinkFollowsAsync(session.Lifetime);
            if (!ReferenceEquals(session, Shell.Session)) return;
            _follow = follows.FirstOrDefault(x => string.Equals(x.SeriesId, _item.Id, StringComparison.OrdinalIgnoreCase));
            _followKnown = true;
            RenderTop();
        }
        // An extension without it, or a hiccup: the page is complete without the button.
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }

    /// <summary>Followed at once; once followed, « Chercher maintenant », the Downloads page, or « Ne plus suivre ».</summary>
    private void FollowMenu(Button button)
    {
        if (Shell.Session is not { } session) return;
        // Said when the page cannot show it (what following does, a search started); « Ne plus suivre » shows on the button.
        async Task Run(Func<Task> action, string? done)
        {
            try { await action(); if (done is not null) Shell.Notice(done); }
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); return; }
            await LoadFollowAsync(session);
        }
        if (_follow is not { } follow)
        {
            _ = Run(() => session.Client.TorLinkFollowAsync(_item.Id, _item.Name, null, session.Lifetime),
                L($"« {_item.Name} » suivie : le PC serveur cherche ses nouveaux épisodes toutes les 6 h. Chacun attend 10 min dans Téléchargements, le temps de l’annuler, puis rejoint ta bibliothèque.",
                    $"Following “{_item.Name}”: the server PC looks for its new episodes every 6 hours. Each one waits 10 min in Downloads, time to cancel it, then joins your library."));
            return;
        }
        var menu = new ContextMenu();
        void Add(string label, Action action) { var entry = new MenuItem { Header = label }; entry.Click += (_, _) => action(); menu.Items.Add(entry); }
        Add(L("Chercher maintenant", "Search now"), () => _ = Run(() => session.Client.TorLinkCheckFollowAsync(follow.Id, session.Lifetime), L($"Le PC serveur cherche les nouveaux épisodes de « {follow.Name} ».", $"The server PC is looking for new episodes of “{follow.Name}”.")));
        Add(L("Voir dans Téléchargements", "See in Downloads"), () => Shell.Go("downloads"));
        Add(L("Ne plus suivre", "Stop following"), () => _ = Run(() => session.Client.TorLinkUnfollowAsync(follow.Id, session.Lifetime), null));
        Ui.OpenUnder(menu, button);
    }
    /// <summary>An administrator of Jellyfin: « ⋮ » offers « Identifier… », « Actualiser les métadonnées » and, for a film, « Chercher une meilleure version ».</summary>
    private bool _admin;
    private async Task CheckAdministratorAsync(Session session)
    {
        try { if (await session.Client.IsAdministratorAsync(session.Lifetime) != true) return; }
        catch (Exception ex) when (Errors.Expected(ex)) { return; }
        if (!ReferenceEquals(session, Shell.Session) || _admin) return;
        _admin = true; RenderTop();
    }
    private void FixMenu(Button button)
    {
        if (Shell.Session is not { } session) return;
        var menu = new ContextMenu();
        var identify = new MenuItem { Header = L("Identifier…", "Identify…") };
        identify.Click += async (_, _) =>
        {
            var sheet = new IdentifySheet(session, _item);
            await sheet.ShowAsync(Shell);
            if (sheet.Said is { } said) Shell.Notice(said);
            if (sheet.Applied && ReferenceEquals(session, Shell.Session) && Shell.Current == this) { _backdrop.Source = null; _backdrop.Opacity = 0; _logo = null; await RefreshAsync(); }
        };
        var refresh = new MenuItem { Header = L("Actualiser les métadonnées", "Refresh metadata") };
        refresh.Click += async (_, _) =>
        {
            try
            {
                await session.Client.RefreshMetadataAsync(_item.Id, session.Lifetime);
                Shell.Notice(L($"Jellyfin relit « {_item.Name} » : la fiche se met à jour dans un moment.", $"Jellyfin is reading “{_item.Name}” again: the page updates in a moment."));
                // Queued by Jellyfin: a film takes a few seconds, a series longer.
                await Task.Delay(TimeSpan.FromSeconds(8), session.Lifetime);
                if (ReferenceEquals(session, Shell.Session) && Shell.Current == this) await RefreshAsync();
            }
            catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
        };
        menu.Items.Add(identify); menu.Items.Add(refresh);
        if (_item.Type == "Movie" && Shell.Offers("downloads"))
        {
            var better = new MenuItem { Header = L("Chercher une meilleure version", "Find a better version") };
            better.Click += (_, _) => Shell.FindBetter(_item);
            menu.Items.Add(better);
        }
        Ui.OpenUnder(menu, button);
    }
    /// <summary>« Lancer pour le groupe », « Reprendre pour le groupe à 42 min », « Regarder avec le groupe » (Together.GroupPlayLabel); null outside a group.</summary>
    private string? GroupPlayLabel(MediaItem target, string code) =>
        Shell.Group.Active ? Together.GroupPlayLabel(code, PlaybackSession.StartAt(target, remember: Shell.Settings.RememberPosition), Shell.Group.Queue?.ItemId == target.Id) : null;
    /// <summary>The group changed: the button says so.</summary>
    public void ShowTogether() => RenderTop();
    /// <summary>« Regarder ensemble »: the group this Mac is in and « Quitter », or the groups to join and « Créer un groupe ».</summary>
    private Task CreateGroupAsync() => Shell.Group.EnterAsync((client, ct) => client.SyncNewAsync(Together.GroupName(client.Connection.UserName), ct), invite: true, made: true);
    private async Task TogetherMenuAsync(Button button)
    {
        if (Shell.Session is not { } session) return;
        var watch = Shell.Group;
        var menu = new ContextMenu();
        void Add(string label, Action? action)
        {
            var entry = new MenuItem { Header = label, IsEnabled = action is not null };
            if (action is not null) entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        if (watch.Active && watch.Group is { } group)
        {
            Add(Together.GroupText(group) + (group.Participants.Count > 0 ? L(" : ", ": ") + string.Join(", ", group.Participants) : ""), null);
            Add(L("Inviter quelqu’un…", "Invite someone…"), () => _ = watch.InviteAsync());
            Add(L("Quitter le groupe", "Leave the group"), () => _ = watch.LeaveAsync());
        }
        else
        {
            List<Together.Group> groups;
            try { groups = await session.Client.SyncGroupsAsync(session.Lifetime); }
            catch (UnauthorizedAccessException) { Shell.Notice(WatchGroup.NoSyncPlay); return; }
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); return; }
            if (!ReferenceEquals(session, Shell.Session) || Shell.Current != this) return;
            // Nothing to join: a menu of one line would only stand in the way of the group.
            if (groups.Count == 0) { _ = CreateGroupAsync(); return; }
            foreach (var g in groups)
                Add(L($"Rejoindre « {g.GroupName} »", $"Join “{g.GroupName}”") + $" · {Together.People(Math.Max(1, g.Participants.Count))}", () => _ = watch.EnterAsync((client, ct) => client.SyncJoinAsync(g.GroupId, ct)));
            if (groups.Count > 0) menu.Items.Add(new Separator());
            Add(L("Créer un groupe et inviter…", "Create a group and invite…"), () => _ = CreateGroupAsync());
        }
        Ui.OpenUnder(menu, button);
    }

    /// <summary>The cast's row after the episodes and the facts, null without a cast.</summary>
    private CardRow? _cast;
    private int _castCount;
    private void RenderBody(Session session)
    {
        _body.Children.Clear();
        // A series' episodes first, right under its buttons; then the facts, two by two, and the cast in one row of
        // smaller photos, as on Windows (under the buttons, the cast stood in the way of the episodes).
        if (_item.Type == "Series") _body.Children.Add(EpisodeSection());
        var left = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 40, 0) };
        var right = new StackPanel { Spacing = 10 }; Grid.SetColumn(right, 1);
        // Each genre: Films or Séries (as the title is) filtered on it, as in Mira web.
        if (_item.Genres.Length > 0) left.Children.Add(Fact("GENRES", Links(_item.Genres.Select<string, (string, string, Action?)>(genre => (genre,
            L($"Les {(_item.Type == "Series" ? "séries" : "films")} « {genre} » de ta bibliothèque", $"The {genre} {(_item.Type == "Series" ? "shows" : "movies")} of your library"),
            () => Shell.OpenGenre(genre, _item.Type))))));
        if (_item.DirectorPeople.Length > 0) left.Children.Add(Fact(L("RÉALISATION", "DIRECTED BY"), People(_item.DirectorPeople, director: true)));
        var studios = _item.Studios.Where(x => x.Name.Trim().Length > 0).Take(3).ToList();
        if (studios.Count > 0) right.Children.Add(Fact("STUDIO", Links(studios.Select(studio => (studio.Name, L($"Les titres de {studio.Name} dans ta bibliothèque", $"Titles from {studio.Name} in your library"),
            studio.Id is { Length: > 0 } ? () => Shell.OpenStudio(studio) : (Action?)null)))));
        if (left.Children.Count + right.Children.Count > 0)
        {
            var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            facts.Children.Add(left); facts.Children.Add(right);
            _body.Children.Add(facts);
        }
        _cast = null; _castCount = 0;
        if (_item.CastPeople.Length > 0)
        {
            var cards = new List<Control>();
            foreach (var person in _item.CastPeople)
            {
                var photo = person.PrimaryImageTag is { } tag ? new Dictionary<string, string> { ["Primary"] = tag } : [];
                var known = person.Id is { Length: > 0 };
                var card = Cards.Person(new MediaItem { Id = person.Id ?? "", Name = person.Name, Type = "Person", ImageTags = photo }, known ? session.Images : null, _ => Shell.OpenPerson(person, false), person.Role, 72);
                if (known) ToolTip.SetTip(card, L($"Les titres avec {person.Name} dans ta bibliothèque", $"Titles with {person.Name} in your library"));
                else { card.IsHitTestVisible = false; card.Focusable = false; }
                cards.Add(card);
            }
            _castCount = cards.Count;
            _cast = new CardRow(L("Distribution", "Cast"), cards, sidePadding: 0, people: true);
            _body.Children.Add(_cast);
        }
    }
    /// <summary>« GENRES   Aventure · Animation »: a small label in its column, the names beside it.</summary>
    private static Control Fact(string title, Control content)
    {
        var row = new DockPanel();
        var label = new TextBlock { Text = title, Width = 110, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label); row.Children.Add(content);
        return row;
    }
    /// <summary>Names as links to their titles in the library; plain text for a person Jellyfin gave no id.</summary>
    private Control People(IEnumerable<PersonInfo> people, bool director) => Links(people.Select(person => (person.Name,
        director ? L($"Les titres réalisés par {person.Name}", $"Titles directed by {person.Name}") : L($"Les titres avec {person.Name}", $"Titles with {person.Name}"),
        person.Id is { Length: > 0 } ? () => Shell.OpenPerson(person, director) : (Action?)null)));
    /// <summary>Names as links (a person's, a studio's, a genre's titles), separated by « · » as on Windows; plain text when there is nothing to open.</summary>
    private static Control Links(IEnumerable<(string Name, string Tip, Action? Open)> links)
    {
        var panel = new WrapPanel { ItemSpacing = 4, LineSpacing = 6 };
        var list = links.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var (text, tip, open) = list[i]; var last = i == list.Count - 1;
            Control name;
            if (open is not null)
            {
                var link = new Button { Content = text, Classes = { "link" }, FontSize = 14, Foreground = Ui.Brush("#C7C7C7") };
                ToolTip.SetTip(link, tip);
                link.Click += (_, _) => open();
                name = link;
            }
            else name = new TextBlock { Text = text, FontSize = 14, Foreground = Ui.Brush("#C7C7C7") };
            panel.Children.Add(last ? name : Ui.Row(0, name, new TextBlock { Text = "·", FontSize = 14, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) }));
        }
        return panel;
    }

    private Control EpisodeSection()
    {
        FillSeasons();
        RenderEpisodes();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_seasons, Dock.Right); DockPanel.SetDock(_seasonMark, Dock.Right);
        // Kept from one refresh to the next (after the player, a title marked watched): each leaves the section it was in,
        // the heading too, or adding it again throws and the page is left without its episodes, nothing to scroll.
        foreach (var kept in new Control[] { _seasons, _seasonMark, _episodeHeading, _pills, _episodes }) (kept.Parent as Panel)?.Children.Remove(kept);
        header.Children.Add(_seasons); header.Children.Add(_seasonMark); header.Children.Add(_episodeHeading);
        return Ui.Column(14, header, _pills, _episodes);
    }
    /// <summary>
    /// The season list: the library's seasons (slices of a long one), then those it has none of (« Saison 1 · absente »);
    /// the page shown before a refresh, else the one of the episode found by a search, of the next episode, of an episode
    /// in progress, of the first unwatched.
    /// </summary>
    private void FillSeasons()
    {
        var previous = (_seasons.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
        var own = EpisodePages.From(_allEpisodes);
        var pages = EpisodeShelf.Pages(own, _allEpisodes, _virtuals, EpisodeCalendar.Today(), Shell.Settings.ShowMissingEpisodes);
        _filling = true;
        _seasons.Items.Clear();
        foreach (var page in pages) _seasons.Items.Add(new ComboBoxItem { Content = page.Label, Tag = page });
        // Two to ten pages: pills under the heading; more (slices of a long anime): the list; one: neither.
        var pills = EpisodeTiles.Pills(pages);
        _seasons.IsVisible = EpisodeTiles.SeasonList(pages);
        FillPills(pills ? pages : []);
        var resume = (previous is null ? null : pages.FirstOrDefault(x => x.Season == previous.Season && x.Skip == previous.Skip && x.Absent == previous.Absent))
            ?? EpisodePages.Holding(own, _allEpisodes, _allEpisodes.FirstOrDefault(x => x.Id == _found?.Id))
            ?? EpisodePages.Holding(own, _allEpisodes, SeriesTarget);
        _seasons.SelectedItem = _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, resume)) ?? _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _filling = false;
    }
    private void RenderEpisodes()
    {
        _episodes.Children.Clear(); _tiles.Clear(); _tileGrid = null;
        _missingViews.RemoveAll(x => !x.Pill);
        if (Shell.Session is not { } session) return;
        var page = (_seasons.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
        UpdatePills(page);
        var today = EpisodeCalendar.Today(); var showMissing = Shell.Settings.ShowMissingEpisodes;
        var grid = TileGrid();
        if (page is { Absent: true })
        {
            // A season the library has none of: a calm card for its aired episodes (their tiles on demand), then those coming.
            _episodeHeading.Text = L("Épisodes", "Episodes"); _seasonMark.IsVisible = false;
            foreach (var line in EpisodeShelf.Lines([], _virtuals, page.Season, true, true, today, showMissing))
            {
                if (line.Kind != ShelfKind.Missing) { AddTiles(session, grid, line, today); continue; }
                var key = "season-" + line.First.Id;
                _episodes.Children.Add(AbsentSeasonCard(page.Season, line.Episodes, today, key));
                if (_shelfOpen.Contains(key)) foreach (var episode in line.Episodes) grid.Children.Add(HollowTile(episode, today));
            }
            if (grid.Children.Count > 0) _episodes.Children.Add(grid);
            LayoutTiles();
            return;
        }
        var episodes = page is null ? [] : EpisodePages.Episodes(_allEpisodes, page);
        _episodeHeading.Text = L("Épisodes", "Episodes") + $"  ·  {(page is null ? 0 : _allEpisodes.Count(x => (x.ParentIndexNumber ?? 1) == page.Season))}";
        ShowSeasonMark(page);
        if (episodes.Count == 0) { _episodes.Children.Add(new TextBlock { Text = L("Aucun épisode disponible dans cette bibliothèque.", "No episodes available in this library."), Foreground = Ui.Muted }); return; }
        // The episode Lecture plays is in another season than the one shown (a long anime opened on its first): a click brings it.
        var pages = _seasons.Items.OfType<ComboBoxItem>().Select(x => x.Tag).OfType<EpisodePage>().ToList();
        if (_found is null && _next is { } next && EpisodePages.NextElsewhere(pages, _allEpisodes, page, next) is { } elsewhere) { var row = NextElsewhereRow(next, elsewhere); row.Margin = new Thickness(0, 0, 0, 16); _episodes.Children.Add(row); }
        // A grid of stills (EpisodeTiles); those the library lacks at their place (TheTVDB's, EpisodeShelf), hollow.
        var last = page!.Skip + page.Take >= _allEpisodes.Count(x => EpisodePages.Season(x) == page.Season);
        foreach (var line in EpisodeShelf.Lines(episodes, _virtuals, page.Season, page.Skip == 0, last, today, showMissing))
            AddTiles(session, grid, line, today);
        _episodes.Children.Add(grid);
        LayoutTiles();
    }
    private async Task LoadCalendarAsync(Session session)
    {
        List<MediaItem> found;
        try { found = (await session.Client.VirtualEpisodesAsync(session.Lifetime)).Where(x => x.SeriesId == _item.Id).ToList(); }
        // Optional, as the similar titles: the episodes say it when Jellyfin is out of reach.
        catch (Exception ex) when (Errors.Expected(ex)) { found = []; }
        if (found.Count == 0 && _virtuals.Count == 0) return;
        _virtuals = found;
        FillSeasons(); RenderEpisodes();
    }
    private static readonly IBrush ShelfBorder = Ui.Brush("#2B2B2B");
    // The tiles' panel: as wide as the page plus one gap, each tile followed by its gap.
    private WrapPanel TileGrid() => _tileGrid = new WrapPanel { Margin = new Thickness(0, 0, -EpisodeTiles.Gap, 0) };
    private void AddTiles(Session session, WrapPanel grid, ShelfLine line, DateOnly today)
    {
        switch (line.Kind)
        {
            case ShelfKind.Episode: grid.Children.Add(EpisodeTile(session, line.First)); break;
            case ShelfKind.Coming: grid.Children.Add(HollowTile(line.First, today)); break;
            default:
                grid.Children.Add(GapTile(line, today));
                if (_shelfOpen.Contains(line.First.Id)) foreach (var episode in line.Episodes) grid.Children.Add(HollowTile(episode, today));
                break;
        }
    }
    /// <summary>The tiles at the page's width: <see cref="EpisodeTiles.Columns"/> a row, pictures in 16:9.</summary>
    private void LayoutTiles()
    {
        var width = _episodes.Bounds.Width;
        if (width <= 0 || _tiles.Count == 0) return;
        _tileWidth = EpisodeTiles.Width(width);
        foreach (var (tile, picture) in _tiles) SizeTile(tile, picture);
    }
    private void SizeTile(Control tile, Control picture)
    {
        var width = _tileWidth > 0 ? _tileWidth : 300;
        tile.Width = width; picture.Width = width; picture.Height = Math.Round(width * 9 / 16);
    }
    private T Tile<T>(T tile, Control picture) where T : Control
    {
        tile.Margin = new Thickness(0, 0, EpisodeTiles.Gap, EpisodeTiles.RowGap); tile.VerticalAlignment = VerticalAlignment.Top;
        _tiles.Add((tile, picture)); SizeTile(tile, picture);
        return tile;
    }
    private static TextBlock TileTitle(MediaItem episode, string name, IBrush? ink = null)
    {
        var title = new TextBlock { FontSize = 15, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Margin = new Thickness(0, 11, 0, 0) };
        if (ink is not null) title.Foreground = ink;
        if (EpisodeTiles.Number(episode) is { Length: > 0 } number) title.Inlines!.Add(new Avalonia.Controls.Documents.Run(number + "  ") { FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted });
        title.Inlines!.Add(new Avalonia.Controls.Documents.Run(name));
        return title;
    }
    // An episode the library lacks: no picture to play, its name and its day (the next to come out, or one of an opened run).
    private StackPanel HollowTile(MediaItem episode, DateOnly today)
    {
        var picture = new Border { CornerRadius = new CornerRadius(10), Background = Ui.Brush("#0B0B0B"), BorderBrush = ShelfBorder, BorderThickness = new Thickness(1), Child = new Icon("calendar", 26) { Foreground = Ui.Faint } };
        var (_, when) = EpisodeShelf.Entry(episode, today);
        var tile = new StackPanel { Children = { picture, TileTitle(episode, EpisodeShelf.Name(episode), Ui.TextTertiary), new TextBlock { Text = when, FontSize = 13, Foreground = Ui.Muted, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 } } };
        Avalonia.Automation.AutomationProperties.SetName(tile, $"{EpisodeShelf.Code([episode])} {EpisodeShelf.Name(episode)}, {when}");
        return Tile(tile, picture);
    }
    // « É5 · Pas dans ta bibliothèque · Sorti le 8 nov. » or « É8–12 · À venir », the size of a picture; it opens its episodes.
    private Button GapTile(ShelfLine line, DateOnly today)
    {
        var key = line.First.Id; var open = _shelfOpen.Contains(key);
        var (first, second) = EpisodeTiles.GapLines(line, today);
        var copy = Ui.Column(0,
            new TextBlock { Text = EpisodeShelf.Code(line.Episodes), FontSize = 15.5, FontWeight = FontWeight.Bold, Foreground = Ui.TextTertiary },
            new TextBlock { Text = first, FontSize = 13, Foreground = Ui.Muted, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
            new TextBlock { Text = second, FontSize = 13, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
            Ui.Row(5, new TextBlock { Text = EpisodeShelf.Show(line.Episodes.Count, open), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#C7C7CC"), VerticalAlignment = VerticalAlignment.Center }, new Icon(open ? "up" : "down", 12) { Foreground = Ui.Brush("#C7C7CC") }));
        var show = (Control)copy.Children[^1];
        show.Margin = new Thickness(0, 10, 0, 0);
        copy.VerticalAlignment = VerticalAlignment.Center; copy.Margin = new Thickness(20, 0);
        // « Télécharger É6–9 » (DetailPage.Missing): its part above « Voir », its progress bar along the tile's foot.
        var inside = new Grid { Children = { copy } };
        if (MissingTilePart(line) is var (part, bar)) { show.Margin = new Thickness(0, 8, 0, 0); copy.Children.Insert(copy.Children.IndexOf(show), part); inside.Children.Add(bar); }
        var picture = new Border { CornerRadius = new CornerRadius(10), BorderBrush = ShelfBorder, BorderThickness = new Thickness(1), ClipToBounds = true, Child = inside };
        var button = new Button { Content = picture, Classes = { "card" }, VerticalContentAlignment = VerticalAlignment.Top };
        var text = line.Kind == ShelfKind.Later ? EpisodeShelf.LaterText(line.Episodes, today) : EpisodeShelf.MissingText(line.Episodes, today);
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        Avalonia.Automation.AutomationProperties.SetHelpText(button, EpisodeShelf.Show(line.Episodes.Count, open));
        button.Click += (_, _) => Toggle(key);
        return Tile(button, picture);
    }
    // « La saison 1 n’est pas dans ta bibliothèque », how many and when, where they come from; its episodes on demand.
    private Border AbsentSeasonCard(int season, IReadOnlyList<MediaItem> run, DateOnly today, string key)
    {
        var open = _shelfOpen.Contains(key);
        var label = EpisodeShelf.ShowSeason(run.Count, open);
        var show = new Button { Content = Ui.Row(6, new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, new Icon(open ? "up" : "down", 12)), Classes = { "link" }, FontSize = 13.5, Foreground = Ui.Brush("#C7C7CC"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetName(show, label);
        show.Click += (_, _) => Toggle(key);
        var copy = Ui.Column(4,
            new TextBlock { Text = EpisodeShelf.SeasonTitle(season), FontSize = 15.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#E0E0E3"), TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = EpisodeShelf.SeasonText(run, today), FontSize = 13.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 20 },
            show);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(new Icon("calendar", 26) { Margin = new Thickness(0, 2, 18, 0), VerticalAlignment = VerticalAlignment.Top, Foreground = Ui.Faint });
        Grid.SetColumn(copy, 1); grid.Children.Add(copy);
        if (MissingSeasonPart(season, run) is { } get) { Grid.SetColumn(get, 2); grid.Children.Add(get); }
        return new Border { Child = grid, BorderBrush = ShelfBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, EpisodeTiles.RowGap) };
    }
    /// <summary>The seasons as pills under the heading; a click shows that one (the hidden list stays the source).</summary>
    private void FillPills(IReadOnlyList<EpisodePage> pages)
    {
        _pills.Children.Clear(); _pills.IsVisible = pages.Count > 0;
        _missingViews.RemoveAll(x => x.Pill);
        foreach (var page in pages)
        {
            var (name, note) = EpisodeTiles.Pill(page);
            var label = Ui.Row(7, new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (note is not null) label.Children.Add(new TextBlock { Text = note, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.62, VerticalAlignment = VerticalAlignment.Center });
            if (MissingPillNote(page) is { } coming) label.Children.Add(coming);
            var pill = new Button { Content = label, Classes = { "chip" }, Padding = new Thickness(16, 8), CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 8, 8), Tag = page };
            Avalonia.Automation.AutomationProperties.SetName(pill, page.Label);
            pill.Click += (_, _) => _seasons.SelectedItem = _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, page)) ?? _seasons.SelectedItem;
            _pills.Children.Add(pill);
        }
    }
    private void UpdatePills(EpisodePage? shown)
    {
        foreach (var pill in _pills.Children.OfType<Button>()) pill.Classes.Set("on", ReferenceEquals(pill.Tag, shown));
    }
    private void Toggle(string key)
    {
        if (!_shelfOpen.Remove(key)) _shelfOpen.Add(key);
        RenderEpisodes();
    }
    /// <summary>« À suivre : S2 É1 · Le titre », above the episodes of another season; « Saison 2 › » shows its own.</summary>
    private Button NextElsewhereRow(MediaItem next, EpisodePage elsewhere)
    {
        var code = $"S{next.ParentIndexNumber ?? 1} {L("É", "E")}{next.IndexNumber ?? 1}";
        var text = new TextBlock { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("#C9C9CF") };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(L("À suivre : ", "Up next: ")));
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(code) { FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
        if (next.Name.Length > 0) text.Inlines.Add(new Avalonia.Controls.Documents.Run("  ·  " + next.Name));
        var go = Ui.Row(6, new TextBlock { Text = elsewhere.Label, FontSize = 13, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center }, new Icon("next", 14) { Foreground = Ui.Muted });
        go.VerticalAlignment = VerticalAlignment.Center; go.Margin = new Thickness(16, 0, 0, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var play = new Icon("play", 18) { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(play); Grid.SetColumn(text, 1); grid.Children.Add(text); Grid.SetColumn(go, 2); grid.Children.Add(go);
        var button = new Button { Content = grid, Classes = { "row" }, Background = Ui.Brush("#191919"), Padding = new Thickness(16, 12) };
        Avalonia.Automation.AutomationProperties.SetName(button, L($"À suivre : {code} {next.Name}, afficher {elsewhere.Label}", $"Up next: {code} {next.Name}, show {elsewhere.Label}"));
        button.Click += (_, _) => _seasons.SelectedItem = _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, elsewhere)) ?? _seasons.SelectedItem;
        return button;
    }
    /// <summary>« Marquer la saison comme vue », or « non vue » once all its episodes are: only beside another season.</summary>
    private void ShowSeasonMark(EpisodePage? page)
    {
        _seasonMark.IsVisible = page is not null && EpisodePages.MarksSeasons(_allEpisodes);
        if (page is null || !_seasonMark.IsVisible) return;
        var played = EpisodePages.SeasonMark(_allEpisodes, page.Season).Played;
        var label = played ? L("Marquer la saison comme non vue", "Mark season as unwatched") : L("Marquer la saison comme vue", "Mark season as watched");
        _seasonMark.Content = Ui.Row(8, new Icon(played ? "refresh" : "check", 16), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Avalonia.Automation.AutomationProperties.SetName(_seasonMark, label);
        _seasonMark.Tag = (page.Season, !played); _seasonMark.IsEnabled = !_marking;
    }
    /// <summary>A whole season watched or not: Jellyfin is told (the season in one call, else each episode), then the page follows it.</summary>
    private async Task MarkSeasonAsync()
    {
        if (_marking || _seasonMark.Tag is not (int season, bool played) || Shell.Session is not { } session) return;
        _marking = true; _seasonMark.IsEnabled = false;
        try
        {
            foreach (var id in EpisodePages.SeasonMark(_allEpisodes, season).Ids) await session.Client.SetPlayedAsync(id, played);
            var episodes = _allEpisodes.Where(x => EpisodePages.Season(x) == season).ToList();
            await Task.Run(() => { foreach (var episode in episodes) session.Store.ForgetProgress(episode.Id); });
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
        finally { _marking = false; _seasonMark.IsEnabled = true; }
        await Shell.RefreshPagesAsync();
    }
    private Button EpisodeTile(Session session, MediaItem episode)
    {
        var isNext = _next?.Id == episode.Id;
        // « Cacher les spoilers »: an episode not started shows Jellyfin's BlurHash of its still, the still never fetched.
        var hidden = Spoilers.Hides(episode, Shell.Settings.HideSpoilers);
        var image = new Image { Stretch = Stretch.UniformToFill };
        if (hidden) image.Source = BlurHash.Of(episode, Spoilers.Still(episode)) is { } hash ? Cards.Blur(hash, 16 / 9d) : null;
        var still = new Grid();
        still.Children.Add(new Border { Background = Ui.Brush("#232323") });
        still.Children.Add(image);
        var progress = episode.UserData.Played ? 1 : episode.Resumable ? episode.Progress : 0;
        if (progress > 0)
        {
            var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Ui.Brush("#38FFFFFF"), ColumnDefinitions = new ColumnDefinitions($"{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}*,{(1 - progress).ToString(System.Globalization.CultureInfo.InvariantCulture)}*") };
            bar.Children.Add(new Border { Background = Ui.Ink });
            still.Children.Add(bar);
        }
        if (episode.UserData.Played)
            still.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Ui.Brush("#A0000000"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0), Child = new Icon("check", 13) { Foreground = Brushes.White } });
        // Play: always on the one to watch, on the others under the pointer.
        var play = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Ui.Brush("#80000000"), Child = new Icon("play", 20) { Foreground = Brushes.White, Margin = new Thickness(3, 0, 0, 0) }, Opacity = isNext ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        still.Children.Add(play);
        if (isNext)
        {
            still.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6), Background = Ui.Ink, Padding = new Thickness(9, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 10, 0, 0),
                Child = new TextBlock { Text = EpisodeTiles.UpNext, FontSize = 11, FontWeight = FontWeight.ExtraBold, Foreground = Ui.Brush("#0B0B0B") }
            });
            still.Children.Add(new Border { BorderBrush = Ui.Ink, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10), IsHitTestVisible = false });
        }
        var picture = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = still };
        var root = new StackPanel { Background = Brushes.Transparent, Children = { picture, TileTitle(episode, episode.Name) } };
        root.Children.Add(new TextBlock { Text = EpisodeTiles.Meta(episode, EpisodeCalendar.Today()), FontSize = 13, Foreground = Ui.Muted, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var overview = hidden ? Spoilers.HiddenSummary : OverviewText.Plain(episode.Overview, flat: true);
        if (overview.Length > 0)
            root.Children.Add(new TextBlock { Text = overview, FontSize = 13, FontStyle = hidden ? FontStyle.Italic : FontStyle.Normal, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis, LineHeight = 19, Margin = new Thickness(0, 6, 0, 0) });
        // « Sur ce Mac », « 42 % », « En attente »: a copy kept on this Mac.
        if (Shell.CopyState(episode.Id) is { } kept) root.Children.Add(new TextBlock { Text = kept, FontSize = 12, Foreground = Ui.Brush("#DADAE0"), Margin = new Thickness(0, 6, 0, 0) });
        var button = new Button { Content = root, Classes = { "card" }, VerticalContentAlignment = VerticalAlignment.Top };
        if (!isNext) { button.PointerEntered += (_, _) => play.Opacity = 1; button.PointerExited += (_, _) => play.Opacity = 0; }
        button.Click += (_, _) => _ = Shell.PlayAsync(episode);
        button.ContextMenu = Shell.Menu(episode);
        Avalonia.Automation.AutomationProperties.SetName(button, $"{L("Épisode", "Episode")} {episode.IndexNumber}, {episode.Name}, {episode.DurationLabel}{(episode.UserData.Played ? L(", vu", ", watched") : isNext ? L(", à suivre", ", up next") : "")}");
        if (!hidden) Ui.LoadWhenVisible(button, image, () => session.Images.EpisodeAsync(episode, 420));
        return Tile(button, picture);
    }

    /// <summary>The film's saga, then the similar titles: a film of the saga is not shown again among them.</summary>
    private async Task LoadSimilarAsync(Session session)
    {
        Saga? saga = null;
        if (_item.Type == "Movie")
        {
            try { saga = await session.SagaAsync(_item.Id); }
            catch (Exception ex) when (Errors.Expected(ex)) { }
        }
        // Bonus videos (making-of, deleted scenes, textless credits): played from their card, they have no page.
        var extras = new List<MediaItem>();
        if (_item.SpecialFeatureCount is > 0)
        {
            try { extras = await session.Client.ExtrasAsync(_item.Id); }
            catch (Exception ex) when (Errors.Expected(ex)) { }
        }
        // « Du même studio »: its studio's other titles, but those of its saga.
        (StudioInfo Studio, List<MediaItem> Items)? studio = null;
        try { studio = await session.Client.StudioTitlesAsync(_item, saga?.Items.Select(x => x.Id).ToHashSet() ?? []); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        var similar = new List<MediaItem>();
        try { similar = (await session.SimilarAsync(_item.Id)).Items.Where(x => x.Id != _item.Id && saga?.Items.Any(s => s.Id == x.Id) != true).ToList(); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        if (saga is null && studio is null && extras.Count == 0 && similar.Count == 0) return;
        try { await Task.Run(() => { if (saga is not null) session.Store.ApplyLocalProgress(saga.Items); if (studio is { } found) session.Store.ApplyLocalProgress(found.Items); session.Store.ApplyLocalProgress(similar); }); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        foreach (var old in _body.Children.OfType<CardRow>().Where(x => x != _cast).ToList()) _body.Children.Remove(old);
        CardRow Row(string title, List<MediaItem> items) => new(title, items.Select(x => { var card = Cards.Poster(x, session.Images, Shell.Open, Shell.Menu); card.Width = 168; return (Control)card; }), sidePadding: 0);
        if (saga is not null) _body.Children.Add(Row(saga.Name, saga.Items));
        if (studio is { } titles) _body.Children.Add(Row(L($"Du même studio · {titles.Studio.Name}", $"More from {titles.Studio.Name}"), titles.Items));
        if (extras.Count > 0) _body.Children.Add(new CardRow(L("Bonus", "Extras"), extras.Select(x => (Control)Cards.Wide(x, session.Images, extra => _ = Shell.PlayAsync(extra))), sidePadding: 0));
        if (similar.Count > 0) _body.Children.Add(Row(L("Titres similaires", "More like this"), similar));
    }
}
