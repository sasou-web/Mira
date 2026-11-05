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
public sealed class DetailPage : Page
{
    private MediaItem _item;
    private readonly Image _backdrop = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private readonly Grid _header = new() { ClipToBounds = true, Height = 600 };
    /// <summary>What sits on the picture, at its bottom left: kind, logo or name, catchphrase, facts, summary, buttons.</summary>
    private readonly StackPanel _top = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(56 + MainWindow.RailWidth, 0, 56, 56) };
    private readonly StackPanel _body = new() { Spacing = 36, Margin = new Thickness(56, 8, 56, 64) };
    private readonly ComboBox _seasons = new() { MinWidth = 200 };
    private readonly StackPanel _episodes = new() { Spacing = 10 };
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
    /// <summary>
    /// Missing episodes and coming ones (TheTVDB's plugin on Jellyfin, <see cref="TvdbExtension"/>): one line above the
    /// episodes, opened into their list, as Mira web and Windows (<see cref="EpisodeCalendar"/>).
    /// </summary>
    private readonly StackPanel _calendar = new() { Spacing = 8 };
    private (List<MediaItem> Missing, List<MediaItem> Coming) _schedule = ([], []);
    private bool _calendarOpen;
    public override string Rail => "";
    public MediaItem Item => _item;
    public IReadOnlyList<MediaItem> Episodes => _allEpisodes;
    /// <summary>For the self-check: the list of episodes on the page, as many rows as the season shown.</summary>
    public bool EpisodesShown => TopLevel.GetTopLevel(_episodes) is not null && _episodes.Children.Count > 0;
    public IEnumerable<string> SeasonLabels => _seasons.Items.OfType<ComboBoxItem>().Select(x => x.Content as string ?? "");

    public DetailPage(MainWindow shell, MediaItem item, MediaItem? atEpisode = null) : base(shell, underRail: true)
    {
        _item = item; _found = atEpisode;
        _backdrop.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(380) }];
        _header.Children.Add(new Border { Background = Ui.Brush("#0E0E10") });
        _header.Children.Add(_backdrop);
        // Shaded from the left and the bottom, where the text and the buttons sit, as on Windows.
        _header.Children.Add(new Border { Background = Gradient(0, 0.5, 1, 0.5, ("#F0070708", 0), ("#C4070708", 0.3), ("#40070708", 0.62), ("#00070708", 0.86)) });
        _header.Children.Add(new Border { Background = Gradient(0.5, 0, 0.5, 1, ("#66070708", 0), ("#00070708", 0.2), ("#00070708", 0.5), ("#A6070708", 0.8), ("#FF070708", 1)) });
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
        if (_item.Type == "Series") { _ = LoadFollowAsync(session); _ = LoadCalendarAsync(session); }
        if (!_admin) _ = CheckAdministratorAsync(session);
        if (_item.Type == "Series" && SeriesTarget is { } target && _targetFile?.Id != target.Id) await LoadTargetFileAsync(session, target);
    }
    private static string FavoriteLabel(bool on) => on ? L("Favori", "Favorite") : L("Ajouter aux favoris", "Add to favorites");
    private static string FavoriteTip(bool on) => on ? L("Dans tes favoris. Clique pour le retirer", "In your favorites. Click to remove it") : FavoriteLabel(false);
    private string WatchedLabel(bool on) => on ? _item.Type == "Series" ? L("Vue", "Watched") : L("Vu", "Watched")
        : _item.Type == "Series" ? L("Marquer la série comme vue", "Mark show as watched") : L("Marquer comme vu", "Mark as watched");
    private string WatchedTip(bool on) => on ? L("Vu. Clique pour le marquer comme non vu", "Watched. Click to mark it as unwatched") : WatchedLabel(false);
    /// <summary>
    /// A button of the page on or off, each state seen at a glance, as on Windows: on, a white outline and its words kept
    /// beside its icon (« Favori », « Suivie »); off, the plain button, its icon alone, its words in its tooltip.
    /// </summary>
    /// <param name="tip">Its tooltip, and what VoiceOver says of it: what it is, and when on what a click does.</param>
    private static void ShowState(Button button, string icon, string label, bool on, string tip, bool keep = true)
    {
        if (button.Content is StackPanel { Children: [Icon glyph, TextBlock text, ..] }) { glyph.Kind = icon; text.Text = label; }
        button.Classes.Set("lit", on);
        if (keep) ActionLine.SetKeep(button, on);
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, on ? tip : label);
        Avalonia.Automation.AutomationProperties.SetHelpText(button, !on && tip != label ? tip : null);
    }
    /// <summary>A button of the line: its icon, its words beside it (hidden while it is round), and a live dot when on.</summary>
    private static Button LineButton(string icon, string label, Action click, bool dot = false)
    {
        var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 9 };
        content.Children.Add(new Icon(icon, 18) { VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        // A green dot: on and live (a group, a series followed).
        if (dot) content.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = Ui.Brush("#86C6A2"), Margin = new Thickness(1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Classes = { "state" }, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }
    private bool _favoriteBusy, _watchedBusy;
    private async Task FlipFavoriteAsync(Button button)
    {
        if (_favoriteBusy) return; _favoriteBusy = true;
        var item = _item; var want = !item.UserData.IsFavorite;
        try
        {
            ShowState(button, want ? "heart-filled" : "heart", FavoriteLabel(want), want, FavoriteTip(want));
            await Shell.SetFavoriteAsync(item, want);
            if (item.UserData.IsFavorite != want && ReferenceEquals(item, _item)) RenderTop();
        }
        finally { _favoriteBusy = false; }
    }
    private async Task FlipWatchedAsync(Button button)
    {
        if (_watchedBusy) return; _watchedBusy = true;
        var item = _item; var want = !item.UserData.Played;
        try
        {
            ShowState(button, "check", WatchedLabel(want), want, WatchedTip(want));
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
            _targetFile = (episode.Id, full.MediaSources?.FirstOrDefault()); RenderTop();
        }
        // Only a detail of the page: without it, the rest is complete.
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    private async Task LoadArtAsync(Session session)
    {
        var art = await session.Images.BackdropAsync(_item, 1920);
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
    private MediaItem? SeriesTarget => _next ?? _allEpisodes.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played) ?? _allEpisodes.FirstOrDefault(x => !x.UserData.Played) ?? _allEpisodes.FirstOrDefault();

    /// <summary>Lecture picks up where it stopped: started, not finished, and Mira set to remember.</summary>
    private bool Resumes(MediaItem item) => item.Progress > 0 && !item.UserData.Played && Shell.Settings.RememberPosition;

    private void RenderTop()
    {
        _top.Children.Clear();
        var kind = Ui.Kind(_item).ToUpperInvariant();
        _top.Children.Add(new TextBlock { Text = _item.ProductionYear is { } y ? $"{kind}  ·  {y}" : kind, FontSize = 11.5, FontWeight = FontWeight.Bold, Foreground = Ui.Brush("#E0E0E0"), Margin = new Thickness(1, 0, 0, 14) });
        _top.Children.Add(Title());
        if (_item.Taglines?.Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) is { } tagline)
            _top.Children.Add(new TextBlock { Text = tagline, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#E0E0E0"), TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 12, 0, 0) });
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
        _top.Children.Add(new TextBlock { Text = string.Join("  ·  ", meta), FontSize = 14.5, Foreground = Ui.Brush("#C7C7C7"), Margin = new Thickness(1, 16, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        // The summary on three lines over the picture, « Lire la suite » for the rest.
        var summary = OverviewText.Plain(string.IsNullOrWhiteSpace(_item.Overview) ? L("Aucun résumé disponible pour ce titre.", "No summary available for this title.") : _item.Overview);
        var overview = new TextBlock { Text = summary, FontSize = 15, LineHeight = 24, Foreground = Ui.Brush("#C7C7C7"), TextWrapping = TextWrapping.Wrap, MaxWidth = 660, HorizontalAlignment = HorizontalAlignment.Left, MaxLines = _overviewOpen ? 0 : 3, TextTrimming = TextTrimming.WordEllipsis, Margin = new Thickness(1, 16, 0, 0) };
        _top.Children.Add(overview);
        // Three lines of the summary's width hold about 300 characters: a longer one gets « Lire la suite ».
        if (summary.Length > 260)
        {
            string MoreText() => _overviewOpen ? L("Réduire", "Show less") : L("Lire la suite", "Read more");
            var more = new Button { Classes = { "quiet" }, Content = MoreText(), Padding = new Thickness(0, 4), Margin = new Thickness(1, 4, 0, 0), FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Left };
            more.Click += (_, _) => { _overviewOpen = !_overviewOpen; overview.MaxLines = _overviewOpen ? 0 : 3; more.Content = MoreText(); };
            _top.Children.Add(more);
        }
        _top.Children.Add(Actions());
        // How far the film was watched, under its buttons and as wide as the page, as on Windows.
        if (_item.Type != "Series" && Resumes(_item))
            _top.Children.Add(new ProgressLine { Value = _item.Progress, Height = 5, Margin = new Thickness(2, 18, 0, 0) });
        SizeHero();
    }

    /// <summary>
    /// Lecture, then « Depuis le début », « Regarder ensemble », Favori, Vu, « Suivre… », « Garder sur ce Mac » and « ⋮ »,
    /// in Windows' order: Lecture and « Regarder ensemble » with their words, the others round, their words in their tooltip
    /// but once on (« Favori », « Vu », « Suivie »).
    /// </summary>
    private ActionLine Actions()
    {
        var line = new ActionLine { Margin = new Thickness(0, 22, 0, 0) };
        var inGroup = Shell.Group.Active;
        MediaItem? restart = null;
        Button play;
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
            play = new Button { Content = Ui.Row(9, new Icon("play", 18) { VerticalAlignment = VerticalAlignment.Center }, text), Classes = { "primary" }, Height = ActionLine.Round, Padding = new Thickness(25, 0), VerticalContentAlignment = VerticalAlignment.Center };
            play.Click += (_, _) => { if (SeriesTarget is { } episode) _ = Shell.PlayAsync(episode); else _ = Shell.PlayAsync(_item); };
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
            play = new Button { Content = content, Classes = { "primary" }, Padding = new Thickness(32, 15, 36, 15), MinWidth = 250, VerticalContentAlignment = VerticalAlignment.Center };
            play.Click += (_, _) => _ = Shell.PlayAsync(_item);
            Avalonia.Automation.AutomationProperties.SetName(play, detail is null ? label : $"{label}, {detail}");
            if (resume) restart = _item;
        }
        play.CornerRadius = ActionLine.Corners; play.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(play);
        if (restart is not null)
        {
            var from = restart;
            var button = LineButton("refresh", L("Depuis le début", "From the beginning"), () => _ = Shell.PlayAsync(from, fromStart: true));
            ShowState(button, "refresh", L("Depuis le début", "From the beginning"), false, L("Depuis le début", "From the beginning"), keep: false);
            line.Children.Add(button);
        }
        // « Regarder ensemble » keeps its words beside Lecture; in a group, its count and a live dot, outlined.
        var group = inGroup ? Shell.Group.Group : null;
        var togetherLabel = group is null ? L("Regarder ensemble", "Watch together") : L("En groupe · ", "In a group · ") + Together.People(Math.Max(1, group.Participants.Count));
        Button? together = null;
        together = LineButton("users", togetherLabel, () => _ = TogetherMenuAsync(together!), dot: group is not null);
        ShowState(together, "users", togetherLabel, group is not null, group is null
            ? L("Regarder ce titre au même moment que d’autres comptes de ce Jellyfin, chacun chez soi", "Watch this title at the same moment as other accounts on this Jellyfin, each at home")
            : L($"En groupe · {Together.GroupText(group)}. Clique pour inviter ou quitter le groupe", $"In a group · {Together.GroupText(group)}. Click to invite or leave the group"), keep: false);
        ActionLine.SetKeep(together, true); ActionLine.SetShrink(together, 5);
        line.Children.Add(together);
        var isFavorite = _item.UserData.IsFavorite;
        Button? favorite = null;
        favorite = LineButton(isFavorite ? "heart-filled" : "heart", FavoriteLabel(isFavorite), () => _ = FlipFavoriteAsync(favorite!));
        ShowState(favorite, isFavorite ? "heart-filled" : "heart", FavoriteLabel(isFavorite), isFavorite, FavoriteTip(isFavorite));
        ActionLine.SetShrink(favorite, 1);
        line.Children.Add(favorite);
        var isPlayed = _item.UserData.Played;
        Button? watched = null;
        watched = LineButton("check", WatchedLabel(isPlayed), () => _ = FlipWatchedAsync(watched!));
        ShowState(watched, "check", WatchedLabel(isPlayed), isPlayed, WatchedTip(isPlayed));
        ActionLine.SetShrink(watched, 2);
        line.Children.Add(watched);
        if (_followKnown)
        {
            // « Suivre… » off, « Suivie » on (outlined, a live dot): the state of the series seen at a glance.
            var label = _follow is null ? L("Suivre…", "Follow…") : L("Suivie", "Following");
            Button? follow = null;
            follow = LineButton("calendar", label, () => FollowMenu(follow!), dot: _follow is not null);
            ShowState(follow, "calendar", label, _follow is not null, _follow is null
                ? L("Suivre les nouveaux épisodes : le PC serveur les cherche et les télécharge tout seul, avec TorLink", "Follow new episodes: the server PC looks for them and downloads them on its own, with TorLink")
                : string.Join(" · ", new[] { L("Suivie", "Following"), RemoteLines.FollowNote(_follow.Note), RemoteLines.Follow(_follow, DateTimeOffset.UtcNow) }.Where(x => x.Length > 0)));
            ActionLine.SetShrink(follow, 4);
            line.Children.Add(follow);
        }
        _offlineButton = new Button { Classes = { "state" }, VerticalAlignment = VerticalAlignment.Center };
        _offlineButton.Click += (_, _) => OfflineClicked();
        line.Children.Add(_offlineButton); ShowOffline();
        if (_admin && _item.Type is "Movie" or "Series")
        {
            // « ⋮ » as on Windows: what an administrator rarely needs stays out of the row of buttons.
            Button? fix = null;
            fix = Ui.IconButton("more", L("Identifier le titre, actualiser ses informations", "Identify the title, refresh its details"), () => FixMenu(fix!), "state", 18, L("Plus d’actions sur ce titre", "More actions for this title"));
            fix.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(fix);
        }
        return line;
    }
    /// <summary>For the self-check: the buttons beside Lecture, and whether each shows its words.</summary>
    public IEnumerable<(string Name, bool Words, bool On)> ActionButtons() => _top.Children.OfType<ActionLine>().SelectMany(x => x.Children.OfType<Button>().Skip(1))
        .Where(x => x.IsVisible).Select(x => (Avalonia.Automation.AutomationProperties.GetName(x) ?? "", ActionLine.ShowsWords(x), x.Classes.Contains("lit")));
    /// <summary>For the self-check: the picture's height, and the cast shown under it.</summary>
    public double HeroHeight => _header.Bounds.Height;
    public int CastShown => _cast?.Children.Count ?? 0;
    public void ShowCastForCheck() => (_cast?.Parent as Control)?.BringIntoView();

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
        string icon, label, tip;
        if (copies.Count == 0)
        {
            (icon, label) = ("download", "");
            tip = _item.Type == "Series" ? L($"Garder la saison {SeasonShown()} sur ce Mac ({Lang.Count(targets.Count, "épisode", "épisodes", "episode", "episodes")}), pour la regarder sans connexion", $"Keep season {SeasonShown()} on this Mac ({Lang.Count(targets.Count, "épisode", "épisodes", "episode", "episodes")}), to watch it offline") : L("Garder ce film sur ce Mac, pour le regarder sans connexion", "Keep this movie on this Mac, to watch it offline");
        }
        else if (complete == targets.Count) { (icon, label) = ("check", L("Sur ce Mac", "On this Mac")); tip = L($"Sur ce Mac · {OfflineCopies.SizeText(copies.Sum(x => x.Size))}. Clique pour le retirer", $"On this Mac · {OfflineCopies.SizeText(copies.Sum(x => x.Size))}. Click to remove it"); }
        else
        {
            icon = "download";
            var running = copies.FirstOrDefault(x => !x.Complete && Shell.CopyState(x.Item.Id) is { } state && state.Any(char.IsDigit));
            label = _item.Type == "Series" ? $"{complete}/{targets.Count}" + (running is null ? "" : " · " + Shell.CopyState(running.Item.Id)) : Shell.CopyState(_item.Id) ?? "";
            tip = L("Arrive sur ce Mac. Clique pour l’arrêter", "Coming to this Mac. Click to stop it");
        }
        var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 9 };
        content.Children.Add(new Icon(icon, 18) { VerticalAlignment = VerticalAlignment.Center });
        if (label.Length > 0) content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, label.Length > 0 ? label + ". " + tip : tip);
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
        Add(copies.All(x => x.Complete) ? L($"Retirer {whole} de ce Mac · {OfflineCopies.SizeText(copies.Sum(x => x.Size))}", $"Remove {whole} from this Mac · {OfflineCopies.SizeText(copies.Sum(x => x.Size))}") : L("Arrêter et retirer de ce Mac", "Stop and remove from this Mac"), () => _ = Shell.ForgetOfflineAsync(copies));
        Ui.OpenUnder(menu, button);
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

    /// <summary>The cast's cards under the picture, null without a cast.</summary>
    private WrapPanel? _cast;
    private void RenderBody(Session session)
    {
        _body.Children.Clear();
        // The cast with their photos, each one their titles in the library; then the facts, two by two, as on Windows.
        _cast = null;
        if (_item.CastPeople.Length > 0)
        {
            _cast = new WrapPanel();
            foreach (var person in _item.CastPeople)
            {
                var photo = person.PrimaryImageTag is { } tag ? new Dictionary<string, string> { ["Primary"] = tag } : [];
                var known = person.Id is { Length: > 0 };
                var card = Cards.Person(new MediaItem { Id = person.Id ?? "", Name = person.Name, Type = "Person", ImageTags = photo }, known ? session.Images : null, _ => Shell.OpenPerson(person, false), person.Role);
                card.Margin = new Thickness(0, 0, 18, 22);
                if (known) ToolTip.SetTip(card, L($"Les titres avec {person.Name} dans ta bibliothèque", $"Titles with {person.Name} in your library"));
                else { card.IsHitTestVisible = false; card.Focusable = false; }
                _cast.Children.Add(card);
            }
            _body.Children.Add(Ui.Column(18, new TextBlock { Text = L("Distribution", "Cast"), FontSize = 24, FontWeight = FontWeight.SemiBold }, _cast));
        }
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
        if (_item.Type == "Series") _body.Children.Add(EpisodeSection());
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
        var previous = (_seasons.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
        var pages = EpisodePages.From(_allEpisodes);
        _filling = true;
        _seasons.Items.Clear();
        foreach (var page in pages) _seasons.Items.Add(new ComboBoxItem { Content = page.Label, Tag = page });
        // The page shown before a refresh, else the one of the episode found by a search, of the next episode, of an
        // episode in progress, of the first unwatched.
        var resume = (previous is null ? null : pages.FirstOrDefault(x => x.Season == previous.Season && x.Skip == previous.Skip))
            ?? EpisodePages.Holding(pages, _allEpisodes, _allEpisodes.FirstOrDefault(x => x.Id == _found?.Id))
            ?? EpisodePages.Holding(pages, _allEpisodes, SeriesTarget);
        _seasons.SelectedItem = _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, resume)) ?? _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _filling = false;
        RenderEpisodes();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_seasons, Dock.Right); DockPanel.SetDock(_seasonMark, Dock.Right);
        // Kept from one refresh to the next (after the player, a title marked watched): each leaves the section it was in,
        // the heading too, or adding it again throws and the page is left without its episodes, nothing to scroll.
        foreach (var kept in new Control[] { _seasons, _seasonMark, _episodeHeading, _episodes, _calendar }) (kept.Parent as Panel)?.Children.Remove(kept);
        header.Children.Add(_seasons); header.Children.Add(_seasonMark); header.Children.Add(_episodeHeading);
        return Ui.Column(14, header, _calendar, _episodes);
    }
    private void RenderEpisodes()
    {
        _episodes.Children.Clear();
        if (Shell.Session is not { } session) return;
        var page = (_seasons.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
        var episodes = page is null ? [] : EpisodePages.Episodes(_allEpisodes, page);
        _episodeHeading.Text = L("Épisodes", "Episodes") + $"  ·  {(page is null ? 0 : _allEpisodes.Count(x => (x.ParentIndexNumber ?? 1) == page.Season))}";
        ShowSeasonMark(page);
        if (episodes.Count == 0) { _episodes.Children.Add(new TextBlock { Text = L("Aucun épisode disponible dans cette bibliothèque.", "No episodes available in this library."), Foreground = Ui.Muted }); return; }
        // The episode Lecture plays is in another season than the one shown (a long anime opened on its first): a click brings it.
        var pages = _seasons.Items.OfType<ComboBoxItem>().Select(x => x.Tag).OfType<EpisodePage>().ToList();
        if (_found is null && _next is { } next && EpisodePages.NextElsewhere(pages, _allEpisodes, page, next) is { } elsewhere) _episodes.Children.Add(NextElsewhereRow(next, elsewhere));
        foreach (var episode in episodes) _episodes.Children.Add(EpisodeRow(session, episode));
    }
    private async Task LoadCalendarAsync(Session session)
    {
        try
        {
            var episodes = await session.Client.VirtualEpisodesAsync(session.Lifetime);
            _schedule = EpisodeCalendar.Read(episodes.Where(x => x.SeriesId == _item.Id), EpisodeCalendar.Today());
        }
        // Optional, as the similar titles: the episodes say it when Jellyfin is out of reach.
        catch (Exception ex) when (Errors.Expected(ex)) { _schedule = ([], []); }
        RenderCalendar();
    }
    private void RenderCalendar()
    {
        _calendar.Children.Clear();
        var (missing, coming) = _schedule;
        var today = EpisodeCalendar.Today();
        var first = missing.Count > 0 ? EpisodeCalendar.MissingText(missing) : EpisodeCalendar.NextText(coming, today);
        _calendar.IsVisible = first.Length > 0;
        if (first.Length == 0) return;
        var warning = Ui.Brush("#D5B787");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = first, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (missing.Count > 0 && coming.Count > 0) text.Children.Add(new TextBlock { Text = EpisodeCalendar.NextText(coming, today), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(new Icon(missing.Count > 0 ? "warning" : "calendar", 18) { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = missing.Count > 0 ? warning : Ui.Muted });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var chevron = new Icon(_calendarOpen ? "up" : "down", 14) { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted };
        Grid.SetColumn(chevron, 2); grid.Children.Add(chevron);
        var line = new Button { Content = grid, Classes = { "row" }, Background = Ui.Brush("#18181B"), Padding = new Thickness(16, 12), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(line, string.Join(". ", text.Children.OfType<TextBlock>().Select(x => x.Text)));
        Avalonia.Automation.AutomationProperties.SetHelpText(line, _calendarOpen ? L("Masquer la liste", "Hide the list") : L("Voir la liste", "Show the list"));
        line.Click += (_, _) => { _calendarOpen = !_calendarOpen; RenderCalendar(); };
        _calendar.Children.Add(line);
        if (!_calendarOpen) return;
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(16, 4, 16, 12) };
        list.Children.Add(new TextBlock { Text = L("D’après TheTVDB, que ton serveur Jellyfin consulte avec son extension : ces épisodes ne sont pas dans ta bibliothèque.", "From TheTVDB, which your Jellyfin server checks with its plugin: these episodes aren’t in your library."), Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        void Heading(string title) => list.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = Ui.Faint, Margin = new Thickness(0, 10, 0, 0) });
        void Line(MediaItem x, string when)
        {
            var row = new DockPanel();
            var day = new TextBlock { Text = when, Foreground = Ui.Muted, Margin = new Thickness(16, 0, 0, 0) };
            DockPanel.SetDock(day, Dock.Right);
            row.Children.Add(day);
            row.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { EpisodeCalendar.Code(x), x.Name }.Where(y => !string.IsNullOrEmpty(y))), TextTrimming = TextTrimming.CharacterEllipsis });
            list.Children.Add(row);
        }
        if (missing.Count > 0)
        {
            Heading(missing.Count == 1 ? L("Manquant", "Missing") : L("Manquants", "Missing"));
            foreach (var x in missing.Take(30)) Line(x, L($"Sorti {EpisodeCalendar.DayText(EpisodeCalendar.AirDay(x)!.Value, today)}", $"Aired {EpisodeCalendar.DayText(EpisodeCalendar.AirDay(x)!.Value, today)}"));
            if (missing.Count > 30) list.Children.Add(new TextBlock { Text = L($"Et {Lang.Count(missing.Count - 30, "autre", "autres", "more", "more")}", $"And {Lang.Count(missing.Count - 30, "autre", "autres", "more", "more")}"), Foreground = Ui.Muted });
        }
        if (coming.Count > 0)
        {
            Heading(L("À venir", "Coming up"));
            foreach (var x in coming.Take(12)) Line(x, EpisodeCalendar.DayTitle(EpisodeCalendar.AirDay(x)!.Value, today));
        }
        _calendar.Children.Add(list);
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
        var button = new Button { Content = grid, Classes = { "row" }, Background = Ui.Brush("#18181B"), Padding = new Thickness(16, 12) };
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
            new TextBlock { Text = OverviewText.Plain(episode.Overview, flat: true), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis, LineHeight = 19 });
        copy.VerticalAlignment = VerticalAlignment.Center; copy.Margin = new Thickness(20, 0);
        var state = episode.UserData.Played ? L("✓ Vu", "✓ Watched") : isNext ? episode.Progress > 0 ? L("En cours", "In progress") : L("À suivre", "Up next") : null;
        var status = Ui.Column(8, new TextBlock { Text = episode.DurationLabel, FontSize = 12.5, Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Right });
        if (state is not null) status.Children.Add(new TextBlock { Text = state, FontSize = 12, Foreground = Ui.Brush(episode.UserData.Played ? "#A0C7A8" : "#DADAE0"), HorizontalAlignment = HorizontalAlignment.Right });
        // « Sur ce Mac », « 42 % », « En attente »: a copy kept on this Mac.
        if (Shell.CopyState(episode.Id) is { } kept) status.Children.Add(new TextBlock { Text = kept, FontSize = 12, Foreground = Ui.Brush("#DADAE0"), HorizontalAlignment = HorizontalAlignment.Right });
        status.VerticalAlignment = VerticalAlignment.Center; status.Width = 90;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(art); Grid.SetColumn(copy, 1); grid.Children.Add(copy); Grid.SetColumn(status, 2); grid.Children.Add(status);
        var button = new Button { Content = grid, Classes = { "row" } };
        if (isNext || _found?.Id == episode.Id) button.Background = Ui.Brush("#18181B");
        button.Click += (_, _) => _ = Shell.PlayAsync(episode);
        button.ContextMenu = Shell.Menu(episode);
        Avalonia.Automation.AutomationProperties.SetName(button, $"{L("Épisode", "Episode")} {episode.IndexNumber}, {episode.Name}, {episode.DurationLabel}{(episode.UserData.Played ? L(", vu", ", watched") : isNext ? L(", à suivre", ", up next") : "")}");
        Ui.LoadWhenVisible(button, image, () => session.Images.EpisodeAsync(episode, 352));
        return button;
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
        foreach (var old in _body.Children.OfType<CardRow>().ToList()) _body.Children.Remove(old);
        CardRow Row(string title, List<MediaItem> items) => new(title, items.Select(x => { var card = Cards.Poster(x, session.Images, Shell.Open, Shell.Menu); card.Width = 168; return (Control)card; }), sidePadding: 0);
        if (saga is not null) _body.Children.Add(Row(saga.Name, saga.Items));
        if (studio is { } titles) _body.Children.Add(Row(L($"Du même studio · {titles.Studio.Name}", $"More from {titles.Studio.Name}"), titles.Items));
        if (extras.Count > 0) _body.Children.Add(new CardRow(L("Bonus", "Extras"), extras.Select(x => (Control)Cards.Wide(x, session.Images, extra => _ = Shell.PlayAsync(extra))), sidePadding: 0));
        if (similar.Count > 0) _body.Children.Add(Row(L("Titres similaires", "More like this"), similar));
    }
}
