using System.IO;
using System.Text.Json;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Chercher une meilleure version »: which results are this title in a better picture (TitleUpgrade, as Mira web),
/// the swap of the files that never loses one (TitleSwap), the journal that lives through Mira's end (TorLinkImporter),
/// and the relay that hands a replacement only to a Mira that knows how (TorLinkRelay).
/// </summary>
internal static class UpgradeTests
{
    private const string ItemId = "0123456789abcdef0123456789abcdef";
    private static string Hash(int n) => n.ToString("x40");
    private static string Magnet(int n) => $"magnet:?xt=urn:btih:{Hash(n)}";
    private static MediaItem Item(JsonElement row) => row[0].GetString() == "Movie"
        ? new() { Type = "Movie", Name = row[1].GetString()!, OriginalTitle = row[2].GetString(), ProductionYear = row[3].ValueKind == JsonValueKind.Number ? row[3].GetInt32() : null }
        : new() { Type = "Episode", SeriesName = row[1].GetString(), ParentIndexNumber = row[2].GetInt32(), IndexNumber = row[3].GetInt32(), IndexNumberEnd = row[4].ValueKind == JsonValueKind.Number ? row[4].GetInt32() : null };
    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
        await Test("Meilleure version : qualité du fichier, versions meilleures, même titre, recherche, comme Mira web (tests/fixtures/upgrade.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "upgrade.json")));
            var root = fixture.RootElement;
            foreach (var row in root.GetProperty("current").EnumerateArray())
            {
                var path = Text(row.GetProperty("path"));
                var found = TitleUpgrade.Current((row.GetProperty("width").GetInt32(), row.GetProperty("height").GetInt32()), path);
                var expected = row.GetProperty("quality");
                Assert(found == new TitleQuality(Number(expected[0]), Text(expected[1])), $"{path}: {found}");
                Assert(TitleUpgrade.Line(found) == row.GetProperty("line").GetString(), $"{path}: {TitleUpgrade.Line(found)}");
            }
            var film = new MediaItem { Type = "Movie", MediaSources = [new() { Path = root.GetProperty("current")[0].GetProperty("path").GetString(), MediaStreams = [new() { Type = "Audio" }, new() { Type = "Video", Width = 1280, Height = 720 }] }] };
            Assert(TitleUpgrade.Current(film) == new TitleQuality(720, "webrip"), "The first video stream of the first file");
            foreach (var row in root.GetProperty("better").EnumerateArray())
                Assert(TitleUpgrade.Better(new(Number(row[0]), Text(row[1])), ReleaseRanking.Parse(row[2].GetString()!)) == row[3].GetBoolean(), $"{row[0]} {row[1]} → {row[2]}");
            foreach (var row in root.GetProperty("fits").EnumerateArray())
                Assert(TitleUpgrade.Fits(Item(row[0]), ReleaseRanking.Parse(row[1].GetString()!)) == row[2].GetBoolean(), $"{row[0]} → {row[1]}");
            foreach (var row in root.GetProperty("queries").EnumerateArray())
                Assert(TitleUpgrade.Query(Item(row[0])) == row[1].GetString(), $"Query {row[0]}: {TitleUpgrade.Query(Item(row[0]))}");
            foreach (var row in root.GetProperty("titles").EnumerateArray())
                Assert(TitleUpgrade.Title(Item(row[0])) == row[1].GetString(), $"Title {row[0]}: {TitleUpgrade.Title(Item(row[0]))}");
            foreach (var row in root.GetProperty("lengths").EnumerateArray())
                Assert(TitleUpgrade.SameLength(TimeSpan.FromSeconds(row[0].GetInt32()), TimeSpan.FromSeconds(row[1].GetInt32())) == row[2].GetBoolean(), $"{row[0]} s → {row[1]} s");
            var candidates = root.GetProperty("candidates");
            var file = candidates.GetProperty("file");
            var dune = Item(candidates.GetProperty("item")) with { MediaSources = [new() { Path = file[2].GetString(), MediaStreams = [new() { Type = "Video", Width = file[0].GetInt32(), Height = file[1].GetInt32() }] }] };
            var hits = candidates.GetProperty("hits").EnumerateArray().Select((x, i) => new RelayHit(Hash(i), x.GetString()!, 1, 10, 1, "yts", null)).ToList();
            var kept = TitleUpgrade.Candidates(dune, hits).Select(x => x.Name);
            Assert(kept.SequenceEqual(candidates.GetProperty("kept").EnumerateArray().Select(x => x.GetString())), "Candidates: " + string.Join(", ", kept));
            foreach (var row in root.GetProperty("probes").EnumerateArray())
                Assert(TitleUpgrade.ProbedLength(row[0].GetString()) == (row[1].ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(row[1].GetDouble()) : null), $"Probe {row[0]}");
            return Task.CompletedTask;
        });

        await Test("Meilleure version : seuls un film ou un épisode d'un seul fichier, de durée connue, dans une bibliothèque, se remplacent", () =>
        {
            var libraries = new[] { @"D:\Medias\Films", "/srv/medias/Séries/" };
            MediaItem Film(string? path, long? runtime = 72_000_000_000, string type = "Movie", int sources = 1) => new()
            {
                Type = type, Name = "Dune", RunTimeTicks = runtime,
                MediaSources = path is null ? null : Enumerable.Range(0, sources).Select(_ => new MediaSource { Path = path }).ToList(),
            };
            Assert(TitleUpgrade.Problem(Film(@"D:\Medias\Films\Dune (2021)\Dune.mkv"), libraries) is null, "A film in a library");
            Assert(TitleUpgrade.Problem(Film("/srv/medias/séries/The Bear/Saison 1/The Bear - S01E01.mp4", type: "Episode"), libraries) is null, "An episode, the case of a folder aside");
            Assert(TitleUpgrade.Problem(Film(@"D:\Medias\Films\Dune (2021)\Dune.mkv", type: "Series"), libraries) is { } series && series.Contains("film ou un épisode", StringComparison.Ordinal), "A series");
            Assert(TitleUpgrade.Problem(Film(@"D:\Medias\Films\Dune (2021)\Dune.mkv", sources: 2), libraries)!.Contains("plusieurs versions", StringComparison.Ordinal), "Several versions");
            Assert(TitleUpgrade.Problem(Film(@"D:\Medias\Films\Dune (2021)\VIDEO_TS"), libraries)!.Contains("un seul fichier vidéo", StringComparison.Ordinal), "A disc's folder");
            Assert(TitleUpgrade.Problem(Film(null), libraries)!.Contains("un seul fichier vidéo", StringComparison.Ordinal), "No file");
            Assert(TitleUpgrade.Problem(Film(@"D:\Medias\Films\Dune (2021)\Dune.mkv", runtime: null), libraries)!.Contains("durée", StringComparison.Ordinal), "No length");
            Assert(TitleUpgrade.Problem(Film(@"D:\Medias\Films2\Dune.mkv"), libraries)!.Contains("aucune bibliothèque", StringComparison.Ordinal), "A folder only named like a library");
            Assert(TitleUpgrade.Problem(Film(@"E:\Autre\Dune.mkv"), libraries) is not null && TitleUpgrade.Problem(Film(@"D:\Medias\Films\Dune.mkv"), []) is not null, "Outside every library");
            Assert(TitleUpgrade.FileOf(new() { Path = "/a.mkv", MediaSources = [new() { Path = "/b.mkv" }] }) == "/b.mkv" && TitleUpgrade.FileOf(new() { Path = "/a.mkv" }) == "/a.mkv", "The file as Jellyfin has it");
            return Task.CompletedTask;
        });

        await Test("Meilleure version : la nouvelle prend la place de l'ancienne, envoyée à la Corbeille ; rien ne change si l'une des deux ne peut pas", async () =>
        {
            var dir = Path.Combine(testRoot, "title-swap");
            var title = Path.Combine(dir, "Films", "Dune (2021)"); var downloads = Path.Combine(dir, "downloads"); var bin = Path.Combine(dir, "bin");
            foreach (var folder in new[] { title, downloads, bin }) Directory.CreateDirectory(folder);
            var old = Path.Combine(title, "Dune.2021.720p.WEBRip.x264-GRP.mkv"); File.WriteAllBytes(old, new byte[10]);
            var release = Path.Combine(downloads, "Dune.2021.1080p.BluRay.x264-GRP"); Directory.CreateDirectory(release);
            var video = Path.Combine(release, "Dune.2021.1080p.BluRay.x264-GRP.mp4"); File.WriteAllBytes(video, new byte[20]);
            var subtitle = Path.Combine(release, "Dune.2021.1080p.BluRay.x264-GRP.fr.srt"); File.WriteAllBytes(subtitle, new byte[3]);
            var source = new SourceFile(video, Path.GetFileName(video), 20);
            var subtitles = new List<SourceFile> { new(subtitle, Path.GetFileName(subtitle), 3) };
            var destination = Path.Combine(title, "Dune.2021.720p.WEBRip.x264-GRP.mp4");
            Assert(TitleSwap.Destination(old, video) == destination && TitleSwap.Destination(old, "x/New.MKV") == old, "The old name, the new extension");
            bool Recycle(string path) { File.Move(path, Path.Combine(bin, Path.GetFileName(path))); return true; }
            string Listed() => string.Join(", ", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(dir, x)).Order());
            var before = Listed();

            // The Recycle Bin refuses: the new file is taken away again, nothing changed.
            var refused = await TitleSwap.SwapAsync(old, source, subtitles, ImportMode.KeepSeeding, _ => false);
            Assert(refused is { NewPath: null, Recycled: false, Busy: false, Problem: not null } && Listed() == before, "Recycle Bin refused: " + Listed());
            // The new name taken by another file: never overwritten.
            File.WriteAllBytes(destination, new byte[5]);
            var taken = await TitleSwap.SwapAsync(old, source, subtitles, ImportMode.KeepSeeding, Recycle);
            Assert(taken is { NewPath: null, Recycled: false } && taken.Problem!.Contains(Path.GetFileName(destination), StringComparison.Ordinal) && File.Exists(old) && new FileInfo(destination).Length == 5, "Name taken: " + Listed());
            File.Delete(destination);
            Assert((await TitleSwap.SwapAsync(Path.Combine(title, "Gone.mkv"), source, subtitles, ImportMode.KeepSeeding, Recycle)) is { NewPath: null, Problem: not null } && File.Exists(video), "The old file gone already");
            Assert((await TitleSwap.SwapAsync(old, source with { Path = Path.Combine(release, "Missing.mkv") }, subtitles, ImportMode.KeepSeeding, Recycle)) is { NewPath: null } && File.Exists(old), "The new file gone");

            // Swapped: the old one in the Recycle Bin, the new one under its name, its subtitle beside; a copy leaves TorLink's folder.
            File.WriteAllBytes(Path.Combine(title, "Dune.2021.720p.WEBRip.x264-GRP.fr.srt"), new byte[1]);
            var swapped = await TitleSwap.SwapAsync(old, source, subtitles, ImportMode.KeepSeeding, Recycle);
            Assert(swapped.NewPath == destination && swapped.Recycled && swapped.Problem is null && !File.Exists(old) && File.Exists(Path.Combine(bin, Path.GetFileName(old))) && new FileInfo(destination).Length == 20,
                "Swapped: " + Listed());
            Assert(swapped.Subtitles.Count == 0 && new FileInfo(Path.Combine(title, "Dune.2021.720p.WEBRip.x264-GRP.fr.srt")).Length == 1, "A subtitle already beside stays as it is");
            Assert(swapped.Moved == !OperatingSystem.IsWindows() || swapped.Moved == !File.Exists(video), "Copied (another drive, or no hard link here): TorLink's file goes");
            Assert(!Directory.EnumerateFiles(title, "*" + TitleSwap.Suffix).Any(), "No temporary file left");

            // The same name: the old file goes, the new one takes its very path.
            File.WriteAllBytes(video = Path.Combine(release, "Dune.2021.2160p.mp4"), new byte[30]);
            var again = await TitleSwap.SwapAsync(destination, new(video, "Dune.2021.2160p.mp4", 30), [new(subtitle, Path.GetFileName(subtitle), 3)], ImportMode.Move, path => { File.Delete(path); return true; });
            Assert(again.NewPath == destination && new FileInfo(destination).Length == 30 && again.Subtitles.Count == 0 && !File.Exists(video),
                "Same name, moved: " + Listed());
        });

        await Test("Meilleure version : le journal attend la fin du téléchargement, vérifie la durée, attend qui regarde, puis remplace ; jamais rangée comme un nouveau titre", async () =>
        {
            var dir = Path.Combine(testRoot, "title-upgrade"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
            var downloads = Path.Combine(dir, "downloads"); var films = Path.Combine(dir, "FILMS"); var title = Path.Combine(films, "Dune (2021)"); var bin = Path.Combine(dir, "bin");
            foreach (var folder in new[] { state.ConfigDirectory, state.DataDirectory, downloads, title, bin }) Directory.CreateDirectory(folder);
            File.WriteAllText(state.ConfigFile, JsonSerializer.Serialize(new { downloadDir = downloads }));
            var profile = Path.Combine(dir, "profile");
            var importer = new TorLinkImporter(profile);
            var at = importer.Baseline.AddSeconds(5).ToUnixTimeMilliseconds();
            object Done(int n, string name) => new { id = Hash(n), name, source = "yts", sizeBytes = 20, dir = downloads, completedAt = at };
            var old = Path.Combine(title, "Dune (2021).mkv"); File.WriteAllBytes(old, new byte[10]);
            var release = Path.Combine(downloads, "Dune.2021.1080p.BluRay.x264-GRP"); Directory.CreateDirectory(release);
            File.WriteAllBytes(Path.Combine(release, "Dune.2021.1080p.BluRay.x264-GRP.mkv"), new byte[20]);
            File.WriteAllBytes(Path.Combine(release, "sample.mkv"), new byte[2]);
            var runtime = TimeSpan.FromMinutes(155);
            var replacement = new TorLinkReplacement { Id = Hash(1).ToUpperInvariant(), ItemId = ItemId, Title = "Dune (2021)", OldPath = old, Runtime = runtime.Ticks };
            importer.ExpectReplacement(replacement);
            Assert(importer.Replacement(Hash(1)) is { State: ReplacementState.Waiting, Id: var id } && id == Hash(1) && importer.Due(state).Count == 0, "Waiting for TorLink");
            var probes = 0; var swaps = 0;
            var snapshot = new TitleSnapshot([new("user-1", true, 2, 0, true, DateTimeOffset.Parse("2026-10-01T20:00:00Z"), null, null)], ["coll-1"]);
            Func<string, CancellationToken, Task<TimeSpan?>> Probe(TimeSpan? length) => (_, _) => { probes++; return Task.FromResult(length); };
            Func<TorLinkReplacement, CancellationToken, Task<ReplacementCheck>> Prepare(ReplacementCheck check) => (_, _) => Task.FromResult(check);
            bool Recycle(string path) { swaps++; File.Move(path, Path.Combine(bin, $"{swaps} {Path.GetFileName(path)}")); return true; }

            // Finished: never placed as a new film, even with automatic placement on.
            File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(new[] { Done(1, "Dune.2021.1080p.BluRay.x264-GRP") }));
            await importer.ProcessAsync(state, new MediaLibraries(films, null, null), ImportMode.KeepSeeding, automatic: true);
            Assert(importer.Find(Hash(1)) is null && Directory.EnumerateFiles(films, "*", SearchOption.AllDirectories).Count() == 1, "Not imported beside the old one");
            Assert(importer.Due(state).Single().Id == Hash(1), "Due once TorLink finished it");

            // Unreadable, another length: given up, the old file stays.
            Assert(await importer.ReplaceAsync(Hash(1), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(null), Recycle) is { State: ReplacementState.Failed, Message: { } unreadable } && unreadable.Contains("ffprobe", StringComparison.Ordinal),
                "Unreadable");
            Assert(importer.Find(Hash(1)) is { State: TorLinkImportState.Failed, Replaced: var replaced, Message: { } message } && replaced == old && message.Contains("L’ancien fichier reste", StringComparison.Ordinal) && File.Exists(old), "Told, the old file kept");
            importer.ExpectReplacement(replacement);
            Assert(await importer.ReplaceAsync(Hash(1), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(runtime * 0.9), Recycle) is { State: ReplacementState.Failed, Message: { } shorter } && shorter.Contains("autre montage", StringComparison.Ordinal), "Another cut");
            importer.ExpectReplacement(replacement);
            Assert(await importer.ReplaceAsync(Hash(1), state, ImportMode.KeepSeeding, Prepare(new(null, Problem: "Ce titre n’est plus dans Jellyfin.")), Probe(runtime), Recycle) is { State: ReplacementState.Failed }, "Gone from Jellyfin");

            // Someone watches it: tried again later, as long as it takes; nothing read again before.
            importer.ExpectReplacement(replacement);
            Assert(await importer.ReplaceAsync(Hash(1), state, ImportMode.KeepSeeding, Prepare(new(null, Wait: "Quelqu’un le regarde.")), Probe(runtime), Recycle) is { State: ReplacementState.Waiting, Attempts: 0, RetryAt: not null, Message: "Quelqu’un le regarde." },
                "Watched now");
            var read = probes;
            Assert(await importer.ReplaceAsync(Hash(1), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(runtime), Recycle) is { State: ReplacementState.Waiting } && probes == read && swaps == 0, "Not before its time");
            Assert(File.Exists(old) && swaps == 0, "The old file never touched until now");

            // Replaced: the sample left out, the old file in the Recycle Bin, the title's state kept for Jellyfin's new item.
            importer.ExpectReplacement(replacement);
            var swapped = await importer.ReplaceAsync(Hash(1), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(runtime * 1.01), Recycle);
            Assert(swapped is { State: ReplacementState.Swapped, NewPath: var placed, Saved: { } saved } && placed == old && saved.Users.Single().PlayCount == 2 && saved.Collections.SequenceEqual(["coll-1"]),
                $"Swapped: {swapped}");
            Assert(new FileInfo(old).Length == 20 && File.Exists(Path.Combine(bin, "1 Dune (2021).mkv")) && !File.Exists(old + TitleSwap.Suffix), "The new file under the old name");
            Assert(importer.Find(Hash(1)) is { State: TorLinkImportState.Imported, Replaced: { }, Files: [{ Role: "video", Destination: var file }, ..] } && file == old, "In the journal as placed, replacing the old file");
            Assert(importer.Due(state).Count == 0 && await importer.ReplaceAsync(Hash(1), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(runtime), Recycle) is { State: ReplacementState.Swapped } && swaps == 1, "Done once");
            Assert(new TorLinkImporter(profile).Replacement(Hash(1)) is { State: ReplacementState.Swapped, Saved.Users: [{ IsFavorite: true }] }, "Kept with the journal");
            Assert(new SavedUserData("u", false, 0, 0, false, null, null, null) is { Kept: false } && snapshot.Users[0].Kept, "An account that never touched the title has nothing to copy");

            // Two videos as long as each other: which one? Given up.
            var two = Path.Combine(downloads, "Dune.2021.Two"); Directory.CreateDirectory(two);
            File.WriteAllBytes(Path.Combine(two, "Dune.Part.1.mkv"), new byte[20]); File.WriteAllBytes(Path.Combine(two, "Dune.Part.2.mkv"), new byte[20]);
            File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(new[] { Done(1, "Dune.2021.1080p.BluRay.x264-GRP"), Done(2, "Dune.2021.Two"), Done(3, "Dune.2021.Three.mkv") }));
            importer.ExpectReplacement(replacement with { Id = Hash(2) });
            Assert(await importer.ReplaceAsync(Hash(2), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(runtime), Recycle) is { State: ReplacementState.Failed, Message: { } several } && several.Contains("plusieurs vidéos", StringComparison.Ordinal), "Several videos");

            // Mira closed while the files changed: the old file in the Recycle Bin, the new one still beside it.
            File.WriteAllBytes(Path.Combine(downloads, "Dune.2021.Three.mkv"), new byte[30]);
            importer.ExpectReplacement(replacement with { Id = Hash(3) });
            importer.UpdateReplacement(importer.Replacement(Hash(3))! with { State = ReplacementState.Swapping, NewPath = old, OldSize = 20 });
            File.Move(old, old + TitleSwap.Suffix);
            Assert(await importer.ReplaceAsync(Hash(3), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(runtime), Recycle) is { State: ReplacementState.Swapped } && File.Exists(old) && !File.Exists(old + TitleSwap.Suffix) && swaps == 1,
                "Recovered: the new file put in place, nothing swapped again");
            // Cut before anything changed (the old file there, of its size): checked and swapped again.
            importer.ExpectReplacement(replacement with { Id = Hash(3) });
            importer.UpdateReplacement(importer.Replacement(Hash(3))! with { State = ReplacementState.Swapping, NewPath = old, OldSize = 20 });
            Assert(await importer.ReplaceAsync(Hash(3), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(runtime), Recycle) is { State: ReplacementState.Swapped } && new FileInfo(old).Length == 30 && swaps == 2,
                "Nothing had changed: done anew");
            // Both gone: said, never guessed.
            importer.ExpectReplacement(replacement with { Id = Hash(3) });
            importer.UpdateReplacement(importer.Replacement(Hash(3))! with { State = ReplacementState.Swapping, NewPath = Path.Combine(title, "Dune (2021).mp4"), OldSize = 30 });
            File.Delete(old);
            Assert(await importer.ReplaceAsync(Hash(3), state, ImportMode.KeepSeeding, Prepare(new(snapshot)), Probe(runtime), Recycle) is { State: ReplacementState.Failed, Message: { } lost } && lost.Contains("Corbeille", StringComparison.Ordinal), "Lost: told where to look");
        });

        await Test("Meilleure version : le relais ne la confie qu'à un Mira qui sait remplacer, une seule à la fois par titre", () =>
        {
            var now = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
            var relay = new TorLinkRelay(() => now);
            AgentReport Report(bool replaces, List<AgentOutcome>? requests = null) => new("PC-SALON", true, null, null, null, null, requests, null, Replaces: replaces);
            Assert(relay.Request(Magnet(1), null, null, "Dune", 1, ItemId).Error!.Contains("mets-le à jour", StringComparison.Ordinal), "No Mira on the PC yet");
            relay.Exchange("pc-1", Report(replaces: false));
            Assert(relay.Status().Agent is { Replaces: false } && relay.Request(Magnet(1), null, null, "Dune", 1, ItemId).Error is not null, "A Mira of 0.8.5 would keep both");
            Assert(relay.Request(Magnet(1), null, null, "Dune", 1, "../etc").Error == "Demande inconnue.", "Not an id of Jellyfin");
            relay.Exchange("pc-1", Report(replaces: true));
            var (first, error) = relay.Request(Magnet(1), null, null, "Dune", 1, ItemId.ToUpperInvariant());
            Assert(first is not null && error is null && relay.Status().Requests.Single() is { Replace: ItemId }, "Asked: " + error);
            Assert(relay.Request(Magnet(1), null, null, "Dune", 1, ItemId).Id == first, "The same, asked twice");
            Assert(relay.Request(Magnet(1), null, null, "Dune", 1).Error == "Ce fichier est déjà demandé au PC serveur.", "The same file, to keep beside");
            Assert(relay.Request(Magnet(2), null, null, "Dune", 1, ItemId).Error == "Une meilleure version de ce titre est déjà demandée.", "Another version of the same title");
            // A Mira put back to an older version on the PC: it keeps the request until one that knows comes.
            Assert(relay.Exchange("pc-1", Report(replaces: false)).Requests.Count == 0, "Not handed to a Mira that would keep both");
            var work = relay.Exchange("pc-1", Report(replaces: true));
            Assert(work.Requests.Single() is { Replace: ItemId, Magnet: var magnet } && magnet == Magnet(1), "Handed with the title it replaces");
            relay.Exchange("pc-1", Report(replaces: true, requests: [new(first!, "sent", null)]));
            Assert(relay.Request(Magnet(2), null, null, "Dune", 1, ItemId).Id is not null, "Sent to TorLink: another one may follow");
            return Task.CompletedTask;
        });
    }
}
