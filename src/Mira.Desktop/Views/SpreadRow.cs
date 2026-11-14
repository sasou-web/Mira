using System.Windows;
using System.Windows.Controls;

namespace Mira.Desktop.Views;

/// <summary>
/// Two children on one line, the first at the left and the second at the right end; the second under the first when they
/// do not both fit whole (« dans ta bibliothèque » beside a result's title, or under it in a small window). One child: the
/// first alone.
/// </summary>
public sealed class SpreadRow : Panel
{
    /// <summary>The least room between the two on one line.</summary>
    public double Gap { get; set; } = 14;
    /// <summary>The room between the two, one under the other.</summary>
    public double RowGap { get; set; } = 2;
    private bool _stacked;

    private (UIElement? First, UIElement? Second) Shown()
    {
        UIElement? first = null;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (first is null) first = child; else return (first, child);
        }
        return (first, null);
    }

    protected override Size MeasureOverride(Size available)
    {
        var (first, second) = Shown();
        if (first is null) return default;
        if (second is null) { first.Measure(available); return first.DesiredSize; }
        var whole = new Size(double.PositiveInfinity, available.Height);
        first.Measure(whole); second.Measure(whole);
        _stacked = first.DesiredSize.Width + Gap + second.DesiredSize.Width > available.Width;
        if (!_stacked)
            return new Size(double.IsInfinity(available.Width) ? first.DesiredSize.Width + Gap + second.DesiredSize.Width : available.Width, Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
        first.Measure(available); second.Measure(available);
        return new Size(Math.Max(first.DesiredSize.Width, second.DesiredSize.Width), first.DesiredSize.Height + RowGap + second.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (first, second) = Shown();
        if (first is null) return final;
        if (second is null) { first.Arrange(new Rect(0, 0, Math.Min(first.DesiredSize.Width, final.Width), final.Height)); return final; }
        if (_stacked)
        {
            first.Arrange(new Rect(0, 0, Math.Min(first.DesiredSize.Width, final.Width), first.DesiredSize.Height));
            second.Arrange(new Rect(0, first.DesiredSize.Height + RowGap, Math.Min(second.DesiredSize.Width, final.Width), second.DesiredSize.Height));
            return final;
        }
        var height = final.Height;
        first.Arrange(new Rect(0, (height - first.DesiredSize.Height) / 2, first.DesiredSize.Width, first.DesiredSize.Height));
        second.Arrange(new Rect(final.Width - second.DesiredSize.Width, (height - second.DesiredSize.Height) / 2, second.DesiredSize.Width, second.DesiredSize.Height));
        return final;
    }
}
