using System.Diagnostics;
using System.Text;

namespace Mira.Mac.Services;

/// <summary>
/// Tailscale's command line on this Mac, for the devices the login screen asks for a Jellyfin: the one inside Tailscale's
/// app (every version of it carries one), else the one its standalone or Homebrew installation puts on disk; never one
/// found on the PATH.
/// </summary>
internal static class TailscaleCli
{
    private static readonly string[] Programs = ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale"];

    /// <summary><c>tailscale status --json</c> when it ends well within 5 s; null otherwise (not installed, not connected).</summary>
    public static string? Status()
    {
        if (Programs.FirstOrDefault(File.Exists) is not { } program) return null;
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("status"); start.ArgumentList.Add("--json");
            using var process = Process.Start(start);
            if (process is null) return null;
            // Read to the end so that a full pipe never blocks it.
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5))) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            Task.WaitAll([output, errors], 2_000);
            return process.ExitCode == 0 && output.IsCompletedSuccessfully ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException) { return null; }
    }
}
