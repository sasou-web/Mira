import BackgroundTasks
import Foundation
import Observation
import UIKit

/// Why a copy stopped and waits for « Try again ».
enum KeepError: Error, Equatable {
    /// 403: Jellyfin doesn't let this account download (its policy).
    case refused
    /// Not enough room left on this iPhone: the bytes still needed.
    case noRoom(Int64)
    /// Jellyfin offers neither the file nor a conversion this iPhone plays.
    case cannotPlay
    /// 404: no longer on Jellyfin.
    case gone

    var text: String {
        switch self {
        case .refused:
            String(localized: "Jellyfin doesn’t let this account download its videos. In Jellyfin, Dashboard → Users → this account, allow media downloads, then try again.")
        case let .noRoom(bytes):
            String(localized: "Not enough room on this iPhone: it needs \(Keeper.size(bytes)) more. Remove a title kept here or free up some space, then try again.")
        case .cannotPlay:
            String(localized: "Jellyfin has no stream this iPhone can play for this title.")
        case .gone:
            String(localized: "This title is no longer on Jellyfin.")
        }
    }
}

/// « Keep on this iPhone »: the films and episodes of the account kept here, fetched one at a time from Jellyfin, in
/// the order they were asked for, resumed where they stopped (Mira closed, the connection gone, iOS's time in the
/// background over). The file as it is when the iPhone plays it (Items/{id}/Download, in ranges), otherwise Jellyfin's
/// conversion of it (its HLS stream, the video copied when the iPhone reads it), segment after segment into one MP4;
/// then its text subtitles as WebVTT and its picture. Asked while Mira is on screen, it goes on in the background with
/// iOS's progress (BGContinuedProcessingTask) as long as iOS allows. Nothing goes anywhere but to the account's Jellyfin.
@MainActor @Observable
final class Keeper {
    /// The account's copies, complete or not, newest first.
    private(set) var copies: [KeptCopy] = []
    /// The title being fetched now.
    private(set) var current: String?
    /// Jellyfin out of reach while copies wait: they go on once a way to it comes back.
    private(set) var waiting = false
    @ObservationIgnored weak var app: AppModel?
    @ObservationIgnored private var account: String?
    @ObservationIgnored private var task: Task<Void, Never>?
    /// Bumped by each run: an older one stopping does not end the one after it.
    @ObservationIgnored private var round = 0
    @ObservationIgnored private var later: Task<Void, Never>?
    @ObservationIgnored private var continued: BGContinuedProcessingTask?
    @ObservationIgnored private var grace: UIBackgroundTaskIdentifier = .invalid
    @ObservationIgnored private var sending = false
    /// Failures of Jellyfin itself (500…) for a title, one after the other: the third one stops it.
    @ObservationIgnored private var failures: [String: Int] = [:]
    private let device: PlaybackProfile.Device

    init() {
        device = PlaybackProfile.Device.read()
    }

    enum State: Equatable {
        case notKept
        /// Asked, waiting for its turn or for Jellyfin.
        case waiting
        case fetching(Double)
        case kept
        case failed(String)
    }

    func state(_ id: String) -> State {
        guard let copy = copies.first(where: { $0.id == id }) else { return .notKept }
        if copy.complete { return .kept }
        if let failure = copy.failure { return .failed(failure) }
        return current == id ? .fetching(KeptRules.fraction(copy)) : .waiting
    }

    /// Fetching or about to: Built-in Tailscale stays on in the background meanwhile.
    var busy: Bool { task != nil }

    var kept: [KeptCopy] { copies.filter(\.complete) }

    /// The copy a title plays from, complete and on the disk; none for a friend's party's guest.
    func playable(_ id: String) -> KeptCopy? {
        guard app?.party.guest == nil, let copy = copies.first(where: { $0.id == id && $0.complete }) else { return nil }
        return FileManager.default.fileExists(atPath: videoURL(copy).path) ? copy : nil
    }

    func next(after episode: Item) -> KeptCopy? {
        guard app?.party.guest == nil else { return nil }
        return KeptRules.next(after: episode, in: copies)
    }

    func videoURL(_ copy: KeptCopy) -> URL { folder(copy).appending(path: copy.videoName) }
    func subtitleURL(_ copy: KeptCopy, _ subtitle: KeptSubtitle) -> URL { folder(copy).appending(path: subtitle.file) }
    func pictureURL(_ copy: KeptCopy) -> URL? { copy.hasPicture ? folder(copy).appending(path: "picture") : nil }

    /// What the copies take, and the room left.
    var used: Int64 { account.map(KeptStore.bytes) ?? 0 }

    private func folder(_ copy: KeptCopy) -> URL { KeptStore.folder(account: account ?? "_", item: copy.id) }

    // MARK: The account

    /// The account signed in (nil: none): its copies; another account's are removed.
    func use(_ session: Session?) {
        stop()
        guard let session else {
            account = nil
            copies = []
            return
        }
        let account = KeptStore.account(serverId: session.serverId, userId: session.userId)
        self.account = account
        KeptStore.erase(keeping: account)
        copies = KeptStore.list(account: account)
    }

    /// Signed out on this iPhone: its copies go with the account's other data.
    func signedOut() {
        stop()
        KeptStore.erase(keeping: nil)
        account = nil
        copies = []
    }

    /// Nothing fetched for now (the session ended, a friend's party): the copies stay.
    func stop() {
        round += 1
        task?.cancel()
        task = nil
        later?.cancel()
        current = nil
        waiting = false
        endBackground(success: false)
    }

    // MARK: Asked here

    /// Keeps films and episodes, after those asked before.
    func keep(_ items: [Item]) {
        guard let account, app?.party.guest == nil else { return }
        var at = Date()
        for item in items where item.isEpisode || item.type == "Movie" {
            guard !copies.contains(where: { $0.id == item.id }) else { continue }
            let copy = KeptCopy(item: item, added: at)
            // The order they were asked in, a season's episodes too.
            at = at.addingTimeInterval(0.001)
            do { try KeptStore.save(copy, account: account) } catch { continue }
            copies.insert(copy, at: 0)
        }
        run()
    }

    func retry(_ id: String) {
        guard var copy = copies.first(where: { $0.id == id }) else { return }
        copy.failure = nil
        failures[id] = nil
        update(copy)
        run()
    }

    /// Takes a copy off this iPhone, fetched or not; the next one goes on.
    func remove(_ id: String) {
        guard let account else { return }
        let running = current == id
        if running {
            round += 1
            task?.cancel()
            task = nil
            current = nil
        }
        copies.removeAll { $0.id == id }
        KeptStore.delete(id, account: account)
        if running { run() }
    }

    // MARK: Fetching

    /// Mira back on screen, or a way to Jellyfin found again: what waits goes on, what was watched here is told.
    func resume() {
        endGrace()
        run()
        Task { await sendProgress() }
    }

    /// Mira leaves the screen: without iOS's progress in the background, the few seconds it grants end the piece
    /// being fetched cleanly; it goes on when Mira is back.
    func background() {
        guard task != nil, continued == nil, grace == .invalid else { return }
        grace = UIApplication.shared.beginBackgroundTask(withName: "Keep on this iPhone") { [weak self] in
            MainActor.assumeIsolated {
                self?.task?.cancel()
                self?.endGrace()
            }
        }
    }

    private func run() {
        guard task == nil, account != nil, app?.client != nil, app?.party.guest == nil,
              copies.contains(where: { !$0.complete && $0.failure == nil }) else { return }
        later?.cancel()
        waiting = false
        askToContinue()
        round += 1
        let round = round
        task = Task { [weak self] in
            await self?.loop()
            self?.ended(round)
        }
    }

    private func loop() async {
        while !Task.isCancelled, let next = copies.filter({ !$0.complete && $0.failure == nil }).min(by: { $0.added < $1.added }) {
            guard let client = app?.client, app?.party.guest == nil else { return }
            current = next.id
            do {
                try await fetch(next, client)
                failures[next.id] = nil
            } catch {
                current = nil
                // Taken off meanwhile: the next one.
                guard copies.contains(where: { $0.id == next.id }) else { continue }
                if Task.isCancelled || error is CancellationError { return }
                switch error {
                case let error as KeepError:
                    fail(next.id, error.text)
                case JellyfinError.unauthorized:
                    app?.sessionEnded()
                    return
                case JellyfinError.forbidden:
                    fail(next.id, KeepError.refused.text)
                case JellyfinError.http, JellyfinError.notJellyfin:
                    // Jellyfin itself fails (a conversion that does not start): tried again, then said.
                    failures[next.id, default: 0] += 1
                    if failures[next.id, default: 0] >= 3 {
                        fail(next.id, Messages.text(error))
                    } else {
                        try? await Task.sleep(for: .seconds(5))
                    }
                case let error as CocoaError where error.code == .fileWriteOutOfSpace:
                    fail(next.id, KeepError.noRoom(max(0, next.size - next.bytes)).text)
                default:
                    // The way to Jellyfin: tried again when one comes back, and every minute meanwhile.
                    waiting = true
                    later = Task { [weak self] in
                        try? await Task.sleep(for: .seconds(60))
                        guard !Task.isCancelled else { return }
                        self?.run()
                    }
                    return
                }
            }
        }
    }

    private func ended(_ round: Int) {
        guard round == self.round else { return }
        task = nil
        current = nil
        endBackground(success: !copies.contains { !$0.complete && $0.failure == nil })
        app?.keepingEnded()
    }

    private func fail(_ id: String, _ text: String) {
        guard var copy = copies.first(where: { $0.id == id }) else { return }
        copy.failure = text
        update(copy)
    }

    /// Written to its folder, if it is still kept (taken off meanwhile, nothing comes back).
    private func update(_ copy: KeptCopy) {
        guard let account, let index = copies.firstIndex(where: { $0.id == copy.id }) else { return }
        copies[index] = copy
        try? KeptStore.save(copy, account: account)
        if copy.id == current { continued?.progress.completedUnitCount = Int64(KeptRules.fraction(copy) * 1000) }
    }

    private var authorization: String { app?.client?.authorization ?? "" }

    private func fetch(_ start: KeptCopy, _ client: JellyfinClient) async throws {
        guard let account else { return }
        var copy = start
        let folder = KeptStore.folder(account: account, item: copy.id)
        if copy.kind == nil {
            // What Jellyfin knows of the title now (its file and tracks, its chapters), and whether the account may
            // keep it: the download right of Jellyfin's policy, for a conversion too.
            do { copy.item = try await client.item(copy.id) } catch JellyfinError.http(404) { throw KeepError.gone }
            try await Self.allowed(client.url("Items/\(copy.id)/Download"), authorization: client.authorization)
            if let preferences = app?.preferences {
                let chosen = TrackChoice.choose(copy.item.mediaSources?.first, memory: preferences.tracks(for: copy.item.seriesId),
                                                audioOrder: preferences.audioOrder, subtitleOrder: preferences.subtitleOrder)
                copy.audioIndex = chosen.audio
            }
        }
        // Asked each time: a conversion's session is new each time (its segments are numbered the same).
        let info: PlaybackInfo
        do {
            info = try await client.playbackInfo(copy.id, request: PlaybackRequest(
                bitrate: PlaybackPlan.fullBitrate, stage: 0, startTicks: 0, audioIndex: copy.audioIndex, subtitleIndex: -1,
                mediaSourceId: copy.item.mediaSources?.first?.id, device: device))
        } catch JellyfinError.http(404) { throw KeepError.gone }
        if info.errorCode == "NotAllowed" { throw KeepError.refused }
        guard info.errorCode == nil, let source = info.mediaSources?.first, let kind = KeptRules.kind(source) else { throw KeepError.cannotPlay }
        if copy.kind != kind {
            // First time, or Jellyfin decides otherwise now (its settings changed): from the start.
            copy.kind = kind
            copy.pieces = 0
            copy.bytes = 0
            copy.total = 0
            copy.size = KeptRules.estimate(source, kind: kind, runTimeTicks: copy.item.runTimeTicks)
            try? FileManager.default.removeItem(at: folder.appending(path: "video.part"))
        }
        copy.source = source
        guard KeptRules.fits(max(0, copy.size - copy.bytes), free: KeptStore.freeSpace()) else { throw KeepError.noRoom(max(0, copy.size - copy.bytes)) }
        update(copy)
        if !copy.hasPicture, await picture(copy, client, to: folder.appending(path: "picture")) {
            copy.hasPicture = true
            update(copy)
        }
        let part = folder.appending(path: "video.part")
        switch kind {
        case .file: copy = try await fetchFile(copy, client, part: part)
        case .converted:
            defer { if let session = info.playSessionId { Task { await client.stopEncoding(session) } } }
            copy = try await fetchConversion(copy, source, client, part: part)
        }
        try Task.checkCancellation()
        let video = folder.appending(path: copy.videoName)
        try? FileManager.default.removeItem(at: video)
        try FileManager.default.moveItem(at: part, to: video)
        copy.size = copy.bytes
        copy.subtitles = await subtitles(copy, source, client, folder: folder)
        copy.complete = true
        copy.failure = nil
        update(copy)
    }

    /// The file as it is, a range at a time: the first one says its size.
    private func fetchFile(_ start: KeptCopy, _ client: JellyfinClient, part: URL) async throws -> KeptCopy {
        var copy = start
        let address = client.url("Items/\(copy.id)/Download")
        while copy.total == 0 || copy.pieces < copy.total {
            try Task.checkCancellation()
            try roomLeft(copy)
            let ranges = KeptRules.ranges(size: copy.size)
            let range = copy.total > 0 && copy.pieces < ranges.count ? ranges[copy.pieces] : 0...(KeptRules.chunk - 1)
            let (piece, response) = try await Self.download(address, range: range, authorization: client.authorization, folder: part.deletingLastPathComponent())
            if response.statusCode == 200 {
                // Sent whole (no range): that is the file.
                copy.bytes = try Self.append(piece, to: part, at: 0)
                copy.size = copy.bytes
                copy.pieces = 1
                copy.total = 1
                update(copy)
                return copy
            }
            let size = KeptRules.rangeTotal(response.value(forHTTPHeaderField: "Content-Range")) ?? 0
            if size != copy.size || copy.total == 0 {
                // Its size known now, or the file changed on Jellyfin since: from the start.
                copy.size = size
                copy.total = KeptRules.ranges(size: size).count
                copy.pieces = 0
                copy.bytes = 0
                guard KeptRules.fits(size, free: KeptStore.freeSpace()) else {
                    try? FileManager.default.removeItem(at: piece)
                    throw KeepError.noRoom(size)
                }
                guard range.lowerBound == 0 else {
                    try? FileManager.default.removeItem(at: piece)
                    continue
                }
            }
            copy.bytes = try Self.append(piece, to: part, at: range.lowerBound)
            copy.pieces += 1
            update(copy)
        }
        return copy
    }

    /// Jellyfin's conversion, its playlist read, then its init segment and each segment after it, into one fragmented
    /// MP4 (the same file Apple's player reads segment by segment).
    private func fetchConversion(_ start: KeptCopy, _ source: MediaSource, _ client: JellyfinClient, part: URL) async throws -> KeptCopy {
        var copy = start
        guard let path = source.transcodingUrl, let master = client.signed(path) else { throw KeepError.cannotPlay }
        let first = try await Self.text(master, authorization: client.authorization)
        var playlist = first, base = master
        if !first.contains("#EXTINF") {
            guard let variant = KeptRules.variant(first, base: master) else { throw KeepError.cannotPlay }
            playlist = try await Self.text(variant, authorization: client.authorization)
            base = variant
        }
        guard let list = KeptRules.segments(playlist, base: base) else { throw KeepError.cannotPlay }
        let pieces = (list.initial.map { [$0] } ?? []) + list.segments
        if copy.total != pieces.count {
            // Another length of segments, or another title: from the start.
            copy.total = pieces.count
            copy.pieces = 0
            copy.bytes = 0
        }
        while copy.pieces < pieces.count {
            try Task.checkCancellation()
            try roomLeft(copy)
            // Jellyfin converts as the segments are asked: a slow one is not a lost connection.
            let (piece, _) = try await Self.download(pieces[copy.pieces], range: nil, authorization: client.authorization, folder: part.deletingLastPathComponent(), timeout: 180)
            copy.bytes = try Self.append(piece, to: part, at: copy.bytes)
            copy.pieces += 1
            if copy.pieces > 1 { copy.size = max(copy.bytes, copy.bytes / Int64(copy.pieces) * Int64(copy.total)) }
            update(copy)
        }
        return copy
    }

    private func roomLeft(_ copy: KeptCopy) throws {
        if let free = KeptStore.freeSpace(), free < KeptRules.reserve / 2 { throw KeepError.noRoom(max(KeptRules.chunk, copy.size - copy.bytes)) }
    }

    /// Every text subtitle of the title, as WebVTT (Jellyfin reads those inside the file too); one Jellyfin can't give
    /// is only missing from the list.
    private func subtitles(_ copy: KeptCopy, _ source: MediaSource, _ client: JellyfinClient, folder: URL) async -> [KeptSubtitle] {
        var kept: [KeptSubtitle] = []
        let sourceId = source.id ?? copy.id
        for stream in source.subtitles where stream.isTextSubtitleStream == true {
            let name = "subtitle-\(stream.index).vtt"
            guard let data = try? await client.send("GET", "Videos/\(copy.id)/\(sourceId)/Subtitles/\(stream.index)/0/Stream.vtt", timeout: 60),
                  !data.isEmpty, (try? data.write(to: folder.appending(path: name), options: .atomic)) != nil else { continue }
            kept.append(KeptSubtitle(file: name, index: stream.index))
        }
        return kept
    }

    /// The title's wide picture, for the list and the Lock Screen without Jellyfin.
    private func picture(_ copy: KeptCopy, _ client: JellyfinClient, to file: URL) async -> Bool {
        guard let art = Artwork.of(copy.item, .wide) ?? Artwork.of(copy.item, .poster) else { return false }
        let address = await Way.shared.map(client.imageURL(art, width: 400, scale: 2))
        guard let answer = try? await JellyfinClient.session.data(from: address), (answer.1 as? HTTPURLResponse)?.statusCode == 200,
              UIImage(data: answer.0) != nil else { return false }
        return (try? answer.0.write(to: file, options: .atomic)) != nil
    }

    // MARK: Requests

    /// Copies take their own connections: nothing kept in the pictures' cache, long waits for a conversion.
    nonisolated private static let session: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 60
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }()

    /// One piece (a range of the file, a segment) into a file of the copy's folder, on the way to Jellyfin taken now.
    nonisolated private static func download(_ url: URL, range: ClosedRange<Int64>?, authorization: String, folder: URL, timeout: TimeInterval = 60) async throws -> (URL, HTTPURLResponse) {
        let address = await Way.shared.map(url)
        var request = URLRequest(url: address, timeoutInterval: timeout)
        request.setValue(authorization, forHTTPHeaderField: "Authorization")
        if let range { request.setValue("bytes=\(range.lowerBound)-\(range.upperBound)", forHTTPHeaderField: "Range") }
        let (file, response): (URL, URLResponse)
        do { (file, response) = try await session.download(for: request) } catch {
            await Way.shared.lost(address)
            throw JellyfinClient.failure(error)
        }
        let piece = folder.appending(path: "piece.tmp")
        try? FileManager.default.removeItem(at: piece)
        do { try FileManager.default.moveItem(at: file, to: piece) } catch {
            try? FileManager.default.removeItem(at: file)
            throw error
        }
        guard let http = response as? HTTPURLResponse else { throw JellyfinError.notJellyfin }
        do { try check(http, address) } catch {
            try? FileManager.default.removeItem(at: piece)
            throw error
        }
        return (piece, http)
    }

    /// A playlist of the conversion.
    nonisolated private static func text(_ url: URL, authorization: String) async throws -> String {
        let address = await Way.shared.map(url)
        var request = URLRequest(url: address, timeoutInterval: 120)
        request.setValue(authorization, forHTTPHeaderField: "Authorization")
        let (data, response): (Data, URLResponse)
        do { (data, response) = try await session.data(for: request) } catch {
            await Way.shared.lost(address)
            throw JellyfinClient.failure(error)
        }
        guard let http = response as? HTTPURLResponse else { throw JellyfinError.notJellyfin }
        try check(http, address)
        return String(decoding: data, as: UTF8.self)
    }

    /// The account may download this title: its first byte asked (403 when Jellyfin's policy says no).
    nonisolated private static func allowed(_ url: URL, authorization: String) async throws {
        let address = await Way.shared.map(url)
        var request = URLRequest(url: address, timeoutInterval: 30)
        request.setValue(authorization, forHTTPHeaderField: "Authorization")
        request.setValue("bytes=0-0", forHTTPHeaderField: "Range")
        // Only the headers: a server that sends the whole file anyway is not read to its end.
        let (bytes, response): (URLSession.AsyncBytes, URLResponse)
        do { (bytes, response) = try await session.bytes(for: request) } catch {
            await Way.shared.lost(address)
            throw JellyfinClient.failure(error)
        }
        bytes.task.cancel()
        guard let http = response as? HTTPURLResponse else { throw JellyfinError.notJellyfin }
        try check(http, address)
    }

    nonisolated private static func check(_ http: HTTPURLResponse, _ address: URL) throws {
        if let code = JellyfinClient.relayFailure(http) { throw JellyfinError.tailscale(code) }
        switch http.statusCode {
        case 200..<300: return
        case 401: throw JellyfinError.unauthorized
        case 403: throw KeepError.refused
        case 404: throw KeepError.gone
        default: throw JellyfinError.http(http.statusCode)
        }
    }

    /// The piece written after the first `offset` bytes of the part (what came after them, from a piece cut short, is
    /// dropped); the part's new length.
    nonisolated private static func append(_ piece: URL, to part: URL, at offset: Int64) throws -> Int64 {
        defer { try? FileManager.default.removeItem(at: piece) }
        if !FileManager.default.fileExists(atPath: part.path), !FileManager.default.createFile(atPath: part.path, contents: nil) {
            throw CocoaError(.fileNoSuchFile)
        }
        let output = try FileHandle(forWritingTo: part)
        defer { try? output.close() }
        try output.truncate(atOffset: UInt64(max(0, offset)))
        try output.seekToEnd()
        let input = try FileHandle(forReadingFrom: piece)
        defer { try? input.close() }
        while let data = try input.read(upToCount: 4 << 20), !data.isEmpty { try output.write(contentsOf: data) }
        return Int64(try output.offset())
    }

    // MARK: What was watched here

    /// Where a title kept here was left: its page and the next playback start there; Jellyfin is told later when it did
    /// not hear it (`heard`).
    func remember(_ id: String, ticks: Int64, finished: Bool, heard: Bool, at: Date = Date()) {
        guard var copy = copies.first(where: { $0.id == id }) else { return }
        var data = copy.item.userData ?? UserData()
        data.playbackPositionTicks = finished ? 0 : ticks
        if finished { data.played = true }
        copy.item.userData = data
        copy.watched = heard ? nil : KeptWatch(ticks: ticks, finished: finished, at: at)
        update(copy)
    }

    /// Jellyfin heard where the title was at `at`: nothing left to tell it from then.
    func heard(_ id: String, at: Date) {
        guard var copy = copies.first(where: { $0.id == id }), let watch = copy.watched, watch.at <= at else { return }
        copy.watched = nil
        update(copy)
    }

    /// What was watched here while Jellyfin heard nothing, told to it now: unless it was watched elsewhere since.
    func sendProgress() async {
        guard !sending, let client = app?.client, app?.party.guest == nil else { return }
        sending = true
        defer { sending = false }
        var told = false
        for id in copies.filter({ $0.watched != nil }).map(\.id) {
            guard let watch = copies.first(where: { $0.id == id })?.watched else { continue }
            let server: Item
            do { server = try await client.item(id) } catch JellyfinError.http(404) {
                clearWatched(id, watch)
                continue
            } catch { return }
            let last = server.userData?.lastPlayedDate.flatMap(HomeContent.parseDate)
            do {
                switch KeptRules.progressStep(watch, serverLastPlayed: last) {
                case .drop: break
                case .played:
                    try await client.setPlayed(id, true)
                    told = true
                case let .position(ticks):
                    let body = try JSONSerialization.data(withJSONObject: [
                        "PlaybackPositionTicks": max(0, ticks), "Played": false,
                        "LastPlayedDate": watch.at.formatted(.iso8601),
                    ])
                    try await client.send("POST", "UserItems/\(id)/UserData", ["userId": client.userId], body: body)
                    told = true
                }
            } catch { return }
            clearWatched(id, watch)
        }
        if told { app?.playbackChanged() }
    }

    private func clearWatched(_ id: String, _ watch: KeptWatch) {
        guard var copy = copies.first(where: { $0.id == id }), copy.watched == watch else { return }
        copy.watched = nil
        update(copy)
    }

    // MARK: In the background

    /// Asked while Mira is on screen: iOS shows the progress and lets it go on once Mira leaves it.
    private func askToContinue() {
        guard continued == nil, UIApplication.shared.applicationState == .active else { return }
        let identifier = "app.miracinema.Mira.keep." + UUID().uuidString.lowercased().replacingOccurrences(of: "-", with: "")
        let registered = BGTaskScheduler.shared.register(forTaskWithIdentifier: identifier, using: nil) { [weak self] task in
            let handed = Handed(task: task)
            Task { @MainActor in self?.started(handed.task) }
        }
        guard registered else { return }
        let request = BGContinuedProcessingTaskRequest(identifier: identifier, title: String(localized: "Keeping on this iPhone"),
                                                       subtitle: String(localized: "To watch without a connection"))
        try? BGTaskScheduler.shared.submit(request)
    }

    private func started(_ task: BGTask) {
        guard let task = task as? BGContinuedProcessingTask, self.task != nil, continued == nil else {
            task.setTaskCompleted(success: true)
            return
        }
        continued = task
        task.progress.totalUnitCount = 1000
        task.progress.completedUnitCount = Int64((copies.first { $0.id == current }.map(KeptRules.fraction) ?? 0) * 1000)
        task.expirationHandler = { [weak self] in
            Task { @MainActor in
                // iOS's time is over: the piece being fetched stops, the rest when Mira is back.
                self?.task?.cancel()
                self?.continued?.setTaskCompleted(success: false)
                self?.continued = nil
            }
        }
        endGrace()
    }

    private func endBackground(success: Bool) {
        continued?.setTaskCompleted(success: success)
        continued = nil
        endGrace()
    }

    private func endGrace() {
        guard grace != .invalid else { return }
        UIApplication.shared.endBackgroundTask(grace)
        grace = .invalid
    }

    /// « 850 MB », « 4.2 GB ».
    nonisolated static func size(_ bytes: Int64) -> String { ByteCountFormatter.string(fromByteCount: max(0, bytes), countStyle: .file) }
}

/// iOS's task, handed from its queue to the main actor (it is only used there).
private struct Handed: @unchecked Sendable {
    let task: BGTask
}
