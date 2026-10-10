using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>How setting Jellyfin up to answer from the PC's start went.</summary>
internal enum AutostartResult { Done, Refused, Failed }

/// <summary>
/// Jellyfin's service and the firewall, so that phones find Mira web, at home and on Tailscale, and as soon as the PC
/// is on (see <see cref="JellyfinStartup"/>). Read without administrator rights; set up by a copy of Mira started with
/// them (<c>--jellyfin-startup &lt;port&gt;</c>), which reads the service and the firewall itself rather than trusting
/// its arguments.
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

    /// <summary>
    /// The jellyfin.exe the service runs (NSSM's Application value), the one in the installer's folder (also the one
    /// its tray app starts), or the one running, for a Jellyfin installed some other way.
    /// </summary>
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
        if (JellyfinInstaller.InstalledFolder() is { } folder && File.Exists(Path.Combine(folder, "jellyfin.exe"))) return Path.Combine(folder, "jellyfin.exe");
        foreach (var process in Process.GetProcessesByName("jellyfin"))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is { } path && Path.IsPathFullyQualified(path)) return path; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
        return null;
    }

    /// <summary>Whether the firewall has Mira's rule: <c>netsh … show rule</c> needs no administrator rights.</summary>
    private static bool RuleExists(string rule) => Execute(new("netsh.exe", $"advfirewall firewall show rule name=\"{rule}\"")) == 0;

    /// <summary>Asks Windows for the administrator's consent, once, and sets it all up in a copy of Mira that has it.</summary>
    public static Task<AutostartResult> ApplyAsync(int port) => ElevatedAsync($"--jellyfin-startup {port}");
    private static async Task<AutostartResult> ElevatedAsync(string arguments)
    {
        if (Environment.ProcessPath is not { } self) return AutostartResult.Failed;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(self, arguments) { UseShellExecute = true, Verb = "runas" });
            if (process is null) return AutostartResult.Failed;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            return process.ExitCode == 0 ? AutostartResult.Done : AutostartResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return AutostartResult.Refused; }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException) { return AutostartResult.Failed; }
    }

    /// <summary>
    /// The copy of Mira started with administrator rights by « Tout préparer »: <c>--jellyfin-startup &lt;port&gt;</c>.
    /// The firewall lets phones through, with or without Jellyfin's service; the service, when there is one, starts with
    /// Windows and again after an error. 0 once every step that matters succeeded, 1 otherwise, 2 for arguments it does
    /// not accept.
    /// </summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var port) || port is < 1 or > 65535) return 2;
        var program = Program();
        var open = OpenFirewall(program, port);
        var boot = !Read().HasService || Apply(program, port);
        return open && boot ? 0 : 1;
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
    /// <summary>
    /// Whether Windows' firewall lets phones reach <paramref name="program"/> on <paramref name="port"/>, from the home
    /// network and, when Tailscale is connected here, from Tailscale: the worse of the two, with the profile it was
    /// judged on. Read through its COM interface, which needs no administrator rights. Null when it cannot be read, or
    /// when this PC is on no network.
    /// </summary>
    public static (JellyfinFirewall.Verdict Verdict, int Profile)? Phones(string? program, int port)
    {
        if (program is null) return null;
        try
        {
            var (rules, settings, active) = ReadFirewall(Policy(), program);
            (JellyfinFirewall.Verdict Verdict, int Profile)? worst = null;
            foreach (var origin in Origins(active))
            {
                var verdict = JellyfinFirewall.Judge(rules.Select(x => x.Rule), settings[origin.Profile], program, port, origin.Profile, origin.Adapter, origin.Home);
                if (worst is null || verdict > worst.Value.Verdict) worst = (verdict, origin.Profile);
            }
            return worst;
        }
        catch (Exception ex) when (IsFirewallError(ex)) { return null; }
    }

    /// <summary>
    /// Lets phones through to <paramref name="program"/>: its own block rules that keep them out (Windows' prompt makes
    /// one for each kind of network left unticked) stop applying to the home network's profile and to Tailscale's
    /// (Private, which Tailscale sets, even before it is connected here), and are turned off when no other profile is
    /// left; then Mira's rule (<see cref="JellyfinStartup.FirewallCommands"/>), which lets in only the home network and
    /// Tailscale. A block rule for any program is someone's own and stays. Needs administrator rights. True once the
    /// firewall, read again, lets phones in.
    /// </summary>
    public static bool OpenFirewall(string? program, int port, string rule = JellyfinStartup.RuleName)
    {
        if (program is null) return false;
        try
        {
            var (rules, _, active) = ReadFirewall(Policy(), program);
            var origins = Origins(active);
            if (!origins.Any(x => x.Home is null)) origins.Add(new(JellyfinFirewall.Private, null, null));
            foreach (var (found, com) in rules.Where(x => !x.Rule.Allow && !string.IsNullOrWhiteSpace(x.Rule.Program)))
            {
                var lifted = origins.Where(x => JellyfinFirewall.Applies(found, program, port, x.Profile, x.Adapter, x.Home)).Aggregate(0, (bits, x) => bits | x.Profile);
                if (lifted == 0) continue;
                var others = JellyfinFirewall.WithoutProfile(found.Profiles, lifted);
                if (others != 0) com.Profiles = others; else com.Enabled = false;
            }
        }
        catch (Exception ex) when (IsFirewallError(ex)) { return false; }
        foreach (var command in JellyfinStartup.FirewallCommands(program, port, rule))
            if (Execute(command) != 0 && !command.Optional) return false;
        // Blocking everything is a setting of Windows the person undoes: the guide says where.
        return Phones(program, port) is null or { Verdict: not JellyfinFirewall.Verdict.Blocked };
    }

    /// <summary>Where phones connect from: the home network (this PC's address there stands for it), or Tailscale's range (no address).</summary>
    private sealed record Origin(int Profile, string? Adapter, string? Home);
    /// <summary>
    /// The home network, on its adapter's profile, and Tailscale, when it is connected here, on its own. A profile
    /// Windows does not say (<see cref="NetworkCategories"/>): for the home network, each one active here; for
    /// Tailscale, the private one it sets.
    /// </summary>
    private static List<Origin> Origins(int active)
    {
        var categories = NetworkCategories();
        var origins = new List<Origin>();
        if (LocalNetwork.ThisPcOnNetwork() is { } home)
        {
            if (categories.TryGetValue(home.Adapter, out var profile)) origins.Add(new(profile, home.Adapter, home.Address));
            else foreach (var bit in new[] { JellyfinFirewall.Domain, JellyfinFirewall.Private, JellyfinFirewall.Public }.Where(x => (active & x) != 0))
                origins.Add(new(bit, home.Adapter, home.Address));
        }
        if (TailnetAdapter() is { } tailnet)
            origins.Add(new(categories.TryGetValue(tailnet, out var profile) ? profile : JellyfinFirewall.TailnetProfile(active), tailnet, null));
        return origins;
    }
    /// <summary>
    /// The profile Windows counts each network adapter under (« Wi-Fi » → Public…), as Get-NetConnectionProfile shows it:
    /// MSFT_NetConnectionProfile, read through WMI's scripting objects, which need no administrator rights. Empty when
    /// it cannot be read.
    /// </summary>
    internal static Dictionary<string, int> NetworkCategories()
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic service = locator.ConnectServer(".", @"root\StandardCimv2");
            foreach (dynamic profile in service.ExecQuery("SELECT InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile"))
            {
                string? adapter = profile.Properties_.Item("InterfaceAlias").Value;
                // NetworkCategory: 0 public, 1 private, 2 domain.
                var category = Convert.ToInt32((object)profile.Properties_.Item("NetworkCategory").Value);
                if (!string.IsNullOrEmpty(adapter))
                    found[adapter] = category switch { 1 => JellyfinFirewall.Private, 2 => JellyfinFirewall.Domain, _ => JellyfinFirewall.Public };
            }
        }
        catch (Exception ex) when (IsFirewallError(ex) || ex is FormatException or OverflowException) { }
        return found;
    }

    /// <summary>
    /// The incoming rules that may concern <paramref name="program"/> (its own and those for any program), each with its
    /// COM object, the firewall's settings on each profile, and the profiles active here. Rules of other programs are
    /// skipped unread.
    /// </summary>
    private static (List<(JellyfinFirewall.Rule Rule, dynamic Com)> Rules, Dictionary<int, JellyfinFirewall.Profile> Settings, int Active) ReadFirewall(object source, string program)
    {
        dynamic policy = source;
        int active = (int)policy.CurrentProfileTypes;
        // NET_FW_ACTION: 0 block, 1 allow. Indexed properties of the profile, read through IDispatch.
        var settings = new Dictionary<int, JellyfinFirewall.Profile>();
        foreach (var profile in new[] { JellyfinFirewall.Domain, JellyfinFirewall.Private, JellyfinFirewall.Public })
            settings[profile] = new((bool)policy.FirewallEnabled[profile], (int)policy.DefaultInboundAction[profile] == 1, (bool)policy.BlockAllInboundTraffic[profile]);
        var rules = new List<(JellyfinFirewall.Rule, dynamic)>();
        foreach (dynamic rule in policy.Rules)
        {
            // NET_FW_RULE_DIRECTION: 1 incoming.
            if ((int)rule.Direction != 1 || !(bool)rule.Enabled) continue;
            string? application = rule.ApplicationName;
            if (!string.IsNullOrWhiteSpace(application) && !JellyfinFirewall.SameProgram(application, program)) continue;
            // A rule naming no program may be for one service only.
            string? service = string.IsNullOrWhiteSpace(application) ? rule.serviceName : null;
            object? interfaces = rule.Interfaces;
            rules.Add((new JellyfinFirewall.Rule((string?)rule.Name ?? "", true, true, (int)rule.Action == 1, (int)rule.Profiles, application, (int)rule.Protocol,
                (string?)rule.LocalPorts, (string?)rule.RemoteAddresses, interfaces is object[] names ? names.OfType<string>().ToList() : null, (string?)rule.InterfaceTypes,
                service), rule));
        }
        return (rules, settings, active);
    }
    /// <summary>The incoming rules that may concern <paramref name="program"/>, as <see cref="Phones"/> reads them (the tests' check).</summary>
    internal static List<JellyfinFirewall.Rule> FirewallRules(string program) => ReadFirewall(Policy(), program).Rules.Select(x => x.Rule).ToList();
    /// <summary>
    /// Windows' firewall policy (HNetCfg.FwPolicy2, FirewallAPI.dll), driven by name through IDispatch: no interop
    /// assembly to keep in step, and a wrong name fails instead of calling another method.
    /// </summary>
    private static object Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
    /// <summary>The name of Tailscale's adapter, which a rule for given interfaces would list.</summary>
    private static string? TailnetAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(nic => nic.OperationalStatus == OperationalStatus.Up
                && LocalNetwork.TailnetIPv4(nic.GetIPProperties().UnicastAddresses.Select(x => (x.Address.ToString(), nic.Name + " " + nic.Description))) is not null)?.Name;
        }
        catch (NetworkInformationException) { return null; }
    }
    private static bool IsFirewallError(Exception ex) => ex is COMException or InvalidComObjectException or RuntimeBinderException or InvalidCastException or UnauthorizedAccessException
        or ArgumentException or TypeLoadException or NotSupportedException or System.Reflection.TargetInvocationException;

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
