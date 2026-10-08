using System.Globalization;

namespace Mira.Core;

/// <summary>
/// Volume above 100 %: mpv stays at 100 and an audio filter raises the sound, then a limiter holds the peaks under full
/// scale. mpv's own volume above 100 applies after the filters, so loud passages clip; this way they never do.
/// </summary>
public static class VolumeBoost
{
    /// <summary>The highest level the slider reaches, as mpv's own scale (200 = 8 times louder than 100).</summary>
    public const double Maximum = 200;
    /// <summary>The filter's label in mpv's audio chain, and its gain stage, for <c>af-command</c>.</summary>
    public const string Label = "mira-boost", Target = "volume@gain";

    /// <summary>A saved or typed level, back between 0 and <see cref="Maximum"/>; 80 when it is not a number.</summary>
    public static double Clamp(double level) => double.IsFinite(level) ? Math.Clamp(level, 0, Maximum) : 80;

    /// <summary>
    /// A level as mpv's volume (up to 100) and the gain of the boost above it (1 when there is none). mpv's volume is
    /// cubic, so the gain follows the same curve and the slider feels the same on either side of 100.
    /// </summary>
    public static (double Volume, double Gain) Split(double level)
    {
        level = Clamp(level);
        return level <= 100 ? (level, 1) : (100, Math.Pow(level / 100, 3));
    }

    public static string Gain(double gain) => gain.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The filter for mpv's <c>af</c> list: the gain in floating point, then a limiter at −0.4 dB that adds no gain of its own.</summary>
    public static string Filter(double gain) => $"@{Label}:lavfi=[{Target}=volume={Gain(gain)}:precision=float,alimiter=limit=0.95:level=disabled]";

    /// <summary>What Jellyfin is told: its scale stops at 100.</summary>
    public static int Reported(double level) => (int)Math.Round(Math.Min(100, Clamp(level)));

    /// <summary>The level as shown beside the slider, « 135 % ».</summary>
    public static string Text(double level) => $"{Math.Round(Clamp(level)).ToString(CultureInfo.InvariantCulture)} %";
}
