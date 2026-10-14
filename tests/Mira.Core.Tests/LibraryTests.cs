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
using static TestSupport;

/// <summary>The library: resume, details, series, filters, facets, caches and banner colour. Free of WPF: also run outside Windows.</summary>
internal static class LibraryTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
        LibraryStore Store(string name) => new(testRoot, name);
        await Test("Catalogue : titres de la page, pages ajoutées sans doublon, vu et favori partout, page vide et démo", () =>
        {
            MediaItem Film(string id, bool favorite = false, long position = 0) => new() { Id = id, Name = id, Type = "Movie", RunTimeTicks = 1000, UserData = new() { IsFavorite = favorite, PlaybackPositionTicks = position } };
            var catalog = new Catalog();
            Assert(catalog.IsHome("") && !catalog.IsHome("x") && catalog.Types == "Movie,Series" && catalog.PageTitle == "Explorer", "Accueil par défaut.");
            catalog.Receive(new ItemsResult { Items = [Film("a"), Film("b")], TotalRecordCount = 3 }, more: false);
            catalog.Receive(new ItemsResult { Items = [Film("b"), Film("c")], TotalRecordCount = 3 }, more: true);
            Assert(string.Join(",", catalog.Items.Select(x => x.Id)) == "a,b,c" && Catalog.CountText(catalog.TotalCount) == "3 titres" && Catalog.CountText(1) == "1 titre", "Afficher plus : sans doublon.");
            catalog.View = "Movie"; catalog.Favorites = true; catalog.ParentId = "lib";
            Assert(catalog.Types == "Movie" && catalog.PageTitle == "Films" && catalog.GridTitle("") == "Tes favoris" && catalog.GridTitle("Alien") == "Résultats pour « Alien »", "Titres d’une page filtrée.");
            Assert(catalog.Key("x", new CatalogQuery()) == "Movie:favorites:lib:x:" + new CatalogQuery().CacheKey, "Clé du cache.");
            catalog.Person = ("p", "Ridley Scott", true);
            Assert(catalog.GridTitle("") == "Réalisés par Ridley Scott" && Catalog.PersonText("Sigourney Weaver", false) == "Avec Sigourney Weaver", "Titres d’une personne.");

            var resumed = Film("b", position: 500); catalog.Resume = [resumed];
            var similar = Film("b");
            Assert(catalog.MarkFavorite(Film("b"), false, [similar]) && catalog.Items.All(x => x.Id != "b") && catalog.TotalCount == 2 && !resumed.UserData.IsFavorite, "Favori retiré : hors de la liste des favoris.");
            catalog.Favorites = false;
            Assert(!catalog.MarkFavorite(Film("a"), true, []) && catalog.Items[0].UserData.IsFavorite, "Favori ajouté partout, la liste reste.");
            catalog.MarkPlayed(Film("b"), true, [], [similar]);
            Assert(resumed.UserData.Played && resumed.UserData.PlaybackPositionTicks == 0 && similar.UserData.Played, "Vu : reprise remise à zéro dans chaque liste.");

            var playing = Film("d", position: 300);
            catalog.Remember(playing);
            Assert(catalog.Resume[0] == playing && catalog.RecentPlayback[0] == playing && catalog.ContinueRow().Any(x => x.Id == "d"), "Lecture : en tête de « Continuer à regarder ».");
            Assert(catalog.HeroCandidates(catalog.ContinueRow())[0].Id == "d", "Bandeau : la rangée d’abord.");

            var empty = catalog.Empty("", new CatalogQuery(), demo: false);
            Assert(empty.Icon == "sliders" && empty.Reset && !empty.Guide, "Bibliothèque choisie : un filtre.");
            catalog.View = "home"; catalog.ParentId = null; catalog.Person = null; catalog.Clear();
            empty = catalog.Empty("", new CatalogQuery(), demo: false);
            Assert(empty.Icon == "folder" && empty.Guide && !empty.Reset && catalog.Items.Count == 0 && catalog.RecentPlayback.Count == 0, "Bibliothèque vide : le guide.");
            Assert(!catalog.Empty("", new CatalogQuery(), demo: true).Guide && catalog.Empty("zz", new CatalogQuery(), false).Title == "Aucun résultat pour « zz »", "Démo et recherche.");

            catalog.View = "Movie";
            catalog.ShowDemo([Film("b2", position: 500) with { Name = "Bravo" }, Film("a2") with { Name = "Alpha" }, new MediaItem { Id = "s", Name = "Série", Type = "Series" }], "", new CatalogQuery(Sort: "title"));
            Assert(string.Join(",", catalog.Items.Select(x => x.Name)) == "Alpha,Bravo" && catalog.TotalCount == 2 && catalog.Resume.Count == 1, "Démo : films triés par titre.");
            return Task.CompletedTask;
        });
        await Test("Reprise : la date Jellyfin est conservée et les titres sont classés par dernière lecture", () =>
        {
            var recent = DateTimeOffset.UtcNow;
            MediaItem Episode(string id, string series, DateTimeOffset at, long position = 30) => new() { Id = id, Type = "Episode", SeriesId = series, RunTimeTicks = 1000, UserData = new() { PlaybackPositionTicks = position, LastPlayedDate = at } };
            var old = Episode("old", "old-series", recent.AddDays(-2));
            var latest = Episode("latest", "new-series", recent);
            var parsed = JsonSerializer.Deserialize<MediaItem>(JsonSerializer.Serialize(latest, Json.Options), Json.Options)!;
            Assert(parsed.UserData.LastPlayedDate == recent, "LastPlayedDate was discarded");
            var list = ContinueWatching.Order([old, latest], [], []);
            Assert(list[0].Id == "latest", "The most recent resume is not first");
            var next = Episode("next", "new-series", default, 0);
            var earlierSameSeries = Episode("earlier", "new-series", recent.AddDays(-1));
            list = ContinueWatching.Order([earlierSameSeries, latest, old], [next], []);
            Assert(list.Count == 2 && list[0].Id == "latest", "NextUp or an older episode displaced the actual resume");
            latest.UserData.Played = true; latest.UserData.PlaybackPositionTicks = 0;
            list = ContinueWatching.Order([old], [next], [latest]);
            Assert(list[0].Id == "next", "A just-finished series must keep its next episode at the front");
            return Task.CompletedTask;
        });
        await Test("Reprise : ordre persistant et réponse serveur retardée après une lecture locale", () =>
        {
            var store = Store("recent-playback"); var now = DateTimeOffset.UtcNow;
            var current = new MediaItem { Id = "local", RunTimeTicks = 1000, UserData = new() { LastPlayedDate = now, PlaybackPositionTicks = 220 } };
            var earlier = current with { UserData = current.UserData with { LastPlayedDate = now.AddMinutes(-1), PlaybackPositionTicks = 100 } };
            store.RememberPlayback(current); store.RememberPlayback(earlier);
            var reopened = Store("recent-playback"); var history = reopened.RecentPlayback();
            Assert(history.Single().UserData.PlaybackPositionTicks == 220, "An old write overwrote recent activity");
            Assert(reopened.Load<List<MediaItem>>("resume")!.Single().Id == "local", "Local resume was not cached across restarts");
            Assert(reopened.MergeResume([], history).Single().Id == "local", "Late server response dropped the most recent playback");
            var newerRemote = current with { UserData = current.UserData with { LastPlayedDate = now.AddSeconds(10), PlaybackPositionTicks = 500 } };
            Assert(reopened.MergeResume([newerRemote], history).Single().UserData.PlaybackPositionTicks == 500, "Local history replaced a newer remote playback");
            reopened.ForgetProgress("local"); Assert(reopened.RecentPlayback().Count == 0 && reopened.Load<List<MediaItem>>("resume")!.Count == 0, "Manual watched/unwatched reset left a stale local resume");
            return Task.CompletedTask;
        });
        await Test("Reprise : une ancienne lecture absente du serveur ne revient que si sa synchronisation attend", () =>
        {
            var store = Store("recent-pending");
            var old = new MediaItem { Id = "item-1", RunTimeTicks = 1000, UserData = new() { LastPlayedDate = DateTimeOffset.UtcNow.AddDays(-1), PlaybackPositionTicks = 200 } };
            store.RememberPlayback(old); Assert(store.MergeResume([], store.RecentPlayback()).Count == 0, "An expired local entry was resurrected");
            store.Enqueue("stop", Report(200)); Assert(store.MergeResume([], store.RecentPlayback()).Count == 1, "An offline pending resume disappeared");
            store.Acknowledge(store.Peek()!.Id); Assert(store.MergeResume([], store.RecentPlayback()).Count == 0, "Acknowledged old history overrode the server");
            return Task.CompletedTask;
        });
        await Test("Ton bilan : temps, films, épisodes, séries finies, genres et séries préférés, comme Mira web (tests/fixtures/report.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "report.json")));
            var root = fixture.RootElement;
            var played = root.GetProperty("played").Deserialize<List<MediaItem>>(Json.Options)!;
            var series = root.GetProperty("series").Deserialize<List<MediaItem>>(Json.Options)!;
            var report = WatchReport.Build(played, series, DateTimeOffset.Parse(root.GetProperty("from").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            var expected = root.GetProperty("expected");
            Assert(report.Minutes == expected.GetProperty("minutes").GetInt32() && report.Films == expected.GetProperty("films").GetInt32() && report.Episodes == expected.GetProperty("episodes").GetInt32(),
                $"{report.Minutes} min, {report.Films} films, {report.Episodes} épisodes");
            static string Names(JsonElement list) => string.Join(" | ", list.EnumerateArray().Select(x => x.GetString()));
            Assert(string.Join(" | ", report.Finished) == Names(expected.GetProperty("finished")), "Finished: " + string.Join(" | ", report.Finished));
            Assert(string.Join(" | ", report.Genres) == Names(expected.GetProperty("genres")), "Genres: " + string.Join(" | ", report.Genres));
            var most = string.Join(" | ", report.Series.Select(x => $"{x.Name} {x.Episodes} {x.Minutes}"));
            Assert(most == string.Join(" | ", expected.GetProperty("series").EnumerateArray().Select(x => $"{x.GetProperty("name").GetString()} {x.GetProperty("episodes").GetInt32()} {x.GetProperty("minutes").GetInt32()}")), "Series: " + most);
            foreach (var row in root.GetProperty("times").EnumerateArray())
                Assert(WatchReport.Time(row[0].GetInt32()) == row[1].GetString(), $"{row[0]}: {WatchReport.Time(row[0].GetInt32())}");
            Assert(WatchReport.Count(1, "film", "films") == "1 film" && WatchReport.Count(0, "film", "films") == "0 films", "Count");
            var now = new DateTimeOffset(2026, 10, 14, 18, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 14, 18, 30, 0)));
            var month = WatchReport.Start(now, year: false).ToLocalTime(); var year = WatchReport.Start(now, year: true).ToLocalTime();
            Assert(month.DateTime == new DateTime(2026, 10, 1) && year.DateTime == new DateTime(2026, 1, 1), $"{month} {year}");
            Assert(WatchReport.Build([], [], now).Empty, "Nothing watched");
            return Task.CompletedTask;
        });
        await Test("Accueil à ta main : rangées dans l’ordre choisi, masquées, nouvelles bibliothèques à la suite, comme Mira web (tests/fixtures/home.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "home.json")));
            var libraries = fixture.RootElement.GetProperty("libraries").EnumerateArray()
                .Select(x => new LibrarySection(x.GetProperty("Id").GetString()!, x.GetProperty("Name").GetString()!, x.GetProperty("CollectionType").GetString())).ToList();
            static string Line(IEnumerable<HomeLayout.Row> rows) => string.Join(" ", rows.Select(x => (x.Shown ? "" : "-") + x.Key));
            foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
            {
                var expected = string.Join(" ", row[1].EnumerateArray().Select(x => x.GetString()));
                var actual = Line(HomeLayout.Rows(row[0].GetString(), libraries));
                Assert(actual == expected, $"« {row[0]} »: {actual}");
            }
            var all = HomeLayout.Rows("", libraries);
            foreach (var row in fixture.RootElement.GetProperty("titles").EnumerateArray())
                Assert(all.FirstOrDefault(x => x.Key == row[0].GetString())?.Title == row[1].GetString(), $"Title of {row[0]}");
            Assert(all.First(x => x.Key == "library:a1").LibraryId == "a1", "Library id");
            foreach (var row in fixture.RootElement.GetProperty("moves").EnumerateArray())
            {
                var moved = HomeLayout.Move(row[0].GetString(), libraries, row[1].GetString()!, row[2].GetInt32());
                Assert(moved == row[3].GetString(), $"« {row[0]} » {row[1]} {row[2]}: {moved}");
            }
            foreach (var row in fixture.RootElement.GetProperty("shows").EnumerateArray())
            {
                var shown = HomeLayout.Show(row[0].GetString(), libraries, row[1].GetString()!, row[2].GetBoolean());
                Assert(shown == row[3].GetString(), $"« {row[0]} » {row[1]} {row[2]}: {shown}");
            }
            return Task.CompletedTask;
        });
        await Test("Continuer à regarder : un titre retiré disparaît jusqu’à sa prochaine lecture, et sa reprise est remise à zéro", async () =>
        {
            var removed = DateTimeOffset.UtcNow.AddHours(-1);
            MediaItem Film(string id, DateTimeOffset? played) => new() { Id = id, Name = id, Type = "Movie", UserData = new() { PlaybackPositionTicks = 600_000_000, LastPlayedDate = played } };
            MediaItem Next(string id, string series) => new() { Id = id, Name = id, Type = "Episode", SeriesId = series, SeriesName = "Show", UserData = new() };
            var row = new List<MediaItem> { Film("dropped", removed.AddDays(-2)), Next("e4", "show"), Film("kept", removed.AddDays(-1)) };
            var hidden = new Dictionary<string, DateTimeOffset> { ["dropped"] = removed, ["show"] = removed };
            Assert(ContinueWatching.WithoutHidden(row, hidden, []).Select(x => x.Id).SequenceEqual(["kept"]), "A removed film, or the next episode of a removed series, stayed in the row");
            Assert(ContinueWatching.WithoutHidden(row, new Dictionary<string, DateTimeOffset>(), []).Count == 3, "Nothing removed, everything shown");
            var playedAgain = new List<MediaItem> { new() { Id = "e3", Type = "Episode", SeriesId = "show", UserData = new() { LastPlayedDate = removed.AddMinutes(5) } } };
            Assert(ContinueWatching.WithoutHidden(row, hidden, playedAgain).Select(x => x.Id).SequenceEqual(["e4", "kept"]), "A series played again since did not come back");
            Assert(ContinueWatching.WithoutHidden([Film("dropped", removed.AddMinutes(1))], hidden, []).Count == 1, "A film resumed elsewhere since did not come back");

            // Kept per account, beyond the 30 days after which cached pages are forgotten; « Annuler » puts it back.
            var store = Store("resume-hidden");
            store.HideFromResume("dropped", removed); store.HideFromResume("show", removed);
            store.Prune(DateTimeOffset.UtcNow.AddDays(31));
            Assert(store.HiddenFromResume() is { Count: 2 } kept && kept["show"] == removed, "The removed titles were forgotten with the cache");
            store.HideFromResume("show", null);
            Assert(store.HiddenFromResume().Keys.SequenceEqual(["dropped"]), "« Annuler » did not put the series back");

            // On Jellyfin: only the resume point changes (its other user data are kept, checked on 12.1).
            string? sent = null;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
            {
                sent = request.Method + " " + request.RequestUri!.PathAndQuery + " " + await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            await client.SetPlaybackPositionAsync("film 1", -5);
            Assert(sent == "POST /UserItems/film%201/UserData?userId=user-1 {\"PlaybackPositionTicks\":0}", "Request: " + sent);
        });
        await Test("Fiche : l’image du fichier (« 4K · HDR10 ») comme Mira web, et l’heure de fin (tests/fixtures/quality.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "quality.json")));
            static int? Number(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
            static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            foreach (var row in fixture.RootElement.GetProperty("videos").EnumerateArray())
            {
                var video = new MediaStream { Type = "Video", Width = Number(row[0]), Height = Number(row[1]), VideoRangeType = Text(row[2]), VideoRange = Text(row[3]) };
                var label = MediaQuality.Label(new MediaSource { MediaStreams = [new() { Type = "Audio", Index = 1 }, video] })?.Replace("  ·  ", " · ") ?? "";
                Assert(label == Text(row[4]), $"{row[0]} × {row[1]} {row[2]} : « {label} » au lieu de « {Text(row[4])} »");
            }
            // As Jellyfin sends a film alone (Items/{id}); a card without its files keeps them out of the caches.
            var film = JsonSerializer.Deserialize<MediaItem>("""
                {"Id":"f","Type":"Movie","RunTimeTicks":49200000000,"MediaSources":[{"Id":"s","MediaStreams":[{"Type":"Audio","Index":1},
                 {"Type":"Video","Index":0,"Width":3840,"Height":1600,"VideoRange":"HDR","VideoRangeType":"DOVIWithHDR10"}]}]}
                """, Json.Options)!;
            Assert(MediaQuality.Label(film) == "4K  ·  Dolby Vision" && MediaQuality.Label(new MediaItem()) is null, "Film: " + MediaQuality.Label(film));
            Assert(!JsonSerializer.Serialize(new MediaItem(), Json.Options).Contains("MediaSources"), "A card without files wrote MediaSources");

            var now = new DateTime(2026, 10, 13, 22, 50, 30);
            var resumed = film with { UserData = new() { PlaybackPositionTicks = TimeSpan.FromMinutes(60).Ticks } };
            Assert(MediaQuality.EndsAt(film, now, resume: false) == "Fin à 0 h 12" && MediaQuality.EndsAt(resumed, now, resume: true) == "Fin à 23 h 12"
                && MediaQuality.EndsAt(resumed, now, resume: false) == "Fin à 0 h 12", "End: " + MediaQuality.EndsAt(resumed, now, resume: true));
            Assert(MediaQuality.EndsAt(new MediaItem(), now, false) is null && MediaQuality.EndsAt(null, now, false) is null
                && MediaQuality.EndsAt(film with { UserData = new() { PlaybackPositionTicks = film.RunTimeTicks!.Value } }, now, true) is null, "An end without a length, or past it");
            return Task.CompletedTask;
        });
        await Test("Fiche : la rangée « Bonus » lit les vidéos des dossiers Extras, nommées comme sur Mira web (tests/fixtures/extras.json)", async () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "extras.json")));
            foreach (var row in fixture.RootElement.GetProperty("kinds").EnumerateArray())
            {
                var type = row[0].ValueKind == JsonValueKind.Null ? null : row[0].GetString();
                Assert((PlayerText.ExtraKind(type) ?? "") == row[1].GetString(), $"{type} : « {PlayerText.ExtraKind(type)} »");
            }
            // As Jellyfin 12.1 answers: a plain list, not { Items }, each video with its kind and its own picture.
            string? asked = null;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request =>
            {
                asked = request.RequestUri!.PathAndQuery;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    [{"Id":"e1","Name":"Tournage","Type":"Video","ExtraType":"BehindTheScenes","RunTimeTicks":50080000,"ImageTags":{"Primary":"t"}},
                     {"Id":"e2","Name":"Bêtisier","Type":"Video","ExtraType":"Unknown","RunTimeTicks":50080000}]
                    """, System.Text.Encoding.UTF8, "application/json") });
            }));
            var extras = await client.ExtrasAsync("film 1");
            Assert(asked == "/Items/film%201/SpecialFeatures?userId=user-1", "Request: " + asked);
            Assert(extras.Select(x => (x.Id, x.ExtraType)).SequenceEqual([("e1", "BehindTheScenes"), ("e2", "Unknown")]) && Artwork.Landscape(extras[0]) == new ImageRef("e1", "Primary", "t"), "Extras: " + string.Join(", ", extras.Select(x => x.Id)));
            var film = JsonSerializer.Deserialize<MediaItem>("""{"Id":"f","Type":"Movie","SpecialFeatureCount":2}""", Json.Options)!;
            Assert(film.SpecialFeatureCount == 2 && new MediaItem().SpecialFeatureCount is null, "SpecialFeatureCount");
        });
        await Test("Fiche : un acteur ou un réalisateur mène à ses titres de la bibliothèque, avec les autres filtres", async () =>
        {
            var item = new MediaItem
            {
                Id = "film", Name = "Aube Claire", Type = "Movie",
                People =
                [
                    new() { Id = "p1", Name = " Jeanne Essai ", Type = "Actor" }, new() { Id = "p1b", Name = "jeanne essai", Type = "Actor" },
                    new() { Name = "Sans Fiche", Type = "Actor" }, new() { Id = "d1", Name = "Réa Lisatrice", Type = "Director" }, new() { Id = "w1", Name = "Scé Nariste", Type = "Writer" }
                ]
            };
            Assert(item.CastPeople.Select(x => (x.Id, x.Name)).SequenceEqual([("p1", "Jeanne Essai"), ((string?)null, "Sans Fiche")]) && item.Cast.SequenceEqual(["Jeanne Essai", "Sans Fiche"]), "Cast: names trimmed, one per name, ids kept");
            Assert(item.DirectorPeople.Single() is { Id: "d1", Name: "Réa Lisatrice" }, "Director");
            var query = new CatalogQuery(Year: 2002, Sort: "title", PersonId: "p 1");
            Assert(query.Parameters.Contains("&personIds=p%201") && query.Parameters.Contains("&years=2002") && query.CacheKey != (query with { PersonId = null }).CacheKey, "Person filter: " + query.Parameters);
            Assert(!new CatalogQuery().Parameters.Contains("personIds"), "No person, no filter");
            Uri? requested = null;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request => { requested = request.RequestUri; return Task.FromResult(JsonResponse(new { Items = Array.Empty<object>(), TotalRecordCount = 0 })); }));
            await client.BrowseAsync(filters: new CatalogQuery(PersonId: "d1"));
            Assert(requested!.Query.Contains("personIds=d1") && requested.Query.Contains("includeItemTypes=Movie,Series"), "Request: " + requested);
            // Checked on Jellyfin 12.1 with actors and a director read from .nfo files: the people come with their id, and
            // personIds returns exactly their films, also with a year and a sort.
        });
        await Test("Série : tous ses épisodes, même au-delà de 500, sans ceux que Jellyfin liste comme manquants", async () =>
        {
            // Jellyfin 12.1 with a 523-episode series cut the list at S01E500 when asked for 500: no season 2 on the title page,
            // and no next episode after the 500th.
            Uri? requested = null;
            var episodes = Enumerable.Range(1, 523).Select(i => new { Id = $"e{i}", Name = $"Épisode {i}", Type = "Episode", SeriesId = "long", ParentIndexNumber = i <= 520 ? 1 : 2, IndexNumber = i <= 520 ? i : i - 520 }).ToArray();
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request => { requested = request.RequestUri; return Task.FromResult(JsonResponse(new { Items = episodes, TotalRecordCount = episodes.Length })); }));
            var result = await client.EpisodesAsync("long");
            Assert(!requested!.Query.Contains("limit=") && requested.Query.Contains("isMissing=false"), "Every episode, none missing: " + requested);
            Assert(result.Items.Count == 523 && result.Items[^1] is { ParentIndexNumber: 2, IndexNumber: 3 }, "The last season arrives");
        });
        await Test("Série : une saison de plus de 100 épisodes se choisit par tranches de 100", () =>
        {
            MediaItem Episode(int season, int number) => new() { Id = $"s{season}e{number}", Name = $"Épisode {number}", Type = "Episode", ParentIndexNumber = season, IndexNumber = number };
            var episodes = new[] { Episode(0, 1) }.Concat(Enumerable.Range(1, 520).Select(i => Episode(1, i))).Concat(Enumerable.Range(1, 3).Select(i => Episode(2, i))).ToList();
            var pages = EpisodePages.From(episodes);
            Assert(pages.Select(x => x.Label).SequenceEqual(["Épisodes spéciaux", "Saison 1 · 1–100", "Saison 1 · 101–200", "Saison 1 · 201–300", "Saison 1 · 301–400", "Saison 1 · 401–500", "Saison 1 · 501–520", "Saison 2"]),
                "Pages: " + string.Join(" | ", pages.Select(x => x.Label)));
            Assert(EpisodePages.Episodes(episodes, pages[6]).Select(x => x.IndexNumber).SequenceEqual(Enumerable.Range(501, 20).Select(x => (int?)x)), "The last slice holds 501 to 520");
            Assert(EpisodePages.Holding(pages, episodes, episodes.Single(x => x.Id == "s1e250")) == pages[3] && EpisodePages.Holding(pages, episodes, episodes[^1]) == pages[^1], "The page of an episode");
            Assert(EpisodePages.Holding(pages, episodes, Episode(1, 999)) is null && EpisodePages.Holding(pages, episodes, null) is null, "An episode not in the list has no page");
            // Numbered from where the season starts, as anime often are, and without numbers.
            var numbered = Enumerable.Range(1, 150).Select(i => Episode(3, 400 + i)).ToList();
            Assert(EpisodePages.From(numbered).Select(x => x.Label).SequenceEqual(["Saison 3 · 401–500", "Saison 3 · 501–550"]), "Labels use the episode numbers");
            var unnumbered = Enumerable.Range(1, 101).Select(i => new MediaItem { Id = $"u{i}", Name = "?", Type = "Episode" }).ToList();
            Assert(EpisodePages.From(unnumbered).Select(x => x.Label).SequenceEqual(["Saison 1 · 1–100", "Saison 1 · 101"]), "Without numbers, their position");
            Assert(EpisodePages.From(Enumerable.Range(1, 100).Select(i => Episode(1, i)).ToList()).Single().Label == "Saison 1", "100 episodes stay one page");
            return Task.CompletedTask;
        });
        await Test("Actualiser : Jellyfin analyse les dossiers, avancement suivi jusqu’à la fin, refus d’un compte non administrateur", async () =>
        {
            // The scan task as Jellyfin lists it: Idle with the end of the last scan, then Running with its progress.
            object ScanTask(string state, double? progress, string ended) => new[] { new { Key = "RefreshLibrary", State = state, CurrentProgressPercentage = progress, LastExecutionResult = new { EndTimeUtc = ended } } };
            async Task<(bool Scanned, List<double?> Seen, List<string> Calls)> Run(Queue<object> states, bool admin = true, int startLimitMs = 2000)
            {
                var seen = new List<double?>(); var calls = new List<string>(); object last = states.Peek();
                using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request =>
                {
                    calls.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
                    if (!admin) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                    if (request.Method == HttpMethod.Post) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                    if (states.Count > 0) last = states.Dequeue();
                    return Task.FromResult(JsonResponse(last));
                }));
                var scanned = await client.ScanAndWaitAsync(new SyncProgress<double?>(seen.Add), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(startLimitMs));
                return (scanned, seen, calls);
            }
            var followed = await Run(new([ScanTask("Idle", null, "2026-10-01T10:00:00Z"), ScanTask("Running", 40, "2026-10-01T10:00:00Z"), ScanTask("Running", 80, "2026-10-01T10:00:00Z"), ScanTask("Idle", null, "2026-10-02T09:00:00Z")]));
            Assert(followed.Scanned && followed.Seen.SequenceEqual([null, .4, .8]) && followed.Calls.Count(x => x == "POST /Library/Refresh") == 1, "Followed scan: " + string.Join(", ", followed.Seen) + " · " + string.Join(", ", followed.Calls));
            var quick = await Run(new([ScanTask("Idle", null, "2026-10-01T10:00:00Z"), ScanTask("Idle", null, "2026-10-02T09:00:00Z")]));
            Assert(quick.Scanned && quick.Seen.SequenceEqual([(double?)null]), "A scan over before the first look was not seen ending");
            var refused = await Run(new([ScanTask("Idle", null, "2026-10-01T10:00:00Z")]), admin: false);
            Assert(!refused.Scanned && refused.Seen.Count == 0, "A non-administrator was told the folders were scanned");
            var stuck = await Run(new([ScanTask("Idle", null, "2026-10-01T10:00:00Z")]), startLimitMs: 60);
            Assert(stuck.Scanned && stuck.Calls.Count < 200, "A scan that never starts is waited for: " + stuck.Calls.Count + " requests");
        });
        await Test("Filtres : recherche, genre, année et pagination envoyés ensemble à Jellyfin", async () =>
        {
            Uri? requestUri = null;
            using var client = new JellyfinClient(new("http://localhost/jellyfin/", "user-1", "Alice", "secret", "device"), new Handler(request =>
            { requestUri = request.RequestUri; return Task.FromResult(JsonResponse(new { Items = Array.Empty<object>(), TotalRecordCount = 0 })); }));
            await client.BrowseAsync("Series", "library-1", "été", 60, true, filters: new CatalogQuery("Action & aventure", 2024, false, "title"));
            var query = requestUri!.Query;
            Assert(query.Contains("genres=Action%20%26%20aventure"), "Genre not encoded");
            foreach (var part in new[] { "years=2024", "isPlayed=false", "sortBy=SortName", "sortOrder=Ascending", "startIndex=60", "parentId=library-1", "isFavorite=true", "includeItemTypes=Series", "RecursiveItemCount" }) Assert(query.Contains(part), "Missing query component: " + part);
            Assert(new CatalogQuery(Played: false).CacheKey != new CatalogQuery(Played: true).CacheKey, "Watched filters share a cache key");
            Assert(new CatalogQuery().Parameters.Contains("DateCreated"), "Default sort changed");
        });
        await Test("Séries : épisodes à voir sur l’affiche d’une série commencée, rien avant ni une fois toute vue", () =>
        {
            MediaItem Item(string json) => JsonSerializer.Deserialize<MediaItem>(json, Json.Options)!;
            var started = Item("""{"Id":"s1","Type":"Series","UserData":{"PlayedPercentage":33.3,"UnplayedItemCount":2,"Played":false}}""");
            Assert(started.EpisodesToWatch == 2 && started.EpisodesToWatchBadge == "2" && started.EpisodesToWatchText == "2 épisodes à voir", "Episodes left: " + started.EpisodesToWatchText);
            var one = Item("""{"Id":"s2","Type":"Series","UserData":{"PlayedPercentage":95,"UnplayedItemCount":1}}""");
            var long_ = Item("""{"Id":"s3","Type":"Series","UserData":{"PlayedPercentage":1,"UnplayedItemCount":1040}}""");
            Assert(one.EpisodesToWatchText == "1 épisode à voir" && long_.EpisodesToWatchBadge == "99+", "Singular or long series: " + one.EpisodesToWatchText + " " + long_.EpisodesToWatchBadge);
            var unseen = Item("""{"Id":"s4","Type":"Series","UserData":{"PlayedPercentage":0,"UnplayedItemCount":12}}""");
            var older = Item("""{"Id":"s5","Type":"Series","UserData":{"UnplayedItemCount":12}}""");
            var done = Item("""{"Id":"s6","Type":"Series","UserData":{"PlayedPercentage":100,"UnplayedItemCount":0,"Played":true}}""");
            var film = Item("""{"Id":"m1","Type":"Movie","UserData":{"PlayedPercentage":40,"UnplayedItemCount":1}}""");
            Assert(new[] { unseen, older, done, film }.All(x => x.EpisodesToWatch is null && x.EpisodesToWatchBadge is null), "A badge on a series never started, one fully watched, or a film");
            return Task.CompletedTask;
        });
        await Test("Fiche : distribution, réalisation, titres similaires et tri par dernière lecture", async () =>
        {
            var film = JsonSerializer.Deserialize<MediaItem>("""
                {"Id":"f","Name":"Film","People":[{"Name":"Ana","Role":"Mia","Type":"Actor"},{"Name":"Ben","Type":"Director"},{"Name":" ana ","Type":"Actor"},
                {"Name":"Chloé","Type":"Writer"},{"Name":"Dan","Type":"actor"},{"Name":"","Type":"Actor"}]}
                """, Json.Options)!;
            Assert(film.Cast.SequenceEqual(["Ana", "Dan"]) && film.Directors.SequenceEqual(["Ben"]), $"Credits: {string.Join(", ", film.Cast)} / {string.Join(", ", film.Directors)}");
            // Pages cached before people were read still load.
            Assert(JsonSerializer.Deserialize<ItemsResult>("""{"Items":[{"Id":"old","Name":"Ancien"}]}""", Json.Options)!.Items[0].Cast.Length == 0, "Old cached page");
            Assert(new CatalogQuery(Sort: "played").Parameters.Contains("sortBy=DatePlayed,SortName&sortOrder=Descending"), "Last played sort");
            Uri? asked = null;
            using var client = new JellyfinClient(new("http://localhost/jellyfin/", "user-1", "Alice", "secret", "device"), new Handler(request =>
            { asked = request.RequestUri; return Task.FromResult(JsonResponse(new { Items = new[] { new { Id = "g", Name = "Autre" } }, TotalRecordCount = 1 })); }));
            var similar = await client.SimilarAsync("film 1");
            Assert(similar.Items.Single().Id == "g" && asked!.AbsolutePath == "/jellyfin/Items/film%201/Similar" && asked.Query.Contains("userId=user-1") && asked.Query.Contains("limit=12"), "Similar request: " + asked);
        });
        await Test("Facettes : genres récursifs et années de toute la bibliothèque", async () =>
        {
            var paths = new List<string>();
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request =>
            {
                paths.Add(request.RequestUri!.PathAndQuery);
                return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("Filters2")
                    ? JsonResponse(new { Genres = new[] { new { Name = "Animation", Id = "genre-1" } } })
                    : JsonResponse(new { Items = new[] { new { Name = "2004" }, new { Name = "2026" }, new { Name = "inconnue" } } }));
            }));
            var facets = await client.FiltersAsync();
            Assert(facets.Genres.SequenceEqual(new[] { "Animation" }), "Genre names were lost");
            Assert(facets.Years.SequenceEqual(new[] { 2004, 2026 }), "Years were not parsed correctly");
            Assert(paths.Count == 2 && paths.All(x => x.Contains("recursive=true") && x.Contains("userId=user-1")), "Facets are not recursive and user scoped");
        });
        await Test("Cache de bibliothèque illisible : mis de côté et recréé, le compte s’ouvre", () =>
        {
            var folder = Path.Combine(testRoot, "damaged-store");
            var first = new LibraryStore(folder, "abîmé"); first.Save("home", new ItemsResult { TotalRecordCount = 1 });
            Assert(first.DamagedCopy is null, "An intact database was set aside");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var database = Directory.GetFiles(folder, "library-*.db").Single();
            foreach (var journal in new[] { database + "-wal", database + "-shm" }) if (File.Exists(journal)) File.Delete(journal);
            File.WriteAllText(database, "Pas une base SQLite : un fichier coupé par une panne de courant, qui ne commence pas par l’en-tête attendu.");
            var store = new LibraryStore(folder, "abîmé");
            store.Save("home", new ItemsResult { TotalRecordCount = 2 }); store.Enqueue("start", Report());
            Assert(store.Load<ItemsResult>("home") is { TotalRecordCount: 2 } && store.PendingCount == 1, "The recreated database does not work");
            Assert(store.DamagedCopy is { } copy && File.Exists(copy) && File.ReadAllText(copy).StartsWith("Pas une base"), "The damaged file was not kept aside");
            return Task.CompletedTask;
        });
        await Test("Cache de bibliothèque aux pages abîmées, en-tête intact : mis de côté et recréé, le compte s’ouvre", () =>
        {
            var folder = Path.Combine(testRoot, "damaged-pages");
            var first = new LibraryStore(folder, "pages"); first.Enqueue("start", Report());
            first.Save("home", new ItemsResult { Items = Enumerable.Range(0, 200).Select(i => new MediaItem { Id = $"film-{i}", Name = $"Film {i}", Overview = new string('x', 200) }).ToList(), TotalRecordCount = 200 });
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var database = Directory.GetFiles(folder, "library-*.db").Single();
            foreach (var journal in new[] { database + "-wal", database + "-shm" }) if (File.Exists(journal)) File.Delete(journal);
            // Page 1 (the 100-byte header and the list of tables) intact: SQLite opens the file, and the damage only shows in
            // a query that reads another page, as Prune did at every opening.
            var bytes = File.ReadAllBytes(database); var page = bytes[16] << 8 | bytes[17]; if (page == 1) page = 65536;
            Assert(bytes.Length >= 4 * page, $"Unexpected database: {bytes.Length} bytes, pages of {page}");
            Array.Fill(bytes, (byte)0x5A, page, bytes.Length - page); File.WriteAllBytes(database, bytes);
            var store = new LibraryStore(folder, "pages");
            Assert(store.DamagedCopy is { } copy && File.Exists(copy) && File.ReadAllBytes(copy).AsSpan(page).IndexOfAnyExcept((byte)0x5A) < 0, "The damaged file was not kept aside");
            Assert(store.Load<ItemsResult>("home") is null && store.PendingCount == 0, "The recreated database is not empty");
            store.Save("home", new ItemsResult { TotalRecordCount = 3 }); store.Enqueue("start", Report());
            Assert(store.Load<ItemsResult>("home") is { TotalRecordCount: 3 } && store.PendingCount == 1, "The recreated database does not work");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert(new LibraryStore(folder, "pages").DamagedCopy is null, "The recreated database was set aside again");
            return Task.CompletedTask;
        });
        await Test("Préchargement : une seule requête partagée, données invalidées à l’actualisation", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(async _ =>
            { Interlocked.Increment(ref count); await gate.Task; return JsonResponse(new MediaItem { Id = "series-1", Name = "Une série" }); }));
            var cache = new MetadataCache(client); var hover = cache.ItemAsync("series-1"); var detail = cache.ItemAsync("series-1");
            Assert(ReferenceEquals(hover, detail) && count == 1, "Hover and detail issue duplicate requests");
            gate.SetResult(); await Task.WhenAll(hover, detail); await cache.ItemAsync("series-1"); Assert(count == 1, "Warm detail refetches metadata");
            cache.Clear(); await cache.ItemAsync("series-1"); Assert(count == 2, "Invalidation retained stale metadata");
        });
        await Test("Préchargement : une erreur temporaire ne bloque pas la prochaine ouverture", async () =>
        {
            var count = 0;
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(_ =>
            { count++; return Task.FromResult(count == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : JsonResponse(new MediaItem { Id = "x", Name = "Rétabli" })); }));
            var cache = new MetadataCache(client);
            try { await cache.ItemAsync("x"); throw new Exception("Expected temporary HTTP failure"); } catch (HttpRequestException) { }
            Assert((await cache.ItemAsync("x")).Name == "Rétabli" && count == 2, "Faulted prefetch remains cached");
        });
        await Test("Cache disque : au-delà du budget, les images les moins récemment vues partent", () =>
        {
            var folder = Path.Combine(testRoot, "disk-cache"); Directory.CreateDirectory(Path.Combine(folder, "compte-2"));
            string Make(string name, int size, double daysAgo)
            { var path = Path.Combine(folder, name); File.WriteAllBytes(path, new byte[size]); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysAgo)); return path; }
            var oldest = Make("a.img", 400, 30); var old = Make(Path.Combine("compte-2", "b.img"), 400, 20); var recent = Make("c.img", 400, 2); var fresh = Make("d.img", 400, 0);
            var interrupted = Make("e.img.tmp", 100, 1); var writing = Make("f.img.tmp", 100, 0); var other = Make("notes.txt", 5000, 40);
            Assert(DiskCache.Trim(folder, 2000, 1000) == (1, 100) && !File.Exists(interrupted) && File.Exists(writing), "Trimmed under budget, or temporary files mishandled");
            var touched = File.GetLastWriteTimeUtc(fresh); DiskCache.Touch(fresh);
            Assert(File.GetLastWriteTimeUtc(fresh) == touched, "A file used today was written again");
            DiskCache.Touch(oldest);
            Assert(DiskCache.Trim(folder, 1500, 800) == (2, 800), "Wrong amount trimmed");
            Assert(File.Exists(oldest) && File.Exists(fresh) && !File.Exists(old) && !File.Exists(recent) && File.Exists(other), "Least recently used files not chosen, or another file touched");
            Assert(DiskCache.Trim(Path.Combine(testRoot, "absent"), 1, 0) == (0, 0), "Missing folder");
            return Task.CompletedTask;
        });
        await Test("Cache de bibliothèque : pages anciennes oubliées ; accueil, reprise, historique et envois en attente gardés", () =>
        {
            var store = Store("prune");
            store.Save("home", new ItemsResult()); store.Save("Movie::|2024||recent", new ItemsResult { TotalRecordCount = 3 });
            store.RememberPlayback(new MediaItem { Id = "vu", RunTimeTicks = 1000, UserData = new() { LastPlayedDate = DateTimeOffset.UtcNow, PlaybackPositionTicks = 500 } });
            store.Enqueue("stop", Report(400) with { ItemId = "livré" }); store.Acknowledge(store.Peek()!.Id);
            store.Enqueue("progress", Report(300));
            Assert(Store("prune").Load<ItemsResult>("Movie::|2024||recent") is { TotalRecordCount: 3 }, "A recent page expired on opening");
            store.Prune(DateTimeOffset.UtcNow.AddMinutes(1));
            Assert(store.Load<ItemsResult>("Movie::|2024||recent") is null, "Old filtered page kept");
            Assert(store.Load<ItemsResult>("home") is not null && store.Load<List<MediaItem>>("resume") is { Count: 1 } && store.RecentPlayback().Count == 1, "Home, resume row or history pruned");
            var items = new[] { new MediaItem { Id = "item-1" }, new MediaItem { Id = "livré" } }; store.ApplyLocalProgress(items);
            Assert(items[0].UserData.PlaybackPositionTicks == 300 && items[1].UserData.PlaybackPositionTicks == 0 && store.PendingCount == 1, "Pending position lost, or delivered one kept");
            return Task.CompletedTask;
        });
        await Test("Continuer à regarder : un épisode montre sa saison ou lui-même, pas l’image d’une autre saison", async () =>
        {
            // A series whose seasons look nothing alike: its backdrop shows one season, the episode belongs to another.
            MediaItem Episode(string? seasonThumb = null, string? backdropParent = "series", Dictionary<string, string>? own = null) => new()
            {
                Id = "episode", Type = "Episode", SeriesId = "series", SeasonId = "season-7", ImageTags = own ?? [],
                ParentBackdropItemId = backdropParent, ParentBackdropImageTags = backdropParent is null ? [] : ["backdrop-tag"], SeasonThumbImageTag = seasonThumb,
            };
            var choices = new[]
            {
                Artwork.Landscape(Episode(own: new() { ["Thumb"] = "own-thumb", ["Primary"] = "still" })),
                Artwork.Landscape(Episode(seasonThumb: "season-thumb", own: new() { ["Primary"] = "still" })),
                Artwork.Landscape(Episode(backdropParent: "season-7", own: new() { ["Primary"] = "still" })),
                Artwork.Landscape(Episode(own: new() { ["Primary"] = "still" })),
                Artwork.Landscape(Episode()),
                Artwork.Landscape(new MediaItem { Id = "movie", Type = "Movie", BackdropImageTags = ["movie-backdrop"], ImageTags = new() { ["Primary"] = "poster" } }),
            };
            Assert(choices.SequenceEqual([
                new ImageRef("episode", "Thumb", "own-thumb"), new ImageRef("season-7", "Thumb", "season-thumb"), new ImageRef("season-7", "Backdrop", "backdrop-tag"),
                new ImageRef("episode", "Primary", "still"), new ImageRef("series", "Backdrop", "backdrop-tag"), new ImageRef("movie", "Backdrop", "movie-backdrop")]),
                "Choices: " + string.Join(" | ", choices.Select(x => $"{x.ItemId}/{x.Type}/{x.Tag}")));
            // The home banner keeps the series' backdrop: it presents the series, with its logo.
            Assert(Artwork.Backdrop(Episode(own: new() { ["Primary"] = "still" })) == new ImageRef("series", "Backdrop", "backdrop-tag"), "Banner of an episode");
            Assert(Artwork.Seasons([Episode(), Episode(), new MediaItem { Type = "Movie", SeasonId = "x" }, new MediaItem { Type = "Episode" }]).SequenceEqual(["season-7"]), "Seasons asked for");
            // One request for every season; a season without a thumbnail answers null.
            string? url = null;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "token", "device"), new Handler(request =>
            {
                url = request.RequestUri!.PathAndQuery;
                return Task.FromResult(JsonResponse(new { Items = new object[] { new { Id = "season-7", ImageTags = new { Thumb = "t7" } }, new { Id = "season-2", ImageTags = new { } } }, TotalRecordCount = 2 }));
            }));
            var thumbs = await client.SeasonThumbsAsync(["season-7", "season-2", "season-9"]);
            Assert(url is not null && url.StartsWith("/Items?userId=user-1&ids=season-7,season-2,season-9&") && url.Contains("enableImageTypes=Thumb"), "Request: " + url);
            Assert(thumbs.Count == 3 && thumbs["season-7"] == "t7" && thumbs["season-2"] is null && thumbs["season-9"] is null, "Thumbnails by season");
            url = null; Assert((await client.SeasonThumbsAsync([])).Count == 0 && url is null, "A request without any season");
        });
        await Test("Continuer à regarder : la vignette d’une saison demandée une fois, une panne n’empêche rien, seule l’annulation de Mira remonte", async () =>
        {
            MediaItem Episode(string season) => new() { Id = "e-" + season, Type = "Episode", SeriesId = "series", SeasonId = season };
            var asked = new List<string>(); var failing = false;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "token", "device"), new Handler(request =>
            {
                asked.Add(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["ids"] ?? "");
                if (failing) throw new HttpRequestException("coupure");
                return Task.FromResult(JsonResponse(new { Items = new object[] { new { Id = "s1", ImageTags = new { Thumb = "t1" } } }, TotalRecordCount = 1 }));
            }));
            var thumbs = new SeasonThumbs();
            var first = new[] { Episode("s1"), Episode("s2") };
            await thumbs.LoadAsync(client, first, CancellationToken.None);
            Assert(asked.SequenceEqual(["s1,s2"]) && first[0].SeasonThumbImageTag == "t1" && first[1].SeasonThumbImageTag is null, "First load: " + string.Join(" | ", asked));
            // Known seasons are not asked again; a new one is, alone. Jellyfin out of reach: the cards stay as they were.
            failing = true; var later = new[] { Episode("s1"), Episode("s3") };
            await thumbs.LoadAsync(client, later, CancellationToken.None);
            Assert(asked.SequenceEqual(["s1,s2", "s3"]) && later[0].SeasonThumbImageTag == "t1", "Second load: " + string.Join(" | ", asked));
            // A copy for a worker thread, applied there.
            var copy = new[] { Episode("s1") }; thumbs.Apply(copy, thumbs.Snapshot());
            Assert(copy[0].SeasonThumbImageTag == "t1" && thumbs.Snapshot().Count == 2, "Snapshot");
            // Mira's own cancellation (another account, a newer refresh) goes on up.
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await thumbs.LoadAsync(client, [Episode("s4")], cancelled.Token); throw new Exception("Cancellation swallowed"); }
            catch (OperationCanceledException) { }
            thumbs.Clear(); Assert(thumbs.Snapshot().Count == 0, "Not cleared");
        });
        await Test("Images : largeur décodée = taille affichée × échelle de l’écran, au palier supérieur", () =>
        {
            Assert(Artwork.Pixels(185, 1, 1280) == 200 && Artwork.Pixels(230, 1, 1280) == 240, "Posters at 100 %");
            Assert(Artwork.Pixels(185, 1.5, 1280) == 280 && Artwork.Pixels(230, 1.5, 1280) == 360, "Posters at 150 %");
            Assert(Artwork.Pixels(290, 2, 1280) == 640 && Artwork.Pixels(160, 1.25, 1280) == 200, "Wide cards and episodes");
            Assert(Artwork.Pixels(1920, 1, 2560) == 1920 && Artwork.Pixels(1366, 1, 2560) == 1600 && Artwork.Pixels(3840, 1, 2560) == 2560, "Backdrops by screen width, 2560 at most");
            Assert(Artwork.Pixels(double.NaN, 1, 900) == 900 && Artwork.Pixels(0, 1, 900) == 900 && Artwork.Pixels(200, double.NaN, 1280) == 200 && Artwork.Pixels(200, 0, 1280) == 200, "Unknown size or scale");
            // Cards a few units apart share one file on disk and one request to Jellyfin.
            Assert(Artwork.Pixels(181, 1, 1280) == Artwork.Pixels(199, 1, 1280), "Steps");
            return Task.CompletedTask;
        });
        await Test("Images en mémoire : une requête par image, les moins récentes libérées au-delà du budget, tout libéré à la réduction", async () =>
        {
            var memory = new ImageMemory<byte[]>(300, 200, x => x.Length);
            var fetches = 0;
            Task<byte[]?> Fetch(int size) { Interlocked.Increment(ref fetches); return Task.FromResult<byte[]?>(new byte[size]); }
            var first = memory.Get("a", () => Fetch(100));
            Assert(ReferenceEquals(memory.Get("a", () => Fetch(100)), first) && fetches == 1, "Two requests for one image");
            await memory.Get("b", () => Fetch(100)); await memory.Get("c", () => Fetch(100));
            await WaitUntil(() => memory.Settled == 3);
            Assert(memory.Bytes == 300 && memory.Count == 3, "Within the budget: " + memory.Bytes);
            // « a » used again: « b » then « c » are the oldest, and leave down to the target.
            _ = memory.Get("a", () => Fetch(100)); await memory.Get("d", () => Fetch(100));
            await WaitUntil(() => memory.Settled == 4);
            Assert(memory.Bytes == 200 && memory.Count == 2 && ReferenceEquals(memory.Get("a", () => Fetch(100)), first), "Least recently used not let go first: " + memory.Bytes);
            // A failed image is asked again next time.
            Assert(await memory.Get("e", () => Task.FromResult<byte[]?>(null)) is null && await memory.Get("f", () => Task.FromException<byte[]?>(new IOException())).ContinueWith(x => x.IsFaulted), "Failures");
            await WaitUntil(() => memory.Settled == 6);
            Assert(memory.Count == 2, "A failed image stayed: " + memory.Count);
            // Minimized: everything goes; a request on its way still answers, without being counted.
            var gate = new TaskCompletionSource<byte[]?>(); var pending = memory.Get("g", () => gate.Task);
            memory.Clear(); Assert(memory.Bytes == 0 && memory.Count == 0, "Not emptied");
            gate.SetResult(new byte[50]); Assert(await pending is { Length: 50 }, "The request on its way was lost");
            await WaitUntil(() => memory.Settled == 7);
            Assert(memory.Bytes == 0, "A request made before emptying was counted: " + memory.Bytes);
            var again = memory.Get("a", () => Fetch(100)); Assert(!ReferenceEquals(again, first) && fetches == 5, "An image let go was not asked again");
        });
        await Test("Cache de bibliothèque : une page identique n’est pas réécrite, une page changée l’est", () =>
        {
            var folder = Path.Combine(testRoot, "unchanged-pages");
            var store = new LibraryStore(folder, "pages");
            string Updated()
            {
                var path = Directory.GetFiles(folder, "library-*.db").Single();
                using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()); db.Open();
                using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT updated FROM cache WHERE key='home'"; return (string)cmd.ExecuteScalar()!;
            }
            var page = new ItemsResult { Items = [new MediaItem { Id = "a", Name = "Film", ImageTags = new() { ["Primary"] = "t" } }], TotalRecordCount = 1 };
            store.Save("home", page); var first = Updated();
            Thread.Sleep(20); store.Save("home", page);
            Assert(Updated() == first, "An identical page was written again");
            page.Items[0] = page.Items[0] with { Name = "Film renommé" }; store.Save("home", page);
            Assert(Updated() != first && store.Load<ItemsResult>("home")!.Items[0].Name == "Film renommé", "A changed page was not written");
            // Another opening of the same cache knows a page once it has read it.
            var second = Updated(); var reopened = new LibraryStore(folder, "pages");
            var loaded = reopened.Load<ItemsResult>("home")!; Thread.Sleep(20); reopened.Save("home", loaded);
            Assert(Updated() == second, "A page read then saved unchanged was written again");
            return Task.CompletedTask;
        });
        await Test("Couleur du bandeau : teintes différentes pour des images rouges et bleues", () =>
        {
            byte[] Pixels(byte b, byte g, byte r) => Enumerable.Range(0, 64).SelectMany(_ => new[] { b, g, r, (byte)255 }).ToArray();
            var red = ArtworkPalette.Extract(Pixels(30, 40, 180)); var blue = ArtworkPalette.Extract(Pixels(180, 60, 30));
            Assert(red.R > red.B && blue.B > blue.R && red != blue, "Artwork colors share a fixed accent");
            Assert(Math.Min(red.R, Math.Min(red.G, red.B)) >= 175 && Math.Min(blue.R, Math.Min(blue.G, blue.B)) >= 175, "Tint loses legibility on the dark header");
            return Task.CompletedTask;
        });
        await Test("Couleur du bandeau : transparence et images neutres gardent un accent blanc", () =>
        {
            foreach (var pixels in new[] { Array.Empty<byte>(), new byte[] { 0, 0, 0, 255, 255, 0, 0, 0 }, new byte[] { 110, 110, 110, 255, 250, 250, 250, 255 } })
                Assert(ArtworkPalette.Extract(pixels) == new RgbColor(245, 245, 247), "Neutral artwork introduces an arbitrary accent");
            return Task.CompletedTask;
        });
        await Test("Jellyfin : dossiers des bibliothèques et signalement des médias, sans déconnexion en cas de refus", async () =>
        {
            var requests = new List<(string Method, string Path, string Body)>(); var refuse = false;
            using var client = new JellyfinClient(new("http://localhost/jellyfin/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                lock (requests) requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
                if (refuse) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (request.RequestUri.AbsolutePath.EndsWith("/Library/VirtualFolders")) return JsonResponse(new object[]
                {
                    new { Name = "Movies", CollectionType = "movies", Locations = new[] { @"C:\Media\FILMS" } }, new { Name = "Anime", CollectionType = "tvshows", Locations = new[] { @"C:\Media\ANIME" } },
                    new { Name = "Shows", CollectionType = "tvshows", Locations = new[] { @"C:\Media\SERIES" } }, new { Name = "Music", CollectionType = "music", Locations = new[] { @"C:\Media\MUSIC" } }
                });
                if (request.RequestUri.AbsolutePath.EndsWith("/Items")) return JsonResponse(new
                {
                    Items = new object[]
                    {
                        new { Id = "movie-1", Type = "Movie", Path = @"C:\Media\FILMS\Big Buck Bunny (2008)\Big Buck Bunny (2008).mkv" },
                        new { Id = "episode-1", Type = "Episode", SeriesId = "series-9", Path = @"C:\Media\SERIES\Show\Season 01\Show - S01E02.mkv" }
                    },
                    TotalRecordCount = 2
                });
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
            var folders = await client.VirtualFoldersAsync();
            Assert(MediaLibraries.FromJellyfin(folders) == new MediaLibraries(@"C:\Media\FILMS", @"C:\Media\SERIES", @"C:\Media\ANIME"), "Libraries not matched to films, series and anime");
            Assert(MediaLibraries.FromJellyfin(folders, anime: @"D:\Anime").Anime == @"D:\Anime" && MediaLibraries.FromJellyfin(null).IsEmpty, "Manual folder or missing server data");
            Assert(await client.ReportMediaChangedAsync([@"C:\Media\FILMS\Big Buck Bunny (2008)"]), "Media report failed");
            var report = requests.Last();
            Assert(report.Method == "POST" && report.Path == "/jellyfin/Library/Media/Updated" && report.Body.Contains("\"Updates\"") && report.Body.Contains("\"UpdateType\":\"Created\"") && report.Body.Contains("Big Buck Bunny (2008)"), "Media report request changed: " + report.Body);
            var before = requests.Count;
            var indexed = await client.FindIndexedAsync([@"c:\media\films\big buck bunny (2008)\big buck bunny (2008).mkv", @"C:\Media\SERIES\Show\Season 01\Show - S01E02.mkv", @"C:\Elsewhere.mkv"]);
            Assert(indexed.Count == 2 && indexed[@"C:\MEDIA\FILMS\Big Buck Bunny (2008)\Big Buck Bunny (2008).mkv"] == "movie-1" && indexed[@"C:\Media\SERIES\Show\Season 01\Show - S01E02.mkv"] == "series-9"
                && !indexed.ContainsKey(@"C:\Elsewhere.mkv") && requests.Count == before + 1, "Indexed file lookup, in a single request");
            Assert((await client.FindIndexedAsync([])).Count == 0 && requests.Count == before + 1, "An empty lookup reached the server");
            Assert(requests.Any(x => x.Path.Contains("fields=Path") && x.Path.Contains("includeItemTypes=Movie,Episode")), "Indexed lookup query changed");
            VirtualFolder Tv(string name, string folder) => new(name, "tvshows", [folder]);
            Assert(MediaLibraries.FromJellyfin([Tv("Dessins animés", @"D:\Cartoons"), Tv("Séries", @"D:\TV")]).Anime is null
                && MediaLibraries.FromJellyfin([Tv("Animation", @"D:\Animation"), Tv("Animaux", @"D:\Nature")]).Anime is null
                && MediaLibraries.FromJellyfin([Tv("Séries", @"D:\TV"), Tv("Japon", @"D:\Médias\Animés")]) is { Anime: @"D:\Médias\Animés", Series: @"D:\TV" }, "Anime library matched on a partial word");
            refuse = true;
            Assert(await client.VirtualFoldersAsync() is null && !await client.ReportMediaChangedAsync([@"C:\Media\FILMS\x"]), "A refused optional call must not end the session");
        });
        await Test("Sagas : l’option de Jellyfin cochée dans chaque bibliothèque de films, ses autres réglages renvoyés tels quels", async () =>
        {
            // As Jellyfin 12.1 lists its libraries (Library/VirtualFolders), options cut down; the second film library already has it.
            var folders = System.Text.Json.Nodes.JsonNode.Parse("""
                [{"Name":"Films","CollectionType":"movies","ItemId":"f1","Locations":["C:\\Films"],"LibraryOptions":{"Enabled":true,"EnableRealtimeMonitor":true,
                  "AutomaticallyAddToCollection":false,"PathInfos":[{"Path":"C:\\Films"}],"TypeOptions":[{"Type":"Movie","MetadataFetchers":["TheMovieDb"]}],"PreferredMetadataLanguage":"fr"}},
                 {"Name":"Séries","CollectionType":"tvshows","ItemId":"s1","LibraryOptions":{"AutomaticallyAddToCollection":false}},
                 {"Name":"Films 4K","CollectionType":"movies","ItemId":"f2","LibraryOptions":{"AutomaticallyAddToCollection":true}},
                 {"Name":"Collections","CollectionType":"boxsets","ItemId":"b1","LibraryOptions":{}}]
                """)!.AsArray();
            var posted = new List<System.Text.Json.Nodes.JsonNode>(); var refuse = false;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
            {
                if (refuse) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/Library/VirtualFolders") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(folders.ToJsonString(), Encoding.UTF8, "application/json") };
                if (path == "/Library/VirtualFolders/LibraryOptions" && request.Method == HttpMethod.Post)
                {
                    var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                    posted.Add(body);
                    folders.First(x => (string?)x!["ItemId"] == (string?)body["Id"])!["LibraryOptions"] = body["LibraryOptions"]!.DeepClone();
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (path == "/Items?userId=user-1&includeItemTypes=BoxSet&recursive=true&limit=0") return JsonResponse(new { Items = Array.Empty<object>(), TotalRecordCount = 3 });
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var original = folders[0]!["LibraryOptions"]!.DeepClone();
            Assert(await client.SagasAsync() == false, "Off in Films");
            Assert(await client.EnableSagasAsync() && posted.Count == 1 && (string?)posted[0]["Id"] == "f1", "Posted: " + string.Join(" ", posted.Select(x => x.ToJsonString())));
            var expected = original.DeepClone(); expected["AutomaticallyAddToCollection"] = true;
            Assert(System.Text.Json.Nodes.JsonNode.DeepEquals(posted[0]["LibraryOptions"], expected), "Other options changed: " + posted[0]["LibraryOptions"]!.ToJsonString());
            Assert(await client.SagasAsync() == true && await client.EnableSagasAsync() && posted.Count == 1 && await client.SagaCountAsync() == 3, "On everywhere: nothing more to send");
            // A Jellyfin older than the option, no film library, an account that is not an administrator: Mira cannot tell.
            folders[0]!["LibraryOptions"]!.AsObject().Remove("AutomaticallyAddToCollection");
            Assert(await client.SagasAsync() is null && await client.EnableSagasAsync() && posted.Count == 1, "Without the option");
            folders.RemoveAt(2); folders.RemoveAt(0);
            Assert(await client.SagasAsync() is null, "Without a film library");
            refuse = true;
            Assert(await client.SagasAsync() is null && !await client.EnableSagasAsync(), "Refused");
        });
        await Test("Aperçus de la barre : l’option trickplay cochée dans les films et les séries, puis la tâche de Jellyfin lancée", async () =>
        {
            var folders = System.Text.Json.Nodes.JsonNode.Parse("""
                [{"Name":"Films","CollectionType":"movies","ItemId":"f1","LibraryOptions":{"EnableTrickplayImageExtraction":false,"AutomaticallyAddToCollection":true,"PathInfos":[{"Path":"C:\\Films"}]}},
                 {"Name":"Séries","CollectionType":"tvshows","ItemId":"s1","LibraryOptions":{"EnableTrickplayImageExtraction":true}},
                 {"Name":"Musique","CollectionType":"music","ItemId":"m1","LibraryOptions":{"EnableTrickplayImageExtraction":false}}]
                """)!.AsArray();
            var posted = new List<System.Text.Json.Nodes.JsonNode>(); var started = new List<string>(); var state = "Idle";
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/Library/VirtualFolders") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(folders.ToJsonString(), Encoding.UTF8, "application/json") };
                if (path == "/Library/VirtualFolders/LibraryOptions" && request.Method == HttpMethod.Post)
                {
                    var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                    posted.Add(body);
                    folders.First(x => (string?)x!["ItemId"] == (string?)body["Id"])!["LibraryOptions"] = body["LibraryOptions"]!.DeepClone();
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                // As Jellyfin 12.1 lists its tasks, cut down.
                if (path == "/ScheduledTasks?isHidden=false") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""[{"Name":"Scan Media Library","Key":"RefreshLibrary","Id":"a1","State":"Idle"},{"Name":"Generate Trickplay Images","Key":"RefreshTrickplayImages","Id":"t1","State":"{{state}}"{{(state == "Running" ? ",\"CurrentProgressPercentage\":42.5" : "")}}}]""") };
                if (path.StartsWith("/ScheduledTasks/Running/", StringComparison.Ordinal) && request.Method == HttpMethod.Post) { started.Add(path); state = "Running"; return new HttpResponseMessage(HttpStatusCode.NoContent); }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            Assert(await client.TrickplayEnabledAsync() == false && await client.TrickplayProgressAsync() is null, "Off in Films, the task idle");
            Assert(await client.EnableTrickplayAsync() && posted.Count == 1 && (string?)posted[0]["Id"] == "f1" && (bool?)posted[0]["LibraryOptions"]!["EnableTrickplayImageExtraction"] == true
                && (bool?)posted[0]["LibraryOptions"]!["AutomaticallyAddToCollection"] == true && started.SequenceEqual(["/ScheduledTasks/Running/t1"]), "Films only, then the task: " + string.Join(" ", started));
            Assert(await client.TrickplayEnabledAsync() == true && await client.TrickplayProgressAsync() == .425, "On, the task running");
            Assert(await client.EnableTrickplayAsync() && posted.Count == 1 && started.Count == 1, "Running: not started twice");
        });
        await Test("Nouveaux épisodes : séries suivies seulement, une notice par série, marque = date de Jellyfin", async () =>
        {
            // As Jellyfin 12.1 answers (cut down): Courte Web is started (a third watched), Courte HLS never, Courte Anime
            // is a favourite; one more episode of Courte Web came after the mark, and two of the other ones.
            var episodes = """
                {"Items":[
                 {"Id":"a1","Name":"Courte Anime S01E01","Type":"Episode","SeriesId":"anime","SeriesName":"Courte Anime","DateCreated":"2026-10-14T14:07:20.2982336Z","ParentIndexNumber":1,"IndexNumber":1,"UserData":{"Played":false}},
                 {"Id":"h2","Name":"Courte HLS S01E02","Type":"Episode","SeriesId":"hls","SeriesName":"Courte HLS","DateCreated":"2026-10-14T14:07:17.7782335Z","ParentIndexNumber":1,"IndexNumber":2,"UserData":{"Played":false}},
                 {"Id":"w4","Name":"Le retour","Type":"Episode","SeriesId":"web","SeriesName":"Courte Web","DateCreated":"2026-10-14T14:07:17.1Z","ParentIndexNumber":1,"IndexNumber":4,"UserData":{"Played":false}},
                 {"Id":"w3","Name":"Le départ","Type":"Episode","SeriesId":"web","SeriesName":"Courte Web","DateCreated":"2026-10-14T14:07:16.6582334Z","ParentIndexNumber":1,"IndexNumber":3,"UserData":{"Played":false}},
                 {"Id":"w2","Name":"Courte Web S01E02","Type":"Episode","SeriesId":"web","SeriesName":"Courte Web","DateCreated":"2026-10-14T14:07:15.6582334Z","ParentIndexNumber":1,"IndexNumber":2,"UserData":{"PlayedPercentage":50.2,"Played":false}}],"TotalRecordCount":5}
                """;
            var series = """
                {"Items":[
                 {"Id":"anime","Name":"Courte Anime","Type":"Series","RecursiveItemCount":1,"UserData":{"PlayedPercentage":0,"UnplayedItemCount":1,"IsFavorite":true,"Played":false}},
                 {"Id":"hls","Name":"Courte HLS","Type":"Series","RecursiveItemCount":2,"UserData":{"PlayedPercentage":0,"UnplayedItemCount":2,"IsFavorite":false,"Played":false}},
                 {"Id":"web","Name":"Courte Web","Type":"Series","RecursiveItemCount":3,"UserData":{"PlayedPercentage":33.33333333333333,"UnplayedItemCount":2,"IsFavorite":false,"Played":false}}],"TotalRecordCount":3}
                """;
            var asked = new List<string>();
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device-1"), new Handler(request =>
            {
                var path = request.RequestUri!.PathAndQuery; asked.Add(path);
                var body = path.Contains("includeItemTypes=Episode", StringComparison.Ordinal) ? episodes : path.Contains("ids=", StringComparison.Ordinal) ? series : null;
                return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }));
            var (recent, shows) = await client.RecentEpisodesAsync();
            Assert(asked.Count == 2 && asked[0].Contains("isPlayed=false", StringComparison.Ordinal) && asked[0].Contains("sortBy=DateCreated&sortOrder=Descending", StringComparison.Ordinal) && asked[0].Contains("fields=DateCreated", StringComparison.Ordinal)
                && asked[1].Contains("ids=anime,hls,web", StringComparison.Ordinal) && asked[1].Contains("fields=RecursiveItemCount", StringComparison.Ordinal), string.Join(" | ", asked));
            Assert(NewEpisodes.Newest(recent, null) == DateTimeOffset.Parse("2026-10-14T14:07:20.2982336Z", System.Globalization.CultureInfo.InvariantCulture), "Newest");
            var seen = DateTimeOffset.Parse("2026-10-14T14:07:16Z", System.Globalization.CultureInfo.InvariantCulture);
            var (notices, more) = NewEpisodes.Pick(recent, shows, seen);
            Assert(notices.Count == 2 && more is null, string.Join(" | ", notices.Select(x => x.Title)));
            Assert(notices[0] is { SeriesId: "anime", Title: "Courte Anime", Text: "Nouvel épisode : S1 É1 · Courte Anime S01E01", Count: 1 }, notices[0].ToString());
            Assert(notices[1] is { SeriesId: "web", Text: "2 nouveaux épisodes : S1 É3 à É4", Count: 2 } && notices[1].Episode.Id == "w3", notices[1].ToString());
            Assert(NewEpisodes.Pick(recent, shows, NewEpisodes.Newest(recent, seen)!.Value).Notices.Count == 0, "Nothing past the mark");
            Assert(NewEpisodes.Text([recent[3] with { ParentIndexNumber = 1, IndexNumber = 9 }, recent[1] with { ParentIndexNumber = 2, IndexNumber = 1 }]) == "2 nouveaux épisodes, dès S1 É9", "Across seasons");
            var many = Enumerable.Range(0, 5).Select(i => new MediaItem { Id = "e" + i, Type = "Episode", SeriesId = "s" + i, Name = "x", DateCreated = seen.AddMinutes(i + 1) }).ToList();
            var followed = many.ToDictionary(x => x.SeriesId!, x => new MediaItem { Id = x.SeriesId!, Type = "Series", Name = "Série " + x.SeriesId, UserData = new() { IsFavorite = true } });
            var picked = NewEpisodes.Pick(many, followed, seen);
            Assert(picked.Notices.Select(x => x.SeriesId).SequenceEqual(["s4", "s3", "s2"]) && picked.More == "Et 2 autres séries que tu suis ont de nouveaux épisodes.", $"{string.Join(",", picked.Notices.Select(x => x.SeriesId))} {picked.More}");
        });
        await Test("En ce moment sur Jellyfin : qui regarde quoi, tel quel ou converti, sur quel matériel et pourquoi ; « Arrêter »", async () =>
        {
            // As Jellyfin 12.1 lists its sessions (Mira web converting a WebM, cut down), plus a friend on Tailscale playing
            // the file as it is, this PC, and a session that plays nothing.
            var sessions = """
                [{"Id":"s-phone","UserName":"mira","Client":"Mira","DeviceName":"iPhone","DeviceId":"phone-1","RemoteEndPoint":"192.168.1.20","SupportsRemoteControl":true,
                  "PlayState":{"PositionTicks":30058340,"IsPaused":false,"PlayMethod":"Transcode"},
                  "TranscodingInfo":{"AudioCodec":"opus","VideoCodec":"vp9","IsVideoDirect":false,"IsAudioDirect":false,"Bitrate":1552384,"HardwareAccelerationType":"none","TranscodeReasons":["ContainerNotSupported","VideoCodecNotSupported","AudioCodecNotSupported"]},
                  "NowPlayingItem":{"Name":"Signes Test (2022)","Type":"Movie","RunTimeTicks":450230000}},
                 {"Id":"s-friend","UserName":"Léa","Client":"Jellyfin Web","DeviceName":"Firefox","DeviceId":"friend","RemoteEndPoint":"100.101.5.7","SupportsRemoteControl":false,
                  "PlayState":{"PositionTicks":37230000000,"IsPaused":true,"PlayMethod":"DirectPlay"},
                  "NowPlayingItem":{"Name":"Le départ","Type":"Episode","SeriesName":"Courte HLS","ParentIndexNumber":1,"IndexNumber":2,"RunTimeTicks":48000000000}},
                 {"Id":"s-pc","UserName":"mira","Client":"Mira","DeviceName":"Windows","DeviceId":"device-1","RemoteEndPoint":"127.0.0.1","SupportsRemoteControl":true,
                  "PlayState":{"PositionTicks":0,"PlayMethod":"Transcode"},
                  "TranscodingInfo":{"AudioCodec":"aac","VideoCodec":"h264","IsVideoDirect":true,"IsAudioDirect":false,"HardwareAccelerationType":"nvenc","TranscodeReasons":["AudioCodecNotSupported","Bogus"]},
                  "NowPlayingItem":{"Name":"Aube Test (2021)","Type":"Movie","RunTimeTicks":400230000}},
                 {"Id":"s-idle","UserName":"mira","Client":"Mira","DeviceName":"iPad","DeviceId":"ipad"}]
                """;
            string? stopped = null; var refuse = false;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device-1"), new Handler(request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/Sessions?activeWithinSeconds=960") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessions, Encoding.UTF8, "application/json") });
                if (path.StartsWith("/Sessions/", StringComparison.Ordinal) && path.EndsWith("/Playing/Stop", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                { if (refuse) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); stopped = path; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }));
            var viewers = await client.ViewersAsync();
            Assert(viewers.Count == 3 && viewers.Select(x => x.SessionId).SequenceEqual(["s-phone", "s-friend", "s-pc"]), "Playing only: " + string.Join(",", viewers.Select(x => x.SessionId)));
            var (phone, friend, pc) = (viewers[0], viewers[1], viewers[2]);
            Assert(Viewers.Who(phone) == "mira · iPhone · réseau de la maison" && Viewers.Who(friend) == "Léa · Firefox · Tailscale" && Viewers.Who(pc) == "ce PC", $"{Viewers.Who(phone)} | {Viewers.Who(friend)} | {Viewers.Who(pc)}");
            Assert(Viewers.What(phone) == "Signes Test (2022) — 0:03 / 0:45" && Viewers.What(friend) == "Courte HLS · S1 É2 · Le départ — 1:02:03 / 1:20:00, en pause", $"{Viewers.What(phone)} | {Viewers.What(friend)}");
            Assert(Viewers.How(phone) == ("Conversion : image en VP9, son en Opus, par le processeur · 1,6 Mb/s", "Pourquoi : format du fichier, codec vidéo, codec audio"), Viewers.How(phone).ToString());
            Assert(Viewers.How(friend) == ("Lecture directe : le fichier tel quel", null) && Viewers.How(pc) == ("Conversion : image copiée, son en AAC", "Pourquoi : codec audio, Bogus"), $"{Viewers.How(friend)} | {Viewers.How(pc)}");
            Assert(Viewers.How(phone with { Conversion = phone.Conversion! with { Hardware = "qsv" } }).How.EndsWith("par la carte graphique (Intel) · 1,6 Mb/s", StringComparison.Ordinal), "On the card");
            Assert(phone.CanStop && !friend.CanStop && await client.StopViewerAsync("s-phone") && stopped == "/Sessions/s-phone/Playing/Stop", "Stop: " + stopped);
            refuse = true;
            Assert(!await client.StopViewerAsync("s-friend"), "Refused");
        });
        await Test("Carte graphique : NVIDIA, puis Intel, puis AMD ; un adaptateur sans fabricant connu n’en propose aucune", () =>
        {
            var cards = GpuEncoding.Candidates(["Microsoft Basic Display Adapter", "AMD Radeon RX 6600", "Intel(R) UHD Graphics 770", null, " ", "NVIDIA GeForce RTX 3060", "NVIDIA GeForce GT 710", "Parsec Virtual Display Adapter"]);
            Assert(string.Join(" | ", cards.Select(x => $"{x.Kind}={x.Name}")) == "nvenc=NVIDIA GeForce RTX 3060 | qsv=Intel(R) UHD Graphics 770 | amf=AMD Radeon RX 6600", string.Join(" | ", cards));
            Assert(GpuEncoding.Candidates(["Advanced Micro Devices, Inc. Radeon"]).Single().Kind == "amf" && GpuEncoding.Candidates(["Microsoft Remote Display Adapter"]).Count == 0, "Makers");
            Assert(GpuEncoding.Maker("qsv") == "Intel" && GpuEncoding.Maker("vaapi") == "vaapi", "Maker");
            return Task.CompletedTask;
        });
        await Test("Carte graphique : chaque carte essayée sur une conversion, la première qui marche gardée, sinon le réglage de Jellyfin remis", async () =>
        {
            // As Jellyfin 12.1 gives its encoding settings (System/Configuration/encoding), cut down.
            var encoding = System.Text.Json.Nodes.JsonNode.Parse("""{"HardwareAccelerationType":"none","EnableHardwareEncoding":false,"HardwareDecodingCodecs":["h264","vc1"],"H264Crf":23}""")!.AsObject();
            var original = encoding.DeepClone();
            var working = new HashSet<string>(); var tried = new List<string>(); var stopped = 0; var titles = true; var refuse = false;
            string? streamed = null;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device-1"), new Handler(async request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/System/Configuration/encoding")
                {
                    if (refuse) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(encoding.ToJsonString(), Encoding.UTF8, "application/json") };
                    encoding = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (path.StartsWith("/Items?userId=user-1&recursive=true&includeItemTypes=Movie,Episode", StringComparison.Ordinal))
                    return JsonResponse(new { Items = titles ? new object[] { new { Id = "vp9", MediaStreams = new[] { new { Type = "Video", Codec = "vp9" } } }, new { Id = "h264", MediaStreams = new[] { new { Type = "Audio", Codec = "aac" }, new { Type = "Video", Codec = "h264" } } } } : [] });
                if (path.StartsWith("/Videos/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                {
                    streamed = path; var kind = (string)encoding["HardwareAccelerationType"]!; tried.Add(kind);
                    if (!working.Contains(kind)) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[300 * 1024]) };
                }
                if (path.StartsWith("/Videos/ActiveEncodings?deviceId=device-1&playSessionId=", StringComparison.Ordinal) && request.Method == HttpMethod.Delete) { stopped++; return new HttpResponseMessage(HttpStatusCode.NoContent); }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var cards = GpuEncoding.Candidates(["Intel(R) UHD Graphics", "NVIDIA GeForce GTX 1050"]);
            Assert(await client.HardwareAccelerationAsync() == "none", "Read");
            working.Add("qsv");
            var (outcome, card) = await GpuEncoding.EnableAsync(client, cards, TimeSpan.FromSeconds(5));
            Assert(outcome == GpuOutcome.Done && card?.Kind == "qsv" && string.Join(",", tried) == "nvenc,qsv" && stopped == 2, $"{outcome} {card} {string.Join(",", tried)} {stopped}");
            Assert(streamed!.StartsWith("/Videos/h264/stream.ts?static=false&videoCodec=h264&", StringComparison.Ordinal) && streamed.Contains("allowVideoStreamCopy=false") && streamed.Contains("deviceId=device-1"), "H.264 title, converted: " + streamed);
            var expected = original.DeepClone(); expected["HardwareAccelerationType"] = "qsv"; expected["EnableHardwareEncoding"] = true;
            Assert(System.Text.Json.Nodes.JsonNode.DeepEquals(encoding, expected), "Other settings changed: " + encoding.ToJsonString());
            // No card converts: Jellyfin is put back as it was, its other settings untouched.
            Assert(await client.SetHardwareAccelerationAsync("none") && (string?)encoding["HardwareAccelerationType"] == "none", "Back to the processor");
            working.Clear(); tried.Clear(); encoding["EnableHardwareEncoding"] = false;
            Assert(await GpuEncoding.EnableAsync(client, cards, TimeSpan.FromSeconds(5)) is (GpuOutcome.Failed, null) && tried.Count == 2, "Failed: " + string.Join(",", tried));
            Assert(System.Text.Json.Nodes.JsonNode.DeepEquals(encoding["HardwareAccelerationType"], "none"), "Put back: " + encoding.ToJsonString());
            titles = false;
            Assert(await GpuEncoding.EnableAsync(client, cards, TimeSpan.FromSeconds(5)) is (GpuOutcome.NothingToTry, null) && (string?)encoding["HardwareAccelerationType"] == "none", "Nothing to try");
            refuse = true;
            Assert(await GpuEncoding.EnableAsync(client, cards, TimeSpan.FromSeconds(5)) is (GpuOutcome.Refused, null) && await client.HardwareAccelerationAsync() is null, "Refused");
        });
        await Test("Sur ce PC : un film gardé, repris là où il s’est coupé, avec ses sous-titres ; par compte, dans l’ordre, épisode suivant sans Jellyfin", async () =>
        {
            var root = Path.Combine(testRoot, "offline"); var other = Path.Combine(testRoot, "offline-usb");
            var connection = new Connection("http://nas:8096/", "user-1", "Alice", "token", "device-1");
            var video = Enumerable.Range(0, 300_000).Select(x => (byte)(x % 251)).ToArray();
            var ranges = new List<string>(); var cut = true; var ignoreRange = false; var refuse = false;
            using var client = new JellyfinClient(connection, new Handler(request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/Items/film/Download")
                {
                    if (refuse) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                    var from = ignoreRange ? 0 : request.Headers.Range?.Ranges.Single().From ?? 0; ranges.Add(request.Headers.Range?.ToString() ?? "");
                    // The first answer stops halfway: the connection was cut.
                    var body = video[(int)from..(cut ? 150_000 : video.Length)];
                    var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
                    response.Content.Headers.ContentLength = video.Length - from;
                    if (from > 0) response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, video.Length - 1, video.Length);
                    return Task.FromResult(response);
                }
                if (path == "/Videos/film/src/Subtitles/2/0/Stream.srt") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1\n00:00:01,000 --> 00:00:02,000\nBonjour\n") });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }));
            var source = new MediaSource { Id = "src", Container = "mkv", Path = @"D:\Films\Dune (2021)\Dune.MKV", Size = video.Length, MediaStreams = [new() { Index = 2, Type = "Subtitle", Codec = "subrip", IsExternal = true, Language = "fre", DisplayTitle = "Français" }, new() { Index = 3, Type = "Subtitle", Codec = "ass", IsExternal = false }] };
            var film = new MediaItem { Id = "film", Name = "Dune", Type = "Movie", MediaSources = [source] };
            var copy = OfflineCopies.Plan(root, connection, film, source, DateTimeOffset.UnixEpoch);
            Assert(copy.File == "video.mkv" && copy.Item.MediaSources is null && OfflineCopies.List([root], connection).Single() is { Complete: false, Size: 300_000 }, "Planned, listed before it comes");
            var seen = new List<long>();
            try { await OfflineCopies.FetchAsync(client, copy, new SyncProgress<(long Done, long Total)>(x => seen.Add(x.Done)), CancellationToken.None); Assert(false, "Cut, not complete"); }
            catch (HttpRequestException ex) { Assert(ex.StatusCode is null && new FileInfo(copy.PartPath).Length == 150_000 && !File.Exists(copy.VideoPath), "Part kept: " + ex.Message); }
            cut = false;
            copy = await OfflineCopies.FetchAsync(client, OfflineCopies.Read(copy.Folder)!, null, CancellationToken.None);
            Assert(ranges.SequenceEqual(["", "bytes=150000-"]) && File.ReadAllBytes(copy.VideoPath).SequenceEqual(video) && !File.Exists(copy.PartPath), "Resumed: " + string.Join(" | ", ranges));
            Assert(copy.Complete && copy.Subtitles.Single() is { File: "subtitle-2.srt", Language: "fre", Title: "Français" } && File.ReadAllText(Path.Combine(copy.Folder, "subtitle-2.srt")).Contains("Bonjour"), "External subtitle only");
            Assert(OfflineCopies.Read(copy.Folder) is { Complete: true } read && read.BytesOnDisk > 300_000 && (await OfflineCopies.FetchAsync(client, read, null, CancellationToken.None)) == read with { } && ranges.Count == 2, "Complete: not fetched again");
            // A server that ignores the range: from the start again, never a file with its beginning twice.
            ignoreRange = true; ranges.Clear();
            var again = OfflineCopies.Plan(root, connection, film with { Id = "film" }, source, DateTimeOffset.UnixEpoch);
            File.Delete(again.VideoPath); File.WriteAllBytes(again.PartPath, new byte[1000]);
            again = await OfflineCopies.FetchAsync(client, again, null, CancellationToken.None);
            Assert(ranges.SequenceEqual(["bytes=1000-"]) && File.ReadAllBytes(again.VideoPath).SequenceEqual(video), "Range ignored: from the start");
            refuse = true;
            var refused = OfflineCopies.Plan(other, connection, film, source, DateTimeOffset.UnixEpoch);
            try { await OfflineCopies.FetchAsync(client, refused, null, CancellationToken.None); Assert(false, "Refused"); }
            catch (OfflineRefusedException ex) { Assert(ex.Message.Contains("autorise le téléchargement"), ex.Message); }

            // Another account sees none of them; a folder that is not one of a copy is left out.
            var bob = connection with { UserId = "user-2" };
            Assert(OfflineCopies.List([root, other], bob).Count == 0 && OfflineCopies.AccountFolder(root, connection) != OfflineCopies.AccountFolder(root, bob), "Per account");
            Directory.CreateDirectory(Path.Combine(OfflineCopies.AccountFolder(root, connection), "junk"));
            File.WriteAllText(Path.Combine(OfflineCopies.AccountFolder(root, connection), "junk", OfflineCopies.Manifest), "{ not json");
            var listed = OfflineCopies.List([root, other, root], connection);
            Assert(listed.Count == 1 && listed[0].Complete && listed[0].Folder.StartsWith(root), "One per title, the complete one: " + listed.Count);
            // A manifest naming a file outside its folder is not a copy.
            File.WriteAllText(Path.Combine(copy.Folder, OfflineCopies.Manifest), File.ReadAllText(Path.Combine(copy.Folder, OfflineCopies.Manifest)).Replace("\"video.mkv\"", "\"..\\\\x.mkv\""));
            Assert(OfflineCopies.Read(copy.Folder) is null, "Name outside its folder");

            MediaItem Episode(string id, int season, int number) => new() { Id = id, Type = "Episode", SeriesId = "s", SeriesName = "Série", ParentIndexNumber = season, IndexNumber = number };
            OfflineCopy Kept(MediaItem item, int day, bool complete = true) => new() { Item = item, Complete = complete, Added = DateTimeOffset.UnixEpoch.AddDays(day) };
            var copies = new[] { Kept(Episode("e3", 1, 3), 1), Kept(Episode("e1", 1, 1), 2), Kept(Episode("s2e1", 2, 1), 3, complete: false), Kept(new MediaItem { Id = "m", Type = "Movie" }, 1) };
            Assert(OfflineCopies.NextEpisode(copies, Episode("e1", 1, 1))?.Item.Id == "e3" && OfflineCopies.NextEpisode(copies, Episode("e3", 1, 3)) is null && OfflineCopies.NextEpisode(copies, new MediaItem { Id = "m" }) is null, "Next kept episode, complete only");
            Assert(string.Join(",", OfflineCopies.Ordered(copies).Select(x => x.Item.Id)) == "e1,e3,s2e1,m", "Series together, in order, newest first: " + string.Join(",", OfflineCopies.Ordered(copies).Select(x => x.Item.Id)));
            Assert(OfflineCopies.SizeText(850L << 20) == "850 Mo" && OfflineCopies.SizeText((long)(4.2 * (1L << 30))) == "4,2 Go" && OfflineCopies.SizeText(12L << 30) == "12 Go", "Sizes");
            Assert(OfflineCopies.Extension(new MediaSource { Container = "mov,mp4,m4a" }) == "mov" && OfflineCopies.Extension(new MediaSource { Path = "/x/a.WEBM" }) == "webm" && OfflineCopies.Extension(new MediaSource { Path = "/x/a.b/c", Container = "../x" }) == "mkv", "Extension");
            Assert(OfflineCopies.SubtitleExtension("ass") == "ass" && OfflineCopies.SubtitleExtension("PGSSUB") == "srt" && OfflineCopies.SubtitleExtension("webvtt") == "vtt", "Subtitle formats");
            Assert(OfflineCopies.Erase([root, other], connection) && OfflineCopies.List([root, other], connection).Count == 0, "Erased with the account");
        });
    }
}
