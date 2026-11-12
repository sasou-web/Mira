import Foundation
import SwiftUI

/// Missing and coming episodes « à leur place » (ogsas, 2026-11-12, mock-up B), as Windows and Mac show them
/// (EpisodeShelf of Core, cases in tests/fixtures/calendar.json, « shelf », written in French there: `french` says
/// them so). No card above a series' episodes: in the season's grid of tiles, the episodes the library lacks are one
/// hollow tile where they would be (opened into one tile each), the next one to come out a hollow tile at its place,
/// those after it one more; a season
/// the library has none of is among the seasons (« Season 1 · not in library »), shown as a calm card. Nothing in
/// amber: some never want those episodes, and « Show missing episodes » off keeps only those coming.
enum EpisodeShelf {
    enum Kind: String, Sendable {
        /// An episode of the library.
        case episode
        /// Episodes aired and not in the library, side by side: one thin line where they would be.
        case missing
        /// The next episode to come out in this season: a row of its own, hollow, with its day.
        case coming
        /// The episodes announced after it: one thin line.
        case later
    }

    struct Line: Identifiable {
        let kind: Kind
        var episodes: [Item]
        var first: Item { episodes[0] }
        var id: String { "\(kind.rawValue)-\(first.id)" }
    }

    typealias Day = EpisodeCalendar.Day
    static var french: Bool { EpisodeCalendar.french }

    /// The lines of a season: `shown` (its episodes of the library, in Jellyfin's order), with the series' virtual
    /// episodes of this season at their place by number. A slice of a long season takes those between its first and
    /// last episode, the first slice those before, the last those after (the iPhone lists a season whole).
    static func lines(_ shown: [Item], virtuals: [Item], season: Int, firstPage: Bool = true, lastPage: Bool = true, today: Day, showMissing: Bool) -> [Line] {
        let numbers = shown.compactMap(\.indexNumber)
        let low = firstPage || numbers.isEmpty ? Int.min : numbers.min()!
        let high = lastPage || numbers.isEmpty ? Int.max : numbers.max()!
        let held = Set(numbers)
        var seen = Set<Int>()
        let absent = dated(virtuals)
            .filter { $0.parentIndexNumber == season && $0.indexNumber! >= low && $0.indexNumber! <= high && !held.contains($0.indexNumber!) }
            .sorted { $0.indexNumber! < $1.indexNumber! }
            .filter { seen.insert($0.indexNumber!).inserted }
        // The library's episodes in their order, each virtual one before the first with a higher number.
        var merged: [(item: Item, virtual: Bool)] = []
        var next = 0
        for episode in shown {
            if let number = episode.indexNumber {
                while next < absent.count, absent[next].indexNumber! < number { merged.append((absent[next], true)); next += 1 }
            }
            merged.append((episode, false))
        }
        while next < absent.count { merged.append((absent[next], true)); next += 1 }
        var lines: [Line] = []
        var coming = false
        for (item, isVirtual) in merged {
            let kind: Kind = !isVirtual ? .episode : EpisodeCalendar.airDay(item)! < today ? .missing : coming ? .later : .coming
            if kind == .missing && !showMissing { continue }
            if kind == .coming { coming = true }
            if kind == .missing || kind == .later, let last = lines.last, last.kind == kind {
                lines[lines.count - 1].episodes.append(item)
            } else {
                lines.append(Line(kind: kind, episodes: [item]))
            }
        }
        return lines
    }

    /// The seasons the library has none of (`held`: the numbers of its seasons), but TheTVDB knows dated episodes of:
    /// numbered seasons only, in order; with « Show missing episodes » off, only those with an episode still to come out.
    static func absentSeasons(held: Set<Int>, virtuals: [Item], today: Day, showMissing: Bool) -> [Int] {
        let seasons = dated(virtuals)
            .filter { !held.contains($0.parentIndexNumber!) && (showMissing || EpisodeCalendar.airDay($0)! >= today) }
            .map { $0.parentIndexNumber! }
        return Array(Set(seasons)).sorted()
    }

    /// In the season list: « Saison 1 · absente », « Saison 3 · à venir ».
    static func seasonLabel(_ season: Int, virtuals: [Item], today: Day, french: Bool = EpisodeShelf.french) -> String {
        let aired = dated(virtuals).contains { $0.parentIndexNumber == season && EpisodeCalendar.airDay($0)! < today }
        if french { return "Saison \(season) · " + (aired ? "absente" : "à venir") }
        return "Season \(season) · " + (aired ? "not in library" : "coming")
    }

    /// The episodes of a season the library has none of, in order.
    static func season(_ season: Int, virtuals: [Item]) -> [Item] {
        dated(virtuals).filter { $0.parentIndexNumber == season }.sorted { $0.indexNumber! < $1.indexNumber! }
    }

    /// « É3 », « É3–4 ».
    static func code(_ run: [Item], french: Bool = EpisodeShelf.french) -> String {
        let (from, to) = (run.first?.indexNumber ?? 0, run.last?.indexNumber ?? 0)
        return (french ? "É" : "E") + (from == to ? "\(from)" : "\(from)–\(to)")
    }

    /// « É3–4 · pas dans ta bibliothèque · sortis du 25 oct. au 1 nov. », a thin line of missing episodes.
    static func missingText(_ run: [Item], today: Day, french: Bool = EpisodeShelf.french) -> String {
        "\(code(run, french: french)) · \(french ? "pas dans ta bibliothèque" : "not in your library") · \(aired(run, today: today, french: french))"
    }

    /// « É7–9 · à venir · du 22 nov. au 6 déc. », a thin line of the episodes announced after the next one.
    static func laterText(_ run: [Item], today: Day, french: Bool = EpisodeShelf.french) -> String {
        "\(code(run, french: french)) · \(french ? "à venir" : "coming") · \(when(run, today: today, french: french))"
    }

    /// When the episodes of a run come out: « samedi », « du 22 nov. au 6 déc. ».
    static func when(_ run: [Item], today: Day, french: Bool = EpisodeShelf.french) -> String {
        run.count == 1
            ? EpisodeCalendar.dayText(day(run[0]), today: today, french: french)
            : span(run, today: today, before: french ? "du " : "", between: french ? " au " : " – ", french: french)
    }

    /// « sorti mardi 20 octobre », « sortis du 8 janv. au 25 juin 2026 ».
    static func aired(_ run: [Item], today: Day, french: Bool = EpisodeShelf.french) -> String {
        if run.count == 1 { return (french ? "sorti " : "aired ") + EpisodeCalendar.dayText(day(run[0]), today: today, french: french) }
        return (french ? "sortis du " : "aired ") + span(run, today: today, before: "", between: french ? " au " : " – ", french: french)
    }

    /// The title of a hollow row: the name TheTVDB gave, else « Épisode 6 ».
    static func name(_ episode: Item, french: Bool = EpisodeShelf.french) -> String {
        if let name = episode.name, !name.isEmpty { return name }
        return (french ? "Épisode " : "Episode ") + "\(episode.indexNumber ?? 0)"
    }

    static func notOut(french: Bool = EpisodeShelf.french) -> String { french ? "Pas encore sorti." : "Not out yet." }

    /// The card of a season the library has none of.
    static func seasonTitle(_ season: Int, french: Bool = EpisodeShelf.french) -> String {
        french ? "La saison \(season) n’est pas dans ta bibliothèque" : "Season \(season) isn’t in your library"
    }

    /// « 25 épisodes, sortis du 8 janv. au 25 juin 2026. D’après TheTVDB, par ton serveur Jellyfin. »
    static func seasonText(_ run: [Item], today: Day, french: Bool = EpisodeShelf.french) -> String {
        let n = run.count
        let count = "\(n) " + (french ? (n == 1 ? "épisode" : "épisodes") : (n == 1 ? "episode" : "episodes"))
        return "\(count), \(aired(run, today: today, french: french)). \(source(french: french))"
    }

    static func source(french: Bool = EpisodeShelf.french) -> String {
        french ? "D’après TheTVDB, par ton serveur Jellyfin." : "From TheTVDB, through your Jellyfin server."
    }

    /// The button of a thin line: its episodes one by one.
    static func show(_ count: Int, open: Bool, french: Bool = EpisodeShelf.french) -> String {
        if open { return french ? "Masquer" : "Hide" }
        if count == 1 { return french ? "Voir" : "Show" }
        return french ? "Voir les \(count)" : "Show all \(count)"
    }

    /// The button of an absent season's card: « Voir les 25 épisodes ».
    static func showSeason(_ count: Int, open: Bool, french: Bool = EpisodeShelf.french) -> String {
        if open { return french ? "Masquer" : "Hide" }
        if count == 1 { return french ? "Voir l’épisode" : "Show the episode" }
        return french ? "Voir les \(count) épisodes" : "Show the \(count) episodes"
    }

    /// A line of the opened list: « É3 · Le nom », and when it aired or comes out.
    static func entry(_ episode: Item, today: Day, french: Bool = EpisodeShelf.french) -> (name: String, when: String) {
        let named = episode.name.map { $0.isEmpty ? "" : " · " + $0 } ?? ""
        let name = (french ? "É" : "E") + "\(episode.indexNumber ?? 0)" + named
        let when = day(episode)
        return (name, when < today ? (french ? "Sorti " : "Aired ") + EpisodeCalendar.dayText(when, today: today, french: french)
            : EpisodeCalendar.dayTitle(when, today: today, french: french))
    }

    /// The two lines of a tile standing for a run the library lacks (EpisodeTiles.GapLines of Core): « Not in your
    /// library » + « Aired Nov 3 – Nov 10 », « Coming » + « Nov 29 – Dec 27 ».
    static func gapLines(_ line: Line, today: Day, french: Bool = EpisodeShelf.french) -> (first: String, second: String) {
        line.kind == .later
            ? (french ? "À venir" : "Coming", capital(when(line.episodes, today: today, french: french)))
            : (french ? "Pas dans ta bibliothèque" : "Not in your library", capital(aired(line.episodes, today: today, french: french)))
    }

    private static func capital(_ text: String) -> String { text.prefix(1).uppercased() + text.dropFirst() }

    /// A short day, « 25 oct. », « Oct 25 », with its year in another year than today's.
    static func shortDay(_ day: Day, today: Day, french: Bool = EpisodeShelf.french) -> String { short(day, year: day.year != today.year, french: french) }

    private static func short(_ day: Day, year: Bool, french: Bool) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: french ? "fr_FR" : "en_US")
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.dateFormat = french ? (year ? "d MMM yyyy" : "d MMM") : (year ? "MMM d, yyyy" : "MMM d")
        return formatter.string(from: day.date)
    }

    // From the first day to the last, the year said once when both share it: « 8 janv. au 25 juin 2026 ».
    private static func span(_ run: [Item], today: Day, before: String, between: String, french: Bool) -> String {
        var (first, last) = (day(run[0]), day(run[run.count - 1]))
        if first > last { swap(&first, &last) }
        if first == last { return before + shortDay(first, today: today, french: french) }
        let apart = first.year != last.year
        return before + short(first, year: apart, french: french) + between + short(last, year: apart || last.year != today.year, french: french)
    }

    private static func day(_ episode: Item) -> Day { EpisodeCalendar.airDay(episode)! }

    // The virtual episodes that count: numbered, of a numbered season, with their day (EpisodeCalendar.read's).
    private static func dated(_ virtuals: [Item]) -> [Item] {
        virtuals.filter { $0.type == "Episode" && $0.locationType == "Virtual" && ($0.parentIndexNumber ?? 0) >= 1 && $0.indexNumber != nil && EpisodeCalendar.airDay($0) != nil }
    }
}

extension EpisodeShelf {
    /// This series' virtual episodes, nothing when Jellyfin is out of reach (the episodes say it), as on Windows and Mac.
    @MainActor
    static func virtuals(of series: Item, app: AppModel) async -> [Item] {
        // A friend's party's guest reaches only what the party lets through.
        guard let client = app.client, app.party.guest == nil else { return [] }
        do {
            return try await VirtualEpisodes.of(series: series.id, client: client)
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch {}
        return []
    }
}

/// An episode the library lacks, as a tile of the grid: no picture to play, its name and its day (the next one to come
/// out, or one of a run opened).
struct ShelfHollowTile: View {
    let episode: Item
    /// The next one to come out (« series.shelf.coming »), else one of a run opened.
    let coming: Bool

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        let (_, when) = EpisodeShelf.entry(episode, today: today)
        VStack(alignment: .leading, spacing: 0) {
            Color.clear
                .aspectRatio(16 / 9, contentMode: .fit)
                .overlay { Image(systemName: "calendar").font(.title3).foregroundStyle(Palette.faint) }
                .overlay { RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Palette.line, lineWidth: 1) }
                .accessibilityHidden(true)
            EpisodeTileTitle(number: episode.indexNumber, name: EpisodeShelf.name(episode), ink: Palette.muted)
                .padding(.top, 8)
            Text(verbatim: when).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1).padding(.top, 2)
        }
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier(coming ? "series.shelf.coming" : "series.shelf.entry")
    }
}

/// A run the library lacks, the size of a picture: « E3–4 », « Not in your library », « Aired Oct 25 – Nov 1 »; it opens
/// into one tile each.
struct ShelfGapTile: View {
    let line: EpisodeShelf.Line
    @Binding var open: Set<String>
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        let shown = open.contains(line.id)
        let text = line.kind == .later ? EpisodeShelf.laterText(line.episodes, today: today) : EpisodeShelf.missingText(line.episodes, today: today)
        let (first, second) = EpisodeShelf.gapLines(line, today: today)
        Button {
            withAnimation(reduceMotion ? nil : .snappy(duration: 0.28)) { if shown { open.remove(line.id) } else { open.insert(line.id) } }
        } label: {
            Color.clear
                .aspectRatio(16 / 9, contentMode: .fit)
                .overlay(alignment: .leading) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(verbatim: EpisodeShelf.code(line.episodes)).font(MiraFont.bold(15, .subheadline)).foregroundStyle(Palette.ink)
                        Text(verbatim: first).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
                        Text(verbatim: second).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
                        HStack(spacing: 4) {
                            Text(verbatim: EpisodeShelf.show(line.episodes.count, open: shown))
                            Image(systemName: shown ? "chevron.up" : "chevron.down").font(.caption2.weight(.bold))
                        }
                        .font(MiraFont.bold(13, .footnote))
                        .foregroundStyle(Palette.ink)
                        .padding(.top, 6)
                    }
                    .minimumScaleFactor(0.8)
                    .padding(.horizontal, 14)
                }
                .overlay { RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Palette.line, lineWidth: 1) }
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(Text(verbatim: text))
        .accessibilityHint(Text(verbatim: EpisodeShelf.show(line.episodes.count, open: shown)))
        .accessibilityIdentifier(line.kind == .later ? "series.shelf.later" : "series.shelf.missing")
    }
}

/// A season the library has none of: « Season 1 isn’t in your library », how many and when, where they come from; its
/// episodes on demand, as tiles.
struct AbsentSeasonCard: View {
    let season: Int
    let episodes: [Item]
    @State private var open = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.dynamicTypeSize) private var typeSize

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: "calendar").font(.title3).foregroundStyle(Palette.faint).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 4) {
                    Text(verbatim: EpisodeShelf.seasonTitle(season)).font(MiraFont.bold(15, .subheadline)).foregroundStyle(Palette.ink)
                    Text(verbatim: EpisodeShelf.seasonText(episodes, today: today)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    Button {
                        withAnimation(reduceMotion ? nil : .snappy(duration: 0.28)) { open.toggle() }
                    } label: {
                        Text(verbatim: EpisodeShelf.showSeason(episodes.count, open: open)).font(MiraFont.bold(13, .footnote)).foregroundStyle(Palette.ink)
                    }
                    .buttonStyle(.plain)
                    .padding(.top, 6)
                    .accessibilityIdentifier("series.absentSeason.show")
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
            }
            .padding(18)
            .overlay { RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Palette.line, lineWidth: 1) }
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("series.absentSeason")
            if open {
                LazyVGrid(columns: EpisodeGrid.columns(typeSize), alignment: .leading, spacing: EpisodeGrid.rowSpacing) {
                    ForEach(episodes, id: \.id) { ShelfHollowTile(episode: $0, coming: false) }
                }
                .transition(.opacity)
            }
        }
    }
}
