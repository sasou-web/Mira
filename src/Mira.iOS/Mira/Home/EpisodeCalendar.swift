import Foundation
import SwiftUI

/// « Missing episodes and what comes next »: what Jellyfin knows of a series beyond its files, once its TheTVDB
/// extension is installed (a virtual episode, LocationType « Virtual », for each episode the library lacks, with the day
/// it airs). Missing: aired before today; coming: today or later; announced without a day: neither. The specials
/// (season 0) never count. As Mira for Windows and Mac say it (EpisodeCalendar of Core, cases in
/// tests/fixtures/calendar.json, written in French there: `french` says them so).
enum EpisodeCalendar {
    static let maxRanges = 3

    /// A day as TheTVDB gives it (Jellyfin keeps it at midnight UTC): the days since 1970-01-01.
    struct Day: Hashable, Comparable, Sendable {
        let number: Int
        static func < (a: Day, b: Day) -> Bool { a.number < b.number }

        private static let utc: Calendar = {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = TimeZone(identifier: "UTC")!
            return calendar
        }()

        init(number: Int) { self.number = number }

        init?(year: Int, month: Int, day: Int) {
            guard let date = Self.utc.date(from: DateComponents(year: year, month: month, day: day)) else { return nil }
            number = Int((date.timeIntervalSince1970 / 86_400).rounded(.down))
        }

        /// « 2026-10-29 », or the start of Jellyfin's « 2026-10-29T00:00:00.0000000Z » (always in UTC).
        init?(_ text: String?) {
            guard let text, text.count >= 10 else { return nil }
            let parts = text.prefix(10).split(separator: "-").compactMap { Int($0) }
            guard parts.count == 3 else { return nil }
            self.init(year: parts[0], month: parts[1], day: parts[2])
        }

        /// Today on this iPhone, as a day.
        static func today(_ now: Date = .now) -> Day {
            let parts = Calendar.current.dateComponents([.year, .month, .day], from: now)
            return Day(year: parts.year ?? 1970, month: parts.month ?? 1, day: parts.day ?? 1) ?? Day(number: 0)
        }

        var date: Date { Date(timeIntervalSince1970: Double(number) * 86_400) }
        var year: Int { Self.utc.component(.year, from: date) }
    }

    static var french: Bool { RemoteLines.french }

    /// The day an episode airs; nil without one.
    static func airDay(_ episode: Item) -> Day? { Day(episode.premiereDate) }

    /// Of a series' episodes (its virtual ones at least): those missing and those coming, each in the order they air.
    static func read(_ episodes: [Item], today: Day) -> (missing: [Item], coming: [Item]) {
        let known = episodes.filter { $0.type == "Episode" && $0.locationType == "Virtual" && ($0.parentIndexNumber ?? 0) >= 1 && $0.indexNumber != nil && airDay($0) != nil }
            .sorted { a, b in
                let (x, y) = (airDay(a)!, airDay(b)!)
                if x != y { return x < y }
                if a.parentIndexNumber != b.parentIndexNumber { return (a.parentIndexNumber ?? 0) < (b.parentIndexNumber ?? 0) }
                return (a.indexNumber ?? 0) < (b.indexNumber ?? 0)
            }
        return (known.filter { airDay($0)! < today }, known.filter { airDay($0)! >= today })
    }

    /// « S2 · É5 », « S2 · É5–8 » for episodes in a row, `maxRanges` at most, and how many episodes are left out.
    static func ranges(_ episodes: [Item], french: Bool = EpisodeCalendar.french) -> (labels: [String], others: Int) {
        var runs: [(season: Int, from: Int, to: Int, count: Int)] = []
        let ordered = episodes.sorted { ($0.parentIndexNumber ?? 0, $0.indexNumber ?? 0) < ($1.parentIndexNumber ?? 0, $1.indexNumber ?? 0) }
        for x in ordered {
            if let last = runs.last, last.season == (x.parentIndexNumber ?? 0), x.indexNumber == last.to + 1 {
                runs[runs.count - 1] = (last.season, last.from, last.to + 1, last.count + 1)
            } else {
                runs.append((x.parentIndexNumber ?? 0, x.indexNumber ?? 0, x.indexNumber ?? 0, 1))
            }
        }
        let e = french ? "É" : "E"
        let labels = runs.prefix(maxRanges).map { "S\($0.season) · \(e)" + ($0.from == $0.to ? "\($0.from)" : "\($0.from)–\($0.to)") }
        return (labels, runs.dropFirst(maxRanges).reduce(0) { $0 + $1.count })
    }

    /// « 2 épisodes manquants : S1 · É3, S2 · É2 », « … et 6 autres »; empty without any.
    static func missingText(_ missing: [Item], french: Bool = EpisodeCalendar.french) -> String {
        guard !missing.isEmpty else { return "" }
        let (labels, rest) = ranges(missing, french: french)
        let list = labels.joined(separator: ", ") + (rest > 0 ? (french ? " et \(rest) autres" : " and \(rest) more") : "")
        let n = missing.count
        let count = french ? "\(n) " + (n == 1 ? "épisode manquant" : "épisodes manquants") : "\(n) " + (n == 1 ? "missing episode" : "missing episodes")
        return count + (french ? " : " : ": ") + list
    }

    /// « Prochain épisode : S2 · É3, samedi »; empty without one.
    static func nextText(_ coming: [Item], today: Day, french: Bool = EpisodeCalendar.french) -> String {
        guard let first = coming.first, let day = airDay(first) else { return "" }
        return (french ? "Prochain épisode : " : "Next episode: ") + "\(code(first, french: french)), \(dayText(day, today: today, french: french))"
    }

    /// « S2 · É3 ».
    static func code(_ episode: Item, french: Bool = EpisodeCalendar.french) -> String {
        "S\(episode.parentIndexNumber ?? 0) · \(french ? "É" : "E")\(episode.indexNumber ?? 0)"
    }

    /// A day as a sentence says it from `today`: « aujourd’hui », « demain », « hier », « samedi » within the week,
    /// « jeudi 5 novembre » beyond, with its year in another year.
    static func dayText(_ day: Day, today: Day, french: Bool = EpisodeCalendar.french) -> String {
        let apart = day.number - today.number
        if apart == 0 { return french ? "aujourd’hui" : "today" }
        if apart == 1 { return french ? "demain" : "tomorrow" }
        if apart == -1 { return french ? "hier" : "yesterday" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: french ? "fr_FR" : "en_US")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.calendar = Calendar(identifier: .gregorian)
        let year = day.year != today.year
        if (2...6).contains(apart) { formatter.dateFormat = "EEEE" }
        else if french { formatter.dateFormat = year ? "EEEE d MMMM yyyy" : "EEEE d MMMM" }
        else { formatter.dateFormat = year ? "EEEE, MMMM d, yyyy" : "EEEE, MMMM d" }
        return formatter.string(from: day.date)
    }

    /// The same at the head of a line: « Aujourd’hui », « Samedi ».
    static func dayTitle(_ day: Day, today: Day, french: Bool = EpisodeCalendar.french) -> String {
        let text = dayText(day, today: today, french: french)
        return text.prefix(1).uppercased() + text.dropFirst()
    }

    /// Coming episodes by day, in order.
    static func byDay(_ coming: [Item]) -> [(day: Day, episodes: [Item])] {
        let dated: [(item: Item, day: Day)] = coming.compactMap { item in airDay(item).map { (item: item, day: $0) } }
        let ordered = dated.sorted { a, b in
            if a.day != b.day { return a.day < b.day }
            return (a.item.parentIndexNumber ?? 0, a.item.indexNumber ?? 0) < (b.item.parentIndexNumber ?? 0, b.item.indexNumber ?? 0)
        }
        var days: [(day: Day, episodes: [Item])] = []
        for x in ordered {
            if days.last?.day == x.day { days[days.count - 1].episodes.append(x.item) } else { days.append((x.day, [x.item])) }
        }
        return days
    }
}

extension JellyfinClient {
    /// The virtual episodes of every series this account sees (TheTVDB's « Missing Episode Fetcher »), with their air
    /// day. Asked across the libraries, as Core does: Jellyfin leaves them out of a series' episodes (checked on 12.1).
    /// Empty without the plugin. All of them, page after page: sorted by air day, one page of a large library left out
    /// the newest, those coming first (checked on 12.1 with 5,057: none past 2020 in the first 5,000).
    func virtualEpisodes() async throws -> [Item] {
        let page = 5000
        var all: [Item] = []
        var seen = Set<String>()
        var start = 0
        while true {
            let list: ItemList = try await get("Items", [
                "userId": userId, "recursive": "true", "includeItemTypes": "Episode", "locationTypes": "Virtual", "fields": "PremiereDate",
                "sortBy": "PremiereDate,SeriesSortName,ParentIndexNumber,IndexNumber", "enableImages": "false", "enableUserData": "false",
                "startIndex": String(start), "limit": String(page),
            ])
            let items = list.items ?? []
            start += items.count
            let fresh = items.filter { seen.insert($0.id.lowercased()).inserted }
            all += fresh
            // The last page, or a server that gives the same page again: nothing more to ask.
            if items.count < page || fresh.isEmpty { return all }
            if let total = list.totalRecordCount, start >= total { return all }
        }
    }
}

/// The virtual episodes, read once for the whole library and kept a minute, as Windows and Mac keep them: every
/// series page opened in that minute reads them from here.
@MainActor
enum VirtualEpisodes {
    private static var kept: (key: String, at: Date, items: [Item])?

    static func of(series: String, client: JellyfinClient) async throws -> [Item] {
        let key = "\(client.server.absoluteString)|\(client.userId ?? "")"
        let items: [Item]
        if let kept, kept.key == key, Date.now.timeIntervalSince(kept.at) < 60 { items = kept.items } else {
            items = try await client.virtualEpisodes()
            kept = (key, .now, items)
        }
        return items.filter { $0.seriesId == series }
    }

    static func forget() { kept = nil }
}
