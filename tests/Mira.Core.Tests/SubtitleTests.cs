using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core.Subtitles;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Sous-titres automatiques »: subtitle files read and checked (SubtitleText, SubtitleMatch.Check), candidates of a
/// source scored for a video file (SubtitleMatch, cases in tests/fixtures/subtitle-match.json).
/// </summary>
internal static class SubtitleTests
{
    private const string Srt = "1\r\n00:00:01,000 --> 00:00:02,500\r\nÇa va ?\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n<i>Oui</i>, très bien.\r\nEt toi ?\r\n\r\n";
    private const string Vtt = "WEBVTT\n\nNOTE made by Mira\n\n00:01.000 --> 00:02.500\nÇa va ?\n\nid-2\n01:00:03.000 --> 01:00:04.000 line:90%\nOui\n";
    private const string Ass = "[Script Info]\nTitle: Essai\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\\an8}Ça va ?\nDialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,Oui, très bien.\\NEt toi ?\nComment: 0,0:00:05.00,0:00:06.00,Default,,0,0,0,,jamais\n";

    private static List<SubtitleCue> Cues(int count, double seconds, double every = 4) =>
        Enumerable.Range(0, count).Select(i => new SubtitleCue(TimeSpan.FromSeconds(i * every), TimeSpan.FromSeconds(i * every + 2), "x")).Take(count).ToList();

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Sous-titres : un fichier lu quel que soit son encodage, son format reconnu, ses lignes, et réécrit en SRT", () =>
        {
            Assert(SubtitleText.Decode(Encoding.UTF8.GetBytes("Ça va ?")) == "Ça va ?", "UTF-8");
            Assert(SubtitleText.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Ça")]) == "Ça", "UTF-8 with a mark");
            Assert(SubtitleText.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Ça")]) == "Ça", "UTF-16");
            // Windows-1252: « Ç » at 0xC7 as Latin-1, « œ », « ’ » and « … » where Latin-1 has control characters.
            Assert(SubtitleText.Decode([0xC7, 0x61, 0x20, 0x9C, 0x92, 0x85]) == "Ça œ’…", "Windows-1252: " + SubtitleText.Decode([0xC7, 0x61, 0x20, 0x9C, 0x92, 0x85]));
            Assert(SubtitleText.FormatOf(Srt) == "srt" && SubtitleText.FormatOf(Vtt) == "vtt" && SubtitleText.FormatOf(Ass) == "ass" && SubtitleText.FormatOf("Bonjour") is null, "Formats");
            foreach (var (name, text) in new[] { ("SRT", Srt), ("VTT", Vtt), ("ASS", Ass) })
            {
                var cues = SubtitleText.Parse(text);
                Assert(cues.Count == 2 && cues[0] == new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), "Ça va ?"), name + ": " + string.Join(" | ", cues));
                Assert(cues[1].Text.StartsWith("Oui", StringComparison.Ordinal) && !cues[1].Text.Contains('<') && !cues[1].Text.Contains('{'), name + " tags: " + cues[1].Text);
            }
            Assert(SubtitleText.Parse(Vtt)[1].Start == new TimeSpan(1, 0, 3), "VTT hours");
            Assert(SubtitleText.Parse(Ass)[1].Text == "Oui, très bien.\nEt toi ?", "ASS line break: " + SubtitleText.Parse(Ass)[1].Text);
            var written = SubtitleText.ToSrt(SubtitleText.Parse(Srt));
            Assert(written == Srt.Replace("<i>", "").Replace("</i>", ""), "Written back:\n" + written);
            Assert(SubtitleText.Parse(written).SequenceEqual(SubtitleText.Parse(Srt)), "Round trip");
            Assert(SubtitleText.Parse("").Count == 0 && SubtitleText.Parse("1\n00:00:01,000 --> garbage\n").Count == 0, "Nothing read from nothing");
            return Task.CompletedTask;
        });
        await Test("Sous-titres : un fichier téléchargé vérifié contre la durée de la vidéo", () =>
        {
            var episode = TimeSpan.FromMinutes(24);
            Assert(SubtitleMatch.Check(Cues(300, 0), episode) is { Verdict: SubtitleCheckVerdict.Ok, Cues: 300 }, "An episode's subtitles");
            Assert(SubtitleMatch.Check(Cues(3, 0), episode).Verdict == SubtitleCheckVerdict.Unreadable, "Three cues");
            Assert(SubtitleMatch.Check(Cues(300, 0, every: 2), episode).Verdict == SubtitleCheckVerdict.TooShort, "Ends at 10 minutes of 24");
            Assert(SubtitleMatch.Check(Cues(400, 0), episode).Verdict == SubtitleCheckVerdict.TooLong, "Ends at 26:40 of 24:00");
            Assert(SubtitleMatch.Check(Cues(370, 0), episode).Verdict == SubtitleCheckVerdict.Ok, "Ends 40 s past the end: within the minute");
            Assert(SubtitleMatch.Check(Cues(8, 0, every: 180), episode).Verdict == SubtitleCheckVerdict.TooFew, "Eight cues in 24 minutes");
            var film = TimeSpan.FromMinutes(150);
            Assert(SubtitleMatch.Check(Cues(1200, 0, every: 7.6), film).Verdict == SubtitleCheckVerdict.Ok, "A film: ends 2 min past, within 2 %");
            Assert(SubtitleMatch.Check(Cues(5, 0), null).Verdict == SubtitleCheckVerdict.Ok, "Without the duration: readable is enough");
            Assert(SubtitleMatch.Text(SubtitleMatch.Check(Cues(8, 0, every: 180), episode)) == "trop peu de lignes (8)", "Text");
            return Task.CompletedTask;
        });
        await Test("Sous-titres : la note d'un sous-titre proposé pour un fichier vidéo (tests/fixtures/subtitle-match.json)", () =>
        {
            var repository = TestSupport.Repository() ?? throw new Exception("Repository not found");
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "subtitle-match.json")))!.AsObject();
            foreach (var x in fixture["editions"]!.AsArray())
                Assert(SubtitleMatch.EditionsOf((string)x!["name"]!).Order().SequenceEqual(x["editions"]!.AsArray().Select(e => (string)e!).Order()), "Editions of " + x["name"] + ": " + string.Join(",", SubtitleMatch.EditionsOf((string)x["name"]!)));
            VideoFacts Video(string key)
            {
                var v = fixture["video"]![key]!;
                return new VideoFacts((string)v["file"]!, v["duration"] is { } d ? TimeSpan.FromSeconds((double)d) : null, (double?)v["fps"], (int?)v["width"], (int?)v["season"], (int?)v["episode"], (int?)v["year"]);
            }
            SubtitleCandidate Candidate(JsonNode c) => new((string)c["source"]!, (string)c["id"]!, "fre", (string)c["release"]!, (double?)c["fps"], c["hashMatch"] is { } h && (bool)h,
                c["hearingImpaired"] is { } hi && (bool)hi, c["forced"] is { } f && (bool)f, c["machineTranslated"] is { } m && (bool)m, c["trusted"] is { } t && (bool)t,
                c["downloads"] is { } n ? (int)n : 0, (double?)c["rating"], "srt", c["files"]?.AsArray().Select(x => (string)x!).ToList());
            MatchPreferences Preferences(JsonNode? p) => p is null ? new() : new(p["hearing"] is { } h ? Enum.Parse<HearingPreference>((string)h!) : HearingPreference.Any,
                p["allowMachineTranslated"] is { } m && (bool)m, p["forced"] is { } f && (bool)f);
            var all = new Dictionary<string, SubtitleCandidate>();
            foreach (var x in fixture["scores"]!.AsArray())
            {
                var candidate = Candidate(x!["candidate"]!); all[candidate.Id] = candidate;
                var score = SubtitleMatch.Score(Video((string)x["video"]!), candidate, Preferences(x["preferences"]));
                var reasons = string.Join(",", score.Reasons.Select(r => r.Code));
                Assert(score.Score == (int)x["score"]! && score.Verdict == Enum.Parse<MatchVerdict>((string)x["verdict"]!), $"{x["name"]}: {score.Score} {score.Verdict} ({reasons})");
                Assert(reasons == string.Join(",", x["reasons"]!.AsArray().Select(r => (string)r!)), $"{x["name"]}: reasons {reasons}");
                var text = string.Join(" · ", score.Reasons.Select(r => SubtitleMatch.Text(r)));
                Assert(text == (string)x["text"]!, $"{x["name"]}: {text}");
            }
            var rank = fixture["rank"]!;
            var ranked = SubtitleMatch.Rank(Video((string)rank["video"]!), rank["candidates"]!.AsArray().Select(id => all[(string)id!]));
            Assert(ranked.Select(x => x.Candidate.Id).SequenceEqual(rank["order"]!.AsArray().Select(x => (string)x!)), "Order: " + string.Join(",", ranked.Select(x => x.Candidate.Id)));
            Assert(SubtitleMatch.Automatic(ranked, true).Select(x => x.Candidate.Id).SequenceEqual(rank["automaticWithSync"]!.AsArray().Select(x => (string)x!)), "Automatic with sync");
            Assert(SubtitleMatch.Automatic(ranked, false).Select(x => x.Candidate.Id).SequenceEqual(rank["automaticWithoutSync"]!.AsArray().Select(x => (string)x!)), "Automatic without sync");
            // English for the same reasons.
            Mira.Core.Lang.Use(Mira.Core.Lang.English);
            var english = SubtitleMatch.Score(Video("dune"), all["4"]);
            Assert(string.Join(" · ", english.Reasons.Select(r => SubtitleMatch.Text(r))) == "other release (GRP) · other source (BluRay) · different frame rate (25 / 23.976 fps)", "English: " + string.Join(" · ", english.Reasons.Select(r => SubtitleMatch.Text(r))));
            return Task.CompletedTask;
        });
        await Test("Sous-titres : recalage sur une piste et sur l'audio, décalage et vitesse retrouvés, autre épisode refusé", () =>
        {
            // FFT: a known spectrum.
            var data = new System.Numerics.Complex[8];
            data[1] = 1;
            SubtitleAlign.Fft(data, false);
            Assert(data.All(x => Math.Abs(x.Magnitude - 1) < 1e-9), "FFT of an impulse");
            SubtitleAlign.Fft(data, true);
            Assert(Math.Abs(data[1].Real - 1) < 1e-9 && data.Where((_, i) => i != 1).All(x => x.Magnitude < 1e-9), "Inverse FFT");
            Assert(SpeechDetector.RollingMinimum([5, 3, 4, 9, 8, 7, 1, 6], 1).SequenceEqual([3f, 3, 3, 4, 7, 1, 1, 1]), "Rolling minimum");

            // A film of 20 minutes: when people speak, and the subtitles made for it as shown on screen (a little before
            // the voice, a while after it).
            var length = TimeSpan.FromMinutes(20);
            List<SubtitleCue> Pattern(int seed)
            {
                var random = new Random(seed);
                var list = new List<SubtitleCue>();
                var at = 5000.0;
                while (at < length.TotalMilliseconds - 10000)
                {
                    var spoken = 800 + random.NextDouble() * 3000;
                    list.Add(new SubtitleCue(TimeSpan.FromMilliseconds(at), TimeSpan.FromMilliseconds(at + spoken), "-"));
                    at += spoken + 300 + random.NextDouble() * (random.NextDouble() < 0.1 ? 30000 : 4000);
                }
                return list;
            }
            var speech = Pattern(1);
            var shownRandom = new Random(2);
            var shown = speech.Select(c => c with { Start = c.Start - TimeSpan.FromMilliseconds(shownRandom.Next(0, 200)), End = c.End + TimeSpan.FromMilliseconds(shownRandom.Next(300, 1200)) }).ToList();
            // The file as fetched: made for another release, its times = (true - shift) / speed.
            List<SubtitleCue> Fetched(double speed, double shift) =>
                shown.Select(c => c with { Start = TimeSpan.FromMilliseconds((c.Start.TotalMilliseconds - shift) / speed), End = TimeSpan.FromMilliseconds((c.End.TotalMilliseconds - shift) / speed) }).ToList();
            double Error(List<SubtitleCue> aligned) => aligned.Zip(shown, (a, b) => Math.Abs((a.Start - b.Start).TotalMilliseconds)).Max();

            // On another subtitle track of the file: its own line breaks, a few lines missing.
            var trackRandom = new Random(3);
            var track = SubtitleAlign.Lines(shown.Where(_ => trackRandom.NextDouble() > 0.1)
                .Select(c => c with { Start = c.Start + TimeSpan.FromMilliseconds(trackRandom.Next(-100, 100)), End = c.End + TimeSpan.FromMilliseconds(trackRandom.Next(-300, 300)) }).ToList(), SubtitleAlign.Frames(length));
            foreach (var (speed, shift) in new[] { (1.0, 0.0), (1.0, 2340.0), (1.0, -7420.0), (25 / 23.976, 1200.0), (23.976 / 25, -300.0) })
            {
                var fetched = Fetched(speed, shift);
                var sync = SubtitleAlign.Align(track, fetched)!;
                var aligned = SubtitleAlign.Apply(fetched, sync);
                Assert(sync.Confident && Math.Abs(sync.Ratio - speed) < 0.0005 && Error(aligned) < 250 && sync.Fit > sync.FitBefore - 0.01,
                    $"Track, {speed:F4} {shift}: {sync.Ratio:F5} {sync.Shift.TotalMilliseconds:F0} ms, error {Error(aligned):F0} ms, z {sync.Confidence:F1}");
                Assert(sync.Changes == (speed != 1 || shift != 0), $"Changes {shift}: {sync.Ratio:F7} {sync.Shift.TotalMilliseconds}");
            }

            // On the audio: speech over music that swells and fades, room noise, a loud scene; at 8 kHz, as ffmpeg gives it.
            var detector = new SpeechDetector();
            var noise = new Random(4);
            double Gauss() => Math.Sqrt(-2 * Math.Log(1 - noise.NextDouble())) * Math.Cos(2 * Math.PI * noise.NextDouble());
            var samples = new short[SpeechDetector.Rate];
            var line = 0;
            for (var second = 0; second < (int)length.TotalSeconds; second++)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var ms = second * 1000.0 + i * 1000.0 / SpeechDetector.Rate;
                    while (line < speech.Count && speech[line].End.TotalMilliseconds < ms) line++;
                    var speaking = line < speech.Count && speech[line].Start.TotalMilliseconds <= ms;
                    var music = 1500 * Math.Sin(2 * Math.PI * 220 * ms / 1000) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * ms / 60000));
                    var voice = speaking ? 5000 * Gauss() * Math.Max(0, Math.Sin(2 * Math.PI * 4 * ms / 1000)) : 0;
                    var loud = second is > 600 and < 615 ? 6000 * Gauss() : 0;
                    samples[i] = (short)Math.Clamp(music + 300 * Gauss() + voice + loud, short.MinValue, short.MaxValue);
                }
                detector.Add(MemoryMarshalBytes(samples));
            }
            var audio = detector.Speech();
            Assert(audio.Length == SubtitleAlign.Frames(length) && SubtitleAlign.Fit(audio, SubtitleAlign.Lines(speech, audio.Length)) > 0.7, "Speech found: " + SubtitleAlign.Fit(audio, SubtitleAlign.Lines(speech, audio.Length)));
            foreach (var (speed, shift) in new[] { (1.0, 1730.0), (1.0, -45000.0), (25 / 23.976, 800.0) })
            {
                var fetched = Fetched(speed, shift);
                var sync = SubtitleAlign.Align(audio, fetched)!;
                var aligned = SubtitleAlign.Apply(fetched, sync);
                Assert(sync.Confident && Math.Abs(sync.Ratio - speed) < 0.0005 && Error(aligned) < 500,
                    $"Audio, {speed:F4} {shift}: {sync.Ratio:F5} {sync.Shift.TotalMilliseconds:F0} ms, error {Error(aligned):F0} ms, z {sync.Confidence:F1}");
            }

            // The lines of another episode fit nowhere: not confident, on the track as on the audio.
            var other = Pattern(99);
            Assert(SubtitleAlign.Align(track, other) is { Confident: false } && SubtitleAlign.Align(audio, other) is { Confident: false }, "Another episode");
            Assert(SubtitleAlign.Align(new float[100], shown) is null && SubtitleAlign.Align(track, []) is null, "Nothing to align");

            // Applied: times stretched then moved; a line pushed before the start is cut there, one entirely before it dropped.
            var moved = SubtitleAlign.Apply([new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "a"), new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), "b")],
                new SubtitleSync(TimeSpan.FromSeconds(-1.5), 2, 1, 0, 10, true));
            Assert(moved is [{ Text: "a", Start.TotalMilliseconds: 500, End.TotalMilliseconds: 2500 }, { Start.TotalMilliseconds: 18500, End.TotalMilliseconds: 22500 }], "Apply");
            moved = SubtitleAlign.Apply([new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "a"), new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "b")],
                new SubtitleSync(TimeSpan.FromSeconds(-2.5), 1, 1, 0, 10, true));
            Assert(moved is [{ Text: "b", Start.TotalMilliseconds: 500 }], "Before the start: " + string.Join(", ", moved));
            return Task.CompletedTask;
        });
        await Test("Sous-titres : langues, empreinte du fichier, identifiants et archives des sources", () =>
        {
            Assert(SubtitleLanguages.Two("fre") == "fr" && SubtitleLanguages.Two("fra") == "fr" && SubtitleLanguages.Two("eng") == "en" && SubtitleLanguages.Two("jpn") == "ja" && SubtitleLanguages.Two("xxx") is null, "Two");
            Assert(SubtitleLanguages.Three("fr") == "fre" && SubtitleLanguages.Three("pt-br") == "por" && SubtitleLanguages.Three("EN") == "eng" && SubtitleLanguages.Three("deu") == "ger", "Three");
            // OpenSubtitles' hash: the size, plus the little-endian 64-bit words of the first and last 64 KiB.
            Assert(MovieHash.Compute(new MemoryStream(new byte[131072])) == "0000000000020000", "Zeros: the size alone");
            var bytes = new byte[200000]; bytes[0] = 1; bytes[^8] = 2; bytes[^1] = 0x80;
            Assert(MovieHash.Compute(new MemoryStream(bytes)) == (200000UL + 1 + 2 + 0x8000000000000000UL).ToString("x16"), "Words added: " + MovieHash.Compute(new MemoryStream(bytes)));
            Assert(MovieHash.Compute(new MemoryStream(new byte[1000])) is null, "Too short");
            // The ids given to Jellyfin: no underscore (Jellyfin splits on the first), read back as they were.
            var id = SubtitleIds.Make(SubtitleIds.SubDl, "fre", "/subtitle/3197651-3213944.zip");
            Assert(!id.Contains('_') && SubtitleIds.Parse(id) == (SubtitleIds.SubDl, "fre", "/subtitle/3197651-3213944.zip"), "Id: " + id);
            Assert(SubtitleIds.Parse("xx:fre:00") is null && SubtitleIds.Parse("os:fre:zz") is null && SubtitleIds.Parse(null) is null, "Bad ids");
            // A season's archive: the episode wanted, else nothing (never another episode).
            var zip = Zip(("Frieren.S02E03.1080p.WEB-DL-ToonsHub.fr.srt", Srt), ("Frieren.S02E04.1080p.WEB-DL-ToonsHub.fr.srt", Srt.Replace("Ça va", "Salut")), ("lisez-moi.txt", "pub"));
            Assert(SubtitleArchive.IsZip(zip) && !SubtitleArchive.IsZip(Encoding.UTF8.GetBytes(Srt)), "Zip");
            var picked = SubtitleArchive.Pick(zip, 2, 4)!;
            Assert(picked.FileName.Contains("S02E04") && Encoding.UTF8.GetString(picked.Bytes).Contains("Salut") && picked.Format == "srt", "Episode 4: " + picked.FileName);
            Assert(SubtitleArchive.Pick(zip, 2, 7) is null, "No episode 7: nothing");
            Assert(SubtitleArchive.Pick(Zip(("Dune.Part.Two.srt", Srt)), null, null)?.FileName == "Dune.Part.Two.srt", "A film's only file");
            // Already there: a file beside the video named for the language, by any of its codes; not another language, not another video.
            var folder = Path.Combine(run.Root, "beside"); Directory.CreateDirectory(folder);
            var video = Path.Combine(folder, "Dune.Part.Two.2024.mkv");
            File.WriteAllText(video, ""); File.WriteAllText(Path.Combine(folder, "Dune.Part.Two.2024.en.srt"), ""); File.WriteAllText(Path.Combine(folder, "Dune.Part.Two.2024.fra.forced.ass"), "");
            File.WriteAllText(Path.Combine(folder, "Dune.Part.Two.2024.Extended.fre.srt.txt"), ""); File.WriteAllText(Path.Combine(folder, "Dune.Part.Two.2024.nfo"), "");
            Assert(SubtitleFiles.HasBeside(video, "fr") && SubtitleFiles.HasBeside(video, "en") && !SubtitleFiles.HasBeside(video, "es"), "Beside");
            Assert(!SubtitleFiles.HasBeside(Path.Combine(folder, "Autre.mkv"), "fr") && !SubtitleFiles.HasBeside(Path.Combine(run.Root, "nowhere", "x.mkv"), "fr"), "Other video, no folder");
            return Task.CompletedTask;
        });
        await Test("Sous-titres : OpenSubtitles, ce qui part (jamais le nom du fichier), les résultats, le téléchargement et le quota", async () =>
        {
            var episode = new SubtitleQuery(true, "0123456789abcdef", 1_400_000_000, "tt0000001", null, "tt22248376", "209867", 2, 3, "Frieren", 2023);
            var sent = OpenSubtitlesApi.SearchQuery(episode, "fr");
            Assert(sent == "languages=fr&moviehash=0123456789abcdef&type=episode&parent_imdb_id=22248376&parent_tmdb_id=209867&season_number=2&episode_number=3&order_by=download_count", "Episode: " + sent);
            var film = new SubtitleQuery(false, null, null, null, null, null, null, null, null, "Dune: Part Two", 2024);
            Assert(OpenSubtitlesApi.SearchQuery(film, "en") == "languages=en&type=movie&query=Dune%3A%20Part%20Two&year=2024&order_by=download_count", "Film without ids: " + OpenSubtitlesApi.SearchQuery(film, "en"));
            Assert(OpenSubtitlesApi.SearchQuery(film with { ImdbId = "tt15239678", HashOnly = true, Hash = "ab" }, "en") == "languages=en&moviehash=ab&moviehash_match=only&type=movie&imdb_id=15239678&order_by=download_count", "Film with an id: the name stays home");

            var calls = new List<string>(); var downloads = 0;
            HttpResponseMessage Answer(HttpRequestMessage request)
            {
                var path = request.RequestUri!.PathAndQuery;
                calls.Add($"{request.Method} {path} key={(request.Headers.TryGetValues("Api-Key", out var k) ? k.First() : "")} agent={string.Join(" ", request.Headers.UserAgent)} auth={request.Headers.Authorization}");
                if (path.StartsWith("/api/v1/subtitles?", StringComparison.Ordinal))
                    return JsonResponse(new
                    {
                        total_pages = 1,
                        data = new object[]
                        {
                            new { id = "1", attributes = new { language = "fr", release = "Dune.Part.Two.2024.1080p.WEB-DL.DDP5.1.H.264-FLUX", fps = 23.976, moviehash_match = true, download_count = 1234, from_trusted = true, hearing_impaired = false, machine_translated = false, ai_translated = false, ratings = 8.5, files = new[] { new { file_id = 9001, file_name = "Dune.Part.Two.2024.1080p.WEB-DL.DDP5.1.H.264-FLUX.fr.srt" } } } },
                            new { id = "2", attributes = new { language = "pt-BR", release = "x", fps = 0.0, download_count = 3, ai_translated = true, files = new[] { new { file_id = 9002, file_name = "x.srt" } } } },
                            new { id = "3", attributes = new { language = "fr", release = "sans fichier", files = Array.Empty<object>() } },
                        },
                    });
                if (path == "/api/v1/login") return JsonResponse(new { token = "jwt-1", user = new { allowed_downloads = 20 } });
                if (path == "/api/v1/download")
                    return ++downloads == 1 ? JsonResponse(new { link = "https://www.opensubtitles.com/download/abc/x.srt", remaining = 19, reset_time_utc = "2026-10-29T00:00:00Z" })
                        : new HttpResponseMessage((HttpStatusCode)406) { Content = new StringContent("""{"message":"You have downloaded your allowed 20 subtitles for 24h","remaining":0,"reset_time_utc":"2026-10-29T00:00:00Z"}""") };
                if (path == "/download/abc/x.srt") return new(HttpStatusCode.OK) { Content = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Srt)]) };
                return new(HttpStatusCode.NotFound);
            }
            using var http = new HttpClient(new Handler(request => Task.FromResult(Answer(request))));
            var api = new OpenSubtitlesApi(http, "key-1", "Mira v0.8.6", "963sasou", "secret");
            var found = await api.SearchAsync(film with { ImdbId = "tt15239678" }, "fr", default);
            Assert(found.Count == 2 && found[0] is { Id: "9001", Language: "fre", HashMatch: true, Trusted: true, Downloads: 1234, Fps: 23.976, MachineTranslated: false }, "First: " + found.FirstOrDefault());
            Assert(found[1] is { Language: "por", MachineTranslated: true, Fps: null }, "Second: " + found[1]);
            Assert(calls.All(x => x.Contains("key=key-1") && x.Contains("agent=Mira v0.8.6")) && !calls[0].Contains("Dune"), "Headers, and no name with an id: " + calls[0]);
            calls.Clear();
            var file = await api.FetchAsync("9001", null, null, default);
            Assert(Encoding.UTF8.GetString(file.Bytes).Contains("Ça va") && api.Quota is (19, { }), "Fetched, quota: " + api.Quota);
            Assert(calls.Any(x => x.StartsWith("POST /api/v1/login", StringComparison.Ordinal)) && calls.Any(x => x.StartsWith("POST /api/v1/download", StringComparison.Ordinal) && x.Contains("auth=Bearer jwt-1")), "Logged in, token sent: " + string.Join("\n", calls));
            // The temporary link gets neither the key nor the token: only what it needs.
            Assert(calls.Single(x => x.StartsWith("GET /download/", StringComparison.Ordinal)) is var file0 && file0.Contains("key= ") && file0.EndsWith("auth=", StringComparison.Ordinal), "Link: " + string.Join("\n", calls));
            try { await api.FetchAsync("9002", null, null, default); throw new Exception("No quota error"); }
            catch (SubtitleSourceException ex) { Assert(ex.Until == DateTimeOffset.Parse("2026-10-29T00:00:00Z") && ex.Message.Contains("20 subtitles"), "Quota: " + ex.Message); }
        });
        await Test("Sous-titres : SubDL, ce qui part, l'épisode gardé et l'archive d'une saison", async () =>
        {
            var episode = new SubtitleQuery(true, "0123456789abcdef", 1, null, null, "tt22248376", null, 2, 3, "Frieren", 2023);
            Assert(SubDlApi.SearchQuery(episode, "fr") == "languages=FR&type=tv&releases=1&subs_per_page=30&imdb_id=tt22248376&season_number=2&episode_number=3", "Episode: " + SubDlApi.SearchQuery(episode, "fr"));
            var calls = new List<string>();
            HttpResponseMessage Answer(HttpRequestMessage request)
            {
                var path = request.RequestUri!.PathAndQuery;
                calls.Add($"{request.Method} {request.RequestUri.Host}{path} agent={string.Join(" ", request.Headers.UserAgent)} key={request.Headers.GetValues("X-API-Key").FirstOrDefault()}");
                if (path.StartsWith("/api/v1/subtitles?", StringComparison.Ordinal))
                    return JsonResponse(new
                    {
                        status = true,
                        subtitles = new object[]
                        {
                            new { release_name = "Sousou.no.Frieren.S02E03.1080p.WEB-DL.HEVC-ToonsHub", name = "SUBDL::frieren", lang = "french", language = "FR", season = 2, episode = 3, hi = false, full_season = false, url = "/subtitle/1-2.zip" },
                            new { release_name = "Sousou.no.Frieren.S02E04.1080p.WEB-DL.HEVC-ToonsHub", name = "x", language = "FR", season = 2, episode = 4, full_season = false, url = "/subtitle/1-3.zip" },
                            new { release_name = "Sousou.no.Frieren.S02.1080p.WEB-DL.HEVC-ToonsHub", name = "y", language = "FR", season = 2, episode = 0, full_season = true, url = "/subtitle/1-4.zip" },
                            new { release_name = "Sousou.no.Frieren.S01.1080p", name = "z", language = "FR", season = 1, full_season = true, url = "/subtitle/1-5.zip" },
                        },
                    });
                if (path == "/subtitle/1-4.zip") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(("Frieren.S02E02.srt", "a"), ("Frieren.S02E03.srt", Srt))) };
                return new(HttpStatusCode.NotFound);
            }
            using var http = new HttpClient(new Handler(request => Task.FromResult(Answer(request))));
            var api = new SubDlApi(http, "own-key", "Mira v0.8.6");
            var found = await api.SearchAsync(episode, "fr", default);
            Assert(found.Select(x => x.Id).SequenceEqual(["/subtitle/1-2.zip", "/subtitle/1-4.zip"]), "Episode 3 and the season 2 pack: " + string.Join(", ", found.Select(x => x.Id)));
            Assert(found[0] is { Source: "SubDL", Language: "fre", HashMatch: false } && found[0].ReleaseName.EndsWith("ToonsHub", StringComparison.Ordinal), "Candidate: " + found[0]);
            var file = await api.FetchAsync("/subtitle/1-4.zip", 2, 3, default);
            Assert(file.FileName == "Frieren.S02E03.srt" && Encoding.UTF8.GetString(file.Bytes) == Srt, "The episode from the pack: " + file.FileName);
            Assert(calls.All(x => x.EndsWith("agent=Mira v0.8.6 key=own-key", StringComparison.Ordinal)) && calls[1].StartsWith("GET dl.subdl.com/subtitle/", StringComparison.Ordinal), "Hosts and key: " + string.Join("\n", calls));
            try { await api.FetchAsync("https://evil.example/x.zip", 2, 3, default); throw new Exception("Other host fetched"); }
            catch (SubtitleSourceException) { }
        });
        // The real services, only when asked (subtitles.yml, a runner that reaches them): one search and one download
        // of each, with Mira's own OpenSubtitles key; SubDL with the key in MIRA_SUBDL_KEY when there is one.
        if (Environment.GetEnvironmentVariable("MIRA_SUBTITLES_LIVE") == "1")
            await Test("Sous-titres : vraies sources, une recherche et un téléchargement", async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var matrix = new SubtitleQuery(false, null, null, "tt0133093", "603", null, null, null, null, "The Matrix", 1999);
                var video = new VideoFacts("The.Matrix.1999.1080p.BluRay.x264-AMIABLE.mkv", TimeSpan.FromMinutes(136), 23.976, 1920, Year: 1999);
                var os = new OpenSubtitlesApi(http, OpenSubtitlesApi.MiraKey, "Mira v0.0.0-check");
                var found = await os.SearchAsync(matrix, "fr", default);
                var ranked = SubtitleMatch.Rank(video, found);
                Console.WriteLine($"      OpenSubtitles: {found.Count} candidates; best {ranked[0].Score} {ranked[0].Candidate.ReleaseName} ({string.Join(" · ", ranked[0].Reasons.Select(r => SubtitleMatch.Text(r)))})");
                Assert(found.Count > 5 && found.All(x => x.Language == "fre" && x.Id.All(char.IsAsciiDigit)) && found.Any(x => x.Fps is not null), "OpenSubtitles results: " + found.Count);
                var file = await os.FetchAsync(ranked[0].Candidate.Id, null, null, default);
                var cues = SubtitleText.Parse(SubtitleText.Decode(file.Bytes));
                var check = SubtitleMatch.Check(cues, video.Duration);
                Console.WriteLine($"      OpenSubtitles file: {file.FileName}, {cues.Count} lines, {check.Verdict}; quota {os.Quota}");
                Assert(cues.Count > 300 && check.Verdict == SubtitleCheckVerdict.Ok && os.Quota is { }, "OpenSubtitles file: " + check);
                if (Environment.GetEnvironmentVariable("MIRA_SUBDL_KEY") is { Length: > 0 } key)
                {
                    var sd = new SubDlApi(http, key, "Mira v0.0.0-check");
                    var series = new SubtitleQuery(true, null, null, null, null, "tt0903747", "1396", 1, 1, "Breaking Bad", 2008);
                    var episodes = await sd.SearchAsync(series, "fr", default);
                    Console.WriteLine($"      SubDL: {episodes.Count} candidates, first {episodes.FirstOrDefault()?.ReleaseName}");
                    Assert(episodes.Count > 0, "SubDL results");
                    var episodeFile = await sd.FetchAsync(episodes[0].Id, 1, 1, default);
                    var episodeCues = SubtitleText.Parse(SubtitleText.Decode(episodeFile.Bytes));
                    Console.WriteLine($"      SubDL file: {episodeFile.FileName}, {episodeCues.Count} lines");
                    Assert(episodeCues.Count > 100, "SubDL file");
                }
                else Console.WriteLine("      SubDL: no key (MIRA_SUBDL_KEY), not tried");
            });
        await Test("Sous-titres : file d'attente, essais espacés, abandon après un mois, gardée au redémarrage", () =>
        {
            var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
            var file = Path.Combine(run.Root, "Mira.subtitles-queue.json");
            var queue = new SubtitleQueue(file, () => now);
            queue.Add("a", "fre"); queue.Add("a", "eng"); queue.Add("b", "fre");
            now += TimeSpan.FromMinutes(1); queue.Add("a", "fre");
            Assert(queue.Count == 3 && queue.Due(10).Count == 0, "Nothing due before two minutes, no double");
            now += TimeSpan.FromMinutes(1);
            Assert(queue.Due(2).Select(x => x.ItemId + x.Language).SequenceEqual(["afre", "aeng"]) && queue.Due(10).Count == 3, "Due, the oldest first, at most asked");
            queue.Done("a", "eng");
            // Tries: +1 h, +6 h, +1 d, +3 d, +7 d, then every week until 30 days after the title arrived.
            var tries = new List<TimeSpan>();
            var arrived = now - TimeSpan.FromMinutes(2);
            while (queue.Retry("a", "fre"))
            {
                var wait = JsonNode.Parse(File.ReadAllText(file))!.AsArray().First(x => (string)x!["itemId"]! == "a")!;
                var at = DateTimeOffset.Parse((string)wait["next"]!, CultureInfo.InvariantCulture);
                tries.Add(at - now); now = at;
            }
            Assert(tries.Take(5).SequenceEqual(SubtitleQueue.Delays) && tries.Skip(5).All(x => x == SubtitleQueue.Week) && tries.Count == 7, "Delays: " + string.Join(", ", tries));
            Assert(now - arrived <= SubtitleQueue.GiveUpAfter && queue.Count == 1, "Given up within a month, the other title kept");
            Assert(SubtitleQueue.NextAttempt(arrived, 1, arrived + TimeSpan.FromDays(29.99)) is null && SubtitleQueue.NextAttempt(arrived, 1, arrived) == arrived + TimeSpan.FromHours(1), "Never past a month");
            // A quota spent: the try is not counted, the title keeps its place until the reset.
            var reset = now + TimeSpan.FromHours(5);
            queue.Postpone("b", "fre", reset);
            Assert(queue.Due(10).Count == 0, "Postponed");
            var again = new SubtitleQueue(file, () => reset);
            Assert(again.Count == 1 && again.Due(10) is [{ ItemId: "b", Attempts: 0 }], "Kept in its file, tries not counted");
            again.Add("c", "fre"); again.Forget("c"); again.Forget("b");
            Assert(again.Count == 0 && new SubtitleQueue(file).Count == 0, "Title gone");
            File.WriteAllText(file, "{ broken");
            Assert(new SubtitleQueue(file).Count == 0, "Broken file: empty queue");
            return Task.CompletedTask;
        });
        await Test("Sous-titres : réglages gardés par l'extension, secrets jamais renvoyés", () =>
        {
            var file = Path.Combine(run.Root, "Mira.subtitles.json");
            var config = new SubtitleConfig(file);
            config.Update(JsonNode.Parse("""{"enabled":true,"languages":["fre","eng","klingon","FRE"],"hearing":"avoid","openSubtitles":{"username":"963sasou","password":"secret","apiKey":""},"subDl":{"enabled":true,"apiKey":"own"}}""")!.AsObject());
            var shown = config.Public().ToJsonString();
            Assert(!shown.Contains("secret") && !shown.Contains("\"own\"") && shown.Contains("\"account\":\"963sasou\"") && shown.Contains("\"languages\":[\"fre\",\"eng\"]"), "Public: " + shown);
            Assert(new SubtitleConfig(file).Current is { Enabled: true, Hearing: "avoid", SubDl.ApiKey: "own", OpenSubtitles.Password: "secret" }, "Kept in the file");
            config.Update(JsonNode.Parse("""{"subDl":{"enabled":false},"openSubtitles":{"password":""}}""")!.AsObject());
            Assert(config.Current is { SubDl: { Enabled: false, ApiKey: "own" }, OpenSubtitles: { Password: "", Username: "963sasou" } }, "A secret not given is kept, given empty is cleared");
            Assert(config.Current.Preferences == new MatchPreferences(HearingPreference.Avoid, false), "Preferences");
            var journal = new SubtitleJournal(Path.Combine(run.Root, "journal.json"));
            for (var i = 0; i < SubtitleJournal.Keep + 5; i++) journal.Add(new SubtitleJournalEntry(DateTimeOffset.UnixEpoch.AddMinutes(i), "id", "Titre " + i, "fre", "none"));
            Assert(journal.Recent(2).Select(x => x.Name).SequenceEqual(["Titre 204", "Titre 203"]) && new SubtitleJournal(Path.Combine(run.Root, "journal.json")).Recent(500).Count == SubtitleJournal.Keep, "Journal");
            return Task.CompletedTask;
        });
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, text) in files)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        return memory.ToArray();
    }

    private static byte[] MemoryMarshalBytes(short[] samples) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
}
