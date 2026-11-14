using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// The episodes in the player: « Épisodes » lists the season of the one playing (thumbnails, watched, progress), a click
// plays another; « Épisode précédent » sits beside « Suivant ». In a group, both go through it (PlayAsync).
public partial class MainWindow
{
    private List<MediaItem> _playerEpisodes = [];
    private List<EpisodePage> _playerPages = [];
    private int _playerPage;

    /// <summary>The series' episodes, once the player has them (LoadServerMarkersAsync); none for a film or without Jellyfin.</summary>
    private void SetPlayerEpisodes(MediaItem current, List<MediaItem> episodes)
    {
        _playerEpisodes = episodes;
        var previous = PreviousEpisode(current);
        PreviousButton.IsEnabled = previous is not null;
        PreviousButton.ToolTip = previous is null ? L("Premier épisode disponible", "First available episode") : L("Épisode précédent · ", "Previous episode · ") + PlayerText.Subtitle(previous, episodes);
        PlayerEpisodesButton.IsEnabled = episodes.Count > 0;
    }
    private MediaItem? PreviousEpisode(MediaItem current)
    {
        var index = _playerEpisodes.FindIndex(x => x.Id == current.Id);
        return index > 0 ? _playerEpisodes[index - 1] : null;
    }
    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Item is not { } current || PreviousEpisode(current) is not { } previous) return;
        await PlayAsync(previous, _miniPlayer);
    }
    private void Episodes_Click(object sender, RoutedEventArgs e) => TogglePlayerMenu(PlayerEpisodesButton);
    /// <summary>Opens on the page of the episode playing.</summary>
    private void BuildEpisodesMenu()
    {
        _playerPages = EpisodePages.From(_playerEpisodes);
        var holding = EpisodePages.Holding(_playerPages, _playerEpisodes, _session.Item);
        _playerPage = holding is null ? 0 : Math.Max(0, _playerPages.IndexOf(holding));
        RenderEpisodePage();
    }
    private void EpisodePage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string step } || !int.TryParse(step, out var delta)) return;
        var page = Math.Clamp(_playerPage + delta, 0, Math.Max(0, _playerPages.Count - 1));
        if (page == _playerPage) return;
        _playerPage = page; RenderEpisodePage(); EpisodeListScroll.ScrollToTop();
        // The arrow just used may have gone (first or last page): the keyboard stays in the menu.
        if (!((Button)sender).IsEnabled) EpisodeList.Children.OfType<Button>().FirstOrDefault()?.Focus();
    }
    private void RenderEpisodePage()
    {
        EpisodeList.Children.Clear();
        var page = _playerPages.ElementAtOrDefault(_playerPage);
        EpisodePageLabel.Text = page?.Label ?? L("Épisodes", "Episodes");
        EpisodePageBack.IsEnabled = _playerPage > 0; EpisodePageNext.IsEnabled = _playerPage < _playerPages.Count - 1;
        EpisodePageBack.Visibility = EpisodePageNext.Visibility = _playerPages.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        if (page is null) { EpisodeList.Children.Add(new TextBlock { Text = L("Aucun épisode disponible.", "No episodes available."), Margin = new Thickness(10, 8, 10, 8), Foreground = Palette.Get("Muted") }); return; }
        foreach (var episode in EpisodePages.Episodes(_playerEpisodes, page)) EpisodeList.Children.Add(EpisodeRow(episode));
    }
    /// <summary>
    /// A row of the season (« Écrin »): the picture, a check on it and its bar full once watched, the bar of a title begun;
    /// the number in the faint ink before the name; the episode playing raised, « Playing » and what is left of it.
    /// </summary>
    private Button EpisodeRow(MediaItem episode)
    {
        var playing = episode.Id == _session.Item?.Id;
        var watched = episode.UserData.Played && !playing;
        var row = new Button { Style = (Style)FindResource("PlayerMenuItem"), Padding = new Thickness(8), Tag = episode.Id };
        Motion.SetRadius(row, Palette.Card);
        if (playing) row.Background = Palette.Get("Raised");
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var thumb = new Grid { Width = 124, Height = 70, Clip = new RectangleGeometry(new Rect(0, 0, 124, 70), 8, 8), Background = Palette.Get("Raised2") };
        var hidden = Spoilers.Hides(episode, _settings.HideSpoilers) && !playing;
        var image = new Image { Stretch = Stretch.UniformToFill }; thumb.Children.Add(image);
        if (hidden) image.Source = BlurHash.Of(episode, Spoilers.Still(episode)) is { } hash ? MediaCard.Blur(hash, 16 / 9d) : null;
        var progress = watched ? 1 : playing ? PlayingProgress(episode) : episode.Resumable ? episode.Progress : 0;
        if (progress > 0)
        {
            var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Palette.Get("HairlineStrong") };
            bar.Children.Add(new Border { Width = 124 * Math.Clamp(progress, 0, 1), Background = Palette.Get("Text"), HorizontalAlignment = HorizontalAlignment.Left });
            thumb.Children.Add(bar);
        }
        if (watched)
            thumb.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Palette.Get("Scrim"), Opacity = .7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0) });
        if (watched)
            thumb.Children.Add(new Icon { Kind = "check", Width = 11, Height = 11, Foreground = Palette.Get("Text"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9.5, 9.5, 0) });
        grid.Children.Add(thumb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) }; Grid.SetColumn(text, 1);
        var title = new TextBlock { FontSize = 14, LineHeight = 19, FontWeight = playing ? FontWeights.Bold : FontWeights.SemiBold, Foreground = Palette.Get(playing ? "Text" : "TextBody"), TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis };
        if (episode.IndexNumber is { } number) title.Inlines.Add(new System.Windows.Documents.Run(number.ToString(Lang.Culture)) { Foreground = Palette.Get("Faint") });
        if (episode.IndexNumber is not null) title.Inlines.Add(new System.Windows.Documents.Run("  "));
        title.Inlines.Add(new System.Windows.Documents.Run(episode.Name));
        text.Children.Add(title);
        var details = new TextBlock { FontSize = 12.5, Foreground = Palette.Get("Faint"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        if (playing)
        {
            details.Inlines.Add(new System.Windows.Documents.Run(L("En lecture", "Playing")) { Foreground = Palette.Get("Text"), FontWeight = FontWeights.Bold });
            if (PlayingLeft() is { } left) details.Inlines.Add(new System.Windows.Documents.Run(" · " + left));
        }
        else details.Text = string.Join(" · ", new[] { episode.DurationLabel, watched ? L("Vu", "Watched") : episode.Progress > 0 ? L("En cours", "In progress") : null }.Where(x => !string.IsNullOrEmpty(x)));
        text.Children.Add(details);
        grid.Children.Add(text);
        row.Content = grid;
        AutomationProperties.SetName(row, L($"Épisode {episode.IndexNumber?.ToString() ?? ""} : {episode.Name}, {episode.DurationLabel}{(playing ? ", en lecture" : episode.UserData.Played ? ", vu" : episode.Progress > 0 ? ", en cours" : "")}",
            $"Episode {episode.IndexNumber?.ToString() ?? ""}: {episode.Name}, {episode.DurationLabel}{(playing ? ", playing" : episode.UserData.Played ? ", watched" : episode.Progress > 0 ? ", in progress" : "")}"));
        row.Click += async (_, _) =>
        {
            PlayerOptionsPopup.IsOpen = false;
            if (!playing) await PlayAsync(episode, _miniPlayer);
        };
        if (_images is not null && !hidden) _ = LoadEpisodeImageAsync(image, episode);
        return row;
    }
    /// <summary>How far the episode playing is, read off mpv (Jellyfin's progress is the one it was opened at).</summary>
    private double PlayingProgress(MediaItem episode)
    {
        if (_mpv is not { } mpv) return episode.Progress;
        var duration = mpv.Number("duration"); var position = mpv.Number("time-pos");
        return duration > 0 && position >= 0 ? position / duration : episode.Progress;
    }
    /// <summary>« 26 min left » of the episode playing; null before mpv knows its length.</summary>
    private string? PlayingLeft()
    {
        if (_mpv is not { } mpv) return null;
        var duration = mpv.Number("duration"); var position = mpv.Number("time-pos");
        return duration > 0 && position >= 0 ? MediaQuality.Left(TimeSpan.FromSeconds(Math.Max(0, duration - position))) : null;
    }
    /// <summary>The episode playing, else the first of the page: what the keyboard starts from.</summary>
    private Button? CurrentEpisodeRow() =>
        EpisodeList.Children.OfType<Button>().FirstOrDefault(x => Equals(x.Tag, _session.Item?.Id)) ?? EpisodeList.Children.OfType<Button>().FirstOrDefault();
    /// <summary>Brought into view once the menu has its size: a long season opens on the episode playing.</summary>
    private void ShowCurrentEpisodeRow() => Dispatcher.BeginInvoke(() => CurrentEpisodeRow()?.BringIntoView(), DispatcherPriority.Loaded);
}
