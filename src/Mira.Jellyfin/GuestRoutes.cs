using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter par lien »: what a guest's page may ask of Jellyfin through <see cref="GuestController"/>, and how its
/// answers are made to come back there. A closed list: the title the group plays, its pictures, streams and
/// subtitles, the playback reports, the clock, and SyncPlay without the queue (the host chooses the titles). Nothing
/// lists the library, searches it, or reads another account; any other path is a 404. Free of ASP.NET, for the tests.
/// </summary>
public static partial class GuestRoutes
{
    /// <summary>The gateway's path after the server's base URL, the guest's page using it as Jellyfin's address.</summary>
    public const string Prefix = "/Mira/guest";

    public enum Access
    {
        /// <summary>Not for a guest: 404.</summary>
        None,
        /// <summary>Public on Jellyfin too (its Id, Ping): passed on without a session.</summary>
        Public,
        /// <summary>The guest's session, passed on as it is.</summary>
        Guest,
        /// <summary>SyncPlay's groups: only those of the host who invited.</summary>
        Groups,
        /// <summary>One group: only one of the host's.</summary>
        Group,
        /// <summary>Joining a group: only one of the host's.</summary>
        Join,
    }

    private const string Id = "[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

    // Method, path below the gateway (case ignored, as Jellyfin's routes), what it is.
    private static readonly (string Method, Regex Path, Access Access)[] Routes =
    [
        ("GET", Route("system/info/public"), Access.Public),
        ("GET", Route("system/ping"), Access.Public),
        ("POST", Route("system/ping"), Access.Public),
        ("GET", Route("users/me"), Access.Guest),
        ("GET", Route($"items/(?:{Id})"), Access.Guest),
        ("GET", Route($"items/(?:{Id})/images/[a-z]+(?:/\\d{{1,3}})?"), Access.Guest),
        ("POST", Route($"items/(?:{Id})/playbackinfo"), Access.Guest),
        ("GET", Route($"videos/(?:{Id})/stream(?:\\.[a-z0-9]{{1,6}})?"), Access.Guest),
        ("GET", Route($"videos/(?:{Id})/(?:master|main|live)\\.m3u8"), Access.Guest),
        ("GET", Route($"videos/(?:{Id})/hls1/[a-z0-9_-]{{1,40}}/-?\\d{{1,6}}\\.(?:ts|mp4|m4s|aac|mp3)"), Access.Guest),
        ("GET", Route($"videos/(?:{Id})/(?:{Id})/subtitles/\\d{{1,3}}/(?:\\d{{1,20}}/)?(?:stream\\.[a-z0-9]{{1,6}}|subtitles\\.m3u8)"), Access.Guest),
        ("GET", Route($"videos/(?:{Id})/trickplay/\\d{{1,5}}/(?:\\d{{1,6}}\\.jpg|tiles\\.m3u8)"), Access.Guest),
        ("DELETE", Route("videos/activeencodings"), Access.Guest),
        ("GET", Route($"mira/hls/(?:{Id})/master\\.m3u8"), Access.Guest),
        ("GET", Route($"mira/hls/(?:{Id})/(?:{Id})/subtitles/\\d{{1,3}}/subtitles\\.m3u8"), Access.Guest),
        ("GET", Route($"shows/(?:{Id})/episodes"), Access.Guest),
        ("GET", Route("shows/nextup"), Access.Guest),
        ("GET", Route($"mediasegments/(?:{Id})"), Access.Guest),
        ("POST", Route("sessions/playing(?:/progress|/stopped|/ping)?"), Access.Guest),
        ("POST", Route("sessions/capabilities(?:/full)?"), Access.Guest),
        ("POST", Route("sessions/logout"), Access.Guest),
        ("GET", Route("getutctime"), Access.Guest),
        ("GET", Route("playback/bitratetest"), Access.Guest),
        ("GET", Route("syncplay/list"), Access.Groups),
        ("GET", Route($"syncplay/(?:{Id})"), Access.Group),
        ("POST", Route("syncplay/join"), Access.Join),
        ("POST", Route("syncplay/(?:leave|pause|unpause|seek|buffering|ready|ping|setignorewait)"), Access.Guest),
    ];

    private static Regex Route(string path) => new("^" + path + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The largest connection test a guest may ask for (Mira web asks for 4 MB).</summary>
    public const long MaxBitrateTest = 10_000_000;

    /// <summary>
    /// What this request is, by its method and its path below the gateway (« Items/… », no slash first), and the one
    /// value of its query that matters (<paramref name="query"/>: a name to its value, null when absent). HEAD is GET.
    /// </summary>
    public static Access For(string method, string path, Func<string, string?> query)
    {
        var verb = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ? "GET" : method.ToUpperInvariant();
        // Plain path segments only: no « .. », no encoded characters, nothing a route could be smuggled through.
        if (path.Length > 300 || !PathRule().IsMatch(path))
        {
            return Access.None;
        }

        foreach (var (routeMethod, pattern, access) in Routes)
        {
            if (routeMethod != verb || !pattern.IsMatch(path))
            {
                continue;
            }

            // Jellyfin's « next up » across the whole library without a series; a connection test of any size.
            if (path.Equals("shows/nextup", StringComparison.OrdinalIgnoreCase) && !IsId(query("seriesId")))
            {
                return Access.None;
            }

            if (path.Equals("playback/bitratetest", StringComparison.OrdinalIgnoreCase)
                && !(long.TryParse(query("size"), out var size) && size > 0 && size <= MaxBitrateTest))
            {
                return Access.None;
            }

            return access;
        }

        return Access.None;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(?:\.[A-Za-z0-9]+)?(?:/[A-Za-z0-9_-]+(?:\.[A-Za-z0-9]+)?)*$")]
    private static partial Regex PathRule();

    public static bool IsId(string? value) => value is not null && Regex.IsMatch(value, "^(?:" + Id + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// An HLS playlist as the guest's page must read it: every address from this server's root
    /// (<paramref name="root"/>, its base URL, « » or « /jellyfin ») made to go through the gateway, those Mira's own
    /// playlists write (<see cref="HlsPlaylists"/>) and any Jellyfin would; relative addresses already do.
    /// </summary>
    public static string Playlist(string text, string root)
    {
        var from = root.TrimEnd('/') + "/";
        var to = root.TrimEnd('/') + Prefix + "/";
        string Through(string address) =>
            address.StartsWith(from, StringComparison.Ordinal) && !address.StartsWith(to, StringComparison.Ordinal) && !address.StartsWith("//", StringComparison.Ordinal)
                ? to + address[from.Length..]
                : address;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            lines[i] = line.StartsWith('#')
                ? UriAttribute().Replace(line, match => match.Groups[1].Value + Through(match.Groups[2].Value) + "\"")
                : Through(line);
        }

        return string.Join('\n', lines);
    }

    [GeneratedRegex("(URI=\")([^\"]*)\"")]
    private static partial Regex UriAttribute();

    /// <summary>
    /// Whether a SyncPlay group (Jellyfin's GroupInfoDto) is the host's: named after them (« Groupe de ogsas »,
    /// « ogsas’s group ») or with them in it.
    /// </summary>
    public static bool HostsGroup(JsonNode? group, string host)
    {
        if (group is not JsonObject value || string.IsNullOrEmpty(host))
        {
            return false;
        }

        var name = value["GroupName"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? value["GroupName"]!.GetValue<string>() : "";
        if (GroupHost(name) == host)
        {
            return true;
        }

        return value["Participants"] is JsonArray people && people.Any(x => x?.GetValueKind() == System.Text.Json.JsonValueKind.String && x.GetValue<string>() == host);
    }

    /// <summary>Who made a group, by its name as Mira names it in either language (sync.js groupHost); null otherwise.</summary>
    public static string? GroupHost(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var french = Regex.Match(name, "^Groupe de (.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (french.Success)
        {
            return french.Groups[1].Value;
        }

        var english = Regex.Match(name, "^(.+)['’]s group$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return english.Success ? english.Groups[1].Value : null;
    }

    /// <summary>
    /// A guest's policy, from the one Jellyfin gave the new account: hidden from the sign-in screen, plays and converts
    /// (an iPhone plays converted HLS), joins the host's groups but makes none, one device, and nothing else: no
    /// download, no administration, no deletion, no other account's devices, no live TV, no sharing. Every library is
    /// open to it, so that SyncPlay lets it play whatever the host puts on: the gateway is what keeps it to that title.
    /// </summary>
    public static JsonObject Policy(JsonObject policy)
    {
        var guest = (JsonObject)policy.DeepClone();
        guest["IsAdministrator"] = false;
        guest["IsHidden"] = true;
        guest["IsDisabled"] = false;
        guest["EnableAllFolders"] = true;
        guest["EnableMediaPlayback"] = true;
        guest["EnableAudioPlaybackTranscoding"] = true;
        guest["EnableVideoPlaybackTranscoding"] = true;
        guest["EnablePlaybackRemuxing"] = true;
        guest["EnableRemoteAccess"] = true;
        // Any device: the link itself is kept to one (GuestInvites.Opened), which Jellyfin cannot know beforehand.
        guest["EnableAllDevices"] = true;
        guest["SyncPlayAccess"] = "JoinGroups";
        guest["MaxActiveSessions"] = 1;
        foreach (var off in new[]
        {
            "EnableContentDownloading", "EnableSyncTranscoding", "EnableMediaConversion", "EnableContentDeletion",
            "EnableRemoteControlOfOtherUsers", "EnableSharedDeviceControl", "EnableLiveTvAccess", "EnableLiveTvManagement",
            "EnablePublicSharing", "EnableUserPreferenceAccess", "EnableCollectionManagement", "EnableSubtitleManagement",
            "EnableLyricManagement", "EnableAllChannels",
        })
        {
            guest[off] = false;
        }

        guest["EnableContentDeletionFromFolders"] = new JsonArray();
        return guest;
    }

    /// <summary>A secret of 128 bits, written for an address (base64url): an invitation's code.</summary>
    public static string NewCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether a text could be an invitation's code (<see cref="NewCode"/>).</summary>
    public static bool IsCode(string? code) => code is { Length: 22 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The guest account's password, never shown: 192 bits.</summary>
    public static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    /// <summary>How a session's token is kept: its SHA-256, never the token.</summary>
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    // Jellyfin's own rule for an account's name (Mira.Core Friends), checked on 12.1.
    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
    private static partial Regex NameRule();

    /// <summary>
    /// The guest's name in the group (« Léa »), free among <paramref name="taken"/> (« Léa 2 »); <paramref name="fallback"/>
    /// (« Invité ») when none is given. Null when Jellyfin would refuse it.
    /// </summary>
    public static string? GuestName(string? wanted, string fallback, IEnumerable<string> taken)
    {
        var name = (wanted ?? "").Trim().Replace('’', '\'');
        if (name.Length == 0)
        {
            name = fallback;
        }

        if (name.Length > 40 || !NameRule().IsMatch(name))
        {
            return null;
        }

        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name))
        {
            return name;
        }

        for (var n = 2; n < 1000; n++)
        {
            if (!used.Contains($"{name} {n}"))
            {
                return $"{name} {n}";
            }
        }

        return null;
    }
}
