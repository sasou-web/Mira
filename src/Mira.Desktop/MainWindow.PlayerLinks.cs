using System.Windows;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Desktop;

/// <summary>
/// The player's title and the line under it open their page: the series (or the film) from the title, the episode's
/// season, scrolled to it, from the line. The video goes on in the mini-player, over the page.
/// </summary>
public partial class MainWindow
{
    private void ShowPlayingLinks(MediaItem item)
    {
        var episode = item.Type == "Episode" && item.SeriesId is not null;
        PlayingTitle.ToolTip = episode ? L("Voir la série", "View show") : L("Voir la fiche", "View details");
        PlayingSubtitle.ToolTip = episode ? L("Voir l’épisode dans sa saison", "View the episode in its season") : L("Voir la fiche", "View details");
    }
    private async void PlayingTitle_Click(object sender, RoutedEventArgs e) => await OpenPlayingPageAsync(atEpisode: false);
    private async void PlayingSubtitle_Click(object sender, RoutedEventArgs e) => await OpenPlayingPageAsync(atEpisode: true);
    private async Task OpenPlayingPageAsync(bool atEpisode)
    {
        if (!_playing || _session.Item is not { } item) return;
        if (!_miniPlayer) SetMiniPlayerSmoothly(true);
        try
        {
            var page = await PageItemAsync(item);
            if (!_playing || _session.Item?.Id != item.Id) return;
            await ShowDetailsAsync(page, atEpisode && item.Type == "Episode" ? item : null);
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }
}
