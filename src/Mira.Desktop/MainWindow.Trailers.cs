using System.Windows;
using System.Windows.Automation;
using Mira.Core;

namespace Mira.Desktop;

/// <summary>
/// « Bande-annonce » on a title's page and « Bande-annonce avant le film » (<see cref="Trailers"/>): a trailer kept on
/// the server plays in Mira's player, from Jellyfin alone; one on the web opens in the browser at the click, never in Mira.
/// </summary>
public partial class MainWindow
{
    /// <summary>Shown when the title has a trailer, its tooltip saying where it leads (the browser for one on the web).</summary>
    private void ShowTrailerButton(MediaItem item)
    {
        var source = _demo ? TrailerSource.None : Trailers.Source(item);
        TrailerButton.Visibility = source == TrailerSource.None ? Visibility.Collapsed : Visibility.Visible;
        if (source == TrailerSource.None) return;
        var tip = Trailers.Tip(item);
        TrailerButton.ToolTip = tip; AutomationProperties.SetName(TrailerButton, tip);
    }

    private async void Trailer_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } item) return;
        switch (Trailers.Source(item))
        {
            case TrailerSource.Jellyfin:
                if (_client is not { } client) return;
                try
                {
                    var trailers = await client.LocalTrailersAsync(item.Id);
                    if (!ReferenceEquals(_detail, item) && _detail?.Id != item.Id) return;
                    if (trailers.FirstOrDefault() is { } trailer) await PlayAsync(trailer, chosen: true);
                    else SetNotice(L("Jellyfin ne trouve plus la bande-annonce de ce titre.", "Jellyfin can no longer find this title’s trailer."));
                }
                catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
                break;
            case TrailerSource.Web when Trailers.WebLink(item) is { } link:
                OpenWebPage(link.AbsoluteUri);
                break;
        }
    }

    /// <summary>
    /// The trailer to play before <paramref name="film"/> with « Bande-annonce avant le film » on, asked to Jellyfin; null
    /// when there is none, or Jellyfin did not answer in time: the film starts as usual, never held up by its trailer.
    /// </summary>
    private async Task<MediaItem?> TrailerBeforeAsync(MediaItem film, bool fromStart)
    {
        if (_client is not { } client) return null;
        var startAt = PlaybackSession.StartAt(film, remember: _settings.RememberPosition && !fromStart, back: _settings.ResumeBack);
        if (!Trailers.PlaysFirst(_settings.TrailerFirst, film, startAt, InGroup, KeptCopy(film.Id) is not null)) return null;
        try { return (await client.LocalTrailersAsync(film.Id).WaitAsync(TimeSpan.FromSeconds(4))).FirstOrDefault(); }
        catch (Exception ex) when (IsExpected(ex) || ex is TimeoutException) { return null; }
    }

    /// <summary>
    /// The film after its trailer (its end, or « Passer la bande-annonce »): from its first second, as the trailer only
    /// plays then (<see cref="Trailers.PlaysFirst"/>). Without it, « Depuis le début » on a film left halfway played the
    /// trailer, then resumed the film where it was left.
    /// </summary>
    private Task FilmAfterTrailerAsync(MediaItem film, bool keepMini) => PlayAsync(film, keepMini, fromStart: true, trailerDone: true);
}
