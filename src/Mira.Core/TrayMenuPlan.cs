namespace Mira.Core;

/// <summary>
/// The menu of Mira's icon in Windows' notification area (« Carte », chosen by ogsas on 2026-11-14): what plays as a card
/// (picture, title, progress, stop, −10 s, play or pause, +10 s, next episode), then Open Mira, Minimize to the notification
/// area and Quit Mira. What it shows and where it opens; drawn by Mira for Windows (Views/TrayMenuWindow.cs).
/// </summary>
public static class TrayMenuPlan
{
    /// <summary>The menu's width, in device-independent pixels (the card's picture is 16:9 inside it).</summary>
    public const double Width = 320;
    /// <summary>Space between the menu and the taskbar, and kept between the menu and the screen's edges (device-independent pixels).</summary>
    public const double Gap = 10, Margin = 8;

    /// <summary>The parts shown: the card only during a playback (never greyed lines), « Next episode » only with one to go to, « Minimize » only while the window shows.</summary>
    public readonly record struct Parts(bool Card, bool Next, bool Minimize);
    public static Parts For(bool playing, bool canNext, bool windowShown) => new(playing, playing && canNext, windowShown);

    /// <summary>« S1 · E3 · The Base Violence Necessary for Change », « 2018 · Paused ».</summary>
    public static string Line(MediaItem item, bool paused)
    {
        var parts = new List<string>();
        if (item.Type == "Episode")
        {
            if (item.ParentIndexNumber is int season) parts.Add($"S{season}");
            if (item.IndexNumber is int episode) parts.Add(L($"É{episode}", $"E{episode}"));
            if (!string.IsNullOrWhiteSpace(item.Name)) parts.Add(item.Name.Trim());
        }
        else if (item.ProductionYear is int year) parts.Add(year.ToString(Lang.Culture));
        if (paused) parts.Add(L("En pause", "Paused"));
        return string.Join(" · ", parts);
    }

    /// <summary>The share played (0 to 1), and the times under the bar: « 13:42 » and « 40:12 ».</summary>
    public static (double Share, string Elapsed, string Total) Progress(double position, double duration)
    {
        duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        position = double.IsFinite(position) ? Math.Clamp(position, 0, duration) : 0;
        return (duration > 0 ? position / duration : 0, Viewers.Clock(position), Viewers.Clock(duration));
    }

    /// <summary>
    /// Where the menu goes (its visible box, without its shadow), in device pixels, for a click at <paramref name="x"/>,
    /// <paramref name="y"/> on a screen of that work area (without the taskbar) and scale. Over a taskbar at the bottom,
    /// <see cref="Gap"/> above it; under one at the top; beside one on a side. Centred on the icon along the taskbar.
    /// Opened from elsewhere (the overflow window of the notification area, the keyboard): above the pointer. Always
    /// <see cref="Margin"/> inside the work area, its top kept on the screen when it is taller than it.
    /// </summary>
    public static ScreenFit.Pixels Place(double x, double y, ScreenFit.Pixels work, double width, double height, double scale)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        var gap = Math.Round(Gap * scale); var margin = Math.Round(Margin * scale);
        var beside = x >= work.Right || x < work.Left;
        var left = x >= work.Right ? work.Right - gap - width : x < work.Left ? work.Left + gap : x - width / 2;
        var top = y >= work.Bottom ? work.Bottom - gap - height : y < work.Top ? work.Top + gap : beside ? y - height / 2 : y - gap - height;
        left = Clamp(left, work.Left + margin, work.Right - margin - width);
        top = Clamp(top, work.Top + margin, work.Bottom - margin - height);
        return new(Math.Round(left), Math.Round(top), width, height);
    }

    private static double Clamp(double value, double low, double high) => high < low ? low : Math.Clamp(value, low, high);
}
