using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mira.Mac.Views;

/// <summary>
/// A row that scrolls sideways fades out at an edge where more of it waits, and only there (as on Windows: cut sharp by
/// the window's edge, the last genre chip read as a stray line). Nothing faded once the row fits.
/// </summary>
public static class EdgeFade
{
    /// <summary>Width of the fade, in points.</summary>
    public const double Width = 48;

    public static ScrollViewer Across(ScrollViewer row)
    {
        var shown = "";
        void Update()
        {
            var scrollable = row.Extent.Width - row.Viewport.Width;
            var before = row.Offset.X > .5;
            var after = row.Offset.X < scrollable - .5;
            var width = row.Bounds.Width;
            var key = before || after ? $"{before}{after}{Math.Round(width)}" : "";
            if (key == shown) return;
            shown = key;
            if (key.Length == 0 || width < Width * 2) { row.OpacityMask = null; return; }
            var part = Width / width;
            row.OpacityMask = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(before ? Colors.Transparent : Colors.Black, 0),
                    new GradientStop(Colors.Black, part),
                    new GradientStop(Colors.Black, 1 - part),
                    new GradientStop(after ? Colors.Transparent : Colors.Black, 1),
                },
            };
        }
        row.ScrollChanged += (_, _) => Update();
        row.SizeChanged += (_, _) => Update();
        return row;
    }
}
