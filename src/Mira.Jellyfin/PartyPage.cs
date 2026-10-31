using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Mira.Jellyfin;

/// <summary>
/// « Ta party »: the page a party link opens (/Mira/party/&lt;code&gt;), with what a chat app shows of it (Discord,
/// iMessage: Open Graph), and the link Mira's apps open (mira://party/…). Free of ASP.NET, for the tests;
/// <see cref="PartyController"/> serves it.
/// </summary>
public static class PartyPage
{
    /// <summary>What the host is watching: the title as the card says it, and the item whose poster it shows.</summary>
    public sealed record Watching(string Title, string ImageItemId);

    /// <summary>The page's title, as the card shows it.</summary>
    public const string Title = "Join the Mira party";

    /// <summary>The card's line: who invites, and what they are watching when they are.</summary>
    public static string Description(string host, Watching? watching) => watching is null
        ? $"{host} invites you to watch together in Mira."
        : $"{host} is watching {watching.Title}. Join in Mira.";

    /// <summary>The link of a party at an address (« https://pc.tailnet.ts.net », a base URL after it, or not).</summary>
    public static string Link(string origin, string code) => origin.TrimEnd('/') + "/Mira/party/" + code;

    /// <summary>
    /// The link Mira's apps open: the party's code, the address it was opened at, and this Jellyfin's id, by which an app
    /// signed in to it finds it whatever address it reaches it at.
    /// </summary>
    public static string AppLink(string origin, string code, string? serverId) =>
        "mira://party/" + code + "?server=" + Uri.EscapeDataString(origin.TrimEnd('/')) + (string.IsNullOrEmpty(serverId) ? "" : "&id=" + Uri.EscapeDataString(serverId));

    /// <summary>
    /// What a host is watching, from Jellyfin's sessions (a JSON array): the first of theirs with a title, paused or
    /// not. An episode is « Show · S1 E3 », with the show's poster.
    /// </summary>
    public static Watching? WatchingOf(JsonNode? sessions, string hostId)
    {
        if (sessions is not JsonArray list)
        {
            return null;
        }

        foreach (var session in list)
        {
            if (!SameId(Text(session?["UserId"]), hostId) || session?["NowPlayingItem"] is not JsonObject item || Text(item["Id"]) is not { } id)
            {
                continue;
            }

            var name = Text(item["Name"]) ?? "";
            if (Text(item["Type"]) == "Episode" && Text(item["SeriesName"]) is { Length: > 0 } series)
            {
                var season = Number(item["ParentIndexNumber"]);
                var episode = Number(item["IndexNumber"]);
                var code = season is null || episode is null ? "" : $" · S{season} E{episode}";
                return new(series + code, Text(item["SeriesId"]) ?? id);
            }

            return name.Length == 0 ? null : new(name, id);
        }

        return null;
    }

    /// <summary>
    /// The party page from its template: every « {{name}} » replaced by its value, written for HTML (in text and
    /// attributes alike); a name not given stays empty.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var text = template;
        foreach (var (name, value) in values)
        {
            text = text.Replace("{{" + name + "}}", WebUtility.HtmlEncode(value ?? ""), StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>This computer's address on Tailscale (100.64.0.0/10) among its own, for a link when Funnel is not open.</summary>
    public static string? TailnetAddress(IEnumerable<IPAddress> addresses) => addresses
        .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
        .Select(x => x.GetAddressBytes())
        .Where(b => b[0] == 100 && (b[1] & 0xC0) == 64)
        .Select(b => new IPAddress(b).ToString())
        .FirstOrDefault();

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Replace("-", "", StringComparison.Ordinal), b.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? value) => value is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}
