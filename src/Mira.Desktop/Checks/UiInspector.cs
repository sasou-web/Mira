using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>A defect the interface audit (--ui-audit) found on one screen: what, where (window pixels), how bad.</summary>
/// <param name="Kind">One of <see cref="UiInspector.Kinds"/>.</param>
/// <param name="Severity">« défaut » (to fix), « à juger » (maybe wanted), « info ».</param>
internal sealed record UiFinding(string Kind, string Severity, string Screen, string Element, Rect Bounds, string Detail)
{
    /// <summary>The same defect on another screen or size: one line in the report, the screens listed.</summary>
    public string Key => $"{Kind}|{Element}|{Detail}";
}

/// <summary>
/// The automatic eye of --ui-audit (MainWindow.UiAudit.cs): walks what WPF shows (the window, or an open menu) and says
/// what a person would point at. Measured, never guessed from the XAML: a text cut by its box, a button cut by the window
/// or its panel, a menu leaving the window, a target smaller than 24 × 24 (WCAG 2.2, 2.5.8), neighbours off by a pixel or
/// three, a control without any change under the pointer. Read-only: it changes nothing it walks.
/// </summary>
internal static class UiInspector
{
    public static readonly (string Kind, string Title, string About)[] Kinds =
    [
        ("menu-hors-fenetre", "Menu ou fenêtre hors de Mira", "Un menu, une liste ou un panneau ouvert sort de la fenêtre de Mira (comme « Get season » avant la 0.9.8)."),
        ("coupe", "Élément coupé", "Un bouton, un texte ou une icône dépasse de la fenêtre ou du panneau qui le contient, sans défilement pour l'atteindre."),
        ("texte-coupe", "Texte coupé net", "Un texte plus long que sa place, sans « … » ni retour à la ligne : la fin manque."),
        ("cible-petite", "Cible trop petite", "Un contrôle cliquable de moins de 24 × 24 pixels (WCAG 2.2, critère 2.5.8) : dur à attraper à la souris."),
        ("desalignement", "Désalignement", "Des voisins d'une même rangée (ou colonne) décalés de 1 à 4 pixels : presque alignés, donc probablement pas voulu."),
        ("survol-absent", "Survol sans retour", "Un contrôle cliquable qui ne change pas du tout sous le pointeur."),
        ("tuile-sans-zoom", "Tuile sans zoom au survol", "Une tuile à image (comme les affiches) qui ne zoome pas sous le pointeur, contrairement aux affiches."),
        ("sans-animation", "Changement sans animation", "Un écran ou un panneau qui apparaît d'une image à l'autre, sans transition."),
        ("sans-nom", "Sans nom pour le lecteur d'écran", "Un contrôle que le Narrateur ne peut pas nommer."),
        ("sans-anneau", "Sans anneau de focus", "Un contrôle atteint au clavier (Tab) qui ne montre pas où est le focus."),
        ("texte-abrege", "Texte abrégé (…)", "Un texte raccourci par « … » : voulu en général, listé pour repérer ceux qui le sont trop souvent."),
    ];

    /// <summary>WCAG 2.2 AA (2.5.8, target size minimum).</summary>
    public const double MinimumTarget = 24;

    /// <summary>Everything wrong on <paramref name="root"/> (the window's content, or a menu's popup), in its pixels.</summary>
    public static List<UiFinding> Inspect(FrameworkElement root, string screen)
    {
        var found = new List<UiFinding>();
        var full = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        Walk(root, root, screen, new Frame(new(full.Left, false), new(full.Top, false), new(full.Right, false), new(full.Bottom, false), 1, false), found);
        return found;
    }

    /// <summary>An edge of the area an element is seen in, and whether a scroll can bring what lies beyond it.</summary>
    private readonly record struct Edge(double At, bool Scrolls);

    /// <summary>
    /// What an element inherits from those around it: the area that shows it, edge by edge (the bottom of a row of the
    /// Home may be the window's, which the page scrolls past, while its right is the row's own), its opacity.
    /// </summary>
    private readonly record struct Frame(Edge Left, Edge Top, Edge Right, Edge Bottom, double Opacity, bool InButton)
    {
        public Rect Clip => Right.At <= Left.At || Bottom.At <= Top.At ? Rect.Empty : new Rect(Left.At, Top.At, Right.At - Left.At, Bottom.At - Top.At);
        /// <summary>Narrowed to <paramref name="box"/>, whose edges a scroll reaches beyond or not, axis by axis.</summary>
        public Frame Within(Rect box, bool scrollsX, bool scrollsY) => this with
        {
            Left = box.Left > Left.At ? new(box.Left, scrollsX) : Left,
            Right = box.Right < Right.At ? new(box.Right, scrollsX) : Right,
            Top = box.Top > Top.At ? new(box.Top, scrollsY) : Top,
            Bottom = box.Bottom < Bottom.At ? new(box.Bottom, scrollsY) : Bottom,
        };
    }

    private static void Walk(DependencyObject node, FrameworkElement root, string screen, Frame frame, List<UiFinding> found)
    {
        if (node is UIElement { IsVisible: false }) return;
        var opacity = frame.Opacity * (node is UIElement element ? element.Opacity : 1);
        // Faded out (a sheet leaving, a button shown only under the pointer): nothing a person sees.
        if (opacity < .05) return;
        var here = frame with { Opacity = opacity };
        if (node is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0 && Bounds(fe, root) is { } bounds)
        {
            Check(fe, bounds, root, screen, frame, found);
            here = Narrow(fe, bounds, here);
            if (fe is ButtonBase) here = here with { InButton = true };
            if (fe is Panel panel) Alignment(panel, root, screen, found);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), root, screen, here, found);
    }

    private static Rect? Bounds(FrameworkElement element, FrameworkElement root)
    {
        try { return ReferenceEquals(element, root) ? new Rect(root.RenderSize) : element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
        // Not under root (a popup's own tree), or a 3D transform.
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The area its children are seen in: a scroll's viewport (one axis or both reachable), a clipping panel.</summary>
    private static Frame Narrow(FrameworkElement element, Rect bounds, Frame frame)
    {
        if (element is ScrollContentPresenter presenter && (presenter.ScrollOwner ?? presenter.TemplatedParent as ScrollViewer) is { } owner)
        {
            // What lies beyond the edges around the scroll (the window's, a panel's) is brought in by scrolling too.
            bool x = owner.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled, y = owner.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
            var reachable = frame with
            {
                Left = frame.Left with { Scrolls = frame.Left.Scrolls || x },
                Right = frame.Right with { Scrolls = frame.Right.Scrolls || x },
                Top = frame.Top with { Scrolls = frame.Top.Scrolls || y },
                Bottom = frame.Bottom with { Scrolls = frame.Bottom.Scrolls || y },
            };
            return reachable.Within(bounds, x, y);
        }
        // A clipping panel: its own edges cut, no scroll beyond them.
        if (element.ClipToBounds || element.Clip is not null) return frame.Within(bounds, false, false);
        return frame;
    }

    private static void Check(FrameworkElement element, Rect bounds, FrameworkElement root, string screen, Frame frame, List<UiFinding> found)
    {
        var clip = frame.Clip;
        var shown = clip.IsEmpty ? Rect.Empty : Rect.Intersect(clip, bounds);
        var meaningful = element is TextBlock or ButtonBase or Icon or TextBox or ComboBox or PasswordBox;
        // Partly seen: cut at an edge no scroll reaches past. Wholly outside: parked (a panel slid away) or scrolled past.
        if (meaningful && !shown.IsEmpty && shown.Width > 2 && shown.Height > 2)
        {
            var cut = new List<string>();
            if (!frame.Left.Scrolls && bounds.Left < clip.Left - 1.5) cut.Add($"{clip.Left - bounds.Left:0} px à gauche");
            if (!frame.Right.Scrolls && bounds.Right > clip.Right + 1.5) cut.Add($"{bounds.Right - clip.Right:0} px à droite");
            if (!frame.Top.Scrolls && bounds.Top < clip.Top - 1.5) cut.Add($"{clip.Top - bounds.Top:0} px en haut");
            if (!frame.Bottom.Scrolls && bounds.Bottom > clip.Bottom + 1.5) cut.Add($"{bounds.Bottom - clip.Bottom:0} px en bas");
            // Inside a button, the button itself is the one reported.
            if (cut.Count > 0 && !(frame.InButton && element is not ButtonBase))
                found.Add(new("coupe", "défaut", screen, Describe(element), bounds, "Dépasse de " + string.Join(", ", cut) + (clip == new Rect(root.RenderSize) ? " de la fenêtre" : " de son panneau")));
        }
        if (shown.IsEmpty) return;
        if (element is TextBlock text && TruncationTip.IsCut(text))
        {
            if (text.TextTrimming == TextTrimming.None && Overshoot(text) > 2)
                found.Add(new("texte-coupe", "défaut", screen, Describe(text), bounds, $"« {Short(text.Text, 60)} » ne tient pas dans ses {text.ActualWidth:0} × {text.ActualHeight:0} px"));
            else if (text.TextTrimming != TextTrimming.None) found.Add(new("texte-abrege", "info", screen, Describe(text), bounds, $"« {Short(text.Text, 60)} » abrégé à {text.ActualWidth:0} px"));
        }
        // A scroll bar's own parts (its track, 12 px wide) are the scroll bar's business, not a target of Mira's.
        if (Clickable(element) && element.IsEnabled && !frame.InButton && element.TemplatedParent is not (ScrollBar or Track or Slider))
        {
            if (element.ActualWidth < MinimumTarget - .5 || element.ActualHeight < MinimumTarget - .5)
                found.Add(new("cible-petite", "défaut", screen, Describe(element), bounds, $"{element.ActualWidth:0.#} × {element.ActualHeight:0.#} px (24 × 24 au moins)"));
            if (Hover(element) is null)
                found.Add(new("survol-absent", "à juger", screen, Describe(element), bounds, "Rien ne change sous le pointeur (ni déclencheur IsMouseOver, ni voile, ni gestionnaire MouseEnter)"));
        }
    }

    /// <summary>How much of a text without « … » is past its box: a pixel of rounding is nothing anyone sees.</summary>
    private static double Overshoot(TextBlock block)
    {
        var full = new TextBlock
        {
            Text = block.Text, FontFamily = block.FontFamily, FontSize = block.FontSize, FontWeight = block.FontWeight, FontStyle = block.FontStyle, FontStretch = block.FontStretch,
            TextWrapping = block.TextWrapping, LineHeight = block.LineHeight, LineStackingStrategy = block.LineStackingStrategy,
        };
        // A pixel and a half more: the box is rounded to whole pixels, the text it was measured with is not.
        full.Measure(new Size(block.ActualWidth + 1.5, double.PositiveInfinity));
        return block.TextWrapping == TextWrapping.NoWrap ? full.DesiredSize.Width - block.ActualWidth : full.DesiredSize.Height - block.ActualHeight;
    }

    public static bool Clickable(FrameworkElement element) => element is ButtonBase or ComboBox or MenuItem or ListBoxItem;

    /// <summary>
    /// How it answers the pointer, from what WPF holds (never by moving the mouse): a trigger on IsMouseOver (or
    /// IsHighlighted, a menu's line) in its style or template, Mira's wash (<see cref="Motion.InteractiveProperty"/>), a
    /// card's own hover, a MouseEnter handler on it or on what is drawn inside it. Null: nothing.
    /// </summary>
    public static string? Hover(FrameworkElement element)
    {
        if (element is MediaCard) return "affiche";
        if (element is Button button && Motion.GetInteractive(button) && button.Template?.FindName("Hover", button) is not null) return "voile";
        // Itself, or what it sits in (a row that lights as a whole).
        DependencyObject? up = element;
        for (var level = 0; level < 3 && up is not null; level++, up = VisualTreeHelper.GetParent(up))
            if (up is FrameworkElement around && (HoverTriggers(around) || Handles(around))) return level == 0 ? "déclencheur" : "parent";
        return Inside(element, 0);

        static string? Inside(DependencyObject node, int depth)
        {
            if (depth > 6) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement inner && (HoverTriggers(inner) || Handles(inner))) return "intérieur";
                if (Inside(child, depth + 1) is { } found) return found;
            }
            return null;
        }
    }

    private static bool HoverTriggers(FrameworkElement element)
    {
        for (var style = element.Style; style is not null; style = style.BasedOn) if (Watches(style.Triggers)) return true;
        if (element is Control { Template: { } template } && Watches(template.Triggers)) return true;
        return element is ContentPresenter { ContentTemplate: { } content } && Watches(content.Triggers);
    }

    private static bool Watches(TriggerCollection triggers) => triggers.Any(trigger => trigger switch
    {
        Trigger t => Pointer(t.Property),
        MultiTrigger m => m.Conditions.Any(c => Pointer(c.Property)),
        DataTrigger d => d.Binding is System.Windows.Data.Binding { Path.Path: { } path } && path.Contains("IsMouseOver", StringComparison.Ordinal),
        MultiDataTrigger md => md.Conditions.Any(c => c.Binding is System.Windows.Data.Binding { Path.Path: { } path } && path.Contains("IsMouseOver", StringComparison.Ordinal)),
        EventTrigger e => e.RoutedEvent == UIElement.MouseEnterEvent || e.RoutedEvent == Mouse.MouseEnterEvent,
        _ => false,
    });

    private static bool Pointer(DependencyProperty? property) => property == UIElement.IsMouseOverProperty || property == MenuItem.IsHighlightedProperty || property == ComboBoxItem.IsHighlightedProperty;

    private static readonly PropertyInfo? HandlersStore = typeof(UIElement).GetProperty("EventHandlersStore", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>A handler of the pointer's arrival, added in code (the posters, the episode tiles, Mira's wash).</summary>
    public static bool Handles(UIElement element)
    {
        if (HandlersStore?.GetValue(element) is not { } store) return false;
        var read = store.GetType().GetMethod("GetRoutedEventHandlers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(RoutedEvent)]);
        return read?.Invoke(store, [UIElement.MouseEnterEvent]) is RoutedEventHandlerInfo[] { Length: > 0 }
            || read?.Invoke(store, [UIElement.MouseMoveEvent]) is RoutedEventHandlerInfo[] { Length: > 0 };
    }

    /// <summary>
    /// Neighbours meant to line up and off by a little: centres of a row's children set to Center, left edges of a
    /// column's children set to Left. 1 to 4 px: more is a choice, less is rounding.
    /// </summary>
    private static void Alignment(Panel panel, FrameworkElement root, string screen, List<UiFinding> found)
    {
        // Only what is on screen: a logo scrolled away above the window is nobody's concern.
        var window = new Rect(root.RenderSize);
        var kids = panel.Children.OfType<FrameworkElement>().Where(x => x is not Image && x.IsVisible && x.Opacity > .05 && x.ActualWidth > 0 && x.ActualHeight > 0)
            .Select(x => (Element: x, Bounds: Bounds(x, root))).Where(x => x.Bounds is { } b && window.Contains(b)).Select(x => (x.Element, Bounds: x.Bounds!.Value)).ToList();
        if (kids.Count < 2) return;
        var rows = panel switch
        {
            StackPanel { Orientation: Orientation.Horizontal } or WrapPanel or DockPanel => [kids],
            Grid => kids.GroupBy(x => Grid.GetRow(x.Element) * 1000 + Grid.GetRowSpan(x.Element)).Select(x => x.ToList()).ToList(),
            _ => new List<List<(FrameworkElement Element, Rect Bounds)>>(),
        };
        foreach (var row in rows) Compare(row.Where(x => x.Element.VerticalAlignment == VerticalAlignment.Center).ToList(), x => x.Top + x.Height / 2, "verticalement");
        var columns = panel switch
        {
            StackPanel { Orientation: Orientation.Vertical } => [kids],
            Grid => kids.GroupBy(x => Grid.GetColumn(x.Element) * 1000 + Grid.GetColumnSpan(x.Element)).Select(x => x.ToList()).ToList(),
            _ => new List<List<(FrameworkElement Element, Rect Bounds)>>(),
        };
        foreach (var column in columns) Compare(column.Where(x => x.Element.HorizontalAlignment == HorizontalAlignment.Left).ToList(), x => x.Left, "horizontalement");

        void Compare(List<(FrameworkElement Element, Rect Bounds)> group, Func<Rect, double> at, string axis)
        {
            if (group.Count < 2) return;
            var values = group.Select(x => at(x.Bounds)).Order().ToList();
            var middle = values[values.Count / 2];
            var worst = group.Select(x => (x.Element, x.Bounds, Off: at(x.Bounds) - middle)).MaxBy(x => Math.Abs(x.Off));
            if (Math.Abs(worst.Off) is > .9 and <= 4)
                found.Add(new("desalignement", "à juger", screen, Describe(worst.Element), worst.Bounds,
                    $"Décalé de {worst.Off:+0.#;-0.#} px {axis} par rapport à ses {group.Count - 1} voisin(s) de « {Describe(panel)} »"));
        }
    }

    /// <summary>What a person would call it: its name for the Narrator, its text, else its kind and its x:Name.</summary>
    public static string Describe(FrameworkElement element)
    {
        var name = AutomationProperties.GetName(element);
        if (string.IsNullOrWhiteSpace(name)) name = element switch
        {
            TextBlock t => t.Text,
            ContentControl { Content: string s } => s,
            HeaderedItemsControl { Header: string h } => h,
            ContentControl { Content: DependencyObject inner } => FirstText(inner),
            _ => null,
        };
        var kind = element.GetType().Name;
        var id = string.IsNullOrEmpty(element.Name) ? "" : "#" + element.Name;
        return string.IsNullOrWhiteSpace(name) ? kind + id : $"{kind}{id} « {Short(name.Trim(), 48)} »";
    }

    private static string? FirstText(DependencyObject node)
    {
        if (node is TextBlock { Text.Length: > 0 } text) return text.Text;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) if (FirstText(child) is { } found) return found;
        return null;
    }

    public static string Short(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    /// <summary>Scale changes on <paramref name="element"/> or inside it: what a poster's hover does (a zoom of 2.5 %).</summary>
    public static double LargestScale(DependencyObject node, int depth = 0)
    {
        var scale = 1d;
        if (node is UIElement { RenderTransform: { } transform } && !transform.Value.IsIdentity) scale = Math.Max(scale, Math.Abs(transform.Value.M11));
        if (node is FrameworkElement { LayoutTransform: { } layout } && !layout.Value.IsIdentity) scale = Math.Max(scale, Math.Abs(layout.Value.M11));
        if (depth < 12) for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) scale = Math.Max(scale, LargestScale(VisualTreeHelper.GetChild(node, i), depth + 1));
        return scale;
    }

    /// <summary>A clickable picture large enough to read as a tile (a poster, an episode, a person, a season), but not a poster card itself.</summary>
    public static IEnumerable<ButtonBase> Tiles(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is UIElement { IsVisible: false }) continue;
            if (node is ButtonBase { IsEnabled: true } button and not MediaCard && button.ActualWidth >= 80 && button.ActualHeight >= 60 && HasPicture(button)) { yield return button; continue; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static bool HasPicture(DependencyObject node, int depth = 0)
    {
        if (node is Image { Source: not null, ActualWidth: >= 60 }) return true;
        if (node is Border { Background: ImageBrush, ActualWidth: >= 60 } or Panel { Background: ImageBrush, ActualWidth: >= 60 }) return true;
        if (depth > 8) return false;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (HasPicture(VisualTreeHelper.GetChild(node, i), depth + 1)) return true;
        return false;
    }
}
