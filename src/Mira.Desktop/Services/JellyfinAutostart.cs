using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>
/// How setting Jellyfin up to answer from the PC's start went. KeptPublic: done, but Windows kept a network phones come
/// from public (one it cannot identify), where Mira's rule never applies: the person's to change in Windows' settings.
/// </summary>
internal enum AutostartResult { Done, Refused, Failed, KeptPublic }

/// <summary>
/// Jellyfin's service and the firewall, so that phones find Mira web, at home and on Tailscale, and as soon as the PC
/// is on (see <see cref="JellyfinStartup"/>). Read without administrator rights; set up by a copy of Mira started with
/// them (<c>--jellyfin-startup &lt;port&gt;</c>), which reads the service and the firewall itself rather than trusting
/// its arguments.
/// </summary>
internal static class JellyfinAutostart
{
    private static string ServiceKey(string service) => @"SYSTEM\CurrentControlSet\Services\" + service;

    /// <summary>The service's start mode and its restart after an error.</summary>
    public static JellyfinStartup.State Read(string service = JellyfinStartup.ServiceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey(service));
            if (key is null) return new(false, false, false, false);
            var start = key.GetValue("Start") is int value ? value : 3;
            var delayed = key.GetValue("DelayedAutostart") is int flag && flag == 1;
            var restarts = JellyfinStartup.RestartsAfterError(key.GetValue("FailureActions") as byte[], key.GetValue("FailureActionsOnNonCrashFailures") is int nonCrash ? nonCrash : 0);
            return new(true, start == 2, delayed, restarts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return new(false, false, false, false); }
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

    /// <summary>
    /// Mira's firewall rule, judged by its content (<see cref="JellyfinFirewall.MiraRule"/>) from the rules bearing its
    /// name, read through the firewall's COM interface without administrator rights. When that cannot be read, its name
    /// is all Mira has (<c>netsh … show rule</c>, whose text is in Windows' language), and a rule Mira wrote whole counts
    /// as ready: otherwise « Tout préparer » would ask for Windows' consent at every click.
    /// </summary>
    public static JellyfinFirewall.RuleState MiraRule(string? program, int port, string rule = JellyfinStartup.RuleName)
    {
        try
        {
            dynamic policy = Policy();
            var named = new List<JellyfinFirewall.Rule>();
            foreach (dynamic found in policy.Rules)
                if ((string?)found.Name == rule) named.Add(Describe((object)found));
            return JellyfinFirewall.MiraRule(named, program, port);
        }
        catch (Exception ex) when (IsFirewallError(ex))
        {
            return Execute(new("netsh.exe", $"advfirewall firewall show rule name=\"{rule}\"")) == 0 ? JellyfinFirewall.RuleState.Ready : JellyfinFirewall.RuleState.Missing;
        }
    }

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
            return process.ExitCode switch { 0 => AutostartResult.Done, 3 => AutostartResult.KeptPublic, _ => AutostartResult.Failed };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return AutostartResult.Refused; }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException) { return AutostartResult.Failed; }
    }

    /// <summary>
    /// The copy of Mira started with administrator rights by « Tout préparer »: <c>--jellyfin-startup &lt;port&gt;</c>.
    /// The firewall lets phones through, with or without Jellyfin's service; the service, when there is one, starts with
    /// Windows and again after an error. 0 once every step that matters succeeded, 1 otherwise, 2 for arguments it does
    /// not accept, 3 when all is done but Windows kept a network public (<see cref="AutostartResult.KeptPublic"/>).
    /// </summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var port) || port is < 1 or > 65535) return 2;
        var program = Program();
        var open = OpenFirewall(program, port);
        var boot = !Read().HasService || Apply(program, port);
        return open == AutostartResult.Failed || !boot ? 1 : open == AutostartResult.KeptPublic ? 3 : 0;
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
    /// network and, when Tailscale is connected here, from Tailscale (<see cref="JellyfinFirewall.Reach"/>). Read
    /// through its COM interface, which needs no administrator rights. Null when it cannot be read, or when this PC is
    /// on no network. <paramref name="rule"/>: Mira's, which never applies to a public network once replaced.
    /// </summary>
    public static JellyfinFirewall.Reach? Phones(string? program, int port, string rule = JellyfinStartup.RuleName)
    {
        if (program is null) return null;
        try
        {
            var (found, settings, active) = ReadFirewall(Policy(), program);
            var rules = found.Select(x => x.Rule).ToList();
            JellyfinFirewall.Reach? reach = null;
            JellyfinFirewall.Verdict? tailnet = null;
            string? kept = null, counted = null;
            foreach (var origin in Origins(active))
            {
                var verdict = JellyfinFirewall.Judge(rules, settings[origin.Profile], program, port, origin.Profile, origin.Adapter, origin.Home);
                if (origin.Home is null && (tailnet is null || verdict > tailnet)) tailnet = verdict;
                if (verdict == JellyfinFirewall.Verdict.Blocked)
                    kept ??= JellyfinFirewall.Blocking(rules, program, port, origin.Profile, origin.Adapter, origin.Home).FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Program))?.Name;
                if (CountedPublic(origin, rules, settings[origin.Profile], program, port, rule)) counted ??= origin.Adapter;
                if (reach is null || verdict > reach.Verdict) reach = new(verdict, origin.Profile);
            }
            return reach is null ? null : reach with { Tailnet = tailnet, Kept = kept, Public = counted };
        }
        catch (Exception ex) when (IsFirewallError(ex)) { return null; }
    }

    /// <summary>
    /// Lets phones through to <paramref name="program"/>. A network they come from that Windows counts as public (the
    /// home network, or Tailscale's), and that keeps them out, is made private: Mira's rule never applies to a public
    /// network, and a domain one is left as Windows set it. Then <paramref name="program"/>'s own block rules that keep
    /// them out (Windows' prompt makes one for each kind of network left unticked) stop applying to the home network's
    /// profile, unless public, and to Tailscale's (Private, which Tailscale sets, even before it is connected here), and
    /// are turned off when no other profile is left. Then Mira's rule (<see cref="JellyfinStartup.FirewallCommands"/>),
    /// which lets in only the home network and Tailscale, even when the rules cannot be read. A block rule for any
    /// program is someone's own and stays. Needs administrator rights. Done once the firewall, read again, is as open as
    /// Mira can make it; KeptPublic when Windows would not make a network private.
    /// </summary>
    public static AutostartResult OpenFirewall(string? program, int port, string rule = JellyfinStartup.RuleName)
    {
        if (program is null) return AutostartResult.Failed;
        try
        {
            var (rules, settings, active) = ReadFirewall(Policy(), program);
            var origins = Origins(active);
            var switched = false;
            foreach (var origin in origins.Where(o => CountedPublic(o, rules.Select(x => x.Rule), settings[o.Profile], program, port, rule)))
                switched |= SetNetworkCategory(origin.Adapter!, JellyfinFirewall.Private);
            // Windows applies the private profile at once: the profiles active here change with it.
            if (switched) { (rules, _, active) = ReadFirewall(Policy(), program); origins = Origins(active); }
            if (!origins.Any(x => x.Home is null)) origins.Add(new(JellyfinFirewall.Private, null, null));
            foreach (var (found, com) in rules.Where(x => !x.Rule.Allow && !string.IsNullOrWhiteSpace(x.Rule.Program)))
            {
                var lifted = origins.Where(x => x.Profile != JellyfinFirewall.Public && JellyfinFirewall.Applies(found, program, port, x.Profile, x.Adapter, x.Home))
                    .Aggregate(0, (bits, x) => bits | x.Profile);
                if (lifted == 0) continue;
                var others = JellyfinFirewall.WithoutProfile(found.Profiles, lifted);
                if (others != 0) com.Profiles = others; else com.Enabled = false;
            }
        }
        catch (Exception ex) when (IsFirewallError(ex)) { }
        foreach (var command in JellyfinStartup.FirewallCommands(program, port, rule))
            if (Execute(command) != 0 && !command.Optional) return AutostartResult.Failed;
        // Blocking everything, or a block rule naming no program, is the person's to undo: the guide says where. So is a
        // network Windows keeps public.
        var reach = Phones(program, port, rule);
        if (reach?.Public is not null) return AutostartResult.KeptPublic;
        return reach is null or { Verdict: not JellyfinFirewall.Verdict.Blocked } or { Kept: not null } ? AutostartResult.Done : AutostartResult.Failed;
    }
    /// <summary>
    /// A network phones come from that Windows says it counts as public, and that keeps them out but for Mira's own rule
    /// (<paramref name="rule"/>: an older version's applied to every network): Mira's never applies there once
    /// replaced, so « Tout préparer » makes it private (the home network is not a café's).
    /// </summary>
    private static bool CountedPublic(Origin origin, IEnumerable<JellyfinFirewall.Rule> rules, JellyfinFirewall.Profile settings, string program, int port, string rule) =>
        origin.Known && origin.Profile == JellyfinFirewall.Public && origin.Adapter is not null
        && JellyfinFirewall.Judge(rules.Where(x => x.Name != rule), settings, program, port, origin.Profile, origin.Adapter, origin.Home) != JellyfinFirewall.Verdict.Open;

    /// <summary>
    /// Where phones connect from: the home network (this PC's address there stands for it), or Tailscale's range (no
    /// address); <paramref name="Known"/>: on the profile Windows says, not one assumed.
    /// </summary>
    private sealed record Origin(int Profile, string? Adapter, string? Home, bool Known = false);
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
            if (categories.TryGetValue(home.Adapter, out var profile)) origins.Add(new(profile, home.Adapter, home.Address, Known: true));
            else
            {
                foreach (var bit in new[] { JellyfinFirewall.Domain, JellyfinFirewall.Private, JellyfinFirewall.Public }.Where(x => (active & x) != 0))
                    origins.Add(new(bit, home.Adapter, home.Address));
            }
        }
        if (TailnetAdapter() is { } tailnet)
            origins.Add(categories.TryGetValue(tailnet, out var profile) ? new(profile, tailnet, null, Known: true) : new(JellyfinFirewall.TailnetProfile(active), tailnet, null));
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
    /// Has Windows count a network adapter's network as private (or public: the tests put theirs back), as
    /// Set-NetConnectionProfile does: MSFT_NetConnectionProfile changed through WMI's scripting objects, which needs
    /// administrator rights. A domain network is never changed (Windows sets it from the domain), and Windows refuses a
    /// network it cannot identify. True once Windows counts it so.
    /// </summary>
    internal static bool SetNetworkCategory(string adapter, int profile)
    {
        if (profile is not (JellyfinFirewall.Private or JellyfinFirewall.Public)) throw new ArgumentOutOfRangeException(nameof(profile));
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic service = locator.ConnectServer(".", @"root\StandardCimv2");
            foreach (dynamic found in service.ExecQuery("SELECT * FROM MSFT_NetConnectionProfile"))
            {
                if (!string.Equals((string?)found.Properties_.Item("InterfaceAlias").Value, adapter, StringComparison.OrdinalIgnoreCase)) continue;
                // NetworkCategory: 0 public, 1 private, 2 domain.
                dynamic category = found.Properties_.Item("NetworkCategory");
                if (Convert.ToInt32((object)category.Value) == 2) return false;
                category.Value = profile == JellyfinFirewall.Private ? 1 : 0;
                // wbemChangeFlagUpdateOnly: the instance is changed, as Set-NetConnectionProfile does (ModifyInstance).
                found.Put_(1);
            }
        }
        catch (Exception ex) when (IsFirewallError(ex) || ex is FormatException or OverflowException) { }
        return NetworkCategories().TryGetValue(adapter, out var now) && now == profile;
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
            rules.Add((Describe((object)rule), rule));
        }
        return (rules, settings, active);
    }
    /// <summary>A rule of the firewall (INetFwRule) as <see cref="JellyfinFirewall"/> judges it.</summary>
    private static JellyfinFirewall.Rule Describe(object com)
    {
        dynamic rule = com;
        string? application = rule.ApplicationName;
        // A rule naming no program may be for one service only.
        string? service = string.IsNullOrWhiteSpace(application) ? rule.serviceName : null;
        object? interfaces = rule.Interfaces;
        // NET_FW_RULE_DIRECTION: 1 incoming; NET_FW_ACTION: 1 allow.
        return new((string?)rule.Name ?? "", (bool)rule.Enabled, (int)rule.Direction == 1, (int)rule.Action == 1, (int)rule.Profiles, application, (int)rule.Protocol,
            (string?)rule.LocalPorts, (string?)rule.RemoteAddresses, interfaces is object[] names ? names.OfType<string>().ToList() : null, (string?)rule.InterfaceTypes, service);
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
