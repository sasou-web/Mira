namespace Mira.Core;

/// <summary>
/// Mira's opening on Windows and Mac, once the Home page is ready (ogsas, 2026-11-14, mock-up E « Le clin d'œil » chosen
/// among four, <c>/mnt/project-files/animation-entree-v2/</c>, after A « Le m prend sa place » tried for real): while
/// Mira loads, nothing moves (the mark and « mira » on black). Then, where it is, « mira » fades and the mark shuts like
/// an eyelid, down to a line of light that goes out, while the black lifts off the Home page. « mira » means « look »:
/// the mark winks, you look. Nothing travels, nothing touches the rail (its mark and the page's tile are in place under
/// the black). It adds no wait: it starts when the Home page is ready, which takes clicks from its first frame. Times in
/// seconds since then, solved at any time: the same at 60 Hz or 240 Hz.
/// </summary>
public static class OpeningBlink
{
    /// <summary>« mira » gone.</summary>
    public const double WordOut = .09;
    /// <summary>The mark shut (its height down to <see cref="Slit"/>, its width out to <see cref="Wide"/>).</summary>
    public const double Shut = .12;
    /// <summary>The line of light left, gone <see cref="LineOut"/> after the mark shut.</summary>
    public const double LineOut = .13;
    /// <summary>The black lifts from <see cref="ShadeFrom"/>, gone <see cref="ShadeOut"/> later.</summary>
    public const double ShadeFrom = .09, ShadeOut = .22;
    /// <summary>The mark shut: its height's share, and its width's.</summary>
    public const double Slit = .055, Wide = 1.06;
    /// <summary>All over: the black lifted, the line gone.</summary>
    public const double Length = ShadeFrom + ShadeOut;

    /// <summary>« mira »'s opacity.</summary>
    public static double Word(double t) => 1 - OutCubic(t / WordOut);
    /// <summary>The black's opacity over the Home page.</summary>
    public static double Shade(double t) => 1 - OutCubic((t - ShadeFrom) / ShadeOut);
    /// <summary>How shut the mark is, 0 to 1: slow first, then quicker, as a lid falls (t²).</summary>
    public static double Lid(double t) { var s = Math.Clamp(t / Shut, 0, 1); return s * s; }
    /// <summary>The mark's scale across and down, around its centre.</summary>
    public static (double X, double Y) Scale(double t) { var lid = Lid(t); return (1 + (Wide - 1) * lid, 1 + (Slit - 1) * lid); }
    /// <summary>The mark's opacity: whole while it shuts, then the line goes out.</summary>
    public static double Mark(double t) => 1 - InOutCubic((t - Shut) / LineOut);
    /// <summary>Nothing left to draw.</summary>
    public static bool Done(double t) => t >= Length;

    private static double OutCubic(double t) { t = Math.Clamp(t, 0, 1); return 1 - Math.Pow(1 - t, 3); }
    private static double InOutCubic(double t) { t = Math.Clamp(t, 0, 1); return t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
}
