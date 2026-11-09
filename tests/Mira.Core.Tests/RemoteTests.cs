using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using static TestSupport;

/// <summary>
/// Jellyfin's remote control (RemoteControl): the commands Mira for Windows and Mac read in Jellyfin's WebSocket, the
/// capabilities they declare, the devices the iPhone offers to play on; with MIRA_REMOTE_JELLYFIN (« url user
/// password »), the same against a real Jellyfin.
/// </summary>
internal static class RemoteTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Télécommande : les commandes de Jellyfin lues comme il les envoie (tests/fixtures/remote.json)", () =>
        {
            var fixture = Fixture();
            foreach (var row in fixture["messages"]!.AsArray())
            {
                var type = (string)row!["type"]!;
                using var data = JsonDocument.Parse(row["data"]!.ToJsonString());
                var command = RemoteControl.Read(type, data.RootElement);
                var expect = row["expect"];
                var where = $"{type} {row["data"]!.ToJsonString()}";
                if (expect is null) { Assert(command is null, $"{where} : {command}"); continue; }
                Assert(command is not null && command.Action.ToString() == (string)expect["action"]!, $"{where} : {command}");
                if (expect["item"] is { } item) Assert(command!.ItemId == (string)item!, $"{where} : {command!.ItemId}");
                if (expect.AsObject().ContainsKey("start")) Assert(command!.Start == (double?)expect["start"], $"{where} : start {command!.Start}");
                if (expect["position"] is { } position) Assert(command!.Position == (double)position!, $"{where} : position {command!.Position}");
                if (expect["volume"] is { } volume) Assert(command!.Volume == (int)volume!, $"{where} : volume {command!.Volume}");
                if (expect["header"] is { } header) Assert(command!.Header == (string)header! && command.Text == (string)expect["text"]!, $"{where} : « {command!.Header} » « {command.Text} »");
                if (expect["said"] is { } said) Assert(RemoteControl.Said(command!) == (string)said!, $"{where} : « {RemoteControl.Said(command!)} »");
            }
            foreach (var row in fixture["levels"]!.AsArray())
            {
                var parts = ((string)row![0]!).Split(':');
                var command = new RemoteCommand(Enum.Parse<RemoteAction>(parts[0])) { Volume = parts.Length > 1 ? int.Parse(parts[1]) : 0 };
                var level = RemoteControl.Level(command, (double)row[1]!);
                Assert(level == (double)row[2]!, $"{row.ToJsonString()} : {level}");
            }
            var targets = fixture["targets"]!;
            using var sessions = JsonDocument.Parse(targets["sessions"]!.ToJsonString());
            var found = RemoteControl.Targets(sessions.RootElement, (string)targets["ownDevice"]!);
            var expected = targets["expect"]!.AsArray();
            Assert(found.Select(x => x.SessionId).SequenceEqual(expected.Select(x => (string)x!["id"]!)), "Appareils : " + string.Join(", ", found.Select(x => x.SessionId)));
            foreach (var (target, e) in found.Zip(expected))
            {
                var wanted = new RemoteTarget((string)e!["id"]!, (string)e["deviceId"]!, (string)e["device"]!, (string)e["client"]!, "ogsas", (string?)e["playing"], (string?)e["playingId"], (string?)e["series"],
                    (int?)e["season"], (int?)e["episode"], (bool)e["paused"]!, (double)e["position"]!, (double)e["duration"]!, (int)e["volume"]!, (bool)e["muted"]!);
                Assert(target == wanted, $"{target} ≠ {wanted}");
            }
            return Task.CompletedTask;
        });

        await Test("Télécommande : capacités déclarées à Jellyfin (vidéo, commandes de Mira, contrôle à distance)", () =>
        {
            var body = RemoteControl.Capabilities();
            Assert(body["PlayableMediaTypes"]!.AsArray().Select(x => (string?)x).SequenceEqual(["Video"]), body.ToJsonString());
            Assert((bool)body["SupportsMediaControl"]! && body["SupportedCommands"]!.AsArray().Count == RemoteControl.Commands.Length, body.ToJsonString());
            // Every command declared is one Read takes: Jellyfin's pages show only those buttons.
            foreach (var name in RemoteControl.Commands)
            {
                using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { Name = name, Arguments = new Dictionary<string, string> { ["Volume"] = "50", ["Text"] = "x" } }));
                Assert(RemoteControl.Read("GeneralCommand", data.RootElement) is not null, name);
            }
            var muted = new RemoteCommand(RemoteAction.ToggleMute);
            Assert(RemoteControl.Muted(muted, true) == false && RemoteControl.Muted(muted, false) == true && RemoteControl.Muted(new(RemoteAction.Mute), true) == true
                && RemoteControl.Muted(new(RemoteAction.Unmute), true) == false && RemoteControl.Muted(new(RemoteAction.Pause), true) is null, "Muet");
            return Task.CompletedTask;
        });

        await Test("Télécommande : capacités refusées (Jellyfin qui redémarre) redites jusqu’à être prises, plus après", async () =>
        {
            var posts = 0;
            using var client = new JellyfinClient(new("http://localhost:8096/", "user-1", "Admin", "secret", "device"), new Handler(request =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/Sessions/Capabilities/Full", request.RequestUri.ToString());
                posts++;
                if (posts == 2) throw new HttpRequestException("connection refused");
                return Task.FromResult(new HttpResponseMessage(posts < 4 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent));
            }));
            Assert(await client.TellCapabilitiesWhileOpenAsync(CancellationToken.None, [TimeSpan.Zero]), "pas prises");
            Assert(posts == 4, $"{posts} envois");
            // The WebSocket closed: no more tries, and nothing thrown.
            posts = -100;
            using var closed = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert(!await client.TellCapabilitiesWhileOpenAsync(closed.Token, [TimeSpan.FromMilliseconds(20)]), "prises par un Jellyfin qui refuse");
            Assert(posts > -100 && posts < -70, $"{posts + 100} envois en 200 ms");
            Assert(JellyfinClient.CapabilityRetries[0] >= TimeSpan.FromSeconds(1) && JellyfinClient.CapabilityRetries[^1] <= TimeSpan.FromMinutes(2), "attentes");
        });

        await Test("Télécommande : capacités envoyées à l’ouverture du WebSocket, commandes reçues par lui", async () =>
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
            string? told = null; var toldAt = new TaskCompletionSource();
            var serving = Task.Run(async () =>
            {
                var context = await server.GetContextAsync();
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                // The capabilities, posted once the socket is open.
                var post = await server.GetContextAsync();
                using (var reader = new StreamReader(post.Request.InputStream)) told = post.Request.HttpMethod + " " + post.Request.RawUrl + " " + await reader.ReadToEndAsync();
                post.Response.StatusCode = 204; post.Response.Close(); toldAt.TrySetResult();
                foreach (var message in new[]
                {
                    """{"MessageId":"1","Data":{"Command":"Pause","ControllingUserId":"u"},"MessageType":"Playstate"}""",
                    """{"MessageId":"2","Data":{"Name":"GoHome","Arguments":{}},"MessageType":"GeneralCommand"}""",
                    """{"MessageId":"3","Data":{"ItemIds":["90892981f28a0ccb86a6e55a3a0039c6"],"StartPositionTicks":0,"PlayCommand":"PlayNow"},"MessageType":"Play"}""",
                })
                    await socket.SendAsync(Encoding.UTF8.GetBytes(message), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
                await Task.Delay(1000);
            });
            using var client = new JellyfinClient(new Connection($"http://127.0.0.1:{port}/", "user-1", "Alice", "jeton", "device-1"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var heard = new List<RemoteCommand>();
            var listening = client.ListenAsync(() => { }, stop.Token, remote: command => { lock (heard) { heard.Add(command); if (heard.Count == 2) stop.Cancel(); } });
            try { await listening; } catch (OperationCanceledException) { }
            await serving.WaitAsync(TimeSpan.FromSeconds(5)); server.Stop();
            Assert(told is { } body && body.StartsWith("POST /Sessions/Capabilities/Full ") && body.Contains("\"SupportsMediaControl\":true"), "Capacités : " + told);
            Assert(heard.Count == 2 && heard[0].Action == RemoteAction.Pause && heard[1] is { Action: RemoteAction.Play, ItemId: "90892981f28a0ccb86a6e55a3a0039c6", Start: 0 },
                "Commandes : " + string.Join(", ", heard));
        });

        if (Environment.GetEnvironmentVariable("MIRA_REMOTE_JELLYFIN") is { Length: > 0 } real)
            await Test("Télécommande : contre un vrai Jellyfin (Lire sur, pause, saut, volume, message, arrêt)", () => RealAsync(real));
    }

    /// <summary>Two sessions of one account: « this Mira » listens, the other steers it, as the iPhone does.</summary>
    private static async Task RealAsync(string settings)
    {
        var parts = settings.Split(' ', 3);
        var server = parts[0].TrimEnd('/') + "/";
        async Task<JellyfinClient> SignInAsync(string device)
        {
            using var anonymous = new JellyfinClient(new Connection(server, "", "", "", device));
            return new JellyfinClient(await anonymous.LoginAsync(parts[1], parts[2]));
        }
        using var player = await SignInAsync("mira-remote-player");
        using var phone = await SignInAsync("mira-remote-phone");
        var heard = new List<RemoteCommand>(); var open = new TaskCompletionSource();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var listening = player.ListenAsync(() => { }, stop.Token, listening: x => { if (x) open.TrySetResult(); }, remote: command => { lock (heard) heard.Add(command); });
        await open.Task.WaitAsync(TimeSpan.FromSeconds(15));
        RemoteTarget? target = null;
        for (var i = 0; i < 20 && target is null; i++) { target = (await phone.RemoteTargetsAsync()).FirstOrDefault(x => x.Device.Length > 0 && x.SessionId.Length > 0); if (target is null) await Task.Delay(250); }
        Assert(target is not null, "Mira n’est pas proposé comme appareil où lire");
        Assert((await player.RemoteTargetsAsync()).All(x => x.SessionId != target!.SessionId), "Un appareil se propose lui-même");
        var movie = (await phone.BrowseAsync()).Items.First(x => x.Type is "Movie" or "Episode");
        await phone.RemotePlayAsync(target!.SessionId, movie.Id, TimeSpan.FromSeconds(12).Ticks);
        await phone.RemoteStateAsync(target.SessionId, "Pause");
        await phone.RemoteStateAsync(target.SessionId, "Seek", TimeSpan.FromSeconds(5).Ticks);
        await phone.RemoteCommandAsync(target.SessionId, "SetVolume", new Dictionary<string, string> { ["Volume"] = "40" });
        await phone.RemoteCommandAsync(target.SessionId, "DisplayMessage", new Dictionary<string, string> { ["Header"] = "Mira", ["Text"] = "Salut", ["TimeoutMs"] = "4000" });
        await phone.RemoteStateAsync(target.SessionId, "Stop");
        // « Arrêter » of « En ce moment »: the same Playstate, by the way Mira for Windows and Mac send it.
        Assert(await phone.StopViewerAsync(target.SessionId), "Arrêter refusé");
        for (var i = 0; i < 40; i++) { lock (heard) if (heard.Count >= 7) break; await Task.Delay(100); }
        stop.Cancel(); try { await listening; } catch (OperationCanceledException) { }
        var actions = heard.Select(x => x.Action).ToList();
        Assert(actions.SequenceEqual([RemoteAction.Play, RemoteAction.Pause, RemoteAction.Seek, RemoteAction.SetVolume, RemoteAction.Message, RemoteAction.Stop, RemoteAction.Stop]), "Reçu : " + string.Join(", ", heard));
        Assert(heard[0].ItemId == movie.Id && heard[0].Start == 12 && heard[2].Position == 5 && heard[3].Volume == 40 && RemoteControl.Said(heard[4]) == "Mira · Salut", "Valeurs : " + string.Join(", ", heard));
    }

    private static JsonNode Fixture()
    {
        var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "remote.json")))!;
    }
}
