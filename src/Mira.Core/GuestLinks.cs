using System.Net;
using System.Text.Json.Serialization;

namespace Mira.Core;

/// <summary>
/// « Inviter par lien » (an administrator of Jellyfin): a link for someone without an account here, who opens it from
/// anywhere (Tailscale Funnel opens /Mira to the Internet) and watches with the host's group. The extension makes each
/// link's account and deletes it with the link (Mira.Jellyfin InvitesController, Mira/invites); Mira web, Windows and
/// Mac say the same things (guests.js).
/// </summary>
public static class GuestLinks
{
    /// <summary>
    /// What Tailscale Funnel runs on the PC of Jellyfin, Jellyfin's port given: /Mira only, never the rest of Jellyfin,
    /// kept across restarts (--bg).
    /// </summary>
    public static string FunnelArguments(int port = 8096) => $"funnel --bg --yes --set-path=/Mira http://127.0.0.1:{port}/Mira";
    /// <summary>The same, to type in PowerShell or a terminal on the PC of Jellyfin.</summary>
    public static string FunnelCommand(int port = 8096) => $"tailscale funnel --bg --set-path=/Mira http://127.0.0.1:{port}/Mira";

    /// <summary>What a link does, said where one is made.</summary>
    public static string Rules => L("Le lien ouvre Mira chez ton invité, sans compte ni Tailscale, et le fait entrer dans ton groupe. Il ne sert qu’à un seul appareil, celui qui l’ouvre en premier. Tu peux le retirer à tout moment ; il s’efface seul après 7 jours sans servir.",
        "The link opens Mira for your guest, with no account and no Tailscale, and brings them into your group. It works on one device only, the first one to open it. You can take it back at any time; it deletes itself after 7 days unused.");

    /// <summary>The words sent with a link.</summary>
    public static string Message(string link) => L($"Regarde avec moi sur Mira : {link}", $"Watch with me on Mira: {link}");

    /// <summary>
    /// What Funnel means, said before it is turned on: the guest's traffic passes through Tailscale's servers (still
    /// encrypted end to end), and the PC's name becomes public.
    /// </summary>
    public static string FunnelPrivacy => L("Funnel fait passer la vidéo de ton invité par les serveurs de Tailscale, chiffrée de bout en bout : Tailscale ne la voit pas. Le nom de ce PC dans Tailscale devient public (son certificat HTTPS est publié). Seul Mira est ouvert, jamais le reste de Jellyfin.",
        "Funnel sends your guest’s video through Tailscale’s servers, encrypted end to end: Tailscale can’t see it. This PC’s Tailscale name becomes public (its HTTPS certificate is published). Only Mira is opened, never the rest of Jellyfin.");

    /// <summary>Where a link is: not opened yet, or on which device and since when it served.</summary>
    public static string State(GuestLink link, DateTimeOffset now)
    {
        var ends = Lang.Day(link.Ends.ToLocalTime().DateTime);
        if (!link.Opened) return L($"Pas encore ouvert · s’efface seul le {ends}", $"Not opened yet · deletes itself on {ends}");
        var device = string.IsNullOrWhiteSpace(link.Device) ? L("un appareil", "a device") : link.Device;
        return now - link.Used < TimeSpan.FromHours(24)
            ? L($"Ouvert sur {device} · a servi à {Lang.Time(link.Used.ToLocalTime().DateTime)} · s’efface seul après 7 jours sans servir",
                $"Opened on {device} · used at {Lang.Time(link.Used.ToLocalTime().DateTime)} · deletes itself after 7 days unused")
            : L($"Ouvert sur {device} · a servi le {Lang.Day(link.Used.ToLocalTime().DateTime)} · s’efface seul le {ends} sans servir",
                $"Opened on {device} · used on {Lang.Day(link.Used.ToLocalTime().DateTime)} · deletes itself on {ends} unless used");
    }

    /// <summary>The words of a refusal the extension gives without its own (an account that is not an administrator, an older extension).</summary>
    public static string? Refused(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden => L("Seuls les comptes administrateurs de Jellyfin peuvent faire des liens d’invités.", "Only Jellyfin administrator accounts can make guest links."),
        HttpStatusCode.NotFound => L("L’extension Mira de ton serveur ne fait pas encore de liens d’invités : mets à jour Mira sur le PC serveur, puis redémarre Jellyfin.",
            "Your server’s Mira plugin can’t make guest links yet: update Mira on the server PC, then restart Jellyfin."),
        _ => null
    };

    /// <summary>
    /// The public address Funnel gives this PC, from <c>tailscale status --json</c> (Self.DNSName, its last dot dropped):
    /// <c>https://pc.tailnet.ts.net</c>; null without one.
    /// </summary>
    public static string? FunnelAddress(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(status);
            if (!json.RootElement.TryGetProperty("Self", out var self) || self.ValueKind != System.Text.Json.JsonValueKind.Object
                || !self.TryGetProperty("DNSName", out var dns) || dns.GetString() is not { Length: > 0 } name) return null;
            name = name.TrimEnd('.');
            return name.Contains('.') ? $"https://{name}" : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Whether <c>tailscale funnel status --json</c> says /Mira is open to the Internet on port 443: a Web handler for
    /// <c>/Mira</c> on <c>&lt;name&gt;:443</c>, and that same host in AllowFunnel.
    /// </summary>
    public static bool FunnelServesMira(string? funnelStatus)
    {
        if (string.IsNullOrWhiteSpace(funnelStatus)) return false;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(funnelStatus);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("Web", out var web) || web.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("AllowFunnel", out var allow) || allow.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            foreach (var host in web.EnumerateObject())
            {
                if (!host.Name.EndsWith(":443", StringComparison.Ordinal)) continue;
                if (!allow.TryGetProperty(host.Name, out var open) || open.ValueKind != System.Text.Json.JsonValueKind.True) continue;
                if (host.Value.TryGetProperty("Handlers", out var handlers) && handlers.ValueKind == System.Text.Json.JsonValueKind.Object
                    && handlers.EnumerateObject().Any(x => x.Name.TrimEnd('/').Equals("/Mira", StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>The page where an administrator of the tailnet turns Funnel on, when <c>tailscale funnel</c> asks for it.</summary>
    public static string? EnablePage(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(output, @"https://login\.tailscale\.com/f/[A-Za-z0-9/?=&_.%-]+");
        return match.Success ? match.Value : null;
    }
}

/// <summary>A link made for a guest, as the extension lists it (GET Mira/invites).</summary>
public sealed record GuestLink(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("created")] DateTimeOffset Created,
    [property: JsonPropertyName("used")] DateTimeOffset Used,
    [property: JsonPropertyName("ends")] DateTimeOffset Ends,
    [property: JsonPropertyName("opened")] bool Opened,
    [property: JsonPropertyName("device")] string? Device,
    [property: JsonPropertyName("link")] string? Link);

/// <summary>The links made, and the address they start with (null until one is given).</summary>
public sealed record GuestLinkList(
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("invites")] List<GuestLink>? Invites);
