import Foundation
import Testing
@testable import Mira

private final class Fixtures {
    /// A file of tests/fixtures, copied into the test bundle (project.yml).
    static func json(_ name: String) throws -> [String: Any] {
        let url = try #require(Bundle(for: Fixtures.self).url(forResource: name, withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }
}

@Suite("Lecture")
struct PlaybackTests {
    private func source(_ fields: (inout MediaSource) -> Void = { _ in }) -> MediaSource {
        var source = MediaSource(id: "abcdef0123456789abcdef0123456789", container: "mov,mp4,m4a", supportsDirectPlay: true)
        fields(&source)
        return source
    }

    private func subtitle(_ index: Int, _ language: String, text: Bool = true, codec: String = "subrip") -> MediaStream {
        MediaStream(index: index, type: "Subtitle", codec: codec, language: language, isTextSubtitleStream: text)
    }

    @Test("Qualité : les mêmes cas que Mira web, Windows et Mac (bitrate.json)")
    func knownBitrate() throws {
        let fixture = try Fixtures.json("bitrate")
        let now = Date()
        for case let entry as [Any] in try #require(fixture["known"] as? [Any]) {
            let choice = (entry[0] as? String) ?? String((entry[0] as? Int) ?? 0)
            let measure = (entry[2] as? [Double]).map { (at: now.addingTimeInterval(-$0[0] * 60), speed: $0[1]) }
            let expected = entry[4] as? Int
            let found = PlaybackPlan.knownBitrate(choice, atHome: entry[1] as? Bool ?? false, measure: measure, now: now, fileBitrate: entry[3] as? Int ?? 0)
            #expect(found == expected, "\(entry)")
        }
        for case let entry as [Any] in try #require(fixture["home"] as? [Any]) {
            let host = try #require(entry[0] as? String)
            let server = try #require(URL(string: "http://\(host):8096"))
            #expect(ServerAddress.isHome(server) == (entry[1] as? Bool), "\(host)")
        }
    }

    @Test("La cascade : tel quel, vidéo convertie, tout converti")
    func stages() {
        #expect(PlaybackPlan.stageRequest(0) == .init(lastResort: false, directPlay: true, directStream: true, videoCopy: true, audioCopy: true))
        #expect(PlaybackPlan.stageRequest(1) == .init(lastResort: false, directPlay: false, directStream: false, videoCopy: false, audioCopy: true))
        #expect(PlaybackPlan.stageRequest(2) == .init(lastResort: true, directPlay: false, directStream: false, videoCopy: false, audioCopy: false))
        #expect(PlaybackPlan.nextStage(0) == 1)
        #expect(PlaybackPlan.nextStage(2) == nil)
        #expect(PlaybackPlan.uncappedBitrate(source { $0.supportsDirectPlay = false }, stage: 0, bitrate: 8_000_000) == PlaybackPlan.fullBitrate)
        #expect(PlaybackPlan.uncappedBitrate(source(), stage: 0, bitrate: 8_000_000) == nil)
        #expect(PlaybackPlan.uncappedBitrate(source { $0.supportsDirectPlay = false }, stage: 1, bitrate: 8_000_000) == nil)
    }

    @Test("Le fichier tel quel, en MP4")
    func directPlay() throws {
        let stream = try #require(PlaybackPlan.stream(source { $0.eTag = "e1" }, itemId: "item", deviceId: "phone 1", stage: 0, subtitleIndex: -1, miraHls: true))
        #expect(stream.path == "Videos/item/stream.mp4?Static=true&mediaSourceId=abcdef0123456789abcdef0123456789&deviceId=phone%201&Tag=e1")
        #expect(stream.playMethod == "DirectPlay")
        #expect(!stream.hls)
        // Past stage 0, never the file as it is.
        #expect(PlaybackPlan.stream(source(), itemId: "item", deviceId: "d", stage: 1, subtitleIndex: -1, miraHls: true) == nil)
    }

    @Test("Une conversion pour le lecteur d'Apple : les sous-titres texte dans le flux HLS, par l'extension en fMP4")
    func conversion() throws {
        let address = "/videos/abcdef01-2345-6789-abcd-ef0123456789/master.m3u8?DeviceId=d&MediaSourceId=abc&VideoCodec=h264&SegmentContainer=mp4&SubtitleMethod=Encode&TranscodeReasons=ContainerNotSupported"
        let file = source {
            $0.supportsDirectPlay = false
            $0.transcodingUrl = address
            $0.transcodingSubProtocol = "hls"
            $0.mediaStreams = [subtitle(2, "fre"), subtitle(3, "eng"), subtitle(4, "eng", text: false, codec: "PGSSUB")]
        }
        let stream = try #require(PlaybackPlan.stream(file, itemId: "item", deviceId: "d", stage: 0, subtitleIndex: 3, miraHls: true))
        #expect(stream.hls && stream.inband)
        #expect(stream.playMethod == "DirectStream")
        #expect(stream.path.hasPrefix("Mira/hls/abcdef01-2345-6789-abcd-ef0123456789/master.m3u8?"))
        #expect(!stream.path.contains("SubtitleMethod=Encode"))
        #expect(stream.path.contains("&SubtitleStreamIndex=3&SubtitleMethod=Hls&MiraNames="))
        // Without the extension, Jellyfin's own playlists.
        let plain = try #require(PlaybackPlan.stream(file, itemId: "item", deviceId: "d", stage: 0, subtitleIndex: -1, miraHls: false))
        #expect(plain.path.hasPrefix("/videos/") && plain.path.hasSuffix("&SubtitleMethod=Hls"))
        // Pictures chosen: Jellyfin draws them into the video, nothing in the stream.
        let burned = try #require(PlaybackPlan.stream(file, itemId: "item", deviceId: "d", stage: 0, subtitleIndex: 4, miraHls: true))
        #expect(burned.path == address && !burned.inband)
    }

    @Test("Vidéo copiée ou convertie, d'après TranscodeReasons")
    func copies() {
        #expect(PlaybackPlan.copiesVideo("/videos/x/master.m3u8?VideoCodec=h264&TranscodeReasons=AudioCodecNotSupported"))
        #expect(!PlaybackPlan.copiesVideo("/videos/x/master.m3u8?TranscodeReasons=VideoCodecNotSupported,AudioCodecNotSupported"))
        #expect(!PlaybackPlan.copiesVideo("/videos/x/master.m3u8?allowVideoStreamCopy=false"))
    }

    @Test("Reprise : là où le titre s'est arrêté, sauf presque fini ou depuis le début")
    func start() {
        var film = Item(id: "f")
        film.runTimeTicks = 1000 * ItemText.ticksPerSecond
        film.userData = UserData(playbackPositionTicks: 300 * ItemText.ticksPerSecond)
        #expect(PlaybackPlan.startTicks(film, fromStart: false) == 300 * ItemText.ticksPerSecond)
        #expect(PlaybackPlan.startTicks(film, fromStart: true) == 0)
        film.userData?.playbackPositionTicks = 980 * ItemText.ticksPerSecond
        #expect(PlaybackPlan.startTicks(film, fromStart: false) == 0)
    }

    @Test("Noms des pistes : langue, détails quand deux se ressemblent, forcés")
    func trackNames() {
        let english = Locale(identifier: "en_US")
        let file = source {
            $0.mediaStreams = [
                MediaStream(index: 1, type: "Audio", codec: "eac3", language: "jpn", channels: 6),
                MediaStream(index: 2, type: "Audio", codec: "aac", language: "fre", channels: 2),
                subtitle(3, "fre"), subtitle(4, "fre", codec: "ass"),
                MediaStream(index: 5, type: "Subtitle", codec: "subrip", language: "eng", title: "Signs", isForced: true, isTextSubtitleStream: true),
            ]
        }
        #expect(TrackNames.names(file.audio, locale: english) == [1: "Japanese", 2: "French"])
        #expect(TrackNames.menuNames(file, locale: english) == [3: "French (SRT)", 4: "French (ASS)", 5: "Signs"])
        #expect(TrackNames.text(file.subtitles[2], number: 3, locale: english).details.hasPrefix("English · SRT · "))
        let next = [MediaStream(index: 7, type: "Subtitle", language: "fre", isForced: true), MediaStream(index: 8, type: "Subtitle", language: "fre")]
        #expect(TrackNames.same(file.subtitles[0], in: next)?.index == 8)
        #expect(TrackNames.same(nil, in: next) == nil)
    }

    @Test("Le profil d'appareil : MP4 tel quel, HLS fMP4 H.264 d'abord, sous-titres texte dans le flux")
    func profile() throws {
        let profile = PlaybackProfile.make(bitrate: 20_000_000, device: .init(hevc: true, av1: false, hdr: true, hevcLevel: 156))
        #expect(profile["MaxStreamingBitrate"] as? Int == 20_000_000)
        let hls = try #require((profile["TranscodingProfiles"] as? [[String: Any]])?.first)
        #expect(hls["Protocol"] as? String == "hls" && hls["Container"] as? String == "mp4" && hls["VideoCodec"] as? String == "h264,hevc")
        let subtitles = try #require(profile["SubtitleProfiles"] as? [[String: Any]])
        #expect(subtitles.first?["Method"] as? String == "Hls")
        #expect(!subtitles.contains { $0["Method"] as? String == "External" })
        #expect(try JSONSerialization.data(withJSONObject: profile).count > 500)
        #expect(try JSONSerialization.data(withJSONObject: PlaybackProfile.lastResort(bitrate: 4_000_000)).count > 300)
    }
}

@Suite("Minuterie")
struct SleepTimerTests {
    @Test("Les mêmes cas que Mira web, Windows et Mac (sleep.json)")
    func shared() throws {
        let fixture = try Fixtures.json("sleep")
        for case let entry as [Any] in try #require(fixture["durations"] as? [Any]) {
            let minutes = try #require(entry[0] as? Int)
            #expect(SleepTimer.duration(minutes) == entry[1] as? String, "\(entry)")
        }
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        for case let entry as [Any] in try #require(fixture["status"] as? [Any]) {
            var timer = SleepTimer()
            timer.choose(entry[0] as? Int, now: start)
            let seconds = try #require(entry[1] as? Double)
            let now = start.addingTimeInterval(seconds)
            let due = timer.due(now)
            let status: String? = switch timer.status(now) {
            case nil: nil
            case .endOfEpisode: "Arrêt à la fin de l’épisode"
            case let .pause(left): "Pause dans \(left)"
            }
            #expect(status == entry[2] as? String, "\(entry)")
            #expect(due == (entry[3] as? Bool), "\(entry)")
        }
    }

    @Test("Ne met en pause qu'une fois")
    func once() {
        var timer = SleepTimer()
        let start = Date()
        timer.choose(15, now: start)
        #expect(timer.due(start.addingTimeInterval(900)))
        #expect(!timer.due(start.addingTimeInterval(901)))
        #expect(timer.chosen == nil)
    }
}

@Suite("Coupures")
struct StallWatchTests {
    private struct Look {
        var t: Int
        var at: Double
        var expecting = true
        var ready = true
        var seeking = false
    }

    /// The looks the player takes, one a second here; the second the stream is stuck, or nil (as stream.test.mjs).
    private func watch(_ looks: [Look]) -> Int? {
        var last = StallWatch()
        let start = Date(timeIntervalSince1970: 1_800_000_000)
        for look in looks {
            last = last.next(now: start.addingTimeInterval(Double(look.t)), at: look.at, expecting: look.expecting, ready: look.ready, seeking: look.seeking)
            if last.stuck { return look.t }
        }
        return nil
    }

    private func seconds(_ from: Int, _ to: Int, _ look: (Int) -> Look) -> [Look] { (from...to).map(look) }

    @Test("Un flux qui avance, ou qu'on n'attend pas, n'est jamais bloqué")
    func playing() {
        #expect(watch(seconds(1, 120) { Look(t: $0, at: Double($0)) }) == nil)
        #expect(watch(seconds(1, 300) { Look(t: $0, at: 12, expecting: false) }) == nil)
    }

    @Test("Lu, puis immobile 15 s")
    func still() {
        let looks = seconds(1, 10) { Look(t: $0, at: Double($0)) } + seconds(11, 60) { Look(t: $0, at: 10, ready: false) }
        #expect(watch(looks) == 11 + Int(StallWatch.stallLimit))
    }

    @Test("Ouvert ou déplacé, rien lu depuis : 45 s")
    func opened() {
        #expect(watch(seconds(1, 120) { Look(t: $0, at: $0 < 3 ? 0 : 600, ready: false) }) == 1 + Int(StallWatch.startLimit))
        #expect(watch(seconds(1, 40) { Look(t: $0, at: 600, ready: false) }) == nil)
        #expect(watch(seconds(1, 120) { Look(t: $0, at: $0 < 5 ? Double($0) : 300, seeking: $0 >= 5) }) == 5 + Int(StallWatch.startLimit))
    }

    @Test("Après une pause, le délai court ; le temps en pause ne compte pas")
    func afterPause() {
        let looks = seconds(1, 10) { Look(t: $0, at: Double($0)) } + seconds(11, 200) { Look(t: $0, at: 10, expecting: false) }
            + seconds(201, 260) { Look(t: $0, at: 10, ready: false) }
        #expect(watch(looks) == 201 + Int(StallWatch.stallLimit))
    }

    @Test("Le serveur interrogé, muet ou qui répond ; une fin avant 90 % n'en est pas une")
    func steps() {
        #expect(StallWatch.step(up: false, reopened: false) == .wait)
        #expect(StallWatch.step(up: false, reopened: true) == .wait)
        #expect(StallWatch.step(up: true, reopened: false) == .reopen)
        #expect(StallWatch.step(up: true, reopened: true) == .fail)
        #expect(StallWatch.forgiven > StallWatch.stallLimit)
        #expect(StallWatch.cut(position: 500, total: 1000))
        #expect(!StallWatch.cut(position: 950, total: 1000))
        #expect(!StallWatch.cut(position: 10, total: 0))
    }
}
