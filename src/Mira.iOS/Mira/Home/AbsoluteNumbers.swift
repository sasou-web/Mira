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
    static func read(_ held: [Item], virtuals: [Item], now: Date = Date()) -> [String: Place] {
        let own = held.filter { $0.locationType != "Virtual" && ($0.parentIndexNumber ?? 0) >= 1 && ($0.indexNumber ?? 0) >= 1 }
        let known = virtuals.filter { ($0.parentIndexNumber ?? 0) >= 1 && ($0.indexNumber ?? 0) >= 1 }
        var found: [String: Place] = [:]
        guard !known.isEmpty else { return found }
        let seasons = Set((own + known).compactMap(\.parentIndexNumber)).sorted()
        // The season's last number at TheTVDB: its virtual episodes, and the held ones it describes (« S02E13 » to « S02E24 »
        // held, S02E01–12 missing: the second half of a season of 24, never numbered from the start).
        func last(_ season: Int) -> Int {
            (known.filter { $0.parentIndexNumber == season }.compactMap(\.indexNumber)
                + own.filter { $0.parentIndexNumber == season && described($0) }.map { $0.indexNumberEnd ?? $0.indexNumber ?? 0 }).max() ?? 0
        }
        spill(own, known: known, seasons: seasons, last: last, now: now, into: &found)
        var offset = 0
        for season in seasons {
            let mine = own.filter { $0.parentIndexNumber == season && found[$0.id.lowercased()] == nil }
            let last = last(season)
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

    /// A pack of several seasons numbered from 1 to its end in one season (« Season 1-2 », « - 01 » to « - 37 », all in
    /// season 1): the held episodes past the season's last number at TheTVDB belong to the seasons after it, when those
    /// hold nothing, are known to TheTVDB and have aired. All of them land, or none (as Core's AbsoluteNumbers.Spill).
    private static func spill(_ own: [Item], known: [Item], seasons: [Int], last lastOf: (Int) -> Int, now: Date, into found: inout [String: Place]) {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "yyyy-MM-dd"
        let today = formatter.string(from: now)
        func coming(_ x: Item) -> Bool { x.premiereDate.map { String($0.prefix(10)) > today } ?? false }
        var filled = Set<Int>()
        for season in seasons where !filled.contains(season) {
            let last = lastOf(season)
            guard last > 0 else { continue }
            let past = own.filter { $0.parentIndexNumber == season && ($0.indexNumber ?? 0) > last }
            // Numbered on from the season's own episodes: never a season numbered from the series' start (« S02E26 », read).
            guard !past.isEmpty, own.contains(where: { $0.parentIndexNumber == season && ($0.indexNumber ?? 0) <= last }) else { continue }
            // The seasons right after it, held nothing: as many of their numbers as have aired.
            var after: [(season: Int, size: Int)] = []
            var next = season + 1
            while !own.contains(where: { $0.parentIndexNumber == next }) {
                let listed = known.filter { $0.parentIndexNumber == next }
                let aired = listed.filter { !coming($0) }.compactMap(\.indexNumber).max() ?? 0
                if aired == 0 { break }
                after.append((next, aired))
                if listed.contains(where: coming) { break }
                next += 1
            }
            guard !after.isEmpty else { continue }
            func at(_ number: Int) -> Place? {
                var rest = number - last
                for (next, size) in after {
                    if rest <= size { return Place(season: next, episode: rest) }
                    rest -= size
                }
                return nil
            }
            var places: [String: Place] = [:]
            for x in past {
                let number = x.indexNumber ?? 0
                // A file of two episodes stays in one season.
                guard let start = at(number), let end = at(x.indexNumberEnd ?? number), end.season == start.season else { places = [:]; break }
                places[x.id.lowercased()] = start
            }
            for (id, place) in places { found[id] = place; filled.insert(place.season) }
        }
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
            moved.parentIndexNumber = place.season
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
