using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Mira.Desktop.Views;

/// <summary>
/// A drop-down list kept inside Mira's window: under its box when there is room, above it otherwise, never taller than
/// the room left (340 at most, the style's). WPF only keeps a popup on the screen: at the bottom of the window (the
/// Subtitles filter, Settings → Quality) the list ran past the window's edge (--ui-audit, 2026-11-14).
/// </summary>
public static class DropDownRoom
{
    public static readonly DependencyProperty FitsWindowProperty = DependencyProperty.RegisterAttached("FitsWindow", typeof(bool), typeof(DropDownRoom), new PropertyMetadata(false, Changed));
    public static bool GetFitsWindow(DependencyObject element) => (bool)element.GetValue(FitsWindowProperty);
    public static void SetFitsWindow(DependencyObject element, bool value) => element.SetValue(FitsWindowProperty, value);

    /// <summary>The list's own height at most, as the template sets it.</summary>
    public const double Tallest = 340;
    /// <summary>Kept from the window's edge, and what the list's frame adds around its scroller (gap, padding, border).</summary>
    private const double Edge = 8, Frame = 6 + 2 * 6 + 2;

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ComboBox box) return;
        if ((bool)e.NewValue) box.DropDownOpened += Opened; else box.DropDownOpened -= Opened;
    }

    private static void Opened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox box || box.Template?.FindName("PART_Popup", box) is not Popup popup || popup.Child is not FrameworkElement frame) return;
        if (Window.GetWindow(box)?.Content is not FrameworkElement root || !root.IsAncestorOf(box)) return;
        var top = box.TransformToAncestor(root).Transform(new Point(0, 0)).Y;
        var below = root.ActualHeight - top - box.ActualHeight - Edge - Frame;
        var above = top - Edge - Frame;
        var scroller = Find<ScrollViewer>(frame);
        var under = below >= Tallest || below >= above;
        popup.Placement = under ? PlacementMode.Bottom : PlacementMode.Top;
        frame.Margin = under ? new Thickness(0, 6, 0, 0) : new Thickness(0, 0, 0, 6);
        if (scroller is not null) scroller.MaxHeight = Math.Max(96, Math.Min(Tallest, under ? below : above));
    }

    private static T? Find<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T found) return found;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find<T>(System.Windows.Media.VisualTreeHelper.GetChild(node, i)) is { } inside) return inside;
        return node is Decorator { Child: { } child } ? Find<T>(child) : null;
    }
}
