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
    private static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text ? text : null;
}
