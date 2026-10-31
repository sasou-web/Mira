using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>ClearType for Mira's text. WindowChrome's glass frame (the DWM shadow and corners) gives the window a render
/// target with an alpha channel, and WPF then draws all its text in grayscale; RenderOptions.ClearTypeHint on the window is
/// not enough (a ScrollViewer's clip, a squircle, start layers of their own), so every text element asks for it.
/// The player's layered overlay is the exception (<see cref="LayeredProperty"/>): there the text lies over transparent
/// pixels, where ClearType would leave colour fringes.</summary>
public static class ClearText
{
    /// <summary>Set on the root of a window drawn with per-pixel transparency; inherited by its text.</summary>
    public static readonly DependencyProperty LayeredProperty = DependencyProperty.RegisterAttached("Layered", typeof(bool), typeof(ClearText),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (d, e) =>
        {
            if (d is TextBlock or TextBox) RenderOptions.SetClearTypeHint(d, (bool)e.NewValue ? ClearTypeHint.Auto : ClearTypeHint.Enabled);
        }));
    public static void SetLayered(DependencyObject element, bool value) => element.SetValue(LayeredProperty, value);

    public static void Register()
    {
        RenderOptions.ClearTypeHintProperty.OverrideMetadata(typeof(TextBlock), new UIPropertyMetadata(ClearTypeHint.Enabled));
        RenderOptions.ClearTypeHintProperty.OverrideMetadata(typeof(TextBox), new UIPropertyMetadata(ClearTypeHint.Enabled));
    }
}
