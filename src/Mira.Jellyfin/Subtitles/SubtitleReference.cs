using System.Diagnostics;
using System.IO;
using System.Text;
using MediaBrowser.Model.Entities;
using Mira.Core.Subtitles;

namespace Mira.Jellyfin;

/// <summary>
/// When people speak in a video, for aligning subtitles on it (<see cref="SubtitleAlign"/>): from a subtitle track of
/// the file itself when there is one (any language, text only: the most exact; never a file beside it, which may be
/// the badly timed one), else from its audio, decoded
/// by Jellyfin's own ffmpeg (<see cref="SpeechDetector"/>). Read from the file on the server, never sent anywhere; ffmpeg
/// runs at a low priority and stops at once when its token is cancelled (someone starts watching).
/// </summary>
public sealed class SubtitleReference(string ffmpeg)
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text" };
    /// <summary>A reference is at most this long to read (a film's audio: a minute or two).</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(15);

    /// <summary>What the reference was made from: « track » or « audio ».</summary>
    public string? Kind { get; private set; }

    /// <summary>A text subtitle track of the file that says when people speak: a full one (not forced), the first.</summary>
    public static MediaStream? Track(IEnumerable<MediaStream> streams) =>
        streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && !s.IsForced && TextCodecs.Contains(s.Codec ?? ""));

    /// <summary>The speech of the video, one value per frame, or null when neither a track nor the audio could be read.</summary>
    public async Task<float[]?> ReadAsync(string videoPath, IReadOnlyList<MediaStream> streams, TimeSpan duration, CancellationToken ct)
    {
        var frames = SubtitleAlign.Frames(duration);
        if (Track(streams) is { } track)
        {
            var text = await TextAsync(["-i", videoPath, "-map", $"0:{track.Index}", "-f", "srt", "pipe:1"], ct).ConfigureAwait(false);
            var cues = text is null ? [] : SubtitleText.Parse(text);
            if (cues.Count >= 20) { Kind = "track"; return SubtitleAlign.Lines(cues, frames); }
        }
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.IsDefault).FirstOrDefault();
        if (audio is null) return null;
        var detector = new SpeechDetector();
        var ok = await RunAsync(["-i", videoPath, "-map", $"0:{audio.Index}", "-vn", "-ac", "1", "-ar", SpeechDetector.Rate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-af", "highpass=f=200,lowpass=f=3000", "-f", "s16le", "pipe:1"], async output =>
        {
            var buffer = new byte[1 << 16];
            var carry = 0;
            int read;
            while ((read = await output.ReadAsync(buffer.AsMemory(carry), ct).ConfigureAwait(false)) > 0)
            {
                var length = carry + read;
                var even = length & ~1;
                detector.Add(buffer.AsSpan(0, even));
                carry = length - even;
                if (carry > 0) buffer[0] = buffer[even];
            }
        }, ct).ConfigureAwait(false);
        if (!ok || detector.Frames < frames / 2) return null;
        Kind = "audio";
        var speech = detector.Speech();
        return speech.Length == frames ? speech : speech.Concat(Enumerable.Repeat(0f, Math.Max(0, frames - speech.Length))).Take(frames).ToArray();
    }

    private async Task<string?> TextAsync(string[] arguments, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var ok = await RunAsync(arguments, output => output.CopyToAsync(memory, ct), ct).ConfigureAwait(false);
        return ok ? Encoding.UTF8.GetString(memory.ToArray()) : null;
    }

    /// <summary>Runs ffmpeg with its output read by <paramref name="read"/>; false when it failed, took too long or was stopped.</summary>
    private async Task<bool> RunAsync(string[] arguments, Func<Stream, Task> read, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-v", "error" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Longest);
        using var stop = limit.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await read(process.StandardOutput.BaseStream).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
        return process.ExitCode == 0;
    }
}
