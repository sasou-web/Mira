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

/// <summary>The player: volume, skipped segments, chapters, tracks, scrolling and carousel. Free of WPF: also run outside Windows.</summary>
internal static class PlayerTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Regarder ensemble : horloge du serveur, commandes du groupe, titre et mots, comme Mira web (tests/fixtures/syncplay.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "syncplay.json")));
            var root = fixture.RootElement;
            foreach (var row in root.GetProperty("times").EnumerateArray())
                Assert(Together.ServerTime(row[0].GetString()) == row[1].GetInt64(), $"{row[0]}: {Together.ServerTime(row[0].GetString())}");
            Assert(Together.ServerTime(null) is null && Together.ServerTime("x") is null, "Not a date");
            foreach (var row in root.GetProperty("clock").EnumerateArray())
            {
                var found = Together.ClockOffset(row[0].EnumerateArray().Select(x => new Together.ClockSample(x.GetProperty("sent").GetInt64(), x.GetProperty("received").GetInt64(), x.GetProperty("serverIn").GetInt64(), x.GetProperty("serverOut").GetInt64())));
                Assert(row[1].ValueKind == JsonValueKind.Null ? found is null : found == (row[1].GetInt64(), row[2].GetInt64()), $"Clock: {found}");
            }
            foreach (var row in root.GetProperty("commands").EnumerateArray())
            {
                var plan = Together.CommandPlan(row[2], row[0].GetInt64(), row[1].GetInt64());
                if (row[3].ValueKind == JsonValueKind.Null) { Assert(plan is null, "Unknown command: " + plan); continue; }
                Assert(plan is not null && plan.Action == row[3].GetProperty("action").GetString() && plan.Wait == row[3].GetProperty("wait").GetInt64()
                    && Math.Abs(plan.Position - row[3].GetProperty("position").GetDouble()) < 1e-6, $"{row[2]}: {plan}");
            }
            foreach (var row in root.GetProperty("queue").EnumerateArray())
            {
                var entry = Together.QueueItem(row[0]);
                Assert(row[1].ValueKind == JsonValueKind.Null ? entry is null
                    : entry == new Together.Entry(row[1].GetProperty("itemId").GetString()!, row[1].GetProperty("playlistItemId").GetString()!, row[1].GetProperty("startTicks").GetInt64()), $"Queue: {entry}");
            }
            foreach (var row in root.GetProperty("groups").EnumerateArray())
                Assert(Together.ParseGroup(row[0]) is { } group && Together.GroupText(group) == row[1].GetString(), $"Group: {row[1]}");
            foreach (var row in root.GetProperty("names").EnumerateArray())
                Assert(Together.NamesText([.. row[0].EnumerateArray().Select(x => x.GetString()!)]) == row[1].GetString(), $"Names: {row[1]}");
            foreach (var row in root.GetProperty("statuses").EnumerateArray())
                Assert(Together.ParseGroup(row[0]) is { } group && Together.GroupStatus(group, row[1].GetString(), row[2].GetBoolean()) is var status
                    && status.Text == row[3].GetProperty("text").GetString() && status.Alert == row[3].GetProperty("alert").GetBoolean(), $"Status: {row[3]} ≠ {(Together.ParseGroup(row[0]) is { } g ? Together.GroupStatus(g, row[1].GetString(), row[2].GetBoolean()) : null)}");
            Assert(Together.MemberText("Marc", true) == "Marc a rejoint le groupe" && Together.MemberText("Marc", false) == "Marc a quitté le groupe", "Member");
            Assert(Together.GroupName("mira") == "Groupe de mira" && Together.NeedsSeek(10, 10.6) && !Together.NeedsSeek(10, 10.4), "Words, drift");
            foreach (var row in root.GetProperty("invites").EnumerateArray())
            {
                var link = Together.InviteLink(row[0].GetString()!, row[1].GetString()!, row[2].GetString());
                Assert(link == row[3].GetString() && Together.InviteSteps(link).SequenceEqual(row[4].EnumerateArray().Select(x => x.GetString())), $"Invite: {link}");
            }
            // « Ouvrir dans Mira »: built by Mira web, read back by Mira for Windows and Mac, which refuse anything else.
            foreach (var row in root.GetProperty("appLinks").EnumerateArray())
            {
                var link = Together.AppLink(row[0].GetString()!, row[1].GetString()!, row[2].GetString());
                Assert(link == row[3].GetString() && Together.ParseAppLink(link) == new Together.Invitation(row[1].GetString()!, row[0].GetString()!.TrimEnd('/'), row[2].GetString()), $"App link: {link}");
            }
            foreach (var bad in root.GetProperty("badAppLinks").EnumerateArray())
                Assert(Together.ParseAppLink(bad.GetString()) is null, $"Refused: {bad.GetString()}");
            Assert(Together.ParseAppLink(null) is null, "No link");
            // Found among the groups of this Jellyfin, whatever address it is reached at; else another Jellyfin, or gone.
            var invite = new Together.Invitation("7464412130f54334a8a14638f0060cd6", "http://100.101.102.103:8096");
            List<Together.Group> groups = [new("74644121-30f5-4334-a8a1-4638f0060cd6", "Groupe de mira", ["mira"], "Idle")];
            var answer = Together.Answer(invite, groups, "http://192.168.1.20:8096", null);
            Assert(answer.Kind == Together.InviteKind.Join && answer.Group == groups[0] && answer.Text == "On t’invite à regarder ensemble dans « Groupe de mira » (1 personne : mira).", answer.Text);
            Assert(Together.Answer(invite, groups, "http://192.168.1.20:8096", "7464412130f54334a8a14638f0060cd6").Kind == Together.InviteKind.Already, "Already in it");
            Assert(Together.Answer(invite, [], "http://192.168.1.20:8096", null).Kind == Together.InviteKind.OtherServer, "Another Jellyfin");
            Assert(Together.Answer(invite, [], "http://100.101.102.103:8096/", null).Kind == Together.InviteKind.Gone, "Gone");
            // A Jellyfin here: its Tailscale address first, else the home network's for localhost; any other keeps its own.
            Assert(Together.InviteServer("http://localhost:8096", true, "100.101.102.103", "192.168.1.20") == "http://100.101.102.103:8096", "Here, Tailscale");
            Assert(Together.InviteServer("http://127.0.0.1:8096/", true, null, "192.168.1.20") == "http://192.168.1.20:8096", "Here, home network");
            Assert(Together.InviteServer("http://192.168.1.20:8096", true, null, "192.168.1.20") == "http://192.168.1.20:8096", "Here, at its address");
            Assert(Together.InviteServer("https://films.example.org/jellyfin/", false, "100.101.102.103", null) == "https://films.example.org/jellyfin", "Elsewhere");
            return Task.CompletedTask;
        });
        await Test("Regarder ensemble : un lien qui reste valable, groupe refait sous le même nom ou attendu, comme Mira web (tests/fixtures/syncplay.json)", async () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "syncplay.json")));
            var root = fixture.RootElement;
            static List<Together.Group> Groups(JsonElement list) => list.EnumerateArray().Select(Together.ParseGroup).OfType<Together.Group>().ToList();
            foreach (var row in root.GetProperty("hosts").EnumerateArray())
                Assert(Together.GroupHost(row[0].GetString()) == row[1].GetString(), $"Host of {row[0]}: {Together.GroupHost(row[0].GetString())}");
            foreach (var row in root.GetProperty("finds").EnumerateArray())
            {
                var found = Together.FindGroup(Groups(row[0]), row[1].GetString()!, row[2].GetString());
                Assert(found?.GroupId == row[3].GetString(), $"Find {row[1]} · {row[2]}: {found?.GroupId}");
            }
            foreach (var row in root.GetProperty("rejoins").EnumerateArray())
            {
                var lost = Together.ParseGroup(row[1])!;
                var plan = Together.RejoinPlan(Groups(row[0]), lost, row[2].GetBoolean());
                var expected = row[3].ValueKind == JsonValueKind.Null ? null
                    : new Together.Rejoin(row[3].TryGetProperty("join", out var j) ? j.GetString() : null, row[3].TryGetProperty("create", out var c) ? c.GetString() : null);
                Assert(plan == expected, $"Rejoin {lost.GroupName} · {row[2]}: {plan}");
            }
            Assert(Together.WaitingText("mira") == "mira n’a pas encore lancé la soirée", "Waiting words");
            // An invitation with its host: the group made again since is the one to join; none yet, Mira can wait for it.
            var invite = new Together.Invitation("7464412130f54334a8a14638f0060cd6", "http://100.101.102.103:8096", "mira");
            List<Together.Group> again = [new("b61818a0ff7c4c6eb39e2b73b85f3bcc", "Groupe de mira", ["mira"], "Idle")];
            var answer = Together.Answer(invite, again, "http://100.101.102.103:8096", null);
            Assert(answer.Kind == Together.InviteKind.Join && answer.Group == again[0], answer.Text);
            answer = Together.Answer(invite, [], "http://100.101.102.103:8096", null);
            Assert(answer.Kind == Together.InviteKind.Waiting && answer.Text == "mira n’a pas encore lancé la soirée : Mira peut t’y faire entrer dès que le groupe est ouvert.", answer.Text);
            Assert(Together.Answer(invite, [], "http://192.168.1.20:8096", null).Kind == Together.InviteKind.OtherServer, "Waiting is for this Jellyfin only");
            // Waited for: Jellyfin read again until the group opens; a time it does not answer changes nothing.
            var reads = 0;
            List<IReadOnlyList<Together.Group>?> answers = [[], null, [new Together.Group("11111111111111111111111111111111", "Groupe de léa", ["léa"], "Idle")], again];
            var waited = await Together.WaitForGroupAsync(_ => Task.FromResult(answers[Math.Min(reads++, answers.Count - 1)]), invite, TimeSpan.FromMilliseconds(5), CancellationToken.None);
            Assert(waited == again[0] && reads == 4, $"Waited: {waited.GroupId} after {reads} reads");
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
            var stopped = false;
            try { await Together.WaitForGroupAsync(_ => Task.FromResult<IReadOnlyList<Together.Group>?>([]), invite, TimeSpan.FromMilliseconds(5), stop.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "Waiting stops when canceled");
        });
        await Test("Minuterie : pause au bout des minutes, une seule fois, ou arrêt à la fin de l’épisode, comme Mira web (tests/fixtures/sleep.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "sleep.json")));
            foreach (var row in fixture.RootElement.GetProperty("durations").EnumerateArray())
                Assert(SleepTimer.Duration(row[0].GetInt32()) == row[1].GetString(), $"{row[0]}: {SleepTimer.Duration(row[0].GetInt32())}");
            var start = new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.FromHours(2));
            foreach (var row in fixture.RootElement.GetProperty("status").EnumerateArray())
            {
                var timer = new SleepTimer(); int? choice = row[0].ValueKind == JsonValueKind.Null ? null : row[0].GetInt32();
                timer.Choose(choice, start);
                var now = start.AddSeconds(row[1].GetInt32());
                var due = timer.Due(now); var status = timer.Status(now);
                Assert(due == row[3].GetBoolean() && status == (row[2].ValueKind == JsonValueKind.Null ? null : row[2].GetString()), $"{choice} after {row[1]} s: {due} « {status} »");
                if (due) Assert(timer.Chosen is null && !timer.AtEnd, "Off once due");
            }
            var end = new SleepTimer(); end.Choose(0, start);
            Assert(end.AtEnd && end.Chosen == 0 && !end.Due(start.AddHours(5)), "At the end: no pause");
            end.Clear();
            Assert(!end.AtEnd && end.Chosen is null && end.Status(start) is null && string.Join(",", SleepTimer.Choices) == "15,30,60", "Cleared");
            return Task.CompletedTask;
        });
        await Test("Volume : au-delà de 100 %, mpv reste à 100 et le filtre amplifie sans saturer", () =>
        {
            Assert(VolumeBoost.Split(80) == (80, 1) && VolumeBoost.Split(100) == (100, 1), "Up to 100, mpv's own volume and no gain");
            Assert(VolumeBoost.Split(150) is (100, var gain) && Math.Abs(gain - 3.375) < 1e-9, "150 is mpv's 150: 100 then 1.5³ of gain");
            Assert(VolumeBoost.Split(200) is (100, 8) && VolumeBoost.Split(260) is (100, 8), "The level stops at 200, 8 times louder (+18 dB)");
            Assert(VolumeBoost.Split(-5) == (0, 1) && VolumeBoost.Clamp(double.NaN) == 80, "Below zero is silence; a broken saved level is the default");
            Assert(VolumeBoost.Filter(3.375) == "@mira-boost:lavfi=[volume@gain=volume=3.375:precision=float,alimiter=limit=0.95:level=disabled]",
                "Gain in floating point, then a limiter at −0.4 dB without gain of its own, under a label for live changes");
            Assert(VolumeBoost.Filter(1.157625) == "@mira-boost:lavfi=[volume@gain=volume=1.1576:precision=float,alimiter=limit=0.95:level=disabled]", "Gain written with a dot, four decimals");
            Assert(VolumeBoost.Reported(150) == 100 && VolumeBoost.Reported(42.4) == 42, "Jellyfin's scale stops at 100");
            Assert(VolumeBoost.Text(135.2) == "135 %" && VolumeBoost.Text(0) == "0 %", "The level beside the slider");
            return Task.CompletedTask;
        });
        await Test("Progression bornée et libellés d’épisodes", () =>
        {
            var item = new MediaItem { Id = "x", Name = "Le départ", SeriesName = "Les étoiles", Type = "Episode", ParentIndexNumber = 2, IndexNumber = 3, RunTimeTicks = 100, UserData = new() { PlaybackPositionTicks = 200 } };
            Assert(item.Progress == 1, "Progress overflow"); Assert(item.Subtitle.Contains("S02 · E03"), "Episode label missing"); return Task.CompletedTask;
        });
        await Test("Défilement : même distance à 30, 60 et 144 images/s", () =>
        {
            var positions = new List<double>();
            foreach (var fps in new[] { 30, 60, 144 })
            {
                var motion = new ScrollMotion(); motion.Add(432, 1000);
                for (var frame = 0; frame < fps; frame++) motion.Step(.2 / fps, 1000);
                positions.Add(motion.Position);
                Assert(motion.Position > 300 && motion.Position < 432, "Scroll must interpolate rather than jump");
            }
            Assert(positions.Max() - positions.Min() < .001, "Frame rate changes the easing speed"); return Task.CompletedTask;
        });
        await Test("Défilement : inversion immédiate et bornes après changement du contenu", () =>
        {
            var motion = new ScrollMotion(); motion.Reset(300); motion.Add(600, 1000); motion.Step(.04, 1000);
            var before = motion.Position; motion.Add(-108, 1000);
            Assert(motion.Step(.016, 1000) < before, "Reversing the wheel continues in the old direction");
            motion.Step(.1, 120); Assert(motion.Position <= 120 && motion.Target <= 120, "Shrinking content overscrolls");
            motion.Add(-1000, 120); motion.Step(10, 120); Assert(motion.Position == 0 && !motion.Moving, "Top boundary never settles");
            motion.Add(1000, 120); motion.Step(10, 120); Assert(motion.Position == 120 && !motion.Moving, "Bottom boundary never settles");
            return Task.CompletedTask;
        });
        await Test("Carrousel : progression, pause, reprise et changement manuel", () =>
        {
            var timeline = new CarouselTimeline(8);
            Assert(!timeline.Advance(4, false) && timeline.Progress == .5, "Halfway marker is incorrect");
            Assert(!timeline.Advance(40, true) && timeline.Progress == .5, "Paused carousel advances");
            Assert(timeline.Advance(4, false) && timeline.Progress == 1, "Slide does not end after resumed duration");
            timeline.Reset(); Assert(timeline.Progress == 0 && !timeline.Advance(7.9, false), "Manual selection inherits previous duration");
            return Task.CompletedTask;
        });
        await Test("Segments Jellyfin : types demandés, passages inconnus ou trop courts ignorés", async () =>
        {
            Uri? requestUri = null;
            using var client = new JellyfinClient(new("http://localhost/jellyfin/", "user-1", "Alice", "secret", "device"), new Handler(request =>
            {
                requestUri = request.RequestUri;
                return Task.FromResult(JsonResponse(new
                {
                    Items = new object[]
                    {
                        new { Type = "Outro", StartTicks = 1_300 * TimeSpan.TicksPerSecond, EndTicks = 1_390 * TimeSpan.TicksPerSecond },
                        new { Type = "Intro", StartTicks = 60 * TimeSpan.TicksPerSecond, EndTicks = 150 * TimeSpan.TicksPerSecond },
                        new { Type = "Unknown", StartTicks = 0L, EndTicks = 30 * TimeSpan.TicksPerSecond },
                        new { Type = "Recap", StartTicks = 0L, EndTicks = TimeSpan.TicksPerSecond }
                    },
                    TotalRecordCount = 4
                }));
            }));
            var segments = PlaybackMarkers.FromJellyfin(await client.SegmentsAsync("episode 1"));
            Assert(requestUri!.AbsolutePath == "/jellyfin/MediaSegments/episode%201", "Segment path or escaping changed");
            foreach (var type in new[] { "Intro", "Outro", "Recap", "Preview", "Commercial" }) Assert(requestUri.Query.Contains("includeSegmentTypes=" + type), "Missing segment type " + type);
            Assert(segments.SequenceEqual(new[] { new SkipSegment(SkipKind.Intro, 60, 150), new SkipSegment(SkipKind.Outro, 1300, 1390) }), "Segments not filtered and ordered");
        });
        await Test("Chapitres nommés : opening, ending, récap, aperçu et générique selon sa position", () =>
        {
            var chapters = new List<ChapterMark> { new(0, "Récapitulatif"), new(45, "OP"), new(135, "Partie A"), new(700, "Chapitre 4"), new(1290, "Ending"), new(1380, "Next Episode Preview"), new(1410, null) };
            var passages = PlaybackMarkers.FromChapters(chapters, 1420);
            Assert(passages.SequenceEqual(new[] { new SkipSegment(SkipKind.Recap, 0, 45), new SkipSegment(SkipKind.Intro, 45, 135), new SkipSegment(SkipKind.Outro, 1290, 1380), new SkipSegment(SkipKind.Preview, 1380, 1410) }), "Named chapters misclassified: " + string.Join(", ", passages));
            Assert(PlaybackMarkers.Classify("Générique", 30, 1400) == SkipKind.Intro && PlaybackMarkers.Classify("Générique", 1300, 1400) == SkipKind.Outro, "Bare credits title ignores its position");
            Assert(PlaybackMarkers.Classify("Avant-générique") is null && PlaybackMarkers.Classify("Top Gun") is null && PlaybackMarkers.Classify("Opening Credits") == SkipKind.Intro, "Cold open or ordinary titles are skippable");
            Assert(PlaybackMarkers.FromChapters(new List<ChapterMark> { new(0, "Opening"), new(600, "Épisode") }, 1400).Count == 0, "A ten-minute chapter is not an opening");
            return Task.CompletedTask;
        });
        await Test("Passages : segments serveur prioritaires, passage actif et fin vers l’épisode suivant", () =>
        {
            var server = new List<SkipSegment> { new(SkipKind.Intro, 60, 150) };
            var guessed = new List<SkipSegment> { new(SkipKind.Intro, 50, 140), new(SkipKind.Outro, 1290, 1380), new(SkipKind.Preview, 1380, 1418) };
            var merged = PlaybackMarkers.Merge(server, guessed);
            Assert(merged.SequenceEqual(new[] { server[0], guessed[1], guessed[2] }), "Chapter guesses override the server");
            Assert(PlaybackMarkers.Active(merged, 59.8) == server[0] && PlaybackMarkers.Active(merged, 149.5) is null && PlaybackMarkers.Active(merged, 700) is null, "Active passage boundaries changed");
            Assert(PlaybackMarkers.EndsPlayback(merged, guessed[1], 1420) && !PlaybackMarkers.EndsPlayback(merged, server[0], 1420), "Credits followed by a preview should lead to the next episode");
            Assert(PlaybackMarkers.Label(SkipKind.Intro, true, false) == "Passer l’opening" && PlaybackMarkers.Label(SkipKind.Outro, false, false) == "Passer le générique" && PlaybackMarkers.Label(SkipKind.Outro, true, true) == "Épisode suivant", "Skip labels changed");
            return Task.CompletedTask;
        });
        await Test("Chapitres : le même classement que Mira web (tests/fixtures/chapters.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "chapters.json")));
            static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            foreach (var row in fixture.RootElement.GetProperty("titles").EnumerateArray())
            {
                var kind = PlaybackMarkers.Classify(Text(row[0]))?.ToString();
                Assert(kind == Text(row[1]), $"« {Text(row[0])} » : {kind ?? "rien"} au lieu de {Text(row[1]) ?? "rien"}");
            }
            foreach (var row in fixture.RootElement.GetProperty("positions").EnumerateArray())
            {
                var kind = PlaybackMarkers.Classify(Text(row[0]), row[1].GetDouble(), row[2].GetDouble())?.ToString();
                Assert(kind == Text(row[3]), $"« {Text(row[0])} » à {row[1]} s sur {row[2]} s : {kind ?? "rien"}");
            }
            foreach (var episode in fixture.RootElement.GetProperty("episodes").EnumerateArray())
            {
                var chapters = episode.GetProperty("chapters").EnumerateArray().Select(c => new ChapterMark(c[0].GetDouble(), Text(c[1]))).ToList();
                var found = PlaybackMarkers.FromChapters(chapters, episode.GetProperty("duration").GetDouble());
                var expected = episode.GetProperty("passages").EnumerateArray().Select(p => new SkipSegment(Enum.Parse<SkipKind>(Text(p[0])!), p[1].GetDouble(), p[2].GetDouble()));
                Assert(found.SequenceEqual(expected), "Passages : " + string.Join(", ", found));
            }
            return Task.CompletedTask;
        });
        await Test("Passer les intros et les récaps : une fois chacun, jamais les endings ni les aperçus", () =>
        {
            var segments = new List<SkipSegment> { new(SkipKind.Recap, 0, 45), new(SkipKind.Intro, 45, 135), new(SkipKind.Outro, 1290, 1380), new(SkipKind.Preview, 1380, 1410) };
            var skipped = new HashSet<SkipSegment>();
            Assert(PlaybackMarkers.AutoSkip(segments, 50, enabled: false, skipped) is null && !new PlayerSettings().AutoSkip, "Skipped without the setting, or on by default");
            Assert(PlaybackMarkers.AutoSkip(segments, 10, enabled: true, skipped) == segments[0] && PlaybackMarkers.AutoSkip(segments, 50, enabled: true, skipped) == segments[1], "Recap or opening not skipped");
            Assert(PlaybackMarkers.AutoSkip(segments, 1300, enabled: true, skipped) is null && PlaybackMarkers.AutoSkip(segments, 1390, enabled: true, skipped) is null, "Credits or preview skipped by themselves");
            Assert(PlaybackMarkers.AutoSkip(segments, 300, enabled: true, skipped) is null && PlaybackMarkers.AutoSkip(segments, 134.5, enabled: true, skipped) is null, "Skipped outside a passage, or in its last second");
            skipped.Add(segments[1]);
            Assert(PlaybackMarkers.AutoSkip(segments, 50, enabled: true, skipped) is null, "Going back into a skipped opening skips it again");
            // The opening of the chapters skipped, then Jellyfin's segments replace it: going back still plays it.
            List<SkipSegment> rebuilt = [new(SkipKind.Recap, 0, 45), new(SkipKind.Intro, 46.2, 134.1)];
            Assert(PlaybackMarkers.AutoSkip(rebuilt, 50, enabled: true, skipped) is null && PlaybackMarkers.AutoSkip(rebuilt, 10, enabled: true, skipped) == rebuilt[0], "A rebuilt opening is skipped again");
            return Task.CompletedTask;
        });
        await Test("Lecteur : « Épisode 3 / 12 », pistes nommées en français, vitesse et décalage", () =>
        {
            MediaItem Episode(string id, int season, int number, string name = "") => new() { Id = id, Type = "Episode", SeriesName = "Sword Art Online", Name = name, ParentIndexNumber = season, IndexNumber = number };
            var single = Enumerable.Range(1, 25).Select(n => Episode($"e{n}", 1, n, n == 1 ? "Le monde des épées" : $"Episode {n}")).ToList();
            Assert(PlayerText.Subtitle(single[0], single) == "Épisode 1 / 25 · Le monde des épées", "Episode line: " + PlayerText.Subtitle(single[0], single));
            Assert(PlayerText.Subtitle(single[2], single) == "Épisode 3 / 25", "A generic name is repeated: " + PlayerText.Subtitle(single[2], single));
            Assert(PlayerText.Subtitle(single[0]) == "Épisode 1 · Le monde des épées", "Before the episode list: " + PlayerText.Subtitle(single[0]));
            var seasons = single.Take(12).Concat(Enumerable.Range(1, 10).Select(n => Episode($"s2e{n}", 2, n))).Append(Episode("sp1", 0, 1, "OVA")).ToList();
            Assert(PlayerText.Subtitle(seasons[0], seasons) == "Saison 1 · Épisode 1 / 12 · Le monde des épées" && PlayerText.Subtitle(seasons[13], seasons) == "Saison 2 · Épisode 2 / 10"
                && PlayerText.Subtitle(seasons[^1], seasons) == "Hors-série · Épisode 1 / 1 · OVA", "Seasons: " + PlayerText.Subtitle(seasons[13], seasons));
            Assert(PlayerText.Subtitle(Episode("e30", 1, 30), single.Take(10).ToList()) == "Épisode 30", "A partial list claims a total");
            Assert(PlayerText.Subtitle(new MediaItem { Name = "Your Name", Type = "Movie", ProductionYear = 2016, RunTimeTicks = TimeSpan.FromMinutes(106).Ticks }) == "2016  ·  1 h 46", "Film line changed");
            Assert(PlayerText.Track("2", null, "jpn", "aac", 2) == ("Japonais", "AAC · stéréo") && PlayerText.Track("3", "Signs & Songs", "fre", "ass", forced: true) == ("Signs & Songs", "Français · ASS · forcés")
                && PlayerText.Track("4", "", "und", "") == ("Piste 4", "") && PlayerText.Track("5", "japanese", "pt-BR", "eac3", 6) == ("Japanese", "Portugais · Dolby Digital Plus · 5.1"), "Track names");
            Assert(PlayerText.Speed(.75) == "0,75×" && PlayerText.Speed(1) == "1×" && PlayerText.Delay(.5) == "+0,5 s" && PlayerText.Delay(-1.5) == "−1,5 s" && PlayerText.Delay(0) == "0,0 s", "Speed or offset formatting");
            return Task.CompletedTask;
        });
        await Test("Lecteur : pistes et chapitres lus de mpv de la même façon sur Windows et sur Mac", () =>
        {
            var tracks = MpvLists.Tracks("""
                [{"id":1,"type":"video","codec":"h264"},{"id":2,"type":"audio","lang":"fre","codec":"eac3","demux-channel-count":6,"title":null},
                 {"id":3,"type":"sub","lang":"eng","codec":"subrip","forced":true,"external":true},{"id":4,"type":"audio","title":"Commentaire"}]
                """);
            Assert(tracks.Select(x => x.Id).SequenceEqual(["2", "3", "4"]) && tracks.Select(x => x.Type).SequenceEqual(["audio", "sub", "audio"]), "Tracks: " + string.Join(", ", tracks));
            Assert(tracks[0] == new MpvTrack("2", "audio", PlayerText.Track("2", "", "fre", "eac3", 6, false, false).Label, PlayerText.Track("2", "", "fre", "eac3", 6, false, false).Details), "Audio track named otherwise: " + tracks[0]);
            Assert(tracks[1].Label == PlayerText.Track("3", "", "eng", "subrip", 0, true, true).Label, "Forced external subtitle: " + tracks[1]);
            Assert(MpvLists.Tracks(null).Count == 0 && MpvLists.Tracks("pas du JSON").Count == 0, "Unreadable track list");
            var chapters = MpvLists.Chapters("""[{"time":1300.5,"title":"Ending"},{"title":"sans heure"},{"time":0,"title":null},{"time":90,"title":7}]""");
            Assert(chapters.SequenceEqual([new ChapterMark(0, null), new ChapterMark(90, null), new ChapterMark(1300.5, "Ending")]), "Chapters: " + string.Join(", ", chapters));
            Assert(MpvLists.Chapters(null).Count == 0 && MpvLists.Chapters("{").Count == 0, "Unreadable chapter list");
            return Task.CompletedTask;
        });
        await Test("Lecteur : l’audio et les sous-titres choisis dans une série reviennent aux épisodes suivants (Windows et Mac)", () =>
        {
            // Episode 1: Japanese and the French dub; French subtitles « Panneaux » (forced) and « Complet ».
            const string first = """
                [{"id":1,"type":"video"},{"id":1,"type":"audio","lang":"jpn"},{"id":2,"type":"audio","lang":"fre","title":"VF"},
                 {"id":1,"type":"sub","lang":"fre","title":"Panneaux","forced":true},{"id":2,"type":"sub","lang":"fre","title":"Complet"},{"id":3,"type":"sub","lang":"eng"}]
                """;
            // Episode 2: another order, other codes for the same languages.
            const string second = """
                [{"id":1,"type":"audio","lang":"fra","title":"VF"},{"id":2,"type":"audio","lang":"ja"},
                 {"id":1,"type":"sub","lang":"fr","title":"Complet"},{"id":2,"type":"sub","lang":"fr","title":"Panneaux","forced":true}]
                """;
            var full = SeriesTracks.Remember(first, "1", "2");
            Assert(full == new TrackMemory("jpn", null, true, "fre", "Complet", false), "Memory: " + full);
            Assert(SeriesTracks.Choose(second, full!) == ("2", "1"), "« Complet » in Japanese not found again: " + SeriesTracks.Choose(second, full!));
            var signs = SeriesTracks.Remember(first, "1", "1")!;
            Assert(SeriesTracks.Choose(second, signs) == ("2", "2"), "« Panneaux » not found again: " + SeriesTracks.Choose(second, signs));
            var dub = SeriesTracks.Remember(first, "2", "no")!;
            Assert(!dub.Subtitles && SeriesTracks.Choose(second, dub) == ("1", "no"), "The French dub without subtitles: " + SeriesTracks.Choose(second, dub));
            // Names that change between releases: the same language, forced or not alike.
            const string renamed = """[{"id":1,"type":"audio","lang":"jpn"},{"id":1,"type":"sub","lang":"fre","title":"Signs","forced":true},{"id":2,"type":"sub","lang":"fre","title":"Full"}]""";
            Assert(SeriesTracks.Choose(renamed, full!) == ("1", "2") && SeriesTracks.Choose(renamed, signs) == ("1", "1"), "Renamed tracks: " + SeriesTracks.Choose(renamed, full!));
            // Nothing alike: mpv keeps its own choice (the languages of the settings).
            const string english = """[{"id":1,"type":"audio","lang":"eng"},{"id":1,"type":"sub","lang":"eng"}]""";
            Assert(SeriesTracks.Choose(english, full!) == (null, null) && SeriesTracks.Choose(null, full!) == (null, null), "A track chosen without its language");
            Assert(SeriesTracks.Remember(first, "no", "2") is null && SeriesTracks.Remember("{", "1", "2") is null, "Remembered without an audio track");

            // Kept per account, the 100 series chosen in last, beyond the 30 days of the cache.
            var store = new LibraryStore(run.Root, "series-tracks");
            store.RememberTracks("show", full!); store.RememberTracks("other", dub); store.RememberTracks("show", signs);
            Assert(store.TracksOf("show") == signs && store.TracksOf("other") == dub && store.TracksOf("unknown") is null, "Tracks of each series: " + store.TracksOf("show"));
            for (var i = 0; i < 99; i++) store.RememberTracks($"series-{i}", full!);
            Assert(store.TracksOf("other") is null && store.TracksOf("show") == signs && store.TracksOf("series-98") == full, "The oldest choice was not the one forgotten");
            store.Prune(DateTimeOffset.UtcNow.AddDays(31));
            Assert(store.TracksOf("show") == signs, "Forgotten with the cache");
            return Task.CompletedTask;
        });
        await Test("Passages : l’épisode suivant proposé dans les 20 dernières secondes sans générique, compte à rebours en temps de lecture", () =>
        {
            SkipSegment intro = new(SkipKind.Intro, 0, 90), credits = new(SkipKind.Outro, 1380, 1500);
            Assert(PlaybackMarkers.Offer([intro], 30, 1500, hasNext: true) == intro, "The active passage first");
            Assert(PlaybackMarkers.Offer([], 1485, 1500, hasNext: true) == new SkipSegment(SkipKind.Outro, 1480, 1500), "Next episode not offered at the end");
            Assert(PlaybackMarkers.Offer([], 1485, 1500, hasNext: false) is null && PlaybackMarkers.Offer([], 1479, 1500, hasNext: true) is null
                && PlaybackMarkers.Offer([], 95, 100, hasNext: true) is null && PlaybackMarkers.Offer([], 0, 10, hasNext: true) is null, "Offered without a next episode, too early, or on a short clip");
            Assert(PlaybackMarkers.Offer([credits], 1400, 1500, hasNext: true) == credits, "The credits marker was not offered");
            Assert(PlaybackMarkers.CountdownEnd(1400, credits) == 1410 && PlaybackMarkers.CountdownEnd(1370, credits) == 1390, "Countdown counted from the wrong time");
            return Task.CompletedTask;
        });
        await Test("Passages : l’offre, le compte à rebours et les intros sautées, communs à Windows et Mac", () =>
        {
            var skips = new SkipPlanner { Next = new MediaItem { Id = "next", Type = "Episode" } };
            skips.ServerSegments = [new(SkipKind.Intro, 45, 135)];
            skips.ServerChapters = [new(0, "Récap"), new(45, "Opening"), new(135, "Partie A"), new(1290, "Ending"), new(1380, "Preview")];
            skips.Rebuild(1410);
            Assert(skips.Segments.Select(s => s.Kind).SequenceEqual([SkipKind.Recap, SkipKind.Intro, SkipKind.Outro, SkipKind.Preview]), "Passages: " + string.Join(", ", skips.Segments));
            skips.FileChapters = [new(0, "Opening"), new(90, "Partie A")];
            Assert(skips.Chapters == skips.FileChapters, "The file's chapters come first");
            skips.Rebuild(1410);
            Assert(skips.Segments.Count == 1 && skips.Segments[0] == new SkipSegment(SkipKind.Intro, 45, 135), "A chapter guess over a server segment: " + string.Join(", ", skips.Segments));
            Assert(skips.AutoSkip(50, enabled: false) is null && skips.AutoSkip(50, enabled: true) == skips.Segments[0] && skips.AutoSkip(50, enabled: true) is null, "An opening is jumped over once, and only with the setting");
            Assert(skips.Offer(50, 1410, offering: true, autoNext: true) && skips.Label(episode: true) == "Passer l’opening" && !skips.Counting, "The opening is offered without a countdown");
            Assert(!skips.Offer(60, 1410, offering: true, autoNext: true), "The same passage is not offered again");
            Assert(skips.Offer(60, 1410, offering: false, autoNext: true) && skips.Active is null && skips.Label(episode: true) == "", "Nothing offered in the mini player");
            skips.Segments = [new(SkipKind.Outro, 1290, 1380), new(SkipKind.Preview, 1380, 1410)];
            Assert(skips.Offer(1300, 1410, offering: true, autoNext: true) && skips.ToNext && skips.Label(episode: true) == "Épisode suivant" && skips.CountdownEnd == 1310, "Credits then a preview lead to the next episode, with a countdown");
            Assert(skips.CountdownText(1302.5) == "Épisode suivant dans 8 s" && skips.Due(1309.5) is null && skips.Due(1310) == skips.Next && skips.CountdownEnd is null, "Countdown text or end");
            skips.Active = null; skips.Offer(1300, 1410, offering: true, autoNext: true);
            Assert(skips.Cancel() == "Épisode suivant" && skips.Stay && !skips.Counting, "Cancelling stops the countdown");
            skips.Active = null; skips.Offer(1300, 1410, offering: true, autoNext: true);
            Assert(!skips.Counting && skips.CountdownText(1300) is null, "No countdown once the viewer stays on the episode");
            skips.Reset();
            Assert(skips.Next is null && !skips.Stay && skips.Segments.Count == 0 && skips.AutoSkipped.Count == 0 && skips.Active is null, "A new title starts clean");
            return Task.CompletedTask;
        });
        await Test("mpv : fin de fichier, échec, et fin sans sortie son sur Mac", () =>
        {
            Assert(MpvClient.Ending(0, 0, false) == (true, false) && MpvClient.Ending(2, 0, false) == (false, false), "End of file, or stopped");
            Assert(MpvClient.Ending(4, -13, true) == (false, true) && MpvClient.Ending(4, -14, false) == (false, true), "An error is a failure");
            Assert(MpvClient.Ending(4, -14, true) == (true, false), "Without a sound output, the picture played to its end (Mac)");
            return Task.CompletedTask;
        });
        await Test("Lecture : départ, saut en attente, rapport à Jellyfin, fin et titre vu partout", () =>
        {
            var hour = TimeSpan.FromHours(1).Ticks;
            MediaItem Film(long position, bool played = false) => new() { Id = "a", RunTimeTicks = hour, UserData = new() { PlaybackPositionTicks = position, Played = played } };
            Assert(PlaybackSession.StartAt(Film(TimeSpan.FromMinutes(20).Ticks), remember: true) == 1200, "Resumes where it was left");
            Assert(PlaybackSession.StartAt(Film(TimeSpan.FromMinutes(20).Ticks), remember: false) == 0, "Positions not remembered");
            Assert(PlaybackSession.StartAt(Film(TimeSpan.FromMinutes(56).Ticks), remember: true) == 0 && PlaybackSession.StartAt(Film(hour / 2, played: true), remember: true) == 0,
                "Nearly finished or watched: starts over");
            var session = new PlaybackSession { Reported = true, Failure = "old" };
            session.Begin(Film(0), new MediaSource { Id = "s" }, "p", 30);
            Assert(session.Position == 30 && session.Duration == 3600 && !session.Reported && session.Failure is null, "A new playback starts clean, its length from Jellyfin");
            var now = DateTimeOffset.UtcNow; session.LastReport = now;
            session.Hold(600, now);
            Assert(session.Position == 600 && session.LastReport == DateTimeOffset.MinValue, "A seek moves at once and asks for a report");
            Assert(session.Settled(31, now + TimeSpan.FromMilliseconds(300)) == 600, "mpv's older position is not believed just after a seek");
            Assert(session.Settled(600.8, now + TimeSpan.FromMilliseconds(300)) == 600.8, "Caught up");
            Assert(session.Settled(31, now + PlaybackSession.SeekHold) == 31, "After the hold, mpv is right again");
            session.Paused = true;
            var report = session.Report(150, muted: true);
            Assert(report is { ItemId: "a", MediaSourceId: "s", PlaySessionId: "p", IsPaused: true, IsMuted: true, VolumeLevel: 100 } && report.PositionTicks == TimeSpan.FromSeconds(600).Ticks,
                "Report: item, source, session, position, pause, Jellyfin's volume scale");
            Assert(!session.Completes(loaded: true, ended: false) && session.Completes(loaded: true, ended: true) && !session.Completes(loaded: false, ended: true), "Stopped midway, ended, never loaded");
            session.Position = 3300;
            Assert(session.Completes(loaded: true, ended: false), "Stopped past 90 %: watched");
            var resume = new List<MediaItem> { Film(500), new() { Id = "b" } }; var next = new List<MediaItem> { Film(500) };
            var finished = Film(3300);
            PlaybackSession.MarkWatched(finished, resume.Concat(next).ToList(), resume, next);
            Assert(finished.UserData is { Played: true, PlaybackPositionTicks: 0 } && resume.Count == 1 && resume[0].Id == "b" && next.Count == 0, "Watched in every list, out of « Continuer à regarder » and « À suivre »");
            return Task.CompletedTask;
        });
        await Test("Aperçus de la barre : images trickplay de Jellyfin, case du moment visé, tuiles chargées une fois et peu gardées", async () =>
        {
            // As Jellyfin 12.1 answers for a film whose library extracts trickplay images.
            const string answer = """{"Id":"film","Chapters":[{"StartPositionTicks":0,"Name":"Début"}],"Trickplay":{"other":{"320":{"Width":320,"Height":180,"TileWidth":10,"TileHeight":10,"ThumbnailCount":4,"Interval":10000}},"src":{"160":{"Width":160,"Height":90,"TileWidth":10,"TileHeight":10,"ThumbnailCount":250,"Interval":10000},"320":{"Width":320,"Height":180,"TileWidth":10,"TileHeight":10,"ThumbnailCount":1000,"Interval":10000},"0":{"Width":0}}}}""";
            var fetched = new List<string>();
            using var client = new JellyfinClient(new Connection("http://nas:8096/", "user-1", "Alice", "token", "device-1"), new Handler(request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                if (path == "/Items/film?userId=user-1") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) });
                fetched.Add(path);
                return Task.FromResult(path.Contains("/9.jpg") ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([(byte)path[^"?mediaSourceId=src".Length..][0]]) });
            }));
            var timeline = await client.TimelineAsync("film", "src");
            var set = timeline.Trickplay!;
            Assert(timeline.Chapters.Count == 1 && set.SourceId == "src" && set.Info.Width == 320 && set.TilePixelWidth == 3200, "The file played, the size above 240: " + set);
            Assert(set.TilePath(2) == "Videos/film/Trickplay/320/2.jpg?mediaSourceId=src", set.TilePath(2));
            Assert((await client.TimelineAsync("film")).Trickplay?.SourceId == "other" && TrickplaySet.Pick("x", null, null) is null && TrickplaySet.Pick("x", new Dictionary<string, Dictionary<string, TrickplayInfo>> { ["s"] = new() { ["0"] = new() } }, "s") is null, "First file without the one played; none without pictures");
            Assert(set.Cell(0) == new TrickplayCell(0, 0, 0, 320, 180) && set.Cell(125) == new TrickplayCell(0, 2 * 320, 1 * 180, 320, 180), "Cells: " + set.Cell(125));
            Assert(set.Cell(1005).Tile == 1 && set.Cell(1005) is { X: 0, Y: 0 } && set.Cell(99_999).Tile == 9 && set.Cell(99_999) is { X: 9 * 320, Y: 9 * 180 } && set.Cell(-3).Tile == 0 && set.Cell(double.NaN).Tile == 0, "Next tiles, the last thumbnail at most");

            var decoded = 0; var loaded = 0;
            using var tiles = new TrickplayTiles<byte[]>(set, (tile, ct) => client.TrickplayTileAsync(set, tile, ct), bytes => { Interlocked.Increment(ref decoded); return bytes; }, keep: 2);
            tiles.Loaded += () => Interlocked.Increment(ref loaded);
            async Task<(byte[] Picture, TrickplayCell Cell)?> Wait(double seconds) { for (var i = 0; i < 100; i++) { if (tiles.At(seconds) is { } hit) return hit; await Task.Delay(10); } return null; }
            Assert(tiles.At(5) is null && tiles.At(6) is null, "Asked for, not there yet");
            Assert(await Wait(5) is { } first && first.Cell.Tile == 0 && fetched.Count(x => x.Contains("/0.jpg")) == 1, "One request for a tile pointed at twice");
            await Wait(1005); await Wait(2005);
            Assert(await Wait(5) is not null && fetched.Count(x => x.Contains("/0.jpg")) == 2 && decoded == 4 && loaded == 4, "Two tiles kept: the oldest fetched again: " + string.Join(",", fetched));
            Assert(await Wait(9 * 1000 + 5) is null && fetched.Count(x => x.Contains("/9.jpg")) == 1, "A missing tile is asked for once: " + string.Join(",", fetched));
        });
    }
}
