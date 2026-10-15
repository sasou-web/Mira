using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Desktop.Services;
using static TestSupport;

/// <summary>Playback reports to Jellyfin: durable queue, outages, end of playback, rhythms. Free of WPF: also run outside Windows.</summary>
internal static class SyncTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
        LibraryStore Store(string name) => new(testRoot, name);
        await Test("File durable : démarrage et arrêt préservés, progression coalescée", () =>
        {
            var store = Store("outbox"); store.Enqueue("start", Report());
            for (var i = 1; i <= 100; i++) store.Enqueue("progress", Report(i));
            store.Enqueue("stop", Report(101)); Assert(store.PendingCount == 3, "Unexpected number of queued reports");
            var reopened = Store("outbox"); var kinds = new List<string>(); var positions = new List<long>();
            while (reopened.Peek() is { } entry) { kinds.Add(entry.Kind); positions.Add(entry.Report.PositionTicks); reopened.Acknowledge(entry.Id); }
            Assert(kinds.SequenceEqual(new[] { "start", "progress", "stop" }), "Ordering lost");
            Assert(positions.SequenceEqual(new long[] { 0, 100, 101 }), "Latest position lost"); return Task.CompletedTask;
        });
        await Test("Une réponse ancienne ne supprime pas une progression plus récente", () =>
        {
            var store = Store("inflight"); store.Enqueue("progress", Report(10)); var old = store.Peek()!;
            store.Enqueue("progress", Report(20)); store.Acknowledge(old.Id);
            Assert(store.Peek()?.Report.PositionTicks == 20, "In-flight acknowledgment deleted fresh progress"); return Task.CompletedTask;
        });
        await Test("Reprise locale et séparation des comptes", () =>
        {
            var store = Store("profile-a"); store.Save("items", new[] { new MediaItem { Id = "item-1", Name = "Film" } }); store.Enqueue("progress", Report(300));
            var items = store.Load<MediaItem[]>("items")!; store.ApplyLocalProgress(items); Assert(items[0].UserData.PlaybackPositionTicks == 300, "Local resume not applied");
            Assert(Store("profile-b").Load<MediaItem[]>("items") is null, "Cross-profile cache leak"); return Task.CompletedTask;
        });
        await Test("Un serveur lent ne bloque pas la sauvegarde de progression", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(async _ => { await gate.Task; return new(HttpStatusCode.ServiceUnavailable); }));
            var store = Store("slow-server"); await using var sync = new SyncService(client, store);
            var watch = Stopwatch.StartNew(); await sync.RecordAsync("start", Report()); await sync.RecordAsync("progress", Report(55)); await sync.RecordAsync("stop", Report(55));
            Assert(watch.Elapsed < TimeSpan.FromSeconds(1), "Saving progress waited for network"); Assert(store.PendingCount == 3, "Reports not durable before response");
            gate.SetResult(); await WaitUntil(() => sync.Error is not null); Assert(store.PendingCount == 3, "Failed report was deleted");
        });
        await Test("Coupure puis reconnexion : livraison ordonnée sans perte", async () =>
        {
            var online = false; var received = new List<string>();
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(request =>
            { if (!online) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); received.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }));
            var store = Store("recovery"); await using var sync = new SyncService(client, store);
            await sync.RecordAsync("start", Report()); await sync.RecordAsync("progress", Report(88)); await sync.RecordAsync("stop", Report(90));
            Assert(store.PendingCount == 3, "Offline reports missing"); online = true; await sync.FlushAsync(); await WaitUntil(() => store.PendingCount == 0);
            Assert(received.SequenceEqual(new[] { "/Sessions/Playing", "/Sessions/Playing/Progress", "/Sessions/Playing/Stopped" }), "Protocol ordering incorrect");
        });
        await Test("Fin de lecture locale : au-delà de 90 %, titre vu et reprise à zéro", () =>
        {
            var store = Store("finished"); var runtime = 1_000 * TimeSpan.TicksPerSecond;
            store.Enqueue("stop", Report(runtime) with { ItemId = "done" }); store.Enqueue("stop", Report(runtime / 2) with { ItemId = "half" });
            var items = new[] { new MediaItem { Id = "done", RunTimeTicks = runtime }, new MediaItem { Id = "half", RunTimeTicks = runtime } };
            store.ApplyLocalProgress(items);
            Assert(items[0].UserData.Played && items[0].UserData.PlaybackPositionTicks == 0 && items[0].Progress == 0, "A title stopped at the credits resumes at the credits");
            Assert(!items[1].UserData.Played && items[1].UserData.PlaybackPositionTicks == runtime / 2, "Partial progress was lost");
            return Task.CompletedTask;
        });
        await Test("Générique terminé avant 90 % : vu et reprise effacée, même après redémarrage hors ligne", () =>
        {
            var store = Store("completed-outro"); var runtime = 1_000 * TimeSpan.TicksPerSecond;
            store.Enqueue("start", Report()); store.Enqueue("complete", Report(runtime * 83 / 100));
            var reopened = Store("completed-outro");
            var item = new MediaItem { Id = "item-1", RunTimeTicks = runtime };
            reopened.ApplyLocalProgress([item]);
            Assert(item.UserData.Played && item.Progress == 0, "Credits completion below threshold was lost");
            var kinds = new List<string>();
            while (reopened.Peek() is { } entry) { kinds.Add(entry.Kind); reopened.Acknowledge(entry.Id); }
            Assert(kinds.SequenceEqual(new[] { "start", "stop", "watched" }), "Completion must atomically persist stop then watched");
            reopened.ForgetProgress(item.Id);
            var reset = item with { UserData = new() }; reopened.ApplyLocalProgress([reset]);
            Assert(!reset.UserData.Played && reset.Progress == 0, "Manual unwatched retains a local completion override");
            return Task.CompletedTask;
        });
        await Test("Marquage vu : échec puis reprise sans renvoyer l’arrêt déjà livré", async () =>
        {
            var online = false; var stops = 0; var marks = 0;
            using var client = new JellyfinClient(new("http://localhost/jellyfin/", "u", "Alice", "token", "device"), new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("Stopped")) { Interlocked.Increment(ref stops); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
                Assert(request.Method == HttpMethod.Post && request.RequestUri.PathAndQuery == "/jellyfin/UserPlayedItems/item-1?userId=u", "Incorrect watched API request");
                if (online) Interlocked.Increment(ref marks);
                return Task.FromResult(new HttpResponseMessage(online ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable));
            }));
            var store = Store("watched-retry"); await using var sync = new SyncService(client, store);
            await sync.RecordAsync("complete", Report(830)); await WaitUntil(() => sync.Error is not null);
            Assert(stops == 1 && store.PendingCount == 1 && store.Peek()?.Kind == "watched", "Successful stop must not be retried");
            var item = new MediaItem { Id = "item-1", RunTimeTicks = 1000 }; store.ApplyLocalProgress([item]);
            Assert(item.UserData.Played && item.Progress == 0, "Waiting for watched request lost the local mark");
            online = true; await sync.FlushAsync(); await WaitUntil(() => store.PendingCount == 0);
            Assert(stops == 1 && marks == 1, "Watched retry duplicated the stop or lost the mark");
        });
        await Test("Synchronisation : un rapport refusé définitivement ne bloque pas la file", async () =>
        {
            var received = new List<string>();
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(request =>
            {
                received.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(received.Count == 1 ? HttpStatusCode.NotFound : HttpStatusCode.NoContent));
            }));
            var store = Store("permanent-refusal"); await using var sync = new SyncService(client, store);
            await sync.RecordAsync("start", Report()); await sync.RecordAsync("stop", Report(9));
            await WaitUntil(() => store.PendingCount == 0);
            Assert(received.Contains("/Sessions/Playing/Stopped") && sync.Discarded == 1 && sync.PendingCount == 0, "A 404 report blocked later deliveries");
        });
        await Test("Synchronisation : l’arrêt final part avant la fermeture", async () =>
        {
            var received = new List<string>();
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(async request =>
            { await Task.Delay(120); lock (received) received.Add(request.RequestUri!.AbsolutePath); return new HttpResponseMessage(HttpStatusCode.NoContent); }));
            var store = Store("final-stop"); var sync = new SyncService(client, store);
            await sync.RecordAsync("start", Report()); await sync.RecordAsync("stop", Report(12));
            await sync.DisposeAsync();
            Assert(received.Contains("/Sessions/Playing/Stopped") && store.PendingCount == 0, "Closing cancelled the stop report");
        });
        await Test("Stockage local en panne : rien ne remonte au lecteur, l’erreur est affichée et la fermeture ne bloque pas", async () =>
        {
            var folder = Path.Combine(testRoot, "broken-storage"); var store = new LibraryStore(folder, "panne");
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
            var sync = new SyncService(client, store);
            // Then the database becomes unopenable (a folder where the file was): every SQLite call fails from now on.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var database = Directory.GetFiles(folder, "library-*.db").Single();
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.CreateDirectory(database);
            await new SyncService(client, store).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await sync.RecordAsync("start", Report()).WaitAsync(TimeSpan.FromSeconds(5));
            await sync.RecordAsync("progress", Report(40)).WaitAsync(TimeSpan.FromSeconds(5));
            await sync.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert(sync.Error == SyncService.StorageError, "Storage failure not reported: " + sync.Error);
            // The flush lock must be released even though counting failed, or closing Mira waits for it forever.
            await sync.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        });
        await Test("Rythmes : secours 45 s sans WebSocket et 5 min avec, progression toutes les 10 s en lecture, rien en pause", () =>
        {
            var now = DateTimeOffset.UtcNow;
            Assert(Cadence.RefreshDue(false, now.AddSeconds(-45), now) && Cadence.RefreshDue(false, now.AddSeconds(-42), now) && !Cadence.RefreshDue(false, now.AddSeconds(-30), now), "Without the WebSocket");
            Assert(!Cadence.RefreshDue(true, now.AddSeconds(-45), now) && !Cadence.RefreshDue(true, now.AddMinutes(-4), now) && Cadence.RefreshDue(true, now.AddMinutes(-5), now), "With the WebSocket");
            Assert(Cadence.RefreshDue(true, DateTimeOffset.MinValue, now), "Never refreshed");
            Assert(Cadence.ProgressDue(false, false, now.AddSeconds(-10), now) && !Cadence.ProgressDue(false, false, now.AddSeconds(-9), now), "Every 10 s while it plays");
            Assert(!Cadence.ProgressDue(false, true, now.AddMinutes(-10), now), "Reported while it stays paused");
            Assert(Cadence.ProgressDue(true, true, now, now) && Cadence.ProgressDue(true, false, now, now), "Pause and resume not reported at once");
            Assert(Cadence.ProgressDue(false, true, DateTimeOffset.MinValue, now), "A seek while paused not reported");
            Assert(new[] { 0, 1, 2, 3, 4, 5, 50 }.Select(x => Cadence.RetryDelay(x).TotalSeconds).SequenceEqual(new double[] { 3, 3, 6, 12, 24, 30, 30 }), "Retry delays");
            return Task.CompletedTask;
        });
        await Test("Ligne d’état : « Hors connexion » gardé jusqu’à ce que Jellyfin réponde de nouveau", () =>
        {
            var failed = DateTimeOffset.Now;
            var (text, warning) = SyncStatus.Describe(0, null, null, failed);
            Assert(text == "○  Hors connexion · cache local" && warning, text);
            // The retry loop's own updates (an older delivery, its error) leave it in place.
            (text, _) = SyncStatus.Describe(0, null, failed.AddMinutes(-1), failed); Assert(text == "○  Hors connexion · cache local", text);
            (text, _) = SyncStatus.Describe(2, "En attente de Jellyfin", null, failed); Assert(text == "○  Hors connexion · 2 envois en attente", text);
            // A report delivered after the failure: Jellyfin answers again.
            (text, warning) = SyncStatus.Describe(0, null, failed.AddSeconds(5), failed); Assert(text.StartsWith("●  Synchronisé à ") && !warning, text);
            (text, warning) = SyncStatus.Describe(0, null, null, null); Assert(text == "●  Connecté à Jellyfin" && !warning, text);
            (text, warning) = SyncStatus.Describe(1, SyncService.StorageError, null, null); Assert(text == "◌  1 envoi en attente · " + SyncService.StorageError && warning, text);
            (text, warning) = SyncStatus.Describe(0, SyncService.StorageError, null, null); Assert(text == "◌  " + SyncService.StorageError && warning, text);
            return Task.CompletedTask;
        });
        await Test("Synchronisation : un envoi échoué est réessayé seul, et aussitôt quand Jellyfin répond de nouveau", async () =>
        {
            var online = false;
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(_ =>
                Task.FromResult(new HttpResponseMessage(Volatile.Read(ref online) ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable))));
            var store = Store("retry-signal"); await using var sync = new SyncService(client, store);
            await sync.RecordAsync("start", Report()); await WaitUntil(() => sync.Error is not null);
            // Nothing new recorded: the loop tries again by itself, 3 s later.
            Volatile.Write(ref online, true); await WaitUntil(() => store.PendingCount == 0, 6);
            // Offline again, a failure, then Jellyfin answers: Wake sends at once, without waiting for the next try.
            Volatile.Write(ref online, false); await sync.RecordAsync("progress", Report(5)); await WaitUntil(() => sync.Error is not null && sync.PendingCount == 1);
            Volatile.Write(ref online, true); var watch = Stopwatch.StartNew(); sync.Wake();
            await WaitUntil(() => store.PendingCount == 0, 6);
            Assert(watch.Elapsed < TimeSpan.FromSeconds(1.5), "Wake did not send at once: " + watch.Elapsed);
        });
        await Test("Tes choix gardés sur ton Jellyfin : ce qui change ici, le plus récent l'emporte, comme Mira web (tests/fixtures/choices.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "choices.json")))!;
            foreach (var row in fixture["capture"]!.AsArray())
            {
                var got = AccountChoices.Capture(row!["doc"], (JsonObject)row["local"]!, row["now"]!.GetValue<long>());
                Assert(AccountChoices.Same(got, row["expected"]), $"{row["name"]}: {got.ToJsonString()}");
            }
            foreach (var row in fixture["merge"]!.AsArray())
            {
                var got = AccountChoices.Merge(row!["doc"], row["remote"]);
                Assert(AccountChoices.Same(got, row["expected"]), $"{row["name"]}: {got.ToJsonString()}");
                Assert(AccountChoices.Differs(got, row["remote"]) == row["differs"]!.GetValue<bool>(), $"{row["name"]}: Jellyfin told or not");
            }
            foreach (var row in fixture["apply"]!.AsArray())
            {
                var got = AccountChoices.Apply(row!["doc"]); var expected = row["expected"]!;
                Assert(AccountChoices.Same(got["settings"], expected["settings"]) && AccountChoices.Same(got["tracks"], expected["tracks"]) && AccountChoices.Same(got["hidden"], expected["hidden"]), $"{row["name"]}: {got.ToJsonString()}");
                Assert(((JsonObject)got["tracks"]!).Select(x => x.Key).SequenceEqual(expected["order"]!.AsArray().Select(x => x!.GetValue<string>())), "Tracks out of order: " + got["tracks"]!.ToJsonString());
            }
            // The newest only, by name on a tie: the same entries go on every device.
            var many = new JsonObject();
            for (var i = 0; i < AccountChoices.TrackLimit + 5; i++) many["s" + i] = new JsonObject { ["audio"] = "jpn", ["subtitles"] = false, ["at"] = i == 0 ? 1000 : i };
            var kept = (JsonObject)AccountChoices.Merge(new JsonObject { ["seen"] = true }, new JsonObject { ["tracks"] = many })["tracks"]!;
            Assert(kept.Count == AccountChoices.TrackLimit && kept.ContainsKey("s0") && kept.ContainsKey("s6") && !kept.ContainsKey("s5") && !kept.ContainsKey("s1"), "Tracks kept: " + kept.Count);
            var tied = new JsonObject();
            for (var i = 0; i <= AccountChoices.HiddenLimit; i++) tied[$"m{i:000}"] = new JsonObject { ["hidden"] = true, ["at"] = 5 };
            var hidden = (JsonObject)AccountChoices.Merge(null, new JsonObject { ["hidden"] = tied })["hidden"]!;
            Assert(hidden.ContainsKey("m000") && !hidden.ContainsKey($"m{AccountChoices.HiddenLimit}"), "Hidden kept on a tie");
            // A series' tracks, as Windows and Mac keep them, and back.
            var memory = new TrackMemory("jpn", "Japonais", true, "fre", "Panneaux", true);
            Assert(AccountChoices.Memory(AccountChoices.Track(memory)) == memory && AccountChoices.Memory(AccountChoices.Track(memory with { Subtitles = false })) == new TrackMemory("jpn", "Japonais", false, null, null, false), "Tracks changed on the way");
            return Task.CompletedTask;
        });
        await Test("Tes choix gardés sur ton Jellyfin : un appareil les envoie, l'autre les retrouve, les autres préférences de Jellyfin restent", async () =>
        {
            var stored = """{"Id":"x","SortBy":"SortName","CustomPrefs":{"chromecastVersion":"stable","skipForwardLength":"30000"},"Client":"mira"}""";
            var posts = 0; var paths = new List<string>();
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
            {
                paths.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
                if (request.Method == HttpMethod.Post) { stored = await request.Content!.ReadAsStringAsync(); posts++; return new HttpResponseMessage(HttpStatusCode.NoContent); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stored, Encoding.UTF8, "application/json") };
            }));
            const string Audio = "jpn,ja,fre,fra,fr,eng,en", Subtitles = "fre,fra,fr,eng,en";
            // The PC: a choice of tracks and a title removed before this version, French audio chosen in its settings.
            var pc = Store("choices-pc");
            pc.RememberTracks("serie-1", new TrackMemory("jpn", null, true, "fre", "Complet", false));
            pc.HideFromResume("film-1", DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000));
            var update = await AccountChoices.SyncAsync(client, pc, () => ("fre,fra,fr", Subtitles, false), CancellationToken.None);
            Assert(posts == 1 && !update.SettingsChanged && update.Hidden is null, "First sync of the PC: " + posts);
            Assert(paths[0] == "GET /DisplayPreferences/mira?userId=user-1&client=mira", paths[0]);
            var sent = JsonNode.Parse(stored)!;
            Assert(sent["SortBy"]!.GetValue<string>() == "SortName" && sent["CustomPrefs"]!["chromecastVersion"]!.GetValue<string>() == "stable", "Jellyfin's own preferences lost: " + stored);
            // Nothing new: Jellyfin is not told again.
            await AccountChoices.SyncAsync(client, pc, () => ("fre,fra,fr", Subtitles, false), CancellationToken.None);
            Assert(posts == 1, "Told Jellyfin without a change");
            // The Mac, signed in for the first time: what Jellyfin keeps wins over its defaults.
            var mac = Store("choices-mac");
            update = await AccountChoices.SyncAsync(client, mac, () => (Audio, Subtitles, false), CancellationToken.None);
            Assert(update.AudioLanguage == "fre,fra,fr" && update.SubtitleLanguage is null && update.AutoSkip is null, "Mac settings: " + update);
            Assert(mac.TracksOf("serie-1") == new TrackMemory("jpn", null, true, "fre", "Complet", false), "Mac tracks: " + mac.TracksOf("serie-1"));
            Assert(update.Hidden is { } macHidden && macHidden.Keys.SequenceEqual(["film-1"]) && mac.HiddenFromResume()["film-1"].ToUnixTimeMilliseconds() == 1_760_000_000_000, "Mac hidden");
            // The Mac puts the film back and turns the intros on: the PC follows.
            mac.HideFromResume("film-1", null);
            await AccountChoices.SyncAsync(client, mac, () => ("fre,fra,fr", Subtitles, true), CancellationToken.None);
            update = await AccountChoices.SyncAsync(client, pc, () => ("fre,fra,fr", Subtitles, false), CancellationToken.None);
            Assert(update.AutoSkip == true && update.Hidden is { Count: 0 } && pc.HiddenFromResume().Count == 0, "PC after the Mac: " + update);
        });
    }
}
