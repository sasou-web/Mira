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

    /// <summary>
    /// « Inviter par lien »: whether Funnel opens Mira to the Internet (<c>tailscale funnel status --json</c>, read by
    /// <see cref="GuestLinks.FunnelServesMira"/>), and the address it gives this PC; false and null without Tailscale.
    /// </summary>
    public static (bool Serves, string? Address) Funnel()
    {
        if (Program() is not { } program) return (false, null);
        return (GuestLinks.FunnelServesMira(Run(program, "funnel status --json")), GuestLinks.FunnelAddress(Run(program, "status --json")));
    }

    /// <summary>
    /// Funnel turned on for /Mira only (<see cref="GuestLinks.FunnelArguments"/>), in the person's rights, as Tailscale's
    /// menu would: whether it serves Mira afterwards, and the page where Funnel is allowed on the tailnet when Tailscale
    /// asks for it first (its command then waits for that: given 20 s, then stopped).
    /// </summary>
    public static (bool Serves, string? Address, string? EnablePage) OpenFunnel(int port)
    {
        if (Program() is not { } program) return (false, null, null);
        var said = RunAll(program, GuestLinks.FunnelArguments(port), TimeSpan.FromSeconds(20));
        var (serves, address) = Funnel();
        return (serves, address, serves ? null : GuestLinks.EnablePage(said));
    }

    /// <summary>What it wrote, both outputs, whether it ended well or not, stopped after <paramref name="limit"/>.</summary>
    private static string RunAll(string program, string arguments, TimeSpan limit)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            });
            if (process is null) return "";
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(limit)) { try { process.Kill(); } catch (InvalidOperationException) { } }
            Task.WaitAll([output, errors], 5_000);
            return (output.IsCompletedSuccessfully ? output.Result : "") + "\n" + (errors.IsCompletedSuccessfully ? errors.Result : "");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or AggregateException) { return ""; }
    }

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
