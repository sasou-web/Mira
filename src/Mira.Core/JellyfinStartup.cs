using System.Globalization;
using System.Text;

namespace Mira.Core;

/// <summary>
/// A Jellyfin on this PC that phones reach as soon as the PC is on, before anyone signs in: its Windows service starts
/// with Windows (delayed or not), Windows starts it again when it stops on an error (three times, then it stays
/// stopped until a day without one), and the firewall lets the home network and Tailscale reach its port. Jellyfin's
/// installer does the first only: its service, under Network Service, gets no firewall rule (a service is never offered
/// Windows' "allow" prompt), and NSSM stops for good when Jellyfin exits. A shutdown asked from Jellyfin's dashboard
/// ends with code 0, which Windows does not undo.
/// </summary>
public static class JellyfinStartup
{
    public const string ServiceName = "JellyfinServer";
    public const string RuleName = "Mira - Jellyfin";
    /// <summary>
    /// 15 s after a first error, then 30 s, then a minute, then nothing: Windows repeats the last action for every later
    /// failure, so an empty one ends the restarts. The count starts again after a day without an error.
    /// </summary>
    public const string RestartActions = "restart/15000/restart/30000/restart/60000//0";
    /// <summary>
    /// The network profiles Mira's rule applies to (<see cref="JellyfinFirewall"/>'s bits): private and domain, never
    /// public, where LocalSubnet is a hotspot's and Jellyfin counts strangers' private addresses as local. A home network
    /// Windows counts as public is made private instead, when the person says it is theirs (« Passer en privé »).
    /// </summary>
    public const int RuleProfiles = JellyfinFirewall.Domain | JellyfinFirewall.Private;

    /// <summary>What Mira reads of the service, without administrator rights.</summary>
    public sealed record State(bool HasService, bool StartsWithWindows, bool Delayed, bool RestartsAfterError)
    {
        /// <summary>
        /// The service starts with Windows and again after an error: Jellyfin answers before anyone signs in. Windows'
        /// delayed start (a couple of minutes after the PC's) is the person's choice, and kept.
        /// </summary>
        public bool StartsAtBoot => HasService && StartsWithWindows && RestartsAfterError;
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
    /// this PC's own networks and Tailscale's, on private and domain networks (<see cref="RuleProfiles"/>).
    /// <paramref name="delayed"/>: the service already starts delayed, which stays. Optional steps may fail: no rule to
    /// replace yet, a service already running.
    /// </summary>
    public static IReadOnlyList<StartupCommand> Commands(string program, int port, string service = ServiceName, string rule = RuleName, bool delayed = false)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (!Path.IsPathFullyQualified(program) || program.Contains('"')) throw new ArgumentException("A full path to jellyfin.exe is expected.", nameof(program));
        if (service.Length == 0 || service.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')) throw new ArgumentException("Unexpected service name.");
        return
        [
            new("sc.exe", $"config {service} start= {(delayed ? "delayed-auto" : "auto")}"),
            new("sc.exe", $"failure {service} reset= 86400 actions= {RestartActions}"),
            new("sc.exe", $"failureflag {service} 1"),
            .. FirewallCommands(program, port, rule),
            new("sc.exe", $"start {service}", Optional: true),
        ];
    }

    /// <summary>
    /// Mira's firewall rule, replaced whole: the home network and Tailscale may reach <paramref name="program"/> on
    /// <paramref name="port"/>, and the home network may ask it Jellyfin's discovery question (UDP 7359, how Mira on
    /// another computer finds it: <see cref="ServerSearch"/>), on private and domain networks, and nothing else. Also what
    /// « Tout préparer » writes (OpenFirewall), with or without Jellyfin's service. Tailscale's range is not bound to
    /// Tailscale's adapter: netsh cannot name one, and the rule is written before Tailscale may be installed; Tailscale
    /// carries no broadcast, so the discovery is for the home network alone.
    /// </summary>
    public static IReadOnlyList<StartupCommand> FirewallCommands(string program, int port, string rule = RuleName)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (!Path.IsPathFullyQualified(program) || program.Contains('"')) throw new ArgumentException("A full path to jellyfin.exe is expected.", nameof(program));
        string Allow(string protocol, int on, string from, string description) => $"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol={protocol} localport={on} " +
            $"remoteip={from} profile={Profiles(RuleProfiles)} program=\"{program}\" enable=yes description=\"{description}\"";
        const string devices = "Mira web : Jellyfin pour tes appareils, chez toi et sur Tailscale.";
        return
        [
            RemoveRule(rule),
            new("netsh.exe", Allow("TCP", port, "localsubnet", devices)),
            new("netsh.exe", Allow("TCP", port, LocalNetwork.TailnetRange, devices)),
            new("netsh.exe", Allow("UDP", ServerSearch.Port, "localsubnet", "Mira : tes autres ordinateurs trouvent Jellyfin sur le réseau de la maison.")),
        ];
    }
    /// <summary>Removes Mira's firewall rule (« Ne plus ouvrir Jellyfin au réseau »): optional, as there may be none.</summary>
    public static StartupCommand RemoveRule(string rule = RuleName)
    {
        if (rule.Length == 0 || rule.Contains('"')) throw new ArgumentException("Unexpected rule name.", nameof(rule));
        return new("netsh.exe", $"advfirewall firewall delete rule name=\"{rule}\"", Optional: true);
    }
    /// <summary>Profiles as netsh writes them: "any", or a list ("domain,private"), in the order of their bits.</summary>
    public static string Profiles(int profiles) => (profiles & JellyfinFirewall.AllProfiles) == JellyfinFirewall.AllProfiles ? "any"
        : string.Join(",", new[] { "domain", "private", "public" }.Where((_, bit) => (profiles & 1 << bit) != 0));

    /// <summary>
    /// A block rule of jellyfin.exe that keeps phones out, as Mira read it (<paramref name="Program"/>: its
    /// ApplicationName as written), and the profiles it is left with: <paramref name="Keep"/>, or turned off when none.
    /// </summary>
    public sealed record Lift(string Name, string Program, int Protocol, string? LocalPorts, string? RemoteAddresses, int Profiles, int Keep);
    /// <summary>
    /// What the copy of Windows PowerShell started with administrator rights does, in order: the networks the person
    /// said are home's made private (never a domain one), the block rules lifted, then the commands.
    /// </summary>
    public sealed record Plan(IReadOnlyList<string> PrivateAdapters, IReadOnlyList<Lift> Lifts, IReadOnlyList<StartupCommand> Commands);

    /// <summary>Windows PowerShell, in System32: the program elevated, which no one without administrator rights can change.</summary>
    public const string PowerShell = @"WindowsPowerShell\v1.0\powershell.exe";
    /// <summary>The longest command line Windows passes on (32767 characters, with the program's path).</summary>
    public const int MaximumArguments = 30_000;

    /// <summary>
    /// The arguments of <see cref="PowerShell"/> that carry out <paramref name="plan"/>: no profile, no window, and the
    /// script itself as <c>-EncodedCommand</c>, so that no file Mira could leave behind is run with those rights.
    /// </summary>
    public static string PowerShellArguments(Plan plan)
    {
        var arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(plan)));
        return arguments.Length <= MaximumArguments ? arguments : throw new ArgumentException("Too much to set up in one go.", nameof(plan));
    }

    /// <summary>
    /// The script of <see cref="PowerShellArguments"/>: fixed code, the plan's numbers written as numbers and its text
    /// (adapter and rule names, command arguments) as base64, decoded by the script, so that nothing in them is ever read
    /// as code. Only the programs Mira runs (sc.exe, netsh.exe, from System32) and Windows' own COM objects are used: no
    /// cmdlet, and no module loaded (a module in the person's Documents would run with administrator rights). Exit code
    /// 0 once every command that is not optional succeeded, 1 otherwise; what was done is read again by Mira.
    /// </summary>
    public static string Script(Plan plan)
    {
        static string Text(string? value) => "'" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "")) + "'";
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        var adapters = plan.PrivateAdapters.Select(adapter => adapter.Length > 0 ? Text(adapter) : throw new ArgumentException("An adapter without a name.", nameof(plan)));
        var lifts = plan.Lifts.Select(x => (x.Keep & ~JellyfinFirewall.AllProfiles) == 0 && x.Profiles != 0 && x.Program.Length > 0
            ? $"@{{ N = (T {Text(x.Name)}); A = (T {Text(x.Program)}); P = {Number(x.Protocol)}; L = (T {Text(x.LocalPorts)}); R = (T {Text(x.RemoteAddresses)}); F = {Number(x.Profiles)}; K = {Number(x.Keep)} }}"
            : throw new ArgumentException("Unexpected profiles for " + x.Name, nameof(plan)));
        var commands = plan.Commands.Select(x => x.File is "sc.exe" or "netsh.exe"
            ? $"@{{ F = '{x.File}'; A = (T {Text(x.Arguments)}); O = ${(x.Optional ? "true" : "false")} }}"
            : throw new ArgumentException("Not a program Mira runs: " + x.File, nameof(plan)));
        // Windows PowerShell 5.1: no ?? nor ternary; @() keeps a single element a list.
        return $$"""
            $ErrorActionPreference = 'Stop'
            $PSModuleAutoLoadingPreference = 'None'
            function T([string]$b) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b)) }
            $adapters = @({{string.Join(", ", adapters)}})
            $lifts = @({{string.Join(", ", lifts)}})
            $commands = @({{string.Join(", ", commands)}})
            foreach ($adapter in $adapters) {
              $alias = T $adapter
              try {
                $wmi = [Activator]::CreateInstance([Type]::GetTypeFromProgID('WbemScripting.SWbemLocator', $true)).ConnectServer('.', 'root\StandardCimv2')
                foreach ($connection in $wmi.ExecQuery('SELECT * FROM MSFT_NetConnectionProfile')) {
                  if ([string]$connection.Properties_.Item('InterfaceAlias').Value -ne $alias) { continue }
                  $category = $connection.Properties_.Item('NetworkCategory')
                  if ([int]$category.Value -ne 0) { continue }
                  $category.Value = 1
                  [void]$connection.Put_(1)
                }
              } catch { }
            }
            if ($lifts.Count -gt 0) {
              try {
                $policy = [Activator]::CreateInstance([Type]::GetTypeFromProgID('HNetCfg.FwPolicy2', $true))
                $found = @()
                foreach ($rule in $policy.Rules) {
                  if ([int]$rule.Direction -ne 1 -or [int]$rule.Action -ne 0 -or -not [bool]$rule.Enabled) { continue }
                  foreach ($lift in $lifts) {
                    if ([string]$rule.Name -ceq $lift.N -and [string]$rule.ApplicationName -ceq $lift.A -and [int]$rule.Protocol -eq $lift.P -and [string]$rule.LocalPorts -ceq $lift.L -and [string]$rule.RemoteAddresses -ceq $lift.R -and [int]$rule.Profiles -eq $lift.F) {
                      $found += ,@($rule, $lift.K)
                      break
                    }
                  }
                }
                foreach ($pair in $found) {
                  try { if ($pair[1] -ne 0) { $pair[0].Profiles = $pair[1] } else { $pair[0].Enabled = $false } } catch { }
                }
              } catch { }
            }
            $failed = $false
            foreach ($command in $commands) {
              $code = -1
              try {
                $info = [Diagnostics.ProcessStartInfo]::new([IO.Path]::Combine([Environment]::SystemDirectory, $command.F), $command.A)
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $process = [Diagnostics.Process]::Start($info)
                if ($process.WaitForExit(30000)) { $code = $process.ExitCode } else { try { $process.Kill() } catch { } }
              } catch { }
              if ($code -ne 0 -and -not $command.O) { $failed = $true }
            }
            if ($failed) { exit 1 }
            exit 0
            """;
    }
}

/// <summary>A program of Windows (in System32) and its arguments.</summary>
public sealed record StartupCommand(string File, string Arguments, bool Optional = false);
