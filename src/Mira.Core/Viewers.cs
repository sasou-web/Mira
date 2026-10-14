using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// « En ce moment sur Jellyfin »: who plays what on the server (its Sessions), and how: the file as it is, or converted,
/// on which hardware and why. What a slow title or a « Lecture impossible » needs explained.
/// </summary>
public static class Viewers
{
    /// <summary>
    /// The sessions playing something, as Jellyfin 12.1 lists them (checked with Mira web converting a WebM): its
    /// PlayState and TranscodingInfo. <paramref name="ownDevice"/> is this Mira's: « ce PC ».
    /// </summary>
    public static List<Viewer> Parse(JsonElement sessions, string ownDevice)
    {
        var found = new List<Viewer>();
        if (sessions.ValueKind != JsonValueKind.Array) return found;
        foreach (var session in sessions.EnumerateArray())
        {
            if (session.ValueKind != JsonValueKind.Object || !session.TryGetProperty("NowPlayingItem", out var item) || item.ValueKind != JsonValueKind.Object) continue;
            if (Text(session, "Id") is not { } id) continue;
            var state = session.TryGetProperty("PlayState", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            Conversion? conversion = null;
            if (session.TryGetProperty("TranscodingInfo", out var t) && t.ValueKind == JsonValueKind.Object)
                conversion = new(Flag(t, "IsVideoDirect"), Flag(t, "IsAudioDirect"), Text(t, "VideoCodec"), Text(t, "AudioCodec"),
                    t.TryGetProperty("Bitrate", out var b) && b.TryGetInt64(out var bitrate) ? bitrate : null,
                    t.TryGetProperty("TranscodeReasons", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray() : [],
                    Text(t, "HardwareAccelerationType"));
            var device = Text(session, "DeviceId");
            found.Add(new(id, device == ownDevice, Text(session, "UserName") ?? "", Text(session, "DeviceName") ?? "", Text(session, "Client") ?? "", Text(session, "RemoteEndPoint"),
                Text(item, "Type") ?? "", Text(item, "Name") ?? "", Text(item, "SeriesName"), Number(item, "ParentIndexNumber"), Number(item, "IndexNumber"),
                Seconds(state, "PositionTicks"), Seconds(item, "RunTimeTicks"), state.ValueKind == JsonValueKind.Object && Flag(state, "IsPaused"),
                state.ValueKind == JsonValueKind.Object ? Text(state, "PlayMethod") ?? "" : "", conversion, Flag(session, "SupportsRemoteControl")));
        }
        return found;
    }
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text ? text : null;
    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static int? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
    private static double Seconds(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var ticks) ? ticks / 1e7 : 0;

    /// <summary>« ogsas · iPhone · Tailscale », « ce PC ».</summary>
    public static string Who(Viewer viewer)
    {
        if (viewer.Own) return "ce PC";
        var parts = new List<string> { viewer.User, viewer.Device.Length > 0 ? viewer.Device : viewer.Client };
        if (Place(viewer.Address) is { } place) parts.Add(place);
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }
    /// <summary>Where the device is: Tailscale, the home network, the Internet; nothing for this PC itself.</summary>
    public static string? Place(string? address)
    {
        if (address is null || !IPAddress.TryParse(address, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return null;
        if (LocalNetwork.IsTailnet(ip.ToString())) return "Tailscale";
        return LocalNetwork.IsHome(ip) ? "réseau de la maison" : "Internet";
    }
    /// <summary>« Courte HLS · S1 É2 · Le départ — 12:34 / 40:00, en pause ».</summary>
    public static string What(Viewer viewer)
    {
        var title = viewer.Type == "Episode" && viewer.Series is { } series
            ? string.Join(" · ", new[] { series, Code(viewer.Season, viewer.Episode), viewer.Name }.Where(x => x is { Length: > 0 }))
            : viewer.Name;
        var time = viewer.Duration > 0 ? $"{Clock(viewer.Position)} / {Clock(viewer.Duration)}" : Clock(viewer.Position);
        return $"{title} — {time}{(viewer.Paused ? ", en pause" : "")}";
    }
    private static string? Code(int? season, int? episode) => episode is null ? null : season is null ? $"É{episode}" : $"S{season} É{episode}";
    /// <summary>« 1:02:03 », « 12:34 ».</summary>
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
    /// <summary>
    /// How it plays: « Lecture directe : le fichier tel quel », « Fichier remis dans un autre format, sans conversion »,
    /// or « Conversion : image en VP9, son en Opus, par le processeur · 1,6 Mb/s », and why.
    /// </summary>
    public static (string How, string? Why) How(Viewer viewer)
    {
        if (viewer.Method == "DirectPlay" || viewer.Method.Length == 0 && viewer.Conversion is null) return ("Lecture directe : le fichier tel quel", null);
        var c = viewer.Conversion;
        if (viewer.Method == "DirectStream" || c is { VideoDirect: true, AudioDirect: true })
            return ("Fichier remis dans un autre format, sans conversion", Why(c));
        if (c is null) return ("Conversion", null);
        var parts = new List<string>
        {
            c.VideoDirect ? "image copiée" : $"image en {PlayerText.Codec(c.VideoCodec) ?? "?"}",
            c.AudioDirect ? "son copié" : $"son en {PlayerText.Codec(c.AudioCodec) ?? "?"}",
        };
        var on = c.VideoDirect ? "" : c.Hardware is { Length: > 0 } kind && kind != GpuEncoding.None ? $", par la carte graphique ({GpuEncoding.Maker(kind)})" : ", par le processeur";
        var rate = c.Bitrate is > 0 ? $" · {(c.Bitrate.Value / 1e6).ToString("0.0", PlayerText.French)} Mb/s" : "";
        return ($"Conversion : {string.Join(", ", parts)}{on}{rate}", Why(c));
    }
    private static string? Why(Conversion? c) => c is { Reasons.Length: > 0 }
        ? "Pourquoi : " + string.Join(", ", c.Reasons.Select(Reason).Distinct())
        : null;
    /// <summary>Jellyfin's TranscodeReasons in words; the name itself for one Mira does not know.</summary>
    public static string Reason(string reason) => reason switch
    {
        "ContainerNotSupported" => "format du fichier",
        "VideoCodecNotSupported" => "codec vidéo",
        "AudioCodecNotSupported" => "codec audio",
        "SubtitleCodecNotSupported" => "sous-titres à incruster",
        "AudioIsExternal" => "piste audio à part",
        "SecondaryAudioNotSupported" => "piste audio secondaire",
        "VideoProfileNotSupported" => "profil vidéo",
        "VideoLevelNotSupported" => "niveau vidéo",
        "VideoResolutionNotSupported" => "définition",
        "VideoBitDepthNotSupported" => "vidéo 10 bits",
        "VideoFramerateNotSupported" => "images par seconde",
        "RefFramesNotSupported" => "images de référence",
        "AnamorphicVideoNotSupported" => "image anamorphosée",
        "InterlacedVideoNotSupported" => "image entrelacée",
        "AudioChannelsNotSupported" => "canaux audio",
        "AudioProfileNotSupported" => "profil audio",
        "AudioSampleRateNotSupported" => "fréquence audio",
        "AudioBitDepthNotSupported" => "audio haute résolution",
        "ContainerBitrateExceedsLimit" => "débit au-dessus de la qualité choisie",
        "VideoBitrateNotSupported" => "débit vidéo",
        "AudioBitrateNotSupported" => "débit audio",
        "UnknownVideoStreamInfo" => "vidéo mal décrite",
        "UnknownAudioStreamInfo" => "audio mal décrit",
        "DirectPlayError" => "lecture directe en échec",
        "VideoRangeTypeNotSupported" => "HDR",
        "VideoCodecTagNotSupported" => "étiquette du codec vidéo",
        "StreamCountExceedsLimit" => "trop de pistes",
        _ => reason,
    };
}

/// <summary>A session playing something on Jellyfin. Position and Duration in seconds; CanStop: it takes « Arrêter ».</summary>
public sealed record Viewer(string SessionId, bool Own, string User, string Device, string Client, string? Address,
    string Type, string Name, string? Series, int? Season, int? Episode, double Position, double Duration, bool Paused,
    string Method, Conversion? Conversion, bool CanStop);

/// <summary>A conversion under way (TranscodingInfo): what is copied, what is made again and how, and why.</summary>
public sealed record Conversion(bool VideoDirect, bool AudioDirect, string? VideoCodec, string? AudioCodec, long? Bitrate, string[] Reasons, string? Hardware);
