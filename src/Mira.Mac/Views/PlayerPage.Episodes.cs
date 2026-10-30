using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// The episodes in the player, as on Windows (MainWindow.PlayerEpisodes.cs): « Épisodes » lists the season of the one
// playing (thumbnails, watched, progress), a click plays another; « Épisode précédent » sits beside « Suivant ». In a
// group, both go through it (PlayNextAsync).
public sealed partial class PlayerPage
{
    private Button _previous = null!, _episodesButton = null!;
    private List<EpisodePage> _episodePages = [];
    private int _episodePage;
    /// <summary>For the self-check.</summary>
    public Button EpisodesButton => _episodesButton;
    public Button PreviousButton => _previous;

    /// <summary>« Épisode précédent » and « Épisodes », built with the bar.</summary>
    private void BuildEpisodeButtons()
    {
        _previous = Ui.IconButton("previous-episode", L("Épisode précédent · P", "Previous episode · P"), PlayPrevious, "player", 22);
        _episodesButton = Ui.IconButton("episodes", L("Épisodes · E", "Episodes · E"), () => { }, "player", 22);
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        flyout.Opening += (_, _) => { flyout.Content = EpisodesMenu(opening: true); };
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => CurrentEpisodeRow()?.BringIntoView(), DispatcherPriority.Loaded);
        _episodesButton.Flyout = flyout;
        _previous.IsEnabled = _episodesButton.IsEnabled = false;
    }
    /// <summary>Only an episode has them, as « Suivant ».</summary>
    private void ShowEpisodeButtons(MediaItem item)
    {
        _next.IsVisible = _previous.IsVisible = _episodesButton.IsVisible = item.Type == "Episode";
        _previous.IsEnabled = _episodesButton.IsEnabled = false;
    }
    /// <summary>The series' episodes, once the player has them (LoadMarkersAsync).</summary>
    private void SetEpisodes(MediaItem current)
    {
        var previous = PreviousEpisode(current);
        _previous.IsEnabled = previous is not null;
        ToolTip.SetTip(_previous, previous is null ? L("Premier épisode disponible", "First available episode") : L("Épisode précédent · ", "Previous episode · ") + PlayerText.Subtitle(previous, _episodes));
        _episodesButton.IsEnabled = _episodes.Count > 0;
    }
    private MediaItem? PreviousEpisode(MediaItem current)
    {
        var index = _episodes.FindIndex(x => x.Id == current.Id);
        return index > 0 ? _episodes[index - 1] : null;
    }
    private void PlayPrevious()
    {
        if (_session.Item is { } current && PreviousEpisode(current) is { } previous) _ = PlayNextAsync(previous);
    }
    private void ToggleEpisodes()
    {
        if (_mini || !_episodesButton.IsVisible || !_episodesButton.IsEnabled || _episodesButton.Flyout is not Flyout flyout) return;
        if (flyout.IsOpen) flyout.Hide(); else { ShowControls(); flyout.ShowAt(_episodesButton); }
    }
    /// <summary>Opens on the page of the episode playing; the arrows go from season to season (100 episodes at most).</summary>
    private Control EpisodesMenu(bool opening = false)
    {
        if (opening)
        {
            _episodePages = EpisodePages.From(_episodes);
            var holding = EpisodePages.Holding(_episodePages, _episodes, _session.Item);
            _episodePage = holding is null ? 0 : Math.Max(0, _episodePages.IndexOf(holding));
        }
        var page = _episodePages.ElementAtOrDefault(_episodePage);
        Button Step(string icon, string tip, int delta)
        {
            var step = Ui.IconButton(icon, tip, () =>
            {
                _episodePage = Math.Clamp(_episodePage + delta, 0, Math.Max(0, _episodePages.Count - 1));
                if (_episodesButton.Flyout is Flyout { IsOpen: true } open) open.Content = EpisodesMenu();
            }, "player", 17);
            step.IsEnabled = delta < 0 ? _episodePage > 0 : _episodePage < _episodePages.Count - 1;
            step.IsVisible = _episodePages.Count > 1;
            step.Width = step.Height = 32; step.Padding = new Thickness(0);
            return step;
        }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(4, 2, 4, 6) };
        var back = Step("back", L("Saison précédente", "Previous season"), -1);
        var forward = Step("next", L("Saison suivante", "Next season"), 1); Grid.SetColumn(forward, 2);
        var label = new TextBlock { Text = page?.Label ?? L("Épisodes", "Episodes"), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        header.Children.Add(back); header.Children.Add(label); header.Children.Add(forward);
        var list = new StackPanel { Spacing = 2 };
        if (page is null) list.Children.Add(MenuNote(L("Aucun épisode disponible.", "No episodes available.")));
        else foreach (var episode in EpisodePages.Episodes(_episodes, page)) list.Children.Add(EpisodeRow(episode));
        var scroll = new ScrollViewer { Content = list, MaxHeight = 460, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return new DockPanel { Width = 380, Children = { Docked(header), scroll } };
        static Control Docked(Control top) { DockPanel.SetDock(top, Dock.Top); return top; }
    }
    private Button EpisodeRow(MediaItem episode)
    {
        var playing = episode.Id == _session.Item?.Id;
        var image = new Image { Stretch = Stretch.UniformToFill };
        var thumb = new Grid { Width = 112, Height = 63 };
        thumb.Children.Add(new Border { Background = Ui.Brush("#222225") });
        thumb.Children.Add(image);
        if (episode.Progress > 0 && !episode.UserData.Played) thumb.Children.Add(new Border { Height = 3, Width = 112 * episode.Progress, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
        var art = new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = thumb };
        var title = episode.IndexNumber is { } number ? $"{number}. {episode.Name}" : episode.Name;
        var state = playing ? L("En lecture", "Playing") : episode.UserData.Played ? L("✓ Vu", "✓ Watched") : episode.Progress > 0 ? L("En cours", "In progress") : null;
        var details = string.Join(" · ", new[] { episode.DurationLabel, state }.Where(x => !string.IsNullOrEmpty(x)));
        var text = Ui.Column(4,
            new TextBlock { Text = title, FontSize = 13.5, FontWeight = playing ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
            new TextBlock { Text = details, FontSize = 12, Foreground = episode.UserData.Played && !playing ? Ui.Brush("#A0C7A8") : Ui.Muted });
        text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Thickness(12, 0, 0, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(art); Grid.SetColumn(text, 1); grid.Children.Add(text);
        var row = new Button { Content = grid, Classes = { "quiet" }, Tag = episode.Id, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 10, 6) };
        if (playing) row.Background = Ui.Brush("#26262A");
        Avalonia.Automation.AutomationProperties.SetName(row, L($"Épisode {episode.IndexNumber?.ToString() ?? ""} : {episode.Name}, {episode.DurationLabel}{(playing ? ", en lecture" : episode.UserData.Played ? ", vu" : episode.Progress > 0 ? ", en cours" : "")}",
            $"Episode {episode.IndexNumber?.ToString() ?? ""}: {episode.Name}, {episode.DurationLabel}{(playing ? ", playing" : episode.UserData.Played ? ", watched" : episode.Progress > 0 ? ", in progress" : "")}"));
        row.Click += (_, _) =>
        {
            (_episodesButton.Flyout as Flyout)?.Hide();
            if (!playing) _ = PlayNextAsync(episode);
        };
        if (_shell.Session is { } session) Ui.LoadWhenVisible(row, image, () => session.Images.EpisodeAsync(episode, 224));
        return row;
    }
    /// <summary>The episode playing, else the first of the page: brought into view when the menu opens.</summary>
    private Button? CurrentEpisodeRow()
    {
        if (_episodesButton.Flyout is not Flyout { Content: DockPanel { Children: [_, ScrollViewer { Content: StackPanel list }] } }) return null;
        var rows = list.Children.OfType<Button>().ToList();
        return rows.FirstOrDefault(x => Equals(x.Tag, _session.Item?.Id)) ?? rows.FirstOrDefault();
    }
}
