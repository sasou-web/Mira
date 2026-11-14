using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// A series' episodes as a grid of stills (EpisodeTiles, mock-up B chosen by ogsas on 2026-11-12): four a row at the usual
// width, the seasons as pills, the one Lecture plays framed in white with its tag, a watched one with a check on its
// picture; those the library lacks (EpisodeShelf) are hollow tiles at their place, a run of them one tile that opens.
public partial class MainWindow
{
    // The tiles of the page, and their picture, sized again when the page's width changes.
    private readonly List<(FrameworkElement Tile, FrameworkElement Picture)> _tiles = [];
    private double _tileWidth;
    // A tile's template: its content alone (no button wash: the picture says the hover).
    private static readonly ControlTemplate TileTemplate = MakeTileTemplate();
    private static ControlTemplate MakeTileTemplate()
    {
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        template.Seal(); return template;
    }

    /// <summary>The ring of a tile under the pointer: the posters' (MediaCard).</summary>
    private static readonly SolidColorBrush HoverRing = Frozen(new SolidColorBrush(Color.FromArgb(235, 245, 245, 247)));
    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }

    // The tiles' panel: as wide as the page plus one gap, each tile followed by its gap.
    private static WrapPanel TileGrid() => new() { Margin = new Thickness(0, 0, -EpisodeTiles.Gap, 0) };

    private void AddTiles(WrapPanel grid, ShelfLine line, DateOnly today)
    {
        switch (line.Kind)
        {
            case ShelfKind.Episode: grid.Children.Add(EpisodeTile(line.First)); break;
            case ShelfKind.Coming: grid.Children.Add(HollowTile(line.First, today)); break;
            default:
                grid.Children.Add(GapTile(line, today));
                if (_shelfOpen.Contains(line.First.Id)) foreach (var episode in line.Episodes) grid.Children.Add(HollowTile(episode, today));
                break;
        }
    }

    /// <summary>The tiles at the page's width: <see cref="EpisodeTiles.Columns"/> a row, pictures in 16:9.</summary>
    private void LayoutTiles()
    {
        var width = EpisodesPanel.ActualWidth;
        if (width <= 0 || _tiles.Count == 0) return;
        _tileWidth = EpisodeTiles.Width(width);
        foreach (var (tile, picture) in _tiles) SizeTile(tile, picture);
    }
    private void SizeTile(FrameworkElement tile, FrameworkElement picture)
    {
        var width = _tileWidth > 0 ? _tileWidth : 300; var height = Math.Round(width * 9 / 16);
        tile.Width = width; picture.Width = width; picture.Height = height;
        picture.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 10, 10);
    }
    private void EpisodesPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && _tiles.Count > 0 && EpisodeTiles.Width(e.NewSize.Width) != _tileWidth) LayoutTiles();
    }
    private T Tile<T>(T tile, FrameworkElement picture) where T : FrameworkElement
    {
        tile.Margin = new Thickness(0, 0, EpisodeTiles.Gap, EpisodeTiles.RowGap); tile.VerticalAlignment = VerticalAlignment.Top;
        _tiles.Add((tile, picture)); SizeTile(tile, picture);
        return tile;
    }

    private Button EpisodeTile(MediaItem episode)
    {
        var isNext = _detailNext?.Id == episode.Id; var wanted = _detailEpisode?.Id == episode.Id;
        var button = new Button { Style = (Style)FindResource("Quiet"), Template = TileTemplate, Background = Brushes.Transparent, Padding = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var root = new StackPanel { Background = Brushes.Transparent };
        var picture = new Grid { Background = Palette.Get("Raised2") };
        // « Cacher les spoilers »: an episode not started shows Jellyfin's BlurHash of its still, the still never fetched.
        var hidden = Spoilers.Hides(episode, _settings.HideSpoilers);
        // Under the pointer, as a poster (MediaCard): the still zooms gently inside its rounded frame.
        var zoom = new ScaleTransform(1, 1);
        var image = new Image { Stretch = Stretch.UniformToFill, RenderTransformOrigin = new Point(.5, .5), RenderTransform = zoom }; picture.Children.Add(image);
        if (hidden) image.Source = BlurHash.Of(episode, Spoilers.Still(episode)) is { } hash ? MediaCard.Blur(hash, 16 / 9d) : null;
        var progress = episode.UserData.Played ? 1 : episode.Resumable ? episode.Progress : 0;
        if (progress > 0)
        {
            var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush("#38FFFFFF") };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(progress, GridUnitType.Star) }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - progress, GridUnitType.Star) });
            bar.Children.Add(new Border { Background = Palette.Get("Text") });
            picture.Children.Add(bar);
        }
        if (episode.UserData.Played)
            picture.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush("#A0000000"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0), Child = new Icon { Kind = "check", Width = 13, Height = 13, Foreground = Brushes.White } });
        // Play: always on the one to watch, on the others under the pointer or the keyboard.
        var play = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Brush("#80000000"), Child = new Icon { Kind = "play", Width = 20, Height = 20, Foreground = Brushes.White, Margin = new Thickness(3, 0, 0, 0) }, Visibility = isNext ? Visibility.Visible : Visibility.Hidden };
        picture.Children.Add(play);
        if (isNext)
        {
            picture.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6), Background = Palette.Get("Text"), Padding = new Thickness(9, 3, 9, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 10, 0, 0),
                Child = new TextBlock { Text = EpisodeTiles.UpNext, FontSize = 11, FontWeight = FontWeights.ExtraBold, Foreground = Palette.Get("OnAccent") }
            });
            picture.Children.Add(new Border { BorderBrush = Palette.Get("Text"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10), IsHitTestVisible = false });
        }
        // The poster's light ring, under the pointer or the keyboard; the one to watch keeps its white frame and only zooms.
        var ring = isNext ? null : new Border { BorderBrush = HoverRing, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10), IsHitTestVisible = false, Opacity = 0 };
        if (ring is not null) picture.Children.Add(ring);
        var lifted = false;
        void Lift(bool up)
        {
            if (up == lifted) return;
            lifted = up;
            if (!isNext) play.Visibility = up ? Visibility.Visible : Visibility.Hidden;
            var scale = up && !Motion.Reduced ? MediaCard.HoverZoom : 1; var opacity = up ? 1d : 0d;
            if (Motion.Reduced)
            {
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null); zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null); zoom.ScaleX = zoom.ScaleY = scale;
                if (ring is not null) { ring.BeginAnimation(OpacityProperty, null); ring.Opacity = opacity; }
                return;
            }
            var time = TimeSpan.FromMilliseconds(160);
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, time) { EasingFunction = Motion.Soft });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, time) { EasingFunction = Motion.Soft });
            ring?.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, time) { EasingFunction = Motion.Soft });
        }
        // The pointer's own doing, as on the posters: tiles going by under a still pointer while the page scrolls stay quiet.
        button.MouseEnter += (_, _) => { if (!PageScrolling) Lift(true); };
        button.MouseMove += (_, _) => { if (!lifted && !PageScrolling) Lift(true); };
        button.MouseLeave += (_, _) => { if (!button.IsKeyboardFocused) Lift(false); };
        button.GotKeyboardFocus += (_, _) => Lift(true); button.LostKeyboardFocus += (_, _) => { if (!button.IsMouseOver) Lift(false); };
        root.Children.Add(picture);
        var title = new TextBlock { FontSize = 14.5, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 11, 0, 0) };
        if (EpisodeTiles.Number(episode) is { Length: > 0 } number) title.Inlines.Add(new Run(number + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Muted") });
        title.Inlines.Add(new Run(episode.Name));
        root.Children.Add(title);
        long? size = _episodeSizes is { } sizes && sizes.TryGetValue(episode.Id, out var known) ? known : null;
        var meta = new TextBlock { Text = EpisodeTiles.Meta(episode, EpisodeCalendar.Today(), size), FontSize = 12.5, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        root.Children.Add(meta); _episodeMeta[episode.Id] = (meta, episode);
        var overview = hidden ? Spoilers.HiddenSummary : OverviewText.Plain(episode.Overview, flat: true);
        if (overview.Length > 0)
            root.Children.Add(new TextBlock { Text = overview, FontSize = 12.5, FontWeight = FontWeights.Normal, FontStyle = hidden ? FontStyles.Italic : FontStyles.Normal, Foreground = Palette.Get("Faint"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis, MaxHeight = 36, LineHeight = 18, Margin = new Thickness(0, 6, 0, 0) });
        if (CanKeep(episode)) { var kept = EpisodeCopyLabel(episode); kept.HorizontalAlignment = HorizontalAlignment.Left; kept.Margin = new Thickness(0, 6, 0, 0); root.Children.Add(kept); }
        button.Content = root;
        var keptText = CopyState(episode.Id) is { } copy ? ", " + copy.ToLowerInvariant() : "";
        AutomationProperties.SetName(button, L($"Épisode {episode.IndexNumber?.ToString() ?? ""} : {episode.Name}, {episode.DurationLabel}{(episode.UserData.Played ? ", vu" : isNext ? ", à suivre" : "")}{keptText}",
            $"Episode {episode.IndexNumber?.ToString() ?? ""}: {episode.Name}, {episode.DurationLabel}{(episode.UserData.Played ? ", watched" : isNext ? ", up next" : "")}{keptText}"));
        button.Click += async (_, _) => await PlayAsync(episode, chosen: true);
        button.ContextMenu = EpisodeMenu(episode);
        if (_images is not null && !hidden) _ = LoadEpisodeImageAsync(image, episode, ImageCache.EpisodeTileWidth);
        // The episode found by a search comes into view the first time its season shows.
        if (wanted && !_detailEpisodeShown)
        {
            _detailEpisodeShown = true;
            _ = Dispatcher.BeginInvoke(() => button.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        return Tile(button, picture);
    }

    // An episode the library lacks: no picture to play, its name and its day (the next to come out, or one of an opened run).
    private StackPanel HollowTile(MediaItem episode, DateOnly today)
    {
        var tile = new StackPanel();
        var picture = new Border { CornerRadius = new CornerRadius(10), Background = Palette.Get("Bg"), BorderBrush = Palette.Get("Raised2"), BorderThickness = new Thickness(1), Child = new Icon { Kind = "calendar", Width = 26, Height = 26, Foreground = Palette.Get("Faint") } };
        tile.Children.Add(picture);
        var (_, when) = EpisodeShelf.Entry(episode, today);
        var title = new TextBlock { FontSize = 14.5, FontWeight = FontWeights.Bold, Foreground = Palette.Get("TextTertiary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 11, 0, 0) };
        if (EpisodeTiles.Number(episode) is { Length: > 0 } number) title.Inlines.Add(new Run(number + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Muted") });
        title.Inlines.Add(new Run(EpisodeShelf.Name(episode)));
        tile.Children.Add(title);
        tile.Children.Add(new TextBlock { Text = when, FontSize = 12.5, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        AutomationProperties.SetName(tile, $"{EpisodeShelf.Code([episode])} {EpisodeShelf.Name(episode)}, {when}");
        return Tile(tile, picture);
    }

    // « É5 · Pas dans ta bibliothèque · Sorti le 8 nov. » or « É8–12 · À venir », the size of a picture; it opens its episodes.
    private Button GapTile(ShelfLine line, DateOnly today)
    {
        var key = line.First.Id; var open = _shelfOpen.Contains(key);
        var button = new Button { Style = (Style)FindResource("Quiet"), Template = TileTemplate, Background = Brushes.Transparent, Padding = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        var picture = new Border { CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, BorderBrush = Palette.Get("Raised2"), BorderThickness = new Thickness(1) };
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 20, 0) };
        var (first, second) = EpisodeTiles.GapLines(line, today);
        copy.Children.Add(new TextBlock { Text = EpisodeShelf.Code(line.Episodes), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Palette.Get("TextTertiary") });
        copy.Children.Add(new TextBlock { Text = first, FontSize = 12.5, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        copy.Children.Add(new TextBlock { Text = second, FontSize = 12.5, Foreground = Palette.Get("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        var show = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        show.Children.Add(new TextBlock { Text = EpisodeShelf.Show(line.Episodes.Count, open), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextSecondary") });
        show.Children.Add(new Icon { Kind = open ? "up" : "down", Width = 12, Height = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get("TextSecondary") });
        copy.Children.Add(show);
        var inside = new Grid(); inside.Children.Add(copy);
        // « Télécharger É6–9 » (MainWindow.MissingDownloads.cs): under the lines, the bar of what comes along the foot.
        if (MissingTilePart(line) is var (part, bar)) { show.Margin = new Thickness(0, 8, 0, 0); copy.Children.Insert(copy.Children.IndexOf(show), part); inside.Children.Add(bar); }
        picture.Child = inside; button.Content = picture;
        var text = line.Kind == ShelfKind.Later ? EpisodeShelf.LaterText(line.Episodes, today) : EpisodeShelf.MissingText(line.Episodes, today);
        AutomationProperties.SetName(button, text); AutomationProperties.SetHelpText(button, EpisodeShelf.Show(line.Episodes.Count, open));
        button.Click += (_, _) => Toggle(key);
        return Tile(button, picture);
    }

    /// <summary>The seasons as pills under the heading; a click shows that one (the hidden list stays the source).</summary>
    private void FillSeasonPills(IReadOnlyList<EpisodePage> pages)
    {
        SeasonPills.Children.Clear(); _missingViews.RemoveAll(x => x.Pill);
        SeasonPills.Visibility = pages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var page in pages)
        {
            var (name, note) = EpisodeTiles.Pill(page);
            var coming = MissingPillNote(page);
            var pill = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 8), BorderBrush = Palette.Get("Line"), Tag = page };
            Motion.SetRadius(pill, new CornerRadius(18));
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            if (note is not null) label.Children.Add(new TextBlock { Text = note, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.62 });
            if (coming is not null) label.Children.Add(coming);
            pill.Content = label; AutomationProperties.SetName(pill, page.Label);
            pill.Click += (_, _) => SeasonSelector.SelectedItem = SeasonSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, page)) ?? SeasonSelector.SelectedItem;
            SeasonPills.Children.Add(pill);
        }
    }
    private void UpdateSeasonPills(EpisodePage? shown)
    {
        foreach (var pill in SeasonPills.Children.OfType<Button>())
        {
            var on = ReferenceEquals(pill.Tag, shown);
            pill.Background = on ? Palette.Get("Text") : Brushes.Transparent; pill.Foreground = Palette.Get(on ? "OnAccent" : "TextSecondary");
            pill.BorderBrush = on ? Palette.Get("Text") : Palette.Get("Line");
            AutomationProperties.SetItemStatus(pill, on ? L("affichée", "shown") : "");
        }
    }
}
