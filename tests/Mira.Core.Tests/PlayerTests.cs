using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            // The rail's group button: its count and what it says.
            foreach (var row in root.GetProperty("indicators").EnumerateArray())
                Assert(Together.ParseGroup(row[0]) is { } group && Together.CountText(group) == row[2].GetString() && Together.IndicatorText(group, row[1].GetString()) == row[3].GetString(),
                    $"Indicator: {row[3]} ≠ {(Together.ParseGroup(row[0]) is { } g ? Together.CountText(g) + " " + Together.IndicatorText(g, row[1].GetString()) : null)}");
            Assert(Together.MemberText("Marc", true) == "Marc a rejoint le groupe" && Together.MemberText("Marc", false) == "Marc a quitté le groupe", "Member");
            // Lecture in a group: its label, and the question before it replaces the title the group has on.
            foreach (var row in root.GetProperty("replaces").EnumerateArray())
            {
                var queue = row[1].ValueKind == JsonValueKind.Null ? null : new Together.Entry(row[1].GetProperty("itemId").GetString()!, row[1].GetProperty("playlistItemId").GetString()!, row[1].GetProperty("startTicks").GetInt64());
                var group = row[0].ValueKind == JsonValueKind.Null ? null : Together.ParseGroup(row[0]);
                Assert(Together.AsksToReplace(group, queue, row[2].GetString()!) == row[3].GetBoolean(), $"Replace: {group?.State} {queue?.ItemId} → {row[2]}");
            }
            foreach (var row in root.GetProperty("titles").EnumerateArray())
                Assert(Together.TitleName(JsonSerializer.Deserialize<MediaItem>(row[0].GetRawText())!) == row[1].GetString(), $"Title: {row[1]}");
            foreach (var row in root.GetProperty("playLabels").EnumerateArray())
            {
                var label = Together.GroupPlayLabel(row[0].GetProperty("code").GetString()!, row[0].GetProperty("resumeAt").GetDouble(), row[0].GetProperty("groupTitle").GetBoolean());
                Assert(label == row[1].GetString(), $"Label: {label}");
            }
            foreach (var row in root.GetProperty("questions").EnumerateArray())
                Assert(Together.ReplaceQuestion(row[0].GetString()) == row[1].GetString(), $"Question: {row[1]}");
            Assert(Together.GroupName("mira") == "Groupe de mira" && Together.NeedsSeek(10, 10.6) && !Together.NeedsSeek(10, 10.4), "Words, drift");
            // Home's line: the first group open on Jellyfin, none while this device is in one.
            List<Together.Group> open = [new("a", "Groupe de Marc", ["Marc"], "Playing"), new("b", "Groupe de Léa", [], "Idle")];
            Assert(Together.HomeGroup(open, null)?.GroupId == "a" && Together.HomeGroup(open, "b") is null && Together.HomeGroup([], null) is null, "Home group");
            // Without a party link (no Mira extension on Jellyfin): the group's mira:// link, read back as the same group.
            foreach (var row in root.GetProperty("invites").EnumerateArray())
            {
                var link = Together.AppLink(row[0].GetString()!, row[1].GetString()!, row[2].GetString());
                Assert(link == row[3].GetString() && Together.ParseAppLink(link)?.GroupId == row[1].GetString()
                    && Together.InviteSteps(row[0].GetString()!).SequenceEqual(row[4].EnumerateArray().Select(x => x.GetString())), $"Invite: {link} · {string.Join(" | ", Together.InviteSteps(row[0].GetString()!))}");
                Assert(!link.Contains("/Mira/"), "Never Mira web's page, gone with it");
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
            // A party link (its page's « Open in Mira »): the code and its Jellyfin, never a group yet.
            var partyLink = Mira.Jellyfin.PartyPage.AppLink("https://jellyfin-pc.tail1234.ts.net", "AbCdEfGhIjKlMnOpQr_-12", "0123456789abcdef0123456789abcdef");
            Assert(Together.ParseAppLink(partyLink) == new Together.Invitation("", "https://jellyfin-pc.tail1234.ts.net", null, "AbCdEfGhIjKlMnOpQr_-12"), $"Party link: {partyLink}");
            Assert(Together.ParseAppLink("mira://party/AbCdEfGhIjKlMnOpQr_-12?server=http%3A%2F%2Fpc.tail1234.ts.net")?.Server == "https://pc.tail1234.ts.net"
                && Together.ParseAppLink("mira://party/AbCdEfGhIjKlMnOpQr_-12?server=http%3A%2F%2Fpc.tail1234.ts.net%3A8096")?.Server == "http://pc.tail1234.ts.net:8096", "A Funnel name in http on port 80: https; its Jellyfin port stays");
            Assert(Together.ParseAppLink("mira://party/short?server=http%3A%2F%2F192.168.1.2%3A8096") is null, "Party code too short");
            Assert(Together.ParseAppLink("mira://party/AbCdEfGhIjKlMnOpQr_-12?server=file%3A%2F%2F%2Fetc") is null, "Party on no Jellyfin");
            var partyGroups = new List<Together.Group> { new("74644121-30f5-4334-a8a1-4638f0060cd6", "Groupe de ogsas", ["ogsas"], "Idle") };
            Assert(Together.Answer(Together.PartyInvitation("http://192.168.1.2:8096", "ogsas"), partyGroups, "http://192.168.1.2:8096", null) is { Kind: Together.InviteKind.Join, Group.GroupId: "74644121-30f5-4334-a8a1-4638f0060cd6" }, "Party: the host's group");
            Assert(Together.Answer(Together.PartyInvitation("http://192.168.1.2:8096", "ogsas"), [], "http://192.168.1.2:8096", null).Kind == Together.InviteKind.Waiting, "Party: waits for the host's group");
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
        await Test("Toujours là : la question après 3 épisodes de suite sans rien toucher, allumée par défaut, comme l’iPhone (tests/fixtures/still-watching.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "still-watching.json")));
            Assert(fixture.RootElement.GetProperty("episodes").GetInt32() == StillWatching.Episodes && new PlayerSettings().StillWatching, "3 episodes, on by default");
            var start = new DateTimeOffset(2026, 11, 6, 23, 0, 0, TimeSpan.FromHours(1));
            foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                var watch = new StillWatching();
                foreach (var played in row[0].EnumerateArray()) watch.Started(start.AddSeconds(played[0].GetDouble()), played[1].GetBoolean());
                var input = row[1].ValueKind == JsonValueKind.Null ? DateTimeOffset.MinValue : start.AddSeconds(row[1].GetDouble());
                var asks = watch.Asks(input, row[2].GetBoolean());
                Assert(asks == row[3].GetBoolean(), $"{row}: {asks}");
            }
            var pad = new StillWatching();
            pad.Started(start, false); pad.Started(start.AddMinutes(25), true); pad.Started(start.AddMinutes(50), true);
            Assert(pad.Asks(DateTimeOffset.MinValue, true), "Three untouched");
            pad.Touched(start.AddMinutes(60));
            Assert(!pad.Asks(DateTimeOffset.MinValue, true), "A controller press Mira read counts as someone there");
            pad.Clear(); Assert(!pad.Asks(DateTimeOffset.MinValue, true) && pad.Untouched(DateTimeOffset.MinValue) == 0, "Cleared with the player");
            Assert(StillWatching.Reason("S01 E04").EndsWith("S01 E04", StringComparison.Ordinal) && !StillWatching.Reason(null).Contains("suivre", StringComparison.Ordinal), StillWatching.Reason(null));
            return Task.CompletedTask;
        });
        await Test("Spoilers : un épisode pas commencé sans son image ni son résumé, réglage éteint par défaut, comme l’iPhone (tests/fixtures/spoilers.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "spoilers.json")));
            Assert(!new PlayerSettings().HideSpoilers, "Off by default");
            foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                var item = new MediaItem { Id = "e", Type = row[0].GetString()!, UserData = new() { Played = row[1].GetBoolean(), PlaybackPositionTicks = row[2].GetInt64() } };
                Assert(Spoilers.Hides(item, row[3].GetBoolean()) == row[4].GetBoolean(), $"{row}");
            }
            // A wide card of an episode not started: its season's art or the series', never a still of its own.
            var next = new MediaItem
            {
                Id = "e", Type = "Episode", SeriesId = "series", SeasonId = "season", ImageTags = new() { ["Primary"] = "still", ["Thumb"] = "thumb" },
                ParentBackdropItemId = "series", ParentBackdropImageTags = ["backdrop"], ImageBlurHashes = new() { ["Primary"] = new() { ["still"] = "LEHV6nWB2yk8pyo0adR*.7kCMdnj" } },
            };
            var started = next with { UserData = new() { PlaybackPositionTicks = 1 } };
            try
            {
                Spoilers.On = true;
                Assert(Artwork.Landscape(next) == new ImageRef("series", "Backdrop", "backdrop") && Artwork.Landscape(started) == new ImageRef("e", "Thumb", "thumb"), "Wide card with spoilers hidden");
                Assert(Spoilers.Still(next) == new ImageRef("e", "Primary", "still") && BlurHash.Of(next, Spoilers.Still(next)) is not null, "The still's BlurHash stands in for it");
                // A series without art of its own (Jellyfin found no metadata for it: its episodes keep the frame it took
                // from each video, as « Bord Test » on Jellyfin 12.1): never the still either, on a wide card nor on a
                // banner (Home's, « À suivre » on top of Séries); no picture rather than the still.
                var bare = next with { ParentBackdropItemId = null, ParentBackdropImageTags = [] };
                var bareThumb = bare with { ImageTags = new() { ["Thumb"] = "thumb" } };
                Assert(Artwork.Landscape(bare) == Artwork.None && Artwork.Landscape(bareThumb) == Artwork.None && Artwork.Backdrop(bare) == Artwork.None
                    && Artwork.Backdrop(bareThumb with { BackdropImageTags = ["own"] }) == Artwork.None && BlurHash.Of(bare, Artwork.Landscape(bare)) is null,
                    $"Series without art: {Artwork.Landscape(bare)} | {Artwork.Landscape(bareThumb)} | {Artwork.Backdrop(bare)}");
                Assert(Artwork.Backdrop(next) == new ImageRef("series", "Backdrop", "backdrop") && Artwork.Backdrop(next with { BackdropImageTags = ["own"] }) == new ImageRef("series", "Backdrop", "backdrop"),
                    "Banner: the series' backdrop");
                // Its own title page, opened on purpose, shows everything; started, it shows as before.
                Assert(Artwork.Backdrop(bare, ownPage: true) == new ImageRef("e", "Thumb", "thumb") && Artwork.Backdrop(started with { ParentBackdropItemId = null, ParentBackdropImageTags = [] }) == new ImageRef("e", "Thumb", "thumb"),
                    "Title page, started");
                // A new episode's notice: the series' backdrop, else nothing; its still when spoilers are shown.
                Assert(NewEpisodes.Picture(next) == new ImageRef("series", "Backdrop", "backdrop") && NewEpisodes.Picture(bare) is null, $"Notice: {NewEpisodes.Picture(next)} | {NewEpisodes.Picture(bare)}");
            }
            finally { Spoilers.On = false; }
            Assert(Artwork.Landscape(next) == new ImageRef("e", "Thumb", "thumb") && Artwork.Backdrop(next with { ParentBackdropItemId = null }) == new ImageRef("e", "Thumb", "thumb")
                && NewEpisodes.Picture(next) == new ImageRef("e", "Primary", "still") && NewEpisodes.Picture(next with { ImageTags = [] }) is null, "Off: as before");
            return Task.CompletedTask;
        });
        await Test("Touches média du Mac : « Now Playing » redit seulement ce qui change, touches comme sous Windows", () =>
        {
            var at = new DateTimeOffset(2026, 11, 7, 21, 0, 0, TimeSpan.FromHours(1));
            var clock = new NowPlaying();
            var playing = new NowPlayingState("Arcane", "S01 E02", true, false, true, 100, 2400, 1);
            Assert(clock.Due(playing, at), "The first state is told");
            clock.Tell(playing, at);
            Assert(!clock.Due(playing with { Position = 110 }, at.AddSeconds(10)), "Playing on: the system moves the bar itself");
            Assert(!clock.Due(playing with { Position = 111 }, at.AddSeconds(10)), "A second off: still the system's");
            Assert(clock.Due(playing with { Position = 170 }, at.AddSeconds(10)), "A seek is told");
            Assert(clock.Due(playing with { Position = 110, Paused = true }, at.AddSeconds(10)), "A pause is told");
            Assert(clock.Due(playing with { Position = 120, Rate = 2 }, at.AddSeconds(10)), "Another speed is told");
            Assert(clock.Due(playing with { Position = 110, CanNext = false }, at.AddSeconds(10)) && clock.Due(playing with { Title = "Dune" }, at), "The next episode gone, another title: told");
            Assert(!clock.Due(playing with { Position = 110, Duration = 2400.4 }, at.AddSeconds(10)), "A length read again a little longer: not told");
            clock.Tell(playing with { Paused = true }, at);
            Assert(!clock.Due(playing with { Paused = true }, at.AddMinutes(5)) && clock.Due(playing with { Paused = true, Position = 103 }, at.AddMinutes(5)), "Paused, the bar stands still");
            var tidy = new NowPlayingState("x", "", true, false, false, double.NaN, double.PositiveInfinity, double.NaN).Tidy();
            Assert(tidy.Position == 0 && tidy.Duration == 0 && tidy.Rate == 1, $"Tidy: {tidy}");
            Assert((playing with { Position = 3000 }).Tidy().Position == 2400 && (playing with { Position = -4 }).Tidy().Position == 0, "Within the title");
            Assert(playing.Moving == 1 && (playing with { Paused = true }).Moving == 0 && (playing with { Ready = false }).Moving == 0, "Moving only while it plays");
            clock.Clear();
            Assert(clock.Told is null && clock.Due(playing, at), "Cleared: told from the start");
            foreach (var key in Enum.GetValues<MediaKey>()) Assert(!NowPlaying.Enabled(key, null) && !NowPlaying.Enabled(key, playing with { Ready = false }), $"{key}: nothing before the file is open");
            Assert(NowPlaying.Enabled(MediaKey.TogglePause, playing) && NowPlaying.Enabled(MediaKey.Seek, playing) && NowPlaying.Enabled(MediaKey.Next, playing), "Everything on an episode with a next one");
            var film = playing with { CanNext = false, Duration = 0 };
            Assert(!NowPlaying.Enabled(MediaKey.Next, film) && !NowPlaying.Enabled(MediaKey.Back, film) && !NowPlaying.Enabled(MediaKey.Seek, film) && NowPlaying.Enabled(MediaKey.Stop, film), "No next one, no length: no next track, no jumps");
            Assert(NowPlaying.Changes(MediaKey.Play, paused: true) && !NowPlaying.Changes(MediaKey.Play, paused: false) && NowPlaying.Changes(MediaKey.Pause, paused: false) && !NowPlaying.Changes(MediaKey.Pause, paused: true)
                && NowPlaying.Changes(MediaKey.TogglePause, paused: true) && NowPlaying.Changes(MediaKey.TogglePause, paused: false) && !NowPlaying.Changes(MediaKey.Seek, paused: true), "Play and Pause only when they change something");
            return Task.CompletedTask;
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
        await Test("Reprise un peu avant l’arrêt, sauf sous 30 s de visionnage, comme Mira web (tests/fixtures/resume.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "resume.json")));
            Assert(string.Join(",", PlaybackSession.ResumeBackChoices) == string.Join(",", fixture.RootElement.GetProperty("choices").EnumerateArray().Select(x => x.GetInt32())), "The same choices");
            Assert(PlaybackSession.ResumeBackFrom == fixture.RootElement.GetProperty("from").GetDouble() && new PlayerSettings().ResumeBack == 5, "From 30 s watched, 5 s by default");
            foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                var at = PlaybackSession.ResumeBack(row[0].GetDouble(), row[1].GetInt32());
                Assert(Math.Abs(at - row[2].GetDouble()) < 1e-9, $"{row[0]} s, {row[1]} s back: {at}");
            }
            var hour = TimeSpan.FromHours(1).Ticks;
            MediaItem Film(long position, bool played = false) => new() { Id = "a", RunTimeTicks = hour, UserData = new() { PlaybackPositionTicks = position, Played = played } };
            Assert(PlaybackSession.StartAt(Film(TimeSpan.FromMinutes(20).Ticks), remember: true, back: 5) == 1195, "Resumes 5 s before");
            Assert(PlaybackSession.StartAt(Film(TimeSpan.FromMinutes(20).Ticks), remember: false, back: 5) == 0, "Not remembered: from the start");
            Assert(PlaybackSession.StartAt(Film(TimeSpan.FromMinutes(56).Ticks), remember: true, back: 5) == 0, "Nearly finished: starts over");
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
        await Test("Son de nuit : un compresseur en tête de la chaîne, gardé, jamais avec le son envoyé à l’ampli (Windows et Mac)", () =>
        {
            Assert(!new PlayerSettings().NightSound, "Off by default");
            Assert(NightSound.Filter == "@mira-night:lavfi=[acompressor=threshold=0.0625:ratio=4:attack=10:release=300:makeup=2.5:knee=4,alimiter=limit=0.95:level=disabled]",
                "A compressor without look-ahead (no delay), +8 dB for what stays quiet, a limiter without gain of its own, under its own label");
            Assert(NightSound.Filter.StartsWith('@' + NightSound.Label + ':') && NightSound.Label != VolumeBoost.Label, "Its own label: the boost and it come and go apart");
            Assert(NightSound.Applies(true, AudioOutput.Off) && NightSound.Applies(true, null), "Chosen, sound decoded by Mira: on");
            Assert(!NightSound.Applies(true, AudioOutput.Optical) && !NightSound.Applies(true, AudioOutput.Hdmi) && !NightSound.Applies(false, AudioOutput.Off), "Never with passthrough, never unchosen");
            Assert(NightSound.Details(true, AudioOutput.Hdmi, refused: false) == "Pas avec le son envoyé à l’ampli", "Says why it does nothing with passthrough");
            Assert(NightSound.Details(true, AudioOutput.Off, refused: true) == "Ce lecteur ne sait pas le faire.", "Says when mpv refused it");
            Assert(NightSound.Details(false, AudioOutput.Off, refused: false) == "Explosions plus douces, voix plus claires", "Otherwise, what it does");
            var kept = System.Text.Json.JsonSerializer.Deserialize<PlayerSettings>(System.Text.Json.JsonSerializer.Serialize(new PlayerSettings { NightSound = true }));
            Assert(kept?.NightSound == true, "Kept with the settings");
            return Task.CompletedTask;
        });
        await Test("Sous-titres : taille, fond sombre, couleur, gras et hauteur donnés à mpv, ses défauts sinon (Windows et Mac)", () =>
        {
            var plain = SubtitleStyle.Options(new PlayerSettings());
            Assert(plain.Count(x => x.Value is not null) == 1 && plain[0] == ("sub-font-size", "40"), "Default style: " + string.Join(", ", plain));
            var options = SubtitleStyle.Options(new PlayerSettings { SubtitleSize = 52, SubtitleBackground = true, SubtitleColor = SubtitleStyle.Yellow, SubtitleBold = true, SubtitleHeight = 88 });
            var styled = options.ToDictionary(x => x.Name, x => x.Value);
            Assert(styled["sub-font-size"] == "52" && styled["sub-color"] == "#FFE14D" && styled["sub-bold"] == "yes" && styled["sub-pos"] == "88", "Text: " + string.Join(", ", options));
            Assert(styled["sub-border-style"] == "background-box" && styled["sub-back-color"] == SubtitleStyle.Box && styled["sub-shadow-offset"] == "4" && styled["sub-outline-size"] == "0", "Box: " + string.Join(", ", options));
            // The same options either way: turned off, what Mira set goes back to mpv's default. ASS keep their own styles.
            Assert(plain.Select(x => x.Name).SequenceEqual(options.Select(x => x.Name)) && options.All(x => !x.Name.StartsWith("sub-ass", StringComparison.Ordinal)), "Options: " + string.Join(", ", options));
            Assert(SubtitleStyle.Height(150) == 100 && SubtitleStyle.Height(95) == 94 && SubtitleStyle.Height(0) == 82 && SubtitleStyle.Heights[0] == (100, "En bas"), "Heights");
            Assert(SubtitleStyle.Color("White") == "#FFFFFF" && SubtitleStyle.Color("Rouge") == "#FFFFFF", "Colour");
            return Task.CompletedTask;
        });
        await Test("Image dans l’image : ouverte en bas à droite de l’écran de Mira ou là où elle était, à la forme de la vidéo, à l’échelle de son écran (Windows et Mac)", () =>
        {
            ScreenFit.Screen Screen(double left, double top, double width, double height, double scale = 1) => new(new(left, top, width, height), scale);
            // A 1080p screen at 100 % (taskbar below), a smaller one on its right, and one at 150 % further right.
            var primary = Screen(0, 0, 1920, 1032); var small = Screen(1920, 0, 1366, 728); var scaled = Screen(3286, 0, 2560, 1392, 1.5);
            const double Wide = 16d / 9;
            Assert(PictureInPicture.Open([primary], null, (960, 500), Wide) is { Left: 1416, Top: 738, Width: 480, Height: 270 }, "First time: 480 × 270 at the bottom right, 24 px from the edges");
            Assert(PictureInPicture.Open([primary, small], null, (2500, 300), Wide) is { Left: 2782, Top: 434, Width: 480, Height: 270 }, "On the screen showing Mira's middle");
            Assert(PictureInPicture.Open([primary, small, scaled], null, (4000, 500), Wide) is { Left: 5090, Top: 951, Width: 720, Height: 405 }, "At 150 %: 480 device-independent pixels, 720 device pixels");
            Assert(PictureInPicture.Open([primary, small], null, (-5000, 0), Wide) is { Left: 1416, Top: 738 }, "Mira's middle on no screen: the first one");
            // Where it was left: kept while most of it is on a screen present, moved inside it; otherwise the first time's place.
            Assert(PictureInPicture.Open([primary], new(100, 100, 640), (960, 500), Wide) is { Left: 100, Top: 100, Width: 640, Height: 360 }, "Back where it was, as wide");
            Assert(PictureInPicture.Open([primary, small, scaled], new(3400, 100, 320), null, Wide) is { Left: 3400, Top: 100, Width: 480, Height: 270 }, "Its width at the scale of the screen it is on");
            Assert(PictureInPicture.Open([primary, small, scaled], new(5600, 100, 320), null, Wide) is { Left: 5366, Top: 100 }, "Mostly on its screen: moved inside it");
            Assert(PictureInPicture.Open([primary], new(-2000, 100, 480), null, Wide) is { Left: 1416, Top: 738 }, "Its screen unplugged: at the bottom right of one present");
            Assert(PictureInPicture.Open([primary], new(double.NaN, 100, 480), null, Wide) is { Left: 1416 } && PictureInPicture.Open([primary], new(100, 100, 0), null, Wide) is { Left: 1416 }, "A broken saved place is ignored");
            Assert(PictureInPicture.Open([], null, null, Wide) is null && PictureInPicture.Open([Screen(0, 0, 0, 0), Screen(0, 0, 1920, 1032, double.NaN)], null, null, Wide) is null, "No usable screen");
            // A tall video stays within 60 % of the screen's height; the narrowest keeps 144 on its shorter side.
            Assert(PictureInPicture.Open([primary], null, null, 9d / 16) is { Width: 348, Height: 619 }, "A portrait video, narrower");
            Assert(PictureInPicture.Widths(primary, Wide) is (256, > 1100 and < 1101) && PictureInPicture.Widths(primary, .5) is (144, > 309 and < 310), "Narrowest and widest");
            Assert(PictureInPicture.Aspect(0, 0) == Wide && PictureInPicture.Aspect(1920, 800) == 2.4 && PictureInPicture.Aspect(4000, 1000) == 2.4 && PictureInPicture.Aspect(100, 1000) == .5
                && PictureInPicture.Aspect(double.NaN, 5) == Wide && PictureInPicture.Aspect(720, 576) == 1.25, "The video's shape, 16:9 while unknown");
            // Where it was left, for the next time: its corner in device pixels, its width in device-independent pixels.
            Assert(PictureInPicture.Remember(new(5090, 951, 720, 405), scaled) == new PictureInPicture.Spot(5090, 951, 480), "Remembered at 150 %");
            return Task.CompletedTask;
        });
        await Test("Image dans l’image : agrandie ou réduite par un bord ou un coin à la forme de la vidéo, entre ses limites, et toujours sur un écran", () =>
        {
            var primary = new ScreenFit.Screen(new(0, 0, 1920, 1032), 1); var small = new ScreenFit.Screen(new(1920, 0, 1366, 728), 1);
            const double Wide = 16d / 9;
            var start = new ScreenFit.Pixels(1416, 738, 480, 270);
            ScreenFit.Pixels Pull(PictureInPicture.Edge edge, double dx, double dy) => PictureInPicture.Resize(start, edge, dx, dy, Wide, primary);
            Assert(Pull(PictureInPicture.Edge.Right, 160, 40) == new ScreenFit.Pixels(1416, 738, 640, 360), "The right edge: wider, the top left corner kept");
            Assert(Pull(PictureInPicture.Edge.Left, -160, 0) == new ScreenFit.Pixels(1256, 738, 640, 360), "The left edge: its right side kept");
            Assert(Pull(PictureInPicture.Edge.Top, 0, -90) == new ScreenFit.Pixels(1416, 648, 640, 360), "The top edge: its bottom kept");
            Assert(Pull(PictureInPicture.Edge.Bottom, 0, 90) == new ScreenFit.Pixels(1416, 738, 640, 360), "The bottom edge: its top kept");
            Assert(Pull(PictureInPicture.Edge.TopLeft, -160, -10) == new ScreenFit.Pixels(1256, 648, 640, 360), "A corner follows the way it was pulled the most, the opposite corner kept");
            Assert(Pull(PictureInPicture.Edge.BottomRight, -1000, -1000) == new ScreenFit.Pixels(1416, 738, 256, 144), "Never below 144 on its shorter side");
            Assert(Pull(PictureInPicture.Edge.Right, 5000, 0) is { Width: 1101, Height: 619 }, "Never above 60 % of the screen");
            // The edges and corners under the pointer, the corners reaching twice as far along the edges.
            Assert(PictureInPicture.EdgeAt(2, 100, 480, 270) == PictureInPicture.Edge.Left && PictureInPicture.EdgeAt(478, 100, 480, 270) == PictureInPicture.Edge.Right
                && PictureInPicture.EdgeAt(100, 2, 480, 270) == PictureInPicture.Edge.Top && PictureInPicture.EdgeAt(100, 268, 480, 270) == PictureInPicture.Edge.Bottom, "Sides");
            Assert(PictureInPicture.EdgeAt(2, 2, 480, 270) == PictureInPicture.Edge.TopLeft && PictureInPicture.EdgeAt(5, 12, 480, 270) == PictureInPicture.Edge.TopLeft && PictureInPicture.EdgeAt(470, 3, 480, 270) == PictureInPicture.Edge.TopRight
                && PictureInPicture.EdgeAt(3, 262, 480, 270) == PictureInPicture.Edge.BottomLeft && PictureInPicture.EdgeAt(478, 268, 480, 270) == PictureInPicture.Edge.BottomRight, "Corners");
            Assert(PictureInPicture.EdgeAt(5, 20, 480, 270) == PictureInPicture.Edge.Left && PictureInPicture.EdgeAt(240, 135, 480, 270) is null && PictureInPicture.EdgeAt(-1, 5, 480, 270) is null && PictureInPicture.EdgeAt(5, 5, 0, 0) is null, "Inside, outside");
            // Another shape (the next title): as wide, held by its side nearest the screen's edge.
            Assert(PictureInPicture.Refit(start, 4d / 3, primary) == new ScreenFit.Pixels(1416, 648, 480, 360), "At the bottom right: its bottom right corner kept");
            Assert(PictureInPicture.Refit(new(100, 100, 480, 270), 4d / 3, primary) == new ScreenFit.Pixels(100, 100, 480, 360), "At the top left: its top left corner kept");
            // Let go partly off the screens: inside the one showing most of it, or the nearest.
            Assert(PictureInPicture.Settle(new(1800, 900, 480, 270), [primary, small]) == new ScreenFit.Pixels(1440, 762, 480, 270), "Back inside its screen, above the taskbar");
            Assert(PictureInPicture.Settle(new(1700, 100, 480, 270), [primary, small]) == new ScreenFit.Pixels(1920, 100, 480, 270), "Across two screens: the one showing most of it");
            Assert(PictureInPicture.Settle(new(5000, 5000, 480, 270), [primary, small]) == new ScreenFit.Pixels(2806, 458, 480, 270), "Off every screen: the nearest");
            Assert(PictureInPicture.Settle(start, []) == start && PictureInPicture.ScreenOf(start, []) is null, "No screen: left as it is");
            return Task.CompletedTask;
        });
        await Test("Image dans l’image : sous-titres agrandis pour rester lisibles dans la petite fenêtre, jamais réduits", () =>
        {
            Assert(PictureInPicture.SubtitleScale(40, 270) == 1.33, $"480 × 270, size 40: {PictureInPicture.SubtitleScale(40, 270)}");
            Assert(PictureInPicture.SubtitleScale(40, 144) == 2, "The smallest window: at most doubled");
            Assert(PictureInPicture.SubtitleScale(40, 720) == 1 && PictureInPicture.SubtitleScale(60, 270) == 1, "Already large enough: mpv's own size");
            Assert(PictureInPicture.SubtitleScale(0, 270) == 1 && PictureInPicture.SubtitleScale(40, double.NaN) == 1 && PictureInPicture.SubtitleScale(40, 0) == 1, "Nothing known: mpv's own size");
            return Task.CompletedTask;
        });
        await Test("Image dans l’image : commandes à la taille de la petite fenêtre, sous-titres au-dessus d’elles, « 10 » au milieu de son cercle (Windows et Mac)", () =>
        {
            var large = PictureInPicture.ControlsFor(480, 270); var smallest = PictureInPicture.ControlsFor(256, 144); var narrow = PictureInPicture.ControlsFor(144, 256);
            Assert(large is { Small: false, Title: true, Jumps: true } && PictureInPicture.ControlsFor(340, 200) == large, "From 340 × 200: the player's own sizes");
            Assert(smallest is { Small: true, Title: false, Jumps: true } && PictureInPicture.ControlsFor(320, 180) is { Small: true, Title: true }, "Smaller: lighter, the title from 300 wide");
            Assert(narrow is { Small: true, Title: false, Jumps: false } && PictureInPicture.ControlsFor(double.NaN, 270).Small, "A narrow window keeps play alone; nothing known is small");
            // Every row fits the smallest window, clear of the band along the edges that resizes it.
            foreach (var (fit, width, height) in new[] { (large, 480d, 270d), (smallest, 256d, 144d), (narrow, 144d, 256d) })
            {
                var corners = PictureInPicture.Controls.Margin + fit.Corner;
                var middle = (fit.Jumps ? 2 * (fit.Jump + fit.Spacing) : 0) + fit.Play;
                Assert(PictureInPicture.Controls.Margin > PictureInPicture.Grip && height - 2 * corners - fit.Play >= 24 && width - middle >= 24 && fit.Bar > corners, $"{width} × {height}: rows and spaces");
                Assert(Math.Abs(fit.Inset - PictureInPicture.Controls.Margin - (fit.Corner - fit.CornerGlyph) / 2) < .01 && fit.Lift > corners - (fit.Corner - fit.CornerGlyph) / 2, $"{width} × {height}: insets and the subtitles' lift");
            }
            Assert(large.Inset == 16 && smallest.Inset == 14 && large.Lift == 41 && smallest.Lift == 37, "Insets and lifts");
            Assert(PictureInPicture.SubtitleLift(41, 270, 100) == 84 && PictureInPicture.SubtitleLift(37, 144, 100) == 74 && PictureInPicture.SubtitleLift(27, 270, 100) == 90, "mpv's sub-pos, a share of the window");
            foreach (var (lift, height) in new[] { (41d, 270d), (37d, 144d), (41d, 1000d), (37d, 200d) })
            {
                var raised = height * (100 - PictureInPicture.SubtitleLift(lift, height, 100)!.Value) / 100;
                Assert(raised >= lift && raised < lift + height / 100, $"{height} high: the subtitles' bottom {lift} above the window's ({raised:0.0})");
            }
            Assert(PictureInPicture.SubtitleLift(41, 270, 82) is null && PictureInPicture.SubtitleLift(41, 270, 84) is null && PictureInPicture.SubtitleLift(37, 144, 82) == 74, "Their own height kept when it is already above the controls");
            Assert(PictureInPicture.SubtitleLift(500, 270, 100) == 50 && PictureInPicture.SubtitleLift(41, 0, 100) is null && PictureInPicture.SubtitleLift(double.NaN, 270, 100) is null, "Never above the middle, nothing when nothing is known");
            // The « 10 » centred on the arcs' centre: as far from both ends of each arc as their radius (8.5).
            foreach (var (kind, ends) in new[] { ("rewind", new[] { (4d, 8d), (3.5, 14d) }), ("forward", new[] { (20d, 8d), (20.5, 14d) }) })
            {
                var (x, y) = IconPaths.TenCentre(kind)!.Value;
                Assert(IconPaths.Strokes[kind].StartsWith(FormattableString.Invariant($"M{ends[0].Item1} {ends[0].Item2}A8.5 8.5"), StringComparison.Ordinal), kind + ": " + IconPaths.Strokes[kind]);
                Assert(ends.All(e => Math.Abs(Math.Sqrt(Math.Pow(e.Item1 - x, 2) + Math.Pow(e.Item2 - y, 2)) - 8.5) < .01), $"{kind}: centre ({x}, {y})");
            }
            Assert(IconPaths.TenCentre("play") is null, "Only the jumps carry it");
            return Task.CompletedTask;
        });
        await Test("Mini-lecteur : rangé tout seul contre le bord droit, un bout de la vidéo qui dépasse, sorti au survol, rangé de nouveau après (Windows et Mac)", () =>
        {
            static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
            var dock = new MiniDock(); dock.Arrive(At(10));
            Assert(dock.Out && !dock.Update(At(11), over: false, held: false) && dock.Out, "Just shrunk: out, long enough to see where the film went");
            Assert(dock.Update(At(10) + MiniDock.FirstRest, over: false, held: false) && !dock.Out, "Then tucked away by itself");
            // The pointer passing by on its way to the edge does not pull it; resting on the sliver does.
            Assert(!dock.Update(At(20), over: true, held: false) && !dock.Update(At(20.05), over: true, held: false) && !dock.Out, "A pointer passing by");
            Assert(!dock.Update(At(20.06), over: false, held: false) && !dock.Update(At(20.1), over: true, held: false) && !dock.Out, "Off the sliver, the wait starts again");
            Assert(dock.Update(At(20.1) + MiniDock.Dwell, over: true, held: false) && dock.Out, "Resting on it: out");
            Assert(!dock.Update(At(30), over: true, held: false) && dock.Out, "Out as long as the pointer is on it");
            Assert(!dock.Update(At(31), over: false, held: false) && !dock.Update(At(31.5), over: true, held: false) && !dock.Update(At(32.5), over: false, held: false) && dock.Out, "A pointer that slips off its edge and back");
            Assert(dock.Update(At(32.5) + MiniDock.Linger, over: false, held: false) && !dock.Out, "Back once the pointer has left");
            // Dragged, a menu or a question open: out, whatever the pointer does, then back after the pointer has gone.
            Assert(dock.Update(At(40), over: false, held: true) && dock.Out && !dock.Update(At(50), over: false, held: true), "Held out");
            Assert(!dock.Update(At(50.1), over: false, held: false) && dock.Update(At(50.1) + MiniDock.Linger, over: false, held: false) && !dock.Out, "Let go: back after the linger");
            dock.Show(At(60));
            Assert(dock.Out && !dock.Update(At(60.5), over: false, held: false) && dock.Update(At(60) + MiniDock.Linger, over: false, held: false), "Shown at once, as after a drag");
            dock.Arrive(At(70));
            Assert(!dock.Update(At(70.1), over: true, held: false) && !dock.Update(At(80), over: true, held: false) && dock.Out, "Shrunk under the pointer: out until it leaves");
            // Places: against the right edge, a sliver tucked away; down the window in proportion.
            Assert(MiniDock.Left(true, 1600) == 1600 - 20 - 384 && MiniDock.Left(false, 1600) == 1600 - MiniDock.Peek, "Out, 20 from the edge; tucked, 14 showing");
            Assert(MiniDock.Top(1, 42, 900) == 900 - 20 - 216 && MiniDock.Top(0, 42, 900) == 42 && MiniDock.Top(.5, 42, 900) == 42 + (664 - 42) / 2d, "Down the window");
            Assert(MiniDock.Top(1, 42, 200) == 42 && MiniDock.Top(double.NaN, 42, 900) == 664 && MiniDock.Top(3, 42, 900) == 664, "No room, or nothing known: under the title bar or at the bottom");
            Assert(MiniDock.Share(664, 42, 900) == 1 && MiniDock.Share(42, 42, 900) == 0 && MiniDock.Share(-50, 42, 900) == 0 && MiniDock.Share(353, 42, 900) == .5, "Let go: its share of the way down");
            Assert(MiniDock.Share(100, 42, 250) == 1 && MiniDock.Top(MiniDock.Share(353, 42, 900), 42, 1200) == 42 + (964 - 42) / 2d, "Kept in proportion when the window grows");
            return Task.CompletedTask;
        });
        await Test("Lecture et pause au milieu de l’image : l’icône seule, là tout de suite, nette, puis partie vite en s’ouvrant, sans voile qui traîne (Windows et Mac)", () =>
        {
            Assert(PlayStateBadge.At(0) == (0, PlayStateBadge.FromScale), $"At the press: {PlayStateBadge.At(0)}");
            Assert(PlayStateBadge.At(PlayStateBadge.In).Opacity == 1 && PlayStateBadge.At(PlayStateBadge.Settle) == (1, 1) && PlayStateBadge.At(PlayStateBadge.Hold) == (1, 1), "Opaque from 70 ms, at its size from 160 ms, held until 360 ms");
            Assert(PlayStateBadge.At(PlayStateBadge.End) == (0, PlayStateBadge.ToScale) && PlayStateBadge.At(10_000) == (0, PlayStateBadge.ToScale) && PlayStateBadge.At(double.NaN).Opacity == 0, "Gone at 620 ms and after");
            // Leaving, it goes while it can still be seen: barely 50 ms faint (5 to 40 %), where the old badge left a
            // grey veil over the film for 150 ms.
            var faint = Enumerable.Range((int)PlayStateBadge.Hold, 400).Count(ms => PlayStateBadge.At(ms).Opacity is > .05 and < .4);
            Assert(faint <= 60, $"{faint} ms faint");
            // Smooth at 240 Hz: no jump from one frame to the next, and once it leaves it only fades and opens up.
            const double frame = 1000 / 240d;
            for (var ms = 0d; ms < PlayStateBadge.End; ms += frame)
            {
                var (a, s) = PlayStateBadge.At(ms); var (b, t) = PlayStateBadge.At(ms + frame);
                Assert(Math.Abs(a - b) < .15 && Math.Abs(s - t) < .01, $"{ms:0} ms: {a:0.00} then {b:0.00}, {s:0.000} then {t:0.000}");
                if (ms >= PlayStateBadge.Hold) Assert(b <= a && t >= s, $"{ms:0} ms: leaving, it never comes back");
            }
            // Pressed again while it shows: from the opacity it has, never off for a frame.
            Assert(PlayStateBadge.At(0, .6).Opacity == .6 && PlayStateBadge.At(35, .6).Opacity > .85 && PlayStateBadge.At(0, 2).Opacity == 1 && PlayStateBadge.At(0, double.NaN).Opacity == 0, "From the opacity it had");
            // At its largest, it and its wide shadow (three standard deviations around it) fit the room kept for them.
            Assert(PlayStateBadge.Glyph * PlayStateBadge.ToScale + 2 * (PlayStateBadge.Glow.Down + 3 * PlayStateBadge.Glow.Sigma) <= PlayStateBadge.Room, "Room for the shadow");
            return Task.CompletedTask;
        });
        await Test("Sous-titres gardés sur Jellyfin : langues, résultats, messages et nouvelle piste, comme Mira web (tests/fixtures/subtitles.json)", () =>
        {
            var repository = TestSupport.Repository() ?? throw new Exception("Repository not found");
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "subtitles.json")))!.AsObject();
            foreach (var x in fixture["searchLanguages"]!.AsArray())
                Assert(JellyfinSubtitles.SearchLanguages((string)x!["preferences"]!).SequenceEqual(x["languages"]!.AsArray().Select(l => (string)l!)), "Languages of " + x["preferences"]);
            Assert(JellyfinSubtitles.SearchCode("fr-FR") == "fre" && JellyfinSubtitles.SearchCode("deu") == "ger" && JellyfinSubtitles.SearchCode("und") is null, "Search codes");
            foreach (var x in fixture["remote"]!.AsArray())
            {
                var found = JellyfinSubtitles.FromJellyfin(x!["found"]!.AsObject(), "fre")!;
                Assert(JellyfinSubtitles.Describe(found) == ((string)x["label"]!, (string)x["details"]!), "Row: " + JellyfinSubtitles.Describe(found));
            }
            var sorted = JellyfinSubtitles.Sort(fixture["sort"]!["found"]!.AsArray().Select(x => JellyfinSubtitles.FromJellyfin(x!.AsObject(), "fre")).OfType<RemoteSubtitle>());
            Assert(sorted.Select(x => x.Id).SequenceEqual(fixture["sort"]!["order"]!.AsArray().Select(x => (string)x!)), "Order: " + string.Join(", ", sorted.Select(x => x.Id)));
            foreach (var x in fixture["privacy"]!.AsArray())
                Assert(JellyfinSubtitles.Privacy([.. x!["providers"]!.AsArray().Select(p => (string)p!)]) == (string)x["text"]!, "Privacy");
            foreach (var x in fixture["kept"]!.AsArray())
                Assert(JellyfinSubtitles.KeptText(Enum.Parse<SubtitleKept>((string)x!["kept"]!), (string)x["language"]!) == (string)x["text"]!, "Kept: " + x["kept"]);
            List<MediaStream> Streams(string key) => [.. fixture["arrived"]![key]!.AsArray().Select(s => new MediaStream { Type = (string)s!["Type"]!, Index = (int)s["Index"]!, IsExternal = s["IsExternal"] is { } e && (bool)e!, Path = (string?)s["Path"] })];
            Assert(JellyfinSubtitles.Arrived(Streams("before"), Streams("now"))?.Index == (int)fixture["arrived"]!["index"]! && JellyfinSubtitles.Arrived(Streams("now"), Streams("now")) is null, "New track");
            foreach (var x in fixture["guess"]!.AsArray())
            {
                var guess = JellyfinSubtitles.Guess((string)x!["file"]!, (string)x["preferences"]!);
                var expected = x["format"] is null ? null : ((string)x["format"]!, (string)x["language"]!, (bool)x["forced"]!, (bool)x["hearingImpaired"]!) as (string, string, bool, bool)?;
                Assert(guess == expected, $"Guess of {x["file"]}: {guess}");
            }
            return Task.CompletedTask;
        });
        await Test("Sous-titres automatiques : lignes du journal et attente, comme Mira web (tests/fixtures/subtitles-auto.json)", () =>
        {
            var repository = TestSupport.Repository() ?? throw new Exception("Repository not found");
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "subtitles-auto.json")))!.AsObject();
            foreach (var x in fixture["journal"]!.AsArray())
            {
                var entry = System.Text.Json.JsonSerializer.Deserialize<AutoSubtitleEntry>(x!["entry"]!.ToJsonString())!;
                Assert(AutoSubtitles.Line(entry) == (string)x["line"]!, "Line: " + AutoSubtitles.Line(entry));
            }
            foreach (var x in fixture["waiting"]!.AsArray())
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<AutoSubtitleState>(x!["state"]!.ToJsonString())!;
                Assert(AutoSubtitles.Waiting(state) == (string)x["line"]!, "Waiting: " + AutoSubtitles.Waiting(state));
            }
            Assert(AutoSubtitles.Tried([new("OpenSubtitles", true, 1240), new("SubDL", false, Refused: true)]) == "OpenSubtitles répond (1\u00A0240 téléchargements restants) · SubDL : identifiants ou clé refusés", "Tried: " + AutoSubtitles.Tried([new("OpenSubtitles", true, 1240)]));
            Assert(AutoSubtitles.Tried([]) == "Aucune source allumée." && AutoSubtitles.LanguageName("spa") == "Espagnol" && AutoSubtitles.LanguageName("xyz") == "xyz", "Tried, names");
            Assert(AutoSubtitles.Languages.Distinct().Count() == AutoSubtitles.Languages.Count && AutoSubtitles.Languages.All(c => PlayerText.Language(c) is not null), "Languages offered");
            return Task.CompletedTask;
        });
        await Test("Sous-titres gardés sur Jellyfin : envoi, dossier du film en lecture seule, droit refusé et recherche", async () =>
        {
            // Jellyfin 12.1 with a media folder it only reads: 500, then the film library keeps them in its own data.
            var calls = new List<string>(); JsonObject? options = null; var sent = new List<JsonObject>(); var uploads = 0; var policy = "IsAdministrator";
            HttpResponseMessage Answer(HttpRequestMessage request)
            {
                var path = request.RequestUri!.PathAndQuery; calls.Add(request.Method + " " + path);
                var body = request.Content?.ReadAsStringAsync().Result;
                if (path == "/Videos/film/Subtitles") { sent.Add(JsonNode.Parse(body!)!.AsObject()); return new(++uploads == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent); }
                if (path.StartsWith("/Items/film/Ancestors", StringComparison.Ordinal)) return JsonResponse(new object[] { new { Id = "lib-films", Type = "CollectionFolder" }, new { Id = "root", Type = "UserRootFolder" } });
                if (path == "/Library/VirtualFolders") return JsonResponse(new object[] { new { Name = "Séries", ItemId = "lib-series", LibraryOptions = new { SaveSubtitlesWithMedia = true } }, new { Name = "Films", ItemId = "lib-films", LibraryOptions = new { SaveSubtitlesWithMedia = true, EnableRealtimeMonitor = true } } });
                if (path == "/Library/VirtualFolders/LibraryOptions") { options = JsonNode.Parse(body!)!.AsObject(); return new(HttpStatusCode.NoContent); }
                if (path == "/Providers/Subtitles/Subtitles/os%2F1") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nÇa va ?\n")) };
                if (path == "/Users/Me") return JsonResponse(new { Policy = new Dictionary<string, bool> { [policy] = true } });
                if (path == "/Libraries/AvailableOptions?libraryContentType=movies") return JsonResponse(new { SubtitleFetchers = new[] { new { Name = "Open Subtitles", DefaultEnabled = true } } });
                if (path == "/Items/film/RemoteSearch/Subtitles/fre") return JsonResponse(new object[] { new { Id = "os/2", Name = "Vieux", ProviderName = "Open Subtitles", Format = "srt", DownloadCount = 900 }, new { Id = "os/1", Name = "Exact", ProviderName = "Open Subtitles", Format = "SRT", ThreeLetterISOLanguageName = "fre", IsHashMatch = true, DownloadCount = 3 } });
                return new(HttpStatusCode.NotFound);
            }
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request => Task.FromResult(Answer(request))));
            Assert((await client.SubtitleProvidersAsync()).SequenceEqual(["Open Subtitles"]), "Providers for an administrator");
            var found = await client.SearchSubtitlesAsync("film", "fre");
            Assert(found.Select(x => x.Id).SequenceEqual(["os/1", "os/2"]), "Found: " + string.Join(", ", found.Select(x => x.Id)));
            Assert(await client.KeepRemoteSubtitleAsync("film", found[0]) == SubtitleKept.KeptInJellyfinData, "Kept in Jellyfin's data");
            Assert(options?["Id"]?.GetValue<string>() == "lib-films" && options["LibraryOptions"]?["SaveSubtitlesWithMedia"]?.GetValue<bool>() == false
                && options["LibraryOptions"]?["EnableRealtimeMonitor"]?.GetValue<bool>() == true, "Library options: " + options?.ToJsonString());
            Assert(sent.Count == 2 && sent[1]["Language"]!.GetValue<string>() == "fre" && sent[1]["Format"]!.GetValue<string>() == "srt" && !sent[1]["IsForced"]!.GetValue<bool>()
                && Encoding.UTF8.GetString(Convert.FromBase64String(sent[1]["Data"]!.GetValue<string>())).Contains("Ça va ?"), "Sent: " + sent.LastOrDefault()?.ToJsonString());
            // Already in Jellyfin's data, and still refused to write: nothing more to change.
            uploads = 0; options = null;
            var upload = new SubtitleUpload("srt", "eng", true, false, [1, 2, 3]);
            using var data = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/Videos/film/Subtitles" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "/Library/VirtualFolders" => JsonResponse(new object[] { new { ItemId = "lib-films", LibraryOptions = new { SaveSubtitlesWithMedia = false } } }),
                _ => request.RequestUri.AbsolutePath.StartsWith("/Items/film/Ancestors", StringComparison.Ordinal) ? JsonResponse(new object[] { new { Id = "lib-films" } }) : new HttpResponseMessage(HttpStatusCode.NotFound),
            })));
            Assert(await data.KeepSubtitleAsync("film", upload) == SubtitleKept.NotWritten && options is null, "Not written");
            using var refused = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
            Assert(await refused.KeepSubtitleAsync("film", upload) == SubtitleKept.Refused, "Refused");
            policy = "EnableSubtitleManagement"; Assert(await client.CanManageSubtitlesAsync() == true, "Allowed to manage subtitles");
            policy = "EnableMediaPlayback"; Assert(await client.CanManageSubtitlesAsync() == false && (await client.SubtitleProvidersAsync()).Count == 0, "No search without the right");
            // The file just kept, once Jellyfin lists it (by its path: it moved the index of the other one), and the
            // address mpv reads it from; nothing when Jellyfin never lists it.
            var looks = 0;
            using var listing = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(_ => Task.FromResult(JsonResponse(new
            {
                Id = "film", MediaSources = new[] { new { Id = "src", MediaStreams = ++looks < 2
                    ? new object[] { new { Index = 2, Type = "Subtitle", Codec = "subrip", IsExternal = true, Path = "/media/Film.en.srt" } }
                    : [new { Index = 2, Type = "Subtitle", Codec = "subrip", IsExternal = true, Path = "/config/metadata/Film.fre.srt" }, new { Index = 3, Type = "Subtitle", Codec = "subrip", IsExternal = true, Path = "/media/Film.en.srt" }] } },
            }))));
            var known = await listing.StreamsAsync("film", "src");
            var arrived = await listing.KeptSubtitleAsync("film", "src", known, TimeSpan.FromSeconds(5));
            Assert(arrived?.Path == "/config/metadata/Film.fre.srt" && listing.SubtitleUri("film", "src", arrived).AbsoluteUri == "http://localhost/Videos/film/src/Subtitles/2/0/Stream.srt",
                "Arrived: " + arrived?.Path);
            Assert(await listing.KeptSubtitleAsync("film", "src", await listing.StreamsAsync("film", "src"), TimeSpan.Zero) is null, "Never listed");
            // A file loaded during playback: read to be sent, unless it is too big to be subtitles.
            var file = Path.Combine(Path.GetTempPath(), $"mira-{Guid.NewGuid():N}.fr.srt");
            try
            {
                await File.WriteAllTextAsync(file, "1\n00:00:01,000 --> 00:00:02,000\nSalut\n");
                var guess = JellyfinSubtitles.Guess(Path.GetFileName(file), null)!.Value;
                Assert((await JellyfinSubtitles.ReadAsync(file, guess)) is { Language: "fre", Format: "srt", Data.Length: > 0 }, "Read");
                using (var stream = File.OpenWrite(file)) stream.SetLength(JellyfinSubtitles.MaxSize + 1);
                Assert(await JellyfinSubtitles.ReadAsync(file, guess) is null, "Too big");
            }
            finally { File.Delete(file); }
        });
        await Test("Progression bornée et libellés d’épisodes", () =>
        {
            var item = new MediaItem { Id = "x", Name = "Le départ", SeriesName = "Les étoiles", Type = "Episode", ParentIndexNumber = 2, IndexNumber = 3, RunTimeTicks = 100, UserData = new() { PlaybackPositionTicks = 200 } };
            Assert(item.Progress == 1, "Progress overflow"); Assert(item.Subtitle.Contains("S02 · E03"), "Episode label missing"); return Task.CompletedTask;
        });
        await Test("Trackpad du Mac : sens des doigts, balayage Retour/Avant, axe, pincement", () =>
        {
            // Natural scrolling: the content follows the fingers; without it, the wheel goes the other way.
            Assert(Trackpad.Finger(1, -1, natural: true) == (1, 1) && Trackpad.Finger(1, -1, natural: false) == (-1, -1), "Finger direction");
            // Fingers to the right, in steps of 16 ms: the page follows them, and letting go past the reach goes back, once.
            var swipe = new TrackpadSwipe(); long time = 1000;
            for (var i = 0; i < 40; i++) swipe.Add(.15, .01, time += 16);
            Assert(swipe.Axis == SwipeAxis.Horizontal && swipe.Way == -1 && Math.Abs(swipe.Turn - 6) < 1e-9, $"Back swipe: {swipe.Axis} {swipe.Way} {swipe.Turn}");
            Assert(swipe.Release(rested: true) == -1 && swipe.Release(rested: true) == 0 && swipe.Turn == 0, "Back swipe let go");
            // A pause ends the gesture: the next one, to the left, goes forward.
            time += Trackpad.Gap + 1;
            for (var i = 0; i < 30; i++) swipe.Add(-.15, 0, time += 16);
            Assert(swipe.Way == 1 && Math.Abs(swipe.Turn - 4.5) < 1e-9 && swipe.Release(rested: true) == 1, $"Forward swipe: {swipe.Way} {swipe.Turn}");
            // Short of the reach, the fingers still before leaving: the page comes back. The same travel flicked: it turns.
            swipe = new TrackpadSwipe(); time = 0;
            for (var i = 0; i < 10; i++) swipe.Add(.2, 0, time += 16);
            Assert(swipe.Turn is > 1 and < Trackpad.Reach && swipe.Release(rested: true) == 0, $"Half a swipe: {swipe.Turn}");
            swipe = new TrackpadSwipe(); time = 0;
            for (var i = 0; i < 10; i++) swipe.Add(.3, 0, time += 16);
            Assert(swipe.Speed > Trackpad.Flick && swipe.Release(rested: false) == -1, $"A flick did not turn the page: {swipe.Speed}");
            // Past the reach, then flicked back: the page comes back. Fingers back toward where they started: never below nothing.
            swipe = new TrackpadSwipe(); time = 0;
            for (var i = 0; i < 30; i++) swipe.Add(.2, 0, time += 16);
            for (var i = 0; i < 3; i++) swipe.Add(-.6, 0, time += 16);
            Assert(swipe.Turn >= Trackpad.Reach && swipe.Release(rested: false) == 0, $"Flicked back, the page turned: {swipe.Turn} {swipe.Speed}");
            swipe = new TrackpadSwipe(); time = 0;
            for (var i = 0; i < 10; i++) swipe.Add(.2, 0, time += 16);
            for (var i = 0; i < 20; i++) swipe.Add(-.2, 0, time += 16);
            Assert(swipe.Way == -1 && swipe.Turn == 0 && swipe.Release(rested: true) == 0, $"Fingers back past the start: {swipe.Way} {swipe.Turn}");
            // A scroll down a page that drifts sideways is vertical, whatever the drift adds up to later.
            swipe = new TrackpadSwipe(); time = 0;
            for (var i = 0; i < 60; i++) swipe.Add(.08, -.2, time += 16);
            Assert(swipe.Axis == SwipeAxis.Vertical && swipe.Way == 0 && swipe.Turn == 0 && swipe.Release(rested: true) == 0, "Vertical drift went back");
            // Claimed by a row that scrolls: no page change.
            swipe = new TrackpadSwipe(); time = 0;
            for (var i = 0; i < 40; i++) { if (swipe.Add(.15, 0, time += 16)) swipe.Claim(); }
            Assert(swipe.Claimed && swipe.Turn == 0 && swipe.Release(rested: true) == 0, "A claimed swipe changed the page");
            // A clock that goes back starts a new gesture.
            swipe.Add(.1, 0, 5); Assert(!swipe.Claimed && swipe.Axis == SwipeAxis.Undecided, "Clock going back");
            // Pinch: once per gesture, either way; the grids' zoom stays within its bounds.
            var pinch = new TrackpadPinch(); time = 0; var pinched = new List<int>();
            for (var i = 0; i < 20; i++) { pinch.Add(.03, time += 16); if (pinch.Pinched() is var way and not 0) pinched.Add(way); }
            time += Trackpad.Gap + 1;
            for (var i = 0; i < 20; i++) { pinch.Add(-.03, time += 16); if (pinch.Pinched() is var way and not 0) pinched.Add(way); }
            Assert(pinched.SequenceEqual([1, -1]), "Pinch: " + string.Join(",", pinched));
            Assert(Trackpad.Zoom(1, 5) == Trackpad.ZoomMax && Trackpad.Zoom(1, -.9) == Trackpad.ZoomMin && Math.Abs(Trackpad.Zoom(1, .1) - 1.1) < 1e-9, "Zoom bounds");
            Assert(new PlayerSettings().GridZoom == 1, "Grid zoom default");
            return Task.CompletedTask;
        });
        await Test("Trackpad du Mac : l’élan de macOS ne compte pas dans le geste", () =>
        {
            // NSEventPhase and momentumPhase: a mouse's wheel says neither.
            Assert(Trackpad.Phase(1, 0) == ScrollPhase.Began && Trackpad.Phase(32, 0) == ScrollPhase.Began && Trackpad.Phase(4, 0) == ScrollPhase.Moved && Trackpad.Phase(2, 0) == ScrollPhase.Moved
                && Trackpad.Phase(8, 0) == ScrollPhase.Ended && Trackpad.Phase(16, 0) == ScrollPhase.Ended && Trackpad.Phase(0, 4) == ScrollPhase.Momentum && Trackpad.Phase(0, 0) == ScrollPhase.Unknown, "Phases");
            // Two thirds of the reach by the fingers, slower than a flick, then macOS's momentum. Read as before (no phase), the
            // momentum turned the page once it died out; with the phases, the fingers let go as the momentum began, short of
            // the reach: the page comes back, and nothing after it moves it.
            int Flick(bool phases)
            {
                var swipe = new TrackpadSwipe(); long time = 1000; var way = 0;
                for (var i = 0; i < 13; i++) swipe.Add(.15, 0, time += 16, phases ? ScrollPhase.Moved : ScrollPhase.Unknown);
                for (var i = 0; i < 30; i++)
                {
                    swipe.Add(.1, 0, time += 16, phases ? ScrollPhase.Momentum : ScrollPhase.Unknown);
                    if (phases && swipe.Coasting && !swipe.Done) way = swipe.Release(rested: false);
                }
                Assert(!phases || swipe.Coasting && swipe.Turn == 0, "The page left turned during the momentum");
                return phases ? way : swipe.Release(rested: true);
            }
            Assert(Flick(phases: false) == -1 && Flick(phases: true) == 0, "A flick's momentum turned the page");
            // The scrub's travel stops with the fingers.
            var scrub = new TrackpadSwipe(); long now = 0;
            for (var i = 0; i < 10; i++) scrub.Add(.2, 0, now += 16, ScrollPhase.Moved);
            var held = scrub.X;
            for (var i = 0; i < 20; i++) scrub.Add(.2, 0, now += 16, ScrollPhase.Momentum);
            Assert(Math.Abs(scrub.X - held) < 1e-9 && !scrub.Started, $"Momentum moved the scrub: {held} then {scrub.X}");
            // Fingers back during the momentum, their « began » never seen (it carries no travel): a new gesture all the same.
            scrub.Add(-.2, 0, now += 16, ScrollPhase.Moved);
            Assert(scrub.Started && !scrub.Coasting && Math.Abs(scrub.X + .2) < 1e-9, "Fingers back after the momentum: not a new gesture");
            // Within one gesture (fingers down), each event goes on with it.
            scrub.Add(-.2, 0, now += 16, ScrollPhase.Moved);
            Assert(!scrub.Started && Math.Abs(scrub.X + .4) < 1e-9, "One gesture cut in two");
            return Task.CompletedTask;
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
        await Test("Défilement : arrêt net à la fin, rien qui traîne après", () =>
        {
            foreach (var fps in new[] { 30, 60, 144, 240 })
            {
                // One notch, then three quick ones: the list stands still at the end of the easing, never later.
                var motion = new ScrollMotion(); motion.Add(108, 5000);
                var frames = 0;
                while (motion.Moving) { motion.Step(1d / fps, 5000); frames++; }
                Assert(motion.Position == 108 && frames <= Math.Ceiling(ScrollMotion.Duration * fps) + 1, $"{fps} images/s : arrêt après {frames} images");
                motion.Add(108, 5000); motion.Step(.03, 5000); motion.Add(108, 5000); motion.Step(.03, 5000); motion.Add(108, 5000);
                frames = 0;
                while (motion.Moving) { motion.Step(1d / fps, 5000); frames++; }
                Assert(motion.Position == 432 && frames <= Math.Ceiling(ScrollMotion.Duration * fps) + 1, $"{fps} images/s : trois crans arrêtés après {frames} images");
            }
            return Task.CompletedTask;
        });
        await Test("Défilement : la vitesse suit la molette sans à-coup", () =>
        {
            // A notch every 30 to 150 ms, as a hand turns a wheel: from one frame to the next, the speed changes by 12 % at
            // most at 144 images/s and more, as the list eases between notches (a curve started again from rest at each
            // notch: 28 to 165 %), never backwards, never past.
            foreach (var (fps, gap) in new[] { 60, 144, 240 }.SelectMany(fps => new[] { .03, .045, .075, .11, .15 }.Select(gap => (fps, gap))))
            {
                var (positions, notches) = Wheel(fps, gap, 14);
                var speeds = positions.Zip(positions.Skip(1), (a, b) => b - a).ToList();
                Assert(speeds.All(x => x >= 0) && positions.Max() <= 14 * 108, $"{fps} images/s, cran toutes les {gap * 1000} ms : recul ou dépassement");
                // Between the third notch and the last: the list goes on, its speed even.
                var steady = Enumerable.Range(1, speeds.Count - 1).Where(i => i >= notches[2] && i < notches[^1]).ToList();
                var mean = steady.Average(i => speeds[i]);
                var jolt = steady.Max(i => Math.Abs(speeds[i] - speeds[i - 1])) / mean;
                Assert(jolt < (fps >= 144 ? .16 : .3), $"{fps} images/s, cran toutes les {gap * 1000} ms : la vitesse saute de {jolt:P0} d’une image à l’autre");
            }
            // From rest, the first notch moves as before: the cubic ease-out over Duration.
            var first = new ScrollMotion(); first.Add(108, 5000); first.Step(.05, 5000);
            Assert(Math.Abs(first.Position - 108 * (1 - Math.Pow(1 - .05 / ScrollMotion.Duration, 3))) < 1e-9, $"Premier cran : {first.Position}");
            return Task.CompletedTask;
        });
        await Test("Défilement : arrêt net après le dernier cran, même lancé vite", () =>
        {
            foreach (var fps in new[] { 60, 144, 240 })
            {
                // Spun fast (a notch every 45 ms): the list stands still the shortest curve after its last notch.
                var (positions, notches) = Wheel(fps, .045, 30);
                var still = positions.FindIndex(notches[^1], x => x == 30 * 108);
                Assert(still >= 0 && (still - notches[^1]) / (double)fps <= ScrollMotion.Shortest + 1.5 / fps, $"{fps} images/s : arrêt {(still - notches[^1]) * 1000d / fps:0} ms après le dernier cran");
                // Notches far apart: at most Duration after the last.
                (positions, notches) = Wheel(fps, .2, 5);
                still = positions.FindIndex(notches[^1], x => x == 5 * 108);
                Assert(still >= 0 && (still - notches[^1]) / (double)fps <= ScrollMotion.Duration + 1.5 / fps, $"{fps} images/s, crans espacés : arrêt {(still - notches[^1]) * 1000d / fps:0} ms après le dernier");
            }
            return Task.CompletedTask;
        });
        await Test("Défilement : au bout de la liste, les crans de plus n’allongent pas l’arrivée", () =>
        {
            foreach (var (fps, gap) in new[] { 60, 144, 240 }.SelectMany(fps => new[] { .045, .11, .15 }.Select(gap => (fps, gap))))
            {
                // 500 px to go, fourteen notches: the fifth reaches the end, the nine after it move nothing. The list stands
                // still at most Duration after the fifth (planned again at each notch, up to 290 ms, its last pixel creeping).
                var motion = new ScrollMotion(); double reached = double.NaN, still = double.NaN, before = 0; var given = 0;
                for (var frame = 1; frame < fps * 5 && double.IsNaN(still); frame++)
                {
                    var now = (frame - 1d) / fps;
                    while (given < 14 && given * gap <= now + 1e-9) { motion.Add(108, 500); given++; if (double.IsNaN(reached) && motion.Target == 500) reached = now; }
                    var at = motion.Step(1d / fps, 500);
                    Assert(at >= before && at <= 500, $"{fps} images/s : recul ou dépassement à {at}");
                    before = at;
                    if (at == 500 && !motion.Moving) still = frame / (double)fps;
                }
                Assert(still - reached <= ScrollMotion.Duration + 1.5 / fps, $"{fps} images/s, cran toutes les {gap * 1000} ms : arrêt {(still - reached) * 1000:0} ms après le cran qui touche le bout");
            }
            return Task.CompletedTask;
        });
        await Test("Défilement : une molette à crans est adoucie sur Mac, pas un trackpad", () =>
        {
            Assert(Trackpad.Notched(false, 0, -1.7) && !Trackpad.Notched(true, 0, -1), "Ce que macOS dit l’emporte");
            Assert(Trackpad.Notched(null, 0, -1) && Trackpad.Notched(null, 0, 3) && !Trackpad.Notched(null, 0, -.92) && !Trackpad.Notched(null, 0, 0), "Sans macOS : des lignes entières sont des crans");
            return Task.CompletedTask;
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
            Assert(PlaybackSession.StartAt(Film(TimeSpan.FromMinutes(56).Ticks), remember: true) == 0 && PlaybackSession.StartAt(Film(0, played: true), remember: true) == 0,
                "Nearly finished or watched: starts over");
            Assert(PlaybackSession.StartAt(Film(hour / 2, played: true), remember: true) == 1800 && Film(hour / 2, played: true).Resumable,
                "Watched before, left halfway again: Jellyfin 12 keeps it watched with the new position, and it resumes there");
            Assert(!Film(0, played: true).Resumable && !Film(TimeSpan.FromMinutes(56).Ticks, played: true).Resumable && !Film(0).Resumable, "Nothing to resume: watched, past 90 %, not started");
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

    /// <summary>
    /// <paramref name="notches"/> notches of 108 px, one every <paramref name="gap"/> s, as the Windows wheel steps a
    /// list (each between two frames, as WPF delivers it), drawn at <paramref name="fps"/>: the list's place at each frame
    /// until it stands still, and the frame before which each notch came.
    /// </summary>
    private static (List<double> Positions, List<int> Notches) Wheel(int fps, double gap, int notches)
    {
        var motion = new ScrollMotion(); var positions = new List<double> { 0 }; var at = new List<int>();
        var given = 0;
        for (var frame = 1; frame < fps * 30; frame++)
        {
            while (given < notches && given * gap <= (frame - 1d) / fps + 1e-9) { motion.Add(108, 100_000); at.Add(frame); given++; }
            positions.Add(motion.Step(1d / fps, 100_000));
            if (given == notches && !motion.Moving) break;
        }
        return (positions, at);
    }
}
