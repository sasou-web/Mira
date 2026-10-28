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

    var continueWatching: [Item] { HomeContent.orderContinue(resume: resume, next: next, history: history) }

    var isEmpty: Bool { continueWatching.isEmpty && latest.isEmpty && favorites.isEmpty }

    /// The banner: the first two titles of Continue watching (under their series' artwork), then the latest additions,
    /// six at most, only those with a picture to show.
    var banner: [BannerEntry] {
        var entries: [BannerEntry] = [], seen = Set<String>()
        for play in continueWatching.prefix(2) {
            let item = play.isEpisode ? (play.seriesId.flatMap { series[$0] } ?? play) : play
            if Artwork.of(item, .backdrop) != nil, seen.insert(item.id).inserted { entries.append(BannerEntry(item: item, play: play)) }
        }
        for item in latest where entries.count < 6 {
            if Artwork.of(item, .backdrop) != nil, seen.insert(item.id).inserted { entries.append(BannerEntry(item: item, play: item.isSeries ? nil : item)) }
        }
        return entries
    }

    /// Continue watching as Mira orders it everywhere: a real resume point wins over the next episode of the same
    /// series, and each film or series stands where its last activity puts it (orderContinue of Mira web).
    static func orderContinue(resume: [Item], next: [Item], history: [Item]) -> [Item] {
        func group(_ item: Item) -> String { item.seriesId ?? item.id }
        func played(_ item: Item) -> Date { item.userData?.lastPlayedDate.flatMap(parseDate) ?? .distantPast }
        var activity: [String: Date] = [:]
        for item in history { activity[group(item)] = max(activity[group(item)] ?? .distantPast, played(item)) }
        let resumed = resume.filter { $0.userData?.played != true && ($0.userData?.playbackPositionTicks ?? 0) > 0 }
            .enumerated().sorted { played($0.element) > played($1.element) || played($0.element) == played($1.element) && $0.offset < $1.offset }.map(\.element)
        var seen = Set<String>(), candidates: [Item] = []
        for item in resumed + next.filter({ $0.userData?.played != true }) where seen.insert(group(item)).inserted {
            candidates.append(item)
        }
        func key(_ item: Item) -> Date { max(played(item), activity[group(item)] ?? .distantPast) }
        return candidates.enumerated().sorted { key($0.element) > key($1.element) || key($0.element) == key($1.element) && $0.offset < $1.offset }.map(\.element)
    }

    /// Jellyfin's dates: « 2026-10-27T21:14:03.1234567Z », seven decimals that ISO8601DateFormatter does not read.
    static func parseDate(_ text: String) -> Date? {
        var text = text
        if let dot = text.firstIndex(of: ".") {
            let end = text[dot...].firstIndex { $0 == "Z" || $0 == "+" || $0 == "-" } ?? text.endIndex
            text.removeSubrange(dot..<end)
        }
        return try? Date(text, strategy: .iso8601)
    }
}

struct BannerEntry: Sendable, Hashable, Identifiable {
    /// The film or series shown.
    let item: Item
    /// What a tap plays: the film, the episode to resume or watch next; nil for a series added lately.
    let play: Item?
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

    /// The account's last Home, shown at once while Jellyfin is asked again.
    func start(userId: String) {
        if content == nil { content = HomeCache.load(userId: userId) }
    }

    func load(_ client: JellyfinClient, onUnauthorized: () -> Void) async {
        if content == nil { state = .loading }
        do {
            async let resume = client.resume()
            async let next = client.nextUp()
            async let history = client.history()
            async let latest = client.browse()
            async let favorites = client.browse(favorites: true)
            var fresh = try await HomeContent(resume: resume, next: next, history: history.filter { $0.userData?.lastPlayedDate != nil },
                                              latest: latest, favorites: favorites)
            var seriesIds: [String] = []
            for id in (fresh.resume + fresh.next).compactMap(\.seriesId) where !seriesIds.contains(id) && seriesIds.count < 4 { seriesIds.append(id) }
            fresh.series = await withTaskGroup(of: Item?.self) { group in
                for id in seriesIds { group.addTask { try? await client.item(id) } }
                var found: [String: Item] = [:]
                for await item in group { if let item { found[item.id] = item } }
                return found
            }
            if fresh != content { content = fresh }
            state = .loaded
            loadedAt = .now
            if let userId = client.userId { HomeCache.save(fresh, userId: userId) }
        } catch is CancellationError {
            return
        } catch JellyfinError.unauthorized {
            onUnauthorized()
        } catch let error as JellyfinError {
            state = .failed(error)
        } catch {
            state = .failed(.unreachable)
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
