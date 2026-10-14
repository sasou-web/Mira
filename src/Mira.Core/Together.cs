using System.Globalization;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// « Regarder ensemble » (Jellyfin's SyncPlay), what needs no window: the server's clock, where and when a command of
/// the group puts the player, the group's words. The same as Mira web's sync.js: tests/fixtures/syncplay.json.
/// </summary>
public static class Together
{
    /// <summary>A group as Jellyfin describes it; Participants names each account once, whatever its devices.</summary>
    public sealed record Group(string GroupId, string GroupName, List<string> Participants, string State);
    /// <summary>An answer of GetUtcTime: sent and received here, received and sent there, in milliseconds since 1970.</summary>
    public sealed record ClockSample(long Sent, long Received, long ServerIn, long ServerOut);
    /// <summary>What a command asks: Action play (in Wait ms), pause, seek or stop, at Position seconds.</summary>
    public sealed record Plan(string Action, long Wait, double Position);
    /// <summary>The group's title and where it starts.</summary>
    public sealed record Entry(string ItemId, string PlaylistItemId, long StartTicks);

    /// <summary>Jellyfin's date (seven decimals) in milliseconds since 1970; null when it is not one.</summary>
    public static long? ServerTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : null;
    /// <summary>Jellyfin's clock minus this PC's, from the answer with the shortest round trip; null without one.</summary>
    public static (long Offset, long Trip)? ClockOffset(IEnumerable<ClockSample> samples)
    {
        (long Offset, long Trip)? best = null;
        foreach (var s in samples)
        {
            var trip = (s.Received - s.Sent) - (s.ServerOut - s.ServerIn);
            if (trip < 0) continue;
            if (best is null || trip < best.Value.Trip)
                best = ((long)Math.Round(((s.ServerIn - s.Sent) + (s.ServerOut - s.Received)) / 2.0, MidpointRounding.AwayFromZero), trip);
        }
        return best;
    }
    /// <summary>
    /// What a SyncPlayCommand asks of this player, on its clock (<paramref name="now"/> in ms since 1970): a play that
    /// arrives late starts further on, where the others are. Null for a command Mira does not know.
    /// </summary>
    public static Plan? CommandPlan(JsonElement data, long offset, long now)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        var position = (data.TryGetProperty("PositionTicks", out var p) && p.TryGetInt64(out var ticks) ? ticks : 0) / 1e7;
        switch (Text(data, "Command"))
        {
            case "Unpause":
                if (ServerTime(Text(data, "When")) is not { } when) return new("play", 0, position);
                var wait = when - offset - now;
                return new("play", Math.Max(0, wait), wait < 0 ? position - wait / 1000.0 : position);
            case "Pause": return new("pause", 0, position);
            case "Seek": return new("seek", 0, position);
            case "Stop": return new("stop", 0, position);
            default: return null;
        }
    }
    /// <summary>Under half a second apart, a jump would be seen more than the gap.</summary>
    public static bool NeedsSeek(double current, double target) => Math.Abs(current - target) >= 0.5;
    /// <summary>The title a PlayQueue update puts on; null when it plays nothing.</summary>
    public static Entry? QueueItem(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Playlist", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var index = data.TryGetProperty("PlayingItemIndex", out var i) && i.TryGetInt32(out var n) ? n : -1;
        if (index < 0 || index >= list.GetArrayLength()) return null;
        var entry = list[index];
        if (Text(entry, "ItemId") is not { } item) return null;
        return new(item, Text(entry, "PlaylistItemId") ?? "", data.TryGetProperty("StartPositionTicks", out var s) && s.TryGetInt64(out var start) ? start : 0);
    }
    public static Group? ParseGroup(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && Text(data, "GroupId") is { } id
            ? new(id, Text(data, "GroupName") ?? "Groupe", data.TryGetProperty("Participants", out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [], Text(data, "State") ?? "")
            : null;
    /// <summary>« Groupe de mira ».</summary>
    public static string GroupName(string user) => "Groupe de " + user;
    /// <summary>« 1 personne », « 2 personnes ».</summary>
    public static string People(int count) => count <= 1 ? "1 personne" : $"{count} personnes";
    /// <summary>« Groupe de mira · 2 personnes ».</summary>
    public static string GroupText(Group group) => $"{group.GroupName} · {People(Math.Max(1, group.Participants.Count))}";
    /// <summary>
    /// The address of Jellyfin to put in an invitation: a Jellyfin on this computer at its Tailscale address when
    /// Tailscale is connected here (a friend reaches it at home and away), else at its home network's address; another
    /// server keeps the one this app reaches it at.
    /// </summary>
    public static string InviteServer(string server, bool onThisComputer, string? tailnet, string? home)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) return server.TrimEnd('/');
        var host = onThisComputer ? tailnet ?? (uri.IsLoopback ? home : null) : null;
        return (host is null ? uri : new UriBuilder(uri) { Host = host }.Uri).GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }
    /// <summary>The link that opens Mira web on the group: the friend signs in with an account of this Jellyfin, then is in it.</summary>
    public static string InviteLink(string server, string groupId) => server.TrimEnd('/') + "/Mira/#/ensemble/" + Uri.EscapeDataString(groupId);
    /// <summary>What the invitation says under its link: who may follow it, and from where (Tailscale, the home network).</summary>
    public static List<string> InviteSteps(string link)
    {
        var host = Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Host : "";
        var steps = new List<string>
        {
            "Ton ami ouvre ce lien sur son téléphone ou son ordinateur et se connecte avec son propre compte Jellyfin : il est alors dans le groupe.",
            "Pas encore de compte ? Crée-le-lui dans Jellyfin : tableau de bord → Utilisateurs → +, avec l’accès à tes bibliothèques.",
        };
        var reach = Reach(host);
        if (reach == "tailnet") steps.Add("Hors de chez toi, il lui faut Tailscale : partage-lui l’ordinateur de Jellyfin (login.tailscale.com → Machines → … → Share), il accepte avec son compte Tailscale gratuit.");
        else if (reach == "home") steps.Add("Ce lien ne marche que sur ton Wi-Fi. Pour un ami ailleurs, installe Tailscale sur l’ordinateur de Jellyfin (Mira pour Windows : Guide → Mira sur ton téléphone).");
        steps.Add("Quand il est là, le groupe compte une personne de plus : lance le titre, il démarre chez vous deux au même moment.");
        return steps;
    }
    /// <summary>Where a host is reached from: Tailscale, the home network only, or the Internet.</summary>
    private static string Reach(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes() is [var a, var b, _, _])
            return a == 100 && b is >= 64 and < 128 ? "tailnet" : a == 10 || a == 192 && b == 168 || a == 172 && b is >= 16 and < 32 || a == 127 ? "home" : "internet";
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return "tailnet";
        return !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ? "home" : "internet";
    }
    private static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text ? text : null;
}
