using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// Missing episodes and coming ones, from TheTVDB's plugin on Jellyfin (TvdbExtension), « à leur place » (EpisodeShelf,
// tests/fixtures/calendar.json): in the season list (a season the library has none of), between a season's episodes
// (one thin line for those it lacks, a hollow row for the next one to come out); « Épisodes manquants et sorties » in
// Réglages → Jellyfin installs the plugin, its names and pictures turned off.
public partial class MainWindow
{
    // This series' virtual episodes; the thin lines and absent seasons opened, by their first episode.
    private List<MediaItem> _seriesVirtuals = [];
    private readonly HashSet<string> _shelfOpen = [];
    private bool _episodesRead, _tvdbBusy;
    // The virtual episodes of every series, kept a minute for this connection (one request for the whole library).
    private (JellyfinClient Client, DateTime At, List<MediaItem> Episodes)? _virtualEpisodes;

    private async Task<List<MediaItem>> VirtualEpisodesAsync(JellyfinClient client)
    {
        if (_virtualEpisodes is { } kept && ReferenceEquals(kept.Client, client) && DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(1)) return kept.Episodes;
        var episodes = await client.VirtualEpisodesAsync(_connectionLifetime.Token);
        _virtualEpisodes = (client, DateTime.UtcNow, episodes);
        return episodes;
    }

    /// <summary>This series' episodes from TheTVDB, put in its season list and its episodes once both are read.</summary>
    private async Task LoadCalendarAsync(MediaItem series, int version)
    {
        if (_client is not { } client) return;
        List<MediaItem> found;
        try { found = (await VirtualEpisodesAsync(client)).Where(x => x.SeriesId == series.Id).ToList(); }
        // Optional, as the similar titles: the episodes say it when Jellyfin is out of reach.
        catch (Exception ex) when (IsExpected(ex)) { found = []; }
        if (version != _detailVersion || found.Count == 0 && _seriesVirtuals.Count == 0) return;
        _seriesVirtuals = found;
        if (_episodesRead) { FillSeasons(); RenderEpisodes(); }
    }

    /// <summary>
    /// The season list: the library's seasons (slices of a long one), then those it has none of; the one shown before
    /// stays, else that of the episode found by a search, of the next one, of one in progress, of the first unwatched,
    /// of S01E01 (a series watched to its end: not its specials).
    /// </summary>
    private void FillSeasons()
    {
        var previous = (SeasonSelector.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
        _seasonBusy = true; SeasonSelector.Items.Clear();
        var own = EpisodePages.From(_episodes);
        var pages = EpisodeShelf.Pages(own, _episodes, _seriesVirtuals, EpisodeCalendar.Today(), _settings.ShowMissingEpisodes);
        foreach (var page in pages) SeasonSelector.Items.Add(new ComboBoxItem { Content = page.Label, Tag = page });
        // « Saison 1 · 1001–1100 » and « Season 1 · not in library » need more room than « Saison 1 ».
        SeasonSelector.Width = pages.Any(x => x.Absent) ? 250 : pages.GroupBy(x => x.Season).Any(x => x.Count() > 1) ? 230 : 180;
        var resume = (previous is null ? null : pages.FirstOrDefault(x => x.Season == previous.Season && x.Skip == previous.Skip && x.Absent == previous.Absent))
            ?? EpisodePages.Holding(own, _episodes, _episodes.FirstOrDefault(x => x.Id == _detailEpisode?.Id))
            ?? EpisodePages.Holding(own, _episodes, _detailNext ?? EpisodePages.Start(_episodes));
        SeasonSelector.SelectedItem = SeasonSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, resume)) ?? SeasonSelector.Items.Cast<object>().FirstOrDefault();
        _seasonBusy = false;
    }

    /// <summary>A season the library has none of: a calm card for its aired episodes, then those coming, as in a season.</summary>
    private void RenderAbsentSeason(EpisodePage page)
    {
        EpisodeHeading.Text = L("Épisodes", "Episodes"); SeasonMarkButton.Visibility = Visibility.Collapsed;
        var today = EpisodeCalendar.Today();
        foreach (var line in EpisodeShelf.Lines([], _seriesVirtuals, page.Season, true, true, today, _settings.ShowMissingEpisodes))
            EpisodesPanel.Children.Add(line.Kind == ShelfKind.Missing ? AbsentSeasonCard(page.Season, line.Episodes, today) : ShelfRow(line, today));
    }

    /// <summary>The row of a line that isn't an episode of the library: hollow for the next to come out, else a thin line.</summary>
    private UIElement ShelfRow(ShelfLine line, DateOnly today) => line.Kind == ShelfKind.Coming ? ComingRow(line.First, today) : ThinLine(line, today);

    // As an episode's row, hollow: no picture to play, its day on the right.
    private Border ComingRow(MediaItem episode, DateOnly today)
    {
        var row = new Border { BorderBrush = Palette.Get("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 9) };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var thumb = new Border { Width = 160, Height = 90, CornerRadius = new CornerRadius(4), Background = Palette.Get("Bg"), BorderBrush = Palette.Get("Raised2"), BorderThickness = new Thickness(1) };
        thumb.Child = new Icon { Kind = "calendar", Width = 22, Height = 22, Foreground = Palette.Get("Faint") };
        grid.Children.Add(thumb);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(21, 0, 20, 0) }; Grid.SetColumn(copy, 1);
        copy.Children.Add(new TextBlock { Text = episode.IndexNumber is { } number ? $"{number:00}   {EpisodeShelf.Name(episode)}" : EpisodeShelf.Name(episode), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextTertiary"), TextTrimming = TextTrimming.CharacterEllipsis });
        copy.Children.Add(new TextBlock { Text = EpisodeShelf.NotOut, FontSize = 12, Foreground = Palette.Get("Faint"), Margin = new Thickness(0, 7, 0, 0) });
        grid.Children.Add(copy);
        var day = new TextBlock { Text = EpisodeCalendar.DayTitle(EpisodeCalendar.AirDay(episode)!.Value, today), FontSize = 12, Foreground = Palette.Get("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(day, 2); grid.Children.Add(day);
        row.Child = grid;
        return row;
    }

    // « É3–4 · pas dans ta bibliothèque · sortis du 25 oct. au 1 nov. », where they would be; « Voir » lists them under it.
    private StackPanel ThinLine(ShelfLine line, DateOnly today)
    {
        var key = line.First.Id; var open = _shelfOpen.Contains(key);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        var button = new Button { Background = Brushes.Transparent, BorderBrush = Palette.Get("Line"), Padding = new Thickness(15, 11, 15, 11), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Icon { Kind = line.Kind == ShelfKind.Later ? "calendar" : "next", Width = 14, Height = 14, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get("Faint") });
        var text = line.Kind == ShelfKind.Later ? EpisodeShelf.LaterText(line.Episodes, today) : EpisodeShelf.MissingText(line.Episodes, today);
        var words = new TextBlock { FontSize = 13, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(words, 1);
        var code = EpisodeShelf.Code(line.Episodes);
        words.Inlines.Add(new System.Windows.Documents.Run(code) { FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextTertiary") });
        words.Inlines.Add(new System.Windows.Documents.Run(text[code.Length..]));
        grid.Children.Add(words);
        var show = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) }; Grid.SetColumn(show, 2);
        show.Children.Add(new TextBlock { Text = EpisodeShelf.Show(line.Episodes.Count, open), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextSecondary") });
        show.Children.Add(new Icon { Kind = open ? "up" : "down", Width = 12, Height = 12, Margin = new Thickness(6, 0, 0, 0), Foreground = Palette.Get("TextSecondary") });
        grid.Children.Add(show); button.Content = grid;
        AutomationProperties.SetName(button, text); AutomationProperties.SetHelpText(button, EpisodeShelf.Show(line.Episodes.Count, open));
        button.Click += (_, _) => Toggle(key);
        panel.Children.Add(button);
        if (open) panel.Children.Add(ShelfEntries(line.Episodes, today));
        return panel;
    }

    // « La saison 1 n’est pas dans ta bibliothèque », how many and when, where they come from; its episodes on demand.
    private StackPanel AbsentSeasonCard(int season, IReadOnlyList<MediaItem> run, DateOnly today)
    {
        var key = "season-" + run[0].Id; var open = _shelfOpen.Contains(key);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        var card = new Border { BorderBrush = Palette.Get("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(22) };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Icon { Kind = "calendar", Width = 26, Height = 26, Margin = new Thickness(0, 2, 18, 0), VerticalAlignment = VerticalAlignment.Top, Foreground = Palette.Get("Faint") });
        var copy = new StackPanel(); Grid.SetColumn(copy, 1);
        copy.Children.Add(new TextBlock { Text = EpisodeShelf.SeasonTitle(season), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap });
        copy.Children.Add(new TextBlock { Text = EpisodeShelf.SeasonText(run, today), FontSize = 13, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 4, 0, 0) });
        var show = new Button { Style = (Style)FindResource("Quiet"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-12, 8, 0, 0) };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock { Text = EpisodeShelf.ShowSeason(run.Count, open), FontWeight = FontWeights.SemiBold });
        label.Children.Add(new Icon { Kind = open ? "up" : "down", Width = 12, Height = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        show.Content = label; AutomationProperties.SetName(show, ((TextBlock)label.Children[0]).Text);
        show.Click += (_, _) => Toggle(key);
        copy.Children.Add(show);
        grid.Children.Add(copy); card.Child = grid;
        panel.Children.Add(card);
        if (open) panel.Children.Add(ShelfEntries(run, today));
        return panel;
    }

    // The episodes of a thin line or of an absent season, one by one: « É3 · Le nom », when it aired or comes out.
    private StackPanel ShelfEntries(IReadOnlyList<MediaItem> run, DateOnly today)
    {
        var list = new StackPanel { Margin = new Thickness(15, 6, 15, 6) };
        foreach (var episode in run)
        {
            var (name, when) = EpisodeShelf.Entry(episode, today);
            var line = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
            var day = new TextBlock { Text = when, FontSize = 13, Foreground = Palette.Get("Muted"), Margin = new Thickness(16, 0, 0, 0) };
            DockPanel.SetDock(day, Dock.Right); line.Children.Add(day);
            line.Children.Add(new TextBlock { Text = name, FontSize = 13, Foreground = Palette.Get("TextTertiary"), TextTrimming = TextTrimming.CharacterEllipsis });
            list.Children.Add(line);
        }
        return list;
    }

    private void Toggle(string key)
    {
        if (!_shelfOpen.Remove(key)) _shelfOpen.Add(key);
        RenderEpisodes();
    }

    /// <summary>« Épisodes manquants et sorties », for an administrator: where TheTVDB's plugin is, and the button that moves it on.</summary>
    private async Task ShowTvdbAsync(string? said = null)
    {
        if (_demo || _client is not { } client) { TvdbRow.Visibility = Visibility.Collapsed; return; }
        if (_tvdbBusy) return;
        _tvdbBusy = true;
        try
        {
            if (!await IsAdministratorAsync(client) || await client.TvdbStateAsync(_connectionLifetime.Token) is not { } state) { TvdbRow.Visibility = Visibility.Collapsed; return; }
            if (!ReferenceEquals(client, _client)) return;
            TvdbRow.Visibility = Visibility.Visible;
            TvdbPrivacy.Text = TvdbExtension.Privacy;
            TvdbButton.Visibility = Visibility.Visible; TvdbButton.IsEnabled = true;
            switch (state)
            {
                case TvdbExtension.State.Absent:
                    TvdbState.Text = L("Sur la page d’une série : les épisodes qui manquent à ta bibliothèque et la date des prochains, avec une page « À venir » sur ton téléphone. Jellyfin les prend sur TheTVDB, par son extension officielle.", "On a series’ page: the episodes missing from your library and the date of the next ones, with a “Coming up” page on your phone. Jellyfin gets them from TheTVDB, through its official plugin.");
                    TvdbButton.Content = L("Activer", "Turn on");
                    break;
                case TvdbExtension.State.Restart:
                    TvdbState.Text = L("L’extension TheTVDB est installée : Jellyfin la charge à son redémarrage.", "The TheTVDB plugin is installed: Jellyfin loads it when it restarts.");
                    TvdbButton.Content = L("Redémarrer Jellyfin", "Restart Jellyfin");
                    break;
                case TvdbExtension.State.Active:
                    var episodes = await VirtualEpisodesAsync(client);
                    var (missing, coming) = EpisodeCalendar.Read(episodes, EpisodeCalendar.Today());
                    TvdbState.Text = L("Activé.", "On.") + " " + (missing.Count + coming.Count == 0
                        ? L("Rien ne manque ni n’est annoncé pour l’instant ; Jellyfin relit TheTVDB à chaque analyse de la bibliothèque.", "Nothing missing or announced for now; Jellyfin checks TheTVDB again at each library scan.")
                        : string.Join(", ", new[] { missing.Count > 0 ? Lang.Count(missing.Count, "épisode manquant", "épisodes manquants", "missing episode", "missing episodes") : null, coming.Count > 0 ? Lang.Count(coming.Count, "épisode à venir", "épisodes à venir", "coming episode", "coming episodes") : null }.OfType<string>()) + ".");
                    TvdbButton.Visibility = Visibility.Collapsed;
                    break;
                default:
                    TvdbState.Text = L("L’extension TheTVDB est désactivée dans le tableau de bord de Jellyfin (Extensions) : réactive-la là-bas.", "The TheTVDB plugin is turned off in Jellyfin’s dashboard (Plugins): turn it back on there.");
                    TvdbButton.Visibility = Visibility.Collapsed;
                    break;
            }
            TvdbStatus.Text = said ?? "";
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        // Jellyfin out of reach: the rest of the tab says so already; this comes back with it.
        catch (Exception ex) when (IsExpected(ex)) { }
        finally { _tvdbBusy = false; }
    }

    /// <summary>
    /// Installs the plugin (its names and pictures off) when it isn't there, then restarts Jellyfin when nobody is
    /// watching, waits for it, and has it scan the libraries: the missing episodes arrive with the scan.
    /// </summary>
    private async void Tvdb_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client) return;
        TvdbButton.IsEnabled = false;
        try
        {
            if (await client.TvdbStateAsync() == TvdbExtension.State.Absent)
            {
                TvdbStatus.Text = L("Installation de l’extension TheTVDB…", "Installing the TheTVDB plugin…");
                if (!await client.InstallTvdbAsync())
                {
                    TvdbStatus.Text = L("Jellyfin n’a pas pu l’installer : son dépôt d’extensions ne répond peut-être pas. Réessaie plus tard.", "Jellyfin couldn’t install it: its plugin repository may not be answering. Try again later.");
                    TvdbButton.IsEnabled = true;
                    return;
                }
            }
            if ((await client.ViewersAsync()).Count > 0)
            {
                await ShowTvdbAsync(L("Quelqu’un regarde sur Jellyfin en ce moment : redémarre-le quand ce sera fini.", "Someone is watching on Jellyfin right now: restart it once they’re done."));
                return;
            }
            TvdbStatus.Text = L("Jellyfin redémarre…", "Jellyfin is restarting…");
            await client.RestartAsync();
            var limit = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            await Task.Delay(TimeSpan.FromSeconds(2));
            while (DateTime.UtcNow < limit && !await client.PingAsync()) await Task.Delay(TimeSpan.FromSeconds(2));
            if (!ReferenceEquals(client, _client)) return;
            if (await client.TvdbStateAsync() != TvdbExtension.State.Active)
            {
                await ShowTvdbAsync(L("Jellyfin n’a pas encore chargé l’extension : réessaie dans un moment.", "Jellyfin hasn’t loaded the plugin yet: try again in a moment."));
                return;
            }
            await client.ScanLibrariesAsync();
            _virtualEpisodes = null;
            await ShowTvdbAsync(L("Jellyfin analyse ta bibliothèque : les épisodes manquants et les prochaines sorties arrivent d’ici quelques minutes.", "Jellyfin is scanning your library: missing episodes and coming ones show up within a few minutes."));
        }
        catch (Exception ex) when (IsExpected(ex)) { TvdbStatus.Text = ServerError(ex); TvdbButton.IsEnabled = true; }
    }
}
