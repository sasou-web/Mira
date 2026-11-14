using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
        flyout.Opening += (_, _) => { flyout.Content = EpisodesMenu(opening: true); AboveBar(flyout, _episodesButton); };
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
        if (PlayingTitle is { } current && PreviousEpisode(current) is { } previous) _ = PlayNextAsync(previous);
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
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(4, 4, 4, 8) };
        var back = Step("back", L("Saison précédente", "Previous season"), -1);
        var forward = Step("next", L("Saison suivante", "Next season"), 1); Grid.SetColumn(forward, 2);
        var label = new TextBlock { Text = page?.Label ?? L("Épisodes", "Episodes"), FontSize = 14.5, FontWeight = FontWeight.Bold, Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        header.Children.Add(back); header.Children.Add(label); header.Children.Add(forward);
        var list = new StackPanel { Spacing = 2 };
        if (page is null) list.Children.Add(MenuNote(L("Aucun épisode disponible.", "No episodes available.")));
        else foreach (var episode in EpisodePages.Episodes(_episodes, page)) list.Children.Add(EpisodeRow(episode));
        var scroll = new ScrollViewer { Content = list, MaxHeight = 460, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return new DockPanel { Width = 432, Children = { Docked(header), scroll } };
        static Control Docked(Control top) { DockPanel.SetDock(top, Dock.Top); return top; }
    }
    private Button EpisodeRow(MediaItem episode)
    {
        var playing = episode.Id == _session.Item?.Id;
        var hidden = Spoilers.Hides(episode, _shell.Settings.HideSpoilers) && !playing;
        var image = new Image { Stretch = Stretch.UniformToFill };
        if (hidden) image.Source = BlurHash.Of(episode, Spoilers.Still(episode)) is { } hash ? Cards.Blur(hash, 16 / 9d) : null;
        // « Écrin », as on Windows: a wider still, a full bar and a check on it once watched; the episode playing raised,
        // « Playing » and what is left of it under its name.
        var thumb = new Grid { Width = 124, Height = 70 };
        thumb.Children.Add(new Border { Background = Ui.Raised2 });
        thumb.Children.Add(image);
        var progress = episode.UserData.Played && !playing ? 1 : playing ? PlayingProgress() : episode.Resumable ? episode.Progress : 0;
        if (progress > 0)
        {
            thumb.Children.Add(new Border { Height = 3, Background = Ui.Brush("#33FFFFFF"), VerticalAlignment = VerticalAlignment.Bottom });
            thumb.Children.Add(new Border { Height = 3, Width = 124 * Math.Clamp(progress, 0, 1), Background = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
        }
        if (episode.UserData.Played && !playing)
            thumb.Children.Add(new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Ui.Brush("#B3000000"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0),
                Child = new Icon("check", 11) { Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        var art = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = thumb };
        var title = new TextBlock { FontSize = 14, LineHeight = 19, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = playing ? FontWeight.Bold : FontWeight.SemiBold, Foreground = playing ? Ui.Ink : Ui.TextBody };
        title.Inlines = [];
        if (episode.IndexNumber is { } number) { title.Inlines.Add(new Run(number.ToString(Culture)) { Foreground = Ui.Faint }); title.Inlines.Add(new Run("  ")); }
        title.Inlines.Add(new Run(episode.Name));
        var details = new TextBlock { FontSize = 12.5, Foreground = Ui.Faint, TextTrimming = TextTrimming.CharacterEllipsis };
        details.Inlines = [];
        if (playing)
        {
            details.Inlines.Add(new Run(L("En lecture", "Playing")) { FontWeight = FontWeight.Bold, Foreground = Ui.Ink });
            if (PlayingLeft() is { } left) details.Inlines.Add(new Run(" · " + left));
        }
        else details.Inlines.Add(new Run(string.Join(" · ", new[] { episode.DurationLabel, episode.UserData.Played ? L("Vu", "Watched") : episode.Progress > 0 ? L("En cours", "In progress") : null }.Where(x => !string.IsNullOrEmpty(x)))));
        var text = Ui.Column(4, title, details);
        text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Thickness(14, 0, 0, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(art); Grid.SetColumn(text, 1); grid.Children.Add(text);
        var row = new Button { Content = grid, Classes = { "menurow" }, Tag = episode.Id, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8), CornerRadius = new CornerRadius(12) };
        row.Classes.Set("on", playing);
        Avalonia.Automation.AutomationProperties.SetName(row, L($"Épisode {episode.IndexNumber?.ToString() ?? ""} : {episode.Name}, {episode.DurationLabel}{(playing ? ", en lecture" : episode.UserData.Played ? ", vu" : episode.Progress > 0 ? ", en cours" : "")}",
            $"Episode {episode.IndexNumber?.ToString() ?? ""}: {episode.Name}, {episode.DurationLabel}{(playing ? ", playing" : episode.UserData.Played ? ", watched" : episode.Progress > 0 ? ", in progress" : "")}"));
        row.Click += (_, _) =>
        {
            (_episodesButton.Flyout as Flyout)?.Hide();
            if (!playing) _ = PlayNextAsync(episode);
        };
        if (_shell.Session is { } session && !hidden) Ui.LoadWhenVisible(row, image, () => session.Images.EpisodeAsync(episode, 224));
        return row;
    }
    /// <summary>How far the episode playing is, from mpv.</summary>
    private double PlayingProgress() => _session.Duration > 0 ? _session.Position / _session.Duration : 0;
    /// <summary>What is left of the episode playing (« 12 min left »), from mpv.</summary>
    private string? PlayingLeft() => _session.Duration > 0 ? MediaQuality.Left(TimeSpan.FromSeconds(Math.Max(0, _session.Duration - _session.Position))) : null;
    /// <summary>The episode playing, else the first of the page: brought into view when the menu opens.</summary>
    private Button? CurrentEpisodeRow()
    {
        if (_episodesButton.Flyout is not Flyout { Content: DockPanel { Children: [_, ScrollViewer { Content: StackPanel list }] } }) return null;
        var rows = list.Children.OfType<Button>().ToList();
        return rows.FirstOrDefault(x => Equals(x.Tag, _session.Item?.Id)) ?? rows.FirstOrDefault();
    }
}
