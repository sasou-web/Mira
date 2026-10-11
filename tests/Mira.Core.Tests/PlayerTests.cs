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
    }
}
