namespace Mira.Core;

/// <summary>
/// Jellyfin's conversions on the graphics card of the PC it runs on (« Convertir avec la carte graphique »): the
/// phone's HLS when its file does not play as it is, the last resort in H.264. Jellyfin names each way by a kind
/// (HardwareAccelerationType): nvenc for NVIDIA, qsv for Intel, amf for AMD, none for the processor.
/// </summary>
public static class GpuEncoding
{
    public const string None = "none";
    /// <summary>A graphics card of this PC and Jellyfin's kind for it.</summary>
    public sealed record Card(string Name, string Kind);
    /// <summary>
    /// The kinds this PC's graphics cards offer, one card for each, best first: NVIDIA's encoder is the most reliable
    /// under Jellyfin on Windows, then Intel's, then AMD's. A card of no known maker (Microsoft's basic adapter, a
    /// remote desktop's) offers none.
    /// </summary>
    public static IReadOnlyList<Card> Candidates(IEnumerable<string?> adapters)
    {
        var found = new List<Card>();
        foreach (var name in adapters)
        {
            if (string.IsNullOrWhiteSpace(name) || KindOf(name) is not { } kind || found.Any(x => x.Kind == kind)) continue;
            found.Add(new(name.Trim(), kind));
        }
        return found.OrderBy(x => Array.IndexOf(Order, x.Kind)).ToList();
    }
    private static readonly string[] Order = ["nvenc", "qsv", "amf"];
    private static string? KindOf(string name) =>
        Has(name, "NVIDIA") ? "nvenc"
        : Has(name, "Intel") ? "qsv"
        : Has(name, "AMD") || Has(name, "Radeon") || Has(name, "Advanced Micro Devices") ? "amf"
        : null;
    private static bool Has(string name, string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Has Jellyfin convert with the first of these cards that converts a title (the order of <see cref="Candidates"/>),
    /// each one tried on the start of a film or an episode: Done and its card. Anything else puts Jellyfin's setting back
    /// as it was: Refused (not an administrator), NothingToTry (no film nor episode yet), Failed (no card converted,
    /// an old driver often).
    /// </summary>
    public static async Task<(GpuOutcome Outcome, Card? Card)> EnableAsync(JellyfinClient client, IReadOnlyList<Card> cards, TimeSpan limit, CancellationToken ct = default)
    {
        if (await client.HardwareAccelerationAsync(ct) is not { } before) return (GpuOutcome.Refused, null);
        var kept = false;
        try
        {
            foreach (var card in cards)
            {
                if (!await client.SetHardwareAccelerationAsync(card.Kind, ct)) return (GpuOutcome.Refused, null);
                switch (await client.ConversionWorksAsync(limit, ct: ct))
                {
                    case true: kept = true; return (GpuOutcome.Done, card);
                    case null: return (GpuOutcome.NothingToTry, null);
                }
            }
            return (GpuOutcome.Failed, null);
        }
        finally
        {
            // Even when Mira is closing or the account changes: a card that does not convert must not stay set.
            if (!kept)
            {
                try { await client.SetHardwareAccelerationAsync(before, CancellationToken.None); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }
    /// <summary>The maker of a kind, as the settings name it; the kind itself when Mira does not know it (vaapi set by hand).</summary>
    public static string Maker(string kind) => kind switch { "nvenc" => "NVIDIA", "qsv" => "Intel", "amf" => "AMD", _ => kind };
}

public enum GpuOutcome { Done, Refused, NothingToTry, Failed }
