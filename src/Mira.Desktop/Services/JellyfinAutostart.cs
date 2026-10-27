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
/// How setting Jellyfin up to answer from the PC's start went. KeptPublic: done, but Windows would not make the network
/// the person asked for private (« Passer en privé »), where Mira's rule never applies: theirs to change in Windows'
/// settings.
/// </summary>
internal enum AutostartResult { Done, Refused, Failed, KeptPublic }

/// <summary>
/// Jellyfin's service and the firewall, so that phones find Mira web, at home and on Tailscale, and as soon as the PC
/// is on (see <see cref="JellyfinStartup"/>). Read without administrator rights; set up by Windows PowerShell started
/// with them (<see cref="ApplyAsync"/>), which only carries out what Mira decided.
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
    /// The service's state as Windows reports it (QueryServiceStatus, which needs no administrator rights): None when
    /// there is no such service, Unknown when Windows does not say.
    /// </summary>
    public static LocalJellyfin.Service ServiceState(string service = JellyfinStartup.ServiceName)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return LocalJellyfin.Service.Unknown;
        try
        {
            var handle = OpenServiceW(manager, service, ServiceQueryStatus);
            if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? LocalJellyfin.Service.None : LocalJellyfin.Service.Unknown;
            try
            {
                if (!QueryServiceStatus(handle, out var status)) return LocalJellyfin.Service.Unknown;
                // SERVICE_STATUS.dwCurrentState: 1 stopped, 2 start pending, 3 stop pending, 4 running, 5 continue pending, 6 pause pending, 7 paused.
                return status.CurrentState switch
                {
                    1 or 7 => LocalJellyfin.Service.Stopped,
                    2 or 5 => LocalJellyfin.Service.Starting,
                    3 or 6 => LocalJellyfin.Service.Stopping,
                    4 => LocalJellyfin.Service.Running,
                    _ => LocalJellyfin.Service.Unknown,
                };
            }
            finally { CloseServiceHandle(handle); }
        }
        finally { CloseServiceHandle(manager); }
    }
    /// <summary>Whether a jellyfin.exe runs on this PC, in any account: what tells, without a service.</summary>
    public static bool ProgramRunning()
    {
        var found = Process.GetProcessesByName("jellyfin");
        foreach (var process in found) process.Dispose();
        return found.Length > 0;
    }
    /// <summary>
    /// « Démarrer Jellyfin » for its service: <c>sc start</c> through the same elevated script as « Tout préparer »,
    /// Windows asking for consent. Done once the script ran (a service already starting makes sc fail, which is no
    /// failure here): whether Jellyfin then answers is what counts.
    /// </summary>
    public static async Task<AutostartResult> StartServiceAsync(string service = JellyfinStartup.ServiceName)
    {
        var code = await ElevatedAsync(new([], [], [new("sc.exe", $"start {service}", Optional: true)]));
        return code is null ? AutostartResult.Refused : code == 0 ? AutostartResult.Done : AutostartResult.Failed;
    }
    /// <summary>
    /// « Démarrer Jellyfin » without a service: its program with its data folder, hidden, as its tray app starts it,
    /// in the person's own rights. False when Mira finds no program or it does not start.
    /// </summary>
    public static bool StartProgram()
    {
        if (Program() is not { } program) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, $"--datadir \"{JellyfinInstaller.DataFolder()}\"")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(program)! });
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return false; }
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

    /// <summary>
    /// Asks Windows for the administrator's consent, once, and sets it all up (see <see cref="Plan"/>): the firewall
    /// lets phones through, with or without Jellyfin's service; the service, when there is one, starts with Windows and
    /// again after an error. <paramref name="privateNetwork"/>: the person said the network Windows counts as public is
    /// home's, to make private (« Passer en privé »). What has administrator rights is Windows PowerShell, from System32,
    /// with a script built here from fixed code and values checked here (<see cref="JellyfinStartup.Script"/>): never a
    /// file of Mira's own folder, which Windows lets the person's programs change. What it did is read again here,
    /// without those rights.
    /// </summary>
    public static async Task<AutostartResult> ApplyAsync(int port, bool privateNetwork = false)
    {
        if (port is < 1 or > 65535) return AutostartResult.Failed;
        var (program, plan) = await Task.Run(() =>
        {
            var found = Program();
            try { return (found, found is null ? null : Plan(found, port, JellyfinStartup.RuleName, privateNetwork, Read())); }
            catch (ArgumentException) { return (found, (JellyfinStartup.Plan?)null); }
        });
        if (program is null || plan is null) return AutostartResult.Failed;
        var code = await ElevatedAsync(plan);
        if (code is not { } exit) return AutostartResult.Refused;
        return exit != 0 ? AutostartResult.Failed : await Task.Run(() => Outcome(program, port, JellyfinStartup.RuleName, privateNetwork));
    }

    /// <summary>
    /// « Ne plus ouvrir Jellyfin au réseau »: Mira's firewall rule removed, with Windows' consent. Block rules Mira lifted
    /// and the service's settings stay as they are. Done once the rule, read again, is gone.
    /// </summary>
    public static async Task<AutostartResult> CloseAsync(int port)
    {
        var code = await ElevatedAsync(new([], [], [JellyfinStartup.RemoveRule()]));
        if (code is null) return AutostartResult.Refused;
        return await Task.Run(() => MiraRule(Program(), port) == JellyfinFirewall.RuleState.Missing) ? AutostartResult.Done : AutostartResult.Failed;
    }

    /// <summary>
    /// What setting it all up does, decided here without administrator rights: when <paramref name="privateNetwork"/>,
    /// the networks phones come from that Windows counts as public and that keep them out are made private (a domain
    /// one is left as Windows set it); then <paramref name="program"/>'s own block rules that keep them out (Windows'
    /// prompt makes one for each kind of network left unticked) stop applying to the home network's profile, unless
    /// public, and to Tailscale's (Private, which Tailscale sets, even before it is connected here), and are turned off
    /// when no other profile is left; then Mira's rule (<see cref="JellyfinStartup.FirewallCommands"/>), which lets in
    /// only the home network and Tailscale, even when the rules cannot be read; then, with a service
    /// (<paramref name="service"/>), its start with Windows and its restarts, its delayed start kept. A block rule for
    /// any program is someone's own and stays. A network about to be made private counts as private for the rules.
    /// </summary>
    internal static JellyfinStartup.Plan Plan(string program, int port, string rule, bool privateNetwork, JellyfinStartup.State? service = null,
        string serviceName = JellyfinStartup.ServiceName, bool start = true)
    {
        var adapters = new List<string>();
        var lifts = new List<JellyfinStartup.Lift>();
        try
        {
            var (rules, settings, active) = ReadFirewall(Policy(), program);
            var origins = Origins(active);
            if (privateNetwork)
            {
                foreach (var origin in origins.Where(o => o.Adapter is not null && JellyfinFirewall.CountedPublic(o, rules.Select(x => x.Rule), settings[o.Profile], program, port, rule)))
                    if (!adapters.Contains(origin.Adapter!, StringComparer.OrdinalIgnoreCase)) adapters.Add(origin.Adapter!);
                // Windows applies the private profile at once: those networks' block rules are lifted for it.
                origins = origins.Select(o => o.Adapter is not null && adapters.Contains(o.Adapter, StringComparer.OrdinalIgnoreCase) ? o with { Profile = JellyfinFirewall.Private } : o).ToList();
            }
            if (!origins.Any(x => x.Home is null)) origins.Add(new(JellyfinFirewall.Private, null, null));
            foreach (var (found, _) in rules.Where(x => !x.Rule.Allow && !string.IsNullOrWhiteSpace(x.Rule.Program)))
            {
                var lifted = origins.Where(x => x.Profile != JellyfinFirewall.Public && JellyfinFirewall.Applies(found, program, port, x.Profile, x.Adapter, x.Home))
                    .Aggregate(0, (bits, x) => bits | x.Profile);
                if (lifted == 0) continue;
                lifts.Add(new(found.Name, found.Program!, found.Protocol, found.LocalPorts, found.RemoteAddresses, found.Profiles, JellyfinFirewall.WithoutProfile(found.Profiles, lifted)));
            }
        }
        catch (Exception ex) when (IsFirewallError(ex)) { }
        var commands = service is { HasService: true }
            ? JellyfinStartup.Commands(program, port, serviceName, rule, service.Delayed).Where(c => start || !c.Arguments.StartsWith("start ", StringComparison.Ordinal)).ToList()
            : JellyfinStartup.FirewallCommands(program, port, rule);
        return new(adapters, lifts, commands);
    }

    /// <summary>
    /// After the plan ran without a failed command: the firewall, read again, as open as Mira can make it. Blocking
    /// everything, or a block rule naming no program, is the person's to undo: the guide says where. So is a network left
    /// public: not said to be home's, Windows keeping it so, or one whose profile Windows does not say; KeptPublic when
    /// Windows would not make it private.
    /// </summary>
    private static AutostartResult Outcome(string program, int port, string rule, bool privateNetwork)
    {
        var reach = Phones(program, port, rule);
        if (reach?.Public is { } left) return privateNetwork && left.Known ? AutostartResult.KeptPublic : AutostartResult.Done;
        return reach is null or { Verdict: not JellyfinFirewall.Verdict.Blocked } or { Kept: not null } ? AutostartResult.Done : AutostartResult.Failed;
    }

    /// <summary>
    /// « Mettre à jour Jellyfin »: its installer, downloaded and checked (<paramref name="setup"/>), run by the same
    /// elevated script, which checks it again before running it; then the service as it was before
    /// (<see cref="JellyfinStartup.AfterUpdate"/>), the installer having made it again. One consent from Windows. The
    /// script's exit code (<see cref="JellyfinStartup.SetupMismatch"/>…), null when the person refused.
    /// </summary>
    public static Task<int?> UpdateAsync(string setup, string sha256, JellyfinStartup.State before) =>
        ElevatedAsync(new([], [], JellyfinStartup.AfterUpdate(before), new(setup, sha256)), JellyfinStartup.SetupWait + TimeSpan.FromMinutes(2));

    /// <summary>
    /// Runs <paramref name="plan"/> in Windows PowerShell with administrator rights, Windows asking for consent: its exit
    /// code, or null when the person refused. -1 when it could not start or did not end within <paramref name="limit"/>
    /// (two minutes by default).
    /// </summary>
    private static async Task<int?> ElevatedAsync(JellyfinStartup.Plan plan, TimeSpan? limit = null)
    {
        try
        {
            // runas rules out CREATE_NO_WINDOW: the console is started hidden instead.
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, JellyfinStartup.PowerShell), JellyfinStartup.PowerShellArguments(plan))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (process is null) return -1;
            await process.WaitForExitAsync().WaitAsync(limit ?? TimeSpan.FromMinutes(2));
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException or ArgumentException) { return -1; }
    }

    /// <summary>
    /// Runs <see cref="JellyfinStartup.Commands"/> through the same script, the service started last unless
    /// <paramref name="start"/> is false, in this process's own rights (the tests', an administrator's); a service and a
    /// rule of another name serve the tests.
    /// </summary>
    public static bool Apply(string? program, int port, string service = JellyfinStartup.ServiceName, string rule = JellyfinStartup.RuleName, bool start = true)
    {
        if (program is null) return false;
        var commands = JellyfinStartup.Commands(program, port, service, rule, Read(service).Delayed).Where(c => start || !c.Arguments.StartsWith("start ", StringComparison.Ordinal)).ToList();
        return RunScript(new([], [], commands)) == 0;
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
            return JellyfinFirewall.Phones(found.Select(x => x.Rule).ToList(), settings, Origins(active), program, port, rule);
        }
        catch (Exception ex) when (IsFirewallError(ex)) { return null; }
    }

    /// <summary>
    /// Lets phones through to <paramref name="program"/>, as « Tout préparer » does (<see cref="Plan"/>, the same
    /// script), in this process's own rights (the tests', an administrator's); with <paramref name="privateNetwork"/>, as
    /// « Passer en privé » does. Done once the firewall, read again, is as open as Mira can make it.
    /// </summary>
    public static AutostartResult OpenFirewall(string? program, int port, string rule = JellyfinStartup.RuleName, bool privateNetwork = false)
    {
        if (program is null) return AutostartResult.Failed;
        return RunScript(Plan(program, port, rule, privateNetwork)) != 0 ? AutostartResult.Failed : Outcome(program, port, rule, privateNetwork);
    }
    /// <summary>The script of <paramref name="plan"/> in Windows PowerShell, with this process's rights: its exit code.</summary>
    private static int RunScript(JellyfinStartup.Plan plan) => Execute(new(JellyfinStartup.PowerShell, JellyfinStartup.PowerShellArguments(plan)), TimeSpan.FromMinutes(2));

    /// <summary>
    /// The home network, on its adapter's profile, and Tailscale, when it is connected here, on its own, each with its
    /// network's name. A profile Windows does not say (<see cref="Networks"/>): for the home network, each one active
    /// here; for Tailscale, the private one it sets.
    /// </summary>
    private static List<JellyfinFirewall.Origin> Origins(int active)
    {
        var networks = Networks();
        var origins = new List<JellyfinFirewall.Origin>();
        if (LocalNetwork.ThisPcOnNetwork() is { } home)
        {
            if (networks.TryGetValue(home.Adapter, out var network)) origins.Add(new(network.Profile, home.Adapter, home.Address, Known: true, network.Name));
            else
            {
                foreach (var bit in new[] { JellyfinFirewall.Domain, JellyfinFirewall.Private, JellyfinFirewall.Public }.Where(x => (active & x) != 0))
                    origins.Add(new(bit, home.Adapter, home.Address));
            }
        }
        if (TailnetAdapter() is { } tailnet)
            origins.Add(networks.TryGetValue(tailnet, out var network) ? new(network.Profile, tailnet, null, Known: true, network.Name) : new(JellyfinFirewall.TailnetProfile(active), tailnet, null));
        return origins;
    }
    /// <summary>The profile Windows counts each network adapter under (« Wi-Fi » → Public…): <see cref="Networks"/>.</summary>
    internal static Dictionary<string, int> NetworkCategories() => Networks().ToDictionary(x => x.Key, x => x.Value.Profile, StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The profile Windows counts each network adapter under (« Wi-Fi » → Public…), and the network's name there
    /// (« Livebox-1234 »), as Get-NetConnectionProfile shows them: MSFT_NetConnectionProfile, read through WMI's
    /// scripting objects, which need no administrator rights. Empty when it cannot be read.
    /// </summary>
    private static Dictionary<string, (int Profile, string? Name)> Networks()
    {
        var found = new Dictionary<string, (int, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic service = locator.ConnectServer(".", @"root\StandardCimv2");
            foreach (dynamic profile in service.ExecQuery("SELECT InterfaceAlias, NetworkCategory, Name FROM MSFT_NetConnectionProfile"))
            {
                string? adapter = profile.Properties_.Item("InterfaceAlias").Value;
                // A name left empty (null through COM) must not drop the profile.
                var name = (object?)profile.Properties_.Item("Name").Value as string;
                // NetworkCategory: 0 public, 1 private, 2 domain.
                var category = Convert.ToInt32((object)profile.Properties_.Item("NetworkCategory").Value);
                if (!string.IsNullOrEmpty(adapter))
                    found[adapter] = (category switch { 1 => JellyfinFirewall.Private, 2 => JellyfinFirewall.Domain, _ => JellyfinFirewall.Public }, name);
            }
        }
        catch (Exception ex) when (IsFirewallError(ex) || ex is FormatException or OverflowException) { }
        return found;
    }
    /// <summary>
    /// Has Windows count a network adapter's network as private (or public: the tests put theirs back), as
    /// Set-NetConnectionProfile does: MSFT_NetConnectionProfile changed through WMI's scripting objects, which needs
    /// administrator rights. A domain network is never changed (Windows sets it from the domain). True once Windows
    /// counts it so.
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

    private const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004;
    private const int ErrorServiceDoesNotExist = 1060;
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenServiceW(IntPtr manager, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>A program of System32 (never one found on the PATH: this may run with administrator rights), hidden.</summary>
    private static int Execute(StartupCommand command, TimeSpan? timeout = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, command.File), command.Arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process is null) return -1;
            // Read to the end so that a full pipe never blocks the program.
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(30))) { try { process.Kill(); } catch (InvalidOperationException) { } return -1; }
            Task.WaitAll([output, errors], 5_000);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return -1; }
    }
}
