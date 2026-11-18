import SwiftUI

/// A series' bonus videos on its page, « La pastille » (SeriesExtras of Core, cases of tests/fixtures/series-extras.json):
/// a chip « Extras » after its seasons, also after a single one, shows them in place of the episodes, by kind (openings,
/// endings, Jellyfin's kinds, the rest), a row each and « Show all », the episodes' tiles.
enum SeriesExtras {
    static var french: Bool { RemoteLines.french }
    private static func L(_ fr: String, _ en: String, _ french: Bool) -> String { french ? fr : en }

    /// The chip after the seasons and the heading over the bonus videos.
    static func name(french: Bool = french) -> String { L("Bonus", "Extras", french) }

    struct Group: Identifiable, Equatable {
        let id: String
        let name: String
        let items: [Item]
    }

    /// What a bonus video's name says: a textless opening or ending, its number, the title it belongs to, which copy.
    struct Reading: Equatable {
        var kind: String?
        var number: Int?
        var from: String?
        var copy: Int?
    }

    /// Jellyfin's kinds of bonus after the openings and endings, in this order; the others (« Unknown ») last.
    private static let kinds = ["Featurette", "BehindTheScenes", "Interview", "DeletedScene", "Scene", "Short", "Clip", "Sample", "Trailer"]

    static func groups(_ extras: [Item], show: String?, french: Bool = french) -> [Group] {
        let read = extras.enumerated().map { (item: $0.element, read: reading($0.element, show: show), at: $0.offset) }
        var groups: [Group] = []
        for (kind, name) in [("opening", "Openings"), ("ending", "Endings")] {
            let items = read.filter { $0.read.kind == kind }.sorted { a, b in
                let fromA = a.read.from ?? "", fromB = b.read.from ?? ""
                let order = fromA.caseInsensitiveCompare(fromB)
                if order != .orderedSame { return order == .orderedAscending }
                if (a.read.number ?? 0) != (b.read.number ?? 0) { return (a.read.number ?? 0) < (b.read.number ?? 0) }
                if (a.read.copy ?? 1) != (b.read.copy ?? 1) { return (a.read.copy ?? 1) < (b.read.copy ?? 1) }
                return a.at < b.at
            }.map(\.item)
            if !items.isEmpty { groups.append(Group(id: kind, name: name, items: items)) }
        }
        let rest = read.filter { $0.read.kind == nil }
        for kind in kinds where kind != "Sample" {
            let items = rest.filter { $0.item.extraType == kind || kind == "Clip" && $0.item.extraType == "Sample" }.map(\.item)
            if !items.isEmpty { groups.append(Group(id: kind, name: plural(kind, french), items: items)) }
        }
        let others = rest.filter { !kinds.contains($0.item.extraType ?? "") }.map(\.item)
        if !others.isEmpty { groups.append(Group(id: "other", name: L("Autres", "Other", french), items: others)) }
        return groups
    }

    private static func plural(_ kind: String, _ french: Bool) -> String {
        switch kind {
        case "Featurette": L("Making-of", "Featurettes", french)
        case "BehindTheScenes": L("Coulisses", "Behind the scenes", french)
        case "Interview": "Interviews"
        case "DeletedScene": L("Scènes coupées", "Deleted scenes", french)
        case "Scene": L("Scènes", "Scenes", french)
        case "Short": L("Courts métrages", "Shorts", french)
        case "Clip": L("Extraits", "Clips", french)
        default: L("Bandes-annonces", "Trailers", french)
        }
    }

    /// A bonus of one of Jellyfin's own kinds (a featurette, a trailer) is never read as an opening or an ending.
    static func reading(_ extra: Item, show: String?) -> Reading {
        kinds.contains(extra.extraType ?? "") ? Reading() : reading(extra.name, show: show)
    }

    private static let leadingPattern = #"^(?:\[[^\]]*\]\s*)+"#
    private static let bracketedPattern = #"\s*[\[(][^\])]*[\])]\s*"#
    private static let creditlessPattern = #"(?<![a-z0-9])(?<p>NC\s*|(?:Creditless|Clean|Textless|Non[\s-]?credit)\s+)?(?<k>Opening|Ending|OP|ED)\s*(?<n>\d{1,2})?(?:\.(?<c>\d{1,2}))?(?![a-z0-9])"#

    /// « Bakemonogatari - NCOP01 »: opening 1 of Bakemonogatari; « Monogatari Series - NCOP03.2 »: opening 3 of the
    /// series itself, its second copy; anything else is no opening nor ending.
    static func reading(_ name: String?, show: String?) -> Reading {
        guard let leading = try? NSRegularExpression(pattern: leadingPattern), let bracketed = try? NSRegularExpression(pattern: bracketedPattern),
              let creditless = try? NSRegularExpression(pattern: creditlessPattern, options: [.caseInsensitive]) else { return Reading() }
        var text = name ?? ""
        text = leading.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "").trimmingCharacters(in: .whitespaces)
        text = bracketed.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: " ").trimmingCharacters(in: .whitespaces)
        guard let m = creditless.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return Reading() }
        func group(_ name: String) -> String? {
            let range = m.range(withName: name)
            guard range.location != NSNotFound, range.length > 0, let r = Range(range, in: text) else { return nil }
            return String(text[r])
        }
        let k = group("k") ?? ""
        if group("p") == nil, group("n") == nil, k.count <= 2 { return Reading() }
        let kind = ["OP", "OPENING"].contains(k.uppercased()) ? "opening" : "ending"
        let before = Range(NSRange(location: 0, length: m.range.location), in: text).map { String(text[$0]) } ?? ""
        var from = before.trimmingCharacters(in: .whitespaces)
        while let last = from.last, "-_– ".contains(last) { from.removeLast() }
        from = from.trimmingCharacters(in: .whitespaces)
        return Reading(kind: kind, number: group("n").flatMap { Int($0) }, from: from.isEmpty || same(from, show) ? nil : from, copy: group("c").flatMap { Int($0) })
    }

    private static func same(_ a: String, _ b: String?) -> Bool {
        guard let b else { return false }
        return key(a) == key(b)
    }
    private static func key(_ text: String) -> String { String(text.lowercased().filter { $0.isLetter || $0.isNumber }) }

    /// A tile's title: « Opening 3 », « Ending », « Opening 3 · 2 » for a second copy; else Jellyfin's name.
    static func title(_ extra: Item, show: String?) -> String {
        let read = reading(extra, show: show)
        guard let kind = read.kind else { return extra.name ?? "" }
        let title = (kind == "opening" ? "Opening" : "Ending") + (read.number.map { " \($0)" } ?? "")
        return read.copy.map { "\(title) · \($0)" } ?? title
    }

    /// Under a tile's title: « Bakemonogatari · 1 min 30 », the title an opening belongs to (none for the series' own), its length.
    static func meta(_ extra: Item, show: String?, french: Bool = french) -> String {
        let read = reading(extra, show: show)
        return [read.kind != nil ? read.from : nil, length(extra, french: french)].compactMap(\.self).filter { !$0.isEmpty }.joined(separator: " · ")
    }

    /// A short clip's length to the second (« 1 min 30 »); past ten minutes, the usual « 24 min ».
    static func length(_ extra: Item, french: Bool = french) -> String {
        guard let ticks = extra.runTimeTicks, ticks > 0 else { return "" }
        let seconds = Double(ticks) / 10_000_000
        if seconds >= 600 { return duration(seconds, french) }
        let whole = Int((seconds).rounded(.toNearestOrAwayFromZero))
        return whole < 60 ? "\(whole) s" : whole % 60 == 0 ? "\(whole / 60) min" : String(format: "%d min %02d", whole / 60, whole % 60)
    }

    /// FormatDuration of Core: « 24 min », « 1 h 05 », « 1 hr 5 min ».
    private static func duration(_ seconds: Double, _ french: Bool) -> String {
        let minutes = max(1, Int((seconds / 60).rounded(.toNearestOrAwayFromZero)))
        if minutes < 60 { return "\(minutes) min" }
        return french ? String(format: "%d h %02d", minutes / 60, minutes % 60) : "\(minutes / 60) hr \(minutes % 60) min"
    }

    /// Over the bonus: « Bonus · 48 · 1 h 12 », their count and their whole length.
    static func heading(_ extras: [Item], french: Bool = french) -> String {
        let ticks = extras.reduce(Int64(0)) { $0 + max(0, $1.runTimeTicks ?? 0) }
        return [name(french: french), "\(extras.count)", ticks > 0 ? duration(Double(ticks) / 10_000_000, french) : nil].compactMap(\.self).joined(separator: "  ·  ")
    }

    /// How many tiles a group shows: one row, all of them once opened.
    static func shown(_ count: Int, columns: Int, open: Bool) -> Int { open ? count : min(count, columns) }

    static func toggle(_ open: Bool, french: Bool = french) -> String { open ? L("Afficher moins", "Show less", french) : L("Tout afficher", "Show all", french) }

    /// The chip shows only for a series with bonus videos; with them, the chips show for a single season too.
    static func pills(seasons: Int, extras: Int) -> Bool { extras > 0 ? seasons >= 1 : seasons >= 2 }
}

/// The bonus videos in place of a season's episodes: « Bonus · 48 · 1 h 12 », then a row for each kind.
struct ExtrasPage: View {
    let series: Item
    let extras: [Item]
    let columns: Int
    @State private var open: Set<String> = []
    @Environment(\.dynamicTypeSize) private var typeSize

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(verbatim: SeriesExtras.heading(extras))
                .font(MiraFont.footnote)
                .foregroundStyle(Palette.muted)
                .accessibilityIdentifier("series.extrasHeading")
            ForEach(SeriesExtras.groups(extras, show: series.name)) { group in
                let opened = open.contains(group.id)
                VStack(alignment: .leading, spacing: 12) {
                    HStack(alignment: .firstTextBaseline) {
                        (Text(verbatim: group.name).foregroundStyle(Palette.ink) + Text(verbatim: "  \(group.items.count)").foregroundStyle(Palette.muted))
                            .font(MiraFont.headline)
                            .accessibilityAddTraits(.isHeader)
                        Spacer()
                        if group.items.count > columns {
                            Button {
                                if opened { open.remove(group.id) } else { open.insert(group.id) }
                            } label: {
                                Label(SeriesExtras.toggle(opened), systemImage: opened ? "chevron.up" : "chevron.down")
                                    .labelStyle(TrailingIcon())
                                    .font(MiraFont.footnote)
                                    .foregroundStyle(Palette.ink)
                            }
                            .buttonStyle(.plain)
                            .accessibilityIdentifier("series.extras.toggle.\(group.id)")
                        }
                    }
                    LazyVGrid(columns: EpisodeGrid.columns(typeSize), alignment: .leading, spacing: EpisodeGrid.rowSpacing) {
                        ForEach(group.items.prefix(SeriesExtras.shown(group.items.count, columns: columns, open: opened))) { extra in
                            ExtraTile(extra: extra, show: series.name)
                        }
                    }
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("series.extras")
    }
}

/// « Show all ⌄ »: the words, then the chevron.
private struct TrailingIcon: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 5) { configuration.title; configuration.icon.imageScale(.small) }
    }
}

/// A bonus video as an episode's tile: its still, « Opening 3 », « Bakemonogatari · 1 min 30 »; a tap plays it.
struct ExtraTile: View {
    let extra: Item
    let show: String?
    @Environment(AppModel.self) private var app
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        let watched = extra.userData?.played == true
        let progress = watched ? 1 : ItemText.progress(extra)
        let title = SeriesExtras.title(extra, show: show)
        let meta = SeriesExtras.meta(extra, show: show)
        Button {
            app.play(extra)
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                Color.clear
                    .aspectRatio(16 / 9, contentMode: .fit)
                    .background(Palette.raised2)
                    .overlay { Image(systemName: "film").foregroundStyle(Palette.faint) }
                    .overlay {
                        if let client = app.client, let art = Artwork.of(extra, .wide) {
                            RemoteImage(url: client.imageURL(art, width: 200, scale: displayScale), blurHash: art.blurHash, label: title, width: 200)
                        }
                    }
                    .overlay(alignment: .bottom) {
                        if progress > 0 {
                            Rectangle().fill(.white.opacity(0.22)).frame(height: 3)
                                .overlay(alignment: .leading) { GeometryReader { g in Rectangle().fill(Palette.ink).frame(width: g.size.width * progress) } }
                        }
                    }
                    .clipShape(.rect(cornerRadius: 10, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .strokeBorder(.white.opacity(0.06), lineWidth: 1)
                    }
                    .padding(.bottom, 6)
                EpisodeTileTitle(number: nil, name: title)
                if !meta.isEmpty {
                    Text(verbatim: meta).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: [title, meta].filter { !$0.isEmpty }.joined(separator: ", ")))
        .accessibilityAddTraits(.isButton)
    }
}
