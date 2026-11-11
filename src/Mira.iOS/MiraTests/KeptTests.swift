import Foundation
import Testing
@testable import Mira

/// « Keep on this iPhone » (Kept/): what kind of copy a title gets, its pieces, the next episode and the order of the
/// list, what is told to Jellyfin of what was watched here, and its manifest.
@Suite("Garder sur cet iPhone")
struct KeptTests {
    private let base = URL(string: "http://jellyfin:8096/videos/abc/master.m3u8?ApiKey=k")!

    private func episode(_ id: String, series: String = "s", season: Int, number: Int) -> Item {
        Item(id: id, name: "E\(number)", type: "Episode", seriesId: series, seriesName: "Série", parentIndexNumber: season, indexNumber: number)
    }

    private func copy(_ item: Item, added: TimeInterval, complete: Bool = true) -> KeptCopy {
        var copy = KeptCopy(item: item, added: Date(timeIntervalSinceReferenceDate: added))
        copy.complete = complete
        return copy
    }

    @Test("Le fichier tel quel quand l'iPhone le lit, sinon la conversion de Jellyfin")
    func kind() {
        #expect(KeptRules.kind(MediaSource(container: "mov,mp4,m4a", supportsDirectPlay: true)) == .file)
        #expect(KeptRules.kind(MediaSource(container: "mkv", supportsDirectPlay: false, transcodingUrl: "/videos/a/master.m3u8?x=1", transcodingSubProtocol: "hls")) == .converted)
        #expect(KeptRules.kind(MediaSource(container: "mkv", supportsDirectPlay: false, transcodingUrl: "/videos/a/stream.ts", transcodingSubProtocol: "http")) == nil)
        #expect(KeptRules.kind(MediaSource(container: "mkv", supportsDirectPlay: false)) == nil)
    }

    @Test("L'extension que lit AVPlayer")
    func videoExtension() {
        #expect(KeptRules.videoExtension(MediaSource(container: "mov,mp4,m4a")) == "mp4")
        #expect(KeptRules.videoExtension(MediaSource(container: "mov")) == "mov")
        #expect(KeptRules.videoExtension(MediaSource(container: "m4v")) == "m4v")
        #expect(KeptRules.videoExtension(nil) == "mp4")
        var converted = KeptCopy(item: Item(id: "a"), added: .now)
        converted.kind = .converted
        converted.source = MediaSource(container: "mov")
        #expect(converted.videoName == "video.mp4")
    }

    @Test("Les morceaux d'un fichier, et sa taille dite par un 206")
    func ranges() {
        let ranges = KeptRules.ranges(size: 40, chunk: 16)
        #expect(ranges == [0...15, 16...31, 32...39])
        #expect(KeptRules.ranges(size: 16, chunk: 16) == [0...15])
        #expect(KeptRules.ranges(size: 0).isEmpty)
        #expect(KeptRules.rangeTotal("bytes 0-1023/52428800") == 52_428_800)
        #expect(KeptRules.rangeTotal("bytes 0-1023/*") == nil)
        #expect(KeptRules.rangeTotal(nil) == nil)
    }

    @Test("La place : la copie et la réserve, ou rien quand la place ne se lit pas")
    func room() {
        #expect(KeptRules.fits(1_000_000_000, free: 2_500_000_000))
        #expect(!KeptRules.fits(1_000_000_001, free: 2_500_000_000))
        #expect(KeptRules.fits(10_000_000_000, free: nil))
        let conversion = MediaSource(bitrate: 8_000_000, size: 1_000_000)
        // 8 Mb/s for 10 min: 600 MB, more than the file.
        #expect(KeptRules.estimate(conversion, kind: .converted, runTimeTicks: 600 * ItemText.ticksPerSecond) == 600_000_000)
        #expect(KeptRules.estimate(conversion, kind: .file, runTimeTicks: 600 * ItemText.ticksPerSecond) == 1_000_000)
    }

    @Test("La conversion : la liste principale, son segment d'ouverture et ses segments, entière seulement")
    func playlists() throws {
        let master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=300000\nmain.m3u8?ApiKey=k&x=1\n"
        let variant = try #require(KeptRules.variant(master, base: base))
        #expect(variant.absoluteString == "http://jellyfin:8096/videos/abc/main.m3u8?ApiKey=k&x=1")
        let playlist = """
        #EXTM3U
        #EXT-X-TARGETDURATION:3
        #EXT-X-MAP:URI="hls1/main/-1.mp4?ApiKey=k"
        #EXTINF:3,
        hls1/main/0.mp4?ApiKey=k
        #EXTINF:3,
        hls1/main/1.mp4?ApiKey=k
        #EXT-X-ENDLIST
        """
        let pieces = try #require(KeptRules.segments(playlist, base: variant))
        #expect(pieces.initial?.absoluteString == "http://jellyfin:8096/videos/abc/hls1/main/-1.mp4?ApiKey=k")
        #expect(pieces.segments.map(\.lastPathComponent) == ["0.mp4", "1.mp4"])
        // Not ended: a live stream, or a playlist cut short.
        #expect(KeptRules.segments(playlist.replacingOccurrences(of: "#EXT-X-ENDLIST", with: ""), base: variant) == nil)
    }

    @Test("L'épisode suivant parmi les copies, dans l'ordre de la série")
    func next() {
        let copies = [
            copy(episode("e3", season: 1, number: 3), added: 1),
            copy(episode("e2", season: 1, number: 2), added: 2, complete: false),
            copy(episode("f1", season: 2, number: 1), added: 3),
            copy(episode("o1", series: "other", season: 1, number: 2), added: 4),
        ]
        #expect(KeptRules.next(after: episode("e1", season: 1, number: 1), in: copies)?.id == "e3")
        #expect(KeptRules.next(after: episode("e3", season: 1, number: 3), in: copies)?.id == "f1")
        #expect(KeptRules.next(after: episode("f1", season: 2, number: 1), in: copies) == nil)
        #expect(KeptRules.next(after: Item(id: "film", type: "Movie"), in: copies) == nil)
    }

    @Test("La liste : les épisodes d'une série ensemble et dans l'ordre, par la copie la plus récente")
    func ordered() {
        let film = copy(Item(id: "film", name: "Film", type: "Movie"), added: 5)
        let copies = [film, copy(episode("e2", season: 1, number: 2), added: 6), copy(episode("e1", season: 1, number: 1), added: 1),
                      copy(Item(id: "old", name: "Old", type: "Movie"), added: 0)]
        #expect(KeptRules.ordered(copies).map(\.id) == ["e1", "e2", "film", "old"])
    }

    @Test("Vu : à 90 % de sa durée")
    func finished() {
        #expect(KeptRules.finished(ticks: 90, total: 100))
        #expect(!KeptRules.finished(ticks: 89, total: 100))
        #expect(!KeptRules.finished(ticks: 50, total: nil))
    }

    @Test("Ce qui est dit à Jellyfin de ce qui a été regardé ici : rien quand il a été regardé ailleurs depuis")
    func progress() {
        let at = Date(timeIntervalSinceReferenceDate: 1000)
        let stopped = KeptWatch(ticks: 42, finished: false, at: at)
        #expect(KeptRules.progressStep(stopped, serverLastPlayed: nil) == .position(42))
        #expect(KeptRules.progressStep(stopped, serverLastPlayed: at.addingTimeInterval(-60)) == .position(42))
        #expect(KeptRules.progressStep(stopped, serverLastPlayed: at.addingTimeInterval(60)) == .drop)
        #expect(KeptRules.progressStep(KeptWatch(ticks: 0, finished: true, at: at), serverLastPlayed: nil) == .played)
    }

    @Test("Revu à moitié ici, un titre déjà vu le reste : la position seule part à Jellyfin")
    func positionKeepsWatched() {
        let body = KeptRules.positionBody(ticks: 42, at: Date(timeIntervalSinceReferenceDate: 1000))
        #expect(body["Played"] == nil)
        #expect(body["PlaybackPositionTicks"] as? Int64 == 42)
        #expect(body["LastPlayedDate"] as? String == "2001-01-01T00:16:40Z")
        #expect(KeptRules.positionBody(ticks: -5, at: .now)["PlaybackPositionTicks"] as? Int64 == 0)
    }

    @Test("Où en est une copie")
    func fraction() {
        var copy = KeptCopy(item: Item(id: "a"), added: .now)
        #expect(KeptRules.fraction(copy) == 0)
        copy.total = 4
        copy.pieces = 1
        #expect(KeptRules.fraction(copy) == 0.25)
        copy.pieces = 4
        #expect(KeptRules.fraction(copy) == 0.99)
        copy.complete = true
        #expect(KeptRules.fraction(copy) == 1)
    }

    @Test("Le film déjà à sa place (Mira fermée pendant ses sous-titres) : ni retéléchargé ni effacé")
    func videoInPlace() {
        var copy = KeptCopy(item: Item(id: "a"), added: .now)
        copy.kind = .file
        copy.total = 3
        copy.pieces = 3
        copy.bytes = 40
        #expect(KeptRules.videoInPlace(copy, videoSize: 40))
        // Nothing there, or another file under its name: fetched again.
        #expect(!KeptRules.videoInPlace(copy, videoSize: nil))
        #expect(!KeptRules.videoInPlace(copy, videoSize: 39))
        // Pieces still to come.
        copy.pieces = 2
        #expect(!KeptRules.videoInPlace(copy, videoSize: 40))
        copy.pieces = 3
        copy.kind = nil
        #expect(!KeptRules.videoInPlace(copy, videoSize: 40))
        copy.kind = .converted
        #expect(KeptRules.videoInPlace(copy, videoSize: 40))
    }

    @Test("mira-copy.json se relit tel quel, le dossier d'un compte ne nomme pas le serveur")
    func manifest() throws {
        var kept = KeptCopy(item: episode("e1", season: 1, number: 1), added: Date(timeIntervalSince1970: 1_800_000_000))
        kept.kind = .converted
        kept.source = MediaSource(id: "e1", container: "mkv", size: 123)
        kept.audioIndex = 2
        kept.size = 123
        kept.bytes = 123
        kept.pieces = 3
        kept.total = 3
        kept.complete = true
        kept.subtitles = [KeptSubtitle(file: "subtitle-3.vtt", index: 3)]
        kept.hasPicture = true
        kept.watched = KeptWatch(ticks: 5, finished: false, at: Date(timeIntervalSince1970: 1_800_000_100))
        let data = try KeptStore.encoder.encode(kept)
        #expect(try KeptStore.decoder.decode(KeptCopy.self, from: data) == kept)
        let account = KeptStore.account(serverId: "B6C2F1A0-D4E9-4C0A-9F3E-2D1C0B9A8F7E", userId: "f0e1")
        #expect(account.count == 16)
        #expect(account == KeptStore.account(serverId: "b6c2f1a0d4e94c0a9f3e2d1c0b9a8f7e", userId: "F0E1"))
        #expect(account != KeptStore.account(serverId: "b6c2f1a0d4e94c0a9f3e2d1c0b9a8f7e", userId: "f0e2"))
        #expect(KeptStore.safeName("../etc") == "_")
        #expect(KeptStore.safeName("a1b2-c3") == "a1b2-c3")
        #expect(KeptStore.safeName("") == "_")
    }
}
