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
}
