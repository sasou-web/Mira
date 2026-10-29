using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// The stream of a title from Jellyfin, for mpv (Windows and Mac): the file as it is, or Jellyfin's conversion under the
/// quality's bitrate (<see cref="StreamQuality"/>), as Mira web asks for them.
/// </summary>
/// <param name="Location">A file on this computer, or an address of the server: its token goes in <paramref name="Authorization"/>, never in the address.</param>
/// <param name="Converted">Jellyfin's conversion (HLS): it carries one audio track, its text subtitles come beside it, and its picture subtitles are drawn into it.</param>
/// <param name="Audio">The audio track converted (Jellyfin's index); null for the file as it is, which has them all.</param>
/// <param name="Burned">The picture subtitles drawn into a conversion (Jellyfin's index); null when none are.</param>
/// <param name="Bitrate">The bitrate asked for, in bits per second.</param>
/// <param name="Reasons">Why Jellyfin converts (its TranscodeReasons).</param>
public sealed record ServerStream(MediaSource Source, string PlaySessionId, string Location, string? Authorization, bool Converted, int? Audio, int? Burned, long Bitrate, IReadOnlyList<string> Reasons)
{
    /// <summary>What Jellyfin's dashboard is told (« En ce moment sur Jellyfin »).</summary>
    public string PlayMethod => Converted ? "Transcode" : "DirectPlay";
}

/// <summary>
/// What a title is opened with: where (seconds), and for a conversion the audio track and the picture subtitles to draw
/// into it, by Jellyfin's index (-1 for <paramref name="Burn"/>: none). Null: chosen by the series' <paramref name="Memory"/>,
/// else by the languages of the settings. Text subtitles are mpv's to choose: they come beside the video.
/// </summary>
public sealed record StreamWish(double Start, int? Audio = null, int? Burn = null, TrackMemory? Memory = null);

/// <summary>The device profile of mpv, the request to PlaybackInfo, and what its answer gives the player.</summary>
public static partial class ServerStreams
{
    private static readonly string[] TextFormats = ["srt", "subrip", "ass", "ssa", "vtt", "webvtt", "mov_text", "ttml", "smi", "sami"];
    private static readonly string[] PictureFormats = ["pgssub", "pgs", "dvdsub", "dvbsub", "vobsub", "xsub"];

    /// <summary>
    /// What mpv plays, told to Jellyfin: every file as it is, up to <paramref name="bitrate"/>; above, a conversion into
    /// HLS (MPEG-TS segments) in H.264, its audio copied when mpv's segments take it, 5.1 kept. Subtitles stay in the
    /// file played as it is; beside a conversion when they are text, drawn into it when they are pictures (PGS, DVD).
    /// </summary>
    public static JsonObject Profile(long bitrate) => new()
    {
        ["Name"] = "Mira (mpv)",
        ["MaxStreamingBitrate"] = bitrate,
        ["MaxStaticBitrate"] = bitrate,
        ["MusicStreamingTranscodingBitrate"] = 384000,
        ["DirectPlayProfiles"] = new JsonArray(new JsonObject { ["Type"] = "Video" }, new JsonObject { ["Type"] = "Audio" }),
        ["TranscodingProfiles"] = new JsonArray(new JsonObject
        {
            ["Type"] = "Video", ["Container"] = "ts", ["Protocol"] = "hls", ["Context"] = "Streaming",
            ["VideoCodec"] = "h264", ["AudioCodec"] = "aac,mp3,ac3,eac3", ["MaxAudioChannels"] = "6",
        }),
        ["ContainerProfiles"] = new JsonArray(),
        ["CodecProfiles"] = new JsonArray(),
        ["SubtitleProfiles"] = new JsonArray([
            .. TextFormats.Concat(PictureFormats).Select(format => (JsonNode)new JsonObject { ["Format"] = format, ["Method"] = "Embed" }),
            .. TextFormats.Select(format => (JsonNode)new JsonObject { ["Format"] = format, ["Method"] = "External" }),
            .. PictureFormats.Select(format => (JsonNode)new JsonObject { ["Format"] = format, ["Method"] = "Encode" }),
        ]),
    };

    /// <summary>
    /// The body of POST Items/{id}/PlaybackInfo: the profile at <paramref name="bitrate"/>, where to start, the audio
    /// track to convert (null: Jellyfin's default) and the picture subtitles to draw into it (none otherwise: a default
    /// PGS track would be burned in, beyond mpv's reach).
    /// </summary>
    public static JsonObject Request(string userId, long bitrate, double start, int? audio, int? burned, string? sourceId)
    {
        var body = new JsonObject
        {
            ["UserId"] = userId, ["DeviceProfile"] = Profile(bitrate), ["MaxStreamingBitrate"] = bitrate,
            ["StartTimeTicks"] = TimeSpan.FromSeconds(Math.Max(0, start)).Ticks, ["SubtitleStreamIndex"] = burned ?? -1,
            ["EnableDirectPlay"] = true, ["EnableDirectStream"] = true, ["EnableTranscoding"] = true,
            ["AllowVideoStreamCopy"] = true, ["AllowAudioStreamCopy"] = true, ["AutoOpenLiveStream"] = true,
        };
        if (audio is { } index) body["AudioStreamIndex"] = index;
        if (sourceId is { Length: > 0 }) body["MediaSourceId"] = sourceId;
        return body;
    }

    /// <summary>
    /// Whether Jellyfin converts this source: it can no longer be sent as it is (above the bitrate) and offers its
    /// conversion. Neither (an account Jellyfin does not let convert): the file as it is, as before.
    /// </summary>
    public static bool Converts(MediaSource source) => !source.SupportsDirectPlay && !string.IsNullOrEmpty(source.TranscodingUrl);

    /// <summary>An address of Jellyfin without its token (ApiKey, api_key): the token goes in the Authorization header.</summary>
    public static string WithoutKey(string address)
    {
        var at = address.IndexOf('?');
        if (at < 0) return address;
        var kept = address[(at + 1)..].Split('&').Where(part => part.Length > 0 && !KeyParameter().IsMatch(part));
        var query = string.Join('&', kept);
        return query.Length == 0 ? address[..at] : $"{address[..at]}?{query}";
    }
    [GeneratedRegex("^(api_?key)=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex KeyParameter();

    /// <summary>A parameter of Jellyfin's conversion address (AudioStreamIndex, TranscodeReasons…).</summary>
    public static string? Parameter(string? address, string name)
    {
        if (address?.IndexOf('?') is not (>= 0 and var at)) return null;
        foreach (var part in address[(at + 1)..].Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && part[..equals].Equals(name, StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(part[(equals + 1)..]);
        }
        return null;
    }
    private static int? Index(string? address, string name) => int.TryParse(Parameter(address, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : null;
    /// <summary>Why Jellyfin converts, in its words (« ContainerBitrateExceedsLimit »); <see cref="Viewers.Reason"/> says them.</summary>
    public static IReadOnlyList<string> Reasons(string? address) => Parameter(address, "TranscodeReasons") is { Length: > 0 } reasons ? reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

    /// <summary>Subtitles in pictures (PGS, DVD): a conversion can only draw them into its video.</summary>
    public static bool Picture(MediaStream stream) => stream.Type == "Subtitle" && !stream.IsTextSubtitleStream
        && (stream.Codec is null || PictureFormats.Contains(stream.Codec.ToLowerInvariant()) || !TextFormats.Contains(stream.Codec.ToLowerInvariant()));

    /// <summary>
    /// The subtitles mpv fetches beside the video: the external files of a file played as it is, every text track of a
    /// conversion. Their addresses on this server only (the token goes with them), without the token. A file played as
    /// it is keeps its own times: Jellyfin cuts a subtitle at the start asked (« /Subtitles/0/300000000/ ») when it
    /// would have converted for an account without the right to, so the file's start goes back to 0.
    /// </summary>
    public static IEnumerable<(MediaStream Stream, string Path)> Beside(MediaSource source, bool converted) =>
        source.MediaStreams
            .Where(s => s.Type == "Subtitle" && s.DeliveryUrl is { Length: > 0 } && (converted ? s.DeliveryMethod == "External" : s.IsExternal))
            .Where(s => s.DeliveryUrl!.StartsWith('/') || !s.DeliveryUrl.Contains("://", StringComparison.Ordinal))
            .Select(s => (s, converted ? WithoutKey(s.DeliveryUrl!) : SubtitleStart().Replace(WithoutKey(s.DeliveryUrl!), "${1}0/", 1)));
    [GeneratedRegex(@"(/Subtitles/\d+/)\d+/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex SubtitleStart();

    /// <summary>
    /// The source's audio and subtitle tracks in the shape of mpv's track-list (« id » being Jellyfin's index), for the
    /// series' memory (<see cref="SeriesTracks"/>) and the menus (<see cref="MpvLists.Tracks"/>) of a conversion.
    /// </summary>
    public static string TrackList(MediaSource source) => new JsonArray([.. source.MediaStreams
        .Where(s => s.Type is "Audio" or "Subtitle")
        .Select(s =>
        {
            var track = new JsonObject { ["id"] = s.Index.ToString(CultureInfo.InvariantCulture), ["type"] = s.Type == "Audio" ? "audio" : "sub", ["forced"] = s.IsForced, ["external"] = s.IsExternal };
            if (s.Language is { Length: > 0 } language) track["lang"] = language;
            if (s.Title is { Length: > 0 } title) track["title"] = title;
            if (s.Codec is { Length: > 0 } codec) track["codec"] = codec.ToLowerInvariant();
            if (s.Channels is { } channels) track["demux-channel-count"] = channels;
            return (JsonNode)track;
        })]).ToJsonString();

    /// <summary>
    /// The audio a conversion carries: the one the series last played (<see cref="SeriesTracks"/>), else the first in the
    /// order of the settings' languages (as mpv's alang chooses in a file), else Jellyfin's default.
    /// </summary>
    public static int? AudioFor(MediaSource source, TrackMemory? memory, string languages)
    {
        if (memory is not null && SeriesTracks.Choose(TrackList(source), memory).Audio is { } remembered && int.TryParse(remembered, CultureInfo.InvariantCulture, out var index)) return index;
        var audio = source.MediaStreams.Where(s => s.Type == "Audio").ToList();
        foreach (var language in Languages(languages))
            if (audio.FirstOrDefault(s => SeriesTracks.LanguageKey(s.Language) == language) is { } match) return match.Index;
        return source.DefaultAudioStreamIndex ?? audio.FirstOrDefault()?.Index;
    }
    /// <summary>The picture subtitles the series last played, drawn into a conversion; null for text ones, or none.</summary>
    public static int? BurnFor(MediaSource source, TrackMemory? memory)
    {
        if (memory is not { Subtitles: true } || SeriesTracks.Choose(TrackList(source), memory).Subtitle is not { } chosen) return null;
        return source.MediaStreams.FirstOrDefault(s => s.Index.ToString(CultureInfo.InvariantCulture) == chosen) is { } stream && Picture(stream) ? stream.Index : null;
    }
    /// <summary>
    /// The text subtitles a conversion starts with, without a memory of the series: by the settings' languages, as mpv
    /// chooses in a file. In the language of the audio, only forced ones (signs, foreign lines); null: none.
    /// </summary>
    public static int? SubtitleFor(MediaSource source, int? audio, string languages)
    {
        var spoken = SeriesTracks.LanguageKey(source.MediaStreams.FirstOrDefault(s => s.Type == "Audio" && s.Index == audio)?.Language);
        var text = source.MediaStreams.Where(s => s.Type == "Subtitle" && !Picture(s)).ToList();
        foreach (var language in Languages(languages))
        {
            var same = text.Where(s => SeriesTracks.LanguageKey(s.Language) == language).ToList();
            if (same.Count == 0) continue;
            if (language == spoken) return same.FirstOrDefault(s => s.IsForced)?.Index;
            return (same.FirstOrDefault(s => !s.IsForced) ?? same[0]).Index;
        }
        return null;
    }
    private static IEnumerable<string> Languages(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(SeriesTracks.LanguageKey).OfType<string>().Distinct();

    /// <summary>
    /// Opens <paramref name="item"/> from Jellyfin: the bitrate of the quality chosen (the connection measured on
    /// « Automatique » away from home), PlaybackInfo with mpv's profile, then the file as it is (this computer's own file
    /// when Jellyfin runs here and <paramref name="fileHere"/> finds it) or Jellyfin's conversion, with its audio and picture
    /// subtitles. Asked again (three times at most) when the answer changes the bitrate or the tracks.
    /// </summary>
    public static async Task<ServerStream> OpenAsync(JellyfinClient client, ConnectionSpeed speed, string quality, MediaItem item, StreamWish wish, string audioLanguages, Func<string, Task<bool>> fileHere, CancellationToken ct = default)
    {
        var atHome = StreamQuality.AtHome(client.Connection.Server);
        Task<double> Measure() => client.BitrateTestAsync(ct: ct);
        var fileBitrate = item.MediaSources?.FirstOrDefault()?.Bitrate ?? 0;
        var asked = (Bitrate: await speed.BitrateAsync(quality, atHome, fileBitrate, Measure), Audio: wish.Audio, Burned: wish.Burn is >= 0 ? wish.Burn : null);
        string? sourceId = null;
        for (var round = 0; ; round++)
        {
            var info = await client.PlaybackInfoAsync(item.Id, Request(client.Connection.UserId, asked.Bitrate, wish.Start, asked.Audio, asked.Burned, sourceId), ct);
            var source = info.MediaSources.FirstOrDefault() ?? throw new IOException(L("Aucune source lisible disponible.", "No playable source available."));
            sourceId = source.Id;
            var converted = Converts(source);
            var given = converted ? Index(source.TranscodingUrl, "AudioStreamIndex") ?? asked.Audio : asked.Audio;
            // What this answer asks for: « Automatique » with the file's own bitrate, now known; a conversion's tracks.
            var wanted = asked;
            if (source.Bitrate is > 0 and var known && known != fileBitrate) { fileBitrate = known; wanted.Bitrate = await speed.BitrateAsync(quality, atHome, known, Measure); }
            if (converted) wanted = (wanted.Bitrate, wish.Audio ?? AudioFor(source, wish.Memory, audioLanguages) ?? given, wish.Burn is { } burn ? (burn >= 0 ? burn : null) : BurnFor(source, wish.Memory));
            if (round < 2 && (wanted.Bitrate != asked.Bitrate || converted && (wanted.Audio != given || wanted.Burned != asked.Burned))) { asked = wanted; continue; }
            var session = string.IsNullOrEmpty(info.PlaySessionId) ? Guid.NewGuid().ToString("N") : info.PlaySessionId;
            if (converted)
                return new(source, session, client.ServerUri(WithoutKey(source.TranscodingUrl!)).AbsoluteUri, client.AuthorizationHeader, true, given, asked.Burned, asked.Bitrate, Reasons(source.TranscodingUrl));
            // Jellyfin on this same computer: its file, read directly.
            if (new Uri(client.Connection.Server).IsLoopback && string.Equals(source.Protocol, "File", StringComparison.OrdinalIgnoreCase) && source.Path is { } path && await fileHere(path))
                return new(source, session, path, null, false, null, null, asked.Bitrate, []);
            return new(source, session, client.StreamUri(item.Id, source.Id).AbsoluteUri, client.AuthorizationHeader, false, null, null, asked.Bitrate, []);
        }
    }
}

/// <summary>
/// The audio and subtitles of Jellyfin's conversion for the player's menu, by Jellyfin's index: it carries one audio
/// track (another is another conversion), its text subtitles come beside it (fetched by mpv the first time they show),
/// and its picture subtitles are drawn into it (another choice is another conversion too). The Windows and Mac players
/// keep one per conversion.
/// </summary>
public sealed class ConvertedTracks(ServerStream stream)
{
    /// <summary>Text tracks fetched by mpv: Jellyfin's index → mpv's id.</summary>
    private readonly Dictionary<int, string> _added = [];
    public ServerStream Stream => stream;
    private List<MpvTrack> All => MpvLists.Tracks(ServerStreams.TrackList(stream.Source));
    /// <summary>The source's audio tracks, named for the menu, « Id » being Jellyfin's index.</summary>
    public List<MpvTrack> Audio => [.. All.Where(t => t.Type == "audio")];
    /// <summary>The source's subtitles, text and pictures, named for the menu, « Id » being Jellyfin's index.</summary>
    public List<MpvTrack> Subtitles => [.. All.Where(t => t.Type == "sub")];
    public static int Index(MpvTrack track) => int.Parse(track.Id, CultureInfo.InvariantCulture);
    /// <summary>Subtitles in pictures: shown only drawn into another conversion.</summary>
    public bool IsPicture(int index) => stream.Source.MediaStreams.FirstOrDefault(s => s.Type == "Subtitle" && s.Index == index) is { } track && ServerStreams.Picture(track);
    /// <summary>Where mpv fetches a text track on the server (to resolve against it, the token going in the header); null for pictures.</summary>
    public string? Path(int index) => ServerStreams.Beside(stream.Source, true).Where(x => x.Stream.Index == index).Select(x => x.Path).FirstOrDefault();
    /// <summary>mpv's id of a text track it fetched; null until then.</summary>
    public string? MpvId(int index) => _added.TryGetValue(index, out var id) ? id : null;
    public void Added(int index, string mpvId) => _added[index] = mpvId;
    /// <summary>
    /// The subtitles shown, by Jellyfin's index: the pictures drawn in, else the text track mpv shows (<paramref name="sid"/>);
    /// null for none, or for a file of the person's (« Charger un fichier… »).
    /// </summary>
    public int? Shown(string? sid) => stream.Burned ?? (_added.Where(x => x.Value == sid).Select(x => (int?)x.Key).FirstOrDefault());
    /// <summary>A track of mpv's that is one of the source's, fetched beside the conversion.</summary>
    public bool Fetched(string mpvId) => _added.ContainsValue(mpvId);
    /// <summary>mpv shows subtitles that are not the source's: a file of the person's.</summary>
    public bool ShowsOther(string? sid) => stream.Burned is null && Off(sid) is false && !_added.ContainsValue(sid!);
    public static bool Off(string? sid) => sid is null or "" or "no" or "false";
    /// <summary>What the series keeps for its next episodes (<see cref="SeriesTracks"/>); null while a file of the person's shows.</summary>
    public TrackMemory? Remember(string? sid) => ShowsOther(sid) ? null
        : SeriesTracks.Remember(ServerStreams.TrackList(stream.Source), stream.Audio?.ToString(CultureInfo.InvariantCulture), Shown(sid)?.ToString(CultureInfo.InvariantCulture));
    /// <summary>
    /// The text subtitles a conversion starts with: the series' when they are text (none when it turned them off or chose
    /// pictures, drawn in already or not), else by the settings' languages (<see cref="ServerStreams.SubtitleFor"/>). None
    /// over pictures drawn in.
    /// </summary>
    public int? StartText(TrackMemory? memory, string languages)
    {
        if (stream.Burned is not null) return null;
        if (memory is not null)
        {
            if (!memory.Subtitles) return null;
            if (int.TryParse(SeriesTracks.Choose(ServerStreams.TrackList(stream.Source), memory).Subtitle, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chosen))
                return IsPicture(chosen) ? null : chosen;
        }
        return ServerStreams.SubtitleFor(stream.Source, stream.Audio, languages);
    }
}

/// <summary>What to do with a stream from Jellyfin that stopped before its end (<see cref="StreamRecovery"/>).</summary>
public enum RecoveryStep
{
    /// <summary>Jellyfin does not answer: wait for it, then reopen where it was.</summary>
    Wait,
    /// <summary>Jellyfin answers: reopen where it was, once.</summary>
    Reopen,
    /// <summary>Reopened already, and it stopped again without playing on: « Lecture impossible », with « Réessayer ».</summary>
    Fail,
}

/// <summary>
/// A stream from Jellyfin that stops before the end of its title: a broken connection (mpv's HTTP gives up and calls it
/// the end of the file, or fails), never the end of the title, which is neither counted as watched nor followed by the
/// next episode. As Mira web: Jellyfin silent, waited for; answering, reopened where it was; stopped again without having
/// played <see cref="Forgiven"/> seconds since, it cannot play.
/// </summary>
public sealed class StreamRecovery
{
    /// <summary>Seconds played past a reopening that make it a success: the next cut is reopened again.</summary>
    public const double Forgiven = 30;
    /// <summary>Under the spinner while Jellyfin is waited for (as Mira web).</summary>
    public static string LostText => L("Connexion au serveur perdue. La lecture reprend dès qu’il répond.", "Lost the connection to the server. Playback resumes as soon as it answers.");
    /// <summary>« Lecture impossible » when the stream opened again stopped again at once (as Mira web).</summary>
    public static string StuckText => L("Jellyfin répond, mais la vidéo n’arrive plus jusqu’ici. Réessaie, ou choisis une qualité plus basse si la connexion est lente.",
        "Jellyfin is answering, but the video stopped coming through. Try again, or pick a lower quality if the connection is slow.");
    /// <summary>Played, then waiting for data this long while it should play: the stream is stuck (Mira web's STALL_LIMIT).</summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(15);
    /// <summary>Opened or moved, nothing played since, this long: stuck too (a conversion starts slowly; START_LIMIT).</summary>
    public static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(45);
    /// <summary>
    /// mpv waiting for data for <paramref name="waiting"/>, <paramref name="moved"/> when it played since it was opened or
    /// moved: the connection may be gone without mpv ever ending the file (a PC that goes off), so Mira decides.
    /// </summary>
    public static bool Stuck(TimeSpan waiting, bool moved) => waiting >= (moved ? StallLimit : StartLimit);
    private double? _reopenedAt;
    /// <summary>An end at <paramref name="position"/> before <see cref="LibraryStore.WatchedThreshold"/> of the title: a cut.</summary>
    public static bool CutShort(double position, double duration) => duration > 0 && position < duration * LibraryStore.WatchedThreshold;
    /// <summary>Where it plays now: far enough past the last reopening, it worked.</summary>
    public void Played(double position) { if (_reopenedAt is { } at && position > at + Forgiven) _reopenedAt = null; }
    public RecoveryStep Step(bool serverUp) => !serverUp ? RecoveryStep.Wait : _reopenedAt is null ? RecoveryStep.Reopen : RecoveryStep.Fail;
    /// <summary>Reopened at <paramref name="position"/> while Jellyfin answered.</summary>
    public void Reopening(double position) => _reopenedAt = position;
    /// <summary>A new title, or another try asked for.</summary>
    public void Reset() => _reopenedAt = null;
}
