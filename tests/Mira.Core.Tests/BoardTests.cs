using Mira.Core;
using Mira.Core.TorLink;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>The Downloads page « Régie » of Windows and Mac (RemoteBoard): its queue, its series followed, the disk left.</summary>
internal static class BoardTests
{
    private const long GiB = 1L << 30, MiB = 1L << 20, TiB = 1L << 40;

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        const string taken = "Les.Etoiles.du.Nord.S01E04.MULTI.1080p.WEB-DL.H264-NOTAG";
        var follow = new RelayFollow("f1", "0123456789abcdef0123456789abcdef", "Les Étoiles du Nord", null, now.AddDays(-40), now.AddHours(-2), now.AddHours(4), false,
            new FollowNote("found", "S01E04"), "S01E03", [new("S01E04", taken, now.AddHours(-1))],
            [new("p1", "S01E05", "Les.Etoiles.du.Nord.S01E05.1080p.WEB.H264-NOTAG", 14 * GiB / 10, "waiting", now.AddMinutes(8), null)], new RelayAiring("S01E06", now.AddDays(1)));
        var quiet = new RelayFollow("f2", "fedcba9876543210fedcba9876543210", "Brume", "Brume (2024)", now.AddDays(-90), now.AddHours(-3), now.AddHours(3), false,
            new FollowNote("nothing", "S02E08"), "S02E08", [new("S02E08", "Brume.2024.S02E08.1080p.WEB-DL-NOTAG", now.AddDays(-6))], [], null);
        var status = new RelayStatus(new("SALON-PC", true, now, true, null, Free: 5 * TiB),
            [new("r1", "La.Riviere.Lente.2021.1080p.BluRay.x264-NOTAG", 9 * GiB / 2, new string('b', 40), "waiting", null, now, Replace: "aaaabbbbccccddddaaaabbbbccccdddd")], [],
            [new(new string('a', 40), taken, "downloading", 41, 9 * MiB, 34, 29 * GiB / 10, 12 * GiB / 10, 190),
             new(new string('c', 40), "La.Maison.du.Lac.2023.1080p.BluRay.x265.10bit-NOTAG", "paused", 63, 0, 0, 4 * GiB, 5 * GiB / 2),
             new(new string('e', 40), "Papier.Lune.2020.1080p.WEB-DL-NOTAG", "failed", 3, 0, 0, 2 * GiB, 0, null, "disk full")],
            [new("d", "Le Dernier Phare", "available", null, true)], [follow, quiet]);

        await Test("Téléchargements « Régie » : la file lit chaque nom, marque les épisodes d’une série suivie et ne montre que les images de Jellyfin", () =>
        {
            var queue = RemoteBoard.Queue(status);
            Assert(queue.Select(x => x.Key[0]).SequenceEqual(['r', 'p', 'd', 'd', 'd']), "Demandes, épisodes choisis, puis TorLink : " + string.Join(",", queue.Select(x => x.Key)));
            var request = queue[0];
            Assert(request is { Title: "La Riviere Lente", Year: 2021, Episode: "", Followed: false, Episodic: false } && request.PosterId == "aaaabbbbccccddddaaaabbbbccccdddd",
                $"Un film demandé pour remplacer un titre : son affiche à lui ({request.Title}, {request.Year}, {request.PosterId}).");
            Assert(queue[1] is { Followed: true, Episode: "S01E05", Episodic: true } && queue[1].PosterId == follow.SeriesId, "L’épisode choisi par une série suivie : « Suivie », l’affiche de la série.");
            Assert(queue[2] is { Followed: true, Episode: "S01E04" } && queue[2].PosterId == follow.SeriesId, "Le téléchargement de l’épisode pris par une série suivie (même nom) : « Suivie ».");
            Assert(queue[3] is { Followed: false, PosterId: null, Year: 2023 } && queue[3].Quality.Length > 0, "Un film sans titre de Jellyfin : aucune image, sa qualité lue : " + queue[3].Quality);
            Assert(RemoteBoard.Summary(queue) == "1 en cours · 9,0 Mo/s · 1 en pause · 2 en attente · 1 en échec", "Résumé : " + RemoteBoard.Summary(queue));
            Assert(RemoteBoard.Summary([]) == "", "Rien en cours : rien à résumer.");
            return Task.CompletedTask;
        });
        await Test("Téléchargements « Régie » : colonnes d’un téléchargement, ligne de ce qui attend", () =>
        {
            var queue = RemoteBoard.Queue(status);
            var (amount, rate, left, peers) = RemoteBoard.Columns(queue[2].Download!);
            Assert(RemoteBoard.Percent(queue[2].Download!) == "41 %" && amount.Length > 0 && rate == "9,0 Mo/s" && left.StartsWith("reste ", StringComparison.Ordinal) && peers == "34 pairs",
                $"En cours : {amount} | {rate} | {left} | {peers}");
            Assert(RemoteBoard.Columns(queue[3].Download!) is { Rate: "En pause", Left: "", Peers: "" }, "En pause : ni temps restant ni pairs.");
            Assert(RemoteBoard.Columns(queue[2].Download! with { Speed = 0, Peers = 0 }).Rate == "Recherche de pairs…", "Rien ne vient, aucun pair.");
            Assert(RemoteBoard.Waiting(queue[2], now) is null && RemoteBoard.Waiting(queue[4], now) is { Length: > 0 }, "Un téléchargement en cours a ses colonnes ; un échec, sa phrase.");
            var pick = RemoteBoard.Waiting(queue[1], now)!;
            Assert(pick.StartsWith("En attente · 1,4 Go · part dans 8\u00a0min", StringComparison.Ordinal) && pick.EndsWith("série que tu suis", StringComparison.Ordinal), "Épisode choisi : " + pick);
            Assert(RemoteBoard.Waiting(queue[0], now) is { Length: > 0 }, "Une demande : ce qu’elle attend.");
            return Task.CompletedTask;
        });
        await Test("Téléchargements « Régie » : séries suivies (en cours, prochain épisode, prochaine recherche)", () =>
        {
            var rows = RemoteBoard.Follows(status, now);
            Assert(rows.Count == 2 && rows[0].Live && rows[0].Line.Contains("S01E05", StringComparison.Ordinal), "Un épisode choisi qui attend : la ligne en direct : " + rows[0].Line);
            Assert(rows[0].NextEpisode == "S01E06" && rows[0].Airs.StartsWith("sort ", StringComparison.Ordinal) && rows[0].NextSearch == "dans 4\u00a0h", $"Prochain : {rows[0].NextEpisode} {rows[0].Airs} {rows[0].NextSearch}");
            Assert(!rows[1].Live && !rows[1].Warning && rows[1].Line.Contains("S02E08", StringComparison.Ordinal) && rows[1].Line.Contains("Pris il y a 6 jours", StringComparison.Ordinal), "Rien de nouveau : " + rows[1].Line);
            Assert(rows[1].NextEpisode == "" && rows[1].Airs == "pas encore de date", "Sans date de sortie.");
            var taking = RemoteBoard.Follows(status with { Follows = [follow with { Picks = [] }] }, now)[0];
            Assert(taking.Live && taking.Line == "S01E04 · téléchargement, 41 %", "L’épisode pris que TorLink télécharge : " + taking.Line);
            var gone = RemoteBoard.Follows(status with { Follows = [quiet with { Note = new FollowNote("missing") }] }, now)[0];
            Assert(gone.Warning && !gone.Live, "Une série partie de Jellyfin : en avertissement.");
            Assert(RemoteBoard.Coming(status.Follows, now) == "Les Étoiles du Nord S01E06 sort demain", "Ligne repliée : " + RemoteBoard.Coming(status.Follows, now));
            Assert(RemoteBoard.Latest([new("1", "Flow", "", null, true), new("2", "Nimona", "", null, true), new("3", "Arcane · S02E05", "", null, true), new("4", "Le Col", "", null, true)]) == "Flow, Nimona, Arcane · S02E05…", "Derniers rangés.");
            return Task.CompletedTask;
        });
        await Test("Téléchargements « Régie » : place libre des disques des bibliothèques, chaque disque une fois", () =>
        {
            var free = new Dictionary<string, long?> { ["D:\\"] = 4 * TiB, ["E:\\"] = 900 * GiB, ["F:\\"] = null };
            string? Root(string path) => path.Length > 2 ? path[..3] : null;
            Assert(RemoteBoard.FreeSpace(["D:\\Films", "D:\\Séries", "E:\\Animes"], Root, x => free[x]) == 4 * TiB + 900 * GiB, "Deux disques, le premier une seule fois.");
            Assert(RemoteBoard.FreeSpace(["F:\\Films", null, ""], Root, x => free[x]) is null, "Aucun disque qui réponde : rien.");
            var relay = new TorLinkRelay(() => now);
            relay.Exchange("pc-1", new AgentReport("PC-SALON", true, null, null, null, null, null, null, Free: 3 * TiB));
            Assert(relay.Status().Agent?.Free == 3 * TiB, "Le relais garde ce que l’agent a dit.");
            relay.Exchange("pc-1", new AgentReport("PC-SALON", true, null, null, null, null, null, null, Free: -1));
            Assert(relay.Status().Agent?.Free is null, "Un nombre impossible n’est pas gardé.");
            relay.Exchange("pc-1", new AgentReport("PC-SALON", true, null, null, null, null, null, null));
            Assert(relay.Status().Agent?.Free is null, "Un agent de la 0.9.8 ne dit rien : rien de montré.");
            Assert(RemoteBoard.Free(5 * TiB + 4 * TiB / 10) == "5,4 To libres" && RemoteBoard.Free(850 * GiB) == "850 Go libres", $"{RemoteBoard.Free(5 * TiB + 4 * TiB / 10)} / {RemoteBoard.Free(850 * GiB)}");
            return Task.CompletedTask;
        });
    }
}
