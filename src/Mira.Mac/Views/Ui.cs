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
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>Small builders for the interface, written in code like the Windows app's dynamic parts.</summary>
public static class Ui
{
    public static readonly IBrush Muted = Brush("#A2A2A6"), Faint = Brush("#8A8A90"), Ink = Brush("#F4F4F5"), Raised = Brush("#1B1B1D");
    public static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    public static TextBlock Text(string text, double size = 14, FontWeight weight = FontWeight.Normal, IBrush? color = null, string? classes = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        if (color is not null) block.Foreground = color;
        if (classes is not null) block.Classes.AddRange(classes.Split(' '));
        return block;
    }
    /// <summary>A page's or a section's title, which VoiceOver lists among the headings (level 1 for the page).</summary>
    public static TextBlock Heading(string text, double size, int level, FontWeight weight = FontWeight.Bold)
    {
        var block = Text(text, size, weight);
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(block, level);
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
    /// <summary>
    /// A button showing only an icon: its tooltip, and the name VoiceOver reads (a tooltip is not read). A shortcut after
    /// « · » in the tooltip (« Rechercher · ⌘F ») becomes the button's help, read after a pause.
    /// </summary>
    public static Button IconButton(string icon, string tip, Action click, string classes = "icon", double size = 22, string? name = null)
    {
        var button = new Button { Content = new Icon(icon, size) };
        button.Classes.AddRange(classes.Split(' '));
        ToolTip.SetTip(button, tip);
        var parts = tip.Split(" · ", 2);
        Avalonia.Automation.AutomationProperties.SetName(button, name ?? parts[0]);
        if (parts.Length > 1) Avalonia.Automation.AutomationProperties.SetHelpText(button, parts[1]);
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

    /// <summary>The time left, « Reste 1 h 22 », “1 h 22 left”.</summary>
    public static string Remaining(MediaItem item)
    {
        if (item.RunTimeTicks is not > 0) return "";
        var left = TimeSpan.FromTicks(Math.Max(0, item.RunTimeTicks.Value - item.UserData.PlaybackPositionTicks));
        return L("Reste ", "") + MediaItem.FormatDuration(left) + L("", " left");
    }
    public static string Kind(MediaItem item) => item.Type switch { "Series" => L("Série", "Series"), "Episode" => L("Épisode", "Episode"), _ => L("Film", "Movie") };
}

/// <summary>
/// An empty page or list: an icon, what is empty, what to do about it, and the button that does it; the same parts as
/// Mira web's emptyState() and the Windows catalogue's. VoiceOver reads its title when it appears.
/// </summary>
public sealed class EmptyState : StackPanel
{
    private readonly Icon _icon = new("library", 26) { Foreground = Ui.Muted };
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _text = new() { FontSize = 14.5, LineHeight = 21, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _note = new() { FontSize = 13, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _action = new() { Classes = { "primary" }, Padding = new Thickness(18, 9), Margin = new Thickness(0, 20, 0, 0) };
    private Action? _run;
    public string Title => _title.Text ?? "";
    /// <summary>The button's label, empty without one (the self-check).</summary>
    public string ActionLabel => _action.IsVisible ? _action.Content as string ?? "" : "";

    /// <param name="centered">In the middle of a page; else at the left of a column (Téléchargements).</param>
    public EmptyState(bool centered = true)
    {
        IsVisible = false; MaxWidth = centered ? 460 : 540;
        var align = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        HorizontalAlignment = align;
        Children.Add(new Border
        {
            Width = 60, Height = 60, CornerRadius = new CornerRadius(30), Background = Ui.Raised, HorizontalAlignment = align, Margin = new Thickness(0, 0, 0, 18), Child = _icon
        });
        _title.TextAlignment = _text.TextAlignment = _note.TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left;
        _title.HorizontalAlignment = _text.HorizontalAlignment = _note.HorizontalAlignment = _action.HorizontalAlignment = align;
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_title, Avalonia.Automation.AutomationLiveSetting.Polite);
        _action.Click += (_, _) => _run?.Invoke();
        Children.Add(_title); Children.Add(_text); Children.Add(_note); Children.Add(_action);
    }

    /// <param name="note">A last line, quieter (« Ne télécharge que ce que tu as le droit d’obtenir. »).</param>
    public void Show(string icon, string title, string text, string? action = null, Action? run = null, string? note = null)
    {
        _icon.Kind = icon; _title.Text = title; _text.Text = text; _text.IsVisible = text.Length > 0;
        _note.Text = note; _note.IsVisible = !string.IsNullOrEmpty(note);
        _action.Content = action; _run = run; _action.IsVisible = action is not null && run is not null;
        IsVisible = true;
    }
    public void Hide() => IsVisible = false;
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
        else if (item.EpisodesToWatchBadge is { } count) frame.Children.Add(ToWatch(count));
        var art = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = frame, Background = Ui.Raised };
        var aspect = new AspectBox(2.0 / 3) { Child = art };
        var caption = Ui.Column(2,
            // Two lines, both always kept: a long title reads whole, and the cards of a grid stay in line (as on Windows).
            new TextBlock { Text = item.DisplayTitle, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, LineHeight = 19, Height = 38 },
            new TextBlock { Text = Caption(item), FontSize = 12.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(aspect); body.Children.Add(caption);
        var button = new Button { Content = body, Classes = { "card" } };
        Hover(button, art);
        button.Click += (_, _) => open(item);
        if (menu?.Invoke(item) is { } context) button.ContextMenu = context;
        Avalonia.Automation.AutomationProperties.SetName(button, string.Join(", ", new[] { item.DisplayTitle, Caption(item), item.EpisodesToWatchText }.Where(x => !string.IsNullOrEmpty(x))));
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
        var line = item.Type == "Episode" ? $"{EpisodeCode(item)} — {item.Name}" : item.Progress > 0 ? Ui.Remaining(item) : Caption(item);
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
        "Series" => string.Join(" · ", new[] { item.ProductionYear?.ToString(), L("Série", "Series") }.Where(x => x is not null)),
        "Episode" => EpisodeCode(item),
        "BoxSet" => L("Saga", "Collection"),
        _ => string.Join(" · ", new[] { item.ExtraType is not null ? PlayerText.ExtraKind(item.ExtraType) : item.ProductionYear?.ToString(), item.DurationLabel }.Where(x => !string.IsNullOrEmpty(x)))
    };
    /// <summary>« S1 · É3 », “S1 · E3”.</summary>
    public static string EpisodeCode(MediaItem item) => $"S{item.ParentIndexNumber ?? 1} · {L("É", "E")}{item.IndexNumber ?? 1}";
    /// <summary>
    /// An actor or director found by a search: their photo in a circle (their initials without one) and their name;
    /// a click shows their titles.
    /// </summary>
    public static Button Person(MediaItem person, Images? images, Action<MediaItem> open)
    {
        const double size = 88;
        var image = new Image { Stretch = Stretch.UniformToFill, Width = size, Height = size };
        var circle = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), ClipToBounds = true, Background = Ui.Raised, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Grid { Children = { new TextBlock { Text = Catalog.Initials(person.Name), FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, image } }
        };
        var body = new StackPanel { Spacing = 10, Width = 112 };
        body.Children.Add(circle);
        body.Children.Add(new TextBlock { Text = person.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button { Content = body, Classes = { "card" } };
        Hover(button, circle);
        button.Click += (_, _) => open(person);
        Avalonia.Automation.AutomationProperties.SetName(button, person.Name);
        if (images is not null && person.ImageTags.ContainsKey("Primary")) Ui.LoadWhenVisible(button, image, () => images.PosterAsync(person, 200));
        return button;
    }

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
    /// <summary>Episodes left in a series under way: new ones show from the library.</summary>
    private static Control ToWatch(string count) => new Border
    {
        Background = Ui.Brush("#E0F5F5F7"), CornerRadius = new CornerRadius(12), MinWidth = 24, Height = 24, Padding = new Thickness(7, 0), Margin = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock { Text = count, FontSize = 12.5, FontWeight = FontWeight.Bold, Foreground = Ui.Brush("#0B0B0C"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
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
        var arrows = Ui.Row(4, Ui.IconButton("back", L("Précédents", "Previous"), () => Page(-1), "icon", 18, L($"Précédents dans « {title} »", $"Previous in “{title}”")),
            Ui.IconButton("next", L("Suivants", "Next"), () => Page(1), "icon", 18, L($"Suivants dans « {title} »", $"Next in “{title}”")));
        DockPanel.SetDock(arrows, Dock.Right); header.Children.Add(arrows);
        var heading = new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(heading, 2);
        header.Children.Add(heading);
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
