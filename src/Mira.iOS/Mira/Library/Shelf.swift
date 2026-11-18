import Foundation

/// « L'étagère » (ogsas, maquette A, 2026-11-16), as Windows and Mac (CollectionShelf of Core; cases shared in
/// tests/fixtures/shelf.json): a Jellyfin collection grouped in the library, one card for a franchise in Movies and
/// Shows, in place of its titles; a touch opens its page, its titles numbered in their watch order. Home and Search
/// always show the titles themselves. Nothing is read but Jellyfin: its collections (sagas from TMDb, those made by
/// hand), their titles, and this account's choices (`preference`).
enum CollectionShelf {
    /// Where each collection's choices are kept: a custom preference of Mira's display preferences of the account, beside
    /// « mira.choices », so that they follow the account to Windows and Mac. Jellyfin keeps no order of its own for a
    /// collection's titles (checked on 12.1).
    static let preference = "mira.collections"
    /// Titles a collection needs in a page for its card to take their place: one title shows as itself.
    static let least = 2

    /// What this account chose for one collection: grouped or not (nil: `automatic`), and its watch order.
    struct Choice: Sendable, Equatable {
        var grouped: Bool?
        var order: [String]?
        var empty: Bool { grouped == nil && (order ?? []).isEmpty }
    }

    /// A collection Jellyfin made by itself (a saga of TMDb: its ids) is grouped unless this account said no; one made
    /// by hand stays a list, its titles in the grids, unless it said yes.
    static func automatic(_ box: Item) -> Bool { box.providerIds?.values.contains { !$0.trimmingCharacters(in: .whitespaces).isEmpty } == true }
    static func grouped(_ box: Item, _ choice: Choice?) -> Bool { choice?.grouped ?? automatic(box) }

    /// The choices kept on Jellyfin (the preference's text); anything unreadable counts as nothing.
    static func read(_ value: JSONValue?) -> [String: Choice] {
        var node = value
        if case let .string(text)? = value { node = JSONValue.parse(text) }
        guard let all = node?.object else { return [:] }
        var read: [String: Choice] = [:]
        for (id, entry) in all {
            guard !id.isEmpty, let entry = entry.object else { continue }
            var order: [String] = []
            if case let .array(list)? = entry["order"] {
                for case let .string(title) in list where !title.isEmpty && !order.contains(where: { $0.caseInsensitiveCompare(title) == .orderedSame }) { order.append(title) }
            }
            let choice = Choice(grouped: entry["grouped"]?.bool, order: order.isEmpty ? nil : order)
            if !choice.empty { read[id] = choice }
        }
        return read
    }

    /// The preference's text for these choices, an empty one left out: the same text Windows and Mac write.
    static func write(_ choices: [String: Choice]) -> String {
        func quoted(_ text: String) -> String {
            let data = (try? JSONEncoder().encode(text)) ?? Data("\"\"".utf8)
            return String(decoding: data, as: UTF8.self).replacingOccurrences(of: "\\/", with: "/")
        }
        let entries = choices.filter { !$0.value.empty }.sorted { Array($0.key.utf16).lexicographicallyPrecedes(Array($1.key.utf16)) }.map { id, choice in
            var parts: [String] = []
            if let grouped = choice.grouped { parts.append("\"grouped\":\(grouped)") }
            if let order = choice.order, !order.isEmpty { parts.append("\"order\":[" + order.map(quoted).joined(separator: ",") + "]") }
            return quoted(id) + ":{" + parts.joined(separator: ",") + "}"
        }
        return "{" + entries.joined(separator: ",") + "}"
    }

    /// A collection grouped in the library: its own item (name, pictures) and its titles, films and series, each once.
    struct Shelf: Sendable, Hashable {
        let box: Item
        let titles: [Item]
    }

    /// A collection's titles once each, those of a collection inside it taken in its place: never a pile in a pile.
    static func flatten(_ boxId: String, children: (String) -> [Item]?) -> [Item] {
        var titles: [Item] = []
        var seen = Set<String>(), boxes = Set<String>()
        func walk(_ id: String, _ depth: Int) {
            guard boxes.insert(id.lowercased()).inserted, depth <= 3, let list = children(id) else { return }
            for item in list {
                if item.type == "BoxSet" { walk(item.id, depth + 1) }
                else if TitleCollections.holds(item), seen.insert(item.id.lowercased()).inserted { titles.append(item) }
            }
        }
        walk(boxId, 0)
        return titles
    }

    /// The order the titles came out: year, then day, then name.
    static func byRelease(_ titles: [Item]) -> [Item] {
        titles.enumerated().sorted { a, b in
            let x = a.element, y = b.element
            if (x.productionYear ?? .max) != (y.productionYear ?? .max) { return (x.productionYear ?? .max) < (y.productionYear ?? .max) }
            let p = x.premiereDate ?? "\u{10FFFF}", q = y.premiereDate ?? "\u{10FFFF}"
            if p != q { return p < q }
            let names = (x.name ?? "").localizedCaseInsensitiveCompare(y.name ?? "")
            return names == .orderedSame ? a.offset < b.offset : names == .orderedAscending
        }.map(\.element)
    }

    /// The watch order: the order chosen for the titles it names, then those it does not (added since) in the order
    /// they came out. Without one, the order they came out.
    static func inOrder(_ titles: [Item], _ order: [String]?) -> [Item] {
        let released = byRelease(titles)
        guard let order, !order.isEmpty else { return released }
        var place: [String: Int] = [:]
        for (i, id) in order.enumerated() where place[id.lowercased()] == nil { place[id.lowercased()] = i }
        return released.enumerated().map { (item: $0.element, at: place[$0.element.id.lowercased()] ?? order.count + $0.offset) }
            .sorted { $0.at < $1.at }.map(\.item)
    }

    /// One title moved up or down the list (« Change the watch order »): the ids in their new order.
    static func move(_ titles: [Item], _ index: Int, _ step: Int) -> [String] {
        var ids = titles.map(\.id)
        let to = index + step
        guard ids.indices.contains(index), ids.indices.contains(to) else { return ids }
        ids.swapAt(index, to)
        return ids
    }

    /// Which collection each title is grouped under: a title in two takes the one with more titles.
    struct Index: Sendable {
        let shelves: [Shelf]
        private let byTitle: [String: Int]

        init(_ shelves: [Shelf]) {
            self.shelves = shelves.filter { $0.titles.count >= CollectionShelf.least }.sorted {
                $0.titles.count != $1.titles.count ? $0.titles.count > $1.titles.count : ($0.box.name ?? "").localizedCaseInsensitiveCompare($1.box.name ?? "") == .orderedAscending
            }
            var byTitle: [String: Int] = [:]
            for (i, shelf) in self.shelves.enumerated() { for title in shelf.titles where byTitle[title.id.lowercased()] == nil { byTitle[title.id.lowercased()] = i } }
            self.byTitle = byTitle
        }

        static let none = Index([])
        func of(_ titleId: String) -> Int? { byTitle[titleId.lowercased()] }
        /// The titles of a shelf a page of `types` holds (films on Movies…) that are grouped under it.
        func count(in shelf: Int, _ types: String) -> Int {
            shelves[shelf].titles.count(where: { CollectionShelf.holds(types, $0) && of($0.id) == shelf })
        }
    }

    private static func holds(_ types: String, _ item: Item) -> Bool {
        types.split(separator: ",").contains { $0.caseInsensitiveCompare(item.type ?? "") == .orderedSame }
    }

    /// A page of the grid as the library shows it: the first title of a grouped collection met becomes the collection's
    /// card, its other titles leave the grid; only when the collection has `least` titles of this page's kind.
    /// `placed`: the collections already given a card by an earlier page of the same list. A filter keeps the card when
    /// one of its titles matches it: Jellyfin only sends those.
    static func collapse(_ page: [Item], _ index: Index, _ types: String, placed: inout Set<String>) -> [Item] {
        var shown: [Item] = []
        for item in page {
            guard let at = index.of(item.id), index.count(in: at, types) >= least else { shown.append(item); continue }
            let shelf = index.shelves[at]
            if placed.insert(shelf.box.id.lowercased()).inserted { shown.append(card(shelf)) }
        }
        return shown
    }

    /// The card of a grouped collection: its own item, with its titles (its count, progress and cover).
    static func card(_ shelf: Shelf) -> Item {
        var card = shelf.box
        card.type = "BoxSet"
        card.childCount = shelf.titles.count
        card.shelf = shelf.titles
        var data = card.userData ?? UserData()
        data.played = shelf.titles.allSatisfy(watched)
        data.playedPercentage = share(shelf.titles) * 100
        card.userData = data
        return card
    }

    static func watched(_ title: Item) -> Bool { title.userData?.played == true }
    /// How far a title is, from 0 to 1: a film by its position, a series by its episodes watched.
    static func progress(_ title: Item) -> Double {
        if watched(title) { return 1 }
        if title.isSeries { return min(1, max(0, (title.userData?.playedPercentage ?? 0) / 100)) }
        return ItemText.progress(title)
    }
    /// How far the whole collection is, each title one notch of the bar.
    static func share(_ titles: [Item]) -> Double { titles.isEmpty ? 0 : titles.map(progress).reduce(0, +) / Double(titles.count) }
    /// « UP NEXT »: the first title of the order not watched to its end; none when all are watched (« Watch again »).
    static func upNext(_ ordered: [Item]) -> Int? { ordered.firstIndex { !watched($0) } }

    // MARK: Words, in French or English as Mira is shown (Windows and Mac say the same)

    static var french: Bool { RemoteLines.french }
    private static func L(_ fr: String, _ en: String, _ french: Bool) -> String { french ? fr : en }
    private static func count(_ n: Int, _ fr: (String, String), _ en: (String, String), _ french: Bool) -> String {
        "\(n) " + (french ? (n > 1 ? fr.1 : fr.0) : (n == 1 ? en.0 : en.1))
    }

    /// « 2009 – 2019  ·  14 shows  ·  3 movies  ·  100 episodes ».
    static func facts(_ titles: [Item], french: Bool = french) -> String {
        let years = titles.compactMap(\.productionYear)
        let shows = titles.count(where: \.isSeries), movies = titles.count(where: { $0.type == "Movie" })
        let episodes = titles.filter(\.isSeries).map { $0.recursiveItemCount ?? 0 }.reduce(0, +)
        var parts: [String] = []
        if let low = years.min(), let high = years.max() { parts.append(low == high ? String(low) : "\(low) – \(high)") }
        if shows > 0 { parts.append(count(shows, ("série", "séries"), ("show", "shows"), french)) }
        if movies > 0 { parts.append(count(movies, ("film", "films"), ("movie", "movies"), french)) }
        if episodes > 0 { parts.append(count(episodes, ("épisode", "épisodes"), ("episode", "episodes"), french)) }
        return parts.joined(separator: "  ·  ")
    }
    /// « 01 », the number of a title in the order.
    static func number(_ index: Int) -> String { String(format: "%02d", index + 1) }
    /// « 2016 · 1 h 04 », « 2009 · 15 episodes » under a numbered poster.
    static func titleLine(_ title: Item, french: Bool = french) -> String {
        var parts: [String] = []
        if let year = title.productionYear { parts.append(String(year)) }
        if title.isSeries {
            if let n = title.recursiveItemCount, n > 0 { parts.append(count(n, ("épisode", "épisodes"), ("episode", "episodes"), french)) } else { parts.append(L("Série", "Show", french)) }
        } else if let ticks = title.runTimeTicks, ticks > 0 { parts.append(ItemText.duration(seconds: ticks / ItemText.ticksPerSecond)) }
        return parts.joined(separator: "  ·  ")
    }
    /// « Collection · 17 titles » under the card.
    static func cardLine(_ titles: Int, french: Bool = french) -> String { "Collection  ·  " + count(titles, ("titre", "titres"), ("title", "titles"), french) }
    static func titlesHeading(_ count: Int, french: Bool = french) -> String { L("Titres", "Titles", french) + "  ·  \(count)" }
    static func groupedNotice(_ titles: Int, _ name: String, french: Bool = french) -> String {
        L("\(titles) titres regroupés dans « \(name) ».", "Grouped \(titles) titles as “\(name)”.", french)
    }
    static func ungroupedNotice(_ name: String, french: Bool = french) -> String {
        L("Les titres de « \(name) » sont de nouveau dans la bibliothèque.", "The titles of “\(name)” are back in the library.", french)
    }
    static func automaticNotice(_ titles: Int, _ collections: Int, french: Bool = french) -> String {
        collections == 1 ? L("\(titles) titres regroupés dans une collection.", "Grouped \(titles) titles into a collection.", french)
            : L("\(titles) titres regroupés dans \(collections) collections.", "Grouped \(titles) titles into \(collections) collections.", french)
    }
    /// « 25 movies · 1 collection » at the top of a grid holding cards of collections.
    static func gridCount(_ titles: Int, _ collections: Int, movies: Bool, french: Bool = french) -> String {
        let said = movies ? count(titles, ("film", "films"), ("movie", "movies"), french) : count(titles, ("série", "séries"), ("show", "shows"), french)
        return collections > 0 ? said + "  ·  " + count(collections, ("collection", "collections"), ("collection", "collections"), french) : said
    }
    static func words(_ fr: String, _ en: String) -> String { french ? fr : en }
}

extension JellyfinClient {
    private static let shelfFields = "ProviderIds,ChildCount,RecursiveItemCount,PremiereDate,Overview,Genres,DateCreated"

    /// A collection's films and series, each once, those of a collection inside it taken in its place, with this
    /// account's progress; in no particular order.
    func collectionTitles(_ collectionId: String) async throws -> [Item] {
        var children: [String: [Item]] = [:]
        var waiting: [(id: String, depth: Int)] = [(collectionId, 0)]
        while !waiting.isEmpty {
            let (id, depth) = waiting.removeFirst()
            if children[id.lowercased()] != nil || depth > 3 { continue }
            let list: ItemList = try await get("Items", [
                "userId": userId, "parentId": id, "fields": Self.shelfFields, "enableImageTypes": "Primary,Backdrop,Thumb,Logo", "imageTypeLimit": "1", "enableUserData": "true",
            ])
            let items = list.items ?? []
            children[id.lowercased()] = items
            for inner in items where inner.type == "BoxSet" { waiting.append((inner.id, depth + 1)) }
        }
        return CollectionShelf.flatten(collectionId) { children[$0.lowercased()] }
    }

    /// The collections grouped in this account's library, with their titles: a collection made by hand is asked only
    /// when this account grouped it.
    func shelves(_ choices: [String: CollectionShelf.Choice]) async throws -> CollectionShelf.Index {
        let list: ItemList = try await get("Items", [
            "userId": userId, "recursive": "true", "includeItemTypes": "BoxSet", "sortBy": "SortName",
            "fields": "ProviderIds,ChildCount", "enableImageTypes": "Primary,Backdrop,Thumb,Logo", "imageTypeLimit": "1", "enableUserData": "true",
        ])
        let boxes = (list.items ?? []).filter { $0.type == "BoxSet" && $0.childCount != 0 && CollectionShelf.grouped($0, choices[$0.id]) }
        var shelves: [CollectionShelf.Shelf] = []
        // A few at a time, as Windows and Mac ask them.
        for start in stride(from: 0, to: boxes.count, by: 6) {
            let batch = Array(boxes[start..<min(start + 6, boxes.count)])
            try await withThrowingTaskGroup(of: CollectionShelf.Shelf.self) { group in
                for box in batch { group.addTask { CollectionShelf.Shelf(box: box, titles: try await self.collectionTitles(box.id)) } }
                for try await shelf in group { shelves.append(shelf) }
            }
        }
        return CollectionShelf.Index(shelves)
    }

    /// This account's choices for its collections, kept beside « mira.choices ».
    func shelfChoices() async throws -> [String: CollectionShelf.Choice] {
        CollectionShelf.read(try await choices().object?["CustomPrefs"]?.object?[CollectionShelf.preference])
    }

    /// One collection's choice changed, the others and Mira's other preferences read just before and sent back whole.
    @discardableResult
    func setShelfChoice(_ collectionId: String, _ change: @Sendable (CollectionShelf.Choice) -> CollectionShelf.Choice) async throws -> [String: CollectionShelf.Choice] {
        var preferences = try await choices().object ?? [:]
        var custom = preferences["CustomPrefs"]?.object ?? [:]
        var all = CollectionShelf.read(custom[CollectionShelf.preference])
        let changed = change(all[collectionId] ?? CollectionShelf.Choice())
        if changed.empty { all[collectionId] = nil } else { all[collectionId] = changed }
        custom[CollectionShelf.preference] = .string(CollectionShelf.write(all))
        preferences["CustomPrefs"] = .object(custom)
        try await saveChoices(.object(preferences))
        return all
    }
}

/// The collections grouped for the account signed in, and its choices, kept ten minutes: each grid's pages ask for them.
@MainActor
final class ShelfGrouping {
    static let shared = ShelfGrouping()
    private var kept: (key: String, index: CollectionShelf.Index, choices: [String: CollectionShelf.Choice], at: Date)?

    private func key(_ client: JellyfinClient) -> String { "\(client.server.absoluteString)|\(client.userId ?? "")" }

    /// Read again at the next grid (a title watched: the cards' progress), kept meanwhile should Jellyfin not answer.
    func forget() { if let known = kept { kept = (known.key, known.index, known.choices, .distantPast) } }

    func choices(_ client: JellyfinClient) -> [String: CollectionShelf.Choice] { kept?.key == key(client) ? kept?.choices ?? [:] : [:] }
    func shelf(_ client: JellyfinClient, _ boxId: String) -> CollectionShelf.Shelf? {
        guard kept?.key == key(client) else { return nil }
        return kept?.index.shelves.first { $0.box.id == boxId }
    }

    /// The collections grouped; none when Jellyfin does not say (the titles then show as they are). `fresh`: what was
    /// grouped for the first time on its own (sagas), to say once.
    func index(_ client: JellyfinClient) async -> (index: CollectionShelf.Index, automatic: [CollectionShelf.Shelf]) {
        let key = key(client)
        if let kept, kept.key == key, Date.now.timeIntervalSince(kept.at) < 600 { return (kept.index, []) }
        do {
            let choices = try await client.shelfChoices()
            let index = try await client.shelves(choices)
            kept = (key, index, choices, .now)
            return (index, index.shelves.filter { choices[$0.box.id] == nil })
        } catch {
            return (kept?.key == key ? kept!.index : .none, [])
        }
    }

    /// Whether a collection's titles are one card in the library for this account (its choice, else its kind).
    func isGrouped(_ client: JellyfinClient, _ box: Item) -> Bool {
        if kept?.key == key(client), let kept {
            if let chosen = kept.choices[box.id]?.grouped { return chosen }
            if kept.index.shelves.contains(where: { $0.box.id == box.id }) { return true }
        }
        return box.shelf != nil || CollectionShelf.automatic(box)
    }

    /// The choices as Jellyfin keeps them now (after a change made here).
    func took(_ client: JellyfinClient, _ choices: [String: CollectionShelf.Choice]) {
        if let known = kept, known.key == key(client) { kept = (known.key, known.index, choices, .distantPast) }
    }
}
