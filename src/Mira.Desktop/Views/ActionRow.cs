using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>
/// The buttons of a title's page on one line: when their labels do not fit, those given a <see cref="ShrinkProperty"/>
/// show their icon alone, the smallest number first, until the row fits; their label becomes their tooltip, and stays
/// their name for screen readers (<see cref="Accessible.ContentText"/> reads a hidden label too). Wider again, they get
/// their labels back. A label is the <see cref="TextBlock"/> beside the icon in the button's content panel.
/// </summary>
public sealed class ActionRow : Panel
{
    public static readonly DependencyProperty ShrinkProperty = DependencyProperty.RegisterAttached("Shrink", typeof(int), typeof(ActionRow), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static int GetShrink(DependencyObject element) => (int)element.GetValue(ShrinkProperty);
    public static void SetShrink(DependencyObject element, int value) => element.SetValue(ShrinkProperty, value);
    /// <summary>
    /// Every button after the first one round, its icon alone and its label in its tooltip, whatever the width: a title's
    /// page, where Lecture alone keeps its words.
    /// </summary>
    public static readonly DependencyProperty IconsOnlyProperty = DependencyProperty.Register(nameof(IconsOnly), typeof(bool), typeof(ActionRow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool IconsOnly { get => (bool)GetValue(IconsOnlyProperty); set => SetValue(IconsOnlyProperty, value); }
    private const double Round = 46;
    /// <summary>The padding of an icon alone, as the page's buttons that never have a label (« … »).</summary>
    private static readonly Thickness IconOnly = new(15, 13, 15, 13);
    /// <summary>What a button had before it lost its label: its padding, and whether its tooltip is the label Mira gave it.</summary>
    private sealed record Labelled(Thickness Padding, string? Tip);
    private static readonly ConditionalWeakTable<Button, Labelled> Compact = new();

    /// <summary>Whether <paramref name="button"/> shows its label; true when it shows the one Mira wants.</summary>
    public static bool ShowsLabel(Button button) => !Compact.TryGetValue(button, out _);
    /// <summary>The width the row takes with every label, at its last measure.</summary>
    public double LabelledWidth { get; private set; }

    // A button given new content (« Regarder ensemble » after « Ensemble · 2 personnes ») keeps its size when it is round:
    // the row would never measure it again, and its new label would show, cut, in the circle.
    private static readonly DependencyPropertyDescriptor ContentChange = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(Button));

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is Button removed) ContentChange.RemoveValueChanged(removed, ContentChanged);
        if (visualAdded is Button added) ContentChange.AddValueChanged(added, ContentChanged);
    }
    private void ContentChanged(object? sender, EventArgs e) => InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        var shown = InternalChildren.OfType<UIElement>().Where(x => x.Visibility != Visibility.Collapsed).ToList();
        double width = 0, height = 0;
        if (IconsOnly)
        {
            foreach (var child in shown)
            {
                if (child is Button button && !ReferenceEquals(child, InternalChildren[0])) MakeRound(button);
                child.Measure(infinite); width += child.DesiredSize.Width; height = Math.Max(height, child.DesiredSize.Height);
            }
            LabelledWidth = width;
            return new Size(Math.Min(width, availableSize.Width), height);
        }
        foreach (var child in shown) { if (child is Button button) Show(button, true); child.Measure(infinite); width += child.DesiredSize.Width; }
        LabelledWidth = width;
        if (width > availableSize.Width)
            foreach (var button in shown.OfType<Button>().Where(x => GetShrink(x) > 0).OrderBy(GetShrink))
            {
                var before = button.DesiredSize.Width;
                Show(button, false); button.Measure(infinite);
                width -= before - button.DesiredSize.Width;
                if (width <= availableSize.Width) break;
            }
        foreach (var child in shown) height = Math.Max(height, child.DesiredSize.Height);
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height)); x += child.DesiredSize.Width;
        }
        return finalSize;
    }

    /// <summary>A squircle button of its icon alone: its label, when it has one, goes to its tooltip.</summary>
    private static void MakeRound(Button button)
    {
        Show(button, false);
        button.Width = button.Height = Round; button.Padding = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
        Motion.SetRadius(button, new CornerRadius(Round * .32));
    }
    /// <summary>Shows or hides the label of a button that has one, and marks the way down to it for measuring again.</summary>
    private static void Show(Button button, bool label)
    {
        if (button.Content is not Panel panel || panel.Children.OfType<TextBlock>().FirstOrDefault() is not { } text) return;
        var compact = Compact.TryGetValue(button, out var before);
        if (label != compact && (text.Visibility == Visibility.Visible) == label) return;
        if (label)
        {
            if (before is not null)
            {
                button.Padding = before.Padding;
                if (before.Tip is not null && Equals(button.ToolTip, before.Tip)) button.ClearValue(FrameworkElement.ToolTipProperty);
                Compact.Remove(button);
            }
            text.Visibility = Visibility.Visible;
        }
        else
        {
            // A tooltip of its own (« Garder ce film sur ce PC… ») says more than the label: kept.
            var tip = button.ToolTip is null && text.Text.Length > 0 ? text.Text : null;
            if (!compact) Compact.Add(button, new Labelled(button.Padding, tip));
            button.Padding = IconOnly; if (tip is not null) button.ToolTip = tip;
            text.Visibility = Visibility.Collapsed;
        }
        // A hidden label changes the size of each element between it and the button, which only knows it once measured again.
        for (DependencyObject? node = text; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is UIElement element) element.InvalidateMeasure();
            if (ReferenceEquals(node, button)) break;
        }
    }
}
