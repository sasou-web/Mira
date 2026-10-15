using System.Globalization;

namespace Mira.Core;

/// <summary>
/// « Taille et style des sous-titres » (Réglages, Windows and Mac): size, a dark box behind the text, its colour, bold and
/// how high on the screen, as mpv options. mpv gives size, colour, bold and box to plain text subtitles only (SRT,
/// WebVTT…): an anime's styled ASS keeps its own, sub-ass-override left at mpv's « scale »; the height raises its
/// dialogue too, never its placed signs. An mpv chosen by hand may predate sub-border-style (0.39): there,
/// sub-back-color with an offset draws the box, and the options it does not know are left out.
/// </summary>
public static class SubtitleStyle
{
    public const string White = "White", Yellow = "Yellow";
    /// <summary>The box: three quarters black, its margin in mpv's scaled pixels, as mpv's own « sub-box » profile.</summary>
    public const string Box = "#C0000000", BoxMargin = "4";
    /// <summary>The text's colour, for mpv and the preview.</summary>
    public static string Color(string? name) => name == Yellow ? "#FFE14D" : "#FFFFFF";
    /// <summary>
    /// The heights offered, as mpv's sub-pos: 100 is its own place, just above the bottom. Never below it: libass may cut
    /// a text subtitle under 100.
    /// </summary>
    public static readonly IReadOnlyList<(int Position, string Label)> Heights = [(100, "En bas"), (94, "Un peu plus haut"), (88, "Plus haut"), (82, "Bien plus haut")];
    /// <summary>The height offered closest to a saved one (a hand-edited settings file may hold anything).</summary>
    public static int Height(int position) => Heights.MinBy(x => Math.Abs(x.Position - position)).Position;

    /// <summary>The options to give mpv, in order; a null value: mpv's own default, what Mira set before is undone.</summary>
    public static IReadOnlyList<(string Name, string? Value)> Options(PlayerSettings settings)
    {
        var box = settings.SubtitleBackground; var height = Height(settings.SubtitleHeight);
        return [
            ("sub-font-size", settings.SubtitleSize.ToString(CultureInfo.InvariantCulture)),
            ("sub-color", settings.SubtitleColor == Yellow ? Color(Yellow) : null),
            ("sub-bold", settings.SubtitleBold ? "yes" : null),
            ("sub-pos", height == 100 ? null : height.ToString(CultureInfo.InvariantCulture)),
            ("sub-border-style", box ? "background-box" : null),
            ("sub-back-color", box ? Box : null),
            ("sub-shadow-offset", box ? BoxMargin : null),
            ("sub-outline-size", box ? "0" : null)];
    }
}
