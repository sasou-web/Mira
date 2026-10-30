using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using static TestSupport;

/// <summary>
/// « Qualité » and the stream of a title on Windows and Mac: the bitrate (as Mira web's), mpv's device profile, the file
/// as it is or Jellyfin's conversion with its tracks, and a stream that stops before its end.
/// </summary>
internal static class StreamTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Qualité : débit demandé et « à la maison », comme Mira web (tests/fixtures/bitrate.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "bitrate.json")));
            var now = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
            foreach (var row in fixture.RootElement.GetProperty("known").EnumerateArray())
            {
                var choice = row[0].ValueKind == JsonValueKind.Number ? row[0].GetRawText() : row[0].GetString();
                ConnectionMeasure? measure = row[2].ValueKind == JsonValueKind.Null ? null : new(now.AddMinutes(-row[2][0].GetDouble()), row[2][1].GetDouble());
                var known = StreamQuality.KnownBitrate(choice, row[1].GetBoolean(), measure, now, row[3].GetInt64());
                long? expected = row[4].ValueKind == JsonValueKind.Null ? null : row[4].GetInt64();
                Assert(known == expected, $"{row.GetRawText()}: {known}");
            }
            foreach (var row in fixture.RootElement.GetProperty("home").EnumerateArray())
                Assert(StreamQuality.AtHome($"http://{row[0].GetString()}:8096/") == row[1].GetBoolean(), $"{row[0]}: {!row[1].GetBoolean()}");
            Assert(StreamQuality.Known("12345") == StreamQuality.Auto && StreamQuality.Known(null) == StreamQuality.Auto && StreamQuality.Known("max") == "max", "Unknown choices are « Automatique »");
            Assert(new PlayerSettings().Quality == StreamQuality.Auto && StreamQuality.Label("1500000") == "1,5 Mb/s" && StreamQuality.Choices.Count == 7, "Default, names");
            return Task.CompletedTask;
        });
        await Test("Image : niveaux de mpv, « Cinéma » par défaut, niveau en dessous quand le PC perd des images", () =>
        {
            Assert(new PlayerSettings().Picture == PictureQuality.Cinema && PictureQuality.Known("x") == PictureQuality.Cinema && PictureQuality.Known(null) == PictureQuality.Cinema, "« Cinéma » by default and for an unknown choice");
            Assert(PictureQuality.Lower(PictureQuality.Best) == PictureQuality.Cinema && PictureQuality.Lower(PictureQuality.Cinema) == PictureQuality.Standard && PictureQuality.Lower(PictureQuality.Standard) is null, "Each level has the one below");
            var standard = PictureQuality.Options(PictureQuality.Standard); var cinema = PictureQuality.Options(PictureQuality.Cinema).ToDictionary(x => x.Name, x => x.Value); var best = PictureQuality.Options(PictureQuality.Best).ToDictionary(x => x.Name, x => x.Value);
            Assert(standard.All(x => x.Value is null), "« Standard » is mpv's own: every option back to its default");
            Assert(cinema["video-sync"] == "display-resample" && cinema["interpolation"] == "yes" && cinema["tscale"] == "oversample" && cinema["deband"] == "yes" && cinema["scale"] is null, "« Cinéma »: frames timed to the screen, interpolation, deband, mpv's scaling");
            Assert(best["scale"] == "ewa_lanczossharp" && best["video-sync"] == "display-resample" && best["deband"] == "yes", "« Maximale »: « Cinéma » and the high-quality profile");
            Assert(standard.Select(x => x.Name).SequenceEqual(cinema.Keys) && cinema.Keys.SequenceEqual(best.Keys), "Every level names every option, so going down puts each one back");
            var mac = PictureQuality.Options(PictureQuality.Best, displaySync: false).ToDictionary(x => x.Name, x => x.Value);
            Assert(mac["video-sync"] is null && mac["interpolation"] is null && mac["tscale"] is null && mac["deband"] == "yes" && mac["scale"] == "ewa_lanczossharp" && mac.Keys.SequenceEqual(best.Keys),
                "Mac (render API, the screen's refresh unknown): no display timing nor interpolation, the rest of the level");
            Assert(PictureQuality.MacChoices.Select(x => x.Key).SequenceEqual(PictureQuality.Choices.Select(x => x.Key)) && PictureQuality.MacChoices.Select(x => x.Label).SequenceEqual(PictureQuality.Choices.Select(x => x.Label)), "Same levels and names on Mac");
            var watch = new PictureWatch(); var t = new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);
            bool Feed(double seconds, long drops, bool steady = true) => watch.Struggling(t.AddSeconds(seconds), drops, steady);
            Assert(!Feed(0, 0, steady: false) && !Feed(1, 40) && !Feed(2.9, 80), "Drops in the seconds after a pause, seek or resize do not count");
            Assert(!Feed(3, 100) && !Feed(8, 105) && !Feed(13, 111), "A few drops over 10 s are not stutter");
            Assert(Feed(14, 120), "12 drops within 10 s of steady playback: the PC cannot keep up");
            Assert(!Feed(15, 200) && !Feed(18, 220), "Then it starts over, after settling");
            Assert(!Feed(19, 0) && !Feed(25, 11), "A new file starts mpv's counter again");
            Assert(!Feed(26, 30, steady: false) && !Feed(30, 30) && !Feed(40, 41) && !Feed(52, 52), "Not steady resets; drops spread out stay under the limit");
            return Task.CompletedTask;
        });
        await Test("Sortie audio : sortie choisie seulement si branchée, canaux et passthrough pour mpv, son de test par haut-parleur", () =>
        {
            var settings = new PlayerSettings();
            Assert(settings.AudioDevice == AudioOutput.Default && settings.AudioChannels == AudioOutput.AutoChannels && settings.AudioPassthrough == AudioOutput.Off, "Windows' output, its channels, no passthrough by default");
            Assert(AudioOutput.Options(settings).All(x => x.Value is null), "Defaults are mpv's own: each option back to its default");
            const string list = """[{"name":"auto","description":"Autoselect device"},{"name":"wasapi/{0.0.0.00000000}.{a1}","description":"Haut-parleurs (Realtek(R) Audio)"},{"name":"wasapi/{0.0.0.00000000}.{b2}","description":"  "},{"name":"wasapi/{0.0.0.00000000}.{a1}","description":"double"},{"name":"openal/Speakers","description":"OpenAL"}]""";
            var devices = AudioOutput.Devices(list, "wasapi");
            Assert(devices.Count == 2 && devices[0].Label == "Haut-parleurs (Realtek(R) Audio)" && devices[1].Label == "{0.0.0.00000000}.{b2}", "Windows' devices, named by Windows, else by their id; auto, duplicates and other outputs left out");
            Assert(AudioOutput.Devices("not json", "wasapi").Count == 0 && AudioOutput.Devices(null, "wasapi").Count == 0 && AudioOutput.Devices("""{"a":1}""", "wasapi").Count == 0, "An unreadable list: no device");
            Assert(AudioOutput.Device("wasapi/{0.0.0.00000000}.{a1}", devices) == "wasapi/{0.0.0.00000000}.{a1}" && AudioOutput.Device("wasapi/gone", devices) == AudioOutput.Default && AudioOutput.Device("", devices) == AudioOutput.Default, "The device chosen while plugged in, else Windows' default");
            settings.AudioChannels = "5.1,stereo"; settings.AudioPassthrough = AudioOutput.Optical;
            var options = AudioOutput.Options(settings).ToDictionary(x => x.Name, x => x.Value);
            Assert(options["audio-channels"] == "5.1,stereo" && options["audio-spdif"] == "ac3,dts", "5.1 at most; an optical cable carries Dolby Digital and DTS only");
            Assert(AudioOutput.Codecs(AudioOutput.Hdmi) == "ac3,eac3,dts-hd,truehd" && AudioOutput.Codecs("x") is null && AudioOutput.KnownChannels("9.1") == AudioOutput.AutoChannels, "HDMI: TrueHD and DTS-HD too; unknown choices are the defaults");
            foreach (var (channels, count, mask) in new[] { (AudioOutput.AutoChannels, 2, 0x3), ("stereo", 2, 0x3), ("5.1,stereo", 6, 0x3F), ("7.1,5.1,stereo", 8, 0x63F) })
            {
                var (wav, speakers) = AudioOutput.TestSound(channels);
                var step = (int)(AudioOutput.TestRate * AudioOutput.TestStep.TotalSeconds);
                Assert(speakers.Count == count && System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 4) == wav.Length - 8 && BitConverter.ToUInt16(wav, 20) == 0xFFFE && BitConverter.ToUInt16(wav, 22) == count && BitConverter.ToInt32(wav, 24) == AudioOutput.TestRate && BitConverter.ToInt32(wav, 40) == mask && BitConverter.ToInt32(wav, 64) == wav.Length - 68 && wav.Length - 68 == step * count * count * 2,
                    $"{channels}: a WAV of {count} speakers with their layout, one step each");
                short Sample(int frame, int channel) => BitConverter.ToInt16(wav, 68 + (frame * count + channel) * 2);
                int Loudest(int frame) => Enumerable.Range(0, count).OrderByDescending(c => Math.Abs((int)Sample(frame, c))).First();
                Assert(Enumerable.Range(0, count).All(c => Loudest(c * step + 9013) == c && Enumerable.Range(0, count).Where(o => o != c).All(o => Sample(c * step + 9013, o) == 0)), $"{channels}: each speaker alone, in turn");
                Assert(Sample(0, 0) == 0 && Enumerable.Range(0, count * step).Max(f => Math.Abs((int)Sample(f, f / step))) < short.MaxValue / 2, $"{channels}: starts from silence, never loud");
            }
            Assert(AudioOutput.TestSound("stereo").Speakers.SequenceEqual(["gauche", "droite"]) && AudioOutput.TestSound("5.1,stereo").Speakers[3] == "caisson de basses", "Speakers named in the order heard");
            return Task.CompletedTask;
        });
        await Test("Qualité : profil de mpv, demande à PlaybackInfo, adresses sans jeton", () =>
        {
            var body = ServerStreams.Request("user-1", 6_000_000, 90, null, null, null);
            var profile = body["DeviceProfile"]!;
            Assert(body["MaxStreamingBitrate"]!.GetValue<long>() == 6_000_000 && profile["MaxStreamingBitrate"]!.GetValue<long>() == 6_000_000, "The bitrate, twice");
            Assert(body["StartTimeTicks"]!.GetValue<long>() == 900_000_000 && body["SubtitleStreamIndex"]!.GetValue<int>() == -1 && body["AudioStreamIndex"] is null && body["MediaSourceId"] is null, "Start, no picture subtitles burned by default");
            Assert(profile["DirectPlayProfiles"]!.AsArray().Count == 2 && profile["DirectPlayProfiles"]![0]!["Container"] is null, "Every file as it is");
            var conversion = profile["TranscodingProfiles"]![0]!;
            Assert(conversion["Protocol"]!.GetValue<string>() == "hls" && conversion["Container"]!.GetValue<string>() == "ts" && conversion["VideoCodec"]!.GetValue<string>() == "h264", "HLS in TS, H.264");
            var subtitles = profile["SubtitleProfiles"]!.AsArray().Select(x => (x!["Format"]!.GetValue<string>(), x["Method"]!.GetValue<string>())).ToList();
            Assert(subtitles.Contains(("ass", "Embed")) && subtitles.Contains(("ass", "External")) && subtitles.Contains(("pgssub", "Encode")) && !subtitles.Contains(("ass", "Encode")), "Text beside, pictures burned");
            var asked = ServerStreams.Request("user-1", 3_000_000, 0, 2, 3, "source-1");
            Assert(asked["AudioStreamIndex"]!.GetValue<int>() == 2 && asked["SubtitleStreamIndex"]!.GetValue<int>() == 3 && asked["MediaSourceId"]!.GetValue<string>() == "source-1", "Tracks asked");
            Assert(ServerStreams.WithoutKey("/videos/a/master.m3u8?DeviceId=d&ApiKey=secret&api_key=other&TranscodeReasons=ContainerBitrateExceedsLimit") == "/videos/a/master.m3u8?DeviceId=d&TranscodeReasons=ContainerBitrateExceedsLimit", "No token");
            Assert(ServerStreams.WithoutKey("/x?ApiKey=secret") == "/x" && ServerStreams.WithoutKey("/x") == "/x", "Only the token");
            Assert(string.Join("+", ServerStreams.Reasons("/v?TranscodeReasons=ContainerBitrateExceedsLimit%2CAudioCodecNotSupported")) == "ContainerBitrateExceedsLimit+AudioCodecNotSupported" && ServerStreams.Reasons(null).Count == 0, "Reasons");
            return Task.CompletedTask;
        });
        await Test("Qualité : fichier tel quel à la maison, conversion loin, débit mesuré une fois, pistes de la série (Jellyfin simulé)", async () =>
        {
            var requests = new List<JsonNode>(); var measures = 0;
            var handler = new Handler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/Playback/BitrateTest", StringComparison.Ordinal)) { measures++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4_000_000]) }; }
                if (!path.EndsWith("/PlaybackInfo", StringComparison.Ordinal) || request.Method != HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!; requests.Add(body);
                var bitrate = body["MaxStreamingBitrate"]!.GetValue<long>(); var audio = body["AudioStreamIndex"]?.GetValue<int>() ?? 1; var burn = body["SubtitleStreamIndex"]!.GetValue<int>();
                var direct = bitrate >= 25_000_000;
                return JsonResponse(Answer(direct, direct ? null : $"/videos/item-1/master.m3u8?DeviceId=device&MediaSourceId=source-1&VideoCodec=h264&AudioStreamIndex={audio}" + (burn >= 0 ? $"&SubtitleStreamIndex={burn}&SubtitleMethod=Encode" : "") + "&PlaySessionId=session-2&ApiKey=secret&TranscodeReasons=ContainerBitrateExceedsLimit"));
            });
            var item = new MediaItem { Id = "item-1", Name = "Film", Type = "Movie" };
            Task<bool> Here(string path) => Task.FromResult(path == "/media/film.mkv");

            using (var home = new JellyfinClient(new("http://127.0.0.1:8096/", "user-1", "Alice", "secret", "device"), handler))
            {
                var file = await ServerStreams.OpenAsync(home, new ConnectionSpeed(), StreamQuality.Auto, item, new(0), "jpn,fre,eng", Here);
                Assert(!file.Converted && file.Location == "/media/film.mkv" && file.Authorization is null && file.PlayMethod == "DirectPlay" && measures == 0, $"At home, this computer's file: {file.Location}");
                var stream = await ServerStreams.OpenAsync(home, new ConnectionSpeed(), StreamQuality.Max, item, new(0), "jpn,fre,eng", _ => Task.FromResult(false));
                Assert(!stream.Converted && stream.Location == "http://127.0.0.1:8096/Videos/item-1/stream?static=true&mediaSourceId=source-1" && stream.Authorization!.Contains("Token=\"secret\"", StringComparison.Ordinal), $"The file as it is from Jellyfin: {stream.Location}");
            }
            using var away = new JellyfinClient(new("http://jellyfin.example.com/jellyfin/", "user-1", "Alice", "secret", "device"), handler);
            requests.Clear();
            var converted = await ServerStreams.OpenAsync(away, new ConnectionSpeed(), "3000000", item, new(42), "jpn,eng,fre", Here);
            Assert(converted.Converted && converted.PlayMethod == "Transcode" && converted.Bitrate == 3_000_000 && converted.Audio == 1 && converted.Burned is null && measures == 0, "Converted under the bitrate chosen, nothing measured");
            Assert(converted.Location.StartsWith("http://jellyfin.example.com/jellyfin/videos/item-1/master.m3u8?", StringComparison.Ordinal) && !converted.Location.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) && converted.Authorization is not null, $"On the server, under its path, without the token: {converted.Location}");
            Assert(converted.PlaySessionId == "session-2" && string.Join(",", converted.Reasons) == "ContainerBitrateExceedsLimit" && requests.Count == 1 && requests[0]["StartTimeTicks"]!.GetValue<long>() == 420_000_000, "One request, its session and reasons");

            // « Automatique » away from home: measured once for both titles, the file as it is when the line holds it.
            var speed = new ConnectionSpeed(); requests.Clear();
            var fast = await ServerStreams.OpenAsync(away, speed, StreamQuality.Auto, item with { MediaSources = [new() { Id = "source-1", Bitrate = 10_000_000 }] }, new(0), "jpn,fre,eng", Here);
            var again = await ServerStreams.OpenAsync(away, speed, StreamQuality.Auto, item with { MediaSources = [new() { Id = "source-1", Bitrate = 10_000_000 }] }, new(0), "jpn,fre,eng", Here);
            Assert(measures == 1 && speed.Last is { Speed: > 15_000_000 } && !fast.Converted && !again.Converted && requests[0]["MaxStreamingBitrate"]!.GetValue<long>() == StreamQuality.FullBitrate, $"Measured once ({measures}), full quality: {requests[0]["MaxStreamingBitrate"]}");

            // A conversion carries the series' audio, and its picture subtitles drawn in; the languages otherwise.
            requests.Clear();
            var memory = new TrackMemory("fre", null, true, "fre", null, false);
            var remembered = await ServerStreams.OpenAsync(away, new ConnectionSpeed(), "3000000", item, new(0, Memory: memory), "jpn,eng", Here);
            Assert(remembered.Audio == 2 && remembered.Burned == 3 && requests.Count == 2 && requests[1]["AudioStreamIndex"]!.GetValue<int>() == 2 && requests[1]["SubtitleStreamIndex"]!.GetValue<int>() == 3, $"Asked again with the series' tracks: {remembered.Audio}, {remembered.Burned}, {requests.Count} requests");
            requests.Clear();
            var english = await ServerStreams.OpenAsync(away, new ConnectionSpeed(), "3000000", item, new(0), "jpn,eng,fre", Here);
            Assert(english.Audio == 1 && english.Burned is null && requests.Count == 1, "The settings' first language there: Jellyfin's choice already");
            requests.Clear();
            var chosen = await ServerStreams.OpenAsync(away, new ConnectionSpeed(), "3000000", item, new(0, Audio: 2, Burn: -1, Memory: memory), "jpn,eng", Here);
            Assert(chosen.Audio == 2 && chosen.Burned is null && requests.Count == 1 && requests[0]["SubtitleStreamIndex"]!.GetValue<int>() == -1, "Tracks chosen in the player win over the memory");
        });
        await Test("Qualité : sous-titres d’une conversion (à côté, sans jeton, sur ce serveur) et choix de départ", () =>
        {
            var source = Source();
            var beside = ServerStreams.Beside(source, converted: true).ToList();
            Assert(beside.Count == 1 && beside[0].Stream.Index == 4 && beside[0].Path == "/Videos/item-1/source-1/Subtitles/4/0/Stream.subrip", $"Text tracks of this server only, no token: {string.Join(", ", beside.Select(x => x.Path))}");
            Assert(!ServerStreams.Beside(source, converted: false).Any(), "The file as it is: its external files only");
            // An account without the right to convert, from 30 s: Jellyfin cuts the file's subtitle at that start (12.1).
            var external = source with { MediaStreams = [.. source.MediaStreams, new MediaStream { Index = 6, Type = "Subtitle", Codec = "subrip", IsExternal = true, IsTextSubtitleStream = true, DeliveryMethod = "External", DeliveryUrl = "/Videos/item-1/source-1/Subtitles/6/300000000/Stream.subrip?ApiKey=secret" }] };
            var own = ServerStreams.Beside(external, converted: false).Single();
            Assert(own.Path == "/Videos/item-1/source-1/Subtitles/6/0/Stream.subrip", $"The file as it is: its subtitle from its own start, {own.Path}");
            Assert(ServerStreams.Picture(source.MediaStreams[3]) && !ServerStreams.Picture(source.MediaStreams[4]), "PGS is a picture");
            var tracks = MpvLists.Tracks(ServerStreams.TrackList(source));
            Assert(tracks.Count(t => t.Type == "audio") == 2 && tracks.Count(t => t.Type == "sub") == 3 && tracks[1].Id == "2" && tracks[1].Label.Contains("Français", StringComparison.Ordinal), $"The menu's tracks: {string.Join(", ", tracks.Select(t => t.Id + " " + t.Label))}");
            Assert(ServerStreams.SubtitleFor(source, 1, "fre,eng") == 4, "English audio: the French subtitles");
            Assert(ServerStreams.SubtitleFor(source, 2, "fre,eng") == 4, "French audio: forced French ones only");
            Assert(ServerStreams.SubtitleFor(source with { MediaStreams = [.. source.MediaStreams.Where(s => s.Index != 4)] }, 2, "fre") is null, "French audio, no forced ones: none");
            Assert(ServerStreams.AudioFor(source, null, "fre,eng") == 2 && ServerStreams.AudioFor(source, null, "jpn") == 1, "Audio by the languages, else Jellyfin's default");
            return Task.CompletedTask;
        });
        await Test("Qualité : pistes d’une conversion dans le menu (une piste audio, texte à côté, images incrustées)", () =>
        {
            var stream = new ServerStream(Source(), "session-2", "http://jellyfin.example/videos/item-1/master.m3u8", "auth", true, 1, null, 3_000_000, ["ContainerBitrateExceedsLimit"]);
            var tracks = new ConvertedTracks(stream);
            Assert(string.Join(",", tracks.Audio.Select(t => t.Id)) == "1,2" && string.Join(",", tracks.Subtitles.Select(t => t.Id)) == "3,4,5", "The source's tracks, by Jellyfin's index");
            Assert(tracks.IsPicture(3) && !tracks.IsPicture(4) && tracks.Path(4) == "/Videos/item-1/source-1/Subtitles/4/0/Stream.subrip" && tracks.Path(3) is null && tracks.Path(5) is null, "Pictures drawn in, text beside (this server only)");
            Assert(tracks.StartText(null, "fre,eng") == 4 && tracks.MpvId(4) is null, "Starts with the settings' languages");
            tracks.Added(4, "1");
            Assert(tracks.MpvId(4) == "1" && tracks.Shown("1") == 4 && tracks.Shown("no") is null && !tracks.ShowsOther("1") && tracks.ShowsOther("2") && !tracks.ShowsOther("no"), "mpv's id, and a file of the person's");
            var memory = tracks.Remember("1");
            Assert(memory is { Audio: "eng", Subtitles: true, Subtitle: "fre", Forced: true } && tracks.Remember("2") is null && tracks.Remember("no") is { Subtitles: false }, $"Kept for the series: {memory}");
            Assert(tracks.StartText(memory, "eng") == 4 && tracks.StartText(memory! with { Subtitles = false }, "fre") is null, "The series' text subtitles, or none");
            Assert(tracks.StartText(new TrackMemory("eng", null, true, "fre", null, false), "fre") is null, "The series' pictures, not drawn in: none rather than other ones");
            var burned = new ConvertedTracks(stream with { Burned = 3 });
            Assert(burned.Shown("no") == 3 && burned.StartText(null, "fre") is null && burned.Remember("no") is { Subtitles: true, Subtitle: "fre", Forced: false }, "Pictures drawn in: shown, kept, no text over them");
            Assert(StreamQuality.Describe(stream) == "Converti par Jellyfin · jusqu’à 3 Mb/s · débit au-dessus de la qualité choisie" && StreamQuality.Describe(stream with { Converted = false }) == "Fichier tel quel", StreamQuality.Describe(stream));
            return Task.CompletedTask;
        });
        await Test("Coupure : un flux arrêté avant la fin est attendu puis rouvert une fois, jamais « vu »", () =>
        {
            Assert(StreamRecovery.CutShort(27.4, 40) && !StreamRecovery.CutShort(39.2, 40) && !StreamRecovery.CutShort(10, 0), "Before 90 % of the title: a cut");
            var recovery = new StreamRecovery();
            Assert(recovery.Step(serverUp: false) == RecoveryStep.Wait && recovery.Step(serverUp: true) == RecoveryStep.Reopen, "Silent: waited for; answering: reopened");
            recovery.Reopening(600);
            Assert(recovery.Step(true) == RecoveryStep.Fail && recovery.Step(false) == RecoveryStep.Wait, "Stopped again at once: cannot play (unless Jellyfin went silent)");
            recovery.Played(620);
            Assert(recovery.Step(true) == RecoveryStep.Fail, "20 s: not yet");
            recovery.Played(631);
            Assert(recovery.Step(true) == RecoveryStep.Reopen, "Past 30 s: the next cut is reopened again");
            recovery.Reopening(700); recovery.Reset();
            Assert(recovery.Step(true) == RecoveryStep.Reopen, "Another try starts over");
            Assert(!StreamRecovery.Stuck(TimeSpan.FromSeconds(14), moved: true) && StreamRecovery.Stuck(TimeSpan.FromSeconds(15), moved: true), "Played, then 15 s without data: stuck");
            Assert(!StreamRecovery.Stuck(TimeSpan.FromSeconds(30), moved: false) && StreamRecovery.Stuck(TimeSpan.FromSeconds(45), moved: false), "Opened or moved: 45 s");
            var session = new PlaybackSession();
            session.Begin(new MediaItem { Id = "item-1", Name = "Film" }, Source(), "session-1", 10, "Transcode");
            session.Failure = "cut"; session.Reported = true;
            session.Reopened(Source(), "session-2", "DirectPlay");
            var report = session.Report(80, false);
            Assert(report.PlaySessionId == "session-2" && report.PlayMethod == "DirectPlay" && session.Reported && session.Failure is null && session.Position == 10, "Reopened: the same playback, under its new session");
            return Task.CompletedTask;
        });
    }

    private static object Answer(bool direct, string? url) => new
    {
        PlaySessionId = "session-2",
        MediaSources = new[] { new { Id = "source-1", Protocol = "File", Path = "/media/film.mkv", Bitrate = 25_000_000, SupportsDirectPlay = direct, TranscodingUrl = url, DefaultAudioStreamIndex = 1, MediaStreams = Streams } },
    };
    private static readonly object[] Streams =
    [
        new { Index = 0, Type = "Video", Codec = "hevc" },
        new { Index = 1, Type = "Audio", Codec = "eac3", Language = "eng", Channels = 6 },
        new { Index = 2, Type = "Audio", Codec = "ac3", Language = "fre", Channels = 6 },
        new { Index = 3, Type = "Subtitle", Codec = "PGSSUB", Language = "fre", IsTextSubtitleStream = false, DeliveryMethod = "Encode" },
        new { Index = 4, Type = "Subtitle", Codec = "subrip", Language = "fre", IsTextSubtitleStream = true, IsForced = true, DeliveryMethod = "External", DeliveryUrl = "/Videos/item-1/source-1/Subtitles/4/0/Stream.subrip?ApiKey=secret" },
        new { Index = 5, Type = "Subtitle", Codec = "subrip", Language = "eng", IsTextSubtitleStream = true, DeliveryMethod = "External", DeliveryUrl = "http://elsewhere.example/x.srt" },
    ];
    private static MediaSource Source() => JsonSerializer.Deserialize<MediaSource>(JsonSerializer.Serialize(new { Id = "source-1", DefaultAudioStreamIndex = 1, MediaStreams = Streams }), Json.Options)!;
}
