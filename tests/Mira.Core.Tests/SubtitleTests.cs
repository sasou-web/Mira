using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core.Subtitles;
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
                var text = string.Join(" · ", score.Reasons.Select(SubtitleMatch.Text));
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
            Assert(string.Join(" · ", english.Reasons.Select(SubtitleMatch.Text)) == "other release (GRP) · other source (BluRay) · different frame rate (25 / 23.976 fps)", "English: " + string.Join(" · ", english.Reasons.Select(SubtitleMatch.Text)));
            return Task.CompletedTask;
        });
    }
}
