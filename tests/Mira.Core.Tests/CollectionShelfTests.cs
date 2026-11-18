using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using static TestSupport;

/// <summary>
/// « L'étagère »: a collection grouped in the library (CollectionShelf), its card in the grids, its titles in order; with
/// MIRA_SHELF_JELLYFIN (« url user password »), against a real Jellyfin (a collection made, nested, read and deleted).
/// </summary>
internal static class CollectionShelfTests
{
    private static MediaItem Title(string id, string type, int year, bool played = false, double? percent = null, long position = 0, int? episodes = null) => new()
    {
        Id = id, Name = id, Type = type, ProductionYear = year, RunTimeTicks = type == "Movie" ? TimeSpan.FromMinutes(100).Ticks : null, RecursiveItemCount = episodes,
        UserData = new UserData { Played = played, PlayedPercentage = percent, PlaybackPositionTicks = position },
    };
    private static MediaItem Box(string id, bool saga = false) => new() { Id = id, Name = id, Type = "BoxSet", ProviderIds = saga ? new() { ["Tmdb"] = "1" } : new() };

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Collections groupées : une saga de TMDb l'est d'elle-même, une collection faite à la main seulement si on le demande ; choix relus tels qu'écrits", () =>
        {
            Assert(CollectionShelf.Grouped(Box("s", saga: true), null) && !CollectionShelf.Grouped(Box("m"), null), "Défauts : saga oui, à la main non.");
            Assert(CollectionShelf.Grouped(Box("m"), new(true)) && !CollectionShelf.Grouped(Box("s", true), new(false)), "Le choix l'emporte.");
            var written = CollectionShelf.Write(new Dictionary<string, CollectionShelf.Choice> { ["b"] = new(true, ["t2", "t1"]), ["a"] = new(false), ["empty"] = new() });
            var read = CollectionShelf.Read(JsonValue.Create(written));
            Assert(read.Count == 2 && read["b"].Grouped == true && read["b"].Order!.SequenceEqual(["t2", "t1"]) && read["a"].Grouped == false && read["a"].Order is null, written);
            Assert(CollectionShelf.Read(JsonValue.Create("pas du json")).Count == 0 && CollectionShelf.Read(null).Count == 0, "Illisible = rien.");
            return Task.CompletedTask;
        });
        await Test("Collections groupées : une collection dans une autre est aplatie, chaque titre une fois", () =>
        {
            var children = new Dictionary<string, IReadOnlyList<MediaItem>>
            {
                ["mono"] = [Title("bake", "Series", 2009), Box("kizu"), Title("nise", "Series", 2012)],
                ["kizu"] = [Title("kizu1", "Movie", 2016), Title("kizu2", "Movie", 2016), Title("bake", "Series", 2009), Box("mono")],
            };
            var titles = CollectionShelf.Flatten("mono", id => children.GetValueOrDefault(id));
            Assert(titles.Select(x => x.Id).SequenceEqual(["bake", "kizu1", "kizu2", "nise"]), string.Join(",", titles.Select(x => x.Id)));
            return Task.CompletedTask;
        });
        await Test("Collections groupées : ordre de visionnage choisi, puis les titres ajoutés depuis par date de sortie ; déplacer un titre", () =>
        {
            MediaItem[] titles = [Title("nise", "Series", 2012), Title("kizu1", "Movie", 2016), Title("bake", "Series", 2009), Title("neko", "Series", 2012)];
            Assert(CollectionShelf.InOrder(titles, null).Select(x => x.Id).SequenceEqual(["bake", "neko", "nise", "kizu1"]), "Sans ordre : la sortie (année, puis nom).");
            var ordered = CollectionShelf.InOrder(titles, ["bake", "kizu1", "gone", "nise"]);
            Assert(ordered.Select(x => x.Id).SequenceEqual(["bake", "kizu1", "nise", "neko"]), string.Join(",", ordered.Select(x => x.Id)));
            Assert(CollectionShelf.Move(ordered, 3, -1).SequenceEqual(["bake", "kizu1", "neko", "nise"]) && CollectionShelf.Move(ordered, 0, -1).SequenceEqual(ordered.Select(x => x.Id)), "Monter, pas plus haut que le premier.");
            return Task.CompletedTask;
        });
        await Test("Collections groupées : la carte prend la place du premier titre de la page, une fois ; un seul titre de ce genre = le titre ; la plus grande collection gagne", () =>
        {
            var bake = Title("bake", "Series", 2009); var nise = Title("nise", "Series", 2012); var kizu1 = Title("kizu1", "Movie", 2016); var kizu2 = Title("kizu2", "Movie", 2016);
            var lone = Title("lone", "Movie", 2001); var other = Title("other", "Series", 2020);
            var index = new CollectionShelf.Index([
                new(Box("mono"), [bake, nise, kizu1, kizu2]),
                new(Box("kizu", saga: true), [kizu1, kizu2]),
                new(Box("mixed"), [lone, other]),
            ]);
            Assert(index.Of("kizu1")!.Box.Id == "mono", "Un titre de deux collections : la plus grande.");
            var placed = new HashSet<string>();
            var first = CollectionShelf.Collapse([other, nise, lone, bake], index, "Movie,Series", placed);
            Assert(first.Select(x => x.Id).SequenceEqual(["mixed", "mono"]), string.Join(",", first.Select(x => x.Id)));
            var second = CollectionShelf.Collapse([kizu1, kizu2], index, "Movie,Series", placed);
            Assert(second.Count == 0, "Page suivante : la carte est déjà là.");
            var movies = CollectionShelf.Collapse([lone, kizu2, kizu1], index, "Movie", new HashSet<string>());
            Assert(movies.Select(x => x.Id).SequenceEqual(["lone", "mono"]), "Films : « mixed » n'y a qu'un film, il reste lui-même : " + string.Join(",", movies.Select(x => x.Id)));
            var card = first[1];
            Assert(card.Type == "BoxSet" && card.ChildCount == 4 && card.Shelf!.Count == 4 && Artwork.Poster(card).ItemId == "bake", "Carte : 4 titres, affiche du premier faute de la sienne.");
            var catalog = new Catalog { View = "library", Grouping = index };
            catalog.Receive(new ItemsResult { Items = [other, nise, lone, bake], TotalRecordCount = 6 }, more: false);
            Assert(catalog.Fetched == 4 && catalog.HasMore && catalog.Items.Count == 2, $"Envoyés {catalog.Fetched}, montrés {catalog.Items.Count}");
            catalog.Receive(new ItemsResult { Items = [kizu1, kizu2], TotalRecordCount = 6 }, more: true);
            Assert(catalog.Fetched == 6 && !catalog.HasMore && catalog.Items.Count == 2, $"Envoyés {catalog.Fetched}, montrés {catalog.Items.Count}");
            Assert(catalog.CountLine == CollectionShelf.GridCount(0, 2), catalog.CountLine);
            catalog.Receive(new ItemsResult { Items = [other, nise], TotalRecordCount = 2 }, more: false, search: "a");
            Assert(catalog.Items.Count == 2 && catalog.Items.All(x => x.Shelf is null), "Une recherche montre les titres.");
            return Task.CompletedTask;
        });
        await Test("Collections groupées : « UP NEXT » = le premier titre pas fini, progression et faits de la page", () =>
        {
            MediaItem[] titles = [Title("bake", "Series", 2009, played: true, episodes: 15), Title("kizu1", "Movie", 2016, played: true), Title("nise", "Series", 2012, percent: 25, episodes: 11), Title("kizu2", "Movie", 2017)];
            Assert(CollectionShelf.UpNext(titles) == 2 && CollectionShelf.UpNext([titles[0]]) is null, "UP NEXT");
            Assert(Math.Abs(CollectionShelf.Share(titles) - (1 + 1 + .25) / 4) < 1e-9, "Barre : " + CollectionShelf.Share(titles));
            var was = Lang.Code; Lang.Use(Lang.English);
            try
            {
                Assert(CollectionShelf.Facts(titles) == "2009 – 2017  ·  2 shows  ·  2 movies  ·  26 episodes", CollectionShelf.Facts(titles));
                Assert(CollectionShelf.CardLine(17) == "Collection   ·   17 titles" && CollectionShelf.Number(0) == "01", CollectionShelf.CardLine(17));
                Assert(CollectionShelf.TitleLine(titles[0]) == "2009  ·  15 episodes", CollectionShelf.TitleLine(titles[0]));
            }
            finally { Lang.Use(was); }
            return Task.CompletedTask;
        });
        await Test("Collections groupées : cas communs avec l'iPhone (tests/fixtures/shelf.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "shelf.json")));
            var x = fixture.RootElement;
            var all = x.GetProperty("titles").EnumerateArray().Select(t => t.Deserialize<MediaItem>(Json.Options)!).ToDictionary(t => t.Id);
            List<string>? Ids(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(i => i.GetString()!).ToList() : null;
            string Join(IEnumerable<MediaItem> items) => string.Join(",", items.Select(i => i.Id));
            foreach (var row in x.GetProperty("grouped").EnumerateArray())
            {
                var box = new MediaItem { Id = "b", Type = "BoxSet", ProviderIds = row[0].ValueKind == JsonValueKind.Object ? row[0].Deserialize<Dictionary<string, string>>() : null };
                var choice = row[1].ValueKind == JsonValueKind.Null ? null : new CollectionShelf.Choice(row[1].GetBoolean());
                Assert(CollectionShelf.Grouped(box, choice) == row[2].GetBoolean(), $"Regroupée : {row}");
            }
            foreach (var row in x.GetProperty("read").EnumerateArray())
            {
                var written = CollectionShelf.Write(CollectionShelf.Read(JsonValue.Create(row[0].GetString())));
                Assert(written == row[1].GetString(), $"{row[0]} → {written}");
            }
            var titles = Ids(x.GetProperty("release"))!.Select(id => all[id]).ToList();
            var shuffled = titles.AsEnumerable().Reverse().ToList();
            Assert(Join(CollectionShelf.ByRelease(shuffled)) == string.Join(",", Ids(x.GetProperty("release"))!), "Date de sortie : " + Join(CollectionShelf.ByRelease(shuffled)));
            foreach (var row in x.GetProperty("order").EnumerateArray())
                Assert(Join(CollectionShelf.InOrder(shuffled, Ids(row[0]))) == string.Join(",", Ids(row[1])!), $"Ordre {row[0]} : " + Join(CollectionShelf.InOrder(shuffled, Ids(row[0]))));
            foreach (var row in x.GetProperty("move").EnumerateArray())
            {
                var moved = CollectionShelf.Move(titles, row[0].GetInt32(), row[1].GetInt32());
                Assert(string.Join(",", moved) == string.Join(",", Ids(row[2])!), $"Déplacé {row[0]} {row[1]} : " + string.Join(",", moved));
            }
            foreach (var row in x.GetProperty("upNext").EnumerateArray())
            {
                var at = CollectionShelf.UpNext(CollectionShelf.InOrder(titles, Ids(row[0])));
                Assert(at == (row[1].ValueKind == JsonValueKind.Null ? null : row[1].GetInt32()), $"À suivre {row[0]} : {at}");
            }
            Assert(Math.Abs(CollectionShelf.Share(titles) - x.GetProperty("share").GetDouble()) < 1e-6, "Barre : " + CollectionShelf.Share(titles));
            var was = Lang.Code;
            try
            {
                Lang.Use(Lang.French); var french = CollectionShelf.Facts(titles);
                Lang.Use(Lang.English); var english = CollectionShelf.Facts(titles);
                Assert(french == x.GetProperty("facts")[0].GetString() && english == x.GetProperty("facts")[1].GetString(), french + " | " + english);
            }
            finally { Lang.Use(was); }
            var shelves = x.GetProperty("shelves").EnumerateObject().Select(s => new CollectionShelf.Shelf(new MediaItem { Id = s.Name, Name = s.Name, Type = "BoxSet" }, Ids(s.Value)!.Select(id => all[id]).ToList()));
            var index = new CollectionShelf.Index(shelves);
            foreach (var row in x.GetProperty("collapse").EnumerateArray())
            {
                var placed = Ids(row[2])!.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var shown = CollectionShelf.Collapse(Ids(row[1])!.Select(id => all[id]), index, row[0].GetString()!, placed);
                Assert(Join(shown) == string.Join(",", Ids(row[3])!), $"Page {row[1]} : " + Join(shown));
            }
            return Task.CompletedTask;
        });
        if (Environment.GetEnvironmentVariable("MIRA_SHELF_JELLYFIN") is { Length: > 0 } real)
            await Test("Collections groupées : contre un vrai Jellyfin (collection imbriquée aplatie, choix gardés à côté de « mira.choices »)", () => RealAsync(real));
    }

    private static async Task RealAsync(string settings)
    {
        var parts = settings.Split(' ', 3);
        using var anonymous = new JellyfinClient(new Connection(parts[0].TrimEnd('/') + "/", "", "", "", "mira-shelf-check"));
        using var client = new JellyfinClient(await anonymous.LoginAsync(parts[1], parts[2]));
        var titles = (await client.BrowseAsync(limit: 50, everyTitle: true)).Items;
        Assert(titles.Count >= 4, "Il faut 4 titres.");
        var name = "Mira shelf " + Guid.NewGuid().ToString("N")[..6]; var innerName = name + " inner";
        var inner = await client.CreateCollectionAsync(innerName, titles.Take(2).Select(x => x.Id));
        var outer = await client.CreateCollectionAsync(name, titles.Skip(1).Take(3).Select(x => x.Id).Append(inner));
        try
        {
            var read = await client.CollectionTitlesAsync(outer);
            Assert(read.Select(x => x.Id).Order().SequenceEqual(titles.Take(4).Select(x => x.Id).Order()), "Aplatie : " + string.Join(",", read.Select(x => x.Name)));
            Assert(read.All(x => x.Type != "Series" || x.RecursiveItemCount > 0), "Épisodes comptés.");
            var before = (await client.ChoicesAsync())["CustomPrefs"]?[AccountChoices.Preference]?.ToJsonString();
            var after = await client.SetShelfChoiceAsync(outer, x => x with { Grouped = true, Order = [.. read.Select(t => t.Id).Reverse()] });
            Assert((await client.ShelfChoicesAsync())[outer].Order!.SequenceEqual(after[outer].Order!), "Ordre relu.");
            Assert((await client.ChoicesAsync())["CustomPrefs"]?[AccountChoices.Preference]?.ToJsonString() == before, "« mira.choices » intact.");
            var index = await client.ShelvesAsync(await client.ShelfChoicesAsync());
            Assert(index.Shelves.Any(x => x.Box.Id == outer && x.Titles.Count == 4), "La collection groupée est sur l'étagère.");
            Assert(index.Shelves.All(x => x.Box.Id != inner), "La collection imbriquée (faite à la main, non groupée) n'y est pas.");
        }
        finally
        {
            await client.SetShelfChoiceAsync(outer, _ => new());
            await client.DeleteCollectionAsync(outer); await client.DeleteCollectionAsync(inner);
        }
    }
}
