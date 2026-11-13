import Foundation
import Observation

/// What Home shows, as Mira web's Home (views/home.js): a banner, then Continue watching, Recently added and Your
/// favorites.
struct HomeContent: Codable, Sendable, Equatable {
    var resume: [Item] = []
    var next: [Item] = []
    var history: [Item] = []
    var latest: [Item] = []
    var favorites: [Item] = []
    /// The series of the first titles of Continue watching, for their artwork in the banner.
    var series: [String: Item] = [:]
    /// The pictures of the banner's titles and their sizes, by title (BannerPicture); nil in a Home kept by an older
    /// Mira.
    var pictures: [String: [ImageInfo]]?
    /// The account's libraries, and the latest titles of those whose row is shown (« New in … », HomeLayout).
    var libraries: [HomeLibrary] = []
    var libraryRows: [String: [Item]] = [:]

    /// Continue watching, without the films and series taken out of it (`hidden`: their ids, or their series', with
    /// when, in ms), unless played again since (withoutHidden of Mira web).
    func continueWatching(hidden: [String: Double] = [:]) -> [Item] {
        let ordered = HomeContent.orderContinue(resume: resume, next: next, history: history)
        guard !hidden.isEmpty else { return ordered }
        func group(_ item: Item) -> String { item.seriesId ?? item.id }
        func played(_ item: Item) -> Double { (item.userData?.lastPlayedDate.flatMap(HomeContent.parseDate)?.timeIntervalSince1970 ?? 0) * 1000 }
        return ordered.filter { item in
            guard let when = hidden[group(item)], when > 0 else { return true }
            return played(item) > when || history.contains { group($0) == group(item) && played($0) > when }
        }
    }

    func isEmpty(hidden: [String: Double]) -> Bool { continueWatching(hidden: hidden).isEmpty && latest.isEmpty && favorites.isEmpty }

    /// The banner, as on Windows (HeroCandidates of Mira.Core): the titles of Continue watching (an episode under its
    /// series), then the latest additions, one per series, five at most, only those with a picture to show; each with
    /// the sizes of its pictures when Jellyfin gave them (BannerPicture).
    func banner(hidden: [String: Double]) -> [BannerEntry] {
        var entries: [BannerEntry] = [], seen = Set<String>()
        func add(_ item: Item, play: Item?, key: String) {
            guard entries.count < 5, Artwork.of(item, .backdrop) != nil, seen.insert(key).inserted else { return }
            entries.append(BannerEntry(item: item, play: play, pictures: pictures?[item.id] ?? [],
                                       playPictures: play.flatMap { $0.id == item.id ? nil : pictures?[$0.id] } ?? []))
        }
        for play in continueWatching(hidden: hidden) {
            let item = play.isEpisode ? (play.seriesId.flatMap { series[$0] } ?? play) : play
            add(item, play: play, key: play.seriesId ?? item.id)
        }
        for item in latest { add(item, play: item.isSeries ? nil : item, key: item.id) }
        return entries
    }

    /// The titles whose pictures the banner asks the sizes of: each slide's film or series, and an episode's own still.
    func bannerPictureIds(hidden: [String: Double]) -> [String] {
        banner(hidden: hidden).flatMap { entry in [entry.item.id] + (entry.play.map { $0.id == entry.item.id ? [] : [$0.id] } ?? []) }
    }

    /// Sizes kept from before still describe `item`'s pictures: its first backdrop and its own picture have the same tags.
    static func picturesCurrent(_ infos: [ImageInfo], of item: Item) -> Bool {
        let tags = Set(infos.compactMap(\.imageTag))
        let wanted = [item.backdropImageTags?.first, item.imageTags?.first { $0.key.caseInsensitiveCompare("Primary") == .orderedSame }?.value].compactMap { $0 }
        return !wanted.isEmpty && wanted.allSatisfy(tags.contains)
    }

    /// Continue watching as Mira orders it everywhere: a real resume point wins over the next episode of the same
    /// series (unless the series went on past it since: `leftBehind`), and each film or series stands where its last
    /// activity puts it (Order of Core). A title watched again and left halfway is resumed too: Jellyfin 12 keeps it
    /// watched, with its position (ItemText.progress).
    static func orderContinue(resume: [Item], next: [Item], history: [Item]) -> [Item] {
        func group(_ item: Item) -> String { item.seriesId ?? item.id }
        func played(_ item: Item) -> Date { item.userData?.lastPlayedDate.flatMap(parseDate) ?? .distantPast }
        var activity: [String: Date] = [:]
        for item in history { activity[group(item)] = max(activity[group(item)] ?? .distantPast, played(item)) }
        let upNext = next.filter { $0.userData?.played != true }
        let resumed = resume.filter { ($0.userData?.playbackPositionTicks ?? 0) > 0 && !leftBehind($0, next: upNext, history: history) }
            .enumerated().sorted { played($0.element) > played($1.element) || played($0.element) == played($1.element) && $0.offset < $1.offset }.map(\.element)
        var seen = Set<String>(), candidates: [Item] = []
        for item in resumed + upNext where seen.insert(group(item)).inserted {
            candidates.append(item)
        }
        func key(_ item: Item) -> Date { max(played(item), activity[group(item)] ?? .distantPast) }
        return candidates.enumerated().sorted { key($0.element) > key($1.element) || key($0.element) == key($1.element) && $0.offset < $1.offset }.map(\.element)
    }

    /// An episode left halfway that its series went on past: Jellyfin offers one after it (NextUp), and an episode after
    /// it was played since it was left (skipped with the next-episode button, finished on another device). Its series is
    /// where that episode left it (LeftBehind of Core; checked on Jellyfin 12.1: S01E02 left at 30 %, then S01E03 and
    /// S01E04 watched, keeps S01E02 in UserItems/Resume while Shows/NextUp says S01E05).
    static func leftBehind(_ resumed: Item, next: [Item], history: [Item]) -> Bool {
        guard resumed.isEpisode, let series = resumed.seriesId, let left = resumed.userData?.lastPlayedDate.flatMap(parseDate) else { return false }
        func later(_ episode: Item) -> Bool {
            (episode.parentIndexNumber ?? 0, episode.indexNumber ?? 0) > (resumed.parentIndexNumber ?? 0, resumed.indexNumber ?? 0)
        }
        func played(_ item: Item) -> Date { item.userData?.lastPlayedDate.flatMap(parseDate) ?? .distantPast }
        return next.contains { $0.seriesId == series && later($0) }
            && history.contains { $0.seriesId == series && $0.id != resumed.id && later($0) && played($0) > left }
    }

    /// Jellyfin's dates: « 2026-10-27T21:14:03.1234567Z », seven decimals that ISO8601DateFormatter does not read.
    static func parseDate(_ text: String) -> Date? {
        // Jellyfin writes seven digits of a second, which the ISO 8601 parser refuses: read apart, then added back
        // (episodes added within the same second are told apart by them).
        var text = text, fraction = 0.0
        if let dot = text.firstIndex(of: ".") {
            let end = text[dot...].firstIndex { $0 == "Z" || $0 == "+" || $0 == "-" } ?? text.endIndex
            fraction = Double("0" + text[dot..<end]) ?? 0
            text.removeSubrange(dot..<end)
        }
        return (try? Date(text, strategy: .iso8601))?.addingTimeInterval(fraction)
    }
}

struct BannerEntry: Sendable, Hashable, Identifiable {
    /// The film or series shown.
    let item: Item
    /// What a tap plays: the film, the episode to resume or watch next; nil for a series added lately.
    let play: Item?
    /// The pictures of `item`, and of the episode `play` (its still), with their sizes: empty when not known.
    var pictures: [ImageInfo] = []
    var playPictures: [ImageInfo] = []
    var id: String { item.id }
}

@MainActor @Observable
final class HomeModel {
    enum State: Equatable {
        case loading
        case loaded
        case failed(JellyfinError)
    }

    private(set) var content: HomeContent?
    private(set) var state: State = .loading
    private(set) var loadedAt: Date?
    /// Loads started: one that ends after a later one began says nothing (the later one answers).
    @ObservationIgnored private var loads = 0

    /// The account's last Home, shown at once while Jellyfin is asked again.
    func start(userId: String) {
        if content == nil { content = HomeCache.load(userId: userId) }
    }

    /// `rows`: Home's rows on this iPhone (HomeLayout): only the libraries whose row is shown are asked. `hidden`: the
    /// titles taken out of Continue watching (the banner leaves them out too). True when this load said where Home is
    /// (loaded or failed); false when a later load replaced it, or it was cancelled.
    @discardableResult
    func load(_ client: JellyfinClient, rows: String, hidden: [String: Double] = [:], onUnauthorized: () -> Void) async -> Bool {
        loads += 1
        let mine = loads
        if content == nil { state = .loading }
        do {
            async let resume = client.resume()
            async let next = client.nextUp()
            async let history = client.history()
            async let latest = client.browse()
            async let favorites = client.browse(favorites: true)
            async let libraries = client.homeLibraries()
            var fresh = try await HomeContent(resume: resume, next: next, history: history.filter { $0.userData?.lastPlayedDate != nil },
                                              latest: latest, favorites: favorites)
            fresh.libraries = (try? await libraries) ?? content?.libraries ?? []
            let wanted = HomeLayout.rows(rows, libraries: fresh.libraries).compactMap { $0.shown ? $0.library : nil }
            fresh.libraryRows = await withTaskGroup(of: (String, [Item]?).self) { group in
                for id in wanted { group.addTask { (id, try? await client.browse(library: id)) } }
                var found: [String: [Item]] = [:]
                for await (id, items) in group { if let items { found[id] = items } }
                return found
            }
            var seriesIds: [String] = []
            for id in fresh.continueWatching(hidden: hidden).compactMap(\.seriesId) where !seriesIds.contains(id) && seriesIds.count < 5 { seriesIds.append(id) }
            fresh.series = await withTaskGroup(of: Item?.self) { group in
                for id in seriesIds { group.addTask { try? await client.item(id) } }
                var found: [String: Item] = [:]
                for await item in group { if let item { found[item.id] = item } }
                return found
            }
            // The sizes of the banner's pictures: asked again only for a title whose pictures changed since.
            let known = content?.pictures ?? [:]
            var titles: [String: Item] = [:]
            for item in fresh.resume + fresh.next + fresh.latest + Array(fresh.series.values) { titles[item.id] = item }
            let pictureIds = fresh.bannerPictureIds(hidden: hidden), current = titles
            fresh.pictures = await withTaskGroup(of: (String, [ImageInfo]?).self) { group in
                for id in pictureIds {
                    if let kept = known[id], let item = current[id], HomeContent.picturesCurrent(kept, of: item) {
                        group.addTask { (id, kept) }
                    } else {
                        group.addTask { (id, try? await client.images(id)) }
                    }
                }
                var found: [String: [ImageInfo]] = [:]
                for await (id, infos) in group { if let infos { found[id] = infos } }
                return found
            }
            guard mine == loads else { return false }
            if fresh != content { content = fresh }
            state = .loaded
            loadedAt = .now
            if let userId = client.userId { HomeCache.save(fresh, userId: userId) }
            return true
        } catch is CancellationError {
            return false
        } catch JellyfinError.unauthorized {
            onUnauthorized()
            return false
        } catch let error as JellyfinError {
            guard mine == loads else { return false }
            state = .failed(error)
            return true
        } catch {
            guard mine == loads else { return false }
            state = .failed(.unreachable)
            return true
        }
    }

    /// Asked again when the app comes back after a minute or more.
    var isStale: Bool { loadedAt.map { Date.now.timeIntervalSince($0) > 60 } ?? true }
}

/// The last Home of each account, in the app's caches: the app opens on it at once, even with the server away.
enum HomeCache {
    private static var folder: URL { URL.cachesDirectory.appending(path: "home", directoryHint: .isDirectory) }
    private static func file(_ userId: String) -> URL { folder.appending(path: "\(userId.filter { $0.isLetter || $0.isNumber }).json") }

    static func load(userId: String) -> HomeContent? {
        guard let data = try? Data(contentsOf: file(userId)) else { return nil }
        return try? JSON.decoder.decode(HomeContent.self, from: data)
    }

    static func save(_ content: HomeContent, userId: String) {
        guard let data = try? JSON.encoder.encode(content) else { return }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? data.write(to: file(userId), options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }

    static func clear(userId: String) { try? FileManager.default.removeItem(at: file(userId)) }
    static func clearAll() { try? FileManager.default.removeItem(at: folder) }
}
