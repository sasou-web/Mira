import Foundation

/// Episodes numbered from the series' start in their season (« Season 02/Show - S02E26 », an anime pack numbered 1 to
/// 89 across four seasons), where TheTVDB numbers each season from 1: Jellyfin then keeps a virtual episode for each of
/// the season's numbers beside the held ones. A held season reads as numbered from the start when every one of its
/// episodes is past the season's last number at TheTVDB and past the episodes of the seasons before, and lands inside
/// the season once those are taken off. Its virtual episodes held this way are not missing. As Windows and Mac read
/// them (AbsoluteNumbers of Core, cases in tests/fixtures/absolute.json); renaming the files is Mira for Windows' work
/// on the PC of Jellyfin.
enum AbsoluteNumbers {
    struct Place: Hashable { let season: Int; let episode: Int }

    /// The number in its season of each held episode numbered from the series' start: episode id → its place.
    static func read(_ held: [Item], virtuals: [Item]) -> [String: Place] {
        let own = held.filter { $0.locationType != "Virtual" && ($0.parentIndexNumber ?? 0) >= 1 && ($0.indexNumber ?? 0) >= 1 }
        let known = virtuals.filter { ($0.parentIndexNumber ?? 0) >= 1 && ($0.indexNumber ?? 0) >= 1 }
        var found: [String: Place] = [:]
        guard !known.isEmpty else { return found }
        var offset = 0
        for season in Set((own + known).compactMap(\.parentIndexNumber)).sorted() {
            let mine = own.filter { $0.parentIndexNumber == season }
            let last = known.filter { $0.parentIndexNumber == season }.compactMap(\.indexNumber).max() ?? 0
            func inside(_ x: Item) -> Bool {
                let number = x.indexNumber ?? 0
                return number > last && number > offset && (x.indexNumberEnd ?? number) - offset <= last
            }
            if offset > 0, last > 0, !mine.isEmpty, mine.allSatisfy(inside) {
                for x in mine { found[x.id.lowercased()] = Place(season: season, episode: (x.indexNumber ?? 0) - offset) }
                offset += last
            } else {
                offset += max(last, mine.map { $0.indexNumberEnd ?? $0.indexNumber ?? 0 }.max() ?? 0)
            }
        }
        return found
    }

    /// The held episodes, those numbered from the start under their season's number (« S02E26 » as S02E01).
    static func seasonal(_ held: [Item], virtuals: [Item]) -> [Item] {
        let read = read(held, virtuals: virtuals)
        guard !read.isEmpty else { return held }
        return held.map { x in
            guard let place = read[x.id.lowercased()], let number = x.indexNumber else { return x }
            var moved = x
            moved.indexNumber = place.episode
            moved.indexNumberEnd = x.indexNumberEnd.map { $0 - number + place.episode }
            return moved
        }
    }

    /// The virtual episodes the library doesn't hold under another number: those still missing or to come.
    static func unheld(_ virtuals: [Item], held: [Item]) -> [Item] {
        let read = read(held, virtuals: virtuals)
        guard !read.isEmpty else { return virtuals }
        var taken = Set<Place>()
        for x in held {
            guard let place = read[x.id.lowercased()], let number = x.indexNumber else { continue }
            for n in place.episode...(place.episode + (x.indexNumberEnd ?? number) - number) { taken.insert(Place(season: place.season, episode: n)) }
        }
        return virtuals.filter { x in
            guard let season = x.parentIndexNumber, let number = x.indexNumber else { return true }
            return !taken.contains(Place(season: season, episode: number))
        }
    }
}
