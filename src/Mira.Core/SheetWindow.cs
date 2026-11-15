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
    /// True when every word of <paramref name="query"/> starts a word of <paramref name="text"/>, without regard to case or accents
    /// (« sous-titre » finds « Sous-titres », « reglage » finds « Réglages »). A word or phrase of <see cref="Synonyms"/>
    /// is found by any of its group: « update », « mettre à jour » or « nouvelle version » find « Updates » as well as
    /// « Mises à jour », « blurry » finds « Quality » (a friend could not find where Mira updates, 2026-11-14). An empty
    /// search finds everything.
    /// </summary>
    public static bool Matches(string text, string query) => Matches(text, "", query);

    /// <summary>
    /// As <see cref="Matches(string, string)"/>, for a setting whose <paramref name="name"/> (its title, the names of its
    /// controls) and <paramref name="details"/> (its explanations) are told apart: a word typed may start a longer word of
    /// the name, but in the details it must be the whole word give or take a few letters (<see cref="Slack"/>). « mini »
    /// finds « Mini player », never a « …even minimized. » at the end of an explanation (Dell, 2026-11-15: two results
    /// without a mini-player among them).
    /// </summary>
    public static bool Matches(string name, string details, string query)
    {
        var words = Fold(query).Split([' ', '\t', '\u00a0', '’', '\''], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var named = Fold(name); var told = Fold(details);
        bool Found(string word) => Starts(named, word, int.MaxValue) || told.Length > 0 && Starts(told, word, Slack(word));
        for (var i = 0; i < words.Length;)
        {
            var (group, length) = Synonym(words, i);
            if (group is not null) { if (!group.Any(Found)) return false; i += length; continue; }
            if (!Found(words[i])) return false;
            i++;
        }
        return true;
    }
    /// <summary>
    /// Whether the foot of the window's left column (version, « Help opens with F1 ») stays under the list of sections:
    /// it goes while the list does not fit above it, so that the last sections (« Report a problem », « Downloads ») show
    /// without scrolling in a short window (1152 × 676 on the Dell, 2026-11-15), and comes back once the list fits with it.
    /// <paramref name="extent"/> and <paramref name="viewport"/> are the list's full and shown heights, <paramref name="room"/>
    /// the height the foot takes (margin included).
    /// </summary>
    public static bool FootShown(bool shown, double extent, double viewport, double room)
    {
        if (!(extent > 0) || !(viewport > 0)) return shown;
        return shown ? extent <= viewport + .5 : extent + room <= viewport + .5;
    }

    /// <summary>The letters a word of the details may have past what was typed: a plural, an ending (« update » finds « updates », « noti » « notify »).</summary>
    private static int Slack(string word) => Math.Max(2, word.Length / 2);

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
    /// <summary>
    /// <paramref name="word"/> at the start of a word of <paramref name="text"/>, that word going on for at most
    /// <paramref name="slack"/> more letters: « mini » finds « Minimize », never « administrator » (Dell, 2026-11-14:
    /// five results without a mini-player among them).
    /// </summary>
    private static bool Starts(string text, string word, int slack)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            if (at > 0 && char.IsLetterOrDigit(text[at - 1])) continue;
            var more = 0;
            for (var end = at + word.Length; end < text.Length && char.IsLetterOrDigit(text[end]) && more <= slack; end++) more++;
            if (more <= slack) return true;
        }
        return false;
    }

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

    /// <summary>
    /// <paramref name="pixels"/> made <paramref name="factor"/> times larger each way by bilinear interpolation (pixel
    /// centres kept, edges repeated), 4 bytes a pixel. A small blurred picture stretched twelve times by WPF's
    /// « HighQuality » scaling came out with sharp specks and lines over it (account page, Dell, 2026-11-14): Mira
    /// enlarges it smoothly itself first, so that WPF only stretches it a little.
    /// </summary>
    public static byte[] Enlarge(byte[] pixels, int width, int height, int factor)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4) throw new ArgumentException("The picture is smaller than its size says.", nameof(pixels));
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        int large = width * factor, tall = height * factor;
        var result = new byte[large * tall * 4];
        for (var y = 0; y < tall; y++)
        {
            var sy = Math.Clamp((y + .5) / factor - .5, 0, height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, height - 1); var fy = sy - y0;
            for (var x = 0; x < large; x++)
            {
                var sx = Math.Clamp((x + .5) / factor - .5, 0, width - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, width - 1); var fx = sx - x0;
                for (var c = 0; c < 4; c++)
                {
                    double top = pixels[(y0 * width + x0) * 4 + c] * (1 - fx) + pixels[(y0 * width + x1) * 4 + c] * fx;
                    double bottom = pixels[(y1 * width + x0) * 4 + c] * (1 - fx) + pixels[(y1 * width + x1) * 4 + c] * fx;
                    result[(y * large + x) * 4 + c] = Byte(top * (1 - fy) + bottom * fy);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The whole background of the account page as one opaque picture of <paramref name="width"/> × <paramref name="height"/>
    /// pixels (4 bytes a pixel, blue, green, red, alpha), <paramref name="dip"/> pixels to the window's unit: the banner's
    /// picture (<paramref name="picture"/>, already soft, none = black) filling it as WPF's « UniformToFill » would, at a
    /// fifth of its light, then the dark vignette from (0.5, 0.35) and the faint light of 325 units around the middle,
    /// each step added up in full precision and rounded once, with a little noise so that no ring of equal colour forms.
    /// The vignette and the light were two gradients drawn by WPF over the picture: they left still arcs of specks near
    /// the logo and right of the form whatever the picture was (Dell, 2026-11-14 and 15, blurred by WPF, then by Mira).
    /// </summary>
    public static byte[] AccountBackdrop(byte[]? picture, int pictureWidth, int pictureHeight, int width, int height, double dip)
    {
        if (width <= 0 || height <= 0 || dip <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (picture is not null && (pictureWidth <= 0 || pictureHeight <= 0 || picture.Length < pictureWidth * pictureHeight * 4)) throw new ArgumentException("The picture is smaller than its size says.", nameof(picture));
        var result = new byte[width * height * 4];
        double scale = picture is null ? 1 : Math.Max((double)width / pictureWidth, (double)height / pictureHeight);
        double left = picture is null ? 0 : (width - pictureWidth * scale) / 2, top = picture is null ? 0 : (height - pictureHeight * scale) / 2;
        // The vignette: transparent #212125 in the middle to #ED000000 at its edge, as the gradient was.
        const double edge = 0xED / 255d; double[] middle = [0x25, 0x21, 0x21];
        double glowRadius = 325 * dip;
        Span<double> colour = stackalloc double[3];
        for (var y = 0; y < height; y++)
        {
            var py = Math.Clamp((y + .5 - top) / scale - .5, 0, (picture is null ? 1 : pictureHeight) - 1);
            int y0 = (int)py, y1 = picture is null ? 0 : Math.Min(y0 + 1, pictureHeight - 1); var fy = py - y0;
            for (var x = 0; x < width; x++)
            {
                var px = Math.Clamp((x + .5 - left) / scale - .5, 0, (picture is null ? 1 : pictureWidth) - 1);
                int x0 = (int)px, x1 = picture is null ? 0 : Math.Min(x0 + 1, pictureWidth - 1); var fx = px - x0;
                for (var c = 0; c < 3; c++)
                {
                    if (picture is null) { colour[c] = 0; continue; }
                    double above = picture[(y0 * pictureWidth + x0) * 4 + c] * (1 - fx) + picture[(y0 * pictureWidth + x1) * 4 + c] * fx;
                    double below = picture[(y1 * pictureWidth + x0) * 4 + c] * (1 - fx) + picture[(y1 * pictureWidth + x1) * 4 + c] * fx;
                    colour[c] = (above * (1 - fy) + below * fy) * .2;
                }
                double dx = (x + .5) / width - .5, dy = (y + .5) / height - .35;
                var t = Math.Min(1, Math.Sqrt(dx * dx / (.65 * .65) + dy * dy / (.8 * .8)));
                var shade = edge * t;
                double gx = x + .5 - width / 2d, gy = y + .5 - height / 2d;
                var glow = .035 * Math.Max(0, 1 - Math.Sqrt(gx * gx + gy * gy) / glowRadius);
                // The same noise for the three colours: grey stays grey.
                var noise = Noise(x, y);
                for (var c = 0; c < 3; c++)
                {
                    var value = colour[c] * (1 - shade) + middle[c] * (1 - t) * shade;
                    value = value * (1 - glow) + 255 * glow;
                    result[(y * width + x) * 4 + c] = Byte(value + noise);
                }
                result[(y * width + x) * 4 + 3] = 255;
            }
        }
        return result;
    }

    /// <summary>A value from -0.5 to 0.5 for each pixel, always the same for the same pixel.</summary>
    private static double Noise(int x, int y)
    {
        var h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFF) / 65536d - .5;
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
