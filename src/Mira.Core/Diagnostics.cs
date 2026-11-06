using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// One playback opened by <see cref="ServerStreams.OpenAsync"/> (or refused on the way), as the diagnostic report shows
/// it: what kind of title, what the file is made of, how Jellyfin sends it, and from where. Never its name nor its path.
/// </summary>
public sealed record PlaybackTrace(DateTimeOffset At, string Type, string? Container, string? Video, string? Audio, string How, string? Network, string? Error = null)
{
    public string Line => $"{At:yyyy-MM-dd HH:mm} {Type}" + (Container is { } c ? $" · {c}" : "") + (Video is { } v ? $" · {v}" : "") + (Audio is { } a ? $" · {a}" : "")
        + $" · {How}" + (Network is { } n ? $" · {n}" : "") + (Error is { } e ? $" · failed: {e}" : "");

    public static PlaybackTrace Of(MediaItem item, MediaSource? source, string server, DateTimeOffset at, string how, string? error = null)
    {
        var video = source?.MediaStreams.FirstOrDefault(s => s.Type == "Video");
        var audio = source?.MediaStreams.FirstOrDefault(s => s.Type == "Audio");
        return new(at, item.Type, source?.Container,
            video is null ? null : string.Join(' ', new[] { video.Codec, video.Height is { } h ? $"{h}p" : null, video.VideoRangeType ?? video.VideoRange }.Where(x => !string.IsNullOrEmpty(x))),
            audio is null ? null : string.Join(' ', new[] { audio.Codec, audio.Channels is { } ch ? $"{ch}ch" : null }.Where(x => !string.IsNullOrEmpty(x))),
            how, DiagnosticReport.NetworkOf(server), error);
    }

    public static string HowOf(ServerStream stream) => stream.Converted
        ? "converted" + (stream.Reasons.Count > 0 ? $" ({string.Join(", ", stream.Reasons)})" : "") + $" at {stream.Bitrate / 1_000_000.0:0.#} Mb/s"
        : stream.Authorization is null ? "this computer's file" : "file as it is";
}

/// <summary>The last playbacks of this run of Mira, for the diagnostic report: kept in memory only, ten at most.</summary>
public static class RecentPlaybacks
{
    public const int Kept = 10;
    private static readonly LinkedList<PlaybackTrace> _traces = new();
    private static readonly object _gate = new();

    public static void Add(PlaybackTrace trace)
    {
        lock (_gate)
        {
            _traces.AddLast(trace);
            while (_traces.Count > Kept) _traces.RemoveFirst();
        }
    }

    public static IReadOnlyList<PlaybackTrace> All { get { lock (_gate) return [.. _traces]; } }

    public static void Clear() { lock (_gate) _traces.Clear(); }
}

/// <summary>
/// Réglages → « Exporter un rapport »: a text file the person saves where they choose and sends by hand (Discord, mail)
/// when something goes wrong; nothing is ever sent by Mira. Versions, system, the kind of network Jellyfin is reached by
/// (never its address), Mira's own facts, the last playbacks (<see cref="RecentPlaybacks"/>) and errors.log. In English:
/// it is read by whoever looks for the fault. Every line passes <see cref="Redact"/>: no token, key, address, e-mail,
/// account name nor user folder survives, even from a fact given by mistake.
/// </summary>
public static partial class DiagnosticReport
{
    /// <summary>The lines of errors.log kept at most (the newest), errors.log.old before it.</summary>
    public const int ErrorLines = 200;

    public static string FileName(DateTimeOffset now) => $"mira-report-{now:yyyyMMdd-HHmm}.txt";

    /// <summary>« this computer », « home network », « Tailscale » or « Internet »: how Jellyfin is reached, never where.</summary>
    public static string? NetworkOf(string? server)
    {
        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server, UriKind.Absolute, out var uri)) return null;
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (uri.IsLoopback || host == "localhost") return "this computer";
        if (host.EndsWith(".ts.net", StringComparison.Ordinal)) return "Tailscale";
        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            if (LocalNetwork.IsTailnet(address.ToString()) || address.GetAddressBytes() is [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0, ..]) return "Tailscale";
            return LocalNetwork.IsHome(address) ? "home network" : "Internet";
        }
        return StreamQuality.AtHome(server) ? "home network" : "Internet";
    }

    /// <summary>
    /// The report. <paramref name="app"/> names Mira and its platform (« Mira for Windows »); <paramref name="facts"/> are
    /// that app's own (settings, engine, built-in Tailscale), a null value written « - »; <paramref name="names"/> are words
    /// that name the person and never leave (the Jellyfin account, this computer's user and name), written « [name] ».
    /// </summary>
    public static string Build(string app, string? server, string? jellyfin, IEnumerable<(string Name, string? Value)> facts, IReadOnlyList<PlaybackTrace> playbacks, IReadOnlyList<string> errors, DateTimeOffset now, IEnumerable<string?> names)
    {
        var text = new StringBuilder();
        void Line(string name, string? value) => text.Append(name).Append(": ").Append(string.IsNullOrWhiteSpace(value) ? "-" : value.Trim()).Append('\n');
        text.Append($"{app} {JellyfinClient.AppVersion} · diagnostic report\n");
        text.Append("Written by Mira on this device and sent by nobody but you. No password, token, address, file name nor account name.\n\n");
        Line("Made", now.ToString("yyyy-MM-dd HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture));
        Line("System", RuntimeInformation.OSDescription);
        Line("Processor", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        Line(".NET", RuntimeInformation.FrameworkDescription);
        Line("Language", Lang.Code);
        text.Append('\n');
        Line("Jellyfin", jellyfin);
        Line("Reached by", NetworkOf(server));
        foreach (var (name, value) in facts) Line(name, value);
        text.Append("\nLast playbacks (this run of Mira)\n");
        if (playbacks.Count == 0) text.Append("  none\n");
        foreach (var trace in playbacks) text.Append("  ").Append(trace.Line).Append('\n');
        text.Append($"\nerrors.log (newest {ErrorLines} lines)\n");
        if (errors.Count == 0) text.Append("  empty\n");
        foreach (var error in errors) text.Append("  ").Append(error).Append('\n');
        var report = Redact(text.ToString());
        foreach (var name in names.Where(x => x is { Length: >= 2 }).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x!.Length))
            report = Regex.Replace(report, @"(?<![\p{L}\p{N}])" + Regex.Escape(name!) + @"(?![\p{L}\p{N}])", "[name]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return report;
    }

    /// <summary>The words <see cref="Build"/> hides on any computer: its user and its name.</summary>
    public static IEnumerable<string?> ComputerNames()
    {
        string? user = null, machine = null;
        try { user = Environment.UserName; machine = Environment.MachineName; } catch (PlatformNotSupportedException) { }
        return [user, machine];
    }

    /// <summary>The newest lines of errors.log, errors.log.old before it; none when there is no log.</summary>
    public static IReadOnlyList<string> ReadErrors(string directory)
    {
        var lines = new List<string>();
        foreach (var name in new[] { "errors.log.old", "errors.log" })
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path).Where(x => x.Length > 0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return lines.Count > ErrorLines ? lines[^ErrorLines..] : lines;
    }

    /// <summary>
    /// What may never leave in a report, replaced by its kind: web addresses, e-mails, IP addresses, keys and tokens
    /// (Jellyfin's, a long hexadecimal or base64 run), the user's folder (C:\Users\…, /Users/…, /home/…). Cases shared with
    /// the iPhone in tests/fixtures/diagnostics.json.
    /// </summary>
    public static string Redact(string text)
    {
        text = Addresses().Replace(text, "[address]");
        text = Emails().Replace(text, "[e-mail]");
        text = Secrets().Replace(text, m => m.Groups[1].Value + "[hidden]");
        text = UserFolders().Replace(text, m => m.Groups[1].Value + "…");
        text = IPv4().Replace(text, "[address]");
        text = IPv6().Replace(text, "[address]");
        text = LongRuns().Replace(text, "[hidden]");
        return text;
    }

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Addresses();
    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Emails();
    [GeneratedRegex(@"((?:api_?key|token|password|access_token|x-emby-token)\s*[=:]\s*""?)[^\s"",;&]+", RegexOptions.IgnoreCase)]
    private static partial Regex Secrets();
    [GeneratedRegex(@"((?:[A-Z]:)?[\\/](?:Users|home)[\\/])[^\\/\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolders();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b")]
    private static partial Regex IPv4();
    [GeneratedRegex(@"\b(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b|\b[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*::(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*)?", RegexOptions.IgnoreCase)]
    private static partial Regex IPv6();
    [GeneratedRegex(@"\b(?=[A-Za-z_\-+/]*\d)[A-Za-z0-9_\-+/]{24,}={0,2}")]
    private static partial Regex LongRuns();
}
