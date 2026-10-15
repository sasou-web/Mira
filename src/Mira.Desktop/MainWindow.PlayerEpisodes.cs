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
        PreviousButton.ToolTip = previous is null ? "Premier épisode disponible" : "Épisode précédent · " + PlayerText.Subtitle(previous, episodes);
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
        EpisodePageLabel.Text = page?.Label ?? "Épisodes";
        EpisodePageBack.IsEnabled = _playerPage > 0; EpisodePageNext.IsEnabled = _playerPage < _playerPages.Count - 1;
        EpisodePageBack.Visibility = EpisodePageNext.Visibility = _playerPages.Count > 1 ? Visibility.Visible : Visibility.Hidden;
        if (page is null) { EpisodeList.Children.Add(new TextBlock { Text = "Aucun épisode disponible.", Margin = new Thickness(10, 8, 10, 8), Foreground = Palette.Get("Muted") }); return; }
        foreach (var episode in EpisodePages.Episodes(_playerEpisodes, page)) EpisodeList.Children.Add(EpisodeRow(episode));
    }
    private Button EpisodeRow(MediaItem episode)
    {
        var playing = episode.Id == _session.Item?.Id;
        var row = new Button { Style = (Style)FindResource("PlayerMenuItem"), Padding = new Thickness(8, 6, 10, 6), Tag = episode.Id };
        if (playing) row.Background = Palette.Get("Raised");
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var thumb = new Grid { Width = 112, Height = 63, Clip = new RectangleGeometry(new Rect(0, 0, 112, 63), 4, 4), Background = Palette.Get("Raised2") };
        var image = new Image { Stretch = Stretch.UniformToFill }; thumb.Children.Add(image);
        if (episode.Progress > 0 && !episode.UserData.Played) thumb.Children.Add(new Border { Height = 3, Width = 112 * episode.Progress, Background = Palette.Get("Text"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
        grid.Children.Add(thumb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(text, 1);
        var title = episode.IndexNumber is { } number ? $"{number}. {episode.Name}" : episode.Name;
        text.Children.Add(new TextBlock { Text = title, FontWeight = playing ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Palette.Get("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        var state = playing ? "En lecture" : episode.UserData.Played ? "✓ Vu" : episode.Progress > 0 ? "En cours" : null;
        var details = string.Join(" · ", new[] { episode.DurationLabel, state }.Where(x => !string.IsNullOrEmpty(x)));
        text.Children.Add(new TextBlock { Text = details, FontSize = 12, Foreground = Palette.Get(episode.UserData.Played && !playing ? "Success" : "Muted"), Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(text);
        row.Content = grid;
        AutomationProperties.SetName(row, $"Épisode {episode.IndexNumber?.ToString() ?? ""} : {episode.Name}, {episode.DurationLabel}{(playing ? ", en lecture" : episode.UserData.Played ? ", vu" : episode.Progress > 0 ? ", en cours" : "")}");
        row.Click += async (_, _) =>
        {
            PlayerOptionsPopup.IsOpen = false;
            if (!playing) await PlayAsync(episode, _miniPlayer);
        };
        if (_images is not null) _ = LoadEpisodeImageAsync(image, episode);
        return row;
    }
    /// <summary>The episode playing, else the first of the page: what the keyboard starts from.</summary>
    private Button? CurrentEpisodeRow() =>
        EpisodeList.Children.OfType<Button>().FirstOrDefault(x => Equals(x.Tag, _session.Item?.Id)) ?? EpisodeList.Children.OfType<Button>().FirstOrDefault();
    /// <summary>Brought into view once the menu has its size: a long season opens on the episode playing.</summary>
    private void ShowCurrentEpisodeRow() => Dispatcher.BeginInvoke(() => CurrentEpisodeRow()?.BringIntoView(), DispatcherPriority.Loaded);
}
