using System.Windows.Controls;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>
/// A list that scrolls under something that stays (the search field of Téléchargements): its top fades out over 18 px once
/// it has left its start, instead of lines cut short against the edge. Nothing at the start, where its first line shows whole.
/// </summary>
public static class ScrollFade
{
    private static readonly LinearGradientBrush Mask = Frozen();

    private static LinearGradientBrush Frozen()
    {
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new(0, 0), EndPoint = new(0, 18) };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1));
        brush.Freeze();
        return brush;
    }

    public static void Top(ScrollViewer scroll) =>
        scroll.ScrollChanged += (_, _) =>
        {
            var mask = scroll.VerticalOffset > 0.5 ? Mask : null;
            if (!ReferenceEquals(scroll.OpacityMask, mask)) scroll.OpacityMask = mask;
        };
}
