using System.Text;
using System.Text.RegularExpressions;

namespace Mira.Core.TorLink;

/// <summary>
/// « Télécharger sur le PC serveur »: what a search or a download sent from Mira web may be, checked the same way by the
/// extension that relays it (Mira.Jellyfin compiles this file) and by Mira for Windows that hands it to TorLink, with
/// the cases of tests/fixtures/torlink-remote.json shared with js/torlink.js.
/// </summary>
public static partial class RemoteDownloads
{
    /// <summary>TorLink's search categories Mira offers: never « games », Jellyfin has no use for them.</summary>
    public static readonly IReadOnlyList<string> Categories = ["movies", "tv", "anime"];
    /// <summary>A search in the three <see cref="Categories"/> at once: three searches of TorLink, their results mixed (<see cref="Combine{T}"/>).</summary>
    public const string All = "all";
    /// <summary>What can be asked of a download already in TorLink: cancelled (its partial files deleted), paused, resumed.</summary>
    public static readonly IReadOnlyList<string> Actions = ["delete", "pause", "resume"];
    public const int MaxQuery = 120, MaxMagnet = 8192, MaxName = 300;
    /// <summary>A month: TorLink's guess beyond that (a few bytes a second) is no time to show.</summary>
    public const long MaxEta = 30L * 24 * 3600;

    /// <summary>
    /// A search as TorLink takes it: spaces gathered, no dash in front (TorLink would read « --x » or « -x » as one of its options),
    /// at most <see cref="MaxQuery"/> characters, no control character; null when nothing is left.
    /// </summary>
    public static string? CleanQuery(string? query)
    {
        if (query is null) return null;
        var text = new StringBuilder(query.Length);
        foreach (var c in query) text.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        var clean = Spaces().Replace(text.ToString(), " ").TrimStart(' ', '-').TrimEnd();
        if (clean.Length > MaxQuery) clean = clean[..MaxQuery].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    public static bool IsCategory(string? category) => category is not null && Categories.Contains(category);
    /// <summary>What Mira web or Mira for Windows or Mac may ask for: one category, or <see cref="All"/>.</summary>
    public static bool IsSearchCategory(string? category) => category == All || IsCategory(category);
    /// <summary>The searches of TorLink a search asks for.</summary>
    public static IReadOnlyList<string> SearchesOf(string category) => category == All ? Categories : [category];

    /// <summary>
    /// The results of the searches of each category, mixed: the first of each, then the second of each, and so on, so
    /// that the best of a category is not put after all of another; a result found twice is kept where it came first.
    /// Fails only when every search failed (the first reason given), otherwise keeps what came.
    /// </summary>
    public static (List<(string Category, T Hit)> Hits, string? Error) Combine<T>(IReadOnlyList<(string Category, IReadOnlyList<T> Hits, string? Error)> parts, Func<T, string> id)
    {
        var found = parts.Where(x => x.Error is null).ToList();
        if (found.Count == 0 && parts.Count > 0) return ([], parts[0].Error);
        var hits = new List<(string, T)>(); var seen = new HashSet<string>();
        for (var rank = 0; found.Any(x => rank < x.Hits.Count); rank++)
            foreach (var part in found)
                if (rank < part.Hits.Count && seen.Add(id(part.Hits[rank]))) hits.Add((part.Category, part.Hits[rank]));
        return (hits, null);
    }
    public static bool IsAction(string? action) => action is not null && Actions.Contains(action);

    /// <summary>
    /// The info hash of a magnet link (40 hexadecimal digits, lower case; a base32 one converted, as TorLink does), or
    /// null when it is not one: « magnet:? » with an urn:btih, no control character, at most <see cref="MaxMagnet"/> characters.
    /// </summary>
    public static string? InfoHash(string? magnet)
    {
        if (magnet is null) return null;
        var text = magnet.Trim();
        if (text.Length > MaxMagnet || !text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || text.Any(char.IsControl)) return null;
        var match = Btih().Match(text);
        if (!match.Success) return null;
        var hash = match.Groups[1].Value;
        return hash.Length == 40 ? hash.ToLowerInvariant() : Base32ToHex(hash);
    }

    /// <summary>The name a magnet link carries (its « dn »), cleaned as <see cref="CleanName"/>; null without one.</summary>
    public static string? DisplayName(string? magnet)
    {
        var query = magnet?.Trim() is { } text && text.IndexOf('?') is var start and >= 0 ? text[(start + 1)..] : null;
        foreach (var pair in (query ?? "").Split('&'))
        {
            if (!pair.StartsWith("dn=", StringComparison.OrdinalIgnoreCase)) continue;
            try { return CleanName(Uri.UnescapeDataString(pair[3..].Replace('+', ' '))); }
            catch (UriFormatException) { return null; }
        }
        return null;
    }

    /// <summary>40 hexadecimal digits: how TorLink names a download.</summary>
    public static bool IsInfoHash(string? id) => id is not null && Hex40().IsMatch(id);
    /// <summary>32 hexadecimal digits: how Jellyfin names a title in its addresses.</summary>
    public static bool IsItemId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

    /// <summary>A name to show: trimmed, without control characters, at most <see cref="MaxName"/> characters; null when empty.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null) return null;
        var clean = Spaces().Replace(new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), " ").Trim();
        if (clean.Length > MaxName) clean = clean[..MaxName].TrimEnd() + "…";
        return clean.Length == 0 ? null : clean;
    }

    private static string? Base32ToHex(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var hex = new StringBuilder(40);
        int buffer = 0, bits = 0;
        foreach (var c in value.ToUpperInvariant())
        {
            var index = alphabet.IndexOf(c);
            if (index < 0) return null;
            buffer = (buffer << 5) | index; bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            hex.Append(((buffer >> bits) & 0xff).ToString("x2"));
            buffer &= (1 << bits) - 1;
        }
        return hex.Length == 40 ? hex.ToString() : null;
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"[?&]xt=urn:btih:([0-9a-f]{40}|[a-z2-7]{32})(?:&|$)", RegexOptions.IgnoreCase)] private static partial Regex Btih();
    [GeneratedRegex("^[0-9a-f]{40}$")] private static partial Regex Hex40();
}
