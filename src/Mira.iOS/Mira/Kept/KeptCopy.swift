import CryptoKit
import Foundation

/// A film or an episode kept on this iPhone (« Keep on this iPhone »), to watch without Jellyfin: a video the iPhone
/// plays, its text subtitles and its picture, with what Mira knew of the title. Described by `mira-copy.json` in its
/// folder (OfflineCopies of Windows and Mac), so that the folder alone is enough.
struct KeptCopy: Codable, Sendable, Identifiable, Hashable {
    /// The file as it is (Jellyfin's own, when the iPhone plays it: MP4, MOV), or Jellyfin's conversion of it into an
    /// MP4 the iPhone plays (MKV, AVI, a codec it does not read), fetched piece by piece from its HLS stream.
    enum Kind: String, Codable, Sendable { case file, converted }

    var item: Item
    /// The file as Jellyfin described it (its tracks), once known.
    var source: MediaSource?
    /// Decided at the first fetch.
    var kind: Kind?
    /// The audio track a conversion carries (Jellyfin's index); a file keeps all of its own.
    var audioIndex: Int?
    /// The video's size in bytes: the file's, or an estimate of the conversion's until it is complete.
    var size: Int64 = 0
    /// Bytes of the video fetched so far, and the pieces they are (ranges of the file, or the conversion's segments).
    var bytes: Int64 = 0
    var pieces = 0
    var total = 0
    var complete = false
    var subtitles: [KeptSubtitle] = []
    var hasPicture = false
    var added: Date
    /// Why it stopped and waits for « Try again » (refused, no room left), in words for the person.
    var failure: String?
    /// Watched here while Jellyfin heard nothing: told to it once it answers (KeptRules.progressStep).
    var watched: KeptWatch?
    /// The title's other files, when Jellyfin put several together (« cd1 », « cd2 »…: MovieParts): each a copy of its
    /// own in a folder of the title's (`part-2`, `part-3`…), fetched before the title's own file, which is its first: the
    /// title is complete only whole. Nil for a title in one file, and for one kept before (its first file alone).
    var parts: [KeptCopy]?

    var id: String { item.id }

    /// The files the title is made of: its other parts, then its own.
    var files: [KeptCopy] { (parts ?? []) + [self] }

    /// What the title takes on this iPhone, every file of it.
    var totalSize: Int64 { files.reduce(0) { $0 + $1.size } }

    /// « video.mp4 », « video.mov »: AVPlayer reads a file by its extension.
    var videoName: String { kind == .file ? "video." + KeptRules.videoExtension(source) : "video.mp4" }

    init(item: Item, added: Date) {
        self.item = item
        self.added = added
    }
}

/// A text subtitle kept beside the video, as WebVTT (Mira draws it: SubtitleCues).
struct KeptSubtitle: Codable, Sendable, Hashable {
    var file: String
    var index: Int
}

/// Where a title kept here was left while Jellyfin could not hear it, and when.
struct KeptWatch: Codable, Sendable, Hashable {
    var ticks: Int64
    var finished: Bool
    var at: Date
}

/// The copies on this iPhone, in Application Support (never in a backup: Jellyfin has them), one folder per account
/// and one per title: `Kept/<account>/<title>/mira-copy.json`.
enum KeptStore {
    static let manifest = "mira-copy.json"

    static var root: URL {
        let base = (try? FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true))
            ?? URL.applicationSupportDirectory
        return base.appending(path: "Kept", directoryHint: .isDirectory)
    }

    /// An account's folder, named by a digest of its server and user (no server name on the disk).
    static func account(serverId: String, userId: String) -> String {
        let digest = SHA256.hash(data: Data((serverId.lowercased().filter { $0 != "-" } + "/" + userId.lowercased()).utf8))
        return String(digest.map { String(format: "%02x", $0) }.joined().prefix(16))
    }

    static func folder(account: String) -> URL { root.appending(path: account, directoryHint: .isDirectory) }

    static func folder(account: String, item: String) -> URL { folder(account: account).appending(path: safeName(item), directoryHint: .isDirectory) }

    /// The account's copies, newest first; a folder that does not read is left out.
    static func list(account: String) -> [KeptCopy] {
        let folder = folder(account: account)
        let titles = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        return titles.compactMap(read).sorted { $0.added > $1.added }
    }

    static func read(_ folder: URL) -> KeptCopy? {
        guard let data = try? Data(contentsOf: folder.appending(path: manifest)), let copy = try? decoder.decode(KeptCopy.self, from: data),
              !copy.item.id.isEmpty, safeName(copy.item.id) == folder.lastPathComponent,
              copy.subtitles.allSatisfy({ safeName($0.file) == $0.file }),
              (copy.parts ?? []).allSatisfy({ part in !part.item.id.isEmpty && part.parts == nil && part.subtitles.allSatisfy { safeName($0.file) == $0.file } })
        else { return nil }
        return copy
    }

    /// The folder of a title's other file, in the title's own: « part-2 » for the first of them (its second part).
    static func partFolder(_ index: Int) -> String { "part-\(index + 2)" }

    /// Written whole or not at all: a manifest cut by a crash would lose the copy.
    static func save(_ copy: KeptCopy, account: String) throws {
        let folder = folder(account: account, item: copy.id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        excludeFromBackup()
        try encoder.encode(copy).write(to: folder.appending(path: manifest), options: .atomic)
    }

    static func delete(_ id: String, account: String) {
        try? FileManager.default.removeItem(at: folder(account: account, item: id))
    }

    /// Every account's copies but this one's (nil: all of them): another account signed in, or signed out.
    static func erase(keeping account: String?) {
        let accounts = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        for folder in accounts where folder.lastPathComponent != account { try? FileManager.default.removeItem(at: folder) }
    }

    /// What the account's copies take on this iPhone, partial ones included.
    static func bytes(account: String) -> Int64 {
        let folder = folder(account: account)
        guard let files = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.fileSizeKey]) else { return 0 }
        var total: Int64 = 0
        for case let file as URL in files { total += Int64((try? file.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0) }
        return total
    }

    /// Room left for what the person keeps (iOS frees caches for it first); nil when it can't be read.
    static func freeSpace() -> Int64? {
        let values = try? root.deletingLastPathComponent().resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage
    }

    private static func excludeFromBackup() {
        var url = root
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? url.setResourceValues(values)
    }

    static func safeName(_ name: String) -> String {
        let safe = String(name.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) && $0.isASCII || "._-".unicodeScalars.contains($0) }.map(Character.init))
        return safe.isEmpty || safe.hasPrefix(".") ? "_" : safe
    }

    static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }

    static var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}

/// What « Keep on this iPhone » decides, as plain functions of their inputs, for the tests (OfflineCopies of Windows
/// and Mac for the next episode and the order).
enum KeptRules {
    /// Kept free beyond what is fetched, so that a copy never fills the iPhone.
    static let reserve: Int64 = 1_500_000_000
    /// A file comes in ranges of this size: each one fetched again alone when the connection drops.
    static let chunk: Int64 = 16 << 20
    /// Played this far, a title is watched (Jellyfin's own limit).
    static let watchedShare = 0.9

    /// The file as it is when Jellyfin lets the iPhone play it so, else its conversion; nil when it offers neither.
    static func kind(_ source: MediaSource) -> KeptCopy.Kind? {
        if source.supportsDirectPlay == true, source.transcodingUrl == nil { return .file }
        if let address = source.transcodingUrl, source.transcodingSubProtocol?.lowercased() == "hls" || address.range(of: ".m3u8", options: .caseInsensitive) != nil {
            return .converted
        }
        return nil
    }

    /// The extension AVPlayer recognizes the file by: MP4 among Jellyfin's containers first.
    static func videoExtension(_ source: MediaSource?) -> String {
        let containers = (source?.container ?? "").lowercased().split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
        if containers.contains("mp4") { return "mp4" }
        if containers.contains("m4v") { return "m4v" }
        if containers.contains("mov") { return "mov" }
        return "mp4"
    }

    /// The bytes a copy will take: the file's size, or for a conversion the larger of the file and its bitrate over
    /// its length (the video is copied when the iPhone plays it, converted otherwise).
    static func estimate(_ source: MediaSource, kind: KeptCopy.Kind, runTimeTicks: Int64?) -> Int64 {
        let file = source.size ?? 0
        guard kind == .converted else { return file }
        let seconds = Double(runTimeTicks ?? source.runTimeTicks ?? 0) / Double(ItemText.ticksPerSecond)
        let streamed = Int64(Double(source.bitrate ?? 0) / 8 * seconds)
        return max(file, streamed)
    }

    /// Room enough to fetch `needed` more bytes and still keep the reserve.
    static func fits(_ needed: Int64, free: Int64?) -> Bool {
        guard let free else { return true }
        return needed + reserve <= free
    }

    /// The ranges of a file of `size` bytes, in order.
    static func ranges(size: Int64, chunk: Int64 = chunk) -> [ClosedRange<Int64>] {
        guard size > 0, chunk > 0 else { return [] }
        var ranges: [ClosedRange<Int64>] = [], start: Int64 = 0
        while start < size {
            ranges.append(start...min(size - 1, start + chunk - 1))
            start += chunk
        }
        return ranges
    }

    /// The total size a 206 tells (« bytes 0-1023/52428800 »).
    static func rangeTotal(_ header: String?) -> Int64? {
        guard let header, let slash = header.lastIndex(of: "/") else { return nil }
        return Int64(header[header.index(after: slash)...].trimmingCharacters(in: .whitespaces))
    }

    /// The stream of a master playlist: its first variant's address, read against the playlist's.
    static func variant(_ master: String, base: URL) -> URL? {
        for line in master.split(whereSeparator: \.isNewline).map({ $0.trimmingCharacters(in: .whitespaces) }) where !line.isEmpty && !line.hasPrefix("#") {
            return URL(string: line, relativeTo: base)?.absoluteURL
        }
        return nil
    }

    /// A media playlist's pieces in order: its init segment (EXT-X-MAP of an fMP4 stream), then each segment. Nil when
    /// it is not a whole one (no end: a live stream, or cut).
    static func segments(_ playlist: String, base: URL) -> (initial: URL?, segments: [URL])? {
        var initial: URL?, segments: [URL] = [], ended = false
        for line in playlist.split(whereSeparator: \.isNewline).map({ $0.trimmingCharacters(in: .whitespaces) }) where !line.isEmpty {
            if line.hasPrefix("#EXT-X-MAP:") {
                if let start = line.range(of: "URI=\"")?.upperBound, let end = line[start...].firstIndex(of: "\"") {
                    initial = URL(string: String(line[start..<end]), relativeTo: base)?.absoluteURL
                }
            } else if line.hasPrefix("#EXT-X-ENDLIST") {
                ended = true
            } else if !line.hasPrefix("#"), let url = URL(string: line, relativeTo: base)?.absoluteURL {
                segments.append(url)
            }
        }
        return ended && !segments.isEmpty ? (initial, segments) : nil
    }

    /// A title's other files to keep with it, from Jellyfin's list of its parts (Videos/{id}/AdditionalParts): the title
    /// itself, if Jellyfin gives it again, left out, as MovieParts does.
    static func parts(of title: KeptCopy, others: [Item]) -> [KeptCopy] {
        var seen: Set = [title.id]
        return others.filter { seen.insert($0.id).inserted }.map { KeptCopy(item: $0, added: title.added) }
    }

    /// The copy of a file by its id, a title kept here or one of its other files: with the title it belongs to, and its
    /// place among the title's parts (nil: the title's own file).
    static func locate(_ id: String, in copies: [KeptCopy]) -> (title: KeptCopy, copy: KeptCopy, part: Int?)? {
        for title in copies {
            if title.id == id { return (title, title, nil) }
            if let parts = title.parts, let at = parts.firstIndex(where: { $0.id == id }) { return (title, parts[at], at) }
        }
        return nil
    }

    /// The episode after this one among the copies, in the order of the series: what plays next when Jellyfin can't
    /// say (OfflineCopies.NextEpisode).
    static func next(after episode: Item, in copies: [KeptCopy]) -> KeptCopy? {
        guard episode.isEpisode, let series = episode.seriesId else { return nil }
        return copies.filter { $0.complete && $0.item.isEpisode && $0.item.seriesId == series && order($0.item) > order(episode) }
            .min { order($0.item) < order($1.item) }
    }

    private static func order(_ item: Item) -> (Int, Int) { (item.parentIndexNumber ?? 0, item.indexNumber ?? 0) }

    /// The copies as « On this iPhone » lists them: a series' episodes together and in order, by the newest kept of each
    /// series or film (OfflineCopies.Ordered).
    static func ordered(_ copies: [KeptCopy]) -> [KeptCopy] {
        func group(_ copy: KeptCopy) -> String { copy.item.seriesId ?? copy.item.id }
        var newest: [String: Date] = [:]
        for copy in copies { newest[group(copy)] = max(newest[group(copy)] ?? .distantPast, copy.added) }
        return copies.sorted { a, b in
            let (ga, gb) = (group(a), group(b))
            if ga != gb { return newest[ga]! != newest[gb]! ? newest[ga]! > newest[gb]! : ga < gb }
            return order(a.item) < order(b.item)
        }
    }

    /// Watched to its end, or far enough that Jellyfin counts it watched.
    static func finished(ticks: Int64, total: Int64?) -> Bool {
        guard let total, total > 0 else { return false }
        return Double(ticks) >= Double(total) * watchedShare
    }

    enum ProgressStep: Equatable {
        /// Watched again since on another device: Jellyfin's is newer, nothing is sent.
        case drop
        case played
        case position(Int64)
    }

    /// What to tell Jellyfin of a title watched here while it heard nothing: nothing when it was watched elsewhere
    /// since, else watched, or where it was left.
    static func progressStep(_ watch: KeptWatch, serverLastPlayed: Date?) -> ProgressStep {
        if let serverLastPlayed, serverLastPlayed > watch.at { return .drop }
        return watch.finished ? .played : .position(watch.ticks)
    }

    /// The film of a copy whose every piece came, already in its place (the part moved there, Mira closed before its
    /// subtitles): a video of the very length fetched. Nothing to fetch nor move again, only the subtitles.
    static func videoInPlace(_ copy: KeptCopy, videoSize: Int64?) -> Bool {
        copy.kind != nil && copy.total > 0 && copy.pieces >= copy.total && copy.bytes > 0 && videoSize == copy.bytes
    }

    /// Where a title was left here, as Jellyfin keeps it (UserItems/{id}/UserData). Its watched mark is left as Jellyfin
    /// has it: a title watched before and watched again halfway stays watched, with its new position, as Jellyfin does
    /// for a stream (checked on 12.1) and as Windows and Mac send it; `"Played": false` took the mark off everywhere.
    static func positionBody(ticks: Int64, at: Date) -> [String: Any] {
        ["PlaybackPositionTicks": max(0, ticks), "LastPlayedDate": at.formatted(.iso8601)]
    }

    /// How far a copy came, 0 to 1: a title in several files, each file its share.
    static func fraction(_ copy: KeptCopy) -> Double {
        if copy.complete { return 1 }
        let files = copy.files
        let done = files.reduce(0.0) { sum, file in
            sum + (file.complete ? 1 : file.total > 0 ? Double(file.pieces) / Double(file.total) : 0)
        }
        return min(0.99, done / Double(files.count))
    }
}
