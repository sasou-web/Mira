using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// Missing episodes and coming ones, from TheTVDB's plugin on Jellyfin (TvdbExtension): the line under a series'
// « Épisodes », opened into their list; « Épisodes manquants et sorties » in Réglages → Jellyfin installs the plugin,
// its names and pictures turned off. As Mira web (calendar.js, views/detail.js), by the same rules (EpisodeCalendar,
// tests/fixtures/calendar.json).
public partial class MainWindow
{
    private (List<MediaItem> Missing, List<MediaItem> Coming) _calendar = ([], []);
    private bool _calendarOpen, _tvdbBusy;
    // The virtual episodes of every series, kept a minute for this connection (one request for the whole library).
    private (JellyfinClient Client, DateTime At, List<MediaItem> Episodes)? _virtualEpisodes;

    private async Task<List<MediaItem>> VirtualEpisodesAsync(JellyfinClient client)
    {
        if (_virtualEpisodes is { } kept && ReferenceEquals(kept.Client, client) && DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(1)) return kept.Episodes;
        var episodes = await client.VirtualEpisodesAsync(_connectionLifetime.Token);
        _virtualEpisodes = (client, DateTime.UtcNow, episodes);
        return episodes;
    }

    /// <summary>The line of this series, nothing without missing or coming episodes (or without the plugin).</summary>
    private async Task LoadCalendarAsync(MediaItem series, int version)
    {
        if (_client is not { } client) { _calendar = ([], []); RenderCalendar(); return; }
        try
        {
            var episodes = await VirtualEpisodesAsync(client);
            if (version != _detailVersion) return;
            _calendar = EpisodeCalendar.Read(episodes.Where(x => x.SeriesId == series.Id), EpisodeCalendar.Today());
        }
        // Optional, as the similar titles: the episodes say it when Jellyfin is out of reach.
        catch (Exception ex) when (IsExpected(ex)) { if (version != _detailVersion) return; _calendar = ([], []); }
        RenderCalendar();
    }

    private void RenderCalendar()
    {
        var (missing, coming) = _calendar;
        var today = EpisodeCalendar.Today();
        var first = missing.Count > 0 ? EpisodeCalendar.MissingText(missing) : EpisodeCalendar.NextText(coming, today);
        CalendarLine.Visibility = first.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (first.Length == 0) { _calendarOpen = false; CalendarList.Visibility = Visibility.Collapsed; return; }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = first, Foreground = Palette.Get("Text"), TextWrapping = TextWrapping.Wrap });
        if (missing.Count > 0 && coming.Count > 0) text.Children.Add(new TextBlock { Text = EpisodeCalendar.NextText(coming, today), Foreground = Palette.Get("Muted"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Icon { Kind = missing.Count > 0 ? "warning" : "calendar", Width = 18, Height = 18, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get(missing.Count > 0 ? "Warning" : "TextSecondary") });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var chevron = new Icon { Kind = _calendarOpen ? "up" : "down", Width = 14, Height = 14, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get("Muted") };
        Grid.SetColumn(chevron, 2); grid.Children.Add(chevron);
        CalendarLine.Content = grid;
        AutomationProperties.SetName(CalendarLine, string.Join(". ", text.Children.OfType<TextBlock>().Select(x => x.Text)));
        AutomationProperties.SetHelpText(CalendarLine, _calendarOpen ? L("Masquer la liste", "Hide the list") : L("Voir la liste", "Show the list"));
        CalendarList.Visibility = _calendarOpen ? Visibility.Visible : Visibility.Collapsed;
        CalendarList.Children.Clear();
        if (!_calendarOpen) return;
        CalendarList.Children.Add(new TextBlock { Text = L("D’après TheTVDB, que ton serveur Jellyfin consulte avec son extension : ces épisodes ne sont pas dans ta bibliothèque.", "From TheTVDB, which your Jellyfin server checks with its plugin: these episodes aren’t in your library."), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 0, 0, 4) });
        void Line(MediaItem x, string when)
        {
            var line = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var day = new TextBlock { Text = when, Foreground = Palette.Get("Muted"), Margin = new Thickness(16, 0, 0, 0) };
            DockPanel.SetDock(day, Dock.Right);
            line.Children.Add(day);
            line.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { EpisodeCalendar.Code(x), x.Name }.Where(y => !string.IsNullOrEmpty(y))), Foreground = Palette.Get("TextBody"), TextTrimming = TextTrimming.CharacterEllipsis });
            CalendarList.Children.Add(line);
        }
        if (missing.Count > 0)
        {
            CalendarList.Children.Add(Heading(missing.Count == 1 ? L("Manquant", "Missing") : L("Manquants", "Missing")));
            foreach (var x in missing.Take(30)) Line(x, L($"Sorti {EpisodeCalendar.DayText(EpisodeCalendar.AirDay(x)!.Value, today)}", $"Aired {EpisodeCalendar.DayText(EpisodeCalendar.AirDay(x)!.Value, today)}"));
            if (missing.Count > 30) CalendarList.Children.Add(Description(L($"Et {Lang.Count(missing.Count - 30, "autre", "autres", "more", "more")}", $"And {Lang.Count(missing.Count - 30, "autre", "autres", "more", "more")}"), 8));
        }
        if (coming.Count > 0)
        {
            CalendarList.Children.Add(Heading(L("À venir", "Coming up")));
            foreach (var x in coming.Take(12)) Line(x, EpisodeCalendar.DayTitle(EpisodeCalendar.AirDay(x)!.Value, today));
        }
    }

    private void CalendarLine_Click(object sender, RoutedEventArgs e) { _calendarOpen = !_calendarOpen; RenderCalendar(); }

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
