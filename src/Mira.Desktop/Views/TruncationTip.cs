using System.Windows;
using System.Windows.Controls;

namespace Mira.Desktop.Views;

/// <summary>A tooltip with the full text on a trimmed <see cref="TextBlock"/>, shown only when the text really is cut off.</summary>
public static class TruncationTip
{
    public static TextBlock Attach(TextBlock block)
    {
        block.ToolTip = block.Text;
        block.ToolTipOpening += (_, e) => { if (!IsCut(block)) e.Handled = true; };
        return block;
    }

    public static bool IsCut(TextBlock block)
    {
        if (block.ActualWidth <= 0 || string.IsNullOrEmpty(block.Text)) return false;
        // Measured on a copy: the real block's own Height or MaxHeight would clamp the answer.
        var full = new TextBlock
        {
            Text = block.Text, FontFamily = block.FontFamily, FontSize = block.FontSize, FontWeight = block.FontWeight, FontStyle = block.FontStyle, FontStretch = block.FontStretch,
            TextWrapping = block.TextWrapping, LineHeight = block.LineHeight, LineStackingStrategy = block.LineStackingStrategy,
        };
        full.Measure(new Size(block.ActualWidth, double.PositiveInfinity));
        return block.TextWrapping == TextWrapping.NoWrap
            ? full.DesiredSize.Width > block.ActualWidth + 0.5
            : full.DesiredSize.Height > block.ActualHeight + 0.5;
    }
}
