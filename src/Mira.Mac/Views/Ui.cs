using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Animation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>Small builders for the interface, written in code like the Windows app's dynamic parts.</summary>
public static class Ui
{
    public static readonly IBrush Muted = Brush("#A2A2A6"), Faint = Brush("#8A8A90"), Ink = Brush("#F4F4F5"), Raised = Brush("#1C1C1C"), TextTertiary = Brush("#B6B6BD");
    /// <summary>Glyphs beside a field (the search magnifier), as Windows' Dim: never text, under 4.5:1.</summary>
    public static readonly IBrush Dim = Brush("#757575");
    /// <summary>The glyph of a poster without artwork, as on Windows (MediaCard): decorative, never text.</summary>
    public static readonly IBrush Glyph = Brush("#4E4E57");
    /// <summary>States, as Windows' palette: a line that went well, and one that warns (« Hors connexion »).</summary>
    public static readonly IBrush Success = Brush("#86C6A2"), Warning = Brush("#D5B787"), WarningText = Brush("#E9DCC3");
    public static SolidColorBrush Brush(string color) => new(Color.Parse(color));
    /// <summary>
    /// The menus, « Écrin » as on Windows (Themes/Cinema.xaml; mockup A chosen by ogsas on 2026-11-14): their window
    /// (Surface, its edge Line), a row raised (Raised) or pressed (Raised2), the edge of a chosen cell (LineStrong), the lines
    /// between their parts (Rule), the sunk track of a slider of choices or of « − 0.0 s + » (Inset), the body and
    /// secondary inks of their rows.
    /// </summary>
    public static readonly IBrush Surface = Brush("#111111"), MenuLine = Brush("#2A2A2A"), Raised2 = Brush("#242424"), LineStrong = Brush("#3F3F3F"),
        Rule = Brush("#222222"), Black = Brush("#000000"), Inset = Brush("#0A0A0A"), TextBody = Brush("#E0E0E0"), TextSecondary = Brush("#C7C7C7");
    /// <summary>When the wheel or the trackpad last scrolled (Environment.TickCount64), set by the window for every event, momentum included.</summary>
    public static long ScrolledAt { get; set; } = long.MinValue / 2;
    /// <summary>The page is scrolling under the pointer: what is under it changes without the pointer moving.</summary>
    public static bool Scrolling => Environment.TickCount64 - ScrolledAt < 250;

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
    /// <summary>A row's or a group's title, as Windows draws them: large, light and quieter than the titles of the cards under it.</summary>
    public static TextBlock SectionTitle(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 24, FontWeight = FontWeight.Normal, Foreground = TextTertiary, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(block, 2);
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
        button.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        ToolTip.SetTip(button, tip);
        var parts = tip.Split(" · ", 2);
        Avalonia.Automation.AutomationProperties.SetName(button, name ?? parts[0]);
        if (parts.Length > 1) Avalonia.Automation.AutomationProperties.SetHelpText(button, parts[1]);
        button.Click += (_, _) => click();
        return button;
    }
    /// <summary>
    /// A menu's line as on Windows (MenuAction): its glyph (IconPaths, 16 px), then its label; without
    /// <paramref name="action"/>, a line that only says something (greyed).
    /// </summary>
    public static MenuItem Entry(string label, string? icon, Action? action)
    {
        var entry = new MenuItem { Header = label, IsEnabled = action is not null };
        if (icon is not null) entry.Icon = new Icon(icon, 17) { Foreground = TextSecondary };
        if (action is not null) entry.Click += (_, _) => action();
        return entry;
    }
    /// <summary>
    /// <paramref name="control"/>'s right-click menu, made as it opens: what it offers (rights read after the control was drawn,
    /// a copy on this Mac, a mark changed) is as it is then. No line: no menu.
    /// </summary>
    public static void MenuOn(Control control, Func<ContextMenu?> make)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            if (make() is { } fresh) { var entries = fresh.Items.ToList(); fresh.Items.Clear(); foreach (var entry in entries) menu.Items.Add(entry); }
            e.Cancel = menu.Items.Count == 0;
        };
        control.ContextMenu = menu;
    }
    /// <summary>A button's menu, opened under it from its left edge (not where the pointer happens to be).</summary>
    public static void OpenUnder(ContextMenu menu, Control button)
    {
        menu.PlacementTarget = button; menu.Placement = PlacementMode.BottomEdgeAlignedLeft; menu.VerticalOffset = 6;
        menu.Open(button);
    }
    /// <summary>A small rounded tag (« 2025 », « 1 h 58 », « ★ 8.1 »).</summary>
    /// <summary>A fact over the banner (year, episode, length, genre), rounded as the buttons beside it.</summary>
    public static Border Tag(string text) => new()
    {
        Background = Brush("#CC1C1C1C"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6),
        Child = new TextBlock { Text = text, FontSize = 13, Foreground = Ink }
    };

    /// <summary>
    /// Loads <paramref name="load"/>'s image into <paramref name="image"/> once it first comes into view. Avalonia works out
    /// where each card waiting for it lies at every layout pass (each frame of a scroll): a card listens only until its
    /// image is asked, and only while its page shows (a page kept hidden behind another one waits).
    /// </summary>
    public static void LoadWhenVisible(Control host, Image image, Func<Task<Bitmap?>> load)
    {
        var started = false; var listening = false;
        void Seen(object? sender, EffectiveViewportChangedEventArgs e)
        {
            if (started || e.EffectiveViewport.Width <= 0 || e.EffectiveViewport.Height <= 0 || !e.EffectiveViewport.Intersects(new Rect(host.Bounds.Size))) return;
            started = true; Listen(false);
            _ = SetAsync();
        }
        void Listen(bool on)
        {
            if (on && started || on == listening) return;
            listening = on;
            if (on) host.EffectiveViewportChanged += Seen; else host.EffectiveViewportChanged -= Seen;
        }
        async Task SetAsync()
        {
            var loading = load();
            // Already in memory (a page come back to): there at once, as on Windows; fetched: it fades in over its placeholder.
            if (loading.IsCompletedSuccessfully) image.Transitions = null;
            var bitmap = await loading;
            if (bitmap is null) return;
            image.Source = bitmap; image.Opacity = 1;
        }
        image.Opacity = 0;
        image.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }];
        Page.WhileShown(host, Listen);
    }

    /// <summary>
    /// <paramref name="panel"/>'s children made <paramref name="wanted"/>, touching as little as it can: nothing when they
    /// already are, only those that changed when the others keep their places (a title watched, its progress), all of
    /// them otherwise. A child taken out and put back is styled again by Avalonia, as a new one is made.
    /// </summary>
    public static void Place(Panel panel, IReadOnlyList<Control> wanted)
    {
        var children = panel.Children;
        if (children.Count == wanted.Count)
        {
            var inPanel = children.ToHashSet(); var inPlace = true;
            for (var i = 0; i < wanted.Count && inPlace; i++) inPlace = wanted[i] == children[i] || !inPanel.Contains(wanted[i]);
            if (inPlace) { for (var i = 0; i < wanted.Count; i++) if (children[i] != wanted[i]) children[i] = wanted[i]; return; }
        }
        children.Clear(); children.AddRange(wanted);
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
    /// <summary>The posters' size chosen with a pinch or ⌘+ / ⌘− (<see cref="Trackpad.Zoom"/>), for every grid.</summary>
    public static double Zoom { get; set; } = 1;
    /// <summary>The width a grid's poster is fetched at: twice its points (Retina), one step up once pinched larger.</summary>
    public static int Pixels => Zoom > 1.05 ? 540 : 360;
    public double Gap { get; set; } = 22;
    public double RowGap { get; set; } = 30;
    private int Columns(double width, double? zoom = null) => Math.Max(1, (int)Math.Floor((width + Gap) / (MinItemWidth * (zoom ?? Zoom) + Gap)));
    /// <summary>The posters on a line at its width with <paramref name="zoom"/>: their size changes only with it.</summary>
    public int ColumnsAt(double zoom) => Columns(Bounds.Width > 0 ? Bounds.Width : 1200 - 140, zoom);
    /// <summary>The columns at its width now (a page not laid out yet counts a window of 1200).</summary>
    public int ColumnsShown => Columns(Bounds.Width > 0 ? Bounds.Width : 1200 - 140);
    /// <summary>
    /// <paramref name="rows"/> rows of <see cref="SkeletonCard"/> after the cards there (none: the skeleton taken away),
    /// as on Windows: two rows while a grid first loads, one under it while the next page does.
    /// </summary>
    public void Skeleton(int rows)
    {
        foreach (var shown in Children.OfType<SkeletonCard>().ToList()) Children.Remove(shown);
        for (var i = 0; i < rows * ColumnsShown; i++) Children.Add(new SkeletonCard());
    }
    public int SkeletonCards => Children.OfType<SkeletonCard>().Count();
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
    /// <param name="pixels">The width the poster is fetched at: <see cref="CardGrid.Pixels"/> in a grid.</param>
    public static Button Poster(MediaItem item, Images? images, Action<MediaItem> open, Func<MediaItem, ContextMenu?>? menu = null, int pixels = 360)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var frame = new Grid { ClipToBounds = true };
        var placeholder = Placeholder(item, false);
        frame.Children.Add(placeholder);
        frame.Children.Add(image);
        if (item.Resumable) frame.Children.Add(Progress(item.Progress));
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
        Hover(button, placeholder, image);
        button.Click += (_, _) => open(item);
        Menu(button, item, menu);
        Avalonia.Automation.AutomationProperties.SetName(button, string.Join(", ", new[] { item.DisplayTitle, Caption(item), item.EpisodesToWatchText }.Where(x => !string.IsNullOrEmpty(x))));
        if (images is not null) Ui.LoadWhenVisible(button, image, () => images.PosterAsync(item, pixels));
        return button;
    }
    public static Button Wide(MediaItem item, Images? images, Action<MediaItem> open, double width = 300, Func<MediaItem, ContextMenu?>? menu = null)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var frame = new Grid { ClipToBounds = true, Height = width * 9 / 16 };
        var placeholder = Placeholder(item, true);
        frame.Children.Add(placeholder);
        frame.Children.Add(image);
        frame.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0.55, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#00000000"), 0), new GradientStop(Color.Parse("#A0000000"), 1) } } });
        if (item.Resumable) frame.Children.Add(Progress(item.Progress));
        var art = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = frame, Background = Ui.Raised };
        var line = item.Type == "Episode" ? $"{EpisodeCode(item)} — {item.Name}" : item.Progress > 0 ? Ui.Remaining(item) : Caption(item);
        var caption = Ui.Column(2,
            new TextBlock { Text = item.DisplayTitle, FontSize = 14.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
            new TextBlock { Text = line, FontSize = 12.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var body = new StackPanel { Spacing = 10, Width = width };
        body.Children.Add(art); body.Children.Add(caption);
        var button = new Button { Content = body, Classes = { "card" } };
        Hover(button, placeholder, image);
        button.Click += (_, _) => open(item);
        Menu(button, item, menu);
        Avalonia.Automation.AutomationProperties.SetName(button, $"{item.DisplayTitle}, {line}");
        if (images is not null) Ui.LoadWhenVisible(button, image, () => images.LandscapeAsync(item, (int)(width * 2)));
        return button;
    }
    /// <summary>
    /// The card's right-click menu, made as it opens: what it offers (rights read after the card was drawn, a copy on this
    /// Mac, the collection shown) is as it is then, even on a card kept from an earlier refresh (<see cref="Update"/>).
    /// </summary>
    private static void Menu(Button button, MediaItem item, Func<MediaItem, ContextMenu?>? make)
    {
        if (make is not null) Ui.MenuOn(button, () => make(item));
    }
    /// <summary>What a card shows of <paramref name="item"/>: the same key, the same card (title, line, marks, picture).</summary>
    public static string Key(MediaItem item) => string.Join('|', item.Id, item.Type, item.Name, item.SeriesName, item.ProductionYear, item.ParentIndexNumber, item.IndexNumber, item.RunTimeTicks,
        item.ExtraType, item.UserData.Played, item.UserData.IsFavorite, item.UserData.PlaybackPositionTicks, item.EpisodesToWatchBadge, string.Join(',', item.ImageTags.Select(x => x.Key + ":" + x.Value)),
        item.SeriesPrimaryImageTag, item.SeasonThumbImageTag, string.Join(',', item.BackdropImageTags), item.ParentLogoImageTag, Spoilers.On);
    /// <summary>
    /// The cards of <paramref name="panel"/> for <paramref name="items"/>, made by <paramref name="make"/> under
    /// <paramref name="kind"/> (its size, its picture's): a card that would show the same (<see cref="Key"/>) stays as it is,
    /// picture and all, and the panel is left alone when nothing changed. A page come back to was otherwise drawn again
    /// whole at each refresh: 40 to 70 ms of the interface's thread for a grid of a hundred titles (--smooth-check).
    /// </summary>
    public static void Update(Panel panel, IReadOnlyList<MediaItem> items, string kind, Func<MediaItem, Control> make)
    {
        var known = new Dictionary<string, Control>();
        foreach (var card in panel.Children) if (card.Tag is string key) known.TryAdd(key, card);
        var wanted = new List<Control>(items.Count);
        foreach (var item in items) wanted.Add(known.Remove(kind + "|" + Key(item), out var card) ? card : Keyed(make(item), kind, item));
        Ui.Place(panel, wanted);
    }
    /// <summary><paramref name="card"/> marked with what it shows, for <see cref="Update"/> to find it again.</summary>
    public static Control Keyed(Control card, string kind, MediaItem item) { card.Tag = kind + "|" + Key(item); return card; }
    private static string Caption(MediaItem item) => item.Type switch
    {
        "Series" => string.Join(" · ", new[] { item.ProductionYear?.ToString(), L("Série", "Series") }.Where(x => x is not null)),
        "Episode" => EpisodeCode(item),
        "BoxSet" => L("Collection", "Collection"),
        _ => string.Join(" · ", new[] { item.ExtraType is not null ? PlayerText.ExtraKind(item.ExtraType) : item.ProductionYear?.ToString(), item.DurationLabel }.Where(x => !string.IsNullOrEmpty(x)))
    };
    /// <summary>« S1 · É3 », “S1 · E3”.</summary>
    public static string EpisodeCode(MediaItem item) => $"S{item.ParentIndexNumber ?? 1} · {L("É", "E")}{item.IndexNumber ?? 1}";
    /// <summary>
    /// An actor or director found by a search: their photo in a circle (their initials without one) and their name, and on
    /// a title's page the part they play; a click shows their titles.
    /// </summary>
    /// <param name="size">Smaller on a title's page (72), its cast in one row after the episodes and facts.</param>
    public static Button Person(MediaItem person, Images? images, Action<MediaItem> open, string? role = null, double size = 88)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, Width = size, Height = size };
        var circle = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), ClipToBounds = true, Background = Ui.Raised, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Grid { Children = { new TextBlock { Text = Catalog.Initials(person.Name), FontSize = size * 26 / 88, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, image } }
        };
        var body = new StackPanel { Spacing = 10, Width = size < 88 ? 104 : 112 };
        body.Children.Add(circle);
        body.Children.Add(new TextBlock { Text = person.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        if (role?.Trim() is { Length: > 0 } part)
            body.Children.Add(new TextBlock { Text = part, FontSize = 12.5, FontWeight = FontWeight.Normal, Foreground = Ui.Muted, TextAlignment = TextAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, -8, 0, 0) });
        var button = new Button { Content = body, Classes = { "card" } };
        Hover(button, circle);
        button.Click += (_, _) => open(person);
        Avalonia.Automation.AutomationProperties.SetName(button, person.Name);
        if (images is not null && person.ImageTags.ContainsKey("Primary")) Ui.LoadWhenVisible(button, image, () => images.PosterAsync(person, 200));
        return button;
    }

    /// <summary>
    /// Behind the artwork while it loads, as on Windows: Jellyfin's BlurHash of that picture (the same pixels as Mira web
    /// and the iPhone), read from what Jellyfin already sent; without one (or without a picture), the title under a film
    /// or play glyph, on the card's quiet tone.
    /// </summary>
    private static Control Placeholder(MediaItem item, bool wide)
    {
        if (Blur(item, wide) is { } blur)
        {
            var soft = new Image { Source = blur, Stretch = Stretch.Fill, IsHitTestVisible = false, Classes = { "blur" } };
            RenderOptions.SetBitmapInterpolationMode(soft, BitmapInterpolationMode.HighQuality);
            return soft;
        }
        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20), IsHitTestVisible = false,
            Children =
            {
                new Icon(wide ? "play" : "film", 30) { Foreground = Ui.Glyph, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 15) },
                new TextBlock { Text = item.DisplayTitle, FontSize = 13, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxLines = 4, TextTrimming = TextTrimming.WordEllipsis }
            }
        };
    }
    /// <summary>Decoded placeholders, kept by hash: the same poster comes back on every page, and each takes a few kilobytes.</summary>
    private static readonly Dictionary<string, Bitmap> Blurs = [];
    private static Bitmap? Blur(MediaItem item, bool wide)
    {
        if (BlurHash.Of(item, wide ? Artwork.Landscape(item) : Artwork.Poster(item)) is not { } hash) return null;
        return Blur(hash, wide ? 16 / 9d : 2 / 3d);
    }
    /// <summary>A BlurHash decoded for a picture of this aspect (width over height), kept with the cards': also the hidden still of an episode (<see cref="Spoilers"/>).</summary>
    public static Bitmap? Blur(string hash, double aspect)
    {
        var (width, height) = BlurHash.Size(aspect);
        var key = $"{hash}|{width}|{height}";
        if (Blurs.TryGetValue(key, out var known)) return known;
        if (BlurHash.Decode(hash, width, height) is not { } pixels) return null;
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
            for (var y = 0; y < height; y++) System.Runtime.InteropServices.Marshal.Copy(pixels, y * width * 4, buffer.Address + y * buffer.RowBytes, width * 4);
        if (Blurs.Count >= 600) Blurs.Clear();
        return Blurs[key] = bitmap;
    }
    /// <summary>The cards under <paramref name="root"/> showing Jellyfin's BlurHash: for the self-check.</summary>
    public static int Blurred(Visual root) => root.GetVisualDescendants().OfType<Image>().Count(x => x.Classes.Contains("blur") && x.IsEffectivelyVisible);
    private static Control Progress(double share) => new ProgressLine { Value = share, VerticalAlignment = VerticalAlignment.Bottom, Height = 4, Margin = new Thickness(10, 0, 10, 10) };
    /// <summary>A soft shadow under the corner badges: they stay legible on a light poster.</summary>
    private static readonly BoxShadows BadgeShadow = BoxShadows.Parse("0 0 0 1 #2E000000, 0 2 8 0 #73000000");
    private static Control Watched() => new Border
    {
        Background = Ui.Brush("#F5F5F7"), CornerRadius = new CornerRadius(12), BoxShadow = BadgeShadow, Width = 24, Height = 24, Margin = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Child = new Icon("check", 14) { Foreground = Ui.Brush("#0B0B0B") }
    };
    /// <summary>Episodes left in a series under way: new ones show from the library.</summary>
    private static Control ToWatch(string count) => new Border
    {
        Background = Ui.Brush("#F5F5F7"), CornerRadius = new CornerRadius(12), BoxShadow = BadgeShadow, MinWidth = 24, Height = 24, Padding = new Thickness(7, 0), Margin = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock { Text = count, FontSize = 12.5, FontWeight = FontWeight.Bold, Foreground = Ui.Brush("#0B0B0B"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
    };
    private static readonly ITransform Lifted = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.025)"), Resting = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
    /// <summary>
    /// A slight zoom of the picture under the pointer, inside its rounded frame, as on Windows (MediaCard.HoverZoom): the
    /// frame itself zoomed lost its rounded corners in Avalonia (square ones past the card, seen on a Mac). The pointer's
    /// own doing: while the page scrolls the cards by under a still pointer (<see cref="Ui.Scrolling"/>), none zooms on its
    /// way; the one under it does once the pointer moves.
    /// </summary>
    private static void Hover(Button button, params Control[] pictures)
    {
        foreach (var picture in pictures)
        {
            picture.RenderTransform = Resting;
            picture.Transitions = [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(160) }];
        }
        var lifted = false;
        void Lift(bool up) { if (up == lifted) return; lifted = up; foreach (var picture in pictures) picture.RenderTransform = up ? Lifted : Resting; }
        button.PointerEntered += (_, _) => { if (!Ui.Scrolling) Lift(true); };
        button.PointerMoved += (_, _) => { if (!lifted && !Ui.Scrolling) Lift(true); };
        button.PointerExited += (_, _) => Lift(false);
    }
}

/// <summary>
/// A card's shape while the grid's titles are on their way, as on Windows: the artwork, the title and its line, pulsing
/// softly (but with macOS's « Réduire les animations », where they stay still), so that nothing jumps when the real cards take their place.
/// </summary>
public sealed class SkeletonCard : StackPanel
{
    private static readonly IBrush Surface = Ui.Brush("#111111");
    private CancellationTokenSource? _pulse;

    public SkeletonCard()
    {
        IsHitTestVisible = false; Focusable = false;
        Children.Add(new AspectBox(2.0 / 3) { Child = new Border { CornerRadius = new CornerRadius(10), Background = Surface } });
        Children.Add(Bar(12, 72, Ui.Raised, new Thickness(0, 16, 0, 0)));
        Children.Add(Bar(9, 42, Surface, new Thickness(0, 12, 0, 20)));
        // Only while its page shows: one kept behind another (MainWindow.Host) would keep the animations' clock going.
        Page.WhileShown(this, Pulse);
    }
    /// <summary>A line <paramref name="share"/> % of the card wide.</summary>
    private static Grid Bar(double height, int share, IBrush brush, Thickness margin)
    {
        var bar = new Border { Height = height, CornerRadius = new CornerRadius(3), Background = brush };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions($"{share}*,{100 - share}*"), Margin = margin };
        line.Children.Add(bar);
        return line;
    }
    private void Pulse(bool on)
    {
        _pulse?.Cancel(); _pulse = null;
        if (!on || MacMotion.Reduced) return;
        _pulse = new CancellationTokenSource();
        var pulse = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1800), IterationCount = IterationCount.Infinite, Easing = new Avalonia.Animation.Easings.SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(.5), Setters = { new Avalonia.Styling.Setter(OpacityProperty, .55) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 1d) } },
            }
        };
        _ = pulse.RunAsync(this, _pulse.Token);
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

/// <summary>
/// A titled horizontal row of cards, scrolled with the trackpad or its arrows: shown only when the row goes past the
/// window, each dimmed at its end, a press gliding about three posters along (as on Windows), never a whole page at once.
/// </summary>
public sealed class CardRow : StackPanel
{
    /// <summary>How far an arrow goes: Windows' step, kept short of the row's width on a small window.</summary>
    public const double Step = 624;
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, Spacing = 18 };
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _arrows;
    private readonly Button _previous, _next;
    private readonly ScrollMotion _motion = new();
    private bool _running;
    private TimeSpan? _last;
    /// <param name="people">A row of people (a title's cast): its arrows named for them, not for titles.</param>
    public CardRow(string title, IEnumerable<Control> cards, double sidePadding = 56, bool people = false)
    {
        Spacing = 16;
        _items.Children.AddRange(cards);
        _items.Margin = new Thickness(sidePadding, 4, sidePadding, 8);
        _scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _items };
        var header = new DockPanel { Margin = new Thickness(sidePadding, 0, sidePadding - 8, 0) };
        _previous = people ? Ui.IconButton("back", L("Personnes précédentes", "Previous people"), () => Glide(-1), "icon", 17)
            : Ui.IconButton("back", L("Titres précédents", "Previous titles"), () => Glide(-1), "icon", 17, L($"Titres précédents dans « {title} »", $"Previous titles in “{title}”"));
        _next = people ? Ui.IconButton("next", L("Personnes suivantes", "Next people"), () => Glide(1), "icon", 17)
            : Ui.IconButton("next", L("Titres suivants", "Next titles"), () => Glide(1), "icon", 17, L($"Titres suivants dans « {title} »", $"Next titles in “{title}”"));
        _arrows = Ui.Row(2, _previous, _next);
        _arrows.IsVisible = false;
        DockPanel.SetDock(_arrows, Dock.Right); header.Children.Add(_arrows);
        var heading = Ui.SectionTitle(title);
        header.Children.Add(heading);
        Children.Add(header); Children.Add(_scroll);
        _scroll.ScrollChanged += (_, _) => UpdateArrows();
        _scroll.SizeChanged += (_, _) => UpdateArrows();
        // One gesture over a row is the row's or the page's, whole, as in Apple's TV app: a swipe down carries a little
        // sideways (the row took it all and the page stood still), a swipe along the row a little up or down (each event
        // went to the row or the page, by turns: the page shook). The window decides the gesture's way on its first points.
        _scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers == Avalonia.Input.KeyModifiers.Shift) return;
            var axis = TopLevel.GetTopLevel(this) is MainWindow window ? window.GestureAxis : SwipeAxis.Undecided;
            var down = axis == SwipeAxis.Undecided ? Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) : axis == SwipeAxis.Vertical;
            e.Handled = true;
            // As Avalonia's own wheel step (ScrollContentPresenter: 50 per unit), the other way left out.
            if (!down) _scroll.Offset = new Vector(Math.Clamp(_scroll.Offset.X - e.Delta.X * 50, 0, Maximum), 0);
            else if (Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<ScrollViewer>(this) is { } page)
                page.Offset = new Vector(page.Offset.X, Math.Clamp(page.Offset.Y - e.Delta.Y * 50, 0, Math.Max(0, page.Extent.Height - page.Viewport.Height)));
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    private double Maximum => Math.Max(0, _scroll.Extent.Width - _scroll.Viewport.Width);
    /// <summary>For the self-check: whether the arrows show, and how far the row has moved.</summary>
    public bool ArrowsShown => _arrows.IsVisible;
    public double Offset => _scroll.Offset.X;
    /// <summary>The arrows only for a row wider than the window; the one at an end dimmed.</summary>
    private void UpdateArrows()
    {
        _arrows.IsVisible = Maximum > 1;
        _previous.IsEnabled = _scroll.Offset.X > 1; _next.IsEnabled = _scroll.Offset.X < Maximum - 1;
    }
    /// <summary>Eased to the next posters (<see cref="ScrollMotion"/>, the Windows rows'), frame by frame; a press while moving adds to it.</summary>
    public void Glide(int direction)
    {
        if (!_running) _motion.Reset(_scroll.Offset.X);
        _motion.Add(direction * Math.Min(Step, Math.Max(200, _scroll.Viewport.Width - 160)), Maximum);
        if (_running || !_motion.Moving || TopLevel.GetTopLevel(this) is not { } top) return;
        _running = true; _last = null;
        top.RequestAnimationFrame(Frame);
    }
    private void Frame(TimeSpan now)
    {
        var seconds = _last is { } last ? (now - last).TotalSeconds : 1 / 60d; _last = now;
        _scroll.Offset = new Vector(_motion.Step(Math.Min(.1, seconds), Maximum), 0);
        if (_motion.Moving && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(Frame);
        else _running = false;
    }
}

/// <summary>
/// A list eased up or down, frame by frame (<see cref="ScrollMotion"/>, the Windows wheel's): a wheel's notches (the
/// window's <c>EaseWheel</c>), its rail entry chosen again (<see cref="ToTop"/>). One motion per list, which each new
/// input goes on from, at the speed it has; a scroll by other means meanwhile (a trackpad, the scroll bar, the keyboard)
/// stops it. At once when macOS reduces motion.
/// </summary>
public static class Glide
{
    private sealed class State
    {
        public readonly ScrollMotion Motion = new();
        public bool Running;
        public TimeSpan? Last;
        /// <summary>Where the last step put the list: anything else moved it since.</summary>
        public double Set;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, State> States = new();
    public static void By(ScrollViewer scroll, double pixels)
    {
        if (TopLevel.GetTopLevel(scroll) is not { } top) return;
        var state = States.GetValue(scroll, _ => new State());
        if (!state.Running || Math.Abs(scroll.Offset.Y - state.Set) > 1) { state.Running = false; state.Motion.Reset(scroll.Offset.Y); }
        state.Motion.Add(pixels, Maximum(scroll));
        if (MacMotion.Reduced) { state.Running = false; scroll.Offset = new Vector(scroll.Offset.X, state.Motion.Step(10, Maximum(scroll))); return; }
        if (state.Running || !state.Motion.Moving) return;
        state.Running = true; state.Last = null; state.Set = scroll.Offset.Y;
        top.RequestAnimationFrame(now => Frame(scroll, state, top, now));
    }
    /// <summary>Back to the top (its rail entry chosen again); nothing at the top already.</summary>
    public static void ToTop(ScrollViewer scroll) { if (scroll.Offset.Y > .5) By(scroll, -scroll.Offset.Y - scroll.Extent.Height); }
    /// <summary>For the self-check: whether the list is being eased.</summary>
    public static bool Moving(ScrollViewer scroll) => States.TryGetValue(scroll, out var state) && state.Running;
    private static void Frame(ScrollViewer scroll, State state, TopLevel top, TimeSpan now)
    {
        if (!state.Running) return;
        if (Math.Abs(scroll.Offset.Y - state.Set) > 1) { state.Running = false; return; }
        // The first frame: a sixtieth of a second (Avalonia's frames have nothing before them to count from).
        var seconds = state.Last is { } last ? (now - last).TotalSeconds : 1 / 60d; state.Last = now;
        state.Set = state.Motion.Step(Math.Clamp(seconds, 0, .1), Maximum(scroll));
        scroll.Offset = new Vector(scroll.Offset.X, state.Set);
        if (state.Motion.Moving && scroll.IsEffectivelyVisible) top.RequestAnimationFrame(next => Frame(scroll, state, top, next));
        else state.Running = false;
    }
    private static double Maximum(ScrollViewer scroll) => Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
}

/// <summary>Converts a hue to the placeholder tone.</summary>
internal static class HsvColor
{
    public static Color ToRgb(double hue, double saturation, double value) => new Avalonia.Media.HsvColor(1, hue, saturation, value).ToRgb();
}

/// <summary>
/// The right-click menus of the window, as on Windows: a right click on a line chooses it too (macOS's own menus and
/// Windows' do; Avalonia's only take the left button: the menu stayed, the line did nothing), and while one is open, a
/// page that would draw its cards again waits for it to close (<see cref="WhenClosed"/>): the card under the menu
/// taken away closed the menu under the pointer (Avalonia closes a menu whose control leaves the window).
/// </summary>
public static class Menus
{
    private static int _open;
    private static readonly List<Action> Waiting = [];
    /// <summary>A right-click menu shows.</summary>
    public static bool AnyOpen => _open > 0;

    /// <summary>Once, before the window opens.</summary>
    public static void Watch()
    {
        MenuBase.OpenedEvent.AddClassHandler<ContextMenu>((_, _) => _open++);
        MenuBase.ClosedEvent.AddClassHandler<ContextMenu>((_, _) =>
        {
            _open = Math.Max(0, _open - 1);
            if (_open > 0 || Waiting.Count == 0) return;
            // After the menu's own closing, and after the line's action (which may itself ask for a new drawing).
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        });
        InputElement.PointerReleasedEvent.AddClassHandler<MenuItem>(RightReleased, handledEventsToo: true);
    }
    /// <summary>
    /// <paramref name="draw"/> now, or once no menu shows: the same method asked three times while a menu is open runs once.
    /// </summary>
    public static void WhenClosed(Action draw)
    {
        if (!AnyOpen) { draw(); return; }
        if (!Waiting.Contains(draw)) Waiting.Add(draw);
    }
    private static void Flush()
    {
        if (AnyOpen) return;
        var due = Waiting.ToList(); Waiting.Clear();
        foreach (var draw in due) draw();
    }
    private static void RightReleased(MenuItem item, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || !item.IsEnabled || item.HasSubMenu || !item.IsPointerOver) return;
        e.Handled = true;
        // As a left click does: the menu closes, then the line acts.
        (item.FindLogicalAncestorOfType<ContextMenu>() ?? item.Parent as ContextMenu)?.Close();
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
    }
}
