using System.Windows;
using System.Windows.Controls;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Ton bilan » (Réglages → Ton bilan, as in Mira web and on Mac): the month or the year from Jellyfin's record of what
/// this account played to the end (<see cref="WatchReport"/>).
/// </summary>
public partial class MainWindow
{
    private int _reportVersion;
    private void ReportPeriod_Changed(object sender, SelectionChangedEventArgs e) { if (!_initializing && ReportSettings.IsVisible) _ = ShowReportAsync(); }
    private async Task ShowReportAsync()
    {
        var version = ++_reportVersion; var year = Choice(ReportPeriod) == "year"; var period = year ? L("cette année", "this year") : L("ce mois-ci", "this month");
        ReportBody.Children.Clear(); ReportTime.Text = "";
        if (_demo || _client is not { } client) { ReportCounts.Text = L("Ton bilan se calcule avec ton compte Jellyfin.", "Your recap is worked out from your Jellyfin account."); return; }
        ReportCounts.Text = L("Calcul de ton bilan…", "Working out your recap…");
        try
        {
            var from = WatchReport.Start(DateTimeOffset.Now, year);
            var (played, series) = await client.PlayedSinceAsync(from, _connectionLifetime.Token);
            if (version != _reportVersion) return;
            var report = WatchReport.Build(played, series, from);
            if (report.Empty) { ReportCounts.Text = L($"Aucun film ni épisode terminé {period} pour l’instant.", $"No movie or episode finished {period} yet."); return; }
            ReportTime.Text = WatchReport.Time(report.Minutes);
            ReportCounts.Text = L($"devant l’écran {period} · ", $"watched {period} · ") + string.Join(" · ", new[] { report.Films > 0 ? WatchReport.Count(report.Films, L("film", "movie"), L("films", "movies")) : null, report.Episodes > 0 ? WatchReport.Count(report.Episodes, L("épisode", "episode"), L("épisodes", "episodes")) : null }.OfType<string>());
            Part(L("SÉRIES LES PLUS REGARDÉES", "MOST WATCHED SHOWS"), report.Series.Select(x => (x.Name, $"{WatchReport.Count(x.Episodes, L("épisode", "episode"), L("épisodes", "episodes"))} · {WatchReport.Time(x.Minutes)}")));
            Part(report.Finished.Count > 1 ? L("SÉRIES TERMINÉES", "FINISHED SHOWS") : L("SÉRIE TERMINÉE", "FINISHED SHOW"), report.Finished.Select(x => (x, "")));
            if (report.Genres.Count > 0) Part(L("GENRES PRÉFÉRÉS", "FAVORITE GENRES"), [(string.Join(" · ", report.Genres), "")]);
        }
        catch (Exception ex) when (IsExpected(ex) && !_connectionLifetime.IsCancellationRequested)
        {
            if (version == _reportVersion) ReportCounts.Text = Friendly(ex);
        }
        void Part(string title, IEnumerable<(string Name, string Detail)> lines)
        {
            var rows = lines.ToList();
            if (rows.Count == 0) return;
            ReportBody.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Faint"), Margin = new Thickness(0, 22, 0, 8) });
            foreach (var (name, detail) in rows)
            {
                var line = new DockPanel { Margin = new Thickness(0, 3, 30, 3) };
                if (detail.Length > 0) { var right = new TextBlock { Text = detail, Foreground = Palette.Get("Muted") }; DockPanel.SetDock(right, Dock.Right); line.Children.Add(right); }
                line.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 16, 0) });
                ReportBody.Children.Add(line);
            }
        }
    }
}
