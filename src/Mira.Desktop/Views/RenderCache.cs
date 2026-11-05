using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>
/// Pictures WPF keeps of parts of the window (<see cref="BitmapCache"/>), drawn again only when something in them
/// changes. Without them, every refresh of the screen draws again each shape, clip, shadow and icon in the part of the
/// window that moved: a poster's rounded corners and its badge's shadow under the wheel, the rail's icons over a page
/// that scrolls beneath them. On a 4K screen at 240 Hz that kept Mira at about one image in two. A picture is made at
/// the window's own scale and laid on whole pixels: as sharp as what is drawn directly.
/// </summary>
internal static class RenderCache
{
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    /// <summary>
    /// The scale WPF makes a picture at by itself: the system's (the main screen's when Windows started), which a window
    /// on a screen of another scale does not have. <see cref="BitmapCache.RenderAtScale"/> makes up the difference.
    /// </summary>
    private static readonly double SystemScale = ReadSystemScale();
    private static double ReadSystemScale()
    {
        try { var dpi = GetDpiForSystem(); return dpi > 0 ? dpi / 96d : 1; }
        catch (EntryPointNotFoundException) { return 1; }
    }
    private static double _scale = SystemScale;
    private static BitmapCache _picture = Make(SystemScale, clearType: true), _plain = Make(SystemScale, clearType: false);
    /// <summary>
    /// A picture whose text keeps ClearType: for what draws its text over something opaque (a badge on a poster, a
    /// page over its background). ClearType over a transparent pixel would leave coloured fringes.
    /// </summary>
    public static BitmapCache Picture => _picture;
    /// <summary>A picture whose text, if any, is drawn in grey: for what lets the page beneath show through (the rail).</summary>
    public static BitmapCache Plain => _plain;
    /// <summary>Raised when the window went to a screen of another scale: the pictures are made again at it.</summary>
    public static event Action? Changed;
    /// <summary>The window's scale (1.75 at 175 %): every picture is made at it from now on.</summary>
    public static void UseScale(double scale)
    {
        if (scale <= 0 || Math.Abs(scale - _scale) < .001) return;
        _scale = scale; _picture = Make(scale, clearType: true); _plain = Make(scale, clearType: false);
        Changed?.Invoke();
    }
    private static BitmapCache Make(double scale, bool clearType)
    {
        var cache = new BitmapCache { RenderAtScale = scale / SystemScale, SnapsToDevicePixels = true, EnableClearType = clearType };
        cache.Freeze(); return cache;
    }
}
