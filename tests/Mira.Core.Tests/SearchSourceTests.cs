using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mira.Core.TorLink;
using static TestSupport;

/// <summary>
/// The sites Mira asks beside TorLink's on the PC of Jellyfin (SearchSources, TorrentSearch): the forms of a search,
/// each site's answer read as it comes (samples cut from real answers of 2026-11-14), the lists gathered; then, with
/// MIRA_SEARCH_LIVE=1 (ci.yml, a runner that reaches them), the real sites.
/// </summary>
internal static class SearchSourceTests
{
    // Knaben: a title of The Pirate Bay cut with « ... » (whole in its description), one whose description and magnet
    // come HTML-escaped, an anime of Nyaa without magnet (filed in films too), one of TokyoTosho in base32, a book, no hash.
    private const string KnabenSample = """
        {"hits":[
         {"bytes":16363825397,"cachedOrigin":"The Pirate Bay (proxy)","category":"TV / HD","categoryId":[2000000,2001000],"date":"2024-11-24T00:00:00+00:00","description":"Uploader: TGxGoodies<br>Arcane.S02.COMPLETE.REPACK.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FL","hash":"32A3F11F9CCA920F4AF99BBA725D7AE0BF9E10E9","magnetUrl":"magnet:?xt=urn:btih:32A3F11F9CCA920F4AF99BBA725D7AE0BF9E10E9&dn=Arcane.S02.COMPLETE.REPACK.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FL&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337","peers":124,"seeders":425,"liveSeeders":353,"title":"Arcane.S02.COMPLETE.REPACK.1080p.NF.WEB-DL.DDP5.1.Atmos ...","tracker":"The Pirate Bay","trackerId":"thepiratebay"},
         {"bytes":1503238553,"categoryId":[2001000,2000000],"date":"2021-11-20T10:00:00","description":"Uploader: someone&lt;br&gt;Arcane (2021) Season 1 S01 (1080p NF WEB-DL x265 HEVC 10bit EAC3 5.1 Silence)","hash":"0123456789abcdef0123456789abcdef01234567","magnetUrl":"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&amp;dn=Arcane&amp;tr=udp%3A%2F%2Fopen.stealth.si%3A80","peers":90,"seeders":null,"liveSeeders":392,"title":"Arcane (2021) Season 1 S01 (1080p NF WEB-DL x265 HEVC 10bit EAC3 ...","tracker":"The Pirate Bay"},
         {"bytes":1400000000,"categoryId":[6001000,6000000,3008000,3000000],"date":"2026-02-13T17:00:00+00:00","hash":"ABCDEF0123456789ABCDEF0123456789ABCDEF01","magnetUrl":null,"peers":3,"seeders":682,"title":"[SubsPlease] Sousou no Frieren S2 - 06 (1080p) [ABCD1234].mkv","tracker":"Nyaa"},
         {"bytes":300000000,"categoryId":[6000000,6001000],"hash":"ISYBVGQ2JYXPMCJVQOI4KL5CJDR7RC4T","magnetUrl":"magnet:?xt=urn:btih:ISYBVGQ2JYXPMCJVQOI4KL5CJDR7RC4T&dn=Frieren","peers":0,"seeders":5,"title":"[Erai-raws] Sousou no Frieren - 01 [1080p].mkv","tracker":"TokyoTosho"},
         {"bytes":1000,"categoryId":[9000000,6006001,6006000,6000000],"hash":"1111111111111111111111111111111111111111","seeders":50,"title":"Sousou no Frieren Vol. 1 (Manga)","tracker":"Nyaa"},
         {"bytes":1000,"categoryId":[2000000],"seeders":50,"title":"No hash at all"}
        ]}
        """;

    // AniList: Frieren (its English name with a curly apostrophe, as AniList writes it; a synonym of its own), Attack on Titan, Dandadan
    // (« DAN DA DAN »), an adult one never asked for, one without an English name.
    private const string AniListSample = """
        {"data":{"Page":{"media":[
         {"title":{"romaji":"Enen no Shouboutai","english":"Fire Force"},"synonyms":["Fire Brigade of Flames"]},
         {"title":{"romaji":"Sousou no Frieren","english":"Frieren: Beyond Journey’s End"},"synonyms":["Frieren at the Funeral","葬送的芙莉莲","Frieren"]},
         {"title":{"romaji":"Sousou no Frieren 2nd Season","english":"Frieren: Beyond Journey’s End Season 2"},"synonyms":[]},
         {"title":{"romaji":"Shingeki no Kyojin","english":"Attack on Titan"},"synonyms":["AoT","SnK"]},
         {"title":{"romaji":"Dandadan","english":"DAN DA DAN"},"synonyms":[]},
         {"title":{"romaji":"Tabi no Kyori","english":null},"synonyms":null}
        ]}}}
        """;

    private const string AnimeToshoSample = """
        [
         {"id":1,"title":"[BlackRabbit] Frieren - Beyond Journey's End (2023) - S01 v2 [Bluray-1080p][AV1]","info_hash":"3333333333333333333333333333333333333333","magnet_uri":null,"total_size":20000000000,"seeders":48,"leechers":10,"timestamp":1730000000},
         {"id":2,"title":"[SubsPlease] Sousou no Frieren - 28 (1080p) [AAAA1111].mkv","info_hash":"2222222222222222222222222222222222222222","magnet_uri":"magnet:?xt=urn:btih:2222222222222222222222222222222222222222&tr=http%3A%2F%2Fnyaa.tracker.wf%3A7777%2Fannounce","total_size":1430000000,"seeders":120,"leechers":4,"timestamp":1711000000},
         {"id":3,"title":"[Group] Something Else - 01","info_hash":"4444444444444444444444444444444444444444","seeders":999},
         {"id":4,"title":"[Group] Frieren - 02","seeders":10}
        ]
        """;

    private const string TorrentsCsvSample = """
        {"torrents":[
         {"infohash":"5555555555555555555555555555555555555555","name":"Arcane (2021) Season 1 S01 (1080p NF WEB-DL x265 HEVC 10bit EAC3 5.1 Silence)","size_bytes":11000000000,"created_unix":1637000000,"seeders":393,"leechers":20,"completed":1000,"scraped_date":1731000000,"id":1},
         {"infohash":"6666666666666666666666666666666666666666","name":"Librarian - Tidy Up the Arcane Library! [FitGirl Repack]","size_bytes":3000000000,"created_unix":1700000000,"seeders":189,"leechers":5,"id":2},
         {"infohash":"7777777777777777777777777777777777777777","name":"Arcane Merchant","size_bytes":100,"seeders":19,"leechers":0,"id":3},
         {"infohash":"nothex","name":"Arcane S01E01 1080p","seeders":5,"id":4},
         {"infohash":"8888888888888888888888888888888888888888","name":"Dune Part Two 2024 1080p","seeders":500,"id":5}
        ],"next":123989}
        """;

    private static AgentHit Hit(string id, string name, int seeders, string source = "tpb-tv", long size = 1000) =>
        new(id.PadRight(40, '0')[..40], name, size, seeders, 0, source, $"magnet:?xt=urn:btih:{id.PadRight(40, '0')[..40]}", null);

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Recherche : chaque site aussi sans accents ni apostrophe (« JoJo's » et « JoJos », « Shōgun » et « Shogun »)", () =>
        {
            foreach (var (typed, forms) in new (string, string[])[]
            {
                ("JoJo's Bizarre Adventure", ["JoJo's Bizarre Adventure", "JoJos Bizarre Adventure"]),
                ("Marvel’s Agents of S.H.I.E.L.D.", ["Marvel’s Agents of S.H.I.E.L.D.", "Marvels Agents of S.H.I.E.L.D."]),
                ("Shōgun", ["Shōgun", "Shogun"]),
                // An elided word stays a word, as releases write it (« Le.Fabuleux.Destin.d.Amelie.Poulain »).
                ("Le Fabuleux Destin d'Amélie Poulain", ["Le Fabuleux Destin d'Amélie Poulain", "Le Fabuleux Destin d Amelie Poulain"]),
                ("L’Attaque des Titans", ["L’Attaque des Titans", "L Attaque des Titans"]),
                ("Jusqu'à la garde", ["Jusqu'à la garde", "Jusqu a la garde"]),
                ("Rock 'n' Roll", ["Rock 'n' Roll", "Rock n Roll"]),
                ("Ocean's Eight", ["Ocean's Eight", "Oceans Eight"]),
                ("Spider-Man: Across the Spider-Verse", ["Spider-Man: Across the Spider-Verse", "Spider Man Across the Spider Verse"]),
                ("Fast & Furious", ["Fast & Furious", "Fast Furious"]),
                ("Pokémon : la série", ["Pokémon : la série", "Pokemon la serie"]),
                ("Dune Part Two", ["Dune Part Two"]),
                ("Frieren S02E03", ["Frieren S02E03"]),
                // Other scripts keep their marks: « ジョ » is « ショ » with one.
                ("ジョジョの奇妙な冒険", ["ジョジョの奇妙な冒険"]),
                ("Mission: Impossible – Dead Reckoning", ["Mission: Impossible – Dead Reckoning", "Mission Impossible Dead Reckoning"]),
            })
                Assert(SearchSources.Forms(typed).SequenceEqual(forms), $"« {typed} » : {string.Join(" | ", SearchSources.Forms(typed))}");
            Assert(SearchSources.Broad(" - ' ") is null && SearchSources.Broad(null) is null, "Nothing left");
            Assert(SearchSources.For("anime").SequenceEqual([SearchSources.Knaben, SearchSources.AnimeTosho]) && SearchSources.For("tv").SequenceEqual([SearchSources.Knaben, SearchSources.TorrentsCsv])
                && SearchSources.For("movies").SequenceEqual([SearchSources.Knaben, SearchSources.TorrentsCsv]), "Sites by category");
            return Task.CompletedTask;
        });

        await Test("Recherche : un nom qui tient la recherche, une vidéo et pas un jeu (sites qui répondent avec n’importe quel mot)", () =>
        {
            Assert(SearchSources.Holds("JoJo's Bizarre Adventure S06E08 1080p", "JoJos Bizarre Adventure") && SearchSources.Holds("JoJos.Bizarre.Adventure.S06E08", "JoJo's bizarre adventure"), "Apostrophes");
            Assert(SearchSources.Holds("Shogun.2024.S01E03.1080p", "Shōgun S01") && SearchSources.Holds("[SubsPlease] Sousou no Frieren - 05 (1080p)", "Frieren 05"), "Accents, S01 in S01E03, an episode number");
            Assert(SearchSources.Holds("Le.Fabuleux.Destin.d.Amelie.Poulain.2001.FRENCH.1080p", "Le Fabuleux Destin d'Amélie Poulain")
                && SearchSources.Holds("L'Attaque des Titans S04E01 VOSTFR", "L’Attaque des Titans"), "Elided words");
            Assert(SearchSources.Holds("The Fast and the Furious (2001)", "Fast & Furious") && SearchSources.Holds("Fast & Furious 6 (2013)", "Fast and Furious"), "« and », « & » and « the » not needed");
            Assert(!SearchSources.Holds("Arcane Merchant", "Arcane Season 2") && !SearchSources.Holds("Something Else - 01", "Frieren"), "A missing word");
            Assert(SearchSources.LooksLikeVideo("Arcane (2021) Season 1") && SearchSources.LooksLikeVideo("Arcane S02E01 1080p") && SearchSources.LooksLikeVideo("[Erai-raws] Frieren - 01 [1080p].mkv"), "Videos");
            Assert(!SearchSources.LooksLikeVideo("Librarian - Tidy Up the Arcane Library! [FitGirl Repack]") && !SearchSources.LooksLikeVideo("Arcane Merchant")
                && !SearchSources.LooksLikeVideo("Cyberpunk 2077 Ultimate Edition [DODI Repack]"), "Games and nameless things");
            return Task.CompletedTask;
        });

        await Test("Recherche : la réponse de Knaben lue (titres coupés repris, base32, catégories vidéo seulement)", () =>
        {
            var tv = SearchSources.ParseKnaben(KnabenSample, "tv");
            Assert(tv.Count == 4, "Book and hashless left out: " + string.Join(" | ", tv.Select(x => x.Name)));
            Assert(tv[0] is { Id: "32a3f11f9cca920f4af99bba725d7ae0bf9e10e9", Name: "Arcane.S02.COMPLETE.REPACK.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FL", Seeders: 425, Leechers: 124, Size: 16363825397, Source: SearchSources.Knaben, Added: 1732406400 },
                "TPB's cut title taken whole: " + tv[0]);
            Assert(tv[0].Magnet.StartsWith("magnet:?xt=urn:btih:32A3F11F9CCA920F4AF99BBA725D7AE0BF9E10E9&dn=", StringComparison.Ordinal), "Its own magnet kept: " + tv[0].Magnet);
            Assert(tv[1] is { Name: "Arcane (2021) Season 1 S01 (1080p NF WEB-DL x265 HEVC 10bit EAC3 5.1 Silence)", Seeders: 392, Added: 1637402400 } && tv[1].Magnet.Contains("&dn=Arcane&tr=", StringComparison.Ordinal)
                && RemoteDownloads.InfoHash(tv[1].Magnet) == tv[1].Id, "Escaped description and magnet, live sharers when no others, a date without zone: " + tv[1]);
            var tokyo = RemoteDownloads.InfoHash("magnet:?xt=urn:btih:ISYBVGQ2JYXPMCJVQOI4KL5CJDR7RC4T")!;
            Assert(tv.Any(x => x.Id == tokyo && x.Id.Length == 40), "TokyoTosho's base32 hash in hexadecimal");
            var anime = SearchSources.ParseKnaben(KnabenSample, "anime");
            var nyaa = anime.Single(x => x.Id == "abcdef0123456789abcdef0123456789abcdef01");
            Assert(nyaa.Magnet.StartsWith("magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01&dn=%5BSubsPlease%5D%20Sousou", StringComparison.Ordinal)
                && nyaa.Magnet.Contains("&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce", StringComparison.Ordinal) && nyaa.Magnet.Length <= RemoteDownloads.MaxMagnet, "No magnet: made from the hash: " + nyaa.Magnet);
            var movies = SearchSources.ParseKnaben(KnabenSample, "movies");
            Assert(movies.All(x => x.Id != nyaa.Id && x.Id != tokyo) && movies.Count == 2, "Nyaa's anime filed in films stay in the anime search");
            var body = JsonDocument.Parse(SearchSources.KnabenRequest("frieren", "anime", 100).Content!.ReadAsStringAsync().Result).RootElement;
            Assert(body.GetProperty("search_type").GetString() == "100%" && body.GetProperty("order_by").GetString() == "seeders" && body.GetProperty("from").GetInt32() == 100 && body.GetProperty("size").GetInt32() == 100
                && body.GetProperty("categories").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual([6001000, 6002000, 6003000, 6004000, 6008000]) && body.GetProperty("hide_unsafe").GetBoolean(),
                "Knaben's request: " + body);
            try { SearchSources.ParseKnaben("""{"error":"busy"}""", "tv"); throw new Exception("An answer without hits read"); }
            catch (JsonException) { }
            return Task.CompletedTask;
        });

        await Test("Recherche : les réponses d’AnimeTosho et de Torrents-CSV lues (la recherche tenue, pas de jeu)", () =>
        {
            var tosho = SearchSources.ParseAnimeTosho(AnimeToshoSample, "Frieren");
            Assert(tosho.Select(x => x.Id[0]).SequenceEqual(['2', '3']) && tosho.All(x => x.Source == SearchSources.AnimeTosho), "Most shared first, others left out: " + string.Join(" | ", tosho.Select(x => x.Name)));
            Assert(tosho[0].Magnet.StartsWith("magnet:?xt=urn:btih:2222", StringComparison.Ordinal) && tosho[0].Magnet.Contains("nyaa.tracker.wf", StringComparison.Ordinal) && tosho[0] is { Seeders: 120, Leechers: 4, Size: 1430000000, Added: 1711000000 }, "Its own magnet: " + tosho[0]);
            Assert(tosho[1].Magnet.Contains("&dn=%5BBlackRabbit%5D", StringComparison.Ordinal), "Made from the hash: " + tosho[1].Magnet);
            Assert(SearchSources.ParseAnimeTosho(AnimeToshoSample, "Frieren Beyond Journey's End").Single().Id[0] == '3', "Apostrophes on both sides");
            Assert(SearchSources.AnimeToshoRequest("jojo's bizarre adventure").RequestUri!.AbsoluteUri == "https://feed.animetosho.net/json?q=jojo%27s%20bizarre%20adventure", "AnimeTosho's address");
            var csv = SearchSources.ParseTorrentsCsv(TorrentsCsvSample, "Arcane");
            Assert(csv.Count == 1 && csv[0] is { Id: "5555555555555555555555555555555555555555", Seeders: 393, Leechers: 20, Size: 11000000000, Source: SearchSources.TorrentsCsv, Added: 1637000000 }
                && csv[0].Magnet.StartsWith("magnet:?xt=urn:btih:5555555555555555555555555555555555555555&dn=Arcane%20%282021%29", StringComparison.Ordinal), "The game, the nameless thing, a bad hash, another title left out: " + string.Join(" | ", csv.Select(x => x.Name)));
            Assert(SearchSources.TorrentsCsvRequest("Spider Man").RequestUri!.AbsoluteUri == "https://torrents-csv.com/service/search?q=Spider%20Man", "Torrents-CSV's address");
            return Task.CompletedTask;
        });

        await Test("Recherche : les listes rassemblées (chaque site garde ses meilleurs, un résultat par torrent, nom entier, le plus partagé d’abord)", () =>
        {
            var torlink = new List<AgentHit> { Hit("a", "Arcane.S02.1080p.WEB-DL ...", 10), Hit("b", "Arcane.S01E01.720p", 5) };
            var knaben = new List<AgentHit> { Hit("c", "Arcane.S01.2160p", 100, SearchSources.Knaben), Hit("a", "Arcane.S02.1080p.WEB-DL.DDP5.1-FL", 50, SearchSources.Knaben, size: 9) };
            var merged = SearchSources.Merge([torlink, knaben, []], 10);
            Assert(merged.Select(x => x.Id[0]).SequenceEqual(['c', 'a', 'b']), "Most shared first: " + string.Join(" | ", merged.Select(x => $"{x.Id[0]} {x.Seeders}")));
            Assert(merged[1] is { Name: "Arcane.S02.1080p.WEB-DL.DDP5.1-FL", Seeders: 50, Source: "tpb-tv", Size: 1000 }, "Found twice: the whole name, the most sharers, the first site: " + merged[1]);
            var many = Enumerable.Range(0, 3).Select(site => (IReadOnlyList<AgentHit>)Enumerable.Range(0, 5).Select(i => Hit($"{site}{i}", $"Title {site}{i}", 100 * site + i)).ToList()).ToList();
            var four = SearchSources.Merge(many, 4);
            Assert(four.Count == 4 && four.Select(x => x.Id[..2]).Order().SequenceEqual(["00", "01", "10", "20"]), "Each site's best in turn, up to the limit: " + string.Join(" ", four.Select(x => x.Id[..2])));
            Assert(SearchSources.Magnet("5555555555555555555555555555555555555555", "x", "magnet:?xt=urn:btih:4444444444444444444444444444444444444444&dn=other").Contains("btih:5555", StringComparison.Ordinal), "Another torrent's magnet never kept");
            return Task.CompletedTask;
        });

        await Test("Recherche : TorLink et les sites de Mira à la fois, dans chaque forme ; une erreur seulement si personne n’a répondu", async () =>
        {
            var asked = new List<string>(); var forms = new List<string>();
            Task<(List<AgentHit>, string?)> TorLink(string form, CancellationToken _) { lock (forms) forms.Add(form); return Task.FromResult<(List<AgentHit>, string?)>(([Hit("a", form, 3)], null)); }
            async Task<HttpResponseMessage> Answer(HttpRequestMessage request)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                lock (asked) asked.Add($"{request.Method} {request.RequestUri!.Host}{request.RequestUri.PathAndQuery} {body}");
                var json = request.RequestUri!.Host switch
                {
                    "api.knaben.org" when body.Contains("\"from\":0", StringComparison.Ordinal) && body.Contains("JoJos", StringComparison.Ordinal) => Page(100, 0),
                    "api.knaben.org" when body.Contains("\"from\":100", StringComparison.Ordinal) => Page(100, 100),
                    "api.knaben.org" => Page(2, 500),
                    "torrents-csv.com" => """{"torrents":[{"infohash":"9999999999999999999999999999999999999999","name":"JoJos Bizarre Adventure S01 1080p","seeders":7}]}""",
                    _ => "[]",
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            static string Page(int count, int start) => "{\"hits\":[" + string.Join(",", Enumerable.Range(start, count).Select(i =>
                $"{{\"hash\":\"{i:x40}\",\"title\":\"JoJos Bizarre Adventure S01E{i % 50:00} 1080p\",\"seeders\":{1000 - i},\"categoryId\":[2000000]}}")) + "]}";
            using var client = new SearchSourceClient(new Handler(Answer));
            var (hits, error, named) = await TorrentSearch.RunAsync(TorLink, client, "JoJo's Bizarre Adventure", "tv", default);
            Assert(error is null && named.Count == 0 && forms.Order().SequenceEqual(["JoJo's Bizarre Adventure", "JoJos Bizarre Adventure"]), "TorLink in both forms, no other name for a show: " + string.Join(" | ", forms));
            Assert(asked.Count(x => x.StartsWith("POST api.knaben.org/v1", StringComparison.Ordinal)) == 3 && asked.Count(x => x.Contains("\"from\":100", StringComparison.Ordinal) && x.Contains("JoJos", StringComparison.Ordinal)) == 1
                && asked.All(x => !x.Contains("animetosho", StringComparison.Ordinal) && !x.Contains("anilist", StringComparison.Ordinal)) && asked.Count(x => x.StartsWith("GET torrents-csv.com/service/search?q=JoJos%20Bizarre%20Adventure", StringComparison.Ordinal)) == 1,
                "Knaben in both forms and a second page of the broadest, Torrents-CSV in the broadest, no anime site nor AniList for a show: " + string.Join("\n", asked));
            Assert(hits.Count == TorrentSearch.MaxHits && hits[0].Seeders == 1000 && hits.Select(x => x.Id).Distinct().Count() == hits.Count, $"Gathered up to the limit: {hits.Count}");
            Assert(hits.Any(x => x.Id.StartsWith('a')) && hits.Any(x => x.Source == SearchSources.TorrentsCsv) && hits.Count(x => x.Id == 500.ToString("x40") || x.Id == 501.ToString("x40")) == 2,
                "TorLink's, Torrents-CSV's and the typed form's best kept beside Knaben's 202");

            asked.Clear();
            _ = await TorrentSearch.RunAsync(TorLink, client, "Frieren", "anime", default);
            Assert(asked.Count(x => x.StartsWith("GET feed.animetosho.net/json?q=Frieren", StringComparison.Ordinal)) == 1 && asked.All(x => !x.Contains("torrents-csv", StringComparison.Ordinal)), "Anime: AnimeTosho, not Torrents-CSV: " + string.Join("\n", asked));

            Task<(List<AgentHit>, string?)> Broken(string form, CancellationToken _) => Task.FromResult<(List<AgentHit>, string?)>(([], "TorLink n’a pas pu chercher : node absent"));
            using var down = new SearchSourceClient(new Handler(_ => throw new HttpRequestException("refused")));
            var (none, failed, _) = await TorrentSearch.RunAsync(Broken, down, "Dune", "movies", default);
            Assert(none.Count == 0 && failed is not null && failed.Contains("Aucun site", StringComparison.Ordinal), "Nobody answered: " + failed);
            var (alone, torlinkError, _) = await TorrentSearch.RunAsync(Broken, null, "Dune", "movies", default);
            Assert(alone.Count == 0 && torlinkError == "TorLink n’a pas pu chercher : node absent", "Mira's sites turned off: TorLink's own words: " + torlinkError);
            var (rescued, fine, _) = await TorrentSearch.RunAsync(Broken, client, "Dune", "movies", default);
            Assert(fine is null && rescued.Count == 2, "TorLink down, Knaben up: its results: " + rescued.Count);
            using var junk = new SearchSourceClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Cloudflare</html>") })));
            var (unreadable, said) = await junk.SearchAsync(SearchSources.Knaben, "Dune", "movies", true, default);
            Assert(unreadable.Count == 0 && said is not null && said.StartsWith("knaben", StringComparison.Ordinal), "A page instead of JSON: " + said);
        });

        await Test("Recherche : un anime aussi sous ses autres noms (AniList), la saison d’un pack en mots pour Knaben", async () =>
        {
            Assert(SearchSources.SeasonWords("Arcane S02") == "Arcane Season 2" && SearchSources.SeasonWords("Arcane S2") == "Arcane Season 2", "« S02 » in words");
            Assert(SearchSources.SeasonWords("Arcane") is null && SearchSources.SeasonWords("Arcane S02E05") is null && SearchSources.SeasonWords(null) is null && SearchSources.SeasonWords(" S02") is null,
                "Only a whole season after a title");
            Assert(SearchSources.TitleAndRest("Frieren S02") == ("Frieren", "S02") && SearchSources.TitleAndRest("Frieren") == ("Frieren", ""), "The title, then what follows it: " + SearchSources.TitleAndRest("Frieren S02"));
            Assert(SearchSources.TitleAndRest("Sousou no Frieren (2023) S02") == ("Sousou no Frieren", "S02"), "A year left out: " + SearchSources.TitleAndRest("Sousou no Frieren (2023) S02"));
            Assert(SearchSources.TitleAndRest("S02") is null && SearchSources.TitleAndRest("1080p") is null && SearchSources.TitleAndRest("K") is null, "No title, no other name");
            Assert(SearchSources.TitleAndRest("86 S01") == ("86", "S01") && SearchSources.TitleAndRest("Re:Zero S03") == ("Re:Zero", "S03"), "A title of digits, of two words in one");

            Assert(SearchSources.ParseAniList(AniListSample, "Frieren").SequenceEqual(["Sousou no Frieren", "Frieren: Beyond Journey’s End"]),
                "The first anime the title names (by a name that begins with its words), Romaji and English: " + string.Join(" | ", SearchSources.ParseAniList(AniListSample, "Frieren")));
            Assert(SearchSources.ParseAniList(AniListSample, "Shingeki no Kyojin").SequenceEqual(["Attack on Titan"]), "The name searched is never one of its other names");
            Assert(SearchSources.ParseAniList(AniListSample, "Dandadan").Count == 0, "« DAN DA DAN » is « Dandadan »: nothing more to search");
            Assert(SearchSources.ParseAniList(AniListSample, "Fire Force").SequenceEqual(["Enen no Shouboutai"]), "The first of the list too");
            Assert(SearchSources.ParseAniList(AniListSample, "Journey").Count == 0 && SearchSources.ParseAniList(AniListSample, "Force").Count == 0,
                "Nothing when no anime is the title (a word in the middle of a name is not it)");
            await Throws<JsonException>(() => Task.FromResult(SearchSources.ParseAniList("""{"errors":[{"message":"Too Many Requests"}],"data":null}""", "Frieren")));

            var asked = new List<string>(); var forms = new List<string>();
            Task<(List<AgentHit>, string?)> TorLink(string form, CancellationToken _) { lock (forms) forms.Add(form); return Task.FromResult<(List<AgentHit>, string?)>(([Hit("a", form, 3)], null)); }
            var aniListDown = false;
            async Task<HttpResponseMessage> Answer(HttpRequestMessage request)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                lock (asked) asked.Add($"{request.Method} {request.RequestUri!.Host}{request.RequestUri.PathAndQuery} {body}");
                if (request.RequestUri!.Host == "graphql.anilist.co" && aniListDown) throw new HttpRequestException("refused");
                var json = request.RequestUri!.Host switch
                {
                    "graphql.anilist.co" => AniListSample,
                    "api.knaben.org" => """{"hits":[]}""",
                    _ => "[]",
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            using var client = new SearchSourceClient(new Handler(Answer));
            var (_, error, names) = await TorrentSearch.RunAsync(TorLink, client, "Frieren S02", "anime", default);
            Assert(error is null && names.SequenceEqual(["Sousou no Frieren", "Frieren: Beyond Journey’s End"]), "Its other names, for the devices: " + string.Join(" | ", names));
            Assert(forms.Order().SequenceEqual(["Frieren Beyond Journeys End S02", "Frieren S02", "Sousou no Frieren S02"]), "TorLink under each name, the season kept: " + string.Join(" | ", forms));
            var aniList = asked.Where(x => x.StartsWith("POST graphql.anilist.co", StringComparison.Ordinal)).ToList();
            Assert(aniList.Count == 1 && aniList[0].Contains("\"search\":\"Frieren\"", StringComparison.Ordinal) && !aniList[0].Contains("S02", StringComparison.Ordinal),
                "AniList gets the anime's name alone: " + string.Join("\n", aniList));
            foreach (var form in new[] { "Frieren S02", "Sousou no Frieren S02", "Frieren Beyond Journeys End S02" })
                Assert(asked.Count(x => x.StartsWith("GET feed.animetosho.net/json?q=" + Uri.EscapeDataString(form) + " ", StringComparison.Ordinal)) == 1
                    && asked.Count(x => x.StartsWith("POST api.knaben.org", StringComparison.Ordinal) && x.Contains($"\"query\":\"{form}\"", StringComparison.Ordinal)) == 1,
                    $"AnimeTosho and Knaben under « {form} »: " + string.Join("\n", asked));
            Assert(asked.Count(x => x.Contains("\"query\":\"Frieren Season 2\"", StringComparison.Ordinal)) == 1, "Knaben, the season's packs in words: " + string.Join("\n", asked));

            asked.Clear(); forms.Clear();
            var (_, _, again) = await TorrentSearch.RunAsync(TorLink, client, "Frieren", "anime", default);
            Assert(again.Count == 2 && asked.All(x => !x.Contains("anilist", StringComparison.Ordinal)) && forms.Order().SequenceEqual(["Frieren", "Frieren Beyond Journeys End", "Sousou no Frieren"]),
                "The next search of the same anime: AniList not asked again: " + string.Join(" | ", forms));
            asked.Clear(); forms.Clear();
            var (_, _, show) = await TorrentSearch.RunAsync(TorLink, client, "Arcane S02", "tv", default);
            Assert(show.Count == 0 && asked.All(x => !x.Contains("anilist", StringComparison.Ordinal)) && forms.SequenceEqual(["Arcane S02"]), "A show: no AniList");
            asked.Clear(); forms.Clear();
            aniListDown = true;
            var (found, fine, none) = await TorrentSearch.RunAsync(TorLink, client, "Dandadan", "anime", default);
            Assert(fine is null && none.Count == 0 && found.Count == 1 && forms.SequenceEqual(["Dandadan"]), "AniList down: the search goes on under its name");
            asked.Clear(); forms.Clear();
            var (_, _, without) = await TorrentSearch.RunAsync(TorLink, null, "Frieren", "anime", default);
            Assert(without.Count == 0 && asked.Count == 0 && forms.SequenceEqual(["Frieren"]), "Mira's sites turned off: AniList neither");
        });

        await Test("Recherche : un rapport au relais jamais plus lourd que ce qu’il accepte, les autres recherches finies à la visite suivante", () =>
        {
            AgentSearchOutcome Outcome(string id, int results, int magnet) => new(id, "done",
                [.. Enumerable.Range(0, results).Select(i => Hit($"{id}{i:x3}", $"Title {i} 1080p", i) with { Magnet = "magnet:?xt=urn:btih:" + new string('a', magnet) })], null);
            var heavy = Enumerable.Range(0, 3).Select(i => Outcome($"{i}", 150, 4000)).ToList();
            var weight = JsonSerializer.SerializeToUtf8Bytes(heavy[0], RemoteProtocol.Json).Length;
            Assert(weight > TorLinkRemote.ReportBudget / 3 && weight < TorLinkRemote.ReportBudget / 2, $"Each weighs {weight}");
            var sent = TorLinkRemote.ReportOf(heavy);
            Assert(sent.SequenceEqual(heavy.Take(2)), $"Two of three: {sent.Count}");
            Assert(TorLinkRemote.ReportOf([Outcome("x", 150, 8000), .. heavy]).Count == 1, "One heavier than the budget still goes, alone");
            Assert(TorLinkRemote.ReportOf([.. Enumerable.Range(0, 20).Select(i => Outcome($"{i:x2}", 3, 300))]).Count == 20 && TorLinkRemote.ReportOf([]).Count == 0, "Light ones all at once");
            return Task.CompletedTask;
        });

        // The real sites, only when asked (ci.yml, a runner that reaches them; a site down there is a warning, never red).
        if (Environment.GetEnvironmentVariable("MIRA_SEARCH_LIVE") == "1")
            await Test("Recherche : les vrais sites de Mira (Knaben, AnimeTosho, Torrents-CSV)", async () =>
            {
                using var client = new SearchSourceClient();
                var problems = new List<string>();
                foreach (var (site, query, category) in new[]
                {
                    (SearchSources.Knaben, "Arcane", "tv"), (SearchSources.Knaben, "Dune Part Two", "movies"), (SearchSources.Knaben, "Frieren", "anime"),
                    (SearchSources.AnimeTosho, "Frieren", "anime"), (SearchSources.TorrentsCsv, "Arcane", "tv"),
                })
                {
                    var (hits, error) = await client.SearchAsync(site, query, category, broadest: true, default);
                    var cut = hits.Count(x => x.Name.EndsWith("...", StringComparison.Ordinal) || x.Name.EndsWith('…'));
                    Console.WriteLine($"      {site} « {query} » ({category}) : {hits.Count} résultats{(error is null ? "" : $", {error}")}, {cut} noms coupés ; en tête : {string.Join(" | ", hits.Take(3).Select(x => $"{x.Name} ({x.Seeders})"))}");
                    if (error is not null || hits.Count == 0) problems.Add($"{site} {query}: {error ?? "nothing"}");
                    if (hits.Any(x => RemoteDownloads.InfoHash(x.Magnet) != x.Id || x.Name.Length == 0 || x.Seeders < 0)) problems.Add($"{site} {query}: a result without its own magnet");
                    if (cut * 10 > hits.Count) problems.Add($"{site} {query}: {cut} names cut");
                }
                // AniList: the names of an anime (Romaji, English), never those of another; Knaben: a season's packs in words.
                var frieren = await client.AnimeNamesAsync("Frieren", default);
                var attack = await client.AnimeNamesAsync("Shingeki no Kyojin", default);
                Console.WriteLine($"      anilist « Frieren » : {string.Join(" | ", frieren)} ; « Shingeki no Kyojin » : {string.Join(" | ", attack)}");
                if (!frieren.Any(x => ReleaseRanking.TitleKey(x) == "sousounofrieren") || !attack.Any(x => ReleaseRanking.TitleKey(x) == "attackontitan"))
                    problems.Add($"anilist: {string.Join(" | ", frieren)} / {string.Join(" | ", attack)}");
                foreach (var form in new[] { "Arcane S02", "Arcane Season 2" })
                {
                    var (season, said) = await client.SearchAsync(SearchSources.Knaben, form, "tv", broadest: false, default);
                    Console.WriteLine($"      knaben « {form} » (tv) : {season.Count} résultats{(said is null ? "" : $", {said}")}, dont {season.Count(x => ReleaseRanking.Parse(x.Name) is { Pack: true, Season: 2 })} packs de la saison 2");
                }
                var (all, failed, names) = await TorrentSearch.RunAsync((_, _) => Task.FromResult<(List<AgentHit>, string?)>(([], "TorLink absent")), client, "JoJo's Bizarre Adventure", "anime", default);
                Console.WriteLine($"      ensemble, animes, « JoJo's Bizarre Adventure » : {all.Count} résultats{(failed is null ? "" : $", {failed}")}, autres noms : {string.Join(" | ", names)}");
                if (failed is not null || all.Count < 20) problems.Add($"together: {all.Count} {failed}");
                Assert(problems.Count == 0, string.Join("\n", problems));
            });
    }
}
