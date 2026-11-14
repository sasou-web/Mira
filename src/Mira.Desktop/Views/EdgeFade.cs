using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>
/// A list fades out at an edge where more of it waits, and only there. Sideways (<c>Across</c>): cut sharp by the
/// window's edge, the last chip read as a stray line (« More genres » at 960 × 600, on the Dell). Up and down
/// (<c>Enabled</c>): the sections of Réglages and the topics of Aide in a short window ended flat under the foot, as if
/// there were nothing more (Dell, 2026-11-14). Nothing faded once the list fits (the mask would only cost a layer).
/// </summary>
public static class EdgeFade
{
    /// <summary>Width of the sideways fade, in pixels of the app.</summary>
    public const double Width = 48;

    /// <summary>Depth of the fade up and down, in pixels of the app.</summary>
    public const double Depth = 36;

    public static readonly DependencyProperty AcrossProperty = DependencyProperty.RegisterAttached("Across", typeof(bool), typeof(EdgeFade), new PropertyMetadata(false, AcrossChanged));
    public static bool GetAcross(DependencyObject obj) => (bool)obj.GetValue(AcrossProperty);
    public static void SetAcross(DependencyObject obj, bool value) => obj.SetValue(AcrossProperty, value);

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(EdgeFade), new PropertyMetadata(false, EnabledChanged));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(string), typeof(EdgeFade), new PropertyMetadata(""));

    private static void AcrossChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer row || !(bool)e.NewValue) return;
        // ScrollChanged also comes when the content or the row change width.
        row.ScrollChanged += (_, _) => Update(row, row.HorizontalOffset, row.ScrollableWidth, row.ActualWidth, Width, sideways: true);
    }

    private static void EnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer list || !(bool)e.NewValue) return;
        list.ScrollChanged += (_, _) => Update(list, list.VerticalOffset, list.ScrollableHeight, list.ActualHeight, Depth, sideways: false);
    }

    private static void Update(ScrollViewer list, double offset, double scrollable, double length, double fade, bool sideways)
    {
        var before = offset > .5;
        var after = offset < scrollable - .5;
        var key = before || after ? $"{before}{after}{Math.Round(length)}" : "";
        if ((string)list.GetValue(ShownProperty) == key) return;
        list.SetValue(ShownProperty, key);
        if (key.Length == 0 || length < fade * 2) { list.ClearValue(UIElement.OpacityMaskProperty); return; }
        var part = fade / length;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = sideways ? new Point(1, 0) : new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(before ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, part));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - part));
        mask.GradientStops.Add(new GradientStop(after ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        list.OpacityMask = mask;
    }
}
