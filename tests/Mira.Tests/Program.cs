using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Desktop.Services;
using Mira.Desktop.Playback;
using static TestSupport;

if (args.Contains("--prepare-ui"))
{
    var profile = new LocalProfile(Path.GetFullPath(".artifacts/mock-profile"));
    profile.SaveConnection(new("http://127.0.0.1:18096/", "mock-user", "Compte de test", "mira-test-token", profile.DeviceId));
    profile.SaveSettings(new() { Volume = 0, AutoNext = false });
    Console.WriteLine("Profil de test isolé créé."); return 0;
}
// Read-only probe of a real server through a validation profile's protected session: the library folders TorLink
// imports would use, and the lookup behind « Voir ». Only GET requests; nothing is reported to or changed on the server.
if (Array.IndexOf(args, "--jellyfin-live") is var liveArg and >= 0)
{
    if (liveArg + 1 >= args.Length || new LocalProfile(Path.GetFullPath(args[liveArg + 1])).LoadConnection() is not { } connection) { Console.WriteLine("Profil sans session protégée valide."); return 0; }
    using var client = new JellyfinClient(connection);
    var folders = await client.VirtualFoldersAsync();
    Console.WriteLine(folders is null ? "Dossiers refusés : compte non administrateur." : $"{folders.Count} bibliothèque(s) : " + string.Join(" · ", folders.Select(x => $"{x.Name} [{x.CollectionType}] {x.Locations.Length} dossier(s)")));
    var libraries = MediaLibraries.FromJellyfin(folders);
    string Describe(string? path) => path is null ? "non trouvé" : Directory.Exists(path) ? "dossier présent sur ce PC" : "dossier absent de ce PC";
    Console.WriteLine($"Films : {Describe(libraries.Movies)} · Séries : {Describe(libraries.Series)} · Animes : {Describe(libraries.Anime)}");
    var newest = libraries.Movies is { } films && Directory.Exists(films)
        ? Directory.EnumerateFiles(films, "*", SearchOption.AllDirectories).Where(ReleaseName.IsVideo).OrderByDescending(File.GetCreationTimeUtc).FirstOrDefault() : null;
    Console.WriteLine(newest is null ? "Aucune vidéo de film à rechercher." : (await client.FindIndexedAsync([newest])).Count > 0
        ? "La vidéo de film la plus récente est retrouvée dans Jellyfin par son chemin exact." : "La vidéo de film la plus récente n’est pas parmi les 100 derniers ajouts indexés.");
    return 0;
}
// Loopback stand-in for GitHub's release list, for tools/update-check.ps1: serves the files of one folder as release v<version>.
//   Mira.Tests --update-server <folder> <port> <version>     (stops after 15 minutes, or when killed)
if (Array.IndexOf(args, "--update-server") is var serverArg and >= 0)
{
    var folder = Path.GetFullPath(args[serverArg + 1]); var port = int.Parse(args[serverArg + 2]); var version = args[serverArg + 3];
    var origin = $"http://127.0.0.1:{port}";
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port); listener.Start();
    Console.WriteLine($"Flux de mises à jour sur {origin}/releases pour {folder}");
    using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    while (!lifetime.IsCancellationRequested)
    {
        System.Net.Sockets.TcpClient connection;
        try { connection = await listener.AcceptTcpClientAsync(lifetime.Token); } catch (OperationCanceledException) { break; }
        _ = Task.Run(async () =>
        {
            using var client = connection; using var stream = client.GetStream();
            var header = new StringBuilder(); var buffer = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n") && await stream.ReadAsync(buffer) == 1) header.Append((char)buffer[0]);
            var target = header.ToString().Split(' ').ElementAtOrDefault(1) ?? "/";
            byte[] body; var type = "application/octet-stream"; var status = "200 OK";
            if (target.StartsWith("/releases", StringComparison.Ordinal))
            {
                var assets = Directory.EnumerateFiles(folder).Select(f => new FileInfo(f))
                    .Select(f => new { name = f.Name, size = f.Length, browser_download_url = $"{origin}/files/{Uri.EscapeDataString(f.Name)}" }).ToArray();
                body = JsonSerializer.SerializeToUtf8Bytes(new[] { new { tag_name = "v" + version, draft = false, prerelease = true, html_url = $"{origin}/release", assets } });
                type = "application/json";
            }
            else if (target.StartsWith("/files/", StringComparison.Ordinal) && Uri.UnescapeDataString(target[7..]) is var name && !name.Contains('/') && !name.Contains('\\') && File.Exists(Path.Combine(folder, name)))
                body = await File.ReadAllBytesAsync(Path.Combine(folder, name));
            else { body = Encoding.UTF8.GetBytes("introuvable"); status = "404 Not Found"; type = "text/plain"; }
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            try { await stream.WriteAsync(head); await stream.WriteAsync(body); } catch (IOException) { }
            Console.WriteLine($"{status[..3]} {target}");
        });
    }
    listener.Stop();
    return 0;
}

// The checks: those free of WPF (tests/Mira.Core.Tests, also run on Linux), then those of the Windows app.
//   Mira.Tests [--filter <texte>] [--list] [--torlink-integration] [--mpv-integration]
var run = new TestRun(args);
var Test = run.Test; var testRoot = run.Root;
LibraryStore Store(string name) => new(testRoot, name);
await PortableTests.RunAsync(run);
await Test("Relance : une copie qui démarre ou se ferme le dit, plus de faux « Mira est déjà ouvert »", async () =>
{
    var key = "test-" + Guid.NewGuid().ToString("N");
    Assert(await Task.Run(() => InstanceActivation.ShowExisting(key)) == ActivationAnswer.None, "Nothing listening: no answer");
    var answer = ActivationAnswer.Starting; var asked = 0; string? given = "";
    using var activation = new InstanceActivation(key, link => { Interlocked.Increment(ref asked); given = link; return answer; });
    Assert(await Task.Run(() => InstanceActivation.ShowExisting(key)) == ActivationAnswer.Starting, "While it starts: « starting », the launch ends quietly");
    answer = ActivationAnswer.Closing;
    Assert(await Task.Run(() => InstanceActivation.ShowExisting(key)) == ActivationAnswer.Closing, "While it closes: « closing », the launch waits for it to end");
    answer = ActivationAnswer.Shown;
    Assert(await Task.Run(() => InstanceActivation.ShowExisting(key)) == ActivationAnswer.Shown && given is null, "Open: its window comes forward");
    // A mira:// link opened meanwhile goes to the Mira already open.
    const string link = "mira://ensemble/7464412130f54334a8a14638f0060cd6?server=http%3A%2F%2F100.101.102.103%3A8096";
    Assert(await Task.Run(() => InstanceActivation.ShowExisting(key, link)) == ActivationAnswer.Shown && given == link, "The link reaches it");
    Assert(Volatile.Read(ref asked) == 4, "Asked once per launch");
});
await Test("Préférences : migration des anciens réglages et persistance de l’apparence", () =>
{
    var old = JsonSerializer.Deserialize<PlayerSettings>("{\"Volume\":45}", Json.Options)!;
    Assert(old.RememberPosition && old.ShowProgress && old.HeroAutoPlay && !old.ReduceMotion && old.PosterDensity == "Comfortable", "Old settings lose defaults");
    var profile = new LocalProfile(Path.Combine(testRoot, "appearance"));
    profile.SaveSettings(new PlayerSettings { ShowProgress = false, ReduceMotion = true, HeroAutoPlay = false, PosterDensity = "Compact", RememberPosition = false, AudioLanguage = "eng,en" });
    var restored = profile.LoadSettings();
    Assert(!restored.ShowProgress && restored.ReduceMotion && !restored.HeroAutoPlay && restored.PosterDensity == "Compact" && !restored.RememberPosition && restored.AudioLanguage == "eng,en", "Preferences were not persisted");
    return Task.CompletedTask;
});
await Test("Moteur mpv : celui installé par Mira est trouvé, un chemin choisi passe avant, un mpv qui n’est pas x64 est ignoré", () =>
{
    // The headers of a PE file: DOS stub, signature, COFF header (machine), optional header (PE32 or PE32+).
    static byte[] Pe(ushort machine, bool plus = true)
    {
        var bytes = new byte[0x40 + 4 + 20 + (plus ? 240 : 224)];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);
        "PE\0\0"u8.ToArray().CopyTo(bytes, 0x40); BitConverter.GetBytes(machine).CopyTo(bytes, 0x44);
        BitConverter.GetBytes((ushort)(plus ? 240 : 224)).CopyTo(bytes, 0x54); BitConverter.GetBytes((ushort)0x2022).CopyTo(bytes, 0x56);
        BitConverter.GetBytes((ushort)(plus ? 0x20B : 0x10B)).CopyTo(bytes, 0x58); BitConverter.GetBytes(16).CopyTo(bytes, 0x58 + (plus ? 108 : 92));
        return bytes;
    }
    string Library(string name, byte[] bytes) { var path = Path.Combine(testRoot, name); File.WriteAllBytes(path, bytes); return path; }
    Assert(PortableExecutable.IsX64(Library("pe-x64.dll", Pe(0x8664))) && !PortableExecutable.IsX64(Library("pe-x86.dll", Pe(0x14C, plus: false)))
        && !PortableExecutable.IsX64(Library("pe-arm64.dll", Pe(0xAA64))) && !PortableExecutable.IsX64(Library("pe-text.dll", "x"u8.ToArray()))
        && !PortableExecutable.IsX64(Library("pe-cut.dll", Pe(0x8664)[..0x46])) && !PortableExecutable.IsX64(Path.Combine(testRoot, "pe-missing.dll")), "PE machine read wrong");
    // Real files of Windows: this x64 test, and a library of its 32-bit part.
    var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "kernel32.dll");
    Assert(PortableExecutable.IsX64(Environment.ProcessPath!) && (!File.Exists(wow) || !Environment.Is64BitOperatingSystem || !PortableExecutable.IsX64(wow)), "Real PE files read wrong");
    var saved = AppFiles.ProfileDirectory;
    try
    {
        AppFiles.ProfileDirectory = Path.Combine(testRoot, "engine-profile");
        var installed = MpvInstaller.LibraryPath(AppFiles.ProfileDirectory); Directory.CreateDirectory(Path.GetDirectoryName(installed)!); File.WriteAllBytes(installed, Pe(0x8664));
        var chosen = Path.Combine(testRoot, "engine-chosen.dll"); File.WriteAllText(chosen, "x");
        Assert(MpvEngine.FindLibrary() == installed && MpvEngine.FindLibrary(chosen) == chosen, "Engine lookup order");
        // A 32-bit mpv where Mira looks (Jellyfin MPV Shim's older builds): passed over, never loaded.
        File.WriteAllBytes(installed, Pe(0x14C, plus: false));
        Assert(MpvEngine.FindLibrary() != installed, "A 32-bit engine was chosen");
    }
    finally { AppFiles.ProfileDirectory = saved; }
    return Task.CompletedTask;
});
await Test("Serveur Jellyfin : son assistant de premier démarrage rempli en français, reprise possible avec le même compte", async () =>
{
    // Answers like Jellyfin 12.1 on its first run: "loading" at first, then the wizard's routes, open until the wizard is completed.
    var loading = 2; var completed = false; string? name = null, password = null, culture = null; bool? remote = null; var failLibrary = false; var signed = true; var requests = 0;
    var libraries = new List<(string Name, string Type, string Path)>(); var sagas = new List<string>();
    // Jellyfin reads JSON names in any case, as ASP.NET does: Mira's bodies go out in camelCase.
    static JsonElement Field(JsonElement? json, params string[] path) => path.Aggregate(json!.Value, (x, name) => x.EnumerateObject().First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value);
    var handler = new Handler(async request =>
    {
        Interlocked.Increment(ref requests);
        signed &= request.Headers.TryGetValues("Authorization", out var values) && values.Single().Contains("Client=\"Mira\"");
        var path = request.RequestUri!.AbsolutePath; JsonElement? body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync()).RootElement;
        var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
        if (path.StartsWith("/Startup/") && completed) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        switch (request.Method.Method + " " + path)
        {
            case "GET /System/Info/Public":
                return loading-- > 0 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Jellyfin Server is loading") }
                    : JsonResponse(new { Id = "server-1", Version = "12.1", StartupWizardCompleted = completed });
            case "GET /System/Ping": return new(HttpStatusCode.OK) { Content = new StringContent("\"Jellyfin Server\"") };
            case "POST /Startup/Configuration": culture = Field(body, "UICulture").GetString() + "/" + Field(body, "MetadataCountryCode").GetString(); return new(HttpStatusCode.NoContent);
            case "GET /Startup/User": return JsonResponse(new { Name = name ?? "jellyfin", Password = "" });
            case "POST /Startup/User":
                if (password is not null) return new(HttpStatusCode.Forbidden);
                if (Field(body, "Password").GetString() is not { Length: > 0 } chosen) return new(HttpStatusCode.BadRequest);
                name = Field(body, "Name").GetString(); password = chosen; return new(HttpStatusCode.NoContent);
            case "POST /Users/AuthenticateByName":
                return Field(body, "Username").GetString() == name && Field(body, "Pw").GetString() == password
                    ? JsonResponse(new { AccessToken = "token-1", User = new { Id = "user-1", Name = name } }) : new(HttpStatusCode.Unauthorized);
            case "GET /Library/VirtualFolders": return JsonResponse(libraries.Select(x => new { x.Name, CollectionType = x.Type, Locations = new[] { x.Path } }));
            case "POST /Library/VirtualFolders":
                if (failLibrary) return new(HttpStatusCode.InternalServerError);
                libraries.Add((query["name"], query["collectionType"], Field(Field(body, "LibraryOptions", "PathInfos")[0], "Path").GetString()!));
                if (Field(body, "LibraryOptions", "AutomaticallyAddToCollection").GetBoolean()) sagas.Add(query["name"]);
                return new(HttpStatusCode.NoContent);
            case "POST /Startup/RemoteAccess": remote = Field(body, "EnableRemoteAccess").GetBoolean(); return new(HttpStatusCode.NoContent);
            case "POST /Startup/Complete": completed = true; return new(HttpStatusCode.NoContent);
            default: return new(HttpStatusCode.NotFound);
        }
    });
    var server = new Uri("http://127.0.0.1:8096/");
    Assert(await JellyfinSetup.StateAsync(server, handler) is null, "A loading server was taken as ready");
    var state = await JellyfinSetup.WaitAsync(server, TimeSpan.FromSeconds(5), handler, TimeSpan.FromMilliseconds(10));
    Assert(state == new ServerState("12.1", false), "Server state: " + state);
    // A comma in the path: Jellyfin's query form would split it, the body keeps it whole.
    var folders = MediaFolders.Under(Path.Combine(testRoot, "Vidéos, à moi") + " ");
    // The service may read the whole root: never a drive or the user's own folder.
    var drive = Path.GetPathRoot(testRoot)!;
    Assert(MediaFolders.Refusal(folders.Root) is null && MediaFolders.Refusal(drive) is { } whole && whole.Contains("tout le disque")
        && MediaFolders.Refusal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar) is not null
        && MediaFolders.Refusal("Vidéos") is not null && MediaFolders.Refusal(" ") is not null, "Media root refusals");
    // Documents, the Desktop and a profile's own folder (another account's too) are never a media root; a folder in them is.
    var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    var profiles = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;
    Assert(MediaFolders.Refusal(documents) is not null && MediaFolders.Refusal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) is not null
        && MediaFolders.Refusal(profiles) is not null && MediaFolders.Refusal(Path.Combine(profiles, "Public")) is not null && MediaFolders.Refusal(Path.Combine(profiles, "Someone")) is not null
        && MediaFolders.Refusal(Path.Combine(documents, "Jellyfin")) is null && MediaFolders.Refusal(MediaFolders.DefaultRoot) is null, "Personal folders as media roots");
    folders.Create(); folders.AllowJellyfinService();
    Assert(new[] { folders.Movies, folders.Series, folders.Anime }.All(Directory.Exists) && Path.GetFileName(folders.Series) == "Séries" && !folders.Root.EndsWith(' '), "Media folders");
    await JellyfinSetup.ConfigureAsync(server, "Alice", "secret", folders, handler);
    (string, string, string)[] expected = [("Films", "movies", folders.Movies), ("Séries", "tvshows", folders.Series), ("Animes", "tvshows", folders.Anime)];
    Assert(completed && name == "Alice" && password == "secret" && culture == "fr/FR" && remote == false && signed, $"Wizard: {completed} {name} {culture} {remote} {signed}");
    Assert(libraries.SequenceEqual(expected), "Libraries: " + string.Join(", ", libraries));
    Assert(sagas.SequenceEqual(["Films"]), "Films gathered into sagas: " + string.Join(", ", sagas));
    // Cut off before its end: run again with the same account, the missing library is added once.
    completed = false; libraries.RemoveAt(2);
    await JellyfinSetup.ConfigureAsync(server, "Alice", "secret", folders, handler);
    Assert(completed && libraries.SequenceEqual(expected), "Resumed setup: " + string.Join(", ", libraries));
    async Task Refused(Func<Task> setup, string message)
    {
        try { await setup(); throw new Exception("Accepted: " + message); }
        catch (InstallException ex) { Assert(ex.Message.Contains(message), ex.Message); }
    }
    completed = false;
    await Refused(() => JellyfinSetup.ConfigureAsync(server, "Alice", "autre", folders, handler), "déjà un compte administrateur");
    libraries.Clear(); failLibrary = true;
    await Refused(() => JellyfinSetup.ConfigureAsync(server, "Alice", "secret", folders, handler), "la bibliothèque Films (code 500)");
    var sent = requests;
    foreach (var (user, secret) in new[] { (" Alice", "secret"), ("a/b", "secret"), ("Alice", "") })
    {
        try { await JellyfinSetup.ConfigureAsync(server, user, secret, folders, handler); throw new Exception("Accepted: " + user); }
        catch (ArgumentException) { }
    }
    Assert(requests == sent, "An invalid account reached the server");
    Assert(new[] { "Alice", "Jean-Luc O'Neil", "Élodie", "a.b@c+d" }.All(JellyfinSetup.IsValidUserName) && !new[] { "", " ", "..", "Alice ", "a\\b", "a<b" }.Any(JellyfinSetup.IsValidUserName), "User name rule");
    // Another program on that port, or nothing at all: not a Jellyfin, and the wait ends with a clear message.
    var page = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>box</html>") }));
    var nothing = new Handler(_ => throw new HttpRequestException("Connection refused"));
    Assert(await JellyfinSetup.StateAsync(server, page) is null && await JellyfinSetup.StateAsync(server, nothing) is null, "Not a Jellyfin, yet a state");
    // Told apart, so that Jellyfin is never installed over one that is starting, nor beside another program on its port.
    HttpMessageHandler Answer(HttpStatusCode status, string body) => new Handler(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
    var answers = new[]
    {
        (await JellyfinSetup.ProbeAsync(server, nothing)).Answer, (await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.ServiceUnavailable, "Jellyfin Server is loading"))).Answer,
        (await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.ServiceUnavailable, ""))).Answer, (await JellyfinSetup.ProbeAsync(server, page)).Answer,
        (await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.NotFound, ""))).Answer, (await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.OK, "{\"Name\":\"box\"}"))).Answer,
    };
    Assert(answers.SequenceEqual([ServerAnswer.Nothing, ServerAnswer.Loading, ServerAnswer.Loading, ServerAnswer.Other, ServerAnswer.Other, ServerAnswer.Other]), "Probe: " + string.Join(", ", answers));
    // Jellyfin 12's startup page, seen on a real 12.1: this route in camelCase, "wizard not completed" even when it is, and 503 everywhere else.
    var startupPage = new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/System/Info/Public"
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"localAddress\":\"http://127.0.0.1:8096\",\"version\":\"12.1.0\",\"productName\":\"Jellyfin Server\",\"id\":\"a\",\"startupWizardCompleted\":false}") }
        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Jellyfin Server is loading. Please try again shortly.") }));
    Assert(await JellyfinSetup.ProbeAsync(server, startupPage) is { Answer: ServerAnswer.Loading, State: null }, "Jellyfin's startup page taken for the server");
    Assert(await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.OK, "{\"Id\":\"a\",\"Version\":\"12.1.0\",\"StartupWizardCompleted\":true}")) is { Answer: ServerAnswer.Ready, State: { Version: "12.1.0", WizardCompleted: true } }, "Ready probe");
    using (var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
    {
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert(JellyfinSetup.PortInUse(port), "A listening port seen as free");
        listener.Stop(); Assert(!JellyfinSetup.PortInUse(port), "A closed port seen as taken");
    }
    await Refused(() => JellyfinSetup.WaitAsync(server, TimeSpan.FromMilliseconds(50), nothing, TimeSpan.FromMilliseconds(10)), "Jellyfin ne répond pas");
    var package = JellyfinServerPackage.Current;
    Assert(package.Url.Scheme == "https" && package.Url.Host == "repo.jellyfin.org" && package.FileName.EndsWith(".exe") && package.Sha256.Length == 64, "Pinned Jellyfin installer");
});
await Test("Démarrage avec Windows : service, relance après une erreur et pare-feu, lus puis réglés", () =>
{
    // SERVICE_FAILURE_ACTIONS as Windows stores it: reset period, two unused fields, the count, an unused field, then each type and delay.
    static byte[] Actions(params (int Type, int Delay)[] actions)
    {
        var bytes = new List<byte>();
        foreach (var value in new[] { 86400, 0, 0, actions.Length, 0 }) bytes.AddRange(BitConverter.GetBytes(value));
        foreach (var (type, delay) in actions) { bytes.AddRange(BitConverter.GetBytes(type)); bytes.AddRange(BitConverter.GetBytes(delay)); }
        return bytes.ToArray();
    }
    Assert(JellyfinStartup.RestartsAfterError(Actions((1, 15000), (1, 30000)), 1), "Restart actions not read");
    Assert(!JellyfinStartup.RestartsAfterError(Actions((1, 15000)), 0), "Without the non-crash flag, NSSM's stop on an error restarts nothing");
    Assert(!JellyfinStartup.RestartsAfterError(Actions((0, 0)), 1) && !JellyfinStartup.RestartsAfterError(null, 1) && !JellyfinStartup.RestartsAfterError(new byte[8], 1), "No action, no value, a short value");
    var delayed = new JellyfinStartup.State(true, true, true, false);
    Assert(!delayed.StartsAtBoot && !(delayed with { Delayed = false }).StartsAtBoot && (delayed with { Delayed = false, RestartsAfterError = true }).StartsAtBoot
        && (delayed with { RestartsAfterError = true }).StartsAtBoot && !(delayed with { StartsWithWindows = false, RestartsAfterError = true }).StartsAtBoot, "Delayed start kept, no restart");
    Assert(!new JellyfinStartup.State(false, true, false, true).StartsAtBoot, "Without a service, nothing starts with Windows");
    var commands = JellyfinStartup.Commands(@"C:\Program Files\Jellyfin\Server\jellyfin.exe", 8096);
    var rules = commands.Where(c => c.Arguments.Contains("add rule")).Select(c => c.Arguments).ToList();
    Assert(rules.Count == 3 && rules.All(r => r.Contains("program=\"C:\\Program Files\\Jellyfin\\Server\\jellyfin.exe\"") && r.Contains("dir=in"))
        && rules.Take(2).All(r => r.Contains("localport=8096") && r.Contains("protocol=TCP")), "Rules for Jellyfin's program and port only");
    Assert(rules[0].Contains("remoteip=localsubnet") && rules[1].Contains("remoteip=" + LocalNetwork.TailnetRange), "Home network and Tailscale only");
    Assert(rules[2].Contains("protocol=UDP") && rules[2].Contains($"localport={ServerSearch.Port} ") && rules[2].Contains("remoteip=localsubnet"), "Jellyfin's discovery, from the home network only");
    Assert(rules.All(r => r.Contains(" profile=domain,private ")) && JellyfinStartup.Profiles(JellyfinFirewall.AllProfiles) == "any"
        && JellyfinStartup.Profiles(JellyfinFirewall.Public) == "public", "Private and domain networks only, as netsh writes them");
    Assert(commands[0].Arguments == "config JellyfinServer start= auto" && commands.Any(c => c.Arguments == $"failure JellyfinServer reset= 86400 actions= {JellyfinStartup.RestartActions}")
        && commands.Any(c => c.Arguments == "failureflag JellyfinServer 1") && commands.All(c => c.File is "sc.exe" or "netsh.exe"), "Service set to start with Windows and to restart after an error");
    // Three restarts, then none (Windows repeats the last action); a delayed start stays delayed.
    Assert(JellyfinStartup.RestartActions.Split('/') is ["restart", _, "restart", _, "restart", _, "", "0"]
        && JellyfinStartup.Commands(@"C:\Program Files\Jellyfin\Server\jellyfin.exe", 8096, delayed: true)[0].Arguments == "config JellyfinServer start= delayed-auto", "Restarts end, delayed start kept");
    // What has administrator rights: Windows PowerShell from System32, its script fixed, the plan's text in base64 only.
    var plan = new JellyfinStartup.Plan(["Wi-Fi 'maison' $(calc)"], [new("jellyfin.exe", @"C:\Program Files\Jellyfin\Server\jellyfin.exe", 6, "*", "*", 2, 0)], commands);
    var arguments = JellyfinStartup.PowerShellArguments(plan);
    Assert(arguments.StartsWith("-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ", StringComparison.Ordinal) && JellyfinStartup.PowerShell == @"WindowsPowerShell\v1.0\powershell.exe"
        && Encoding.Unicode.GetString(Convert.FromBase64String(arguments.Split(' ')[^1])) == JellyfinStartup.Script(plan), "PowerShell's arguments");
    var script = JellyfinStartup.Script(plan);
    Assert(!script.Contains("maison") && !script.Contains("calc") && !script.Contains("Program Files") && !script.Contains("advfirewall") && script.Contains("$PSModuleAutoLoadingPreference = 'None'")
        && script.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(commands[0].Arguments))), "The plan's text in the script itself");
    foreach (var bad in new JellyfinStartup.Plan[] { new([], [], [new("cmd.exe", "/c calc")]), new([""], [], []), new([], [plan.Lifts[0] with { Keep = 8 }], []) })
    {
        var refused = false;
        try { JellyfinStartup.Script(bad); } catch (ArgumentException) { refused = true; }
        Assert(refused, "A plan Mira never builds accepted");
    }
    foreach (var (program, port) in new[] { ("jellyfin.exe", 8096), (@"C:\a\jellyfin.exe", 0), (@"C:\a\b"" & calc.exe", 8096) })
    {
        var refused = false;
        try { JellyfinStartup.Commands(program, port); } catch (ArgumentException) { refused = true; }
        Assert(refused, $"Accepted: {program} {port}");
    }
    // GitHub's Windows runners are administrators without a consent prompt: the real sc and netsh, on a service of the test's own.
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
    {
        const string service = "MiraStartupCheck", rule = "Mira - startup check";
        var program = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert(JellyfinAutostart.RunSystem("sc.exe", $"create {service} binPath= \"{program}\" start= delayed-auto") == 0, "Test service not created");
        try
        {
            var before = JellyfinAutostart.Read(service);
            Assert(before.HasService && before.StartsWithWindows && before.Delayed && !before.RestartsAfterError && JellyfinAutostart.MiraRule(program, 18096, rule) == JellyfinFirewall.RuleState.Missing, $"Before: {before}");
            Assert(JellyfinAutostart.Apply(program, 18096, service, rule, start: false), "sc or netsh failed");
            var after = JellyfinAutostart.Read(service);
            Assert(after.StartsAtBoot && after.Delayed && JellyfinAutostart.MiraRule(program, 18096, rule) == JellyfinFirewall.RuleState.Ready, $"After: {after}");
            // As sc wrote them: three restarts, then a fourth action that does nothing.
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}"))
            {
                var actions = key?.GetValue("FailureActions") as byte[];
                Assert(actions is { Length: >= 52 } && BitConverter.ToInt32(actions, 12) == 4 && BitConverter.ToInt32(actions, 20 + 8 * 2) == 1 && BitConverter.ToInt32(actions, 20 + 8 * 3) == 0,
                    "Failure actions: " + (actions is null ? "none" : Convert.ToHexString(actions)));
            }
            // Read back by its content: on another port, or for another program, it is not ready, and is replaced whole.
            Assert(JellyfinAutostart.MiraRule(program, 18098, rule) == JellyfinFirewall.RuleState.Stale
                && JellyfinAutostart.MiraRule(Path.Combine(Environment.SystemDirectory, "notepad.exe"), 18096, rule) == JellyfinFirewall.RuleState.Stale, "Judged by its name only");
            Assert(JellyfinAutostart.Apply(program, 18098, service, rule, start: false) && JellyfinAutostart.MiraRule(program, 18098, rule) == JellyfinFirewall.RuleState.Ready
                && JellyfinAutostart.MiraRule(program, 18096, rule) == JellyfinFirewall.RuleState.Stale, "Not replaced on another port");
            Assert(JellyfinAutostart.Apply(program, 18098, service, rule, start: false) && JellyfinAutostart.MiraRule(program, 18098, rule) == JellyfinFirewall.RuleState.Ready, "Not set up again over itself");
        }
        finally { JellyfinAutostart.RunSystem("sc.exe", $"delete {service}"); JellyfinAutostart.RemoveRule(rule); }
        Assert(!JellyfinAutostart.Read(service).HasService && JellyfinAutostart.RunSystem("netsh.exe", $"advfirewall firewall show rule name=\"{rule}\"") != 0
            && JellyfinAutostart.MiraRule(program, 18098, rule) == JellyfinFirewall.RuleState.Missing, "Test service or rule left behind");
        Console.WriteLine("      (sc et netsh réels par Windows PowerShell, service de test créé puis supprimé)");
    }
    return Task.CompletedTask;
});
await Test("Mira sur ton téléphone : le pare-feu de Windows laisse passer la maison et Tailscale, ouvert par Mira avec ou sans service", () =>
{
    const string jellyfin = @"C:\Program Files\Jellyfin\Server\jellyfin.exe";
    const int all = 0x7FFFFFFF, priv = JellyfinFirewall.Private, pub = JellyfinFirewall.Public;
    static JellyfinFirewall.Rule Rule(string name, bool allow, int profiles, string? program = jellyfin, int protocol = JellyfinFirewall.Tcp, string? ports = "*", string? remote = "*",
        bool enabled = true, bool inbound = true, IReadOnlyList<string>? interfaces = null, string types = "All") => new(name, enabled, inbound, allow, profiles, program, protocol, ports, remote, interfaces, types);
    JellyfinFirewall.Verdict Judge(IEnumerable<JellyfinFirewall.Rule> rules, int profile = priv, JellyfinFirewall.Profile? settings = null) =>
        JellyfinFirewall.Judge(rules, settings ?? new(true, false, false), jellyfin, 8096, profile, "Tailscale");
    const JellyfinFirewall.Verdict open = JellyfinFirewall.Verdict.Open, blocked = JellyfinFirewall.Verdict.Blocked;

    // Windows' prompt at Jellyfin's first start, the box counted as public: allowed there, blocked on private networks,
    // which Tailscale's is. The home address works, the Tailscale one shows nothing.
    var prompt = new[] { Rule("jellyfin.exe", true, pub), Rule("jellyfin.exe", true, pub, protocol: 17), Rule("jellyfin.exe", false, priv), Rule("jellyfin.exe", false, priv, protocol: 17) };
    Assert(Judge(prompt) == blocked && Judge(prompt, pub) == open, "The prompt's rules, judged wrong");
    Assert(JellyfinFirewall.Blocking(prompt, jellyfin, 8096, priv) is [{ Protocol: JellyfinFirewall.Tcp, Profiles: priv }], "Only the TCP block rule on private networks stands in the way");
    Assert(JellyfinFirewall.WithoutProfile(priv, priv) == 0 && JellyfinFirewall.WithoutProfile(all, priv) == (JellyfinFirewall.Domain | pub), "A rule for every network keeps blocking the others");
    // What « Tout préparer » leaves: that block rule off (private was its only profile), and Mira's rule, on private
    // and domain networks only.
    var mira = new[] { Rule("Mira - Jellyfin", true, JellyfinStartup.RuleProfiles, ports: "8096", remote: "LocalSubnet"),
        Rule("Mira - Jellyfin", true, JellyfinStartup.RuleProfiles, ports: "8096", remote: "100.64.0.0/255.192.0.0") };
    var opened = prompt.Where(x => x.Allow || x.Protocol != JellyfinFirewall.Tcp).Concat(mira).ToList();
    Assert(Judge(opened) == open && Judge(opened, pub) == open, "Still blocked once opened");
    // The box counted as private: the prompt's rules already let Tailscale in. A service has no rule until Mira's.
    Assert(Judge([Rule("jellyfin.exe", true, priv), Rule("jellyfin.exe", false, pub)]) == open, "The prompt's private rule lets Tailscale in");
    Assert(Judge([]) == blocked && Judge(mira) == open && Judge([mira[0]]) == blocked && Judge(mira, pub) == blocked, "Without rules, with the home network's only, or Tailscale counted public");
    // Windows' own apps allow everything to their package, naming no program and no port: not to Jellyfin. A port rule does.
    Assert(Judge([Rule("@{Microsoft.SecHealthUI}", true, all, program: null, protocol: 256, ports: ""), Rule("svc", true, all, program: null, ports: "8096") with { Service = "*" }]) == blocked
        && Judge([Rule("port 8096", true, all, program: null, ports: "8096")]) == open, "A packaged app's or a service's rule lets Jellyfin in, or a port rule does not");
    Assert(Judge([], settings: new(false, false, false)) == open && Judge([], settings: new(true, true, false)) == open
        && Judge(mira, settings: new(true, false, true)) == JellyfinFirewall.Verdict.Closed, "Firewall off, allowing by default, or blocking everything");
    // Rules that do not concern a TCP connection from Tailscale to Jellyfin's port, and rules that do.
    var unrelated = new[] { Rule("other", false, all, program: @"C:\Other\jellyfin2.exe"), Rule("out", false, all, inbound: false), Rule("off", false, all, enabled: false), Rule("udp", false, all, protocol: 17),
        Rule("port", false, all, ports: "8920"), Rule("phone", false, all, remote: "100.101.102.103"), Rule("half", false, all, remote: "100.64.0.0/11"), Rule("named", false, all, remote: "LocalSubnet"),
        Rule("wifi", false, all, interfaces: ["Wi-Fi"]), Rule("lan", false, all, types: "Lan"), Rule("public", false, pub), Rule("rpc", false, all, ports: "RPC"),
        Rule("app", false, all, program: null, protocol: 256, ports: ""), Rule("any port", false, all, program: null), Rule("dns", false, all, program: null, ports: "8096") with { Service = "Dnscache" } };
    Assert(Judge(mira.Concat(unrelated)) == open, "An unrelated rule seen as blocking");
    foreach (var rule in new[] { Rule("any program", false, all, program: null, ports: "8096"), Rule("list", false, all, ports: "80, 8096"), Rule("range", false, all, ports: "8000-9000"), Rule("any protocol", false, all, protocol: 256),
        Rule("wider", false, all, remote: "100.0.0.0/8"), Rule("prefix", false, all, remote: "100.64.0.0/10"), Rule("span", false, all, remote: "10.0.0.0, 100.0.0.0-100.200.0.0"), Rule("adapter", false, all, interfaces: ["tailscale"]),
        Rule("case", false, all, program: @"c:\program files\jellyfin\server\JELLYFIN.EXE"), Rule("empty", false, all, ports: null, remote: "") })
        Assert(Judge(mira.Append(rule)) == blocked, "Not seen as blocking: " + rule.Name);
    // The home network: the same rules, judged for a phone next to this PC (192.168.1.20) on the Wi-Fi's profile.
    const string pc = "192.168.1.20";
    JellyfinFirewall.Verdict Home(IEnumerable<JellyfinFirewall.Rule> rules, int profile = pub) => JellyfinFirewall.Judge(rules, new(true, false, false), jellyfin, 8096, profile, "Wi-Fi", pc);
    Assert(Home(prompt) == open && Home(prompt, priv) == blocked, "The prompt's rules, judged wrong for the home network");
    // Private ticked at the prompt, the box counted as public: Tailscale gets in, the phone at home does not. Closing the
    // prompt leaves no rule at all (seen on a PC where the address showed nothing at home or away).
    var privateOnly = new[] { Rule("jellyfin.exe", true, priv), Rule("jellyfin.exe", false, pub) };
    Assert(Judge(privateOnly) == open && Home(privateOnly) == blocked && Home([]) == blocked && Judge([]) == blocked, "Ticked private, or no answer to the prompt");
    // Mira's rule lets the home network in once it is private (or a domain's), never on a public one: there, LocalSubnet
    // is a hotspot's, and « Tout préparer » makes the box's network private instead.
    Assert(Home(mira, priv) == open && Home(mira, JellyfinFirewall.Domain) == open && Home(mira) == blocked && Home([mira[1]], priv) == blocked
        && Home(prompt.Concat(mira)) == open, "Mira's rule for the home network");
    Assert(JellyfinFirewall.Blocking(privateOnly, jellyfin, 8096, pub, "Wi-Fi", pc) is [{ Allow: false, Profiles: pub }], "The public block rule stands in the way at home");
    foreach (var remote in new[] { "*", "", "LocalSubnet", "localsubnet", "10.0.0.0/8, LocalSubnet", "192.168.1.0/255.255.255.0", "192.168.1.0/24", "192.168.0.0-192.168.255.255" })
        Assert(JellyfinFirewall.CoversHome(remote, pc), "Not the home network: " + remote);
    foreach (var remote in new[] { "192.168.2.0/24", "192.168.1.30", "192.168.1.20", "192.168.1.20/255.255.255.255", "100.64.0.0/255.192.0.0", "Internet", "DNS" })
        Assert(!JellyfinFirewall.CoversHome(remote, pc), "Seen as the home network: " + remote);
    Assert(JellyfinFirewall.WithoutProfile(all, priv | pub) == JellyfinFirewall.Domain, "Both the home network's profile and Tailscale's lifted");
    // What phones meet from the home network and Tailscale, each on its own profile. A public network is named, with its
    // name, when it keeps them out but for Mira's rule: only the person may have it made private (a café's is public too).
    // A profile Windows does not say, assumed public, only when it alone keeps them out: Mira's rule is not what is missing.
    var profiles = new Dictionary<int, JellyfinFirewall.Profile> { [JellyfinFirewall.Domain] = new(true, false, false), [priv] = new(true, false, false), [pub] = new(true, false, false) };
    JellyfinFirewall.Reach? Phones(IEnumerable<JellyfinFirewall.Rule> rules, params JellyfinFirewall.Origin[] origins) =>
        JellyfinFirewall.Phones(rules.ToList(), profiles, origins, jellyfin, 8096, "Mira - Jellyfin");
    var box = new JellyfinFirewall.Origin(pub, "Wi-Fi", pc, Known: true, "Livebox-1234");
    var tailscale = new JellyfinFirewall.Origin(priv, "Tailscale", null, Known: true, "example.ts.net");
    Assert(Phones(mira, box, tailscale) is { Verdict: blocked, Profile: pub, Tailnet: open, Kept: null, Public: { Adapter: "Wi-Fi", Name: "Livebox-1234", Known: true } }
        && Phones(mira, box with { Profile = priv }, tailscale) is { Verdict: open, Public: null } && Phones(mira) is null, "The box counted as public, then private");
    Assert(Phones(mira.Select(x => x with { Profiles = all }), box, tailscale) is { Verdict: open, Public.Adapter: "Wi-Fi" }, "An older version's rule hides the public box");
    Assert(Phones(prompt.Where(x => x.Allow).Concat(mira), box, tailscale) is { Verdict: open, Public: null }, "Allowed on public networks by Windows' prompt: nothing to switch");
    Assert(Phones(mira.Append(Rule("Bloquer 8096", false, all, program: null, ports: "8096")), box with { Profile = priv }, tailscale) is { Verdict: blocked, Kept: "Bloquer 8096", Public: null },
        "A block rule of someone's own");
    var assumed = new[] { new JellyfinFirewall.Origin(priv, "Wi-Fi", pc), new JellyfinFirewall.Origin(pub, "Wi-Fi", pc), tailscale };
    Assert(Phones(mira, assumed) is { Verdict: blocked, Public: { Adapter: "Wi-Fi", Name: null, Known: false } } && Phones([], assumed) is { Verdict: blocked, Public: null }
        && Phones(mira, [assumed[0], tailscale]) is { Verdict: open, Public: null }, "A profile Windows does not say");
    Assert(JellyfinFirewall.TailnetProfile(priv | pub) == priv && JellyfinFirewall.TailnetProfile(pub) == pub && JellyfinFirewall.TailnetProfile(JellyfinFirewall.Domain) == JellyfinFirewall.Domain
        && JellyfinFirewall.TailnetProfile(0) == priv, "Tailscale's profile");
    // The same rule with the service (« Disponible dès l'allumage du PC ») and without it.
    var firewall = JellyfinStartup.FirewallCommands(jellyfin, 8096);
    Assert(firewall.Count == 4 && firewall[0].Optional && JellyfinStartup.Commands(jellyfin, 8096).Where(c => c.File == "netsh.exe").SequenceEqual(firewall), "Mira's rule differs without the service");
    // Mira's rule judged by its content, as the firewall reads back what netsh wrote: ready, missing, or not as Mira
    // writes it (« Tout préparer » replaces it). Without Jellyfin's program, the rest is judged.
    const JellyfinFirewall.RuleState ready = JellyfinFirewall.RuleState.Ready, stale = JellyfinFirewall.RuleState.Stale;
    var written = new[] { Rule("Mira - Jellyfin", true, JellyfinStartup.RuleProfiles, ports: "8096", remote: "LocalSubnet"),
        Rule("Mira - Jellyfin", true, JellyfinStartup.RuleProfiles, ports: "8096", remote: "100.64.0.0/255.192.0.0"),
        Rule("Mira - Jellyfin", true, JellyfinStartup.RuleProfiles, protocol: JellyfinFirewall.Udp, ports: "7359", remote: "LocalSubnet") };
    Assert(JellyfinFirewall.MiraRule(written, jellyfin, 8096) == ready && JellyfinFirewall.MiraRule(written, null, 8096) == ready
        && JellyfinFirewall.MiraRule([], jellyfin, 8096) == JellyfinFirewall.RuleState.Missing, "Mira's rule as written");
    Assert(JellyfinFirewall.MiraRule(written, @"C:\Other\jellyfin.exe", 8096) == stale && JellyfinFirewall.MiraRule(written, jellyfin, 8097) == stale
        && JellyfinFirewall.MiraRule(written, null, 8097) == stale, "Left on another program or port");
    var changes = new (string Name, Func<JellyfinFirewall.Rule, JellyfinFirewall.Rule> Change)[]
    {
        ("off", x => x with { Enabled = false }), ("outgoing", x => x with { Inbound = false }), ("block", x => x with { Allow = false }), ("udp", x => x with { Protocol = 17 }),
        ("any protocol", x => x with { Protocol = JellyfinFirewall.AnyProtocol }), ("two ports", x => x with { LocalPorts = "8096,8920" }), ("any port", x => x with { LocalPorts = "*" }),
        ("anywhere", x => x with { RemoteAddresses = "*" }), ("wider", x => x with { RemoteAddresses = "LocalSubnet,100.0.0.0/8" }), ("no program", x => x with { Program = null }),
        ("one adapter", x => x with { Interfaces = ["Wi-Fi"] }), ("cable only", x => x with { InterfaceTypes = "Lan" }), ("domain only", x => x with { Profiles = JellyfinFirewall.Domain }),
        ("public too", x => x with { Profiles = all }),
    };
    foreach (var (name, change) in changes)
        Assert(JellyfinFirewall.MiraRule([change(written[0]), written[1], written[2]], jellyfin, 8096) == stale, "Seen as ready: " + name);
    // The discovery: UDP, its own port alone, from the home network alone (Tailscale carries no broadcast). An older
    // version's rule, without it, is replaced by one « Tout préparer ».
    var discovery = new (string Name, Func<JellyfinFirewall.Rule, JellyfinFirewall.Rule> Change)[]
    {
        ("tcp", x => x with { Protocol = JellyfinFirewall.Tcp }), ("any protocol", x => x with { Protocol = JellyfinFirewall.AnyProtocol }), ("Jellyfin's port", x => x with { LocalPorts = "8096" }),
        ("any port", x => x with { LocalPorts = "*" }), ("Tailscale", x => x with { RemoteAddresses = "100.64.0.0/255.192.0.0" }), ("anywhere", x => x with { RemoteAddresses = "*" }),
        ("public too", x => x with { Profiles = all }), ("other program", x => x with { Program = @"C:\Other\jellyfin.exe" }),
    };
    foreach (var (name, change) in discovery)
        Assert(JellyfinFirewall.MiraRule([written[0], written[1], change(written[2])], jellyfin, 8096) == stale, "Discovery seen as ready: " + name);
    Assert(JellyfinFirewall.MiraRule(written.Take(2), jellyfin, 8096) == stale && JellyfinFirewall.MiraRule(written.Take(2), null, 8096) == stale, "An older version's rule, without the discovery, seen as ready");
    Assert(JellyfinFirewall.MiraRule([written[0]], jellyfin, 8096) == stale && JellyfinFirewall.MiraRule(written.Append(written[0] with { LocalPorts = "8920" }), jellyfin, 8096) == stale,
        "One range only, or a rule left behind on another port");
    // An older version's rule, on every network (netsh's profile=any): not ready, so one « Tout préparer » narrows it.
    Assert(JellyfinFirewall.MiraRule(written.Select(x => x with { Profiles = all }), jellyfin, 8096) == stale, "An older version's rule seen as ready");
    Assert(JellyfinFirewall.IsTailnetRange("100.64.0.0/10") && JellyfinFirewall.IsTailnetRange("100.64.0.0/255.192.0.0") && !JellyfinFirewall.IsTailnetRange("100.64.0.0/11")
        && !JellyfinFirewall.IsTailnetRange("100.0.0.0/8") && !JellyfinFirewall.IsTailnetRange("*") && !JellyfinFirewall.IsTailnetRange(null), "Tailscale's range, exactly");

    // GitHub's Windows runners are administrators without a consent prompt: the real firewall, on a program of the test's own.
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
    {
        const string promptRule = "Mira - firewall check (prompt)", rule = "Mira - firewall check", portRule = "Mira - firewall check (port)";
        var folder = Path.Combine(testRoot, "firewall"); Directory.CreateDirectory(folder);
        var program = Path.Combine(folder, "jellyfin.exe"); File.WriteAllBytes(program, []);
        // Its own rules: those naming no program (Windows' apps, services) are read too, as they may concern any program.
        List<JellyfinFirewall.Rule> Own() => JellyfinAutostart.FirewallRules(program).Where(x => !string.IsNullOrEmpty(x.Program)).ToList();
        string Seen() => string.Join("; ", Own());
        // The runner's adapter, on the profile Windows counts it under (Get-NetConnectionProfile), stands for the home
        // network; « Passer en privé » makes it private when it is public (GitHub's is), and the test puts it back.
        var home = LocalNetwork.ThisPcOnNetwork();
        int? original = home is not null && JellyfinAutostart.NetworkCategories().TryGetValue(home.Adapter, out var counted) ? counted : null;
        int Now() => home is null ? 0 : JellyfinAutostart.NetworkCategories().GetValueOrDefault(home.Adapter);
        Assert(JellyfinAutostart.RunSystem("netsh.exe", $"advfirewall firewall add rule name=\"{promptRule}\" dir=in action=block protocol=TCP program=\"{program}\" profile=any enable=yes") == 0, "Block rule not created");
        try
        {
            Assert(Own() is [{ Name: promptRule, Allow: false, Protocol: JellyfinFirewall.Tcp, Inbound: true } read] && (read.Profiles & JellyfinFirewall.AllProfiles) == JellyfinFirewall.AllProfiles
                && JellyfinFirewall.CoversTailnet(read.RemoteAddresses) && JellyfinFirewall.CoversPort(read.LocalPorts, 18097), "Read: " + Seen());
            Assert(home is not null && original is not null, $"Network profiles: {string.Join(", ", JellyfinAutostart.NetworkCategories())}; adapter {home}");
            var before = JellyfinAutostart.Phones(program, 18097, rule);
            Assert(before is { Verdict: JellyfinFirewall.Verdict.Blocked, Kept: null } && before.Profile == original
                && before.Public?.Adapter == (original == JellyfinFirewall.Public ? home!.Adapter : null), $"Before: {before}");
            // The runner's own rules naming no program: those of Windows' apps must not count for Jellyfin.
            var anyProgram = JellyfinAutostart.FirewallRules(program).Where(x => string.IsNullOrEmpty(x.Program)).ToList();
            var packaged = anyProgram.Where(x => x.Name.StartsWith("@{", StringComparison.Ordinal)).ToList();
            bool Counts(JellyfinFirewall.Rule x) => JellyfinFirewall.Applies(x, program, 18097, before!.Profile, home!.Adapter, home.Address) || JellyfinFirewall.Applies(x, program, 18097, JellyfinFirewall.Private);
            Assert(!packaged.Any(Counts), "A packaged app's rule counts for Jellyfin: " + string.Join("; ", packaged.Where(Counts)));
            // « Tout préparer » alone: Mira's rule written, the network left as it is, still named when public (the step is the
            // person's: a café's stays public).
            var result = JellyfinAutostart.OpenFirewall(program, 18097, rule);
            var left = JellyfinAutostart.Phones(program, 18097, rule);
            Assert(result == AutostartResult.Done && Now() == original && JellyfinAutostart.MiraRule(program, 18097, rule) == JellyfinFirewall.RuleState.Ready
                && left?.Public?.Adapter == before!.Public?.Adapter, $"Changed without the person ({result}, {Now()}): {left}; " + Seen());
            // « Passer en privé »: a public network made private; a private or domain one left as it was.
            result = JellyfinAutostart.OpenFirewall(program, 18097, rule, privateNetwork: true);
            Assert(result == AutostartResult.Done, $"Not opened ({result}): " + Seen());
            var profile = Now();
            Assert(profile == (original == JellyfinFirewall.Public ? JellyfinFirewall.Private : original), $"The runner's network: {original} → {profile}");
            var after = Own();
            // Lifted for the home network's profile, now private, and for Tailscale's (Private, ahead of its installation); kept for the others, the public one too.
            Assert(after.Count(x => x.Name == promptRule) == 1 && after.Single(x => x.Name == promptRule) is { Enabled: true } kept
                && (kept.Profiles & JellyfinFirewall.AllProfiles) == JellyfinFirewall.WithoutProfile(JellyfinFirewall.AllProfiles, profile | JellyfinFirewall.Private), "The block rule should keep the other networks: " + Seen());
            Assert(after.Count(x => x.Name == rule && x.Allow && x.LocalPorts == "18097" && (x.Profiles & JellyfinFirewall.AllProfiles) == JellyfinStartup.RuleProfiles) == 2
                && after.Any(x => x.Name == rule && x.RemoteAddresses != "*" && JellyfinFirewall.CoversTailnet(x.RemoteAddresses))
                && JellyfinAutostart.MiraRule(program, 18097, rule) == JellyfinFirewall.RuleState.Ready, "Mira's rule: " + Seen());
            Assert(JellyfinAutostart.Phones(program, 18097, rule) is { Verdict: open, Public: null }, "Still blocked: " + Seen());
            Assert(JellyfinAutostart.OpenFirewall(program, 18097, rule) == AutostartResult.Done && Own().Count(x => x.Name == rule) == 2, "Not opened again over itself: " + Seen());
            // An older version's rule, on every network, Windows' prompt rule gone and the runner's network public again:
            // phones get through by that rule alone, yet it is not ready, and the network is still named to make private
            // when the public profile keeps them out without it (Windows' default). « Tout préparer » narrows the rule and
            // leaves the network; « Passer en privé » then makes it private.
            JellyfinAutostart.RemoveRule(rule); JellyfinAutostart.RemoveRule(promptRule);
            if (original == JellyfinFirewall.Public) Assert(JellyfinAutostart.SetNetworkCategory(home!.Adapter, JellyfinFirewall.Public) && Now() == JellyfinFirewall.Public, "Not put back as public");
            var bare = JellyfinAutostart.Phones(program, 18097, rule);
            Assert(bare is not null && bare.Public?.Adapter == (original == JellyfinFirewall.Public && bare.Verdict == JellyfinFirewall.Verdict.Blocked ? home!.Adapter : null), $"Without rules: {bare}");
            foreach (var from in new[] { "localsubnet", LocalNetwork.TailnetRange })
                Assert(JellyfinAutostart.RunSystem("netsh.exe", $"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP localport=18097 remoteip={from} profile=any program=\"{program}\" enable=yes") == 0, "Old rule not created");
            var older = JellyfinAutostart.Phones(program, 18097, rule);
            Assert(JellyfinAutostart.MiraRule(program, 18097, rule) == JellyfinFirewall.RuleState.Stale && older?.Verdict == open && older.Public == bare!.Public,
                $"An older version's rule: {older}; " + Seen());
            var stay = Now();
            result = JellyfinAutostart.OpenFirewall(program, 18097, rule);
            Assert(result == AutostartResult.Done && JellyfinAutostart.MiraRule(program, 18097, rule) == JellyfinFirewall.RuleState.Ready
                && Own().Where(x => x.Name == rule).All(x => (x.Profiles & JellyfinFirewall.Public) == 0) && Now() == stay, $"Not narrowed ({result}, {Now()}): " + Seen());
            var expected = older!.Public is null ? stay : JellyfinFirewall.Private;
            result = JellyfinAutostart.OpenFirewall(program, 18097, rule, privateNetwork: true);
            Assert(result == AutostartResult.Done && Now() == expected && JellyfinAutostart.Phones(program, 18097, rule) is { Verdict: open, Public: null },
                $"Not made private ({result}, {Now()}): " + Seen());
            // A block rule naming no program (someone's own) stays, and is named for the person: « Tout préparer » does not ask again.
            Assert(JellyfinAutostart.RunSystem("netsh.exe", $"advfirewall firewall add rule name=\"{portRule}\" dir=in action=block protocol=TCP localport=18097 profile=any enable=yes") == 0, "Port rule not created");
            var byPort = JellyfinAutostart.Phones(program, 18097);
            Assert(byPort is { Verdict: JellyfinFirewall.Verdict.Blocked, Kept: portRule } && JellyfinAutostart.OpenFirewall(program, 18097, rule) == AutostartResult.Done
                && JellyfinAutostart.FirewallRules(program).Any(x => x.Name == portRule && x.Enabled), $"Port rule: {byPort}");
            var applying = anyProgram.Where(Counts).Select(x => x.Name).ToList();
            Console.WriteLine($"      (pare-feu réel : carte {home!.Adapter} en profil {original} puis {profile}, avant {before!.Verdict}, règle bloquante gardée pour les autres réseaux ; "
                + $"règle d'une ancienne version resserrée, réseau sans règle {bare!.Verdict} ; {anyProgram.Count} règles sans programme dont {packaged.Count} d'apps, "
                + $"{applying.Count} comptées pour Jellyfin : {string.Join(", ", applying)})");
        }
        finally
        {
            JellyfinAutostart.RemoveRule(promptRule); JellyfinAutostart.RemoveRule(rule); JellyfinAutostart.RemoveRule(portRule);
            if (original is JellyfinFirewall.Public or JellyfinFirewall.Private && Now() != original) JellyfinAutostart.SetNetworkCategory(home!.Adapter, original.Value);
        }
        Assert(Own().Count == 0 && Now() == (original ?? 0), $"Test rules left behind, or the runner's network not put back ({Now()}): " + Seen());
    }
    return Task.CompletedTask;
});
await Test("Dossiers médias : le service Jellyfin peut les lire, fichiers liés ou déplacés compris", async () =>
{
    // Jellyfin's Windows service runs as Network Service: a hard link or a move keeps the download's own permissions.
    var folders = MediaFolders.Under(Path.Combine(testRoot, "service-access")); folders.Create(); folders.AllowJellyfinService();
    var downloads = Path.Combine(testRoot, "service-downloads"); Directory.CreateDirectory(downloads);
    var other = Path.Combine(testRoot, "service-other"); Directory.CreateDirectory(other);
    // Films, Séries and Animes only: not the rest of the root.
    Assert(new[] { folders.Movies, folders.Series, folders.Anime }.All(JellyfinServiceAccess.HasOwnRule) && !JellyfinServiceAccess.HasOwnRule(folders.Root)
        && !JellyfinServiceAccess.HasOwnRule(downloads), "Media folders not shared with the service, or the root shared");
    async Task<string> Import(string file, string library, ImportMode mode)
    {
        var source = Path.Combine(downloads, file); File.WriteAllBytes(source, new byte[64]);
        var result = await MediaImporter.ExecuteAsync(MediaPlanner.Plan(file, null, [new SourceFile(source, file, 64)], new MediaLibraries(library, null, null)), mode);
        Assert(result.Placed.Count == 1 && (mode == ImportMode.Move ? result.Moved : result.Linked), "Import: " + result.Method);
        return result.Placed[0].Destination;
    }
    var linked = await Import("Big.Buck.Bunny.2008.mkv", folders.Movies, ImportMode.KeepSeeding);
    var moved = await Import("Sintel.2010.mkv", folders.Movies, ImportMode.Move);
    var elsewhere = await Import("Tears.of.Steel.2012.mkv", other, ImportMode.KeepSeeding);
    Assert(JellyfinServiceAccess.HasOwnRule(linked) && JellyfinServiceAccess.HasOwnRule(moved), "A placed file stays unreadable by the service");
    Assert(!JellyfinServiceAccess.HasOwnRule(elsewhere), "A folder the service was not given received the right anyway");
});
await Test("Nouveautés : chaque icône citée est dessinée par Mira", () =>
{
    var missing = WhatsNew.All.SelectMany(x => x.Items).Select(x => x.Icon).Where(x => !Mira.Desktop.Views.Icon.Draws(x)).Distinct().ToArray();
    Assert(missing.Length == 0, "Icons not drawn: " + string.Join(", ", missing));
    return Task.CompletedTask;
});
await Test("Session protégée par Windows, mot de passe absent du stockage", () =>
{
    var profile = new LocalProfile(Path.Combine(testRoot, "protected")); var connection = new Connection("http://localhost/", "user", "Alice", "secret-session-token", profile.DeviceId);
    profile.SaveConnection(connection); Assert(profile.LoadConnection() == connection, "Session round trip failed");
    var disk = File.ReadAllBytes(Path.Combine(profile.DirectoryPath, "session.protected")); Assert(!Encoding.UTF8.GetString(disk).Contains("secret-session-token"), "Token stored as plain text"); return Task.CompletedTask;
});
await Test("Profil : un identifiant d’appareil vide est remplacé, puis gardé", () =>
{
    var folder = Path.Combine(testRoot, "empty-device"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "device-id"), "  \n");
    var id = new LocalProfile(folder).DeviceId;
    Assert(id.Length == 32 && new LocalProfile(folder).DeviceId == id && File.ReadAllText(Path.Combine(folder, "device-id")) == id, "Empty device id kept, or not persisted: '" + id + "'");
    return Task.CompletedTask;
});
await Test("Réglages illisibles : réglages par défaut, ancien fichier gardé et signalé ; session illisible sans plantage", () =>
{
    var folder = Path.Combine(testRoot, "garbled-settings"); Directory.CreateDirectory(folder);
    var settings = Path.Combine(folder, "settings.json"); File.WriteAllText(settings, "{\"Volume\":45,\"AutoNext\":fa");
    var profile = new LocalProfile(folder);
    Assert(profile.LoadSettings().Volume == new PlayerSettings().Volume && profile.SettingsUnreadable, "Garbled settings not reported, or not replaced by the defaults");
    Assert(profile.SettingsCopy is { } copy && Path.GetFileName(copy).StartsWith("settings.json.bad-") && File.ReadAllText(copy) == File.ReadAllText(settings), "The garbled settings were not kept aside");
    // No file yet: a first start, nothing to say.
    var fresh = new LocalProfile(Path.Combine(testRoot, "fresh-settings")); fresh.LoadSettings();
    Assert(!fresh.SettingsUnreadable && fresh.SettingsCopy is null, "A missing settings file was reported");
    // A folder where the file was: refused by the system (UnauthorizedAccessException), and nothing to copy.
    var refused = Path.Combine(testRoot, "refused-settings"); Directory.CreateDirectory(Path.Combine(refused, "settings.json"));
    var denied = new LocalProfile(refused);
    Assert(denied.LoadSettings().Volume == new PlayerSettings().Volume && denied.SettingsUnreadable && denied.SettingsCopy is null, "Refused settings not handled");
    // Held by another program (IOException): the settings and the session are both read as absent, Mira still starts.
    var held = new LocalProfile(Path.Combine(testRoot, "held-profile")); held.SaveSettings(new() { Volume = 45 });
    held.SaveConnection(new("http://localhost/", "user", "Alice", "token", held.DeviceId));
    using (File.Open(Path.Combine(held.DirectoryPath, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    using (File.Open(Path.Combine(held.DirectoryPath, "session.protected"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Assert(held.LoadSettings().Volume == new PlayerSettings().Volume && held.SettingsUnreadable && held.SettingsCopy is null && held.LoadConnection() is null, "Settings or session held by another program not handled");
    // Let go before the first save: the intact file is copied before the defaults replace it, once.
    held.SaveSettings(new() { Volume = 70 });
    Assert(held.SettingsCopy is { } kept && File.Exists(kept) && JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(kept), Json.Options)?.Volume == 45, "Settings held at loading were replaced without a copy");
    held.SaveSettings(new() { Volume = 75 });
    Assert(JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(held.SettingsCopy!), Json.Options)?.Volume == 45 && Directory.GetFiles(held.DirectoryPath, "settings.json.bad-*").Length == 1, "The copy was replaced by a later save");
    return Task.CompletedTask;
});
await Test("Images : mémoire bornée, les moins récentes libérées puis relues sur disque sans nouveau téléchargement", async () =>
{
    // 500 × 500 artwork, decoded at the poster width: 1 000 000 bytes each.
    var pixels = new byte[500 * 500 * 4]; Array.Fill(pixels, (byte)180);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(500, 500, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 2000)));
    using var png = new MemoryStream(); encoder.Save(png); var bytes = png.ToArray();
    var downloads = 0;
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(_ =>
    { Interlocked.Increment(ref downloads); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
    const long each = 1_000_000;
    var cache = new ImageCache(client, Path.Combine(testRoot, "image-cache")) { MemoryLimit = each * 3, MemoryTarget = each * 2 };
    MediaItem Poster(int i) => new() { Id = "poster-" + i, ImageTags = new() { ["Primary"] = "tag" } };
    var requests = new List<Task<System.Windows.Media.Imaging.BitmapSource?>>();
    for (var i = 0; i < 5; i++) { requests.Add(cache.GetAsync(Poster(i), targetWidth: 500)); Assert(await requests[i] is { PixelWidth: 500 }, "Image not decoded"); }
    await WaitUntil(() => cache.Settled == 5);
    Assert(cache.MemoryBytes == each * 3, "Memory not trimmed to its target: " + cache.MemoryBytes);
    Assert(ReferenceEquals(cache.GetAsync(Poster(4), targetWidth: 500), requests[4]), "The most recent image left memory");
    var again = cache.GetAsync(Poster(0), targetWidth: 500);
    Assert(!ReferenceEquals(again, requests[0]), "The oldest image stayed in memory past the budget");
    Assert(await again is { PixelWidth: 500 } && downloads == 5, "An image evicted from memory was downloaded again instead of read from disk");
});
await Test("Images : décodées à la taille affichée sur l’écran le plus net, jamais plus grandes que le fichier", async () =>
{
    var pixels = new byte[500 * 500 * 4]; Array.Fill(pixels, (byte)90);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(500, 500, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 2000)));
    using var png = new MemoryStream(); encoder.Save(png); var bytes = png.ToArray();
    var asked = new List<string>();
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(request =>
    { lock (asked) asked.Add(request.RequestUri!.Query); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
    var cache = new ImageCache(client, Path.Combine(testRoot, "image-sizes")) { Scale = 1.5, ScreenPixels = 1920 };
    MediaItem Poster(string id) => new() { Id = id, ImageTags = new() { ["Primary"] = "tag" } };
    // The widest poster card (230 units) at 150 %: 360 px, asked of Jellyfin at that width.
    Assert(await cache.GetAsync(Poster("p")) is { PixelWidth: 360 } && asked.Last().Contains("maxWidth=360"), "Poster: " + asked.Last());
    // A file narrower than asked is not enlarged in memory.
    Assert(await cache.GetAsync(Poster("q"), targetWidth: 640) is { PixelWidth: 500 }, "A 500 px file was decoded wider");
    Assert(await cache.GetAsync(Poster("r"), backdrop: true) is not null && asked.Last().Contains("maxWidth=1920"), "Backdrop: " + asked.Last());
    Assert(cache.Pixels(ImageCache.EpisodeWidth) == 240 && cache.Pixels(ImageCache.LogoWidth) == 960, "Episodes and logos");
});
await Test("Images : disque plein, l’image téléchargée s’affiche quand même", async () =>
{
    var pixels = new byte[64 * 64 * 4]; Array.Fill(pixels, (byte)120);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 256)));
    using var png = new MemoryStream(); encoder.Save(png); var bytes = png.ToArray();
    var connection = new Connection("http://localhost/", "u", "Alice", "token", "device");
    using var client = new JellyfinClient(connection, new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
    var profile = Path.Combine(testRoot, "image-disk-full");
    var cache = new ImageCache(client, profile);
    // The copy on disk cannot be written: a folder stands where its temporary file goes.
    var key = "full-Primary-tag-500";
    var path = Path.Combine(AccountData.ImageFolder(profile, connection), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".img");
    Directory.CreateDirectory(path + ".tmp");
    Assert(await cache.GetAsync(new MediaItem { Id = "full", ImageTags = new() { ["Primary"] = "tag" } }, targetWidth: 500) is { PixelWidth: 64 }, "The image was dropped with its copy on disk");
    Assert(!File.Exists(path), "A copy was kept");
});
await Test("TorLink : sous-titres, bonus, clés de dossiers et noms Windows", () =>
{
    Assert(ReleaseName.SubtitleTags(@"Film\Subs\2_English.srt") == ".en" && ReleaseName.SubtitleTags(@"Film\Film.2012.en.forced.srt") == ".en.forced" && ReleaseName.SubtitleTags(@"Film\French.srt") == ".fr"
        && ReleaseName.SubtitleTags(@"Film\Film.2012.srt") == "" && ReleaseName.SubtitleTags(@"Show\Show.S01E01.eng.SDH.srt") == ".en.sdh", "Subtitle language tags");
    Assert(ReleaseName.CreditlessLabel("[Group] Show - NCOP1 [1080p].mkv", false) == "NCOP01" && ReleaseName.CreditlessLabel("Show - Creditless Ending 2.mkv", false) == "NCED02"
        && ReleaseName.CreditlessLabel("Show - OP1.mkv", false) is null && ReleaseName.CreditlessLabel("Show - OP1.mkv", true) == "NCOP01", "Creditless openings and endings");
    Assert(ReleaseName.Key("JoJo's Bizarre Adventure (2012)") == ReleaseName.Key("JoJos Bizarre Adventure") && ReleaseName.Key("L'Argent (1983)") == ReleaseName.Key("L'argent")
        && ReleaseName.Key("Les Bronzés font du ski") == "les bronzes font du ski", "Folder matching keys");
    Assert(ReleaseName.SafeName("Mission: Impossible - Fallout (2018)") == "Mission - Impossible - Fallout (2018)" && ReleaseName.SafeName("CON") == "CON_"
        && ReleaseName.SafeName("What?/Why* (2020)") == "What Why (2020)" && ReleaseName.SafeName("Trailing dots...") == "Trailing dots", "Windows-safe names");
    Assert(ReleaseName.SeasonOfFolder("Season 17 - Thousand-Year Blood War") == 17 && ReleaseName.SeasonOfFolder("Specials") == 0 && ReleaseName.SeasonOfFolder("Extras") is null, "Season folders");
    Assert(ReleaseName.IsSample(@"Film\Sample\film-sample.mkv") && !ReleaseName.IsSample(@"Film\Film.2019.mkv"), "Sample detection");
    return Task.CompletedTask;
});
await Test("TorLink : films, épisodes, packs et bonus rangés dans les dossiers existants", () =>
{
    var root = Path.Combine(testRoot, "planner-library"); var films = Path.Combine(root, "FILMS"); var series = Path.Combine(root, "SERIES"); var anime = Path.Combine(root, "ANIME");
    foreach (var folder in new[] { Path.Combine(films, "L'Argent (1983)"), series, Path.Combine(anime, "Jujutsu Kaisen (2020)", "Season 01"), Path.Combine(anime, "Bleach (2004)", "Season 17 - Thousand-Year Blood War") }) Directory.CreateDirectory(folder);
    var libraries = new MediaLibraries(films, series, anime);
    SourceFile File(string relative, long length) => new(Path.Combine(@"C:\downloads", relative), relative, length);
    HashSet<string> Targets(ImportPlan plan) => plan.Operations.Select(x => x.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var a = "L'argent.1983.Criterion.1080p.BluRay.x265.HEVC.FLAC-SARTRE";
    var argent = MediaPlanner.Plan(a, "tpb-movies", [File($@"{a}\{a}.mkv", 6_900_000_000), File($@"{a}\Cannes Film Festival 1983.mkv", 121_000_000), File($@"{a}\Sample\sample.mkv", 50_000_000), File($@"{a}\L'argent.1983.French.srt", 90_000), File($@"{a}\info.txt", 1_898)], libraries);
    var argentFolder = Path.Combine(films, "L'Argent (1983)");
    Assert(argent.Kind == MediaKind.Movie && argent.Title == "L'Argent (1983)" && Targets(argent).SetEquals(new[] { Path.Combine(argentFolder, "L'Argent (1983).mkv"), Path.Combine(argentFolder, "Extras", "Cannes Film Festival 1983.mkv"), Path.Combine(argentFolder, "L'Argent (1983).fr.srt") }),
        "Film with extras, sample and notes: " + string.Join(" | ", Targets(argent)));
    Assert(argent.Notes.Any(x => x.Contains("échantillon")) && argent.Notes.Any(x => x.Contains("annexe")), "Left-out files are not reported");
    var jujutsu = MediaPlanner.Plan("[SubsPlease] Jujutsu Kaisen - 05 (1080p) [ABCD1234].mkv", "subsplease", [File("[SubsPlease] Jujutsu Kaisen - 05 (1080p) [ABCD1234].mkv", 1_400_000_000)], libraries);
    Assert(jujutsu.Kind == MediaKind.Anime && jujutsu.Operations.Single().Destination == Path.Combine(anime, "Jujutsu Kaisen (2020)", "Season 01", "Jujutsu Kaisen (2020) - S01E05.mkv"), "Existing anime folder not reused: " + string.Join(" | ", Targets(jujutsu)));
    var bleach = MediaPlanner.Plan("Bleach.S17E49.1080p.WEB.H264-GRP", null, [File(@"Bleach.S17E49.1080p.WEB.H264-GRP\Bleach.S17E49.1080p.WEB.H264-GRP.mkv", 1_000_000_000)], libraries);
    Assert(bleach.Kind == MediaKind.Anime && bleach.Operations.Single().Destination == Path.Combine(anime, "Bleach (2004)", "Season 17 - Thousand-Year Blood War", "Bleach (2004) - S17E49.mkv"), "Named season folder not reused: " + string.Join(" | ", Targets(bleach)));
    var jojoName = "JoJos.Bizarre.Adventure.S06E02.The.Sheriffs.Request.to.Mountain.Tim.1080p.NF.WEB-DL.DUAL.AAC2.0.H.264.MSubs-ToonsHub.mkv";
    var jojo = MediaPlanner.Plan(jojoName, "x1337-tv", [File(jojoName, 942_154_418)], libraries);
    Assert(jojo.Kind == MediaKind.Series && jojo.Operations.Single().Destination == Path.Combine(series, "JoJos Bizarre Adventure", "Season 06", "JoJos Bizarre Adventure - S06E02.mkv"), "TV episode: " + string.Join(" | ", Targets(jojo)));
    var p = "[Marin] My Dress-Up Darling - S01 [PROPER BD 1080p HEVC FLAC] [Dual-Audio]";
    var pack = MediaPlanner.Plan(p, null, [File($@"{p}\[Marin] My Dress-Up Darling - 01 [BD 1080p HEVC FLAC] [ABCDEF12].mkv", 1_064_000_000), File($@"{p}\[Marin] My Dress-Up Darling - 02 [BD 1080p HEVC FLAC] [ABCDEF13].mkv", 1_277_000_000),
        File($@"{p}\[Marin] My Dress-Up Darling - 01 [BD 1080p HEVC FLAC] [ABCDEF12].fr.ass", 40_000), File($@"{p}\Extras\[Marin] My Dress-Up Darling - NCOP [BD 1080p].mkv", 113_000_000)], libraries);
    var show = Path.Combine(anime, "My Dress-Up Darling");
    Assert(pack.Kind == MediaKind.Anime && pack.Title == "My Dress-Up Darling · 2 épisodes" && Targets(pack).SetEquals(new[] { Path.Combine(show, "Season 01", "My Dress-Up Darling - S01E01.mkv"), Path.Combine(show, "Season 01", "My Dress-Up Darling - S01E02.mkv"),
        Path.Combine(show, "Season 01", "My Dress-Up Darling - S01E01.fr.ass"), Path.Combine(show, "Extras", "My Dress-Up Darling - NCOP.mkv") }), "Anime season pack: " + string.Join(" | ", Targets(pack)));
    var lone = MediaPlanner.Plan("Sample.2019.1080p", null, [File(@"Sample.2019.1080p\Sample.2019.1080p.mkv", 2_000_000_000)], libraries);
    Assert(lone.Operations.Count == 1 && lone.Title == "Sample (2019)", "A lone feature titled Sample was dropped");
    Assert(MediaPlanner.Plan("Some.Game-FitGirl", "fitgirl", [File(@"Some.Game-FitGirl\setup.exe", 1000)], libraries).Ignored is not null, "A game would reach Jellyfin");
    Assert(MediaPlanner.Plan("Album.2020.FLAC", null, [File(@"Album.2020.FLAC\01.flac", 1000)], libraries).Ignored is not null, "A download without video would reach Jellyfin");
    Assert(MediaPlanner.Plan("Film.2020.1080p.mkv", null, [File("Film.2020.1080p.mkv", 1000)], new MediaLibraries(null, series, anime)).Problem is not null, "A missing film library must wait for a folder, not guess one");
    Assert(MediaPlanner.Plan("Tears.of.Steel.2012.mkv", null, [File("Tears.of.Steel.2012.mkv", 1000)], libraries, MediaKind.Anime).Kind == MediaKind.Anime, "A forced kind is ignored");
    return Task.CompletedTask;
});
await Test("TorLink : lien physique, déplacement entre disques sans copie restante, fichier existant jamais remplacé, rien hors de la bibliothèque", async () =>
{
    var dir = Path.Combine(testRoot, "importer"); var downloads = Path.Combine(dir, "downloads"); var films = Path.Combine(dir, "FILMS");
    Directory.CreateDirectory(downloads); Directory.CreateDirectory(films);
    var video = Path.Combine(downloads, "Big.Buck.Bunny.2008.1080p.mkv"); File.WriteAllBytes(video, new byte[4096]);
    var plan = MediaPlanner.Plan("Big.Buck.Bunny.2008.1080p.mkv", "yts", [new SourceFile(video, "Big.Buck.Bunny.2008.1080p.mkv", 4096)], new MediaLibraries(films, null, null));
    var target = Path.Combine(films, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).mkv");
    var first = await MediaImporter.ExecuteAsync(plan, ImportMode.KeepSeeding);
    Assert(first.Placed.Count == 1 && first.Linked && !first.Copied && File.Exists(target) && File.Exists(video), "Hard link not created, or the download moved");
    using (var stream = new FileStream(video, FileMode.Append)) stream.WriteByte(1);
    Assert(new FileInfo(target).Length == 4097, "The library file is a copy instead of a hard link");
    var again = await MediaImporter.ExecuteAsync(plan, ImportMode.KeepSeeding);
    Assert(again.AlreadyThere.Count == 1 && again.Placed.Count == 0, "A second run duplicated the file");
    var other = Path.Combine(downloads, "other.mkv"); File.WriteAllBytes(other, new byte[10]);
    var conflict = await MediaImporter.ExecuteAsync(plan with { Operations = [plan.Operations[0] with { Source = other }] }, ImportMode.KeepSeeding);
    Assert(conflict.Conflicts.Count == 1 && new FileInfo(target).Length == 4097, "An existing library file was replaced");
    foreach (var outside in new[] { Path.Combine(dir, "outside.mkv"), Path.Combine(films, "..", "escaped.mkv"), Path.Combine(films, "tool", "tool.exe") })
    {
        var refused = await MediaImporter.ExecuteAsync(plan with { Operations = [plan.Operations[0] with { Destination = outside }] }, ImportMode.KeepSeeding);
        Assert(refused.Failures.Count == 1 && !File.Exists(Path.GetFullPath(outside)), "A file was written outside the library or with a program extension: " + outside);
    }
    // A \\?\ library path does not share the downloads' root: this exercises the way between two drives, where no
    // link is possible. Even when sharing is kept, the download moves: nothing stays twice on disk.
    var copies = Path.Combine(dir, "COPIES"); Directory.CreateDirectory(copies);
    var bytes = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
    foreach (var mode in new[] { ImportMode.KeepSeeding, ImportMode.Move })
    {
        var name = mode == ImportMode.Move ? "Elephants.Dream.2006.mkv" : "Tears.of.Steel.2012.mkv";
        var source = Path.Combine(downloads, name); File.WriteAllBytes(source, bytes);
        var crossed = await MediaImporter.ExecuteAsync(MediaPlanner.Plan(name, null, [new SourceFile(source, name, 5000)], new MediaLibraries(@"\\?\" + copies, null, null)), mode);
        var arrived = crossed.Placed.Single().Destination;
        Assert(crossed is { Moved: true, OtherDrive: true, Copied: false, Linked: false, Placed.Count: 1 } && !File.Exists(source) && File.ReadAllBytes(arrived).SequenceEqual(bytes),
            $"{mode} between drives left a copy behind, or lost data: " + crossed.Method);
        Assert(crossed.Method == "déplacement depuis un autre disque", "Method shown after crossing drives: " + crossed.Method);
    }
    Assert(!Directory.EnumerateFiles(copies, "*" + MediaImporter.PartialSuffix, SearchOption.AllDirectories).Any(), "A partial file was left");
    var sintel = Path.Combine(downloads, "Sintel.2010.mkv"); File.WriteAllBytes(sintel, new byte[100]);
    var moved = await MediaImporter.ExecuteAsync(MediaPlanner.Plan("Sintel.2010.mkv", null, [new SourceFile(sintel, "Sintel.2010.mkv", 100)], new MediaLibraries(films, null, null)), ImportMode.Move);
    Assert(moved is { Moved: true, OtherDrive: false, Copied: false } && moved.Method == "déplacement" && !File.Exists(sintel) && File.Exists(Path.Combine(films, "Sintel (2010)", "Sintel (2010).mkv")), "Move mode: " + moved.Method);
    Directory.CreateDirectory(Path.Combine(films, "Empty Show", "Season 01"));
    MediaImporter.PruneEmptyFolders([Path.Combine(films, "Empty Show", "Season 01")], [films]);
    Assert(!Directory.Exists(Path.Combine(films, "Empty Show")) && Directory.Exists(films) && File.Exists(target), "Empty folders were kept, or more was removed");
    // Nested library folders: an anime root inside the series one is never removed, even once empty.
    var seriesRoot = Path.Combine(dir, "SERIES"); var nestedAnime = Path.Combine(seriesRoot, "Anime");
    Directory.CreateDirectory(Path.Combine(nestedAnime, "Show", "Season 01"));
    MediaImporter.PruneEmptyFolders([Path.Combine(nestedAnime, "Show", "Season 01")], [seriesRoot, nestedAnime]);
    Assert(Directory.Exists(nestedAnime) && !Directory.Exists(Path.Combine(nestedAnime, "Show")), "A nested library root was removed, or the emptied show was kept");
});
await Test("TorLink : fichier déjà présent jamais déplacé, classement conservé, déplacement repris et journal illisible gardé", async () =>
{
    var dir = Path.Combine(testRoot, "torlink-recovery"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
    var downloads = Path.Combine(dir, "downloads"); var films = Path.Combine(dir, "FILMS"); var series = Path.Combine(dir, "SERIES"); var anime = Path.Combine(dir, "ANIME");
    foreach (var folder in new[] { state.ConfigDirectory, state.DataDirectory, downloads, films, series, anime }) Directory.CreateDirectory(folder);
    var libraries = new MediaLibraries(films, series, anime);
    var importer = new TorLinkImporter(Path.Combine(dir, "profile"));
    var at = importer.Baseline.AddSeconds(5).ToUnixTimeMilliseconds();
    var history = new List<object>();
    string Finished(char c, string name, string source) { var id = new string(c, 40); history.Add(new { id, name, source, sizeBytes = 10, dir = downloads, completedAt = at }); File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(history)); return id; }
    string Download(string relative, int size) { var path = Path.Combine(downloads, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[size]); return path; }

    // A same-size film that was already in the library: found there, never moved by « Classer ».
    Download("Dune.2021.1080p.mkv", 64);
    var existing = Path.Combine(films, "Dune (2021)", "Dune (2021).mkv"); Directory.CreateDirectory(Path.GetDirectoryName(existing)!); File.WriteAllBytes(existing, new byte[64]);
    var dune = Finished('a', "Dune.2021.1080p.mkv", "yts");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(dune) is { State: TorLinkImportState.Imported, Files: [{ Found: true }] }, "A file already in the library was taken for Mira's own");
    var (left, _) = await importer.ReclassifyAsync(dune, MediaKind.Series, libraries);
    Assert(File.Exists(existing) && left?.Kind == MediaKind.Movie && !Directory.EnumerateFileSystemEntries(series).Any(), "« Classer » moved a file Mira did not place");

    // A hard link of TorLink's own file is Mira's, even without its log line: it can move.
    var sintelSource = Download("Sintel.2010.mkv", 32);
    Assert((await MediaImporter.ExecuteAsync(MediaPlanner.Plan("Sintel.2010.mkv", "yts", [new SourceFile(sintelSource, "Sintel.2010.mkv", 32)], libraries), ImportMode.KeepSeeding)).Linked, "Hard link");
    var sintel = Finished('b', "Sintel.2010.mkv", "yts");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(sintel) is { State: TorLinkImportState.Imported, Files: [{ Found: false }] }, "A hard link of TorLink's file was not recognised");

    // « Réessayer » after « Classer comme anime » keeps the anime library: no second link among the series.
    Download("Show.S01E02.1080p.mkv", 16);
    var show = Finished('c', "Show.S01E02.1080p.mkv", "eztv");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(show)?.Kind == MediaKind.Series, "Episode not placed among the series");
    await importer.ReclassifyAsync(show, MediaKind.Anime, libraries);
    Assert(importer.Find(show) is { Kind: MediaKind.Anime, ForcedKind: MediaKind.Anime, Files: [var moved] } && File.Exists(moved.Destination) && moved.Destination.StartsWith(anime), "Reclassification");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true, requested: [show]);
    Assert(importer.Find(show) is { Kind: MediaKind.Anime, State: TorLinkImportState.Imported } && !Directory.EnumerateFiles(series, "*", SearchOption.AllDirectories).Any(), "A new attempt put the reclassified episode back among the series");

    // Move mode stopped by a conflicting subtitle: the moved video stays tracked and the subtitle follows on the next attempt.
    var packFiles = new[] { Download(@"Pack.S02E01\Pack.S02E01.mkv", 24), Download(@"Pack.S02E01\Pack.S02E01.en.srt", 5) };
    var packPlan = MediaPlanner.Plan("Pack.S02E01", "eztv", packFiles.Select(x => new SourceFile(x, Path.GetRelativePath(downloads, x), new FileInfo(x).Length)).ToList(), libraries);
    var subtitle = packPlan.Operations.Single(x => x.Role == "subtitle").Destination; var packVideo = packPlan.Operations.Single(x => x.Role == "video").Destination;
    Directory.CreateDirectory(Path.GetDirectoryName(subtitle)!); File.WriteAllBytes(subtitle, new byte[3]);
    var pack = Finished('d', "Pack.S02E01", "eztv");
    await importer.ProcessAsync(state, libraries, ImportMode.Move, automatic: true);
    Assert(importer.Find(pack) is { State: TorLinkImportState.Partial } && File.Exists(packVideo) && !File.Exists(packFiles[0]), "Move with a conflicting subtitle");
    File.Delete(subtitle);
    await importer.ProcessAsync(state, libraries, ImportMode.Move, automatic: true, requested: [pack]);
    Assert(importer.Find(pack) is { State: TorLinkImportState.Imported, Files.Count: 2 } && File.Exists(subtitle) && new FileInfo(subtitle).Length == 5 && File.Exists(packVideo) && !File.Exists(packFiles[1]),
        "The rest of a moved download could not be placed any more: " + importer.Find(pack)?.Message);
    // Emptied by the move, the download's folder leaves TorLink's; TorLink's own folder stays.
    Assert(!Directory.Exists(Path.Combine(downloads, "Pack.S02E01")) && Directory.Exists(downloads), "The emptied download folder stayed in TorLink's, or TorLink's folder went");
    Assert(importer.Find(pack) is { MovedOut: true, SeedPaused: false } && importer.MovedSeeds().Contains(pack) && !importer.MovedSeeds().Contains(dune), "A moved download is not set for its sharing to pause");

    // Before Mira starts TorLink again: the moved download stops being shared; anything else is left as it was.
    var other = new string('f', 40);
    File.WriteAllText(state.SeedsFile, JsonSerializer.Serialize(new object[] { pack.ToUpperInvariant(), new { id = other, status = "seeding" } }));
    var paused = state.PauseSeeds([pack]);
    var seeds = JsonDocument.Parse(File.ReadAllText(state.SeedsFile)).RootElement.EnumerateArray().ToList();
    Assert(paused.SequenceEqual([pack]) && seeds.Count == 2 && seeds[0].GetProperty("status").GetString() == "paused" && seeds[1].GetProperty("status").GetString() == "seeding" && seeds[1].GetProperty("id").GetString() == other,
        "seeds.json after pausing: " + File.ReadAllText(state.SeedsFile));
    importer.MarkSeedPaused(paused);
    Assert(!importer.MovedSeeds().Contains(pack) && state.PauseSeeds([pack]).SequenceEqual([pack]), "A paused download is paused again, or no longer found");
    File.WriteAllText(state.SeedsFile, "[{\"id\":");
    Assert(state.PauseSeeds([pack]).Count == 0 && File.ReadAllText(state.SeedsFile) == "[{\"id\":", "A half-written seeds.json was rewritten");
    Assert(new TorLinkState(Path.Combine(dir, "none"), Path.Combine(dir, "none")).PauseSeeds([pack]).Count == 0, "Pausing without seeds.json");

    // An unreadable log is kept aside instead of lost; incomplete entries are dropped without failing.
    var broken = Path.Combine(dir, "broken"); Directory.CreateDirectory(broken);
    File.WriteAllText(Path.Combine(broken, "torlink-imports.json"), "{\"Baseline\":\"2026-01-01T00:00:00+00:00\",\"Entries\":[{\"Id\":");
    Assert(new TorLinkImporter(broken).Entries.Count == 0 && Directory.EnumerateFiles(broken, "torlink-imports.json.bad-*").Any(), "An unreadable log was overwritten without a copy");
    var partial = Path.Combine(dir, "partial"); Directory.CreateDirectory(partial);
    File.WriteAllText(Path.Combine(partial, "torlink-imports.json"), "{\"Baseline\":\"2026-01-01T00:00:00+00:00\",\"Entries\":[null,{\"Id\":\"zz\"},{\"Id\":\"" + new string('e', 40) + "\",\"Files\":null,\"Title\":null}]}");
    Assert(new TorLinkImporter(partial) is { Entries: [{ Files.Count: 0, Title: "" }] } loaded && loaded.Baseline.Year == 2026, "A log with incomplete entries");
    File.WriteAllText(Path.Combine(partial, "torlink-imports.json"), "{\"Baseline\":\"2026-01-01T00:00:00+00:00\",\"Entries\":null}");
    Assert(new TorLinkImporter(partial).Entries.Count == 0, "A log without entries");
});
await Test("Pseudo-console : sortie UTF-8, taille et code de sortie d’un programme console", async () =>
{
    var output = new StringBuilder();
    var exitedEarly = new TaskCompletionSource<int>();
    using var console = Mira.Desktop.TorLink.PseudoConsole.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "echo mira-conpty-ok é & mode con"], testRoot, EnvironmentBlock(), 90, 20,
        text => { lock (output) output.Append(text); }, code => exitedEarly.TrySetResult(code));
    var code = await console.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(await exitedEarly.Task.WaitAsync(TimeSpan.FromSeconds(2)) == code, "The exit callback given at start was not called");
    // The exit can be reported before the last output is read: wait for the text itself.
    string Text() { lock (output) return Plain(output.ToString()); }
    try { await WaitUntil(() => Text() is var t && t.Contains("mira-conpty-ok é") && t.Contains("90") && t.Contains("20"), 6); } catch (TimeoutException) { }
    Assert(code == 0, "Console program exit code lost: " + code);
    Assert(Text() is var text && text.Contains("mira-conpty-ok é") && text.Contains("90") && text.Contains("20"), "Pseudo console output or size lost: " + Text());
});
await Test("Mises à jour : type d’installation, dossier remplacé sans toucher aux données, retour arrière sur erreur", () =>
{
    var root = Path.Combine(testRoot, "update-apply"); var app = Path.Combine(root, "app"); var incoming = Path.Combine(root, "incoming");
    Directory.CreateDirectory(Path.Combine(app, "data")); Directory.CreateDirectory(Path.Combine(incoming, "sub")); Directory.CreateDirectory(Path.Combine(incoming, "data"));
    File.WriteAllText(Path.Combine(app, "Mira.dll"), "ancienne"); File.WriteAllText(Path.Combine(app, "seulement-avant.txt"), "gardé"); File.WriteAllText(Path.Combine(app, "data", "settings.json"), "mes réglages");
    File.WriteAllText(Path.Combine(incoming, "Mira.dll"), "nouvelle"); File.WriteAllText(Path.Combine(incoming, "sub", "ajout.txt"), "ajouté"); File.WriteAllText(Path.Combine(incoming, "data", "settings.json"), "autres réglages");
    Assert(Updater.Detect(app, singleFile: true) == InstallKind.Portable && Updater.Detect(app, singleFile: false) == InstallKind.Folder
        && Updater.Detect(@"C:\src\Mira\src\Mira.Desktop\bin\Release\net10.0-windows10.0.19041.0\", false) == InstallKind.Development, "Installation kind");
    UpdateApplier.CopyFolder(incoming, app, Path.Combine(app, "data", "updates", "rollback-1"));
    Assert(File.ReadAllText(Path.Combine(app, "Mira.dll")) == "nouvelle" && File.ReadAllText(Path.Combine(app, "sub", "ajout.txt")) == "ajouté" && File.ReadAllText(Path.Combine(app, "seulement-avant.txt")) == "gardé", "Folder not updated");
    Assert(File.ReadAllText(Path.Combine(app, "data", "settings.json")) == "mes réglages" && !Directory.Exists(Path.Combine(app, "data", "updates", "rollback-1")), "Data touched or rollback copy left behind");
    // A file that cannot be replaced: what was already replaced comes back, and nothing new stays.
    File.WriteAllText(Path.Combine(incoming, "Mira.dll"), "plus récente"); File.WriteAllText(Path.Combine(incoming, "verrou.bin"), "x"); File.WriteAllText(Path.Combine(app, "verrou.bin"), "ancien");
    using (new FileStream(Path.Combine(app, "verrou.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var failed = false; try { UpdateApplier.CopyFolder(incoming, app, Path.Combine(app, "data", "updates", "rollback-2"), attempts: 2); } catch (IOException) { failed = true; }
        Assert(failed && File.ReadAllText(Path.Combine(app, "Mira.dll")) == "nouvelle" && File.ReadAllText(Path.Combine(app, "data", "settings.json")) == "mes réglages"
            && !Directory.EnumerateFiles(app, "*" + UpdateApplier.NewSuffix, SearchOption.AllDirectories).Any(), "A failed update was not rolled back, or left new files behind");
    }
    File.WriteAllText(Path.Combine(app, "unins000.exe"), ""); File.WriteAllText(Path.Combine(app, "unins000.dat"), "");
    Assert(Updater.Detect(app, singleFile: false) == InstallKind.Installer, "Inno Setup installation not recognised");
    var exe = Path.Combine(root, "Mira-portable.exe"); var fresh = Path.Combine(root, "nouveau.exe"); File.WriteAllText(exe, "ancien exe"); File.WriteAllText(fresh, "nouvel exe");
    UpdateApplier.ReplaceFile(fresh, exe);
    Assert(File.ReadAllText(exe) == "nouvel exe" && !File.Exists(exe + ".previous") && !File.Exists(exe + ".update"), "Portable executable not replaced cleanly");
    return Task.CompletedTask;
});
await Test("Lecteurs d’écran : un bouton fait d’une icône et d’un texte porte ce texte, une icône seule est signalée", () =>
{
    // WPF names a button after its content only when it is a string: Mira's default style names the others after the
    // text they show (Accessible.ContentName), and the visual checks fail on any control left without a name.
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            static System.Windows.Controls.StackPanel Label(string kind, string text)
            {
                var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                panel.Children.Add(new Mira.Desktop.Views.Icon { Kind = kind }); panel.Children.Add(new System.Windows.Controls.TextBlock { Text = text });
                return panel;
            }
            Assert(Mira.Desktop.Views.Accessible.ContentText(Label("heart-fill", "Dans tes favoris")) == "Dans tes favoris", "Icon and text");
            Assert(Mira.Desktop.Views.Accessible.ContentText(new Mira.Desktop.Views.Icon { Kind = "close" }) == "" && Mira.Desktop.Views.Accessible.ContentText(null) == "", "An icon alone has no words");
            Assert(Mira.Desktop.Views.Accessible.ContentText(new System.Windows.Controls.Border { Child = new System.Windows.Controls.TextBlock { Text = "Lecture" } }) == "Lecture", "Text in a border");
            var root = new System.Windows.Controls.StackPanel();
            var iconOnly = new System.Windows.Controls.Button { Content = new Mira.Desktop.Views.Icon { Kind = "close" } };
            var text = new System.Windows.Controls.Button { Content = "Réessayer" };
            var named = new System.Windows.Controls.Button { Content = new Mira.Desktop.Views.Icon { Kind = "close" } }; System.Windows.Automation.AutomationProperties.SetName(named, "Fermer");
            var labelled = new System.Windows.Controls.Button { Content = Label("info", "Plus d’infos") };
            System.Windows.Automation.AutomationProperties.SetName(labelled, (string)Mira.Desktop.Views.Accessible.ContentName.Convert(labelled.Content, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
            var hidden = new System.Windows.Controls.Button { Content = new Mira.Desktop.Views.Icon(), Visibility = System.Windows.Visibility.Collapsed };
            var decorative = new System.Windows.Controls.Button { Content = new Mira.Desktop.Views.Icon(), Focusable = false };
            var field = new System.Windows.Controls.TextBox();
            foreach (var control in new System.Windows.UIElement[] { iconOnly, text, named, labelled, hidden, decorative, field }) root.Children.Add(control);
            var unnamed = Mira.Desktop.Views.Accessible.Unnamed(root);
            Assert(unnamed.Count == 2 && unnamed.Contains(iconOnly) && unnamed.Contains(field), "Unnamed: " + string.Join(", ", unnamed.Select(Mira.Desktop.Views.Accessible.Describe)));
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (failure is not null) throw failure;
    return Task.CompletedTask;
});
await DesktopSupplyChainTests.RunAsync(Test, testRoot);
if (args.Contains("--torlink-integration"))
{
    await Test("TorLink réel : écran d’accueil dans la pseudo-console, redimensionnement, arrêt propre et état isolé", async () =>
    {
        // No TorLink on this PC (npx's copy is no longer used): Mira's own, installed into a test profile from its lock.
        var installation = Mira.Desktop.TorLink.TorLinkInstallation.Locate(null)
            ?? await Mira.Desktop.TorLink.TorLinkSetup.InstallAsync(Path.Combine(testRoot, "torlink-profile"), new Progress<Mira.Desktop.TorLink.TorLinkSetup.Step>(_ => { }));
        Assert(installation.HasRuntime, "Node.js runtime not found for TorLink");
        var state = Path.Combine(testRoot, "torlink-state");
        var real = TorLinkState.ForCurrentUser(stateOverride: null);
        var realStamp = File.Exists(real.QueueFile) ? File.GetLastWriteTimeUtc(real.QueueFile) : DateTime.MinValue;
        var environment = EnvironmentBlock(); environment["TORLINK_STATE_DIR"] = state; environment["COLORTERM"] = "truecolor";
        var before = installation.RunningElsewhere(null);
        var output = new StringBuilder();
        using var console = Mira.Desktop.TorLink.PseudoConsole.Start(installation.Node, [installation.Entry], installation.Root, environment, 110, 32, text => { lock (output) output.Append(text); });
        await WaitUntil(() => { lock (output) return Plain(output.ToString()).Contains("torrent downloader"); }, 20);
        Assert(before is not null || installation.RunningElsewhere(null) == console.ProcessId, "A running TorLink is not detected from its command line");
        Assert(installation.RunningElsewhere(console.ProcessId) == before, "Mira's own TorLink is reported as another instance");
        console.Resize(130, 40); await Task.Delay(600);
        Assert(!console.HasExited, "TorLink stopped after a resize");
        console.Write("\u0003");
        var code = await console.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert(code == 0, "TorLink did not quit cleanly on Ctrl+C: " + code);
        Assert(File.Exists(Path.Combine(state, "data", "queue.json")), "TorLink did not use the isolated state folder");
        Assert((File.Exists(real.QueueFile) ? File.GetLastWriteTimeUtc(real.QueueFile) : DateTime.MinValue) == realStamp, "The real TorLink queue was touched");
        await WaitUntil(() => installation.RunningElsewhere(null) == before, 5);
    });
}
if (args.Contains("--mpv-integration"))
{
    await Test("libmpv réel : flux HTTP authentifié, reprise, pause, seek et synchronisation", async () =>
    {
        using var client = new JellyfinClient(new("http://127.0.0.1:18096/", "mock-user", "Compte de test", "mira-test-token", "mpv-integration"));
        var catalog = await client.BrowseAsync(); Assert(catalog.Items.Count == 1, "Mock catalog not loaded");
        var info = await client.PlaybackAsync("mock-film"); var source = info.MediaSources.Single();
        using var mpv = new MpvEngine(IntPtr.Zero, new() { Volume = 0 }, headless: true);
        var loaded = false; var ended = false; string? error = null;
        mpv.FileLoaded += () => loaded = true; mpv.Ended += _ => ended = true; mpv.Error += text => error = text;
        mpv.Load(client.StreamUri("mock-film", source.Id).AbsoluteUri, 2, client.AuthorizationHeader);
        var wait = Stopwatch.StartNew(); while (!loaded && error is null && wait.Elapsed < TimeSpan.FromSeconds(10)) { mpv.Poll(); await Task.Delay(20); }
        Assert(loaded, "Authenticated mpv stream did not load: " + error);
        await Task.Delay(250); mpv.Poll(); Assert(mpv.Number("time-pos") >= 1.9, "Resume position ignored");
        mpv.Set("pause", "yes"); await Task.Delay(80); mpv.Poll(); Assert(mpv.Flag("pause"), "Pause failed");
        var position = mpv.Number("time-pos"); await Task.Delay(150); mpv.Poll(); Assert(Math.Abs(mpv.Number("time-pos") - position) < 0.1, "Playback progressed while paused");
        mpv.Command("seek", "8", "absolute+exact"); await Task.Delay(150); mpv.Poll(); Assert(Math.Abs(mpv.Number("time-pos") - 8) < 0.3, "Seek failed");
        Assert(mpv.Tracks().Any(t => t.Type == "audio"), "Audio track not exposed");
        var report = new PlaybackReport { ItemId = "mock-film", MediaSourceId = source.Id, PlaySessionId = info.PlaySessionId, PositionTicks = 2 * TimeSpan.TicksPerSecond };
        var store = Store("native-sync"); await using var sync = new SyncService(client, store);
        await sync.RecordAsync("start", report); await sync.RecordAsync("progress", report with { PositionTicks = 8 * TimeSpan.TicksPerSecond, IsPaused = true });
        await sync.RecordAsync("stop", report with { PositionTicks = 8 * TimeSpan.TicksPerSecond });
        await WaitUntil(() => store.PendingCount == 0);
        mpv.Command("stop"); await Task.Delay(50); mpv.Poll(); Assert(ended, "Stop event not observed");
    });
}
return run.Finish();
