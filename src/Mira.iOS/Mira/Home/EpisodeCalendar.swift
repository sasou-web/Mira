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

    // The words of the list, as Windows and Mac write them.
    static func missingHeading(_ count: Int, french: Bool = EpisodeCalendar.french) -> String { french ? (count == 1 ? "Manquant" : "Manquants") : "Missing" }
    static func comingHeading(french: Bool = EpisodeCalendar.french) -> String { french ? "À venir" : "Coming up" }
    static func aired(_ day: Day, today: Day, french: Bool = EpisodeCalendar.french) -> String { (french ? "Sorti " : "Aired ") + dayText(day, today: today, french: french) }
    static func others(_ count: Int, french: Bool = EpisodeCalendar.french) -> String { french ? "Et \(count) " + (count == 1 ? "autre" : "autres") : "And \(count) more" }
    static func source(french: Bool = EpisodeCalendar.french) -> String {
        french ? "D’après TheTVDB, que ton serveur Jellyfin consulte avec son extension : ces épisodes ne sont pas dans ta bibliothèque."
            : "From TheTVDB, which your Jellyfin server checks with its plugin: these episodes aren’t in your library."
    }
}

extension JellyfinClient {
    /// The virtual episodes of every series this account sees (TheTVDB's « Missing Episode Fetcher »), with their air
    /// day. Asked across the libraries, as Core does: Jellyfin leaves them out of a series' episodes (checked on 12.1).
    /// Empty without the plugin.
    func virtualEpisodes() async throws -> [Item] {
        let list: ItemList = try await get("Items", [
            "userId": userId, "recursive": "true", "includeItemTypes": "Episode", "locationTypes": "Virtual", "fields": "PremiereDate",
            "sortBy": "PremiereDate,ParentIndexNumber,IndexNumber", "enableImages": "false", "enableUserData": "false", "limit": "5000",
        ])
        return list.items ?? []
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

/// Above a series' episodes: « 2 missing episodes: S1 · E3, S2 · E2 » or « Next episode: S2 · E3, Saturday », opened
/// into their list (TheTVDB, through Jellyfin). Nothing without Jellyfin's TheTVDB extension.
struct EpisodeCalendarLine: View {
    let series: Item
    @Environment(AppModel.self) private var app
    @State private var schedule: (missing: [Item], coming: [Item]) = ([], [])
    @State private var open = false

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        let (missing, coming) = schedule
        let first = missing.isEmpty ? EpisodeCalendar.nextText(coming, today: today) : EpisodeCalendar.missingText(missing)
        Group {
            if !first.isEmpty {
                VStack(alignment: .leading, spacing: 0) {
                    Button {
                        withAnimation(.easeOut(duration: 0.2)) { open.toggle() }
                    } label: {
                        HStack(spacing: 12) {
                            Image(systemName: missing.isEmpty ? "calendar" : "exclamationmark.triangle")
                                .font(.body.weight(.semibold))
                                .foregroundStyle(missing.isEmpty ? Palette.muted : Palette.warning)
                                .accessibilityHidden(true)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(verbatim: first).font(MiraFont.subheadline).foregroundStyle(Palette.ink)
                                if !missing.isEmpty, !coming.isEmpty {
                                    Text(verbatim: EpisodeCalendar.nextText(coming, today: today)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                                }
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .multilineTextAlignment(.leading)
                            Image(systemName: "chevron.down")
                                .font(.footnote.weight(.bold))
                                .foregroundStyle(Palette.muted)
                                .rotationEffect(.degrees(open ? 180 : 0))
                                .accessibilityHidden(true)
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 12)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint(Text(open ? "Hides the list" : "Shows the list"))
                    .accessibilityIdentifier("series.calendar")
                    if open { list(missing: missing, coming: coming, today: today) }
                }
                .background(Palette.raised, in: .rect(cornerRadius: 14, style: .continuous))
            }
        }
        .task(id: series.id) { await load() }
    }

    @ViewBuilder private func list(missing: [Item], coming: [Item], today: EpisodeCalendar.Day) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(verbatim: EpisodeCalendar.source()).font(MiraFont.footnote).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
            if !missing.isEmpty {
                heading(EpisodeCalendar.missingHeading(missing.count))
                ForEach(missing.prefix(30), id: \.id) { x in line(x, EpisodeCalendar.aired(EpisodeCalendar.airDay(x)!, today: today)) }
                if missing.count > 30 { Text(verbatim: EpisodeCalendar.others(missing.count - 30)).font(MiraFont.footnote).foregroundStyle(Palette.muted) }
            }
            if !coming.isEmpty {
                heading(EpisodeCalendar.comingHeading())
                ForEach(coming.prefix(12), id: \.id) { x in line(x, EpisodeCalendar.dayTitle(EpisodeCalendar.airDay(x)!, today: today)) }
            }
        }
        .padding(.horizontal, 14)
        .padding(.bottom, 14)
        .accessibilityIdentifier("series.calendar.list")
    }

    private func heading(_ text: String) -> some View {
        Text(verbatim: text).font(MiraFont.footnote).foregroundStyle(Palette.faint).padding(.top, 6).accessibilityAddTraits(.isHeader)
    }

    /// « S2 · E3 · Its name », the day on the right (under it at the largest text sizes).
    private func line(_ episode: Item, _ when: String) -> some View {
        let name = [EpisodeCalendar.code(episode), episode.name ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
        return ViewThatFits(in: .horizontal) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text(verbatim: name).font(MiraFont.footnote).foregroundStyle(Palette.ink).lineLimit(1)
                Spacer(minLength: 0)
                Text(verbatim: when).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(verbatim: name).font(MiraFont.footnote).foregroundStyle(Palette.ink)
                Text(verbatim: when).font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
        }
        .accessibilityElement(children: .combine)
    }

    private func load() async {
        // A friend's party's guest reaches only what the party lets through.
        guard let client = app.client, app.party.guest == nil else { return }
        do {
            let episodes = try await VirtualEpisodes.of(series: series.id, client: client)
            schedule = EpisodeCalendar.read(episodes, today: .today())
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch {
            // Optional, as on Windows and Mac: the episodes say it when Jellyfin is out of reach.
            schedule = ([], [])
        }
    }
}
