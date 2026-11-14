import Foundation

/// The room a title takes on the server's disks, as Jellyfin knows its files (`mediaSources[].size`, nothing asked of
/// anyone else): a film's file, its other versions and parts (cd1/cd2) too; a series' episodes, the season shown and each
/// episode. Said on the title's page, in its line of facts (« 2022 · 2 h · 14 GB »), under the seasons (« 12 episodes ·
/// 48 GB ») and on an episode's tile (« 24 min · 1.2 GB »). TitleStorage of Windows and Mac, the same cases
/// (tests/fixtures/storage.json).
enum StorageSize {
    /// The files of a title: each version once (by its path, else its id), those whose size Jellyfin does not know left
    /// out; nil when it knows none.
    static func of(_ sources: [MediaSource]?) -> Int64? {
        guard let sources else { return nil }
        var total: Int64 = 0, any = false, seen = Set<String>()
        for source in sources {
            guard let size = source.size, size > 0 else { continue }
            let key = (source.path.flatMap { $0.isEmpty ? nil : $0 } ?? source.id ?? "").lowercased()
            guard seen.insert(key).inserted else { continue }
            total += size
            any = true
        }
        return any ? total : nil
    }

    /// A title and its other parts (a film in cd1/cd2: Jellyfin lists the first file alone as the title's).
    static func of(_ item: Item, parts: [Item] = []) -> Int64? {
        let sizes = ([of(item.mediaSources)] + parts.map { of($0.mediaSources) }).compactMap(\.self)
        return sizes.isEmpty ? nil : sizes.reduce(0, +)
    }

    /// Each episode's files by its id (those Jellyfin knows a size of).
    static func sizes(_ episodes: [Item]) -> [String: Int64] {
        var sizes: [String: Int64] = [:]
        for episode in episodes { if let size = of(episode.mediaSources) { sizes[episode.id] = size } }
        return sizes
    }

    /// The episodes known among `episodes`: nil when none is.
    static func sum(_ episodes: [Item], _ sizes: [String: Int64]) -> Int64? {
        var total: Int64 = 0, any = false, seen = Set<String>()
        for episode in episodes {
            guard seen.insert(episode.id).inserted, let size = sizes[episode.id] else { continue }
            total += size
            any = true
        }
        return any ? total : nil
    }

    /// « 850 MB », « 14 GB », « 1.4 TB » (RemoteLines.size, as Windows writes a size); nil for no size.
    static func text(_ bytes: Int64?) -> String? {
        guard let bytes, bytes > 0 else { return nil }
        return RemoteLines.size(bytes)
    }

    /// The room a title takes: a film's (its parts asked when it has some), a series' (its episodes asked, kept for
    /// `keep`: an anime of a thousand episodes is a long list); nil when Jellyfin knows none or does not answer (only a
    /// detail of the page).
    @MainActor static func size(of item: Item, client: JellyfinClient) async -> Int64? {
        let key = "\(client.server.absoluteString)|\(client.userId ?? "")|\(item.id)"
        if let hit = kept[key], Date.now.timeIntervalSince(hit.at) < keep { return hit.size }
        let size: Int64?
        do {
            if item.isSeries {
                let episodes = try await client.episodeFiles(item.id)
                size = sum(episodes, sizes(episodes))
            } else {
                size = of(item, parts: (item.partCount ?? 0) > 1 ? try await client.additionalParts(item.id) : [])
            }
        } catch {
            return nil
        }
        if kept.count > 128 { kept.removeAll() }
        kept[key] = (Date.now, size)
        return size
    }

    static let keep: TimeInterval = 30 * 60
    @MainActor private static var kept: [String: (at: Date, size: Int64?)] = [:]
}
