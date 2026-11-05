using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Mira.Mac.Views;

/// <summary>
/// The buttons of a title's page on one line, as Windows' <c>ActionRow</c>: Lecture first, with its words; every other
/// button a rounded square of its icon alone (its words in its tooltip), but those that keep theirs (<see cref="KeepProperty"/>:
/// « Regarder ensemble »). A state button (Favori, Vu…) never keeps words: on or off, the same square, its glyph alone
/// changing (<see cref="StateIcon"/>). Short of room, a kept button goes round too, the smallest
/// <see cref="ShrinkProperty"/> first, and gets its words back when the line widens. A button's words are the
/// <see cref="TextBlock"/> beside its icon in its content panel.
/// </summary>
public sealed class ActionLine : Panel
{
    public const double Round = 46;
    /// <summary>The corners of every button of the line, Lecture's too: one curve along the whole line.</summary>
    public static readonly CornerRadius Corners = new(Round * .32);
    public static readonly AttachedProperty<bool> KeepProperty = AvaloniaProperty.RegisterAttached<ActionLine, Control, bool>("Keep");
    public static readonly AttachedProperty<int> ShrinkProperty = AvaloniaProperty.RegisterAttached<ActionLine, Control, int>("Shrink");
    public static bool GetKeep(Control control) => control.GetValue(KeepProperty);
    public static void SetKeep(Control control, bool value) => control.SetValue(KeepProperty, value);
    public static int GetShrink(Control control) => control.GetValue(ShrinkProperty);
    public static void SetShrink(Control control, int value) => control.SetValue(ShrinkProperty, value);
    static ActionLine() => AffectsParentMeasure<ActionLine>(KeepProperty, ShrinkProperty);

    public double Spacing { get; init; } = 12;

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        var shown = Children.Where(x => x.IsVisible).ToList();
        double width = Spacing * Math.Max(0, shown.Count - 1), height = 0;
        foreach (var child in shown)
        {
            if (child is Button button && !ReferenceEquals(child, Children[0])) { if (GetKeep(button)) Labelled(button); else MakeRound(button); }
            child.Measure(infinite); width += child.DesiredSize.Width; height = Math.Max(height, child.DesiredSize.Height);
        }
        if (width > availableSize.Width)
            foreach (var button in shown.OfType<Button>().Where(x => !ReferenceEquals(x, Children[0]) && GetKeep(x)).OrderBy(GetShrink))
            {
                var before = button.DesiredSize.Width;
                MakeRound(button); button.Measure(infinite);
                width -= before - button.DesiredSize.Width;
                if (width <= availableSize.Width) break;
            }
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children.Where(x => x.IsVisible))
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }
        return finalSize;
    }

    /// <summary>Its icon alone, in a rounded square; its words, if it has some, only in its tooltip and for VoiceOver.</summary>
    private static void MakeRound(Button button)
    {
        if (Words(button) is { } words) words.IsVisible = false;
        button.Width = button.Height = Round; button.Padding = new Thickness(0); button.CornerRadius = Corners;
        button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
    }
    /// <summary>Its words beside its icon, as tall and as rounded as the round ones.</summary>
    private static void Labelled(Button button)
    {
        if (Words(button) is { } words) words.IsVisible = true;
        button.Width = double.NaN; button.Height = Round; button.Padding = new Thickness(20, 0); button.CornerRadius = Corners;
        button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
    }
    private static TextBlock? Words(Button button) => button.Content is Panel panel ? panel.Children.OfType<TextBlock>().FirstOrDefault() : null;
    /// <summary>Whether a button shows its words: for the self-check.</summary>
    public static bool ShowsWords(Button button) => Words(button) is { IsVisible: true };
}
