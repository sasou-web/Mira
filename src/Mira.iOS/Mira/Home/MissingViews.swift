import SwiftUI

/// « Télécharger les manquants » on a series' page (MissingDownloads): « Get E6–9 » under a tile of the episodes the
/// library lacks, « Get season 2 » on the card of a season it has none of; a sheet puts forward a release for each episode
/// (or a season pack), searched with TorLink on the PC of Jellyfin through the relay of Mira's extension; what goes shows
/// its progress under the tile and in the season's chip until the episodes take their place. For an administrator, when
/// Mira for Windows on the PC of Jellyfin places what a series' page asks (the relay's agent says `seasons`).
@MainActor @Observable
final class MissingWatch {
    /// What a button asked: the sheet opens on it.
    struct Asked: Identifiable, Equatable {
        let ask: MissingDownloads.Ask
        let heading: String
        var id: String { "\(ask.season)-\(ask.episodes.map(String.init).joined(separator: ","))-\(ask.wholeSeason)" }
    }

    private(set) var offered = false
    private(set) var state: RemoteLines.State?
    var asked: Asked?
    /// Changes when something asked from this page was placed: the page reads its episodes again.
    private(set) var placedRound = 0
    /// Changes now and then while a tile says « Adding to your library »: the page reads its seasons and episodes again, the
    /// tile goes once Jellyfin lists them.
    private(set) var lookRound = 0
    /// Changes when something was sent: the page looks at the relay again, every 3 s while it is on its way.
    private(set) var round = 0
    private var placedSeen: Set<String> = []
    private var seriesId = ""

    func progress(season: Int, episodes: [Int]?) -> MissingDownloads.Progress? {
        MissingDownloads.progress(state, seriesId: seriesId, season: season, episodes: episodes, now: Date())
    }

    func ask(_ ask: MissingDownloads.Ask, heading: String) { asked = Asked(ask: ask, heading: heading) }

    func sent(_ state: RemoteLines.State?) {
        if let state { self.state = state }
        round += 1
    }

    /// Looks at the relay for this series: once, then every 3 s while something asked is on its way, and every 10 s while
    /// what was placed waits for Jellyfin to list it (half an hour at most: then it did not see it, the tile says so).
    func watch(_ series: Item, app: AppModel) async {
        if seriesId != series.id { seriesId = series.id; offered = false; state = nil; placedSeen = [] }
        guard app.party.guest == nil, let client = app.client else { return }
        var first = true
        var lookedAt = Date()
        var waitingSince: Date?
        while !Task.isCancelled {
            // A server without Mira's extension answers 404, an account that is not an administrator 403: no buttons.
            guard let read = try? await client.torLink() else { if first { offered = false }; return }
            state = read
            offered = read.agent?.seasons == true
            let placed = (read.missing ?? []).filter { isPlaced($0, in: read) }.map(\.id)
            if first { placedSeen.formUnion(placed) }
            else if !Set(placed).isSubset(of: placedSeen) {
                placedSeen.formUnion(placed)
                placedRound += 1
                lookedAt = Date()
            }
            first = false
            let stages = (read.missing ?? []).compactMap { MissingDownloads.progress(read, seriesId: series.id, season: $0.season, episodes: nil)?.stage }
            let underWay = stages.contains { $0 == .waiting || $0 == .downloading }
            let awaits = stages.contains { $0 == .adding || $0 == .placed }
            guard offered, underWay || awaits else { return }
            if awaits {
                let now = Date()
                let since = waitingSince ?? now
                waitingSince = since
                if now.timeIntervalSince(since) > 30 * 60, !underWay { return }
                if now.timeIntervalSince(lookedAt) >= MissingDownloads.lookAgain(sincePlaced: now.timeIntervalSince(since)) { lookedAt = now; lookRound += 1 }
            } else { waitingSince = nil }
            try? await Task.sleep(for: .seconds(underWay ? 3 : 10))
        }
    }

    private func isPlaced(_ m: RemoteLines.Missing, in state: RemoteLines.State) -> Bool {
        var one = state
        one.missing = [m]
        return MissingDownloads.progress(one, seriesId: seriesId, season: m.season, episodes: nil)?.stage == .placed
    }
}

/// Under a tile of missing episodes: « Get E6–9 », or how what was asked is going.
struct MissingTileFoot: View {
    let line: EpisodeShelf.Line
    @Environment(MissingWatch.self) private var missing: MissingWatch?

    var body: some View {
        let numbers = line.episodes.compactMap(\.indexNumber)
        let season = line.first.parentIndexNumber ?? 1
        if let missing, missing.offered, line.kind == .missing, !numbers.isEmpty, line.episodes.count <= MissingDownloads.maxEpisodes {
            let ask = MissingDownloads.Ask(season: season, episodes: numbers, wholeSeason: false)
            let heading = MissingDownloads.code(numbers.min()!, numbers.max()!)
            if let progress = missing.progress(season: season, episodes: numbers) {
                MissingProgressView(progress: progress) { missing.ask(ask, heading: heading) }
            } else {
                MissingButton(text: MissingDownloads.getEpisodes(numbers.min()!, numbers.max()!), large: false) { missing.ask(ask, heading: heading) }
            }
        }
    }
}

/// The bar along the foot of a tile while its episodes come.
struct MissingTileBar: View {
    let line: EpisodeShelf.Line
    @Environment(MissingWatch.self) private var missing: MissingWatch?

    var body: some View {
        let numbers = line.episodes.compactMap(\.indexNumber)
        if let progress = missing?.progress(season: line.first.parentIndexNumber ?? 1, episodes: numbers), progress.stage != .failed {
            let share = progress.stage == .downloading || progress.stage == .waiting ? min(1, max(0, Double(progress.percent) / 100)) : 1
            Rectangle().fill(.white.opacity(0.22)).frame(height: 3)
                .overlay(alignment: .leading) { GeometryReader { g in Rectangle().fill(Palette.ink).frame(width: g.size.width * share) } }
                .accessibilityHidden(true)
        }
    }
}

/// On the card of a season the library has none of: « Get season 2 », or how what was asked is going.
struct MissingSeasonFoot: View {
    let season: Int
    let episodes: [Item]
    @Environment(MissingWatch.self) private var missing: MissingWatch?

    var body: some View {
        if let missing, missing.offered {
            let ask = MissingDownloads.Ask(season: season, episodes: Array(Set(episodes.compactMap(\.indexNumber))).sorted(), wholeSeason: true)
            let heading = MissingDownloads.french ? "Saison \(season)" : "Season \(season)"
            if let progress = missing.progress(season: season, episodes: nil) {
                MissingProgressView(progress: progress, bar: true) { missing.ask(ask, heading: heading) }
            } else {
                MissingButton(text: MissingDownloads.getSeason(season), large: true) { missing.ask(ask, heading: heading) }
            }
        }
    }
}

struct MissingButton: View {
    let text: String
    let large: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label { Text(verbatim: text) } icon: { Image(systemName: "arrow.down.circle") }
                .font(large ? MiraFont.subheadline : MiraFont.footnote)
                .foregroundStyle(large ? Palette.onAccent : Palette.ink)
                .lineLimit(1)
                .padding(.horizontal, large ? 16 : 11)
                .padding(.vertical, large ? 9 : 6)
                .background(large ? Palette.accent : Color.clear, in: .capsule)
                .overlay { if !large { Capsule().strokeBorder(Palette.line, lineWidth: 1) } }
                .contentShape(.capsule)
        }
        .buttonStyle(.plain)
        .accessibilityHint(Text(verbatim: MissingDownloads.privacy))
        .accessibilityIdentifier("series.missing.get")
    }
}

/// « Downloading on your Jellyfin PC · 38% · about 12 min left », « The download failed » with « Try again ».
struct MissingProgressView: View {
    let progress: MissingDownloads.Progress
    var bar = false
    let retry: () -> Void

    var body: some View {
        let failed = progress.stage == .failed
        VStack(alignment: .leading, spacing: 2) {
            Text(verbatim: progress.title).font(MiraFont.footnote).foregroundStyle(failed ? Palette.danger : Palette.ink).lineLimit(2)
            if !progress.line.isEmpty {
                Text(verbatim: progress.line).font(MiraFont.regular(12, .caption)).foregroundStyle(Palette.muted).lineLimit(failed ? 3 : 1)
            }
            if bar, progress.stage == .downloading || progress.stage == .waiting {
                Rectangle().fill(.white.opacity(0.22)).frame(width: 200, height: 3)
                    .overlay(alignment: .leading) { Rectangle().fill(Palette.ink).frame(width: 200 * min(1, max(0, Double(progress.percent) / 100))) }
                    .padding(.top, 6)
                    .accessibilityHidden(true)
            }
            if failed {
                Button(action: retry) { Text(verbatim: MissingDownloads.retry).font(MiraFont.bold(13, .footnote)).foregroundStyle(Palette.ink) }
                    .buttonStyle(.plain)
                    .padding(.top, 4)
                    .accessibilityIdentifier("series.missing.retry")
            }
        }
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("series.missing.progress")
    }
}

/// The sheet of a button: the search on the PC of Jellyfin, then the releases put forward, then what goes.
struct MissingSheet: View {
    let series: Item
    let asked: MissingWatch.Asked
    /// The episodes of the seasons before, as an anime's absolute numbers count them.
    let before: @MainActor () async -> Int
    let watch: MissingWatch
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var query: String?
    @State private var typed = ""
    @State private var picks: MissingDownloads.Picks?
    @State private var chosen: [Int: MissingDownloads.Option] = [:]
    @State private var pack: MissingDownloads.Option?
    @State private var opened: Set<Int> = []
    @State private var packsOpen = false
    @State private var error: String?
    @State private var sending = false
    @State private var searchRound = 0

    private var ask: MissingDownloads.Ask { asked.ask }

    var body: some View {
        NavigationStack {
            List {
                content
                Section {
                    Text(verbatim: MissingDownloads.privacy).font(MiraFont.regular(12, .caption)).foregroundStyle(Palette.muted)
                }
                .listRowBackground(Color.clear)
            }
            .font(MiraFont.body)
            .scrollContentBackground(.hidden)
            .background(Palette.bg)
            .navigationTitle(Text(verbatim: asked.heading))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Close") { dismiss() } }
            }
            .safeAreaInset(edge: .bottom) { foot }
            .task(id: searchRound) { await search() }
        }
        .presentationDetents([.medium, .large])
    }

    @ViewBuilder private var content: some View {
        if let error, picks?.empty ?? true {
            Section {
                Text(verbatim: error).font(MiraFont.footnote).foregroundStyle(Palette.danger)
                Button { searchRound += 1 } label: { Text(verbatim: MissingDownloads.retry).foregroundStyle(Palette.ink) }
            }
            .listRowBackground(Palette.surface)
        } else if let picks {
            if picks.empty {
                Section {
                    Text(verbatim: MissingDownloads.nothingFound(query ?? series.name ?? "")).font(MiraFont.subheadline).foregroundStyle(Palette.ink)
                    Text(verbatim: MissingDownloads.nothingHelp).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    TextField(text: $typed) { Text(verbatim: MissingDownloads.nameToSearch) }
                        .submitLabel(.search)
                        .onSubmit(again)
                        .accessibilityIdentifier("series.missing.query")
                    Button(action: again) { Text(verbatim: MissingDownloads.searchAgain).foregroundStyle(Palette.ink) }
                }
                .listRowBackground(Palette.surface)
            } else if let pack {
                // Up to three to choose from (best picture, best balance, smallest), the best checked; the others below them.
                let episodes = ask.episodes.count
                let choices = MissingDownloads.packChoices(picks.packs, episodes: episodes)
                let rest = picks.packs.filter { option in !choices.contains { $0.option == option } }
                Section {
                    if choices.count == 1, picks.packs.count == 1 {
                        optionText(pack, label: Releases.episodeLabel(pack.entry.release), covers: MissingDownloads.covers(pack, episodes: episodes))
                    } else {
                        ForEach(choices, id: \.option.entry.hit.id) { choice in
                            Button { self.pack = choice.option } label: {
                                choiceRow(choice.option, on: choice.option == pack, label: MissingDownloads.packKindName(choice.kind), covers: MissingDownloads.covers(choice.option, episodes: episodes))
                            }
                            .buttonStyle(.plain)
                            .accessibilityIdentifier("series.missing.pack.\(choice.kind.rawValue)")
                        }
                    }
                    if !rest.isEmpty {
                        DisclosureGroup(isExpanded: $packsOpen) {
                            ForEach(rest, id: \.entry.hit.id) { option in
                                Button { self.pack = option } label: { choiceRow(option, on: option == pack, covers: MissingDownloads.covers(option, episodes: episodes)) }.buttonStyle(.plain)
                            }
                        } label: {
                            Text(verbatim: MissingDownloads.otherChoices(rest.count)).font(MiraFont.footnote).foregroundStyle(Palette.ink)
                        }
                    }
                    if !picks.episodes.isEmpty {
                        Button { self.pack = nil } label: { Text(verbatim: MissingDownloads.episodeByEpisode(picks.episodes.count)).font(MiraFont.footnote).foregroundStyle(Palette.ink) }
                    }
                }
                .listRowBackground(Palette.surface)
            } else {
                Section {
                    ForEach(picks.episodes, id: \.episode) { episodeRow($0) }
                } footer: {
                    if !picks.notFound.isEmpty { Text(verbatim: MissingDownloads.notFound(picks.notFound)).font(MiraFont.footnote).foregroundStyle(Palette.muted) }
                }
                .listRowBackground(Palette.surface)
            }
            if let error, !picks.empty {
                Section { Text(verbatim: error).font(MiraFont.footnote).foregroundStyle(Palette.danger) }.listRowBackground(Palette.surface)
            }
        } else {
            Section {
                HStack(spacing: 10) {
                    ProgressView()
                    Text(verbatim: MissingDownloads.searching).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
                ForEach(0..<3, id: \.self) { i in
                    VStack(alignment: .leading, spacing: 7) {
                        RoundedRectangle(cornerRadius: 5).fill(Palette.raised2).frame(width: 220 - CGFloat(i) * 30, height: 11)
                        RoundedRectangle(cornerRadius: 4).fill(Palette.raised2).frame(width: 140, height: 9).opacity(0.7)
                    }
                    .opacity(1 - Double(i) * 0.25)
                    .accessibilityHidden(true)
                }
            }
            .listRowBackground(Palette.surface)
            .accessibilityIdentifier("series.missing.searching")
        }
    }

    /// An episode's row: checked with the release put forward; « Change » opens the others.
    @ViewBuilder private func episodeRow(_ choice: MissingDownloads.Choice) -> some View {
        let current = chosen[choice.episode]
        let shown = current ?? choice.best
        let label = MissingDownloads.code(choice.episode, choice.episode)
        let others = [choice.best] + choice.others
        Button {
            if current == nil { chosen[choice.episode] = shown } else { chosen[choice.episode] = nil }
        } label: {
            HStack(spacing: 12) {
                Image(systemName: current == nil ? "circle" : "checkmark.circle.fill").font(.title3).foregroundStyle(current == nil ? Palette.muted : Palette.ink)
                optionText(shown, label: label)
            }
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(current == nil ? [] : .isSelected)
        .accessibilityIdentifier("series.missing.episode.\(choice.episode)")
        if others.count > 1 {
            DisclosureGroup(isExpanded: Binding(get: { opened.contains(choice.episode) }, set: { if $0 { opened.insert(choice.episode) } else { opened.remove(choice.episode) } })) {
                ForEach(others, id: \.entry.hit.id) { option in
                    Button { chosen[choice.episode] = option } label: { choiceRow(option, on: option == shown && current != nil) }.buttonStyle(.plain)
                }
            } label: {
                Text(verbatim: "\(MissingDownloads.change) \(label)").font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
        }
    }

    private func choiceRow(_ option: MissingDownloads.Option, on: Bool, label: String? = nil, covers: String? = nil) -> some View {
        HStack(spacing: 10) {
            Image(systemName: on ? "largecircle.fill.circle" : "circle").foregroundStyle(on ? Palette.ink : Palette.muted)
            optionText(option, label: label, covers: covers)
        }
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private func optionText(_ option: MissingDownloads.Option, label: String?, covers: String? = nil) -> some View {
        let (quality, detail) = MissingDownloads.option(option)
        return VStack(alignment: .leading, spacing: 2) {
            Text(verbatim: [label, quality].compactMap(\.self).joined(separator: "  ")).font(MiraFont.subheadline).foregroundStyle(Palette.ink).lineLimit(1)
            Text(verbatim: detail).font(MiraFont.regular(12, .caption)).foregroundStyle(Palette.muted).lineLimit(1)
            if let covers { Text(verbatim: covers).font(MiraFont.regular(12, .caption)).foregroundStyle(Palette.muted) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    /// « 5.6 GB · into Arcane › Season 02 » and « Download 3 ».
    @ViewBuilder private var foot: some View {
        if let picks, !picks.empty {
            let going = pack.map { [$0] } ?? Array(chosen.values)
            HStack(spacing: 12) {
                Text(verbatim: MissingDownloads.footer(size: going.reduce(0) { $0 + ($1.entry.hit.size ?? 0) }, show: series.name ?? "", season: ask.season))
                    .font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(2)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Button {
                    Task { await send() }
                } label: {
                    Text(verbatim: sending ? MissingDownloads.sendingText : MissingDownloads.download(going.count, season: pack != nil))
                        .font(MiraFont.subheadline).foregroundStyle(Palette.onAccent)
                        .padding(.horizontal, 18).padding(.vertical, 10)
                        .background(Palette.accent, in: .capsule)
                }
                .buttonStyle(.plain)
                .disabled(going.isEmpty || sending)
                .opacity(going.isEmpty || sending ? 0.5 : 1)
                .accessibilityIdentifier("series.missing.download")
            }
            .padding(.horizontal, 20).padding(.vertical, 12)
            .background(.bar)
        }
    }

    private func again() {
        guard let clean = RemoteLines.cleanQuery(typed) else { return }
        query = clean
        searchRound += 1
    }

    private func search() async {
        guard let client = app.client else { return }
        picks = nil; error = nil; chosen = [:]; pack = nil
        let known = MissingDownloads.Series(name: series.name ?? "", originalTitle: series.originalTitle, year: series.productionYear, before: await before(), query: query)
        do {
            let anime = await MissingLibraries.isAnime(series, client: client)
            var hits: [(search: String, hit: Releases.Hit)] = []
            var failure: String?
            for q in MissingDownloads.queries(known, season: ask.season) {
                if query == nil { query = q }
                let found = try await client.missingSearch(q, categories: MissingDownloads.categories(anime: anime))
                hits += found.hits
                failure = failure ?? found.error
                let trial = MissingDownloads.choose(ask, known, hits)
                // Everything found already (or three season packs to choose from): the second, wider search would only add noise.
                if MissingDownloads.searchedEnough(trial) { break }
            }
            try Task.checkCancellation()
            let result = MissingDownloads.choose(ask, known, hits)
            if result.empty, let failure { error = failure }
            pack = result.packs.first
            for choice in result.episodes { chosen[choice.episode] = choice.best }
            typed = query ?? ""
            picks = result
        } catch is CancellationError {
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch {
            self.error = Messages.text(error)
            picks = MissingDownloads.Picks(packs: [], episodes: [], notFound: [])
        }
    }

    /// Each release chosen goes to the relay with where it goes; the sheet closes, the tile shows the rest.
    private func send() async {
        guard let client = app.client else { return }
        let seriesId = series.id
        let sends: [(MissingDownloads.Option, [Int])] = pack.map { [($0, [])] } ?? chosen.sorted { $0.key < $1.key }.map { ($0.value, [$0.key]) }
        guard !sends.isEmpty else { return }
        sending = true; error = nil
        var sent = 0
        do {
            for (option, episodes) in sends {
                try await client.torLinkMissing(search: option.search, hit: option.entry.hit, seriesId: seriesId, season: ask.season, episodes: episodes)
                sent += 1
            }
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch {
            self.error = Messages.text(error)
        }
        sending = false
        if sent > 0 { watch.sent(try? await client.torLink()) }
        if error == nil { dismiss(); return }
        // What went is no longer offered: only what the relay refused stays to send again.
        for (_, episodes) in sends.prefix(sent) { if let e = episodes.first { chosen[e] = nil } else { pack = nil } }
    }
}

/// Whether a series is in a library of animes (MediaLibraries.IsAnime of Mira.Core): TorLink is asked in « anime » too.
enum MissingLibraries {
    struct Folder: Decodable, Sendable { var name: String?; var locations: [String]? }

    /// « Anime », « Animes », « Animés » or アニメ; not « Animation » nor « Dessins animés » (western cartoons).
    static func animeWords(_ text: String) -> Bool {
        if text.contains("アニメ") { return true }
        let words = text.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: nil).lowercased()
            .split { !$0.isLetter && !$0.isNumber }.map(String.init)
        for (i, word) in words.enumerated() where word == "anime" || word == "animes" {
            if i == 0 || (words[i - 1] != "dessin" && words[i - 1] != "dessins") { return true }
        }
        return false
    }

    /// `path` inside `root`, as the server writes them (a Windows server's paths too).
    static func inside(_ path: String, _ root: String) -> Bool {
        func clean(_ s: String) -> String {
            var t = s.replacingOccurrences(of: "\\", with: "/")
            while t.hasSuffix("/") { t.removeLast() }
            return t.lowercased()
        }
        let folder = clean(root)
        return !folder.isEmpty && clean(path).hasPrefix(folder + "/")
    }

    static func isAnime(_ series: Item, client: JellyfinClient) async -> Bool {
        var known = series.path
        if known == nil { known = (try? await client.item(series.id))?.path }
        guard let path = known,
              let folders = try? await client.get("Library/VirtualFolders", as: [Folder].self) else { return false }
        guard let library = folders.first(where: { ($0.locations ?? []).contains { inside(path, $0) } }) else { return false }
        return animeWords(library.name ?? "") || (library.locations ?? []).contains { location in
            animeWords(location.replacingOccurrences(of: "\\", with: "/").split(separator: "/").last.map(String.init) ?? "")
        }
    }
}
