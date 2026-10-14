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
        var version = ++_reportVersion; var year = Choice(ReportPeriod) == "year"; var period = year ? "cette année" : "ce mois-ci";
        ReportBody.Children.Clear(); ReportTime.Text = "";
        if (_demo || _client is not { } client) { ReportCounts.Text = "Ton bilan se calcule avec ton compte Jellyfin."; return; }
        ReportCounts.Text = "Calcul de ton bilan…";
        try
        {
            var from = WatchReport.Start(DateTimeOffset.Now, year);
            var (played, series) = await client.PlayedSinceAsync(from, _connectionLifetime.Token);
            if (version != _reportVersion) return;
            var report = WatchReport.Build(played, series, from);
            if (report.Empty) { ReportCounts.Text = $"Aucun film ni épisode terminé {period} pour l’instant."; return; }
            ReportTime.Text = WatchReport.Time(report.Minutes);
            ReportCounts.Text = $"devant l’écran {period} · " + string.Join(" · ", new[] { report.Films > 0 ? WatchReport.Count(report.Films, "film", "films") : null, report.Episodes > 0 ? WatchReport.Count(report.Episodes, "épisode", "épisodes") : null }.OfType<string>());
            Part("SÉRIES LES PLUS REGARDÉES", report.Series.Select(x => (x.Name, $"{WatchReport.Count(x.Episodes, "épisode", "épisodes")} · {WatchReport.Time(x.Minutes)}")));
            Part(report.Finished.Count > 1 ? "SÉRIES TERMINÉES" : "SÉRIE TERMINÉE", report.Finished.Select(x => (x, "")));
            if (report.Genres.Count > 0) Part("GENRES PRÉFÉRÉS", [(string.Join(" · ", report.Genres), "")]);
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
