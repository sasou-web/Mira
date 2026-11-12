using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using static TestSupport;

/// <summary>
/// A title in several files (« Film - cd1.mkv », « Film - cd2.mkv »): MovieParts, with the cases of
/// tests/fixtures/parts.json; with MIRA_PARTS_JELLYFIN (« url user password »), its parts asked of a real Jellyfin.
/// </summary>
internal static class MoviePartTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Film en plusieurs fichiers : la partie où « Lecture » reprend, celle qui suit (tests/fixtures/parts.json)", () =>
        {
            var fixture = Fixture();
            foreach (var row in fixture["has"]!.AsArray())
            {
                var item = row!["item"].Deserialize<MediaItem>(Json.Options)!;
                Assert(MovieParts.Has(item) == (bool)row["has"]!, $"{row["item"]!.ToJsonString()} : {MovieParts.Has(item)}");
            }
            foreach (var row in fixture["start"]!.AsArray())
            {
                var title = Part("title", row!["title"]!) with { Type = "Movie", PartCount = row["parts"]!.AsArray().Count + 1 };
                var parts = new MovieParts(title, row["parts"]!.AsArray().Select((x, at) => Part($"part-{at + 1}", x!)));
                var wanted = parts.Parts[(int)row["start"]!].Id;
                var start = parts.Start((bool)row["fromStart"]!);
                Assert(start.Id == wanted, $"{row["why"]} : {start.Id}, pas {wanted}");
            }
            var set = Set((int)fixture["after"]!["parts"]!);
            foreach (var row in fixture["after"]!["cases"]!.AsArray())
            {
                var at = (int)row![0]!; var next = (int?)row[1];
                var id = at >= 0 ? set.Parts[at].Id : "another-title";
                Assert(set.After(id)?.Id == (next is { } n ? set.Parts[n].Id : null), $"après {id} : {set.After(id)?.Id ?? "rien"}");
                Assert(set.Holds(id) == at >= 0, $"{id} : {set.Holds(id)}");
            }
            var was = Lang.Code;
            try
            {
                foreach (var row in fixture["labels"]!.AsArray())
                {
                    Lang.Use((string)row!["lang"]! == "fr" ? Lang.French : Lang.English);
                    var parts = Set((int)row["parts"]!); var at = (int)row["at"]!;
                    var label = parts.Label(at >= 0 ? parts.Parts[at].Id : "another-title");
                    Assert(label == (string)row["label"]!, $"{row.ToJsonString()} : « {label} »");
                }
            }
            finally { Lang.Use(was); }
            return Task.CompletedTask;
        });

        await Test("Film en plusieurs fichiers : sous le titre, l'année (pas la durée de la seule première partie) et la partie", () =>
        {
            var was = Lang.Code;
            try
            {
                Lang.Use(Lang.English);
                var film = new MovieParts(new MediaItem { Id = "f", Name = "Film", Type = "Movie", ProductionYear = 2018, RunTimeTicks = TimeSpan.FromMinutes(50).Ticks, PartCount = 2 }, [new MediaItem { Id = "f2", Name = "Film", Type = "Video" }]);
                Assert(film.Subtitle("f2") == "2018  ·  Part 2 of 2", film.Subtitle("f2"));
                Assert(film.Title.Subtitle.Contains("50", StringComparison.Ordinal) && !film.Subtitle("f").Contains("50", StringComparison.Ordinal), film.Subtitle("f"));
                // Jellyfin gives the title again among its parts: never twice.
                Assert(new MovieParts(film.Title, [film.Title, film.Parts[1]]).Parts.Count == 2, "titre en double");
                var episode = new MovieParts(new MediaItem { Id = "e", Name = "Pilot", Type = "Episode", SeriesName = "Show", ParentIndexNumber = 1, IndexNumber = 1, PartCount = 2 }, [new MediaItem { Id = "e2", Type = "Video" }]);
                Assert(episode.Subtitle("e2").StartsWith(PlayerText.Subtitle(episode.Title), StringComparison.Ordinal) && episode.Subtitle("e2").EndsWith("Part 2 of 2", StringComparison.Ordinal), episode.Subtitle("e2"));
            }
            finally { Lang.Use(was); }
            return Task.CompletedTask;
        });

        await Test("Film en plusieurs fichiers : gardé sur ce PC en entier, ses autres parties d'abord, chacune dans son dossier ; complet seulement entier", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "mira-kept-parts-" + Guid.NewGuid().ToString("N"));
            try
            {
                var connection = new Connection("http://nas:8096/", "user-1", "Alice", "token", "device-1");
                static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(x => (byte)((x + seed) % 251)).ToArray();
                var first = Bytes(200_000, 1); var second = Bytes(300_000, 7);
                var asked = new List<string>(); var cut = true;
                using var client = new JellyfinClient(connection, new Handler(request =>
                {
                    var path = request.RequestUri!.PathAndQuery;
                    var file = path switch { "/Items/film/Download" => first, "/Items/film-2/Download" => second, _ => null };
                    if (file is null)
                    {
                        // Each part's own subtitle file, under the same index: one beside each video, never one for both.
                        var text = path switch { "/Videos/film/src/Subtitles/2/0/Stream.srt" => "Un", "/Videos/film-2/src-2/Subtitles/2/0/Stream.srt" => "Deux", _ => null };
                        return Task.FromResult(text is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"1\n00:00:01,000 --> 00:00:02,000\n{text}\n") });
                    }
                    var from = request.Headers.Range?.Ranges.Single().From ?? 0; asked.Add($"{path} {from}");
                    // The first answer for the second file stops halfway: the connection was cut.
                    var body = file[(int)from..(cut && file == second ? 100_000 : file.Length)];
                    var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
                    response.Content.Headers.ContentLength = file.Length - from;
                    if (from > 0) response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, file.Length - 1, file.Length);
                    return Task.FromResult(response);
                }));
                static MediaSource Source(string id, long size) => new() { Id = id, Container = "mkv", Path = $"/media/Film/{id}.mkv", Size = size, MediaStreams = [new() { Index = 2, Type = "Subtitle", Codec = "subrip", IsExternal = true, Language = "fre" }] };
                var title = new MediaItem { Id = "film", Name = "Film", Type = "Movie", PartCount = 2 };
                // Jellyfin may give the title again among its parts: never kept twice.
                var copy = OfflineCopies.Plan(root, connection, title, Source("src", first.Length), DateTimeOffset.UnixEpoch, [(title, Source("src", first.Length)), (new MediaItem { Id = "film-2", Name = "Film", Type = "Video" }, Source("src-2", second.Length))]);
                var listed = OfflineCopies.List([root], connection).Single();
                Assert(listed is { Complete: false, TotalSize: 500_000 } && listed.Parts.Single() is { Complete: false, Item.Id: "film-2" } kept && kept.Folder == Path.Combine(copy.Folder, "part-2"), "Planned with its other file, in a folder of its own");

                var seen = new List<(long Done, long Total)>();
                try { await OfflineCopies.FetchTitleAsync(client, listed, new SyncProgress<(long Done, long Total)>(seen.Add), CancellationToken.None); Assert(false, "Cut, not complete"); }
                catch (HttpRequestException) { }
                Assert(asked.SequenceEqual(["/Items/film-2/Download 0"]) && OfflineCopies.Read(copy.Folder) is { Complete: false }, "Its other file first, the title's own after it: " + string.Join(" | ", asked));
                cut = false; seen.Clear();
                var done = await OfflineCopies.FetchTitleAsync(client, OfflineCopies.List([root], connection).Single(), new SyncProgress<(long Done, long Total)>(seen.Add), CancellationToken.None);
                Assert(asked.SequenceEqual(["/Items/film-2/Download 0", "/Items/film-2/Download 100000", "/Items/film/Download 0"]), "Resumed where it stopped, then the title's own: " + string.Join(" | ", asked));
                Assert(done is { Complete: true } && done.Parts.Single().Complete && File.ReadAllBytes(done.VideoPath).SequenceEqual(first) && File.ReadAllBytes(done.Parts[0].VideoPath).SequenceEqual(second), "Both files whole");
                Assert(seen.Count > 0 && seen.All(x => x.Total == 500_000) && seen[^1] == (500_000, 500_000) && seen.Select(x => x.Done).SequenceEqual(seen.Select(x => x.Done).Order()), "One progress for the whole title: " + string.Join(", ", seen));
                Assert(File.ReadAllText(Path.Combine(done.Folder, "subtitle-2.srt")).Contains("Un") && File.ReadAllText(Path.Combine(done.Parts[0].Folder, "subtitle-2.srt")).Contains("Deux"), "Each file's subtitles beside it");

                var read = OfflineCopies.List([root], connection).Single();
                Assert(read is { Complete: true, TotalSize: 500_000 } && read.Parts.Single().Complete && read.BytesOnDisk > 500_000 && read.VideoPaths.SequenceEqual([read.VideoPath, read.Parts[0].VideoPath]), "Read again whole");
                Assert(OfflineCopies.Find([read], "film-2")?.VideoPath == read.Parts[0].VideoPath && OfflineCopies.Find([read], "film") == read && OfflineCopies.Find([read], "other") is null, "Found by each file's id");
                Assert(new MovieParts(title, read.Parts.Select(x => x.Item)).Parts.Select(x => x.Id).SequenceEqual(["film", "film-2"]), "Its parts without Jellyfin");
                asked.Clear();
                Assert(await OfflineCopies.FetchTitleAsync(client, read, null, CancellationToken.None) is { Complete: true } && asked.Count == 0, "Complete: nothing fetched again");
                Assert(OfflineCopies.Delete(read) && !Directory.Exists(read.Folder), "Removed whole");
                // Kept before its other files were (one folder, the first file): as before, its first file only.
                var before = OfflineCopies.Plan(root, connection, title, Source("src", first.Length), DateTimeOffset.UnixEpoch);
                Assert(OfflineCopies.Read(before.Folder) is { Parts.Count: 0, TotalSize: 200_000 }, "Kept before: its first file only");
            }
            finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
        });

        if (Environment.GetEnvironmentVariable("MIRA_PARTS_JELLYFIN") is { Length: > 0 } real)
            await Test("Film en plusieurs fichiers : contre un vrai Jellyfin (PartCount, AdditionalParts, un flux par partie)", () => RealAsync(real));
    }

    /// <summary>The first film in several files of a real Jellyfin: its other parts, each played from its own file.</summary>
    private static async Task RealAsync(string settings)
    {
        var parts = settings.Split(' ', 3);
        using var anonymous = new JellyfinClient(new Connection(parts[0].TrimEnd('/') + "/", "", "", "", "mira-parts-check"));
        using var client = new JellyfinClient(await anonymous.LoginAsync(parts[1], parts[2]));
        var films = await client.BrowseAsync("Movie", limit: 500);
        var title = films.Items.FirstOrDefault(MovieParts.Has) ?? throw new Exception("Aucun film en plusieurs fichiers : " + string.Join(", ", films.Items.Select(x => x.Name)));
        var others = await client.AdditionalPartsAsync(title.Id);
        var set = new MovieParts(title, others);
        Assert(set.Parts.Count == title.PartCount, $"{title.Name} : {title.PartCount} parties annoncées, {set.Parts.Count} reçues");
        var files = new List<string?>();
        foreach (var part in set.Parts)
        {
            var info = await client.PlaybackInfoAsync(part.Id, new JsonObject());
            files.Add(info.MediaSources.FirstOrDefault()?.Path);
        }
        Assert(files.All(x => x is { Length: > 0 }) && files.Distinct().Count() == files.Count, "un fichier par partie : " + string.Join(", ", files));
        // Kept on this PC (« Garder sur ce PC »): every file, each that of its part.
        var root = Path.Combine(Path.GetTempPath(), "mira-real-parts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var full = await client.ItemAsync(title.Id);
            var kept = await OfflineCopies.FetchTitleAsync(client, OfflineCopies.Plan(root, client.Connection, full, full.MediaSources!.First(), DateTimeOffset.Now, others.Select(x => (x, x.MediaSources!.First())).ToList()), null, CancellationToken.None);
            List<OfflineCopy> copies = [kept, .. kept.Parts]; List<MediaItem> titles = [full, .. others];
            var sizes = copies.Select(x => new FileInfo(x.VideoPath).Length).ToList();
            var expected = titles.Select(x => x.MediaSources!.First().Size ?? -1).ToList();
            Assert(kept.Complete && kept.Parts.Count == others.Count && sizes.SequenceEqual(expected), $"gardé en entier : {string.Join(", ", sizes)}, attendu {string.Join(", ", expected)}");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    private static MediaItem Part(string id, JsonNode data) => new()
    {
        Id = id, Name = "Film", Type = "Video",
        UserData = new UserData { Played = (bool)data["Played"]!, PlaybackPositionTicks = (long)data["PlaybackPositionTicks"]!, LastPlayedDate = data["LastPlayedDate"] is { } at ? DateTimeOffset.Parse((string)at!, System.Globalization.CultureInfo.InvariantCulture) : null }
    };
    private static MovieParts Set(int count) =>
        new(new MediaItem { Id = "title", Name = "Film", Type = "Movie", PartCount = count }, Enumerable.Range(1, count - 1).Select(at => new MediaItem { Id = $"part-{at}", Name = "Film", Type = "Video" }));

    private static JsonNode Fixture()
    {
        var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "parts.json")))!;
    }
}
