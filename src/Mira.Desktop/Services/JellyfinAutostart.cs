using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>How setting Jellyfin up to answer from the PC's start went.</summary>
internal enum AutostartResult { Done, Refused, Failed }

/// <summary>
/// Jellyfin's service and the firewall, so that phones find Mira web as soon as the PC is on (see
/// <see cref="JellyfinStartup"/>). Read without administrator rights; set up by a copy of Mira started with them
/// (<c>--jellyfin-startup &lt;port&gt;</c>), which reads the service itself rather than trusting its arguments.
/// </summary>
internal static class JellyfinAutostart
{
    private static string ServiceKey(string service) => @"SYSTEM\CurrentControlSet\Services\" + service;

    /// <summary>The service's start mode, its restart after an error, and Mira's firewall rule.</summary>
    public static JellyfinStartup.State Read(string service = JellyfinStartup.ServiceName, string rule = JellyfinStartup.RuleName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey(service));
            if (key is null) return new(false, false, false, false, false);
            var start = key.GetValue("Start") is int value ? value : 3;
            var delayed = key.GetValue("DelayedAutostart") is int flag && flag == 1;
            var restarts = JellyfinStartup.RestartsAfterError(key.GetValue("FailureActions") as byte[], key.GetValue("FailureActionsOnNonCrashFailures") is int nonCrash ? nonCrash : 0);
            return new(true, start == 2, delayed, restarts, RuleExists(rule));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return new(false, false, false, false, false); }
    }

    /// <summary>The jellyfin.exe the service runs: NSSM's Application value, or the installer's folder.</summary>
    public static string? Program(string service = JellyfinStartup.ServiceName)
    {
        try
        {
            using var parameters = Registry.LocalMachine.OpenSubKey(ServiceKey(service) + @"\Parameters");
            if (parameters?.GetValue("Application") is string application && application.Length > 0)
            {
                var path = Environment.ExpandEnvironmentVariables(application.Trim().Trim('"'));
                if (Path.IsPathFullyQualified(path) && File.Exists(path)) return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return JellyfinInstaller.InstalledFolder() is { } folder && File.Exists(Path.Combine(folder, "jellyfin.exe")) ? Path.Combine(folder, "jellyfin.exe") : null;
    }

    /// <summary>Whether the firewall has Mira's rule: <c>netsh … show rule</c> needs no administrator rights.</summary>
    private static bool RuleExists(string rule) => Execute(new("netsh.exe", $"advfirewall firewall show rule name=\"{rule}\"")) == 0;

    /// <summary>Asks Windows for the administrator's consent and sets it all up in a copy of Mira that has it.</summary>
    public static async Task<AutostartResult> ApplyAsync(int port)
    {
        if (Environment.ProcessPath is not { } self) return AutostartResult.Failed;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(self, $"--jellyfin-startup {port}") { UseShellExecute = true, Verb = "runas" });
            if (process is null) return AutostartResult.Failed;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            return process.ExitCode == 0 ? AutostartResult.Done : AutostartResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return AutostartResult.Refused; }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException) { return AutostartResult.Failed; }
    }

    /// <summary>
    /// The copy of Mira started with administrator rights: <c>--jellyfin-startup &lt;port&gt;</c>. 0 once every step
    /// that matters succeeded, 1 otherwise, 2 for arguments it does not accept.
    /// </summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var port) || port is < 1 or > 65535) return 2;
        return Apply(Program(), port) ? 0 : 1;
    }

    /// <summary>
    /// Runs <see cref="JellyfinStartup.Commands"/>, the service started last unless <paramref name="start"/> is false;
    /// a service and a rule of another name serve the tests.
    /// </summary>
    public static bool Apply(string? program, int port, string service = JellyfinStartup.ServiceName, string rule = JellyfinStartup.RuleName, bool start = true)
    {
        if (program is null) return false;
        var ok = true;
        foreach (var command in JellyfinStartup.Commands(program, port, service, rule))
        {
            if (!start && command.Arguments.StartsWith("start ", StringComparison.Ordinal)) continue;
            if (Execute(command) != 0 && !command.Optional) ok = false;
        }
        return ok;
    }
    /// <summary>Removes Mira's firewall rule (the tests' own).</summary>
    public static void RemoveRule(string rule) => Execute(new("netsh.exe", $"advfirewall firewall delete rule name=\"{rule}\""));
    /// <summary>A program of System32 with its arguments: its exit code.</summary>
    public static int RunSystem(string file, string arguments) => Execute(new(file, arguments));

    /// <summary>A program of System32 (never one found on the PATH: this may run with administrator rights), hidden.</summary>
    private static int Execute(StartupCommand command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, command.File), command.Arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null) return -1;
            // Read to the end so that a full pipe never blocks the program.
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000)) { try { process.Kill(); } catch (InvalidOperationException) { } return -1; }
            Task.WaitAll([output, errors], 5_000);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return -1; }
    }
}
