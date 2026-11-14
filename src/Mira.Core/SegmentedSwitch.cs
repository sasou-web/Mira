namespace Mira.Core;

/// <summary>
/// A segmented switch of Mira for Windows and Mac (Réglages | Aide, and any other row of equal parts with one chosen):
/// the lit pill behind the chosen part slides to the next one instead of jumping (ogsas, 2026-11-14: « une petite
/// animation satisfaisante où ça coulisse »), the ink of the parts following it. One timing for every such switch.
/// </summary>
public static class SegmentedSwitch
{
    /// <summary>How long the pill takes from one part to the next, in milliseconds.</summary>
    public const int SlideMs = 200;

    /// <summary>
    /// The slide's curve, from 0 to 1: a cubic ease-out (WPF's <c>CubicEase</c> EaseOut, Avalonia's <c>CubicEaseOut</c>):
    /// quick off the mark, landing still, never past its place (no bounce).
    /// </summary>
    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var left = 1 - t;
        return 1 - left * left * left;
    }

    /// <summary>The pill's left edge for part <paramref name="index"/> of <paramref name="count"/> equal parts across <paramref name="width"/>.</summary>
    public static double At(int index, int count, double width) => count <= 1 || width <= 0 ? 0 : Math.Clamp(index, 0, count - 1) * width / count;

    /// <summary>
    /// Where a pill standing at <paramref name="offset"/> is, in parts (0 for the first, 1 for the second, 2.5 halfway
    /// between the third and the fourth): a switch shown in place of another (Réglages turned over to Aide, two windows)
    /// starts its pill where the other one's was, even mid-slide.
    /// </summary>
    public static double Part(double offset, int count, double width) => count <= 1 || width <= 0 ? 0 : Math.Clamp(offset * count / width, 0, count - 1);
}
