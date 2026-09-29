namespace Mira.Core;

public readonly record struct RgbColor(byte R, byte G, byte B);

public static class ArtworkPalette
{
    // A small quantized histogram favors populated chromatic areas over black letterboxing.
    public static RgbColor Extract(ReadOnlySpan<byte> bgra)
    {
        var bins = new Dictionary<int, (double Weight, double R, double G, double B)>();
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 200) continue;
            double r = bgra[i + 2], g = bgra[i + 1], b = bgra[i];
            var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
            if (max < 32 || min > 225 || max - min < 14) continue;
            var weight = .25 + (max - min) / Math.Max(1, max);
            var key = ((int)r / 32 << 6) | ((int)g / 32 << 3) | (int)b / 32;
            var bin = bins.GetValueOrDefault(key);
            bins[key] = (bin.Weight + weight, bin.R + r * weight, bin.G + g * weight, bin.B + b * weight);
        }
        if (bins.Count == 0) return new(245, 245, 247);
        var dominant = bins.Values.MaxBy(x => x.Weight);
        var channels = new[] { dominant.R / dominant.Weight, dominant.G / dominant.Weight, dominant.B / dominant.Weight };
        // A light tint stays legible both as text on black and behind a dark play icon.
        var lift = Math.Max(.34, (176 - channels.Min()) / Math.Max(1, 255 - channels.Min()));
        byte Tint(double channel) => (byte)Math.Clamp(Math.Round(channel + (255 - channel) * lift), 0, 255);
        return new(Tint(channels[0]), Tint(channels[1]), Tint(channels[2]));
    }
}
