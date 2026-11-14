using System.Windows;
using System.Windows.Controls;

namespace Mira.Desktop.Views;

/// <summary>
/// A wrapped text cut at a word to fit its block's <see cref="FrameworkElement.MaxHeight"/>, its last punctuation
/// dropped before the « … »: WordEllipsis alone would end on « apparition.… ». The block keeps its own trimming as a
/// net, never needed when the measure here agrees with WPF's (it is the same layout, on a copy).
/// </summary>
public static class FittedText
{
    private static readonly DependencyProperty FullProperty = DependencyProperty.RegisterAttached("Full", typeof(string), typeof(FittedText), new PropertyMetadata(null));
    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached("Watched", typeof(bool), typeof(FittedText), new PropertyMetadata(false));
    private static readonly char[] Trailing = [' ', ',', '.', ';', ':', '!', '?', '—', '–', '-', '·', '…', '(', '«', '"', '\''];

    public static void Set(TextBlock block, string? text)
    {
        block.SetValue(FullProperty, text ?? "");
        if (!(bool)block.GetValue(WatchedProperty))
        {
            block.SetValue(WatchedProperty, true);
            block.SizeChanged += (_, e) => { if (e.WidthChanged) Fit(block); };
        }
        Fit(block);
    }

    private static void Fit(TextBlock block)
    {
        var full = (string?)block.GetValue(FullProperty) ?? "";
        var width = block.ActualWidth;
        var text = width <= 0 || double.IsInfinity(block.MaxHeight) || Fits(block, full, width) ? full : Cut(block, full, width);
        if (block.Text != text) block.Text = text;
    }

    private static string Cut(TextBlock block, string full, double width)
    {
        var words = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The most words whose cut text still fits: fewer words never take more room.
        int low = 0, high = words.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Fits(block, Ending(words, middle), width)) low = middle; else high = middle - 1;
        }
        return low == 0 ? full : Ending(words, low);
    }

    private static string Ending(string[] words, int count) => string.Join(' ', words, 0, count).TrimEnd(Trailing) + "…";

    private static bool Fits(TextBlock block, string text, double width)
    {
        var copy = new TextBlock
        {
            Text = text, FontFamily = block.FontFamily, FontSize = block.FontSize, FontWeight = block.FontWeight, FontStyle = block.FontStyle, FontStretch = block.FontStretch,
            TextWrapping = TextWrapping.Wrap, LineHeight = block.LineHeight, LineStackingStrategy = block.LineStackingStrategy, Padding = block.Padding,
        };
        copy.Measure(new Size(width, double.PositiveInfinity));
        return copy.DesiredSize.Height <= block.MaxHeight + 0.5;
    }
}
