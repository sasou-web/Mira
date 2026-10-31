using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>ClearType for Mira's text. WindowChrome's glass frame (the DWM shadow and corners) gives the window a render
/// target with an alpha channel, and WPF then draws all its text in grayscale; RenderOptions.ClearTypeHint on the window is
/// not enough (a ScrollViewer's clip, a squircle, start layers of their own), so every element that draws text asks for it:
/// TextBlock, and WPF's internal TextBoxView, which draws what is typed in a TextBox or a PasswordBox.
/// The player's layered overlay is the exception (<see cref="LayeredProperty"/>): there the text lies over transparent
/// pixels, where ClearType would leave colour fringes.</summary>
public static class ClearText
{
    private static readonly Type? TextBoxView = typeof(TextBox).Assembly.GetType("System.Windows.Controls.TextBoxView");

    /// <summary>Set on the root of a window drawn with per-pixel transparency; inherited by its text.</summary>
    public static readonly DependencyProperty LayeredProperty = DependencyProperty.RegisterAttached("Layered", typeof(bool), typeof(ClearText),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (d, e) =>
        {
            if (d is TextBlock || d.GetType() == TextBoxView) RenderOptions.SetClearTypeHint(d, (bool)e.NewValue ? ClearTypeHint.Auto : ClearTypeHint.Enabled);
        }));
    public static void SetLayered(DependencyObject element, bool value) => element.SetValue(LayeredProperty, value);

    public static void Register()
    {
        RenderOptions.ClearTypeHintProperty.OverrideMetadata(typeof(TextBlock), new UIPropertyMetadata(ClearTypeHint.Enabled));
        if (TextBoxView is not null) RenderOptions.ClearTypeHintProperty.OverrideMetadata(TextBoxView, new UIPropertyMetadata(ClearTypeHint.Enabled));
    }
}
