using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mira.Core.Tailscale;

namespace Mira.Core.Family;

/// <summary>
/// A home link of « Le foyer », as the extension makes it (HomeTailnet.Link of Mira.Jellyfin): its page
/// https://miracinema.app/home#&lt;data&gt;, or mira://home#&lt;data&gt; that the page hands to Mira, the data a base64url
/// JSON {v, name, id, url, key, until}. It says where the home's Jellyfin is on its administrator's tailnet and carries a
/// single-use key for this device's node there; never the home's password. Anything else is not a home link (null).
/// </summary>
/// The short form (<see cref="JoinLink"/>, https://miracinema.app/join#…) is read here too: without the server's name
/// (« Jellyfin » until Mira reaches it), its id (empty) nor its end (the key's own, which Tailscale checks); a friend's
/// invitation (<see cref="Friend"/>) is let in by the key itself, without the home's password.
public sealed record HomeLink(string Name, string ServerId, string Address, string Key, DateTimeOffset Until)
{
    /// <summary>A friend's invitation (the extension's /Mira/friend): traded for a session of their account, no password.</summary>
    public bool Friend { get; init; }

    public const string Page = "https://miracinema.app/home#";
    public const string App = "mira://home#";

    /// <summary>The address's host: what the home's node alone reaches (<see cref="TailnetNetwork.UseHome"/>).</summary>
    public string Host => new Uri(Address).Host;

    /// <summary>Whether its key can still bring a new device in: 48 h after it was made (the extension's HomeTailnet.LinkLife).</summary>
    public bool Expired(DateTimeOffset now) => now >= Until;

    /// <summary>Whether a text is a home link at all (its page or Mira's), read or not.</summary>
    public static bool IsHomeLink(string? text) =>
        JoinLink.IsJoinLink(text) || text is not null && (text.StartsWith(App, StringComparison.OrdinalIgnoreCase) || text.StartsWith("mira://home/#", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(Page, StringComparison.OrdinalIgnoreCase));

    /// <summary>The link read, or null: another link, damaged data, an address outside Tailscale's, a key that is not one.</summary>
    public static HomeLink? Read(string? text)
    {
        if (JoinLink.IsJoinLink(text))
        {
            if (JoinLink.Read(text) is not var (kind, at, secret) || !Uri.TryCreate(at, UriKind.Absolute, out var joined) || !TailnetRoute.IsTailnet(joined.Host)
                || !secret.StartsWith("tskey-", StringComparison.Ordinal)) return null;
            return new HomeLink("Jellyfin", "", joined.AbsoluteUri.TrimEnd('/') + "/", secret, DateTimeOffset.MaxValue) { Friend = kind == JoinLink.FriendKind };
        }
        if (!IsHomeLink(text) || text!.Length > 4200) return null;
        var data = text[(text.IndexOf('#', StringComparison.Ordinal) + 1)..].Trim();
        if (data.Length == 0 || data.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '='))) return null;
        var base64 = data.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - (base64.Length % 4)) % 4);
        Data? read;
        try { read = JsonSerializer.Deserialize<Data>(Convert.FromBase64String(base64)); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
        if (read is not { Version: 1, Name: { } name, ServerId: { } id, Address: { } address, Key: { } key }) return null;
        // Only a device of a tailnet: the home's node reaches that one host, nothing a link could aim elsewhere.
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo)
            || !IPAddress.TryParse(url.Host.Trim('[', ']'), out _) || !TailnetRoute.IsTailnet(url.Host)) return null;
        if (!key.StartsWith("tskey-", StringComparison.Ordinal) || key.Length > 200 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        if (id.Length is 0 or > 64 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 80) clean = clean[..80];
        return new HomeLink(clean.Length == 0 ? "Jellyfin" : clean, id, url.AbsoluteUri.TrimEnd('/') + "/", key, DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(read.Until, 0, 253402300799)));
    }

    /// <summary>Whether a server's own id (System/Info/Public) is the link's: Jellyfin writes it with or without dashes.</summary>
    public bool Is(string? serverId) =>
        serverId is { Length: > 0 } && ServerId.Length > 0 && string.Equals(serverId.Replace("-", ""), ServerId.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

    private sealed record Data(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("id")] string? ServerId,
        [property: JsonPropertyName("url")] string? Address,
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("until")] long Until);
}
