import Foundation
import SwiftUI

/// Missing and coming episodes « à leur place » (ogsas, 2026-11-12, mock-up B), as Windows and Mac show them
/// (EpisodeShelf of Core, cases in tests/fixtures/calendar.json, « shelf », written in French there: `french` says
/// them so). No card above a series' episodes: in the season shown, the episodes the library lacks are one thin line
/// where they would be, the next one to come out a hollow row at its place, those after it one more line; a season
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
        let when = run.count == 1
            ? EpisodeCalendar.dayText(day(run[0]), today: today, french: french)
            : span(run, today: today, before: french ? "du " : "", between: french ? " au " : " – ", french: french)
        return "\(code(run, french: french)) · \(french ? "à venir" : "coming") · \(when)"
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

/// The next episode to come out, at its place: as an episode's row, hollow, its day under its name.
struct ShelfComingRow: View {
    let episode: Item

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        let width: CGFloat = 150
        HStack(alignment: .top, spacing: 12) {
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(Palette.line, lineWidth: 1)
                .frame(width: width, height: width * 9 / 16)
                .overlay { Image(systemName: "calendar").font(.title3).foregroundStyle(Palette.faint) }
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                Text(verbatim: [episode.indexNumber.map { "\($0)" }, EpisodeShelf.name(episode)].compactMap(\.self).joined(separator: ". "))
                    .font(MiraFont.headline)
                    .foregroundStyle(Palette.muted)
                    .lineLimit(2)
                Text(verbatim: EpisodeCalendar.dayTitle(EpisodeCalendar.airDay(episode)!, today: today)).font(MiraFont.footnote).foregroundStyle(Palette.ink)
                Text(verbatim: EpisodeShelf.notOut()).font(MiraFont.footnote).foregroundStyle(Palette.faint)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("series.shelf.coming")
    }
}

/// « E3–4 · not in your library · aired Oct 25 – Nov 1 », where they would be; « Show » lists them under it.
struct ShelfThinLine: View {
    let line: EpisodeShelf.Line
    @Binding var open: Set<String>

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        let shown = open.contains(line.id)
        let text = line.kind == .later ? EpisodeShelf.laterText(line.episodes, today: today) : EpisodeShelf.missingText(line.episodes, today: today)
        let code = EpisodeShelf.code(line.episodes)
        VStack(alignment: .leading, spacing: 0) {
            Button {
                withAnimation(.easeOut(duration: 0.2)) { if shown { open.remove(line.id) } else { open.insert(line.id) } }
            } label: {
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Text(styled(code: code, text: text))
                        .font(MiraFont.footnote)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .multilineTextAlignment(.leading)
                    Text(verbatim: EpisodeShelf.show(line.episodes.count, open: shown))
                        .font(MiraFont.bold(13, .footnote))
                        .foregroundStyle(Palette.ink)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 11)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Palette.line, lineWidth: 1) }
            .accessibilityLabel(Text(verbatim: text))
            .accessibilityHint(Text(verbatim: EpisodeShelf.show(line.episodes.count, open: shown)))
            .accessibilityIdentifier(line.kind == .later ? "series.shelf.later" : "series.shelf.missing")
            if shown { ShelfEntries(episodes: line.episodes) }
        }
    }

    // Its code in bold, the rest quieter: « **E3–4** · not in your library · … ».
    private func styled(code: String, text: String) -> AttributedString {
        var head = AttributedString(code)
        head.font = MiraFont.bold(13, .footnote)
        head.foregroundColor = Palette.ink
        var rest = AttributedString(String(text.dropFirst(code.count)))
        rest.foregroundColor = Palette.muted
        return head + rest
    }
}

/// A season the library has none of: « Season 1 isn’t in your library », how many and when, where they come from; its
/// episodes on demand.
struct AbsentSeasonCard: View {
    let season: Int
    let episodes: [Item]
    @State private var open = false

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: "calendar").font(.title3).foregroundStyle(Palette.faint).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 4) {
                    Text(verbatim: EpisodeShelf.seasonTitle(season)).font(MiraFont.bold(15, .subheadline)).foregroundStyle(Palette.ink)
                    Text(verbatim: EpisodeShelf.seasonText(episodes, today: today)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    Button {
                        withAnimation(.easeOut(duration: 0.2)) { open.toggle() }
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
            if open { ShelfEntries(episodes: episodes) }
        }
    }
}

/// The episodes of a thin line or of an absent season, one by one: « E3 · Its name », the day on the right (under it
/// at the largest text sizes).
struct ShelfEntries: View {
    let episodes: [Item]

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        VStack(alignment: .leading, spacing: 8) {
            ForEach(episodes, id: \.id) { episode in
                let (name, when) = EpisodeShelf.entry(episode, today: today)
                ViewThatFits(in: .horizontal) {
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
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .accessibilityIdentifier("series.shelf.list")
    }
}
