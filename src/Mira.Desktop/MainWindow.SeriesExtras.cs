using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// A series' bonus videos, « La pastille » (SeriesExtras, mock-up B chosen by ogsas on 2026-11-18): a pill « Extras » after
// the seasons shows them in place of the episodes, by kind, one row each with « Show all », the episodes' tiles. A film
// keeps its row « Bonus » under its facts (LoadExtrasAsync).
public partial class MainWindow
{
    /// <summary>The tag of the hidden list's item and of the pill that show the bonus videos instead of a season.</summary>
    private static readonly object ExtrasPage = new();
    // The groups opened past their first row, and how many tiles a row held when they were drawn.
    private readonly HashSet<string> _extrasOpen = [];
    private int _extrasColumns;

    private bool ExtrasShown => ReferenceEquals((SeasonSelector.SelectedItem as ComboBoxItem)?.Tag, ExtrasPage);
    private bool SeriesExtrasPill => _detail?.Type == "Series" && _extras.Count > 0;

    /// <summary>« Extras » after the seasons, a thin line between them.</summary>
    private void AddExtrasPill()
    {
        SeasonPills.Children.Add(new Border { Width = 1, Height = 22, Background = Palette.Get("Line"), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
        // No background of its own, as the seasons' pills: the row's white pill is its body once shown.
        var pill = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(16, 8, 16, 8), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Tag = ExtrasPage };
        Motion.SetRadius(pill, new CornerRadius(18));
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = SeriesExtras.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        pill.Content = label;
        if (SeasonPills.Compact) { pill.Height = PillRow.Round; pill.Padding = new Thickness(16, 0, 16, 0); }
        AutomationProperties.SetName(pill, SeriesExtras.Name); AutomationProperties.SetHelpText(pill, SeriesExtras.Heading(_extras));
        pill.Click += (_, _) => SeasonSelector.SelectedItem = SeasonSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, ExtrasPage)) ?? SeasonSelector.SelectedItem;
        SeasonPills.Children.Add(pill);
    }

    /// <summary>The bonus videos in place of the episodes: « Bonus · 48 · 1 h 12 », then a row for each kind.</summary>
    private void RenderExtras()
    {
        EpisodeHeading.Text = SeriesExtras.Heading(_extras); SeasonMarkButton.Visibility = SeasonMoreButton.Visibility = SeasonSubtitlesButton.Visibility = Visibility.Collapsed;
        var width = EpisodesPanel.ActualWidth > 200 ? EpisodesPanel.ActualWidth : Math.Max(600, DetailScroll.ActualWidth - 156);
        _extrasColumns = EpisodeTiles.Columns(width);
        foreach (var group in SeriesExtras.Groups(_extras, _detail?.Name))
        {
            var open = _extrasOpen.Contains(group.Key);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            if (group.Items.Count > _extrasColumns)
            {
                var toggle = new Button { Style = (Style)FindResource("Quiet"), Margin = new Thickness(0, -6, -12, -6) };
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new TextBlock { Text = SeriesExtras.Toggle(open), FontWeight = FontWeights.SemiBold });
                label.Children.Add(new Icon { Kind = open ? "up" : "down", Width = 12, Height = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                toggle.Content = label;
                AutomationProperties.SetName(toggle, $"{SeriesExtras.Toggle(open)} : {group.Name}");
                toggle.Click += (_, _) => { if (!_extrasOpen.Remove(group.Key)) _extrasOpen.Add(group.Key); RenderEpisodes(); };
                DockPanel.SetDock(toggle, Dock.Right); head.Children.Add(toggle);
            }
            var name = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.Inlines.Add(new System.Windows.Documents.Run(group.Name));
            name.Inlines.Add(new System.Windows.Documents.Run("  " + group.Items.Count.ToString(Lang.Culture)) { FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted") });
            head.Children.Add(name);
            EpisodesPanel.Children.Add(head);
            var grid = TileGrid(); grid.Margin = new Thickness(0, 0, -EpisodeTiles.Gap, 10);
            foreach (var extra in group.Items.Take(SeriesExtras.Shown(group.Items.Count, _extrasColumns, open))) grid.Children.Add(ExtraTile(extra));
            EpisodesPanel.Children.Add(grid);
        }
        LayoutTiles();
    }

    /// <summary>A bonus video as an episode's tile: its still, « Opening 3 », « Bakemonogatari · 1 min 30 »; a click plays it.</summary>
    private Button ExtraTile(MediaItem extra)
    {
        var button = SlidingText.Zone(new Button { Style = (Style)FindResource("Quiet"), Template = TileTemplate, Background = Brushes.Transparent, Padding = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        var root = new StackPanel { Background = Brushes.Transparent };
        var picture = new Grid { Background = Palette.Get("Raised2") };
        picture.Children.Add(new Icon { Kind = "film", Width = 26, Height = 26, Foreground = Palette.Get("Faint") });
        var zoom = new ScaleTransform(1, 1);
        var image = new Image { Stretch = Stretch.UniformToFill, RenderTransformOrigin = new Point(.5, .5), RenderTransform = zoom }; picture.Children.Add(image);
        var progress = extra.UserData.Played ? 1 : extra.Resumable ? extra.Progress : 0;
        if (progress > 0)
        {
            var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush("#38FFFFFF") };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(progress, GridUnitType.Star) }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - progress, GridUnitType.Star) });
            bar.Children.Add(new Border { Background = Palette.Get("Text") });
            picture.Children.Add(bar);
        }
        var play = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Brush("#80000000"), Child = new Icon { Kind = "play", Width = 20, Height = 20, Foreground = Brushes.White, Margin = new Thickness(3, 0, 0, 0) }, Visibility = Visibility.Hidden };
        picture.Children.Add(play);
        Hover(button, picture, zoom, play, false);
        root.Children.Add(picture);
        var title = new TextBlock { Text = SeriesExtras.Title(extra, _detail?.Name), FontSize = 14.5, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 11, 0, 0) };
        root.Children.Add(SlidingText.Wrap(title));
        var meta = SeriesExtras.Meta(extra, _detail?.Name);
        if (meta.Length > 0) root.Children.Add(new TextBlock { Text = meta, FontSize = 12.5, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        button.Content = root;
        AutomationProperties.SetName(button, string.Join(", ", new[] { title.Text, meta }.Where(x => x.Length > 0)));
        button.Click += async (_, _) => await PlayAsync(extra, chosen: true);
        if (_images is not null && extra.ImageTags.Count > 0) _ = LoadEpisodeImageAsync(image, extra, ImageCache.EpisodeTileWidth);
        return Tile(button, picture);
    }
}
