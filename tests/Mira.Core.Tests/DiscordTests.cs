using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using Mira.Core.Discord;
using static TestSupport;

/// <summary>Réglages → Discord: the card of each playback (DiscordPresence), its guard, and the Discord app's pipe (DiscordIpc, DiscordStatus).</summary>
internal static class DiscordTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Discord : la carte de chaque lecture, telle que les amis la voient (tests/fixtures/discord.json)", () =>
        {
            var fixture = Fixture();
            var now = DateTimeOffset.Parse((string)fixture["now"]!, System.Globalization.CultureInfo.InvariantCulture);
            var was = Lang.Code;
            try
            {
                Lang.Use(Lang.English);
                foreach (var row in fixture["cards"]!.AsArray())
                {
                    var name = (string)row!["name"]!;
                    var settings = Settings(row["settings"]);
                    var item = fixture["items"]![(string)row["item"]!].Deserialize<MediaItem>(Json.Options)!;
                    var title = fixture["titles"]![(string)row["title"]!].Deserialize<DiscordTitle>(Json.Options)!;
                    var paused = (bool?)row["paused"] ?? false;
                    var playback = new DiscordPlayback(item, title, paused, (double)row["position"]!, (double)row["duration"]!, (double?)row["rate"] ?? 1,
                        (bool?)row["inGroup"] ?? false, (int?)row["friends"] ?? 0, (string?)row["partyLink"], (bool?)row["guest"] ?? false, (bool?)row["trailer"] ?? false, (bool?)row["hideSpoilers"] ?? false);
                    var card = DiscordPresence.Card(settings, playback, now, TimeSpan.FromMinutes((double?)row["pausedMinutes"] ?? 0), (string?)row["app"] ?? "Windows");
                    var activity = card?.Activity();
                    if (activity?["timestamps"] is JsonObject times)
                        foreach (var key in new[] { "start", "end" }) times[key] = (times[key]!.GetValue<long>() - now.ToUnixTimeMilliseconds()) / 1000;
                    Assert(JsonNode.DeepEquals(activity, row["activity"]), $"{name} :\n  {activity?.ToJsonString() ?? "rien"}\n  attendu {row["activity"]?.ToJsonString() ?? "rien"}");
                    if (activity is not null) Assert(DiscordPresence.Leaks(activity, ["192.168.1.42", "maison-pc", "Alex"], DiscordPresence.PartyButton(playback.PartyLink)?.Url).Count == 0, $"{name} : rien du serveur");
                }
                Assert(DiscordPresence.Browsing(new() { Enabled = true }) is null, "rien quand rien ne joue");
                var browsing = DiscordPresence.Browsing(new() { Enabled = true, ShowBrowsing = true, SecondButton = DiscordButton.Tmdb })!;
                Assert(browsing.Details == "Browsing the library" && browsing.Buttons.Count == 1 && browsing.Start is null, "« Browsing the library »");
            }
            finally { Lang.Use(was); }
            foreach (var row in fixture["mature"]!.AsArray()) Assert(DiscordPresence.Mature((string?)row![0]) == (bool)row[1]!, $"Pour adultes : {row[0]}");
            foreach (var row in fixture["posters"]!.AsArray()) Assert(DiscordPresence.Poster((string?)row![0]) == (string?)row[1], $"Affiche : {row[0]} → {DiscordPresence.Poster((string?)row[0])}");
            Assert(new DiscordSettings().Enabled, "allumé par défaut");
            foreach (var (json, on) in new[] { ("{\"enabled\":false}", true), ("{\"enabled\":false,\"offered\":true}", false), ("{\"enabled\":true,\"offered\":true}", true), ("{\"enabled\":false,\"defaults\":1}", false) })
            {
                var read = JsonSerializer.Deserialize<DiscordSettings>(json, Json.Options)!; read.Upgrade();
                Assert(read.Enabled == on && read.Defaults == 1, $"Réglages d'avant : {json}");
            }
            foreach (var row in fixture["party"]!.AsArray()) Assert(DiscordPresence.PartyButton((string?)row![0]) is not null == (bool)row[1]!, $"Party : {row[0]}");
            return Task.CompletedTask;
        });

        await Test("Discord : la garde refuse une carte qui nomme le serveur, Tailscale, une adresse privée, le compte ou le PC", () =>
        {
            JsonObject Card(string url) => new() { ["details"] = "Arcane", ["assets"] = new JsonObject { ["large_image"] = url } };
            foreach (var bad in new[] { "http://192.168.1.42:8096/Items/1/Images/Primary", "https://maison-pc.tail1234.ts.net/Items/1", "http://100.88.12.34:8096/x", "http://10.0.0.2/x", "http://localhost:8096/x",
                         "https://jellyfin.example.com/Items/1", "https://image.tmdb.org/t/p/w500/MAISON-PC.jpg", "http://[fd7a:115c:a1e0::1]:8096/x" })
                Assert(DiscordPresence.Leaks(Card(bad), ["jellyfin.example.com", "maison-pc", "Alex"]).Count > 0, $"refusée : {bad}");
            Assert(DiscordPresence.Leaks(Card("https://image.tmdb.org/t/p/w500/abc.jpg"), ["jellyfin.example.com", "maison-pc", "Alex"]).Count == 0, "affiche de TMDB");
            // A title that holds the account's name is no address: it goes.
            Assert(DiscordPresence.Leaks(new JsonObject { ["details"] = "Alex Rider" }, ["Alex"]).Count == 0, "un titre n’est pas une adresse");
            var party = "https://maison-pc.tail1234.ts.net/Mira/party/AbCdEfGhIjKlMnOpQrStUv";
            var withParty = new JsonObject { ["details"] = "Arcane", ["buttons"] = new JsonArray(new JsonObject { ["label"] = "Join the party", ["url"] = party }) };
            Assert(DiscordPresence.Leaks(withParty, ["maison-pc"], party).Count == 0 && DiscordPresence.Leaks(withParty, ["maison-pc"]).Count > 0, "le lien de party seulement quand il est permis");
            return Task.CompletedTask;
        });

        await Test("Discord : la poignée de main, la carte, sa mise à jour, sa fin, et rien du tout tant que c’est coupé", async () =>
        {
            await using var discord = new FakeDiscord();
            var was = Environment.GetEnvironmentVariable("MIRA_DISCORD_IPC");
            Environment.SetEnvironmentVariable("MIRA_DISCORD_IPC", discord.Address);
            try
            {
                Assert(DiscordIpc.Available(), "Discord trouvé sans s’y connecter");
                var settings = new DiscordSettings { Enabled = false };
                await using var status = new DiscordStatus("Windows", retry: TimeSpan.FromMilliseconds(200), window: TimeSpan.FromSeconds(1)) { Private = () => ["maison-pc"] };
                async Task<JsonNode> Next(TimeSpan? wait = null)
                {
                    try { return await discord.NextAsync(wait); }
                    catch (Exception ex) { throw new Exception($"{ex.Message} · état {status.State}, connexions {discord.Connections}, erreur {status.Error}, IPC {DiscordIpc.LastFailure}"); }
                }
                var episode = new MediaItem { Id = "ep-3", Type = "Episode", Name = "Pilot", SeriesName = "Arcane", SeriesId = "s", IndexNumber = 1, ParentIndexNumber = 1 };
                var playing = new DiscordPlayback(episode, null, false, 10, 2400, 1);
                var now = DateTimeOffset.UtcNow;
                status.Update(settings, playing, now);
                await Task.Delay(400);
                Assert(discord.Connections == 0 && status.State == DiscordState.Off, "coupé : Discord jamais joint");

                settings.Enabled = true;
                status.Update(settings, playing, now);
                var first = await Next();
                Assert(discord.ClientId == DiscordPresence.ApplicationId, $"application : {discord.ClientId}");
                Assert((string?)first["cmd"] == "SET_ACTIVITY" && (int?)first["args"]!["pid"] == Environment.ProcessId && (string?)first["args"]!["activity"]!["details"] == "Arcane", first.ToJsonString());
                await Until(() => status.State == DiscordState.Live, "en direct");

                // The same playback a second later: nothing new is sent.
                status.Update(settings, playing with { Position = 11 }, now.AddSeconds(1));
                // A pause: sent at once.
                status.Update(settings, playing with { Paused = true, Position = 11 }, now.AddSeconds(1));
                var paused = await Next();
                Assert(((string?)paused["args"]!["activity"]!["state"])!.StartsWith("Paused", StringComparison.Ordinal) || ((string?)paused["args"]!["activity"]!["state"])!.StartsWith("En pause", StringComparison.Ordinal), paused.ToJsonString());
                Assert(discord.Received == 2, $"une seule carte par changement : {discord.Received}");

                // Paused for now, then nothing plays: cleared once.
                status.HiddenForNow = true;
                status.Update(settings, playing with { Paused = true }, now.AddSeconds(2));
                var cleared = await Next();
                Assert(cleared["args"]!["activity"] is null, cleared.ToJsonString());
                await Until(() => status.State == DiscordState.HiddenForNow, "masqué pour l’instant");
                status.HiddenForNow = false;

                // Discord gone, then back: the card comes back by itself.
                await discord.DropAsync();
                status.Update(settings, playing with { Position = 100 }, now.AddSeconds(100));
                var again = await Next(TimeSpan.FromSeconds(5));
                Assert((string?)again["args"]!["activity"]!["details"] == "Arcane" && discord.Connections == 2, $"reconnecté : {discord.Connections}");

                // More than 5 changes in the window: the latest one alone goes once the window allows.
                for (var i = 0; i < 8; i++) status.Update(settings, playing with { Position = 200 + i * 100 }, now.AddSeconds(200));
                var expected = (now.AddSeconds(200).ToUnixTimeSeconds() - 900) * 1000;
                long? Start() => discord.Last?["args"]?["activity"]?["timestamps"]?["start"]?.GetValue<long>();
                await Until(() => Start() == expected, "la dernière position, une fois la fenêtre passée");
                var last = discord.Last!;
                var start = Start();
                Assert(start == (now.AddSeconds(200).ToUnixTimeSeconds() - 900) * 1000, $"la dernière position : {last.ToJsonString()}");

                // Turned off: cleared, then the pipe is closed.
                settings.Enabled = false;
                status.Update(settings, playing, now.AddSeconds(300));
                await Until(() => discord.Last?["args"]?["activity"] is null && status.State == DiscordState.Off, "coupé : carte effacée");
            }
            finally { Environment.SetEnvironmentVariable("MIRA_DISCORD_IPC", was); }
        });
    }

    private static DiscordSettings Settings(JsonNode? changes)
    {
        var settings = new JsonObject { ["enabled"] = true };
        if (changes is JsonObject o) foreach (var (key, value) in o) settings[key] = value?.DeepClone();
        return settings.Deserialize<DiscordSettings>(Json.Options)!;
    }

    private static async Task Until(Func<bool> done, string what)
    {
        for (var i = 0; i < 160 && !done(); i++) await Task.Delay(50);
        Assert(done(), what);
    }

    private static JsonNode Fixture()
    {
        var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "discord.json")))!;
    }

    /// <summary>
    /// The Discord app's pipe as the checks see it: a Unix socket (a named pipe on Windows) that answers the handshake
    /// with READY, pings once, and keeps each SET_ACTIVITY it is given.
    /// </summary>
    private sealed class FakeDiscord : IAsyncDisposable
    {
        private readonly CancellationTokenSource _life = new();
        private readonly System.Threading.Channels.Channel<JsonNode> _frames = System.Threading.Channels.Channel.CreateUnbounded<JsonNode>();
        private readonly Socket? _listener;
        private readonly string _folder = "";
        private Stream? _client;
        private readonly Task _accepting;
        public string Address { get; }
        public int Connections;
        public int Received;
        public string? ClientId;
        public JsonNode? Last;

        public FakeDiscord()
        {
            if (OperatingSystem.IsWindows()) Address = "mira-discord-check-" + Guid.NewGuid().ToString("N");
            else
            {
                _folder = Directory.CreateTempSubdirectory("mira-discord-").FullName;
                Address = Path.Combine(_folder, "discord-ipc-0");
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(Address)); _listener.Listen(4);
            }
            _accepting = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_life.IsCancellationRequested)
            {
                try
                {
                    Stream stream;
                    if (OperatingSystem.IsWindows())
                    {
                        var pipe = new NamedPipeServerStream(Address, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await pipe.WaitForConnectionAsync(_life.Token); stream = pipe;
                    }
                    else stream = new NetworkStream(await _listener!.AcceptAsync(_life.Token), ownsSocket: true);
                    _client = stream; Interlocked.Increment(ref Connections);
                    _ = Task.Run(() => ServeAsync(stream));
                }
                catch (Exception) when (_life.IsCancellationRequested) { return; }
            }
        }

        private async Task ServeAsync(Stream stream)
        {
            try
            {
                var (op, hello) = await ReadAsync(stream);
                if (op != 0) return;
                ClientId = (string?)hello["client_id"];
                await WriteAsync(stream, 1, new JsonObject { ["cmd"] = "DISPATCH", ["evt"] = "READY", ["data"] = new JsonObject { ["v"] = 1 } });
                await WriteAsync(stream, 3, new JsonObject { ["hello"] = 1 });
                while (true)
                {
                    var (kind, body) = await ReadAsync(stream);
                    if (kind == 4) continue; // the answer to the ping
                    Interlocked.Increment(ref Received); Last = body;
                    await WriteAsync(stream, 1, new JsonObject { ["cmd"] = "SET_ACTIVITY", ["data"] = body["args"]?["activity"]?.DeepClone(), ["evt"] = null, ["nonce"] = body["nonce"]?.DeepClone() });
                    _frames.Writer.TryWrite(body);
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or OperationCanceledException or SocketException) { }
        }

        public async Task<JsonNode> NextAsync(TimeSpan? wait = null)
        {
            using var time = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(3));
            try { return await _frames.Reader.ReadAsync(time.Token); }
            catch (OperationCanceledException) { throw new Exception("Discord n’a rien reçu"); }
        }

        /// <summary>Discord quits: the pipe breaks.</summary>
        public async Task DropAsync() { if (_client is { } client) await client.DisposeAsync(); }

        private static async Task<(int, JsonNode)> ReadAsync(Stream stream)
        {
            var header = new byte[8];
            await stream.ReadExactlyAsync(header);
            var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
            await stream.ReadExactlyAsync(body);
            return (BinaryPrimitives.ReadInt32LittleEndian(header), JsonNode.Parse(body)!);
        }

        private static async Task WriteAsync(Stream stream, int op, JsonNode body)
        {
            var json = Encoding.UTF8.GetBytes(body.ToJsonString());
            var frame = new byte[8 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, op); BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
            json.CopyTo(frame, 8);
            await stream.WriteAsync(frame); await stream.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _life.CancelAsync();
            _listener?.Dispose();
            if (_client is { } client) await client.DisposeAsync();
            try { await _accepting; } catch (Exception) { }
            if (_folder.Length > 0) try { Directory.Delete(_folder, true); } catch (IOException) { }
        }
    }
}
