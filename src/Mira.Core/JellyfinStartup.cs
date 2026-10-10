namespace Mira.Core;

/// <summary>
/// A Jellyfin on this PC that phones reach as soon as the PC is on, before anyone signs in: its Windows service starts
/// with Windows (not delayed), Windows starts it again when it stops on an error, and the firewall lets the home
/// network and Tailscale reach its port. Jellyfin's installer does the first only: its service, under Network Service,
/// gets no firewall rule (a service is never offered Windows' "allow" prompt), and NSSM stops for good when Jellyfin
/// exits. A shutdown asked from Jellyfin's dashboard ends with code 0, which Windows does not undo.
/// </summary>
public static class JellyfinStartup
{
    public const string ServiceName = "JellyfinServer";
    public const string RuleName = "Mira - Jellyfin";
    /// <summary>15 s after a first error, then 30 s, then a minute; the count starts again after a day without one.</summary>
    public const string RestartActions = "restart/15000/restart/30000/restart/60000";

    /// <summary>
    /// What Mira reads of the service and of its firewall rule (<paramref name="FirewallOpen"/>: Mira's rule exists),
    /// without administrator rights.
    /// </summary>
    public sealed record State(bool HasService, bool StartsWithWindows, bool Delayed, bool RestartsAfterError, bool FirewallOpen)
    {
        /// <summary>The service starts with Windows, not delayed, and again after an error: Jellyfin answers before anyone signs in.</summary>
        public bool StartsAtBoot => HasService && StartsWithWindows && !Delayed && RestartsAfterError;
    }

    /// <summary>
    /// Whether the service's FailureActions value restarts it, also after an error exit that is not a crash
    /// (FailureActionsOnNonCrashFailures = 1: NSSM stops with Jellyfin's exit code). The value is SERVICE_FAILURE_ACTIONS
    /// as Windows stores it: reset period, two unused fields, the number of actions, an unused field, then each action's
    /// type (1: restart) and delay.
    /// </summary>
    public static bool RestartsAfterError(byte[]? failureActions, int nonCrashFailures)
    {
        if (nonCrashFailures != 1 || failureActions is null || failureActions.Length < 28) return false;
        var count = BitConverter.ToInt32(failureActions, 12);
        return count > 0 && failureActions.Length >= 20 + 8 * count && BitConverter.ToInt32(failureActions, 20) == 1;
    }

    /// <summary>
    /// The programs of Windows that set it all up, in order, run with administrator rights. <paramref name="program"/>
    /// is the jellyfin.exe the service runs: the rule lets in only it, only on <paramref name="port"/>, and only from
    /// this PC's own networks (any profile: at start, Windows may count the home network as public for a while) and
    /// Tailscale's. Optional steps may fail: no rule to replace yet, a service already running.
    /// </summary>
    public static IReadOnlyList<StartupCommand> Commands(string program, int port, string service = ServiceName, string rule = RuleName)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (!Path.IsPathFullyQualified(program) || program.Contains('"')) throw new ArgumentException("A full path to jellyfin.exe is expected.", nameof(program));
        if (service.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')) throw new ArgumentException("Unexpected service name.");
        return
        [
            new("sc.exe", $"config {service} start= auto"),
            new("sc.exe", $"failure {service} reset= 86400 actions= {RestartActions}"),
            new("sc.exe", $"failureflag {service} 1"),
            .. FirewallCommands(program, port, rule),
            new("sc.exe", $"start {service}", Optional: true),
        ];
    }

    /// <summary>
    /// Mira's firewall rule, replaced whole: the home network and Tailscale may reach <paramref name="program"/> on
    /// <paramref name="port"/>, on any profile, and nothing else. Also what the guide's « Ouvrir le pare-feu » sets, with
    /// or without Jellyfin's service.
    /// </summary>
    public static IReadOnlyList<StartupCommand> FirewallCommands(string program, int port, string rule = RuleName)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (!Path.IsPathFullyQualified(program) || program.Contains('"')) throw new ArgumentException("A full path to jellyfin.exe is expected.", nameof(program));
        if (rule.Contains('"')) throw new ArgumentException("Unexpected rule name.");
        string Allow(string from) => $"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP localport={port} " +
            $"remoteip={from} profile=any program=\"{program}\" enable=yes description=\"Mira web : Jellyfin pour tes appareils, chez toi et sur Tailscale.\"";
        return
        [
            new("netsh.exe", $"advfirewall firewall delete rule name=\"{rule}\"", Optional: true),
            new("netsh.exe", Allow("localsubnet")),
            new("netsh.exe", Allow(LocalNetwork.TailnetRange)),
        ];
    }
}

/// <summary>A program of Windows (in System32) and its arguments.</summary>
public sealed record StartupCommand(string File, string Arguments, bool Optional = false);
