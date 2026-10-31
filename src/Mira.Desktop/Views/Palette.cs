using System.Windows;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>
/// The window's palette (Themes/Cinema.xaml) for brushes built in code: one name, never a colour written out again.
/// Muted and Faint are read when called, so a view drawn in high contrast gets their lighter ink.
/// </summary>
internal static class Palette
{
    public static Brush Get(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    public static CornerRadius Small => Radius("RadiusSmall");
    public static CornerRadius Control => Radius("RadiusControl");
    public static CornerRadius Card => Radius("RadiusCard");
    public static CornerRadius Sheet => Radius("RadiusSheet");
    private static CornerRadius Radius(string key) => Application.Current?.TryFindResource(key) is CornerRadius radius ? radius : new CornerRadius(10);
    /// <summary>One of the palette's colours, see-through (alpha 0-255): the tile of a notice tinted by its state.</summary>
    public static Brush Tint(string key, byte alpha)
    {
        if (Get(key) is not SolidColorBrush solid) return Brushes.Transparent;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B)); brush.Freeze(); return brush;
    }
}
