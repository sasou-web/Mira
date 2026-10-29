using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Mira.Core.TorLink;

namespace Mira.Desktop.Services;

/// <summary>
/// « Remplacer par une meilleure version »: the new file read by Jellyfin's own ffprobe, as Jellyfin will read it,
/// before the old one goes. Only the copy beside Jellyfin's ffmpeg (as its settings name it) or beside jellyfin.exe,
/// never one found on the PATH.
/// </summary>
internal static class FfProbe
{
    /// <summary>Jellyfin's ffprobe on this PC; null when there is none.</summary>
    public static string? Program(string? encoder)
    {
        foreach (var folder in new[] { encoder, JellyfinAutostart.Program() }.Select(x => x is { Length: > 0 } && Path.IsPathFullyQualified(x) ? Path.GetDirectoryName(x) : null))
            if (folder is { Length: > 0 } && Path.Combine(folder, "ffprobe.exe") is var path && File.Exists(path)) return path;
        return null;
    }

    /// <summary>The file's length; null when ffprobe cannot read it, finds no picture in it, or takes over two minutes.</summary>
    public static async Task<TimeSpan?> LengthAsync(string program, string file, CancellationToken ct)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", file }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            // Never in the way of a film being watched.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None); _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
            return process.ExitCode == 0 ? TitleUpgrade.ProbedLength(await output) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; }
    }
}
