import Foundation

/// « Télécharger les manquants » of a series' page (mock-up A chosen by ogsas on 2026-11-13): what a click on « Get E6–9 »
/// or « Get season 2 » searches with TorLink on the PC of Jellyfin, the release put forward for each episode (or a season
/// pack), and how what was asked is going. The rules of Mira for Windows and Mac (MissingDownloads of Mira.Core), with
/// their shared cases in tests/fixtures/missing.json; the texts in French and English here, as EpisodeCalendar's.
enum MissingDownloads {
    static let maxOthers = 8
    /// A gap a click can ask at once: past this, a season pack is what the page offers.
    static let maxEpisodes = 30
    /// A release within this many points of the best is as good: the one of the family most episodes share goes first.
    static let familyMargin = 5
    /// An episode larger than this is a season in one file, or a mistake (SeriesFollowing.MaxSize of Mira.Core).
    static let maxSize: Int64 = 25 << 30

    static var french: Bool { RemoteLines.french }

    /// What a click asks: the season and its missing episodes; `wholeSeason`, the library has none of it.
    struct Ask: Sendable, Equatable {
        var season: Int
        var episodes: [Int]
        var wholeSeason: Bool
    }

    /// The series as the releases may name it; `before`: the episodes of the seasons before, as an anime's absolute
    /// numbers count them; `query`: a name typed in the menu.
    struct Series: Sendable, Equatable {
        var name: String
        var originalTitle: String?
        var year: Int?
        var before: Int
        var query: String?
        /// The other names the PC of Jellyfin searched the series under (an anime's, from AniList: « Sousou no Frieren »).
        var also: [String] = []
    }

    /// A release, with the search that found it (the relay needs both to download it).
    struct Option: Sendable, Equatable {
        var search: String
        var entry: Releases.Entry
    }

    struct Choice: Sendable, Equatable {
        var episode: Int
        var best: Option
        var others: [Option]
    }

    struct Picks: Sendable, Equatable {
        var packs: [Option]
        var episodes: [Choice]
        var notFound: [Int]
        var empty: Bool { packs.isEmpty && episodes.isEmpty }
    }

    enum Stage: Sendable, Equatable { case waiting, downloading, adding, failed, placed }

    struct Progress: Sendable, Equatable {
        var stage: Stage
        var percent: Int
        var title: String
        var line: String
        var ids: [String]
        /// Placed a while ago and Jellyfin has still not listed it.
        var late = false
    }

    /// Placed this long ago and still not in the series: Jellyfin did not see it (its folder monitor takes about a minute).
    static let late: TimeInterval = 3 * 60

    /// When a page waiting for Jellyfin to list what was placed reads the series' episodes again: often at first, then once a minute.
    static func lookAgain(sincePlaced: TimeInterval) -> TimeInterval { sincePlaced < 5 * 60 ? 10 : 60 }

    // MARK: Searching

    /// The searches of TorLink: an anime's in the anime sites, then the series ones.
    static func categories(anime: Bool) -> [String] { anime ? ["anime", "tv"] : ["tv"] }

    private static func withoutYear(_ name: String) -> String {
        Regex.replace(#"\s*\((?:19|20)\d\d\)\s*$"#, in: name, with: "", options: [])
    }

    /// What to search: the name typed alone; otherwise « Arcane S02 » then « Arcane », without the year Jellyfin may put
    /// after the name; and the same with its original title when it is another name in Latin letters (« Les Anneaux de
    /// pouvoir », released as « The Lord of the Rings: The Rings of Power »). Never a Japanese or Korean original title.
    static func queries(_ series: Series, season: Int) -> [String] {
        if let typed = series.query.flatMap(RemoteLines.cleanQuery) { return [typed] }
        var names: [String] = []
        for (name, original) in [(Optional(series.name), false), (series.originalTitle, true)] {
            guard let name, let clean = RemoteLines.cleanQuery(withoutYear(name)) ?? RemoteLines.cleanQuery(name) else { continue }
            if original && !latin(clean) { continue }
            if names.contains(where: { Releases.titleKey($0) == Releases.titleKey(clean) }) { continue }
            names.append(clean)
        }
        return names.map { "\($0) S\(season < 10 ? "0" : "")\(season)" } + names
    }

    /// Mostly Latin letters: a name the sites of TorLink can find releases under.
    private static func latin(_ name: String) -> Bool {
        let letters = name.unicodeScalars.filter {
            switch $0.properties.generalCategory {
            case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter: true
            default: false
            }
        }
        let latin = letters.filter { (0x41...0x5A).contains($0.value) || (0x61...0x7A).contains($0.value) || (0xC0...0x24F).contains($0.value) || (0x1E00...0x1EFF).contains($0.value) }
        return !letters.isEmpty && latin.count * 2 >= letters.count
    }

    /// The episodes of the seasons before `season`: the last episode of each, from the library's own and TheTVDB's.
    static func before(_ episodes: [Item], season: Int) -> Int {
        guard season > 1 else { return 0 }
        return (1..<season).reduce(0) { sum, s in
            sum + (episodes.filter { $0.parentIndexNumber == s && ($0.indexNumber ?? 0) >= 1 }.compactMap(\.indexNumber).max() ?? 0)
        }
    }

    /// The releases of TorLink's results (with the search of each) for what was asked.
    static func choose(_ ask: Ask, _ series: Series, _ hits: [(search: String, hit: Releases.Hit)]) -> Picks {
        let wanted = Set(ask.episodes.filter { $0 >= 1 })
        let first = wanted.min() ?? 1, last = wanted.max() ?? Int.max
        let keys = Set(([series.name, withoutYear(series.name), series.originalTitle, series.query] + series.also.map { Optional($0) }).map(Releases.titleKey).filter { !$0.isEmpty })
        var seen = Set<String>()
        var packs: [Option] = []
        var episodes: [(episode: Int, option: Option)] = []
        var order = 0
        for (search, hit) in hits {
            guard seen.insert(hit.id.lowercased()).inserted else { continue }
            let release = Releases.parse(hit.name)
            let titled = keys.contains(Releases.titleKey(release.title)) || release.alternate.map { keys.contains(Releases.titleKey($0)) } == true
            if !titled { continue }
            if let year = release.year, let born = series.year, abs(year - born) > 1 { continue }
            if release.source == "cam" || Releases.reportsHealth(hit.source) && (hit.seeders ?? 0) <= 0 { continue }
            let option = Option(search: search, entry: Releases.Entry(hit: hit, release: release, order: order, score: Releases.score(hit, release, category: hit.category)))
            order += 1
            if release.pack || release.episode == nil && release.half == nil && release.season != nil {
                // Only the season asked, whole: « S02 », or « S02E01-E09 » when it covers the episodes the page knows of.
                if ask.wholeSeason, release.season == ask.season,
                   release.episode == nil || (release.episode ?? 0) <= first && (release.lastEpisode ?? 0) >= (wanted.isEmpty ? 0 : last) {
                    packs.append(option)
                }
                continue
            }
            // One episode, never two in one (« S02E06-E07 ») nor one between two (« 07.5 »).
            guard let number = release.episode, (release.lastEpisode ?? number) <= number, release.half == nil else { continue }
            // Numbered without a season: from the first episode of the series, or of the season for the first one.
            let episode: Int? = if let s = release.season { s == ask.season ? number : nil }
                else if ask.season == 1 { number }
                else if series.before > 0, number > series.before { number - series.before }
                else { nil }
            if let e = episode, wanted.contains(e) { episodes.append((e, option)) }
        }

        // Over three times the usual size of an episode: a season in one file, or a mistake.
        let sizes = episodes.map { $0.option.entry.hit.size ?? 0 }.filter { $0 > 0 }.sorted()
        let limit = sizes.count >= 3 ? min(maxSize, sizes[sizes.count / 2] * 3) : maxSize
        episodes.removeAll { ($0.option.entry.hit.size ?? 0) > limit }

        // The group and picture most episodes share: a season looks the same from one episode to the next.
        var families: [String: (episodes: Set<Int>, best: Int)] = [:]
        var familyOrder: [String] = []
        for (e, option) in episodes {
            let key = family(option.entry.release)
            if families[key] == nil { familyOrder.append(key) }
            var known = families[key] ?? ([], Int.min)
            known.episodes.insert(e); known.best = max(known.best, option.entry.score)
            families[key] = known
        }
        let shared = familyOrder.enumerated()
            .filter { families[$0.element]!.episodes.count >= 2 }
            .sorted { a, b in
                let (x, y) = (families[a.element]!, families[b.element]!)
                if x.episodes.count != y.episodes.count { return x.episodes.count > y.episodes.count }
                if x.best != y.best { return x.best > y.best }
                return a.offset < b.offset
            }.first?.element

        var choices: [Choice] = []
        for e in Set(episodes.map(\.episode)).sorted() {
            let sorted = ranked(episodes.filter { $0.episode == e }.map(\.option))
            var top = sorted[0]
            if let shared, let alike = sorted.first(where: { family($0.entry.release) == shared }), alike.entry.score >= top.entry.score - familyMargin { top = alike }
            choices.append(Choice(episode: e, best: top, others: Array(sorted.filter { $0 != top }.prefix(maxOthers))))
        }
        let found = Set(choices.map(\.episode))
        return Picks(packs: Array(ranked(packs).prefix(maxOthers + 1)), episodes: choices, notFound: wanted.filter { !found.contains($0) }.sorted())
    }

    /// Whether the menu has what it needs after a search: every episode asked, or enough season packs to choose from
    /// (`packChoices`); otherwise the wider search (the name alone) runs too.
    static func searchedEnough(_ picks: Picks) -> Bool { picks.notFound.isEmpty || picks.packs.count >= 3 }

    /// Why a season pack is put forward among the three.
    enum PackKind: String, Sendable, Equatable { case quality, balanced, smallest }

    struct PackChoice: Sendable, Equatable {
        var kind: PackKind
        var option: Option
    }

    private static let lighter = 0.6

    /// Up to three season packs to choose from, the best picture first: `quality` = the best ranked; `smallest` = the
    /// smallest at most 60 % of its size; `balanced` = between them, the best ranked (the larger at equal rank) at least 1.5
    /// times the smallest. Never a pack too small for the season's `episodes` (`doubtful`) as a lighter one. One pack
    /// found, one choice. PackChoices of Mira.Core, cases in missing.json.
    static func packChoices(_ packs: [Option], episodes: Int) -> [PackChoice] {
        guard let best = packs.first else { return [] }
        var choices = [PackChoice(kind: .quality, option: best)]
        let size = best.entry.hit.size ?? 0
        guard size > 0 else { return choices }
        let lighter = packs.dropFirst().filter { let s = $0.entry.hit.size ?? 0; return s > 0 && Double(s) <= Double(size) * Self.lighter && !doubtful($0, episodes: episodes) }
        guard let smallest = lighter.min(by: { a, b in
            let (x, y) = (a.entry.hit.size ?? 0, b.entry.hit.size ?? 0)
            return x != y ? x < y : a.entry.score > b.entry.score
        }) else { return choices }
        let smallSize = Double(smallest.entry.hit.size ?? 0)
        let balanced = lighter.filter { $0 != smallest && $0.entry.score >= smallest.entry.score && Double($0.entry.hit.size ?? 0) >= smallSize * 1.5 }
            .min { a, b in a.entry.score != b.entry.score ? a.entry.score > b.entry.score : (a.entry.hit.size ?? 0) > (b.entry.hit.size ?? 0) }
        if let balanced { choices.append(PackChoice(kind: .balanced, option: balanced)) }
        choices.append(PackChoice(kind: .smallest, option: smallest))
        return choices
    }

    /// A pack whose name doesn't say its episodes, too small for all of them at its picture (a season split in two).
    static func doubtful(_ pack: Option, episodes: Int) -> Bool {
        let release = pack.entry.release, size = pack.entry.hit.size ?? 0
        guard release.episode == nil, episodes > 1, size > 0 else { return false }
        let mib: Int64 = switch release.resolution ?? 0 { case 2160...: 500; case 1080...: 120; case 720...: 70; default: 40 }
        return size / Int64(episodes) < mib << 20
    }

    static func packKindName(_ kind: PackKind, french: Bool = MissingDownloads.french) -> String {
        switch kind {
        case .quality: french ? "Meilleure qualité" : "Best quality"
        case .balanced: french ? "Meilleur compromis" : "Best balance"
        case .smallest: french ? "Le plus léger" : "Smallest"
        }
    }

    /// What a season pack holds, as its name says, for a season of `episodes` episodes (Covers of Mira.Core).
    static func covers(_ pack: Option, episodes: Int, french: Bool = MissingDownloads.french) -> String {
        let release = pack.entry.release, size = pack.entry.hit.size ?? 0
        let each = episodes > 0 && size > 0 ? RemoteLines.size(size / Int64(episodes)) : nil
        if let from = release.episode, let to = release.lastEpisode {
            let range = code(from, to, french: french)
            guard let each else { return range }
            return french ? "\(range) · ≈ \(each) par épisode" : "\(range) · ≈ \(each) per episode"
        }
        guard let each else { return french ? "Épisodes non dits dans le nom" : "Episodes not in its name" }
        if doubtful(pack, episodes: episodes) {
            return french ? "Peut-être pas les \(episodes) épisodes : ≈ \(each) chacun s’il les a" : "Maybe not all \(episodes) episodes: ≈ \(each) each if so"
        }
        return french ? "Épisodes non dits dans le nom · ≈ \(each) chacun s’il a les \(episodes)" : "Episodes not in its name · ≈ \(each) each for all \(episodes)"
    }

    private static func ranked(_ options: [Option]) -> [Option] {
        options.sorted { a, b in
            if a.entry.score != b.entry.score { return a.entry.score > b.entry.score }
            let (x, y) = (a.entry.hit.seeders ?? 0, b.entry.hit.seeders ?? 0)
            if x != y { return x > y }
            return a.entry.order < b.entry.order
        }
    }

    private static func family(_ release: Releases.Release) -> String { "\(release.group?.lowercased() ?? "")|\(release.resolution.map(String.init) ?? "")" }

    // MARK: How it is going

    /// Where what a series' page asked stands, from the relay's list, TorLink's downloads and what Mira placed; nil when
    /// nothing was asked. `episodes` nil: anything of that season (its pill).
    static func progress(_ state: RemoteLines.State?, seriesId: String, season: Int, episodes: [Int]?, now: Date? = nil, french: Bool = MissingDownloads.french) -> Progress? {
        guard let state, let missing = state.missing, !missing.isEmpty else { return nil }
        func L(_ fr: String, _ en: String) -> String { french ? fr : en }
        let id = seriesId.replacingOccurrences(of: "-", with: "").lowercased()
        let asked = episodes.map(Set.init)
        let mine = missing.filter {
            $0.seriesId.replacingOccurrences(of: "-", with: "").lowercased() == id && $0.season == season
                && (asked == nil || $0.episodes.isEmpty || $0.episodes.contains { asked!.contains($0) })
        }
        guard !mine.isEmpty else { return nil }
        let parts = mine.map { (ask: $0, part: part($0, state, french: french)) }
        let active = parts.filter { [.waiting, .downloading, .adding].contains($0.part.stage) }
        let ids = mine.map(\.id)
        if active.isEmpty {
            // One placed, another failed: what was asked is there, as far as the tile can tell.
            if parts.allSatisfy({ $0.part.stage != .placed }), let failed = parts.first(where: { $0.part.stage == .failed }) {
                return Progress(stage: .failed, percent: 0, title: L("Le téléchargement a échoué", "The download failed"), line: failed.part.error ?? "", ids: ids)
            }
            // Placed: Jellyfin reads the folder after its monitor's delay; a tile still there long after: it did not see it.
            let placedAt = parts.compactMap(\.ask.placedDate).max()
            let late = now.flatMap { now in placedAt.map { now.timeIntervalSince($0) > MissingDownloads.late } } ?? false
            return Progress(stage: .placed, percent: 100, title: L("Rangement dans ta bibliothèque…", "Adding to your library…"),
                            line: late ? L("Jellyfin ne l’a pas encore ajouté à la série.", "Jellyfin hasn’t added it to the series yet.") : L("Rangé. Jellyfin l’ajoute à la série…", "Filed. Jellyfin is adding it to the series…"),
                            ids: ids, late: late)
        }
        // By size, what each has; what is not known yet counts as one equal share.
        let counted = parts.filter { $0.part.stage != .failed }
        let total = counted.reduce(0.0) { $0 + Double(max(1, $1.part.size)) }
        let done = counted.reduce(0.0) { $0 + Double(max(1, $1.part.size)) * Double($1.part.percent) / 100 }
        let percent = min(100, max(0, Int((done * 100 / max(1, total)).rounded(.toNearestOrAwayFromZero))))
        if active.contains(where: { $0.part.stage == .downloading }) {
            let left = active.compactMap(\.part.left).max()
            let line = left.map { L("\(percent) % · encore \(RemoteLines.remaining($0))", "\(percent)% · about \(RemoteLines.remaining($0)) left") } ?? L("\(percent) %", "\(percent)%")
            return Progress(stage: .downloading, percent: percent, title: L("Téléchargement sur le PC de Jellyfin", "Downloading on your Jellyfin PC"), line: line, ids: ids)
        }
        if active.contains(where: { $0.part.stage == .waiting }) {
            let sending = active.contains { $0.ask.state == "claimed" || $0.ask.state == "sent" }
            return Progress(stage: .waiting, percent: percent, title: sending ? L("Envoi à TorLink…", "Sending to TorLink…") : L("En attente du PC de Jellyfin", "Waiting for your Jellyfin PC"),
                            line: sending ? "" : L("Il le prend dès que Mira y est ouvert.", "It starts as soon as Mira is open there."), ids: ids)
        }
        return Progress(stage: .adding, percent: 100, title: L("Rangement dans ta bibliothèque…", "Adding to your library…"),
                        line: L("Téléchargé. Mira le range sur le PC de Jellyfin…", "Downloaded. Mira is filing it on your Jellyfin PC…"), ids: ids)
    }

    private struct Part { var stage: Stage; var percent: Int; var size: Int64; var left: Double?; var error: String? }

    private static func part(_ ask: RemoteLines.Missing, _ state: RemoteLines.State, french: Bool) -> Part {
        func L(_ fr: String, _ en: String) -> String { french ? fr : en }
        let size = ask.size ?? 0
        if ask.state == "failed" { return Part(stage: .failed, percent: 0, size: size, left: nil, error: ask.error ?? L("TorLink a refusé ce téléchargement.", "TorLink refused this download.")) }
        let hash = (ask.infoHash ?? "").lowercased()
        if !hash.isEmpty, let placed = state.placed?.first(where: { $0.id.lowercased() == hash }) {
            return placed.ok == true ? Part(stage: .placed, percent: 100, size: size, left: nil, error: nil) : Part(stage: .failed, percent: 0, size: size, left: nil, error: placed.status)
        }
        // Placed, and no longer among the latest the agent lists.
        if ask.placedAt != nil { return Part(stage: .placed, percent: 100, size: size, left: nil, error: nil) }
        if !hash.isEmpty, let download = state.downloads?.first(where: { $0.id.lowercased() == hash }) {
            let total = (download.size ?? 0) > 0 ? download.size! : size
            switch download.status {
            case "completed", "seeding": return Part(stage: .adding, percent: 100, size: total, left: nil, error: nil)
            case "failed":
                let why = RemoteLines.failure(download.error)
                return Part(stage: .failed, percent: 0, size: total, left: nil, error: why.isEmpty ? L("TorLink a abandonné ce téléchargement.", "TorLink gave up on this download.") : why)
            default: return Part(stage: .downloading, percent: RemoteLines.percent(download.progress), size: total, left: RemoteLines.timeLeft(download), error: nil)
            }
        }
        // Finished and gone from TorLink's list (to its history): Mira places it.
        if ask.finished == true || ask.listed == true { return Part(stage: .adding, percent: 100, size: size, left: nil, error: nil) }
        // Sent, not listed by TorLink yet; or still at the relay.
        return Part(stage: .waiting, percent: 0, size: size, left: nil, error: nil)
    }

    // MARK: Words

    static func code(_ from: Int, _ to: Int, french: Bool = MissingDownloads.french) -> String { (french ? "É" : "E") + (from == to ? "\(from)" : "\(from)–\(to)") }
    static func getEpisodes(_ from: Int, _ to: Int, french: Bool = MissingDownloads.french) -> String { (french ? "Télécharger " : "Get ") + code(from, to, french: french) }
    static func getSeason(_ season: Int, french: Bool = MissingDownloads.french) -> String { french ? "Télécharger la saison \(season)" : "Get season \(season)" }
    static var searching: String { french ? "Recherche sur le PC de Jellyfin…" : "Searching on your Jellyfin PC…" }
    static func nothingFound(_ query: String) -> String { french ? "Rien trouvé pour « \(query) »." : "Nothing found for “\(query)”." }
    static var nothingHelp: String {
        french ? "Essaie un autre nom (celui des sorties, en anglais souvent), ou cherche dans Téléchargements." : "Try another name (the one releases use, often English), or search in Downloads."
    }

    /// Episodes in runs: 6, 7, 9 → 6–7, 9.
    static func runs(_ episodes: [Int]) -> [(from: Int, to: Int)] {
        var runs: [(from: Int, to: Int)] = []
        for e in Set(episodes).sorted() {
            if let last = runs.last, last.to == e - 1 { runs[runs.count - 1].to = e } else { runs.append((e, e)) }
        }
        return runs
    }

    static func notFound(_ episodes: [Int], french: Bool = MissingDownloads.french) -> String {
        guard !episodes.isEmpty else { return "" }
        let codes = runs(episodes).map { code($0.from, $0.to, french: french) }.joined(separator: ", ")
        return french ? "Rien trouvé pour \(codes)." : "Nothing found for \(codes)."
    }

    /// A release in the menu: « 1080p · WEB-DL · MULTI », then « 1.4 GB · 120 seeders · YTS ».
    static func option(_ option: Option) -> (quality: String, detail: String) {
        let quality = Releases.qualityLine(option.entry.release)
        return (quality.isEmpty ? (french ? "Qualité inconnue" : "Unknown quality") : quality, RemoteLines.hitLine(option.entry.hit))
    }

    static func footer(size: Int64, show: String, season: Int) -> String {
        let s = season < 10 ? "0\(season)" : "\(season)"
        return french ? "\(RemoteLines.size(size)) · dans \(show) › Saison \(s)" : "\(RemoteLines.size(size)) · into \(show) › Season \(s)"
    }

    static func download(_ count: Int, season: Bool) -> String {
        season ? (french ? "Télécharger la saison" : "Download season") : (french ? "Télécharger \(count)" : "Download \(count)")
    }

    static var change: String { french ? "Changer" : "Change" }
    static var retry: String { french ? "Réessayer" : "Try again" }
    static var searchAgain: String { french ? "Chercher" : "Search again" }
    static var nameToSearch: String { french ? "Nom à chercher" : "Name to search" }
    static var sendingText: String { french ? "Envoi…" : "Sending…" }
    static func otherChoices(_ count: Int) -> String { french ? "Autres choix (\(count))" : "Other choices (\(count))" }
    static func episodeByEpisode(_ count: Int) -> String { french ? "Plutôt épisode par épisode (\(count))" : "Episode by episode instead (\(count))" }

    static var privacy: String { french ? "Les sites de TorLink reçoivent le nom de la série, depuis le PC de Jellyfin." : "TorLink’s sites get the series’ name, from your Jellyfin PC." }

    /// The short text of a season's pill while its episodes come: « 38 % ».
    static func pill(_ progress: Progress, french: Bool = MissingDownloads.french) -> String {
        switch progress.stage {
        case .downloading: return french ? "\(progress.percent) %" : "\(progress.percent)%"
        case .waiting: return french ? "en attente" : "waiting"
        case .adding: return french ? "rangement" : "adding"
        case .failed: return french ? "échec" : "failed"
        case .placed: return ""
        }
    }
}

extension RemoteLines {
    /// What a series' page asked of the relay (RelayMissing of Mira.Core): listed until its episodes are placed.
    struct Missing: Decodable, Sendable, Equatable, Identifiable {
        var id: String
        var seriesId: String
        var season: Int
        var episodes: [Int]
        var name: String?
        var size: Int64?
        var infoHash: String?
        /// waiting, claimed, sent, failed.
        var state: String
        var error: String?
        /// TorLink listed it once; it finished downloading (Mira places it); when Mira placed it (an extension of 0.9.7 or
        /// before says none of them).
        var listed: Bool?
        var finished: Bool?
        var placedAt: String?

        /// `placedAt` read: .NET writes up to seven digits of a second, which ISO8601DateFormatter refuses.
        var placedDate: Date? {
            guard let placedAt else { return nil }
            let plain = placedAt.replacingOccurrences(of: #"\.\d+"#, with: "", options: .regularExpression)
            return ISO8601DateFormatter().date(from: plain)
        }
    }
}

extension JellyfinClient {
    /// A result of a search to download into a series' season: the relay hands its place to Mira on the PC of Jellyfin.
    /// `episodes` empty: the whole season (a pack).
    func torLinkMissing(search: String, hit: Releases.Hit, seriesId: String, season: Int, episodes: [Int]) async throws {
        var fields: [String: Any] = ["search": search, "result": hit.id, "name": hit.name,
                                     "missing": ["seriesId": seriesId, "season": season, "episodes": episodes]]
        if let size = hit.size { fields["size"] = size }
        try await send("POST", "Mira/torlink/requests", body: try JSONSerialization.data(withJSONObject: fields))
    }

    /// One search in a category, through the relay, waited for (2 min at most): its results, or why none came.
    func torLinkSearch(_ query: String, category: String) async throws -> String {
        let body = try JSONSerialization.data(withJSONObject: ["query": query, "category": category])
        return try JSON.decoder.decode(RemoteLines.Made.self, from: try await send("POST", "Mira/torlink/search", body: body)).id
    }

    /// The searches of « Télécharger les manquants », one per category, waited for together.
    func missingSearch(_ query: String, categories: [String]) async throws -> (hits: [(search: String, hit: Releases.Hit)], error: String?, names: [String]) {
        var left: [String] = []
        for category in categories { left.append(try await torLinkSearch(query, category: category)) }
        var hits: [(search: String, hit: Releases.Hit)] = [], error: String?, names: [String] = []
        let until = Date.now.addingTimeInterval(120)
        while !left.isEmpty, Date.now < until {
            try await Task.sleep(for: .seconds(1))
            for id in left {
                let state = try await torLinkSearchState(id)
                if state.state == "done" {
                    hits += (state.results ?? []).map { (search: id, hit: $0) }
                    for name in state.names ?? [] where !names.contains(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) { names.append(name) }
                    left.removeAll { $0 == id }
                }
                else if state.state == "failed" { error = error ?? state.error; left.removeAll { $0 == id } }
            }
        }
        if !left.isEmpty, error == nil { error = MissingDownloads.french ? "Le PC de Jellyfin n’a pas répondu à temps." : "Your Jellyfin PC didn’t answer in time." }
        return (hits, hits.isEmpty ? error : nil, names)
    }
}
