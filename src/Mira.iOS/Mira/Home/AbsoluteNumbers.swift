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
            // The season's last number at TheTVDB: its virtual episodes, and the held ones it describes (« S02E13 » to « S02E24 »
            // held, S02E01–12 missing: the second half of a season of 24, never numbered from the start).
            let last = (known.filter { $0.parentIndexNumber == season }.compactMap(\.indexNumber)
                + mine.filter(described).map { $0.indexNumberEnd ?? $0.indexNumber ?? 0 }).max() ?? 0
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

    /// A held episode TheTVDB knows under its number: Jellyfin gave it an overview, or a name that is not its number
    /// (a file's: « Shingeki no Kyojin - S02E26 », « [SubsPlease] Show - 26 », « Episode 26 »).
    static func described(_ episode: Item) -> Bool {
        if let overview = episode.overview, !overview.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return true }
        guard let name = episode.name?.trimmingCharacters(in: .whitespaces), !name.isEmpty, let number = episode.indexNumber else { return false }
        let digits = String(number)
        for run in name.split(whereSeparator: { !$0.isASCII || !$0.isNumber }) {
            let trimmed = run.drop { $0 == "0" }
            if String(trimmed) == digits { return false }
        }
        return true
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
        // A double episode held (« S01E03-E04 ») holds both: never the second « missing ».
        let doubles = held.filter { x in x.parentIndexNumber != nil && (x.indexNumberEnd ?? 0) > (x.indexNumber ?? Int.max) }
        let virtuals = virtuals.filter { v in
            !doubles.contains { x in x.parentIndexNumber == v.parentIndexNumber && (x.indexNumber ?? 0) <= (v.indexNumber ?? -1) && (v.indexNumber ?? -1) <= (x.indexNumberEnd ?? 0) }
        }
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
