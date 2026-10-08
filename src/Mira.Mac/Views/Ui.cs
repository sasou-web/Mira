using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Animation;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>Small builders for the interface, written in code like the Windows app's dynamic parts.</summary>
public static class Ui
{
    public static readonly IBrush Muted = Brush("#A2A2A6"), Faint = Brush("#6E6E75"), Ink = Brush("#F4F4F5"), Raised = Brush("#1B1B1D");
    public static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    public static TextBlock Text(string text, double size = 14, FontWeight weight = FontWeight.Normal, IBrush? color = null, string? classes = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        if (color is not null) block.Foreground = color;
        if (classes is not null) block.Classes.AddRange(classes.Split(' '));
        return block;
    }
    public static StackPanel Stack(Orientation orientation, double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = orientation, Spacing = spacing };
        panel.Children.AddRange(children);
        return panel;
    }
    public static StackPanel Row(double spacing, params Control[] children) => Stack(Orientation.Horizontal, spacing, children);
    public static StackPanel Column(double spacing, params Control[] children) => Stack(Orientation.Vertical, spacing, children);

    /// <summary>A button with an icon and a label (« Lecture », « Plus d’infos »…).</summary>
    public static Button Action(string label, string? icon, string classes, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        if (icon is not null) content.Children.Add(new Icon(icon, 18) { VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content };
        button.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        button.Click += (_, _) => click();
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
    public static Button IconButton(string icon, string tip, Action click, string classes = "icon", double size = 22)
    {
        var button = new Button { Content = new Icon(icon, size) };
        button.Classes.AddRange(classes.Split(' '));
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }
    /// <summary>A small rounded tag (« 2025 », « 1 h 58 », « ★ 8.1 »).</summary>
    public static Border Tag(string text) => new()
    {
        Background = Brush("#CC1B1B1D"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
        Child = new TextBlock { Text = text, FontSize = 13, Foreground = Ink }
    };

    /// <summary>Loads <paramref name="load"/>'s image into <paramref name="image"/> once it first comes into view.</summary>
    public static void LoadWhenVisible(Control host, Image image, Func<Task<Bitmap?>> load)
    {
        var started = false;
        void Start()
        {
            if (started) return; started = true;
            _ = SetAsync();
        }
        async Task SetAsync()
        {
            var bitmap = await load();
            if (bitmap is null) return;
            image.Source = bitmap; image.Opacity = 1;
        }
        image.Opacity = 0;
        image.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }];
        host.EffectiveViewportChanged += (_, e) => { if (e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0 && e.EffectiveViewport.Intersects(new Rect(host.Bounds.Size))) Start(); };
    }

    /// <summary>The time left, « Reste 1 h 22 ».</summary>
    public static string Remaining(MediaItem item)
    {
        if (item.RunTimeTicks is not > 0) return "";
        var left = TimeSpan.FromTicks(Math.Max(0, item.RunTimeTicks.Value - item.UserData.PlaybackPositionTicks));
        return "Reste " + MediaItem.FormatDuration(left);
    }
    public static string Kind(MediaItem item) => item.Type switch { "Series" => "Série", "Episode" => "Épisode", _ => "Film" };
}

/// <summary>Columns of equal width that fill the page, as many as fit: the library grid.</summary>
public sealed class CardGrid : Panel
{
    public double MinItemWidth { get; set; } = 168;
    public double Gap { get; set; } = 22;
    public double RowGap { get; set; } = 30;
    private int Columns(double width) => Math.Max(1, (int)Math.Floor((width + Gap) / (MinItemWidth + Gap)));
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 1200 : available.Width;
        var columns = Columns(width); var item = (width - Gap * (columns - 1)) / columns;
        double height = 0, row = 0; var index = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(item, double.PositiveInfinity));
            row = Math.Max(row, child.DesiredSize.Height);
            if (++index % columns == 0) { height += row + RowGap; row = 0; }
        }
        if (index % columns != 0) height += row; else if (index > 0) height -= RowGap;
        return new Size(width, Math.Max(0, height));
    }
    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width); var item = (final.Width - Gap * (columns - 1)) / columns;
        double y = 0;
        for (var start = 0; start < Children.Count; start += columns)
        {
            var row = Children.Skip(start).Take(columns).ToList();
            var height = row.Max(x => x.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++) row[i].Arrange(new Rect(i * (item + Gap), y, item, height));
            y += height + RowGap;
        }
        return final;
    }
}

/// <summary>Posters and wide cards, with their progress and watched marks.</summary>
public static class Cards
{
    public static Button Poster(MediaItem item, Images? images, Action<MediaItem> open, Func<MediaItem, ContextMenu?>? menu = null)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var frame = new Grid { ClipToBounds = true };
        frame.Children.Add(Placeholder(item));
        frame.Children.Add(image);
        if (item.Progress > 0 && !item.UserData.Played) frame.Children.Add(Progress(item.Progress));
        if (item.UserData.Played) frame.Children.Add(Watched());
        var art = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = frame, Background = Ui.Raised };
        var aspect = new AspectBox(2.0 / 3) { Child = art };
        var caption = Ui.Column(2,
            new TextBlock { Text = item.DisplayTitle, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
            new TextBlock { Text = Caption(item), FontSize = 12.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(aspect); body.Children.Add(caption);
        var button = new Button { Content = body, Classes = { "card" } };
        Hover(button, art);
        button.Click += (_, _) => open(item);
        if (menu?.Invoke(item) is { } context) button.ContextMenu = context;
        Avalonia.Automation.AutomationProperties.SetName(button, $"{item.DisplayTitle}, {Caption(item)}");
        if (images is not null) Ui.LoadWhenVisible(button, image, () => images.PosterAsync(item));
        return button;
    }
    public static Button Wide(MediaItem item, Images? images, Action<MediaItem> open, double width = 300, Func<MediaItem, ContextMenu?>? menu = null)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var frame = new Grid { ClipToBounds = true, Height = width * 9 / 16 };
        frame.Children.Add(Placeholder(item));
        frame.Children.Add(image);
        frame.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0.55, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#00000000"), 0), new GradientStop(Color.Parse("#A0000000"), 1) } } });
        if (item.Progress > 0 && !item.UserData.Played) frame.Children.Add(Progress(item.Progress));
        var art = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = frame, Background = Ui.Raised };
        var line = item.Type == "Episode" ? $"S{item.ParentIndexNumber ?? 1} · É{item.IndexNumber ?? 1} — {item.Name}" : item.Progress > 0 ? Ui.Remaining(item) : Caption(item);
        var caption = Ui.Column(2,
            new TextBlock { Text = item.DisplayTitle, FontSize = 14.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
            new TextBlock { Text = line, FontSize = 12.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var body = new StackPanel { Spacing = 10, Width = width };
        body.Children.Add(art); body.Children.Add(caption);
        var button = new Button { Content = body, Classes = { "card" } };
        Hover(button, art);
        button.Click += (_, _) => open(item);
        if (menu?.Invoke(item) is { } context) button.ContextMenu = context;
        Avalonia.Automation.AutomationProperties.SetName(button, $"{item.DisplayTitle}, {line}");
        if (images is not null) Ui.LoadWhenVisible(button, image, () => images.LandscapeAsync(item, (int)(width * 2)));
        return button;
    }
    private static string Caption(MediaItem item) => item.Type switch
    {
        "Series" => string.Join(" · ", new[] { item.ProductionYear?.ToString(), "Série" }.Where(x => x is not null)),
        "Episode" => $"S{item.ParentIndexNumber ?? 1} · É{item.IndexNumber ?? 1}",
        _ => string.Join(" · ", new[] { item.ProductionYear?.ToString(), item.DurationLabel }.Where(x => !string.IsNullOrEmpty(x)))
    };
    /// <summary>Behind the artwork while it loads, or when there is none: the initials on a quiet tone.</summary>
    private static Control Placeholder(MediaItem item)
    {
        var hue = Math.Abs(item.Id.GetHashCode() % 360);
        var tone = HsvColor.ToRgb(hue, 0.25, 0.22);
        return new Border
        {
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(tone, 0), new GradientStop(Color.Parse("#141416"), 1) } },
            Child = new TextBlock { Text = item.DisplayTitle, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#CCFFFFFF"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(14), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
        };
    }
    private static Control Progress(double share) => new ProgressLine { Value = share, VerticalAlignment = VerticalAlignment.Bottom, Height = 4, Margin = new Thickness(10, 0, 10, 10) };
    private static Control Watched() => new Border
    {
        Background = Ui.Brush("#E0F5F5F7"), CornerRadius = new CornerRadius(12), Width = 24, Height = 24, Margin = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Child = new Icon("check", 14) { Foreground = Ui.Brush("#0B0B0C") }
    };
    /// <summary>A slight lift under the pointer.</summary>
    private static void Hover(Button button, Control art)
    {
        art.RenderTransform = new ScaleTransform(1, 1);
        art.Transitions = [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(160) }];
        button.PointerEntered += (_, _) => art.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.03)");
        button.PointerExited += (_, _) => art.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
    }
}

/// <summary>How far a title was watched: a thin bar on its artwork.</summary>
public sealed class ProgressLine : Control
{
    public double Value { get; set; }
    private static readonly IBrush Track = Ui.Brush("#59FFFFFF"), Fill = Ui.Brush("#F5F5F7");
    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size; var radius = size.Height / 2;
        context.DrawRectangle(Track, null, new Rect(size), radius, radius);
        context.DrawRectangle(Fill, null, new Rect(0, 0, size.Width * Math.Clamp(Value, 0.03, 1), size.Height), radius, radius);
    }
}

/// <summary>Keeps its child at a fixed width-to-height ratio.</summary>
public sealed class AspectBox(double ratio) : Decorator
{
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 180 : available.Width;
        var size = new Size(width, width / ratio);
        Child?.Measure(size);
        return size;
    }
    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(0, 0, final.Width, final.Width / ratio));
        return new Size(final.Width, final.Width / ratio);
    }
}

/// <summary>A titled horizontal row of cards, scrolled with the trackpad or its arrows.</summary>
public sealed class CardRow : StackPanel
{
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, Spacing = 18 };
    private readonly ScrollViewer _scroll;
    public CardRow(string title, IEnumerable<Control> cards, double sidePadding = 56)
    {
        Spacing = 16;
        _items.Children.AddRange(cards);
        _items.Margin = new Thickness(sidePadding, 4, sidePadding, 8);
        _scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _items };
        var header = new DockPanel { Margin = new Thickness(sidePadding, 0, sidePadding - 8, 0) };
        var arrows = Ui.Row(4, Ui.IconButton("back", "Précédents", () => Page(-1), "icon", 18), Ui.IconButton("next", "Suivants", () => Page(1), "icon", 18));
        DockPanel.SetDock(arrows, Dock.Right); header.Children.Add(arrows);
        header.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(header); Children.Add(_scroll);
    }
    private void Page(int direction)
    {
        var target = Math.Clamp(_scroll.Offset.X + direction * Math.Max(200, _scroll.Viewport.Width - 160), 0, Math.Max(0, _scroll.Extent.Width - _scroll.Viewport.Width));
        _scroll.Offset = new Vector(target, 0);
    }
}

/// <summary>Converts a hue to the placeholder tone.</summary>
internal static class HsvColor
{
    public static Color ToRgb(double hue, double saturation, double value) => new Avalonia.Media.HsvColor(1, hue, saturation, value).ToRgb();
}
