using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// A stand-in for mira-tailscale in the checks of TailnetProcess: this test program itself, started with --fake-tailnet,
/// speaks the helper's protocol (cmd/mira-tailscale) on its input and output, with a tailnet of two devices: the PC of
/// Jellyfin (100.64.0.2, online) and a NAS (100.64.0.3, offline). Options after --fake-tailnet:
///   --version &lt;x&gt;          the version it says it is (default « test »)
///   --log-upload           says it could upload logs to Tailscale
///   --crash-once &lt;file&gt;    ends right after answering start, the first time only (the file remembers)
///   --silent               ends before its first line, as when an antivirus stops it at once
///   --state &lt;state&gt;        the state its saved state gives at start: running (signed in before), expired, machine-auth
///   --login-waits          gives the login page but stays signed out, as while the person signs in in the browser
///   --key-expiry &lt;date&gt;    when its key expires once signed in
/// </summary>
internal static class FakeTailnet
{
    public const string Page = "https://login.tailscale.com/a/1a2b3c4d5e";
    public const string ProxyAddress = "127.0.0.1:54321";

    /// <summary>How to start it: this program, with --fake-tailnet and the options.</summary>
    public static (string Executable, IReadOnlyList<string> Arguments) Command(params string[] options)
    {
        var executable = Environment.ProcessPath!;
        IReadOnlyList<string> prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetEntryAssembly()!.Location] : [];
        return (executable, [.. prefix, "--fake-tailnet", .. options]);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string Option(string name, string fallback) => Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
        if (args.Contains("--silent")) return 5;
        var crashMarker = Option("--crash-once", "");
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        void Send(object message) => output.WriteLine(JsonSerializer.Serialize(message));
        Send(new { @event = "hello", protocol = 1, version = Option("--version", "test"), tailscale = "1.104.4", go = "go1.27.3", logUpload = args.Contains("--log-upload") });
        // What the real helper never writes, as its log lines hide login pages: written here so that Mira's own hiding shows.
        Console.Error.WriteLine("control: visit " + Page + " to log in");
        Console.Error.Flush();
        var expiry = Option("--key-expiry", "");
        var self = new { id = "n0", hostName = "mira-test", dnsName = "mira-test.tail1234.ts.net", ips = new[] { "100.64.0.9" }, online = true, keyExpiry = expiry.Length > 0 ? expiry : null };
        object[] peers =
        [
            new { id = "n1", hostName = "DESKTOP-JELLYFIN", dnsName = "desktop-jellyfin.tail1234.ts.net", ips = new[] { "100.64.0.2", "fd7a:115c:a1e0::2" }, os = "windows", online = true },
            new { id = "n2", hostName = "nas", dnsName = "nas.tail1234.ts.net", ips = new[] { "100.64.0.3" }, os = "linux", online = false, lastSeen = "2026-10-29T10:00:00Z" },
        ];
        var running = new { state = "running", self, user = "ogsas@example.com", tailnet = "ogsas@example.com", magicDNSSuffix = "tail1234.ts.net", peers };
        // The state its saved state gives at start (--state running: signed in before; expired, machine-auth).
        var first = Option("--state", "needs-login");
        while (await input.ReadLineAsync() is { } line)
        {
            var request = JsonNode.Parse(line)!.AsObject();
            var id = request["id"]?.GetValue<long>() ?? 0;
            switch ((string?)request["op"])
            {
                case "start":
                    var key = Convert.FromBase64String((string?)request["stateKey"] ?? "");
                    if (key.Length != 32 || string.IsNullOrEmpty((string?)request["proxyUser"]) || string.IsNullOrEmpty((string?)request["proxyPassword"]) || string.IsNullOrEmpty((string?)request["dir"]))
                    { Send(new { id, ok = false, code = "protocol", error = "start: missing key, folder or proxy credentials" }); break; }
                    Send(new { @event = "proxy", address = ProxyAddress });
                    Send(new { @event = "status", status = first == "running" ? running : (object)new { state = first, authURL = Page } });
                    Send(new { id, ok = true });
                    if (crashMarker.Length > 0 && !File.Exists(crashMarker)) { File.WriteAllText(crashMarker, "crashed"); return 3; }
                    break;
                case "login":
                    Send(new { id, ok = true, url = Page });
                    if (!args.Contains("--login-waits")) Send(new { @event = "status", status = running });
                    break;
                case "probe":
                    var address = (string?)request["address"];
                    if (address == "100.64.0.2:8096" || address == "desktop-jellyfin:8096") Send(new { id, ok = true });
                    else if (address == "100.64.0.3:8096") Send(new { id, ok = false, code = "peer-offline", error = "tailnet: peer-offline" });
                    else Send(new { id, ok = false, code = "refused", error = "tailnet: refused" });
                    break;
                case "logout":
                    Send(new { id, ok = true });
                    Send(new { @event = "status", status = new { state = "needs-login" } });
                    break;
                case "rebind": Send(new { id, ok = true }); break;
                case "quit": Send(new { id, ok = true }); return 0;
                default: Send(new { id, ok = false, code = "protocol", error = "unknown op" }); break;
            }
        }
        return 0;
    }
}
