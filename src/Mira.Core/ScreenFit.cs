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

    /// <summary>A rectangle of the virtual screen, in device pixels.</summary>
    public readonly record struct Pixels(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
        public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;
        /// <summary>The area this rectangle shares with <paramref name="other"/>; 0 when they do not meet.</summary>
        public double Overlap(Pixels other) =>
            Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left)) * Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
    }
    /// <summary>A screen: its work area (without the taskbar) in device pixels, and its scale (1 at 96 DPI, 1.5 at 150 %).</summary>
    public sealed record Screen(Pixels WorkArea, double Scale);
    /// <summary>Where the window opens: <see cref="Fit"/> in device-independent pixels, <see cref="Window"/> in device pixels.</summary>
    public sealed record Placement(Fit Fit, Pixels Window, Screen Screen, bool Remembered);
    /// <summary>Part of the title bar that must be on a screen for the window to reopen where it was (device-independent pixels).</summary>
    public const double GripWidth = 160, GripHeight = 32;

    /// <summary>
    /// The screen the window opens on and its place there. Where it was last time (its top-left corner in device pixels)
    /// when enough of its title bar would be on one of the screens present; otherwise centred on the screen under the
    /// pointer, or on the first one (the primary screen). Sized by <see cref="Window(Extent, Extent, Extent)"/> on that
    /// screen's work area and scale, and moved inside that work area. Null without a usable screen.
    /// </summary>
    public static Placement? Place(Extent size, Extent minimum, IReadOnlyList<Screen> screens, (double X, double Y)? remembered, (double X, double Y)? pointer)
    {
        var usable = screens.Where(s => s.Scale > 0 && double.IsFinite(s.Scale) && s.WorkArea.Width > 0 && s.WorkArea.Height > 0
            && double.IsFinite(s.WorkArea.Left + s.WorkArea.Top + s.WorkArea.Width + s.WorkArea.Height)).ToList();
        if (usable.Count == 0) return null;
        Fit FitOn(Screen screen) => Window(size, minimum, new(screen.WorkArea.Width / screen.Scale, screen.WorkArea.Height / screen.Scale))!;
        Screen? target = null;
        if (remembered is { X: var x, Y: var y } && double.IsFinite(x) && double.IsFinite(y))
        {
            // The screen showing most of the window as it would reopen there, provided its title bar can still be grabbed.
            var best = 0d;
            foreach (var screen in usable)
            {
                var fit = FitOn(screen).Size;
                var window = new Pixels(x, y, fit.Width * screen.Scale, fit.Height * screen.Scale);
                var grip = new Pixels(x, y, window.Width, GripHeight * screen.Scale);
                var shown = window.Overlap(screen.WorkArea);
                if (shown > best && grip.Overlap(screen.WorkArea) >= GripWidth * GripHeight * screen.Scale * screen.Scale) { best = shown; target = screen; }
            }
        }
        var remember = target is not null;
        if (target is null && pointer is { X: var px, Y: var py }) target = usable.FirstOrDefault(s => s.WorkArea.Contains(px, py));
        target ??= usable[0];
        var placed = FitOn(target); var area = target.WorkArea;
        var (width, height) = (Math.Round(placed.Size.Width * target.Scale), Math.Round(placed.Size.Height * target.Scale));
        var (left, top) = remember ? remembered!.Value : (area.Left + Math.Floor((area.Width - width) / 2), area.Top + Math.Floor((area.Height - height) / 2));
        // Within the work area; a window larger than it (the minimum size on a tiny screen) keeps its top-left corner there.
        left = Math.Max(area.Left, Math.Min(left, area.Right - width)); top = Math.Max(area.Top, Math.Min(top, area.Bottom - height));
        return new(placed, new(Math.Round(left), Math.Round(top), width, height), target, remember);
    }
}
