using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>
/// Tailscale's command line on this PC (<see cref="TailscaleNode"/>), run in the person's own rights: on Windows, the
/// person signed in to Tailscale reads and changes its preferences without administrator rights. Only the copy its
/// installer puts in Program Files, never one found on the PATH.
/// </summary>
internal static class TailscaleCli
{
    private static string? Program()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Its state; Unreadable without its command line, or when Tailscale does not answer it.</summary>
    public static TailscaleNode.State Read()
    {
        if (Program() is not { } program) return TailscaleNode.Unreadable;
        var status = Run(program, "status --json");
        return TailscaleNode.Parse(status, status is null ? null : Run(program, "debug prefs"), DateTimeOffset.Now);
    }

    /// <summary>
    /// <c>tailscale status --json</c>, whose devices the login screen asks for a Jellyfin (<see cref="ServerSearch.TailnetDevices"/>);
    /// null without its command line, or when Tailscale does not answer it.
    /// </summary>
    public static string? Status() => Program() is { } program ? Run(program, "status --json") : null;

    /// <summary>
    /// « Garder Tailscale connecté »: « Run unattended » turned on, as its menu does (<c>tailscale set</c> changes that
    /// preference only); true once read back on.
    /// </summary>
    public static bool KeepConnected() => Program() is { } program && Run(program, "set --unattended=true") is not null && Read().Unattended == true;

    /// <summary>Its output when it ends well within 10 s; null otherwise.</summary>
    private static string? Run(string program, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            });
            if (process is null) return null;
            // Read to the end so that a full pipe never blocks it.
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10))) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            Task.WaitAll([output, errors], 5_000);
            return process.ExitCode == 0 && output.IsCompletedSuccessfully ? output.Result : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or AggregateException) { return null; }
    }
}
