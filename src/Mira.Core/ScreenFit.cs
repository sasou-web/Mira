namespace Mira.Core;

/// <summary>The size Mira's window opens at: its own on a large screen, smaller on a small one, always windowed.</summary>
public static class ScreenFit
{
    public readonly record struct Extent(double Width, double Height);
    public sealed record Fit(Extent Size, Extent Minimum);
    /// <summary>Share of the screen's work area (without the taskbar) a window larger than it shrinks to.</summary>
    public const double Share = .9;

    /// <summary>
    /// The default size, shrunk to 90 % of the work area where it does not fit, and the minimum size, lowered to the
    /// work area on a screen smaller than it. Null when the work area is unknown. In device-independent pixels.
    /// </summary>
    public static Fit? Window(Extent size, Extent minimum, Extent area)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        var floor = new Extent(Math.Min(minimum.Width, area.Width), Math.Min(minimum.Height, area.Height));
        return new(new(Math.Max(floor.Width, Math.Min(size.Width, Math.Floor(area.Width * Share))), Math.Max(floor.Height, Math.Min(size.Height, Math.Floor(area.Height * Share)))), floor);
    }
}
