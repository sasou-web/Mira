import Foundation

/// A title in several files (« Film - cd1.mkv », « Film - cd2.mkv », « part1 », « disc2 »…, put together by Jellyfin):
/// one title in the library (`partCount`) whose length, stream and progress are those of its first file; each other
/// file is an item of its own, hidden from the library (Videos/{id}/AdditionalParts, checked on Jellyfin 12.1). They
/// play one after the other in the same player, each reported under its own id, as Jellyfin's web client does;
/// Jellyfin marks the title watched at the end of its first part. MovieParts of Core, with the cases of
/// tests/fixtures/parts.json (its labels in French and in English: `french` says them so).
struct MovieParts: Sendable {
    let title: Item
    /// The title first, then its other files in Jellyfin's order.
    let parts: [Item]

    init(title: Item, others: [Item]) {
        self.title = title
        parts = [title] + others.filter { $0.id != title.id }
    }

    /// A film or an episode in more than one file: its other parts are worth asking Jellyfin for.
    static func has(_ item: Item) -> Bool {
        (item.partCount ?? 0) > 1 && (item.type == "Movie" || item.type == "Episode")
    }

    func holds(_ id: String) -> Bool { index(id) != nil }

    /// The part after this one; nil after the last one, and for an id that is none of them.
    func after(_ id: String) -> Item? {
        guard let at = index(id), at + 1 < parts.count else { return nil }
        return parts[at + 1]
    }

    /// Where « Play » starts: a later part left halfway, from where it stopped (Jellyfin marked the title watched at the
    /// end of its first part), unless the first part was left halfway after it (Jellyfin 12 keeps a title watched when
    /// it is watched again, with its position: checked on 12.1). Otherwise, and for « From the beginning », the first.
    func start(fromStart: Bool) -> Item {
        if fromStart { return title }
        let later = parts.dropFirst().filter { $0.userData?.played != true && ($0.userData?.playbackPositionTicks ?? 0) > 0 }.max { seen($0) < seen($1) }
        guard let later else { return title }
        return (title.userData?.playbackPositionTicks ?? 0) > 0 && seen(title) >= seen(later) ? title : later
    }

    /// « Part 2 of 3 »; empty for an id that is none of them.
    func label(_ id: String, french: Bool = RemoteLines.french) -> String {
        guard let at = index(id) else { return "" }
        return french ? "Partie \(at + 1) sur \(parts.count)" : "Part \(at + 1) of \(parts.count)"
    }

    private func seen(_ item: Item) -> Date {
        item.userData?.lastPlayedDate.flatMap(HomeContent.parseDate) ?? .distantPast
    }

    private func index(_ id: String) -> Int? { parts.firstIndex { $0.id == id } }
}
