using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Mira.Core;
using static TestSupport;

/// <summary>
/// Metadata kept beside the files (MetadataBeside): the libraries' options as Jellyfin 12.1 gives them, where it writes
/// each NFO, and the pass that writes what it kept in its own data (a stand-in for Jellyfin, as 12.1 behaved in the
/// container: an image given again is saved beside the files, a backdrop added after the others).
/// </summary>
internal static class MetadataBesideTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Métadonnées à côté des fichiers : options des bibliothèques, autres réglages gardés", () =>
        {
            // A library made through the API (MetadataSavers null), as Jellyfin 12.1 gives it back.
            var api = JsonNode.Parse("""{"SaveLocalMetadata":false,"EnableRealtimeMonitor":true,"SaveSubtitlesWithMedia":false,"TypeOptions":[]}""")!.AsObject();
            Assert(MetadataBeside.Keeps(api) == false, "API");
            var kept = MetadataBeside.Kept(api)!;
            Assert(kept["SaveLocalMetadata"]!.GetValue<bool>() && kept["MetadataSavers"] is null && kept["EnableRealtimeMonitor"]!.GetValue<bool>() && !kept["SaveSubtitlesWithMedia"]!.GetValue<bool>(), kept.ToJsonString());
            Assert(MetadataBeside.Keeps(kept) == true && MetadataBeside.Kept(kept) is null, "already");
            Assert(api["SaveLocalMetadata"]!.GetValue<bool>() == false, "the options read changed");
            // The dashboard's « Nfo » unticked: ticked again.
            var none = JsonNode.Parse("""{"SaveLocalMetadata":true,"MetadataSavers":[]}""")!.AsObject();
            Assert(MetadataBeside.Keeps(none) == false && MetadataBeside.Kept(none)!["MetadataSavers"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["Nfo"]), "Nfo");
            Assert(MetadataBeside.Keeps(JsonNode.Parse("""{"MetadataSavers":["Nfo"]}""")!.AsObject()) is null && MetadataBeside.Kept(JsonNode.Parse("{}")!.AsObject()) is null, "unknown");
            return Task.CompletedTask;
        });
        await Test("Métadonnées à côté des fichiers : où Jellyfin écrit chaque NFO", () =>
        {
            var root = Path.Combine(run.Root, "nfo");
            var film = Path.Combine(root, "Films", "Sintel (2010)", "Sintel (2010).mkv");
            var disc = Path.Combine(root, "Films", "Aube (2021)"); Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS"));
            var episode = Path.Combine(root, "Séries", "Pioneer One", "Season 01", "Pioneer One S01E01.mkv");
            Assert(MetadataBeside.NfoPaths(new() { Type = "Movie", Path = film }).SequenceEqual([Path.ChangeExtension(film, ".nfo"), Path.Combine(Path.GetDirectoryName(film)!, "movie.nfo")]), "film");
            Assert(MetadataBeside.NfoPaths(new() { Type = "Movie", Path = disc }).SequenceEqual([Path.Combine(disc, "VIDEO_TS", "VIDEO_TS.nfo"), Path.Combine(disc, "Aube (2021).nfo")]), "disc");
            Assert(MetadataBeside.NfoPaths(new() { Type = "Series", Path = Path.Combine(root, "Séries", "Pioneer One") }).Single().EndsWith("tvshow.nfo"), "series");
            Assert(MetadataBeside.NfoPaths(new() { Type = "Episode", Path = episode }).Single() == Path.ChangeExtension(episode, ".nfo"), "episode");
            Assert(MetadataBeside.NfoPaths(new() { Type = "Season", Path = Path.GetDirectoryName(episode) }).Count == 0 && MetadataBeside.NfoPaths(new() { Type = "Episode" }).Count == 0
                && MetadataBeside.NfoPaths(new() { Type = "Movie", Path = "Sintel.mkv" }).Count == 0, "nothing to wait for");
            Directory.CreateDirectory(Path.GetDirectoryName(film)!);
            Assert(!MetadataBeside.HasNfo(new() { Type = "Movie", Path = film }), "no NFO");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(film)!, "movie.nfo"), "<movie/>");
            Assert(MetadataBeside.HasNfo(new() { Type = "Movie", Path = film }), "movie.nfo");
            var folders = new[] { Path.Combine(root, "Films") };
            Assert(MetadataBeside.Inside(film, folders) && !MetadataBeside.Inside(Path.Combine(root, "Films 2", "x.jpg"), folders) && !MetadataBeside.Inside(Path.Combine(root, "Films"), folders)
                && !MetadataBeside.Inside("/config/metadata/library/eb/x/poster.jpg", folders) && !MetadataBeside.Inside(null, folders), "inside");
            return Task.CompletedTask;
        });
        await Test("Métadonnées à côté des fichiers : images redonnées à Jellyfin, NFO manquants demandés", async () =>
        {
            var root = Path.Combine(run.Root, "beside");
            var films = Path.Combine(root, "Films"); var series = Path.Combine(root, "Séries");
            var sintel = Path.Combine(films, "Sintel (2010)", "Sintel (2010).mkv"); var bunny = Path.Combine(films, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).mkv");
            var show = Path.Combine(series, "Pioneer One"); var episode = Path.Combine(show, "Season 01", "Pioneer One S01E01.mkv");
            foreach (var file in new[] { sintel, bunny, episode }) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, [1]); }
            File.WriteAllText(Path.ChangeExtension(bunny, ".nfo"), "<movie/>");
            var kept = new Dictionary<string, bool> { ["lib-films"] = false, ["lib-series"] = false };
            var images = new Dictionary<string, List<(string Type, string Path, string Data)>>
            {
                ["sintel"] = [("Primary", "/config/metadata/library/s/poster.jpg", "P"), ("Backdrop", "/config/metadata/library/s/backdrop.png", "B0"), ("Backdrop", "/config/metadata/library/s/backdrop1.png", "B1")],
                ["bunny"] = [("Primary", Path.Combine(films, "Big Buck Bunny (2008)", "folder.jpg"), "Q")],
                ["show"] = [], ["season"] = [], ["episode"] = [],
            };
            var writes = true; var refreshed = new List<string>(); var calls = new List<string>();
            HttpResponseMessage Answer(HttpRequestMessage request)
            {
                var path = request.RequestUri!.AbsolutePath; var query = request.RequestUri.Query; var body = request.Content?.ReadAsStringAsync().Result;
                calls.Add(request.Method + " " + path);
                if (path == "/Library/VirtualFolders") return JsonResponse(new object[]
                {
                    new { Name = "Films", CollectionType = "movies", ItemId = "lib-films", Locations = new[] { films }, LibraryOptions = new { SaveLocalMetadata = kept["lib-films"], EnableRealtimeMonitor = true } },
                    new { Name = "Séries", CollectionType = "tvshows", ItemId = "lib-series", Locations = new[] { series }, LibraryOptions = new { SaveLocalMetadata = kept["lib-series"] } },
                    new { Name = "Musique", CollectionType = "music", ItemId = "lib-music", Locations = new[] { Path.Combine(root, "Musique") }, LibraryOptions = new { SaveLocalMetadata = false } },
                });
                if (path == "/Library/VirtualFolders/LibraryOptions")
                {
                    var sent = JsonNode.Parse(body!)!.AsObject();
                    Assert(sent["Id"]!.GetValue<string>() != "lib-music", "music changed");
                    kept[sent["Id"]!.GetValue<string>()] = sent["LibraryOptions"]!["SaveLocalMetadata"]!.GetValue<bool>();
                    return new(HttpStatusCode.NoContent);
                }
                if (path == "/Items" && query.Contains("parentId=lib-films")) return JsonResponse(new { Items = new object[] { new { Id = "sintel", Type = "Movie", Path = sintel }, new { Id = "bunny", Type = "Movie", Path = bunny } }, TotalRecordCount = 2 });
                if (path == "/Items" && query.Contains("parentId=lib-series")) return JsonResponse(new
                {
                    Items = new object[]
                {
                    new { Id = "show", Type = "Series", Path = show }, new { Id = "season", Type = "Season", Path = Path.GetDirectoryName(episode) },
                    new { Id = "episode", Type = "Episode", Path = episode, SeriesId = "show" }, new { Id = "virtual", Type = "Episode", SeriesId = "show" },
                }, TotalRecordCount = 4
                });
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["Items", var id, "Refresh"]) { Assert(query.Contains("metadataRefreshMode=FullRefresh") && query.Contains("replaceAllMetadata=false") && query.Contains("replaceAllImages=false"), query); refreshed.Add(id); return new(HttpStatusCode.NoContent); }
                if (parts is ["Items", var item, "Images", ..] && images.TryGetValue(item, out var list))
                {
                    var typed = (string type) => list.Select((x, i) => (x, i)).Where(x => x.x.Type == type).ToList();
                    if (parts.Length == 3) return JsonResponse(list.GroupBy(x => x.Type).SelectMany(g => g.Select((x, i) => new { ImageType = x.Type, ImageIndex = x.Type == "Backdrop" ? i : (int?)null, x.Path })).ToList());
                    var type = parts[3]; var index = parts.Length > 4 ? int.Parse(parts[4]) : 0;
                    if (request.Method == HttpMethod.Get) return typed(type) is { } found && index < found.Count
                        ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(found[index].x.Data)) { Headers = { ContentType = new(type == "Backdrop" ? "image/png" : "image/jpeg") } } } : new(HttpStatusCode.NotFound);
                    if (request.Method == HttpMethod.Delete) { list.RemoveAt(typed(type)[index].i); return new(HttpStatusCode.NoContent); }
                    // Saved beside the files when it may write there, else in its own data again (its retry path).
                    var data = Encoding.ASCII.GetString(Convert.FromBase64String(body!));
                    var place = writes ? Path.Combine(Path.GetDirectoryName(item == "sintel" ? sintel : bunny)!, type + data + ".jpg") : "/config/metadata/library/s/again-" + data;
                    if (type == "Backdrop") list.Add((type, place, data));
                    else { Assert(parts.Length == 5 && index == 0, "index"); var at = list.FindIndex(x => x.Type == type); if (at >= 0) list[at] = (type, place, data); else list.Add((type, place, data)); }
                    return new(HttpStatusCode.NoContent);
                }
                return new(HttpStatusCode.NotFound);
            }
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request => Task.FromResult(Answer(request))));
            // Not kept yet: nothing to do; then both libraries set, music left alone.
            var first = await MetadataBeside.CatchUpAsync(client, refresh: true);
            Assert(first is { Images: 0, Refreshed: 0, Done: true } && calls.All(x => x == "GET /Library/VirtualFolders"), "before: " + first);
            Assert(await client.KeepMetadataBesideAsync() && kept["lib-films"] && kept["lib-series"], "set");
            // Jellyfin may not write in the folders: stopped at the first image saved in its own data again.
            writes = false;
            var refused = await MetadataBeside.CatchUpAsync(client, refresh: true);
            Assert(refused is { Writes: false, Done: false, Refreshed: 0 } && refreshed.Count == 0, "refused: " + refused);
            images["sintel"] = [("Primary", "/config/metadata/library/s/poster.jpg", "P"), ("Backdrop", "/config/metadata/library/s/backdrop.png", "B0"), ("Backdrop", "/config/metadata/library/s/backdrop1.png", "B1")];
            writes = true;
            var pass = await MetadataBeside.CatchUpAsync(client, refresh: false);
            Assert(pass is { Images: 3, Refreshed: 0, Writes: true, Done: false }, "pass: " + pass);
            // The poster replaced, the backdrops in their order, the old ones removed.
            Assert(images["sintel"].Select(x => x.Data).SequenceEqual(["P", "B0", "B1"]) && images["sintel"].All(x => MetadataBeside.Inside(x.Path, [films])), string.Join(", ", images["sintel"]));
            Assert(images["bunny"].Single().Data == "Q" && !calls.Contains("POST /Items/bunny/Images/Primary/0"), "an image already beside the files given again");
            // A week later: Sintel (no NFO) and the series (its episode has none) asked for what is missing; Bunny has its NFO.
            var asked = await MetadataBeside.CatchUpAsync(client, refresh: true);
            Assert(asked is { Images: 0, Refreshed: 2, Done: false } && refreshed.SequenceEqual(["sintel", "show"]), "asked: " + asked + " " + string.Join(", ", refreshed));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(sintel)!, "movie.nfo"), "<movie/>"); File.WriteAllText(Path.Combine(show, "tvshow.nfo"), "<tvshow/>");
            Assert((await MetadataBeside.CatchUpAsync(client, refresh: true)) is { Refreshed: 1, Done: false } && refreshed[^1] == "show", "an episode still without its NFO");
            File.WriteAllText(Path.ChangeExtension(episode, ".nfo"), "<episodedetails/>");
            Assert((await MetadataBeside.CatchUpAsync(client, refresh: true)) is { Images: 0, Refreshed: 0, Done: true }, "done");
            // Not an administrator: nothing done, nothing set.
            using var user = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
            Assert(!await user.KeepMetadataBesideAsync() && await MetadataBeside.CatchUpAsync(user, true) is { Done: false }, "refused");
        });
    }
}
