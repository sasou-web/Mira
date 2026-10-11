using System.Globalization;

namespace Mira.Core;

/// <summary>What keeps Mira readable for everyone: text contrast, how long a message stays, the size of the text.</summary>
public static class Legibility
{
    /// <summary>
    /// WCAG 2 contrast ratio of a text colour on a background, from 1 to 21; 4.5 is the floor for ordinary text. Colours
    /// are #RRGGBB or #AARRGGBB; a translucent text is blended over the background first.
    /// </summary>
    public static double Contrast(string text, string background)
    {
        var (alpha, r, g, b) = Parse(text); var (_, br, bg, bb) = Parse(background);
        double Blend(double ink, double paper) => ink * alpha + paper * (1 - alpha);
        var front = Luminance(Blend(r, br), Blend(g, bg), Blend(b, bb)); var back = Luminance(br, bg, bb);
        return (Math.Max(front, back) + .05) / (Math.Min(front, back) + .05);
    }
    private static (double A, double R, double G, double B) Parse(string hex)
    {
        var digits = hex.TrimStart('#');
        if (digits.Length is not (6 or 8) || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            throw new FormatException("Not a #RRGGBB or #AARRGGBB colour: " + hex);
        if (digits.Length == 6) value |= 0xFF000000;
        return ((value >> 24) / 255d, (value >> 16 & 0xFF) / 255d, (value >> 8 & 0xFF) / 255d, (value & 0xFF) / 255d);
    }
    private static double Luminance(double r, double g, double b) => .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
    private static double Linear(double c) => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);

    /// <summary>
    /// How long a message stays before leaving on its own: long enough to be read at a calm pace (a second to notice it,
    /// then about 12 characters a second), between 6 and 20 s. A message with a button does not leave on its own.
    /// </summary>
    public static TimeSpan NoticeDuration(string message) => TimeSpan.FromSeconds(Math.Clamp(1 + message.Length / 12d, 6, 20));

    /// <summary>
    /// The zoom of Mira's pages for Windows' « Taille du texte » (100 to 225 %): the text scale, as far as the window
    /// still holds the smallest layout Mira is designed for (<paramref name="minimumWidth"/> × <paramref name="minimumHeight"/>),
    /// and never below 1. Rounded down to a hundredth, so that it never takes that layout below its minimum.
    /// </summary>
    public static double Zoom(double textScale, double width, double height, double minimumWidth, double minimumHeight)
    {
        if (!double.IsFinite(textScale) || textScale <= 1 || !(width > 0) || !(height > 0) || !(minimumWidth > 0) || !(minimumHeight > 0)) return 1;
        var zoom = Math.Min(Math.Min(textScale, 2.25), Math.Min(width / minimumWidth, height / minimumHeight));
        return Math.Max(1, Math.Floor(zoom * 100) / 100);
    }
}

/// <summary>
/// Arrow keys among the cards of a grid or a row, as on a TV: ← → to the previous or next card, ↑ ↓ to the nearest card
/// of the row above or below.
/// </summary>
public static class CardGrid
{
    public readonly record struct Box(double Left, double Top, double Width, double Height)
    {
        public double CenterX => Left + Width / 2;
        public double CenterY => Top + Height / 2;
    }
    public enum Direction { Left, Right, Up, Down }
    /// <param name="cards">The cards in reading order, where they are drawn.</param>
    /// <returns>The card to move to; null at an edge, where the key keeps doing what it did (scroll, or leave the grid).</returns>
    public static int? Neighbour(IReadOnlyList<Box> cards, int current, Direction direction)
    {
        if (current < 0 || current >= cards.Count) return null;
        if (direction == Direction.Left) return current > 0 ? current - 1 : null;
        if (direction == Direction.Right) return current + 1 < cards.Count ? current + 1 : null;
        // The row above or below: the cards whose middle is past this card's top or bottom edge, the nearest of those rows.
        var from = cards[current];
        var beyond = cards.Select((box, index) => (Box: box, Index: index))
            .Where(x => direction == Direction.Up ? x.Box.CenterY < from.Top : x.Box.CenterY > from.Top + from.Height).ToList();
        if (beyond.Count == 0) return null;
        var row = direction == Direction.Up ? beyond.Max(x => x.Box.Top) : beyond.Min(x => x.Box.Top);
        return beyond.Where(x => Math.Abs(x.Box.Top - row) < Math.Max(1, from.Height / 2)).MinBy(x => Math.Abs(x.Box.CenterX - from.CenterX)).Index;
    }
}
