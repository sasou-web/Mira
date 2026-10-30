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
/// A title's page: its banner, Lecture or Reprendre, favourites and watched, the synopsis, genres, cast, direction and
/// studios (each one leads to its titles), the episodes of a series by season (a season marked watched at once), its
/// saga, its studio's other titles and similar titles.
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
    private readonly TextBlock _episodeHeading = new() { FontSize = 24, FontWeight = FontWeight.Normal, Foreground = Ui.TextTertiary, VerticalAlignment = VerticalAlignment.Center };
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
    public IEnumerable<string> SeasonLabels => _seasons.Items.OfType<ComboBoxItem>().Select(x => x.Content as string ?? "");

    public DetailPage(MainWindow shell, MediaItem item, MediaItem? atEpisode = null) : base(shell)
    {
        _item = item; _found = atEpisode;
        _backdrop.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(380) }];
        _header.Children.Add(new Border { Background = Ui.Brush("#0E0E10") });
        _header.Children.Add(_backdrop);
        _header.Children.Add(new Border { Background = Gradient(0, 0.5, 1, 0.5, ("#F2070708", 0), ("#A0070708", 0.45), ("#20070708", 0.8)) });
        _header.Children.Add(new Border { Background = Gradient(0.5, 0, 0.5, 1, ("#00070708", 0.35), ("#FF070708", 1)) });
        var back = Ui.IconButton("back", L("Retour · Échap", "Back · Esc"), () => Shell.Back(), "icon", 24);
        back.HorizontalAlignment = HorizontalAlignment.Left; back.VerticalAlignment = VerticalAlignment.Top; back.Margin = new Thickness(40, 26 + shell.TitleBarInset, 0, 0);
        back.Width = back.Height = 46; back.Background = Ui.Brush("#66000000");
        _header.Children.Add(_top); _header.Children.Add(back);
        var page = new StackPanel();
        page.Children.Add(_header); page.Children.Add(_body);
        Content = new ScrollViewer { Content = page };
        SizeChanged += (_, e) => _header.Height = Math.Clamp(e.NewSize.Height * 0.66, 430, 720);
        _seasons.SelectionChanged += (_, _) => { if (!_filling) RenderEpisodes(); };
        _seasonMark.Click += (_, _) => _ = MarkSeasonAsync();
        Avalonia.Automation.AutomationProperties.SetName(_seasons, L("Saison", "Season"));
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
        if (_item.Type == "Series") { _ = LoadFollowAsync(session); _ = LoadCalendarAsync(session); }
        if (!_admin) _ = CheckAdministratorAsync(session);
        if (_item.Type == "Series" && SeriesTarget is { } target && _targetFile?.Id != target.Id) await LoadTargetFileAsync(session, target);
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
        if (await session.Images.LogoAsync(_item) is { } logo && _top.Children.Count > 1 && _top.Children[1] is TextBlock title)
        {
            _top.Children[1] = new Image { Source = logo, MaxHeight = 140, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 6) };
            Avalonia.Automation.AutomationProperties.SetName(_top.Children[1], title.Text ?? "");
            Avalonia.Automation.AutomationProperties.SetHeadingLevel(_top.Children[1], 1);
        }
    }

    /// <summary>The episode Lecture opens on a series: Jellyfin's next one, else one in progress, else the first unwatched.</summary>
    private MediaItem? SeriesTarget => _next ?? _allEpisodes.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played) ?? _allEpisodes.FirstOrDefault(x => !x.UserData.Played) ?? _allEpisodes.FirstOrDefault();

    private void RenderTop()
    {
        var logo = _top.Children.Count > 1 && _top.Children[1] is Image ? _top.Children[1] : null;
        _top.Children.Clear();
        _top.Children.Add(new TextBlock { Text = (Ui.Kind(_item) + (_item.ProductionYear is { } y ? " · " + y : "")).ToUpperInvariant(), Classes = { "label" }, FontSize = 12.5, Foreground = Ui.Brush("#D0D0D6") });
        var name = new TextBlock { Text = _item.Name, FontSize = 45, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, LineHeight = 49 };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(name, 1);
        _top.Children.Add(logo ?? name);
        var meta = new List<string>();
        if (_item.ProductionYear is { } year) meta.Add(year.ToString());
        if (_item.Type == "Series") { if (_item.ChildCount is > 0) meta.Add(Count(_item.ChildCount.Value, "saison", "saisons", "season", "seasons")); }
        else if (_item.DurationLabel.Length > 0) meta.Add(_item.DurationLabel);
        if (_item.CommunityRating is { } rating) meta.Add("★ " + Number(rating, "0.0"));
        if (!string.IsNullOrWhiteSpace(_item.OfficialRating)) meta.Add(_item.OfficialRating!);
        // The picture of the file Lecture plays and when it ends: the film's, or the episode's it picks up at.
        var played = _item.Type == "Series" ? SeriesTarget : _item;
        if ((_item.Type != "Series" ? MediaQuality.Label(_item) : _targetFile is { } known && known.Id == played?.Id ? MediaQuality.Label(known.Source) : null) is { } file) meta.Add(file);
        if (MediaQuality.EndsAt(played, DateTime.Now, played is { Progress: > 0, UserData.Played: false }) is { } end) meta.Add(end);
        _top.Children.Add(new TextBlock { Text = string.Join("  ·  ", meta), FontSize = 16, Foreground = Ui.Brush("#DADADF") });

        var actions = new WrapPanel { ItemSpacing = 12, LineSpacing = 12 };
        if (_item.Type == "Series")
        {
            var target = SeriesTarget;
            var code = target is null ? "" : $"S{target.ParentIndexNumber ?? 1} {L("É", "E")}{target.IndexNumber ?? 1}";
            var label = target is null ? L("Lecture", "Play") : GroupPlayLabel(target, code)
                ?? (target.Progress > 0 && !target.UserData.Played ? L("Reprendre", "Resume") : L("Regarder", "Watch")) + " · " + code;
            actions.Children.Add(Ui.Action(label, "play", "primary", () => { if (SeriesTarget is { } episode) _ = Shell.PlayAsync(episode); else _ = Shell.PlayAsync(_item); }));
        }
        else
        {
            var resume = _item.Progress > 0 && !_item.UserData.Played;
            actions.Children.Add(Ui.Action(GroupPlayLabel(_item, "") ?? (resume ? L("Reprendre · ", "Resume · ") + Ui.Remaining(_item).ToLowerInvariant() : L("Lecture", "Play")), "play", "primary", () => _ = Shell.PlayAsync(_item)));
            if (resume) actions.Children.Add(Ui.Action(L("Depuis le début", "From the beginning"), "refresh", "", () => _ = Shell.PlayAsync(_item, fromStart: true)));
        }
        actions.Children.Add(Ui.Action(_item.UserData.IsFavorite ? L("Dans tes favoris", "In your favorites") : L("Ajouter aux favoris", "Add to favorites"), _item.UserData.IsFavorite ? "heart-filled" : "heart", _item.UserData.IsFavorite ? "on" : "", () => _ = Shell.SetFavoriteAsync(_item, !_item.UserData.IsFavorite)));
        actions.Children.Add(Ui.Action(_item.UserData.Played ? L("Vu", "Watched") : L("Marquer comme vu", "Mark as watched"), "check", _item.UserData.Played ? "on" : "", () => _ = Shell.SetPlayedAsync(_item, !_item.UserData.Played)));
        var group = Shell.Group.Active ? Shell.Group.Group : null;
        Button? together = null;
        together = Ui.Action(group is null ? L("Regarder ensemble", "Watch together") : L("Ensemble · ", "Together · ") + Together.People(Math.Max(1, group.Participants.Count)), "users", group is null ? "" : "on", () => _ = TogetherMenuAsync(together!));
        actions.Children.Add(together);
        if (_followKnown)
        {
            Button? follow = null;
            follow = Ui.Action(_follow is null ? L("Nouveaux épisodes", "New episodes") : L("Épisodes suivis", "Episodes followed"), _follow is null ? "plus" : "check", _follow is null ? "" : "on", () => FollowMenu(follow!));
            ToolTip.SetTip(follow, _follow is null ? L("Le PC serveur cherche les nouveaux épisodes et les télécharge tout seul, avec TorLink", "The server PC looks for new episodes and downloads them on its own, with TorLink")
                : string.Join(" · ", new[] { RemoteLines.FollowNote(_follow.Note), RemoteLines.Follow(_follow, DateTimeOffset.UtcNow) }.Where(x => x.Length > 0)));
            actions.Children.Add(follow);
        }
        if (_admin && _item.Type is "Movie" or "Series")
        {
            Button? fix = null;
            fix = Ui.Action(L("Corriger", "Fix"), "search", "", () => FixMenu(fix!));
            actions.Children.Add(fix);
        }
        _top.Children.Add(actions);
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
        async Task Run(Func<Task> action, string done)
        {
            try { await action(); Shell.Notice(done); }
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); return; }
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
        Add(L("Ne plus suivre", "Stop following"), () => _ = Run(() => session.Client.TorLinkUnfollowAsync(follow.Id, session.Lifetime), L($"« {follow.Name} » n’est plus suivie.", $"No longer following “{follow.Name}”.")));
        Ui.OpenUnder(menu, button);
    }
    /// <summary>An administrator of Jellyfin: « Corriger » offers « Identifier… », « Actualiser les métadonnées » and, for a film, « Chercher une meilleure version ».</summary>
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
            var window = new IdentifyWindow(session, _item);
            await window.ShowDialog(Shell);
            if (window.Said is { } said) Shell.Notice(said);
            if (window.Applied && ReferenceEquals(session, Shell.Session) && Shell.Current == this) { _backdrop.Source = null; _backdrop.Opacity = 0; await RefreshAsync(); }
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
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); }
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
            catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); return; }
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

    private void RenderBody(Session session)
    {
        _body.Children.Clear();
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,48,*") };
        var left = new StackPanel { Spacing = 18 };
        if (!string.IsNullOrWhiteSpace(_item.Overview)) left.Children.Add(new TextBlock { Text = OverviewText.Plain(_item.Overview), FontSize = 16, LineHeight = 26, Foreground = Ui.Brush("#C9C9CF"), TextWrapping = TextWrapping.Wrap });
        columns.Children.Add(left);
        var side = new StackPanel { Spacing = 22 }; Grid.SetColumn(side, 2);
        // Each genre: Films or Séries (as the title is) filtered on it, as in Mira web.
        if (_item.Genres.Length > 0) side.Children.Add(Section(L("GENRES", "GENRES"), Links(_item.Genres.Select<string, (string, string, Action?)>(genre => (genre,
            L($"Les {(_item.Type == "Series" ? "séries" : "films")} « {genre} » de ta bibliothèque", $"The {genre} {(_item.Type == "Series" ? "shows" : "movies")} of your library"),
            () => Shell.OpenGenre(genre, _item.Type))))));
        if (_item.CastPeople.Length > 0) side.Children.Add(Section(L("AVEC", "STARRING"), People(_item.CastPeople, director: false)));
        if (_item.DirectorPeople.Length > 0) side.Children.Add(Section(L("RÉALISATION", _item.DirectorPeople.Length > 1 ? "DIRECTORS" : "DIRECTOR"), People(_item.DirectorPeople, director: true)));
        var studios = _item.Studios.Where(x => x.Name.Trim().Length > 0).Take(3).ToList();
        if (studios.Count > 0) side.Children.Add(Section("STUDIO", Links(studios.Select(studio => (studio.Name, L($"Les titres de {studio.Name} dans ta bibliothèque", $"Titles from {studio.Name} in your library"),
            studio.Id is { Length: > 0 } ? () => Shell.OpenStudio(studio) : (Action?)null)))));
        columns.Children.Add(side);
        _body.Children.Add(columns);
        if (_item.Type == "Series") _body.Children.Add(EpisodeSection());
    }
    private static Control Section(string title, Control content) => Ui.Column(8, new TextBlock { Text = title, Classes = { "label" } }, content);
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
                var link = new Button { Content = text, Classes = { "link" } };
                ToolTip.SetTip(link, tip);
                link.Click += (_, _) => open();
                name = link;
            }
            else name = new TextBlock { Text = text, FontSize = 15 };
            panel.Children.Add(last ? name : Ui.Row(0, name, new TextBlock { Text = "·", FontSize = 15, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) }));
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
        if (_seasons.Parent is Panel old) old.Children.Remove(_seasons);
        if (_seasonMark.Parent is Panel holding) holding.Children.Remove(_seasonMark);
        header.Children.Add(_seasons); header.Children.Add(_seasonMark); header.Children.Add(_episodeHeading);
        if (_episodes.Parent is Panel holder) holder.Children.Remove(_episodes);
        if (_calendar.Parent is Panel calendarHolder) calendarHolder.Children.Remove(_calendar);
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
            Shell.Notice(played ? L("Saison marquée comme vue.", "Season marked as watched.") : L("Saison marquée comme non vue.", "Season marked as unwatched."));
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); }
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
