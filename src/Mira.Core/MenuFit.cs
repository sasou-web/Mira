namespace Mira.Core;

/// <summary>
/// A menu opened from a button of a page (« Get season 2 », « Get E6–9 »): kept inside the window. Below the button when
/// it fits, above it otherwise (or on the side with more room), left-aligned with the button unless that crosses the
/// window's right edge (then right-aligned with it), always <see cref="Margin"/> inside the window; narrower than asked in
/// a narrow window, never taller than the larger side's room (what is longer scrolls inside it). Device-independent pixels,
/// in the window's coordinates.
/// </summary>
public static class MenuFit
{
    public readonly record struct Box(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }
    public readonly record struct Bounds(double Width, double MaxHeight);
    public readonly record struct Place(double X, double Y, bool Above);

    /// <summary>Space kept between the menu and the window's edges, and between the menu and its button.</summary>
    public const double Margin = 12, Gap = 8;
    /// <summary>Height a menu may always take, even where neither side of the button has that much room.</summary>
    public const double Shortest = 160;

    /// <summary>The menu's width (the one asked, at most the window's less its margins) and its tallest height.</summary>
    public static Bounds Size(double windowWidth, double windowHeight, Box anchor, double width)
    {
        var w = Math.Max(0, Math.Min(width, windowWidth - 2 * Margin));
        var room = Math.Max(Below(windowHeight, anchor), Above(anchor));
        var floor = Math.Min(Shortest, windowHeight - 2 * Margin);
        return new(w, Math.Max(0, Math.Max(room, floor)));
    }

    /// <summary>Where a menu of that size goes (its top-left corner).</summary>
    public static Place At(double windowWidth, double windowHeight, Box anchor, double width, double height)
    {
        var below = Below(windowHeight, anchor); var above = Above(anchor);
        var up = height > below && (height <= above || above > below);
        var y = Clamp(up ? anchor.Top - Gap - height : anchor.Bottom + Gap, Margin, windowHeight - Margin - height);
        var x = Clamp(anchor.Left + width <= windowWidth - Margin ? anchor.Left : anchor.Right - width, Margin, windowWidth - Margin - width);
        return new(x, y, up);
    }

    private static double Below(double windowHeight, Box anchor) => windowHeight - Margin - anchor.Bottom - Gap;
    private static double Above(Box anchor) => anchor.Top - Gap - Margin;
    private static double Clamp(double value, double low, double high) => high < low ? low : Math.Clamp(value, low, high);
}
