using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>
/// A row that scrolls sideways fades out at an edge where more of it waits, and only there: cut sharp by the window's
/// edge, the last chip read as a stray line (« More genres » at 960 × 600, on the Dell). Nothing faded once the row fits.
/// </summary>
public static class EdgeFade
{
    /// <summary>Width of the fade, in pixels of the app.</summary>
    public const double Width = 48;

    public static readonly DependencyProperty AcrossProperty = DependencyProperty.RegisterAttached("Across", typeof(bool), typeof(EdgeFade), new PropertyMetadata(false, AcrossChanged));
    public static bool GetAcross(DependencyObject obj) => (bool)obj.GetValue(AcrossProperty);
    public static void SetAcross(DependencyObject obj, bool value) => obj.SetValue(AcrossProperty, value);

    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(string), typeof(EdgeFade), new PropertyMetadata(""));

    private static void AcrossChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer row || !(bool)e.NewValue) return;
        // ScrollChanged also comes when the content or the row change width.
        row.ScrollChanged += (_, _) => Update(row);
    }

    private static void Update(ScrollViewer row)
    {
        var before = row.HorizontalOffset > .5;
        var after = row.HorizontalOffset < row.ScrollableWidth - .5;
        var width = row.ActualWidth;
        var key = before || after ? $"{before}{after}{Math.Round(width)}" : "";
        if ((string)row.GetValue(ShownProperty) == key) return;
        row.SetValue(ShownProperty, key);
        if (key.Length == 0 || width < Width * 2) { row.ClearValue(UIElement.OpacityMaskProperty); return; }
        var part = Width / width;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(before ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, part));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - part));
        mask.GradientStops.Add(new GradientStop(after ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        row.OpacityMask = mask;
    }
}
