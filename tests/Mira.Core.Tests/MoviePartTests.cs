using System.IO;
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
