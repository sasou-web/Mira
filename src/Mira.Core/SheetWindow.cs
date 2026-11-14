using System.Globalization;
using System.Text;

namespace Mira.Core;

/// <summary>
/// Réglages and Aide as a window over Mira, on Windows and on Mac: what its search finds, and the still, blurred picture
/// of the app behind it. The picture is made once as the window opens, small, from the app as it was: a live blur
/// would be drawn again at each frame of a scroll inside the window.
/// </summary>
public static class SheetWindow
{
    /// <summary>The scale the app is drawn at for the picture behind the window: blurred and stretched back, nothing is lost.</summary>
    public const double BackdropScale = .25;

    /// <summary>
    /// True when every word of <paramref name="query"/> is in <paramref name="text"/>, without regard to case or accents
    /// (« sous-titre » finds « Sous-titres », « reglage » finds « Réglages »). A word or phrase of <see cref="Synonyms"/>
    /// is found by any of its group: « update », « mettre à jour » or « nouvelle version » find « Updates » as well as
    /// « Mises à jour », « blurry » finds « Quality » (a friend could not find where Mira updates, 2026-11-14). An empty
    /// search finds everything.
    /// </summary>
    public static bool Matches(string text, string query)
    {
        var words = Fold(query).Split([' ', '\t', '\u00a0', '’', '\''], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var folded = Fold(text);
        for (var i = 0; i < words.Length;)
        {
            var (group, length) = Synonym(words, i);
            if (group is not null) { if (!group.Any(phrase => folded.Contains(phrase, StringComparison.Ordinal))) return false; i += length; continue; }
            if (!folded.Contains(words[i], StringComparison.Ordinal)) return false;
            i++;
        }
        return true;
    }

    /// <summary>
    /// Words people use for a setting that the window names otherwise, folded (<see cref="Fold"/>), in English and in
    /// French: a search for one finds the others. A phrase is matched whole, as typed; a word being typed (« upd ») is
    /// still found as part of a word.
    /// </summary>
    public static readonly string[][] Synonyms =
    [
        ["update", "updates", "upgrade", "mise a jour", "mises a jour", "mettre a jour", "nouvelle version", "new version"],
        ["blurry", "flou", "floue", "pixelated", "pixelise", "quality", "qualite", "resolution", "bitrate", "debit"],
        ["away from home", "ailleurs", "loin de chez", "en deplacement", "remote", "a distance", "tailscale", "vpn"],
        ["language", "languages", "langue", "langues"],
        ["sign out", "log out", "logout", "deconnexion", "se deconnecter", "deconnecter", "switch account", "changer de compte"],
        ["subtitles", "subtitle", "sous-titres", "sous-titre", "sous titres", "captions"],
        ["notification", "notifications", "notify", "prevenir", "alert"],
        ["skip", "intro", "intros", "opening", "generique", "recap", "recaps", "passer"],
        ["back to back", "autoplay", "binge", "enchainer", "episode suivant", "next episode"],
    ];
    /// <summary>The longest phrase of <see cref="Synonyms"/> that <paramref name="words"/> spell from <paramref name="at"/> on, with its group.</summary>
    private static (string[]? Group, int Length) Synonym(string[] words, int at)
    {
        (string[]? Group, int Length) best = (null, 0);
        foreach (var group in Synonyms)
            foreach (var phrase in group)
            {
                var parts = phrase.Split(' ');
                if (parts.Length <= best.Length || at + parts.Length > words.Length) continue;
                var same = true;
                for (var i = 0; i < parts.Length && same; i++) same = words[at + i] == parts[i];
                if (same) best = (group, parts.Length);
            }
        return best;
    }

    /// <summary>Lowercase, accents taken off, typographic apostrophes and dashes made plain.</summary>
    public static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c switch { '’' or '‘' => '\'', '–' or '—' or '‑' => '-', 'œ' or 'Œ' => 'o', _ => char.ToLowerInvariant(c) });
            if (c is 'œ' or 'Œ') builder.Append('e');
        }
        return builder.ToString();
    }

    /// <summary>
    /// The app's picture made soft and dim, in place: three box blurs of <paramref name="radius"/> pixels each way (close
    /// to a Gaussian blur), then a little less colour and light, so that the window stands out in front of it.
    /// <paramref name="pixels"/> holds 4 bytes a pixel (blue, green, red, alpha), rows of <paramref name="width"/>.
    /// </summary>
    public static void Soften(byte[] pixels, int width, int height, int radius = 2, double light = .62, double colour = .85)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4) throw new ArgumentException("The picture is smaller than its size says.", nameof(pixels));
        var line = new int[Math.Max(width, height) * 4];
        for (var pass = 0; pass < 3; pass++)
        {
            for (var y = 0; y < height; y++) Box(pixels, line, y * width * 4, 4, width, radius);
            for (var x = 0; x < width; x++) Box(pixels, line, x * 4, width * 4, height, radius);
        }
        for (var i = 0; i < width * height * 4; i += 4)
        {
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            var grey = .299 * r + .587 * g + .114 * b;
            pixels[i] = Byte((grey + colour * (b - grey)) * light);
            pixels[i + 1] = Byte((grey + colour * (g - grey)) * light);
            pixels[i + 2] = Byte((grey + colour * (r - grey)) * light);
            // The window's background under it: the picture is opaque whatever was see-through in the app.
            pixels[i + 3] = 255;
        }
    }

    /// <summary>One box blur along a row or a column: each pixel the mean of its neighbours, the edges repeated.</summary>
    private static void Box(byte[] pixels, int[] line, int start, int step, int count, int radius)
    {
        for (var i = 0; i < count; i++)
            for (var c = 0; c < 4; c++) line[i * 4 + c] = pixels[start + i * step + c];
        var span = 2 * radius + 1;
        for (var c = 0; c < 4; c++)
        {
            var sum = 0;
            for (var k = -radius; k <= radius; k++) sum += line[Math.Clamp(k, 0, count - 1) * 4 + c];
            for (var i = 0; i < count; i++)
            {
                pixels[start + i * step + c] = (byte)((sum + span / 2) / span);
                sum += line[Math.Min(i + radius + 1, count - 1) * 4 + c] - line[Math.Max(i - radius, 0) * 4 + c];
            }
        }
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
