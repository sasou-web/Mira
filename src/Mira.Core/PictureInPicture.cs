namespace Mira.Core;

/// <summary>
/// « Image dans l’image » of Mira for Windows and Mac: the video alone in a small window above every other, outside Mira's
/// window, moved and resized freely, its subtitles drawn by mpv as in the player. Where it opens, its size as an edge is
/// pulled, where it settles once let go, and the subtitles' size at its size. Places and sizes in device pixels of the
/// virtual screen (<see cref="ScreenFit.Pixels"/>), unless said in device-independent pixels.
/// </summary>
public static class PictureInPicture
{
    /// <summary>The width it opens at the first time, and the least its shorter side goes to, in device-independent pixels.</summary>
    public const double Width = 480, MinimumSide = 144;
    /// <summary>The most of the screen's work area it covers, across and down.</summary>
    public const double MaximumShare = .6;
    /// <summary>Between it and the screen's edges where it first opens, in device-independent pixels.</summary>
    public const double Gap = 24;
    /// <summary>The band along its edges that resizes it rather than moves it, in device-independent pixels (twice at the corners).</summary>
    public const double Grip = 8;
    /// <summary>The font size subtitles are raised to at least, in device-independent pixels (<see cref="SubtitleScale"/>).</summary>
    public const double Readable = 20;

    /// <summary>The video's shape, width over height: 16:9 while it is unknown, held between 1:2 and 2.4:1.</summary>
    public static double Aspect(double width, double height) =>
        width > 0 && height > 0 && double.IsFinite(width / height) ? Math.Clamp(width / height, .5, 2.4) : 16d / 9;

    /// <summary>Where it was left, as saved: its top-left corner in device pixels, its width in device-independent pixels.</summary>
    public readonly record struct Spot(double X, double Y, double Width);

    /// <summary>An edge or a corner pulled, numbered as Windows numbers them (WMSZ_LEFT 1 to WMSZ_BOTTOMRIGHT 8).</summary>
    public enum Edge { Left = 1, Right, Top, TopLeft, TopRight, Bottom, BottomLeft, BottomRight }

    /// <summary>The narrowest and the widest it can be on <paramref name="screen"/> at this shape (device pixels).</summary>
    public static (double Minimum, double Maximum) Widths(ScreenFit.Screen screen, double aspect)
    {
        var minimum = MinimumSide * screen.Scale * Math.Max(1, aspect);
        var maximum = Math.Min(screen.WorkArea.Width * MaximumShare, screen.WorkArea.Height * MaximumShare * aspect);
        return (minimum, Math.Max(minimum, maximum));
    }

    /// <summary>
    /// Where it opens: where it was left when most of it would still be on one of the screens present, kept inside that
    /// screen; otherwise at the bottom right of the screen showing Mira's middle (<paramref name="mira"/>), or the first.
    /// Null without a usable screen.
    /// </summary>
    public static ScreenFit.Pixels? Open(IReadOnlyList<ScreenFit.Screen> screens, Spot? remembered, (double X, double Y)? mira, double aspect)
    {
        var usable = Usable(screens);
        if (usable.Count == 0) return null;
        if (remembered is { } spot && double.IsFinite(spot.X) && double.IsFinite(spot.Y) && spot.Width > 0 && double.IsFinite(spot.Width))
        {
            ScreenFit.Screen? best = null; var shown = 0d; var place = default(ScreenFit.Pixels);
            foreach (var screen in usable)
            {
                var (width, height) = Sized(screen, spot.Width * screen.Scale, aspect);
                var window = new ScreenFit.Pixels(spot.X, spot.Y, width, height);
                var overlap = window.Overlap(screen.WorkArea);
                if (overlap > shown) { shown = overlap; best = screen; place = window; }
            }
            if (best is not null && shown >= place.Width * place.Height / 2) return Inside(place, best.WorkArea);
        }
        var target = (mira is { X: var x, Y: var y } ? usable.FirstOrDefault(s => s.WorkArea.Contains(x, y)) : null) ?? usable[0];
        var (w, h) = Sized(target, Width * target.Scale, aspect);
        var gap = Gap * target.Scale; var area = target.WorkArea;
        return Inside(new(area.Right - gap - w, area.Bottom - gap - h, w, h), area);
    }

    /// <summary>The edge or corner under the pointer at (<paramref name="x"/>, <paramref name="y"/>) of a window of this size; null inside.</summary>
    public static Edge? EdgeAt(double x, double y, double width, double height, double grip = Grip)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x > width || y > height) return null;
        var corner = grip * 2;
        bool left = x < grip, right = x > width - grip, top = y < grip, bottom = y > height - grip;
        bool nearLeft = x < corner, nearRight = x > width - corner, nearTop = y < corner, nearBottom = y > height - corner;
        if ((left || top) && nearLeft && nearTop) return Edge.TopLeft;
        if ((right || top) && nearRight && nearTop) return Edge.TopRight;
        if ((left || bottom) && nearLeft && nearBottom) return Edge.BottomLeft;
        if ((right || bottom) && nearRight && nearBottom) return Edge.BottomRight;
        return left ? Edge.Left : right ? Edge.Right : top ? Edge.Top : bottom ? Edge.Bottom : null;
    }

    /// <summary>
    /// The window <paramref name="start"/> once <paramref name="edge"/> was pulled by (<paramref name="dx"/>, <paramref name="dy"/>):
    /// the video's shape kept, between the narrowest and the widest on its screen. A side drives the size, a corner the
    /// direction it was pulled the most; the opposite side or corner stays where it was, a side pulled alone keeps the top
    /// or the left one.
    /// </summary>
    public static ScreenFit.Pixels Resize(ScreenFit.Pixels start, Edge edge, double dx, double dy, double aspect, ScreenFit.Screen screen)
    {
        bool left = edge is Edge.Left or Edge.TopLeft or Edge.BottomLeft, right = edge is Edge.Right or Edge.TopRight or Edge.BottomRight;
        bool top = edge is Edge.Top or Edge.TopLeft or Edge.TopRight, bottom = edge is Edge.Bottom or Edge.BottomLeft or Edge.BottomRight;
        var across = start.Width + (left ? -dx : right ? dx : 0);
        var down = (start.Height + (top ? -dy : bottom ? dy : 0)) * aspect;
        var wanted = (left || right) && (top || bottom) ? Math.Abs(across - start.Width) >= Math.Abs(down - start.Width) ? across : down
            : left || right ? across : down;
        var (width, height) = Sized(screen, wanted, aspect);
        var x = left ? start.Right - width : start.Left;
        var y = top ? start.Bottom - height : start.Top;
        return new(Math.Round(x), Math.Round(y), width, height);
    }

    /// <summary>
    /// The window at another shape (the next title, or the video's shape once known): as wide, within its limits on its
    /// screen, held by its side nearest the screen's edge, and inside the screen's work area.
    /// </summary>
    public static ScreenFit.Pixels Refit(ScreenFit.Pixels window, double aspect, ScreenFit.Screen screen)
    {
        var (width, height) = Sized(screen, window.Width, aspect);
        var area = screen.WorkArea;
        var right = window.Left + window.Width / 2 > area.Left + area.Width / 2;
        var below = window.Top + window.Height / 2 > area.Top + area.Height / 2;
        return Inside(new(right ? window.Right - width : window.Left, below ? window.Bottom - height : window.Top, width, height), area);
    }

    /// <summary>Where it rests once let go after a move: wholly inside the work area of the screen showing most of it.</summary>
    public static ScreenFit.Pixels Settle(ScreenFit.Pixels window, IReadOnlyList<ScreenFit.Screen> screens) =>
        ScreenOf(window, screens) is { } screen ? Inside(window, screen.WorkArea) : window;

    /// <summary>The screen showing most of the window, else the one nearest its middle; null without a usable screen.</summary>
    public static ScreenFit.Screen? ScreenOf(ScreenFit.Pixels window, IReadOnlyList<ScreenFit.Screen> screens)
    {
        var usable = Usable(screens);
        if (usable.Count == 0) return null;
        var best = usable.MaxBy(s => s.WorkArea.Overlap(window))!;
        if (best.WorkArea.Overlap(window) > 0) return best;
        var (x, y) = (window.Left + window.Width / 2, window.Top + window.Height / 2);
        return usable.MinBy(s => Math.Pow(Math.Clamp(x, s.WorkArea.Left, s.WorkArea.Right) - x, 2) + Math.Pow(Math.Clamp(y, s.WorkArea.Top, s.WorkArea.Bottom) - y, 2));
    }

    /// <summary>What to save of where it is, for <see cref="Open"/> next time.</summary>
    public static Spot Remember(ScreenFit.Pixels window, ScreenFit.Screen screen) => new(window.Left, window.Top, Math.Round(window.Width / screen.Scale));

    /// <summary>
    /// mpv's sub-scale in a window <paramref name="height"/> device-independent pixels high. mpv sizes text subtitles
    /// to the window's height (<paramref name="fontSize"/> for a window 720 high): in a small window they would be too
    /// small to read. Raised to about <see cref="Readable"/>, never lowered, at most doubled.
    /// </summary>
    public static double SubtitleScale(double fontSize, double height)
    {
        if (!(fontSize > 0) || !(height > 0) || !double.IsFinite(fontSize * height)) return 1;
        return Math.Round(Math.Clamp(Readable / (fontSize * height / 720), 1, 2), 2);
    }

    /// <summary>
    /// How its controls fit its size, in device-independent pixels: the player's sizes from 340 × 200, lighter bars,
    /// buttons and spaces below; the title from 300 wide only, the 10 s jumps from 200 only.
    /// </summary>
    public readonly record struct Controls(bool Small, bool Title, bool Jumps)
    {
        /// <summary>From the window's edges to the rows of buttons: just clear of the band that resizes it (<see cref="Grip"/>).</summary>
        public const double Margin = Grip + .5;
        /// <summary>The top and bottom shades' height.</summary>
        public double Bar => Small ? 46 : 56;
        /// <summary>The round buttons of the corners (back to Mira, close, subtitles) and their glyph.</summary>
        public double Corner => Small ? 26 : 32;
        public double CornerGlyph => Small ? 15 : 17;
        /// <summary>The play button in the middle, the jumps beside it, and the space between them.</summary>
        public double Play => Small ? 40 : 52;
        public double PlayGlyph => Small ? 20 : 26;
        public double Jump => Small ? 32 : 40;
        public double JumpGlyph => Small ? 17 : 21;
        public double Spacing => Small ? 8 : 14;
        /// <summary>From the window's edges to the title, the time and the corners' glyphs: the same on every side.</summary>
        public double Inset => Margin + (Corner - CornerGlyph) / 2;
        public double TextSize => Small ? 11.5 : 12.5;
        /// <summary>How high the subtitles go while the controls show: above the time and the subtitles' button.</summary>
        public double Lift => Inset + CornerGlyph + 8;
    }

    /// <summary>The controls for a window <paramref name="width"/> × <paramref name="height"/> device-independent pixels.</summary>
    public static Controls ControlsFor(double width, double height)
    {
        var small = !(width >= 340 && height >= 200);
        return new(small, !small || width >= 300, !(width < 200));
    }

    /// <summary>
    /// Where the subtitles go while the controls show: mpv's sub-pos that keeps them <paramref name="lift"/> above the
    /// bottom of a window <paramref name="height"/> high (device-independent pixels), sub-margin-y at 0 meanwhile. A share
    /// of the window, whatever the subtitles' size: a recent mpv grows sub-margin-y with sub-scale (the small window's
    /// larger subtitles went halfway up, over the play button), an older one does not. Null: their own height
    /// (<paramref name="own"/>, from Réglages) is already above the controls, nothing changes. Never above the middle.
    /// </summary>
    public static int? SubtitleLift(double lift, double height, int own)
    {
        if (!(lift > 0) || !(height > 0) || !double.IsFinite(lift / height)) return null;
        var position = (int)Math.Clamp(Math.Floor(100 - lift * 100 / height + 1e-9), 50, 100);
        return own <= position ? null : position;
    }

    private static List<ScreenFit.Screen> Usable(IReadOnlyList<ScreenFit.Screen> screens) =>
        screens.Where(s => s.Scale > 0 && double.IsFinite(s.Scale) && s.WorkArea.Width > 0 && s.WorkArea.Height > 0
            && double.IsFinite(s.WorkArea.Left + s.WorkArea.Top + s.WorkArea.Width + s.WorkArea.Height)).ToList();
    private static (double Width, double Height) Sized(ScreenFit.Screen screen, double width, double aspect)
    {
        var (minimum, maximum) = Widths(screen, aspect);
        var sized = Math.Round(Math.Clamp(double.IsFinite(width) ? width : minimum, minimum, maximum));
        return (sized, Math.Round(sized / aspect));
    }
    /// <summary>Moved, never resized, to lie inside <paramref name="area"/>; one larger than it keeps its top-left corner there.</summary>
    private static ScreenFit.Pixels Inside(ScreenFit.Pixels window, ScreenFit.Pixels area) =>
        new(Math.Round(Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width))), Math.Round(Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height))), window.Width, window.Height);
}
