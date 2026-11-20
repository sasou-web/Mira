using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mira.Core.Family;

/// <summary>
/// The short form of a link that brings a device to a Jellyfin through its administrator's tailnet (ogsas, 2026-11-20:
/// « rendre le lien moins effrayant »): https://miracinema.app/join#&lt;data&gt;, or mira://join#&lt;data&gt; that the page
/// hands to Mira. The data, base64url of a few bytes: what the link is (a home link, a friend's invitation), the
/// server's Tailscale address (IPv4) and port, the Tailscale key without its « tskey-auth- », and the server's path
/// (« /jellyfin ») when it has one. Nothing else: the server's name is read once Mira reaches it, and a friend's
/// invitation is the key itself (the extension keeps its hash). About 95 characters, neither an address nor « tskey »
/// to read. Shared by the apps (HomeLink) and the extension, which makes it; free of anything else of Core.
/// </summary>
public static class JoinLink
{
    public const string Page = "https://miracinema.app/join#";
    public const string App = "mira://join#";
    /// <summary>What a link is: a home link (the home's password follows), a friend's invitation (straight in).</summary>
    public const byte HomeKind = 1, FriendKind = 2;
    private const string KeyPrefix = "tskey-auth-";
    // The kind's high bit: the key kept whole (it did not start with KeyPrefix).
    private const byte WholeKey = 0x80;

    /// <summary>A link: <see cref="Page"/> then its data. Null when the address is not IPv4 or the key or path are too long.</summary>
    public static string? Make(byte kind, IPAddress address, int port, string? path, string key)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || port is < 1 or > 65535 || key.Length is 0 or > 200 || key.Any(c => c > '\u007f')) return null;
        var whole = !key.StartsWith(KeyPrefix, StringComparison.Ordinal);
        var body = Encoding.ASCII.GetBytes(whole ? key : key[KeyPrefix.Length..]);
        var rest = Encoding.UTF8.GetBytes((path ?? "").TrimEnd('/'));
        if (body.Length > 255 || rest.Length > 200) return null;
        var data = new List<byte> { (byte)(kind | (whole ? WholeKey : 0)) };
        data.AddRange(address.GetAddressBytes());
        data.Add((byte)(port >> 8)); data.Add((byte)port);
        data.Add((byte)body.Length);
        data.AddRange(body);
        data.AddRange(rest);
        return Page + Convert.ToBase64String([.. data]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Whether a text is such a link at all (its page or Mira's), read or not.</summary>
    public static bool IsJoinLink(string? text) =>
        text is not null && (text.StartsWith(App, StringComparison.OrdinalIgnoreCase) || text.StartsWith("mira://join/#", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(Page, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The link read: its kind, the server's address (« http://100.x.y.z:8096/path »), the whole key; null for anything
    /// else or damaged data. The address is not checked here (the apps check it is Tailscale's).
    /// </summary>
    public static (byte Kind, string Address, string Key)? Read(string? text)
    {
        if (!IsJoinLink(text) || text!.Length > 1200) return null;
        var data = text[(text.IndexOf('#', StringComparison.Ordinal) + 1)..].Trim();
        if (data.Length == 0 || data.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '='))) return null;
        var base64 = data.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - (base64.Length % 4)) % 4);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        if (bytes.Length < 9) return null;
        var kind = (byte)(bytes[0] & ~WholeKey);
        if (kind is not (HomeKind or FriendKind)) return null;
        var length = bytes[7];
        if (length == 0 || bytes.Length < 8 + length) return null;
        var port = (bytes[5] << 8) | bytes[6];
        if (port == 0) return null;
        var body = Encoding.ASCII.GetString(bytes, 8, length);
        if (body.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        string path;
        try { path = new UTF8Encoding(false, true).GetString(bytes, 8 + length, bytes.Length - 8 - length); }
        catch (ArgumentException) { return null; }
        if (path.Length > 0 && (!path.StartsWith('/') || path.Any(c => char.IsControl(c) || c is '?' or '#' or '@' or '\\' or ' '))) return null;
        var address = new IPAddress(bytes[1..5]);
        var key = (bytes[0] & WholeKey) != 0 ? body : KeyPrefix + body;
        return (kind, $"http://{address}:{port}{path}", key);
    }
}
