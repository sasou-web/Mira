using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mira.Jellyfin;

/// <summary>
/// Jellyfin's HLS playlists, rewritten for Apple's player so that the subtitles of a conversion come in time.
/// <para>
/// Jellyfin writes the WebVTT of its HLS subtitles for MPEG-TS segments, which start at 10 s: each file says so with
/// X-TIMESTAMP-MAP=MPEGTS:900000. Its fMP4 segments start at 0, and Apple's player reads that map as it is, so it
/// shows every line 10 s late (hls.js counts from the first frame, and is not affected). Without the map, cue times
/// count from the start of the video, which is where fMP4 segments start.
/// </para>
/// </summary>
public static partial class HlsPlaylists
{
    /// <summary>
    /// A master playlist served by Mira: its subtitle playlists are Mira's (<paramref name="miraPath"/>, ending in a
    /// slash), every other address is Jellyfin's own, next to <paramref name="videosPath"/> (/videos/{item}/). The
    /// subtitles take the names Mira gives them, by Jellyfin index (Apple's menu shows them; Jellyfin's are in English).
    /// </summary>
    public static string Master(string text, string videosPath, string miraPath, IReadOnlyDictionary<int, string>? names = null)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith('#'))
            {
                lines[i] = Absolute(line, videosPath);
                continue;
            }

            var subtitles = line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) && line.Contains("TYPE=SUBTITLES", StringComparison.Ordinal);
            lines[i] = UriAttribute().Replace(line, match =>
            {
                var uri = match.Groups["uri"].Value;
                var playlist = subtitles ? SubtitlePlaylist().Match(uri) : Match.Empty;
                return playlist.Success
                    ? $"URI=\"{miraPath}{playlist.Groups["source"].Value}/Subtitles/{playlist.Groups["index"].Value}/subtitles.m3u8{playlist.Groups["query"].Value}\""
                    : $"URI=\"{Absolute(uri, videosPath)}\"";
            });
            var named = subtitles ? SubtitlePlaylist().Match(UriAttribute().Match(line).Groups["uri"].Value) : Match.Empty;
            if (named.Success && names is not null
                && int.TryParse(named.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && names.TryGetValue(index, out var name))
            {
                lines[i] = NameAttribute().Replace(lines[i], $"NAME=\"{name}\"", 1);
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// A subtitle playlist served by Mira: its WebVTT segments are Jellyfin's, next to <paramref name="subtitlePath"/>
    /// (/videos/{item}/{source}/Subtitles/{index}/), asked for without the MPEG-TS time map.
    /// </summary>
    public static string Subtitles(string text, string subtitlePath)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            lines[i] = TimeMap().Replace(Absolute(line, subtitlePath), "${key}=false");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Mira's names for the subtitles, from its query (« 3:Français|5:Anglais »): what a quoted playlist attribute can
    /// hold, at most 120 characters each.
    /// </summary>
    public static Dictionary<int, string> Names(string? value)
    {
        var names = new Dictionary<int, string>();
        foreach (var pair in (value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Take(100))
        {
            var colon = pair.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !int.TryParse(pair[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            var name = new string(pair[(colon + 1)..].Where(c => c != '"' && !char.IsControl(c)).Take(120).ToArray()).Trim();
            if (name.Length > 0)
            {
                names[index] = name;
            }
        }

        return names;
    }

    private static string Absolute(string uri, string basePath) =>
        uri.StartsWith('/') || uri.Contains("://", StringComparison.Ordinal) ? uri : basePath + uri;

    [GeneratedRegex("URI=\"(?<uri>[^\"]*)\"")]
    private static partial Regex UriAttribute();

    [GeneratedRegex("NAME=\"[^\"]*\"")]
    private static partial Regex NameAttribute();

    [GeneratedRegex("(?:^|/)(?<source>[0-9a-fA-F]{32})/Subtitles/(?<index>[0-9]+)/subtitles\\.m3u8(?<query>\\?.*)?$")]
    private static partial Regex SubtitlePlaylist();

    [GeneratedRegex("(?<key>[?&]AddVttTimeMap)=true", RegexOptions.IgnoreCase)]
    private static partial Regex TimeMap();
}
