using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// A series' bonus videos, « La pastille » (SeriesExtras, as on Windows): a pill « Extras » after the seasons shows them in
// place of the episodes, by kind, one row each with « Show all », the episodes' tiles. A film keeps its row « Bonus ».
public sealed partial class DetailPage
{
    /// <summary>The tag of the hidden list's item and of the pill that show the bonus videos instead of a season.</summary>
    private static readonly object ExtrasPage = new();
    private List<MediaItem> _seriesExtras = [];
    // The groups opened past their first row, and how many tiles a row held when they were drawn.
    private readonly HashSet<string> _extrasOpen = [];
    private int _extrasColumns;

    private bool ExtrasShown => ReferenceEquals((_seasons.SelectedItem as ComboBoxItem)?.Tag, ExtrasPage);

    /// <summary>A series' bonus videos came: the pill « Extras » joins its seasons.</summary>
    private void ShowSeriesExtras(List<MediaItem> extras)
    {
        _seriesExtras = extras;
        // Read, even with none (a series of bonus alone): « Extras » shows them, as on Windows (M11: never shown).
        if (_episodesRead) { FillSeasons(); RenderEpisodes(); }
    }

    /// <summary>« Extras » after the seasons, a thin line between them.</summary>
    private void AddExtrasPill(bool compact)
    {
        _pills.Children.Add(new Border { Width = 1, Height = 22, Background = Ui.Brush("#2B2B2B"), Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center });
        var pill = new Button { Content = new TextBlock { Text = SeriesExtras.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, Classes = { "chip", "season" }, Padding = new Thickness(16, 8), CornerRadius = new CornerRadius(18), Tag = ExtrasPage };
        if (compact) { pill.Height = PillRow.Round; pill.Padding = new Thickness(16, 0); pill.VerticalContentAlignment = VerticalAlignment.Center; }
        Avalonia.Automation.AutomationProperties.SetName(pill, SeriesExtras.Name);
        Avalonia.Automation.AutomationProperties.SetHelpText(pill, SeriesExtras.Heading(_seriesExtras));
        pill.Click += (_, _) => _seasons.SelectedItem = _seasons.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, ExtrasPage)) ?? _seasons.SelectedItem;
        _pills.Children.Add(pill);
    }

    /// <summary>The bonus videos in place of the episodes: « Bonus · 48 · 1 h 12 », then a row for each kind.</summary>
    private void DrawExtras(Session session)
    {
        _episodeHeading.Text = SeriesExtras.Heading(_seriesExtras); _seasonMark.IsVisible = false;
        var width = _episodes.Bounds.Width > 200 ? _episodes.Bounds.Width : Math.Max(600, Bounds.Width - 112 - MainWindow.RailWidth);
        _extrasColumns = EpisodeTiles.Columns(width);
        foreach (var group in SeriesExtras.Groups(_seriesExtras, _item.Name))
        {
            var open = _extrasOpen.Contains(group.Key);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            if (group.Items.Count > _extrasColumns)
            {
                var toggle = new Button { Content = Ui.Row(6, new TextBlock { Text = SeriesExtras.Toggle(open), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, new Icon(open ? "up" : "down", 12)), Classes = { "link" }, FontSize = 13.5, Foreground = Ui.Brush("#C7C7CC"), VerticalAlignment = VerticalAlignment.Center };
                Avalonia.Automation.AutomationProperties.SetName(toggle, $"{SeriesExtras.Toggle(open)} : {group.Name}");
                toggle.Click += (_, _) => { if (!_extrasOpen.Remove(group.Key)) _extrasOpen.Add(group.Key); RenderEpisodes(); };
                DockPanel.SetDock(toggle, Dock.Right); head.Children.Add(toggle);
            }
            var name = new TextBlock { FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            name.Inlines!.Add(new Avalonia.Controls.Documents.Run(group.Name));
            name.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + group.Items.Count.ToString(Culture)) { FontWeight = FontWeight.Normal, Foreground = Ui.Muted });
            head.Children.Add(name);
            _episodes.Children.Add(head);
            var grid = TileGrid(); grid.Margin = new Thickness(0, 0, -EpisodeTiles.Gap, 10);
            foreach (var extra in group.Items.Take(SeriesExtras.Shown(group.Items.Count, _extrasColumns, open))) grid.Children.Add(ExtraTile(session, extra));
            _episodes.Children.Add(grid);
        }
        LayoutTiles();
    }

    /// <summary>A bonus video as an episode's tile: its still, « Opening 3 », « Bakemonogatari · 1 min 30 »; a click plays it.</summary>
    private Button ExtraTile(Session session, MediaItem extra)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var still = new Grid();
        still.Children.Add(new Border { Background = Ui.Brush("#232323"), Child = new Icon("film", 26) { Foreground = Ui.Faint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        still.Children.Add(image);
        var progress = extra.UserData.Played ? 1 : extra.Resumable ? extra.Progress : 0;
        if (progress > 0)
        {
            var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Ui.Brush("#38FFFFFF"), ColumnDefinitions = new ColumnDefinitions($"{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)}*,{(1 - progress).ToString(System.Globalization.CultureInfo.InvariantCulture)}*") };
            bar.Children.Add(new Border { Background = Ui.Ink });
            still.Children.Add(bar);
        }
        var play = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Ui.Brush("#80000000"), Child = new Icon("play", 20) { Foreground = Brushes.White, Margin = new Thickness(3, 0, 0, 0) }, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        still.Children.Add(play);
        var ring = Cards.Ring(10);
        still.Children.Add(ring);
        var picture = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = still };
        var title = SeriesExtras.Title(extra, _item.Name);
        var root = new StackPanel { Background = Brushes.Transparent, Children = { picture, TileTitle(extra, title) } };
        var meta = SeriesExtras.Meta(extra, _item.Name);
        if (meta.Length > 0) root.Children.Add(new TextBlock { Text = meta, FontSize = 13, Foreground = Ui.Muted, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var button = SlidingText.Zone(new Button { Content = root, Classes = { "card" }, VerticalContentAlignment = VerticalAlignment.Top });
        Cards.Hover(button, ring, [image], up => play.Opacity = up ? 1 : 0);
        button.Click += (_, _) => _ = Shell.PlayAsync(extra);
        Avalonia.Automation.AutomationProperties.SetName(button, string.Join(", ", new[] { title, meta }.Where(x => x.Length > 0)));
        if (extra.ImageTags.Count > 0) Ui.LoadWhenVisible(button, image, () => session.Images.EpisodeAsync(extra, 420));
        return Tile(button, picture);
    }
}
