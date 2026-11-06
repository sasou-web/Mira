using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using static TestSupport;

/// <summary>« Bande-annonce » of a title's page and « Bande-annonce avant le film » (Trailers, SkipPlanner.Film).</summary>
internal static class TrailerTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Bande-annonce : celle du serveur dans Mira, celle du web seulement en https dans le navigateur (tests/fixtures/trailers.json)", () =>
        {
            var fixture = Fixture();
            foreach (var row in fixture["links"]!.AsArray())
            {
                var url = (string?)row![0]; var wanted = (string?)row[1];
                Assert(Trailers.WebLink(url)?.AbsoluteUri == wanted, $"« {url} » : {Trailers.WebLink(url)?.AbsoluteUri ?? "rien"}");
            }
            foreach (var row in fixture["sites"]!.AsArray())
                Assert(Trailers.Site(new Uri((string)row![0]!)) == (string?)row[1], $"{row[0]} : {Trailers.Site(new Uri((string)row[0]!))}");
            foreach (var row in fixture["sources"]!.AsArray())
            {
                var item = row!["item"].Deserialize<MediaItem>(Json.Options)!;
                var source = Trailers.Source(item).ToString().ToLowerInvariant();
                Assert(source == (string?)row["source"], $"{row["item"]!.ToJsonString()} : {source}");
                Assert(Trailers.WebLink(item)?.AbsoluteUri == (string?)row["link"] || row["link"] is null, $"{row["item"]!.ToJsonString()} : {Trailers.WebLink(item)}");
            }
            foreach (var row in fixture["first"]!.AsArray())
            {
                var item = row!["item"].Deserialize<MediaItem>(Json.Options)!;
                var plays = Trailers.PlaysFirst((bool)row["enabled"]!, item, (double)row["startAt"]!, (bool)row["inGroup"]!, (bool)row["kept"]!);
                Assert(plays == (bool)row["plays"]!, $"Avant le film : {row.ToJsonString()} : {plays}");
            }
            return Task.CompletedTask;
        });

        await Test("Bande-annonce : le bouton dit où il mène avant le clic, en français et en anglais", () =>
        {
            var was = Lang.Code;
            try
            {
                var web = new MediaItem { Type = "Movie", RemoteTrailers = [new() { Url = "https://www.youtube.com/watch?v=x" }] };
                var kept = web with { LocalTrailerCount = 1 };
                Lang.Use(Lang.French);
                Assert(Trailers.Tip(web) == "Bande-annonce sur YouTube, dans ton navigateur" && Trailers.Tip(kept) == "Voir la bande-annonce", Trailers.Tip(web));
                Assert(PlaybackMarkers.Label(SkipKind.Trailer, false, false) == "Passer la bande-annonce", "Pastille");
                Lang.Use(Lang.English);
                Assert(Trailers.Tip(web) == "Trailer on YouTube, in your browser" && Trailers.Tip(kept) == "Watch the trailer" && Trailers.SkipLabel == "Skip trailer", Trailers.Tip(web));
                Assert(Trailers.BeforeFilmLabel.Length > 0 && Trailers.BeforeFilmHint.Contains("never", StringComparison.OrdinalIgnoreCase), Trailers.BeforeFilmHint);
            }
            finally { Lang.Use(was); }
            return Task.CompletedTask;
        });

        await Test("Bande-annonce avant le film : la pastille passe toute la bande-annonce, sans compte à rebours", () =>
        {
            var film = new MediaItem { Id = "film", Type = "Movie" };
            var skips = new SkipPlanner();
            skips.Reset(); skips.Film = film;
            Assert(skips.Offer(0, 0, offering: true, autoNext: true) && skips.Active is { Kind: SkipKind.Trailer } && !skips.ToNext && !skips.Counting, "Dès le départ");
            Assert(skips.Label(false) == Trailers.SkipLabel, skips.Label(false));
            skips.Offer(150, 160, offering: true, autoNext: true);
            Assert(skips.Active is { Kind: SkipKind.Trailer, Start: 0, End: 160 } && !skips.Counting && skips.Due(159) is null, "Jusqu'à la fin, jamais « Épisode suivant dans »");
            Assert(skips.Offer(10, 160, offering: false, autoNext: true) && skips.Active is null, "Pas dans le mini-lecteur");
            skips.Reset();
            Assert(skips.Film is null && !skips.Offer(0, 160, offering: true, autoNext: true), "Un autre titre : plus de film en attente");
            return Task.CompletedTask;
        });

        await Test("Bande-annonce : Jellyfin dit combien il en garde et lesquelles sont sur le web (comme 12.1 répond)", async () =>
        {
            var item = JsonSerializer.Deserialize<MediaItem>("""{"Id":"f","Type":"Movie","LocalTrailerCount":1,"RemoteTrailers":[{"Url":"https://www.youtube.com/watch?v=a","Name":"Official Trailer"}]}""", Json.Options)!;
            Assert(item.LocalTrailerCount == 1 && item.RemoteTrailers is [{ Url: "https://www.youtube.com/watch?v=a", Name: "Official Trailer" }], "Champs de la fiche");
            string? asked = null;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request =>
            {
                asked = request.RequestUri!.PathAndQuery;
                // Items/{id}/LocalTrailers of Jellyfin 12.1: a plain list, each trailer a video of its own.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    [{"Name":"Bande-annonce","Id":"t1","Type":"Trailer","ExtraType":"Trailer","MediaType":"Video","RunTimeTicks":60230000}]
                    """, System.Text.Encoding.UTF8, "application/json") });
            }));
            var trailers = await client.LocalTrailersAsync("film 1");
            Assert(asked == "/Items/film%201/LocalTrailers?userId=user-1", "Requête : " + asked);
            Assert(trailers is [{ Id: "t1", Type: "Trailer", ExtraType: "Trailer" }], "Liste");
            // Played as a trailer, never kept as a copy nor counted as a film.
            Assert(!Trailers.PlaysFirst(true, trailers[0], 0, false, false), "Une bande-annonce n'a pas de bande-annonce");
        });
    }

    private static JsonNode Fixture()
    {
        var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "trailers.json")))!;
    }
}
