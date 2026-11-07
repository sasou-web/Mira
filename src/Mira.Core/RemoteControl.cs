using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mira.Core;

/// <summary>What another device of Jellyfin asks this Mira to do (<see cref="RemoteControl.Read"/>).</summary>
public enum RemoteAction { Play, Pause, Unpause, TogglePause, Stop, Seek, Back, Forward, Next, Previous, SetVolume, VolumeUp, VolumeDown, Mute, Unmute, ToggleMute, Fullscreen, Message }

/// <summary>
/// One command of Jellyfin's remote control. <see cref="ItemId"/> and <see cref="Start"/> (seconds, null when not said)
/// for Play; <see cref="Position"/> (seconds) for Seek; <see cref="Volume"/> (0 to 100) for SetVolume;
/// <see cref="Header"/> and <see cref="Text"/> for Message.
/// </summary>
public sealed record RemoteCommand(RemoteAction Action)
{
    public string? ItemId { get; init; }
    public double? Start { get; init; }
    public double Position { get; init; }
    public int Volume { get; init; }
    public string Header { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>A device Mira can play on, or steer: another Mira (or any player) that takes Jellyfin's remote control.</summary>
/// <param name="Playing">The title it plays now (an episode's own name, its series in <paramref name="Series"/>), null when idle; Position and Duration in seconds; Volume 0 to 100.</param>
public sealed record RemoteTarget(string SessionId, string DeviceId, string Device, string Client, string User, string? Playing, string? PlayingId, string? Series, int? Season, int? Episode,
    bool Paused, double Position, double Duration, int Volume, bool Muted);

/// <summary>
/// Jellyfin's remote control, both ways, through the WebSocket each app already keeps open: Mira for Windows and for
/// Mac say what they take (<see cref="Capabilities"/>, sent each time the socket opens: Jellyfin forgets it when it
/// restarts) and obey <c>Play</c>, <c>Playstate</c> and <c>GeneralCommand</c> (<see cref="Read"/>): « Arrêter » of
/// « En ce moment sur Jellyfin », « Lire sur… » of Jellyfin's pages and of the iPhone, its small remote. The iPhone
/// lists the devices it can play on with <see cref="Targets"/> (Mira/RemoteRules.swift, cases shared in
/// tests/fixtures/remote.json). Nothing new leaves the device: the server already holds every session.
/// </summary>
public static class RemoteControl
{
    /// <summary>The jumps of Rewind and FastForward, as the player's ← and →.</summary>
    public const double Skip = 10;
    /// <summary>VolumeUp and VolumeDown, as the player's ↑ and ↓.</summary>
    public const double VolumeStep = 5;
    private const int Longest = 280;

    /// <summary>Jellyfin's general commands Mira obeys (its GeneralCommandType names), told in <see cref="Capabilities"/>.</summary>
    public static readonly string[] Commands = ["SetVolume", "VolumeUp", "VolumeDown", "Mute", "Unmute", "ToggleMute", "ToggleFullscreen", "DisplayMessage"];

    /// <summary>
    /// The body of <c>POST Sessions/Capabilities/Full</c>: videos, the commands above, and the media control that makes
    /// the session « controllable » (SupportsRemoteControl, CanStop for « En ce moment ») while its socket is open.
    /// </summary>
    public static JsonObject Capabilities() => new()
    {
        ["PlayableMediaTypes"] = new JsonArray("Video"),
        ["SupportedCommands"] = new JsonArray(Commands.Select(x => (JsonNode)x).ToArray()),
        ["SupportsMediaControl"] = true,
        ["SupportsPersistentIdentifier"] = true,
    };

    /// <summary>
    /// The command a message of Jellyfin's WebSocket carries (its MessageType and Data), null for any other message or a
    /// command Mira does not take: a queue to add to rather than play now (PlayNext, PlayLast, instant mix, shuffle), a
    /// seek without a position, a message without words.
    /// </summary>
    public static RemoteCommand? Read(string type, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        switch (type)
        {
            case "Play":
                if (Field(data, "PlayCommand") is { } how && Name(how, PlayCommands) is not "PlayNow") return null;
                if (Field(data, "ItemIds") is not { ValueKind: JsonValueKind.Array } ids) return null;
                var list = ids.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? Id(x.GetString()) : null).ToList();
                var index = Field(data, "StartIndex") is { } at && at.TryGetInt32(out var i) ? i : 0;
                if (index < 0 || index >= list.Count || list[index] is not { } item) return null;
                return new(RemoteAction.Play) { ItemId = item, Start = Ticks(data, "StartPositionTicks") };
            case "Playstate":
                var command = Field(data, "Command") is { } c ? Name(c, PlaystateCommands) : null;
                return command switch
                {
                    "Stop" => new(RemoteAction.Stop),
                    "Pause" => new(RemoteAction.Pause),
                    "Unpause" => new(RemoteAction.Unpause),
                    "PlayPause" => new(RemoteAction.TogglePause),
                    "NextTrack" => new(RemoteAction.Next),
                    "PreviousTrack" => new(RemoteAction.Previous),
                    "Rewind" => new(RemoteAction.Back),
                    "FastForward" => new(RemoteAction.Forward),
                    "Seek" when Ticks(data, "SeekPositionTicks") is { } position => new(RemoteAction.Seek) { Position = position },
                    _ => null,
                };
            case "GeneralCommand":
                var arguments = Field(data, "Arguments") is { ValueKind: JsonValueKind.Object } a ? a : default;
                return (Field(data, "Name") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null) switch
                {
                    "SetVolume" when Argument(arguments, "Volume") is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var level) && double.IsFinite(level)
                        => new(RemoteAction.SetVolume) { Volume = (int)Math.Round(Math.Clamp(level, 0, 100)) },
                    "VolumeUp" => new(RemoteAction.VolumeUp),
                    "VolumeDown" => new(RemoteAction.VolumeDown),
                    "Mute" => new(RemoteAction.Mute),
                    "Unmute" => new(RemoteAction.Unmute),
                    "ToggleMute" => new(RemoteAction.ToggleMute),
                    "ToggleFullscreen" => new(RemoteAction.Fullscreen),
                    "DisplayMessage" when Words(Argument(arguments, "Header")) is var header && Words(Argument(arguments, "Text")) is var text && (header.Length > 0 || text.Length > 0)
                        => new(RemoteAction.Message) { Header = header, Text = text },
                    _ => null,
                };
            default: return null;
        }
    }

    /// <summary>
    /// The player's volume (its slider's scale, above 100 when boosted) after a volume command. Jellyfin's scale stops at
    /// 100 (<see cref="VolumeBoost.Reported"/>): 100 asked of a boosted player leaves the boost alone.
    /// </summary>
    public static double Level(RemoteCommand command, double current) => command.Action switch
    {
        RemoteAction.SetVolume => command.Volume >= 100 && current > 100 ? current : Math.Clamp(command.Volume, 0, 100),
        RemoteAction.VolumeUp => Math.Min(VolumeBoost.Maximum, VolumeBoost.Clamp(current) + VolumeStep),
        RemoteAction.VolumeDown => Math.Max(0, VolumeBoost.Clamp(current) - VolumeStep),
        _ => current,
    };

    /// <summary>Whether a muted player must be unmuted for <paramref name="command"/> (Mute and Unmute say it, ToggleMute changes it).</summary>
    public static bool? Muted(RemoteCommand command, bool muted) => command.Action switch
    {
        RemoteAction.Mute => true,
        RemoteAction.Unmute => false,
        RemoteAction.ToggleMute => !muted,
        _ => null,
    };

    /// <summary>A message on screen: « Header · Text », or either alone.</summary>
    public static string Said(RemoteCommand command) => string.Join(" · ", new[] { command.Header, command.Text }.Where(x => x.Length > 0));

    /// <summary>
    /// The devices this account can play on, from <c>Sessions?controllableByUserId=…</c>: those taking the remote
    /// control and videos, never this device itself, the most recently active first as Jellyfin lists them, one per
    /// device (Jellyfin may keep an older session of it).
    /// </summary>
    public static List<RemoteTarget> Targets(JsonElement sessions, string ownDevice)
    {
        var found = new List<RemoteTarget>();
        if (sessions.ValueKind != JsonValueKind.Array) return found;
        var devices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions.EnumerateArray())
        {
            if (session.ValueKind != JsonValueKind.Object || Text(session, "Id") is not { } id || !Flag(session, "SupportsRemoteControl")) continue;
            var device = Text(session, "DeviceId") ?? id;
            if (device == ownDevice || !devices.Add(device)) continue;
            var media = Field(session, "PlayableMediaTypes") is { ValueKind: JsonValueKind.Array } types ? types.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToList() : [];
            if (!media.Contains("Video")) continue;
            var item = Field(session, "NowPlayingItem") is { ValueKind: JsonValueKind.Object } i ? i : default;
            var state = Field(session, "PlayState") is { ValueKind: JsonValueKind.Object } s ? s : default;
            var playing = item.ValueKind == JsonValueKind.Object;
            found.Add(new(id, device, Text(session, "DeviceName") ?? "", Text(session, "Client") ?? "", Text(session, "UserName") ?? "",
                playing ? Text(item, "Name") ?? "" : null, playing ? Id(Text(item, "Id")) : null,
                playing ? Text(item, "SeriesName") : null, playing ? Number(item, "ParentIndexNumber") : null, playing ? Number(item, "IndexNumber") : null, playing && Flag(state, "IsPaused"),
                Ticks(state, "PositionTicks") ?? 0, Ticks(item, "RunTimeTicks") ?? 0,
                Field(state, "VolumeLevel") is { } volume && volume.TryGetInt32(out var level) ? Math.Clamp(level, 0, 100) : 100, Flag(state, "IsMuted")));
        }
        return found;
    }

    // Jellyfin's enums, by name or by their number (their order in Jellyfin.Data: PlayCommand, PlaystateCommand).
    private static readonly string[] PlayCommands = ["PlayNow", "PlayNext", "PlayLast", "PlayInstantMix", "PlayShuffle"];
    private static readonly string[] PlaystateCommands = ["Stop", "Pause", "Unpause", "NextTrack", "PreviousTrack", "Seek", "Rewind", "FastForward", "PlayPause"];
    private static string? Name(JsonElement value, string[] names) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt32(out var n) && n >= 0 && n < names.Length => names[n],
        _ => null,
    };
    /// <summary>A property by its name whatever its case: Jellyfin writes PascalCase, a proxy or a later version might not.</summary>
    private static JsonElement? Field(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var exact)) return exact;
        foreach (var property in e.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }
    private static string? Text(JsonElement e, string name) => Field(e, name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { Length: > 0 } text ? text : null;
    private static bool Flag(JsonElement e, string name) => Field(e, name) is { ValueKind: JsonValueKind.True };
    private static int? Number(JsonElement e, string name) => Field(e, name) is { } v && v.TryGetInt32(out var n) ? n : null;
    private static double? Ticks(JsonElement e, string name) => Field(e, name) is { } v && v.TryGetInt64(out var ticks) && ticks >= 0 ? ticks / 1e7 : null;
    /// <summary>The general commands' arguments are strings; a number is taken too.</summary>
    private static string? Argument(JsonElement arguments, string name) => Field(arguments, name) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => null,
    };
    /// <summary>An item id as Mira writes them: 32 hexadecimal digits, lowercase, without dashes.</summary>
    private static string? Id(string? id)
    {
        if (id is null) return null;
        var plain = id.Replace("-", "").ToLowerInvariant();
        return plain.Length > 0 && plain.All(Uri.IsHexDigit) ? plain : null;
    }
    /// <summary>Someone's words on screen: one line, control characters out, not too long.</summary>
    private static string Words(string? text)
    {
        if (text is null) return "";
        var line = new string(text.Select(x => char.IsControl(x) ? ' ' : x).ToArray());
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= Longest ? line : line[..(Longest - 1)].TrimEnd() + "…";
    }
}
