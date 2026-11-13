import SwiftUI

/// A series' episodes on its page: its seasons to choose from, then a grid of tiles, each episode with its still, how
/// far it was watched and a Play of its own; the season shown first is the one of the episode to watch next.
struct EpisodeList: View {
    let series: Item
    /// The episode Play plays on the series' page.
    let next: Item?
    @Environment(AppModel.self) private var app
    @State private var seasons: [Item] = []
    @State private var season: String?
    @State private var episodes: [Item] = []
    @State private var failed = false
    /// A season chosen by hand: the episode to watch next no longer moves the list.
    @State private var picked = false
    @State private var marking = false
    /// This series' episodes from TheTVDB (through Jellyfin), « à leur place » (EpisodeShelf): the seasons the library
    /// has none of among the seasons, the episodes it lacks between those it has; the thin lines opened.
    @State private var virtuals: [Item] = []
    @State private var shelfOpen: Set<String> = []
    /// « Télécharger les manquants » (MissingViews): an administrator's buttons, and how what was asked is going.
    @State private var missing = MissingWatch()
    @Environment(\.dynamicTypeSize) private var typeSize
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        let showMissing = app.preferences.showMissingEpisodes
        let chips = self.chips(today: today, showMissing: showMissing)
        VStack(alignment: .leading, spacing: 14) {
            if chips.count > 1 {
                ScrollView(.horizontal) {
                    HStack(spacing: 8) {
                        ForEach(chips) { entry in
                            let chosen = entry.id == season
                            let coming = entry.season.flatMap { missing.progress(season: $0, episodes: nil) }.map { MissingDownloads.pill($0) } ?? ""
                            Button {
                                picked = true
                                season = entry.id
                            } label: {
                                Text(verbatim: coming.isEmpty ? entry.name : "\(entry.name)  ·  \(coming)")
                                    .font(MiraFont.callout)
                                    .foregroundStyle(chosen ? Palette.onAccent : Palette.ink)
                                    .padding(.horizontal, 14)
                                    .padding(.vertical, 8)
                                    .background(chosen ? Palette.accent : Palette.raised, in: .capsule)
                            }
                            .buttonStyle(.plain)
                            .accessibilityAddTraits(chosen ? .isSelected : [])
                        }
                    }
                }
                .scrollIndicators(.hidden)
                .accessibilityIdentifier("series.seasons")
            } else if let only = chips.first?.name {
                Text(verbatim: only).font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
            }
            // « Mark the season as watched »: only beside another season (with one, the series' own button does it).
            if seasons.count > 1, !episodes.isEmpty, absentSeason == nil {
                let mark = SeasonMark.of(episodes)
                Button {
                    Task { await markSeason(mark) }
                } label: {
                    Group {
                        if mark.played {
                            Label("Mark the season as unwatched", systemImage: "arrow.uturn.backward.circle")
                        } else {
                            Label("Mark the season as watched", systemImage: "checkmark.circle")
                        }
                    }
                    .font(MiraFont.callout)
                    .foregroundStyle(Palette.ink)
                }
                .buttonStyle(.plain)
                .disabled(marking)
                .accessibilityIdentifier("series.markSeason")
            }
            KeepSeasonButton(episodes: episodes)
            if failed && episodes.isEmpty {
                Text(verbatim: Messages.text(JellyfinError.unreachable)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
            if let absent = absentSeason {
                // A season the library has none of: a calm card for its aired episodes, then those coming, as tiles.
                let lines = EpisodeShelf.lines([], virtuals: virtuals, season: absent, today: today, showMissing: showMissing)
                VStack(alignment: .leading, spacing: 16) {
                    ForEach(lines.filter { $0.kind == .missing }) { AbsentSeasonCard(season: absent, episodes: $0.episodes) }
                    LazyVGrid(columns: EpisodeGrid.columns(typeSize), alignment: .leading, spacing: EpisodeGrid.rowSpacing) {
                        ForEach(tiles(lines.filter { $0.kind != .missing })) { tileView($0) }
                    }
                }
            } else {
                // A grid of stills (EpisodeTiles of Core), those the library lacks hollow at their place.
                LazyVGrid(columns: EpisodeGrid.columns(typeSize), alignment: .leading, spacing: EpisodeGrid.rowSpacing) {
                    ForEach(tiles(shelf(today: today, showMissing: showMissing))) { tileView($0) }
                }
                .id(season)
                .transition(.opacity)
            }
        }
        .animation(reduceMotion ? nil : .easeOut(duration: 0.2), value: season)
        .environment(missing)
        .task(id: "\(series.id)-\(missing.round)") { await missing.watch(series, app: app) }
        // Episodes asked from this page placed: they leave the hollow tiles for their own.
        .onChange(of: missing.placedRound) {
            Task {
                VirtualEpisodes.forget()
                await loadSeasons()
                virtuals = await EpisodeShelf.virtuals(of: series, app: app)
            }
        }
        // Waiting for Jellyfin to list what was placed: the seasons and episodes read again (a held one hides its hollow tile).
        .onChange(of: missing.lookRound) { Task { await loadSeasons() } }
        .sheet(item: Binding(get: { missing.asked }, set: { missing.asked = $0 })) { asked in
            MissingSheet(series: series, asked: asked, before: { await episodesBefore(asked.ask.season) }, watch: missing)
        }
        .task(id: series.id) { await loadSeasons() }
        .task(id: series.id) {
            virtuals = await EpisodeShelf.virtuals(of: series, app: app)
            // A series the library holds no season of: its first season known to TheTVDB.
            if season == nil, seasons.isEmpty { season = self.chips(today: .today(), showMissing: app.preferences.showMissingEpisodes).first?.id }
        }
        .task(id: season) { await loadEpisodes() }
        // A title stopped or marked: the season shown is read again in place (same episodes, same rows).
        .onChange(of: app.playback) { Task { await loadEpisodes() } }
        // The episode to watch next, known after the seasons: its season comes first.
        .onChange(of: next?.seasonId) { _, id in
            if !picked, let id, seasons.contains(where: { $0.id == id }) { season = id }
        }
    }

    /// The seasons to choose from: Jellyfin's, then those the library has none of (« Season 1 · not in library »).
    private struct Chip: Identifiable {
        let id: String
        let name: String
        var season: Int?
    }

    private func chips(today: EpisodeCalendar.Day, showMissing: Bool) -> [Chip] {
        let held = Set(seasons.compactMap(\.indexNumber))
        let absent = EpisodeShelf.absentSeasons(held: held, virtuals: virtuals, today: today, showMissing: showMissing)
        return seasons.map { Chip(id: $0.id, name: $0.name ?? "", season: $0.indexNumber) }
            + absent.map { Chip(id: Self.absentPrefix + "\($0)", name: EpisodeShelf.seasonLabel($0, virtuals: virtuals, today: today), season: $0) }
    }

    private static let absentPrefix = "absent-"

    /// The season shown, when it is one the library has none of.
    private var absentSeason: Int? {
        guard let season, season.hasPrefix(Self.absentPrefix) else { return nil }
        return Int(season.dropFirst(Self.absentPrefix.count))
    }

    /// The season shown: its episodes, those it lacks at their place.
    private func shelf(today: EpisodeCalendar.Day, showMissing: Bool) -> [EpisodeShelf.Line] {
        guard let number = seasons.first(where: { $0.id == season })?.indexNumber else { return episodes.map { EpisodeShelf.Line(kind: .episode, episodes: [$0]) } }
        return EpisodeShelf.lines(episodes, virtuals: virtuals, season: number, today: today, showMissing: showMissing)
    }

    /// A tile of the grid: an episode, one the library lacks, or a run of them (opened into one tile each).
    private enum Tile: Identifiable {
        case episode(Item)
        case hollow(Item, coming: Bool)
        case gap(EpisodeShelf.Line)

        var id: String {
            switch self {
            case .episode(let item): "episode-\(item.id)"
            case .hollow(let item, _): "hollow-\(item.id)"
            case .gap(let line): "gap-\(line.id)"
            }
        }
    }

    private func tiles(_ lines: [EpisodeShelf.Line]) -> [Tile] {
        lines.flatMap { line -> [Tile] in
            switch line.kind {
            case .episode: [.episode(line.first)]
            case .coming: [.hollow(line.first, coming: true)]
            case .missing, .later: [.gap(line)] + (shelfOpen.contains(line.id) ? line.episodes.map { .hollow($0, coming: false) } : [])
            }
        }
    }

    @ViewBuilder private func tileView(_ tile: Tile) -> some View {
        switch tile {
        case .episode(let item): EpisodeTile(episode: item, isNext: item.id == next?.id)
        case .hollow(let item, let coming): ShelfHollowTile(episode: item, coming: coming).transition(.opacity)
        case .gap(let line): ShelfGapTile(line: line, open: $shelfOpen)
        }
    }

    private func markSeason(_ mark: SeasonMark) async {
        guard let client = app.client else { return }
        marking = true
        defer { marking = false }
        do {
            for id in mark.ids { try await client.setPlayed(id, !mark.played) }
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch {
            app.actionMessage = Messages.text(error)
        }
        app.playbackChanged()
    }

    private func loadSeasons() async {
        guard let client = app.client else { return }
        do {
            seasons = try await client.seasons(series.id)
            if absentSeason == nil, season == nil || !seasons.contains(where: { $0.id == season }) {
                season = seasons.first { $0.id == next?.seasonId }?.id ?? seasons.first?.id
            } else {
                await loadEpisodes()
            }
        } catch JellyfinError.unauthorized { app.sessionEnded() } catch { failed = true }
    }

    /// The episodes of the seasons before `season`, the library's and TheTVDB's (MissingDownloads.before).
    private func episodesBefore(_ season: Int) async -> Int {
        guard season > 1, let client = app.client else { return 0 }
        var all = virtuals
        for held in seasons where (held.indexNumber ?? 0) >= 1 && (held.indexNumber ?? 0) < season {
            if let found = try? await client.episodes(series.id, season: held.id) { all += found }
        }
        return MissingDownloads.before(all, season: season)
    }

    private func loadEpisodes() async {
        guard let client = app.client, let season else { return }
        // A season the library has none of: nothing to ask Jellyfin.
        if absentSeason != nil { episodes = []; failed = false; return }
        do {
            episodes = try await client.episodes(series.id, season: season)
            failed = false
        } catch JellyfinError.unauthorized { app.sessionEnded() } catch is CancellationError {} catch { failed = true }
    }
}

/// The grid of a series' episodes: two columns on a phone, one at the largest text sizes.
enum EpisodeGrid {
    static let rowSpacing: CGFloat = 20

    static func columns(_ size: DynamicTypeSize) -> [GridItem] {
        size.isAccessibilitySize
            ? [GridItem(.flexible(), alignment: .top)]
            : [GridItem(.adaptive(minimum: 150), spacing: 12, alignment: .top)]
    }
}

/// « 03  The name », the number quieter.
struct EpisodeTileTitle: View {
    let number: Int?
    let name: String
    var ink: Color = Palette.ink

    private var title: AttributedString {
        var lead = AttributedString(number.map { String(format: "%02d  ", $0) } ?? "")
        lead.foregroundColor = Palette.muted
        var rest = AttributedString(name)
        rest.foregroundColor = ink
        return lead + rest
    }

    var body: some View {
        Text(title)
            .font(MiraFont.bold(15, .subheadline))
            .lineLimit(2)
            .multilineTextAlignment(.leading)
    }
}

/// One episode as a tile: its still (a check on it once watched, its progress, framed in white with « UP NEXT » for the
/// one Play plays), then its number, name, length and summary. The still plays it at once; the text opens its page.
struct EpisodeTile: View {
    let episode: Item
    let isNext: Bool
    @Environment(AppModel.self) private var app
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        let width: CGFloat = 200
        let hidden = Spoilers.hides(episode, on: app.preferences.hideSpoilers)
        let watched = episode.userData?.played == true
        let progress = watched ? 1 : ItemText.progress(episode)
        VStack(alignment: .leading, spacing: 0) {
            Button {
                app.play(episode)
            } label: {
                Color.clear
                    .aspectRatio(16 / 9, contentMode: .fit)
                    .overlay {
                        if hidden {
                            // Jellyfin's BlurHash of its still, the still itself never fetched.
                            RemoteImage(url: nil, blurHash: Spoilers.stillBlurHash(episode), width: width)
                        } else if let client = app.client {
                            let art = Artwork.of(episode, .wide)
                            RemoteImage(url: art.map { client.imageURL($0, width: width, scale: displayScale) }, blurHash: art?.blurHash, label: episode.name ?? "", width: width)
                        }
                    }
                    .overlay(alignment: .bottom) {
                        if progress > 0 {
                            Rectangle().fill(.white.opacity(0.22)).frame(height: 3)
                                .overlay(alignment: .leading) { GeometryReader { g in Rectangle().fill(Palette.ink).frame(width: g.size.width * progress) } }
                        }
                    }
                    .overlay(alignment: .topTrailing) {
                        if watched {
                            Image(systemName: "checkmark")
                                .font(.caption2.weight(.heavy))
                                .foregroundStyle(.white)
                                .frame(width: 22, height: 22)
                                .background(.black.opacity(0.62), in: .circle)
                                .padding(7)
                        }
                    }
                    .overlay(alignment: .topLeading) {
                        if isNext {
                            Text("UP NEXT")
                                .font(MiraFont.extraBold(11, .caption2))
                                .foregroundStyle(Palette.onAccent)
                                .padding(.horizontal, 8)
                                .padding(.vertical, 3)
                                .background(Palette.ink, in: .rect(cornerRadius: 6, style: .continuous))
                                .padding(8)
                        }
                    }
                    .overlay {
                        if isNext {
                            Image(systemName: "play.fill")
                                .font(.footnote.weight(.bold))
                                .foregroundStyle(Palette.ink)
                                .frame(width: 36, height: 36)
                                .glassEffect(.clear, in: .circle)
                        }
                    }
                    .clipShape(.rect(cornerRadius: 10, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .strokeBorder(isNext ? Palette.ink : .white.opacity(0.06), lineWidth: isNext ? 2 : 1)
                    }
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("Play \(heading)"))
            .accessibilityValue(watched ? Text("Watched") : isNext ? Text("UP NEXT") : Text(verbatim: ""))
            .accessibilityIdentifier("episode.play.\(episode.indexNumber ?? 0)")

            HStack(alignment: .top, spacing: 4) {
                NavigationLink(value: TitleRoute(item: episode, source: "episode-\(episode.id)")) {
                    VStack(alignment: .leading, spacing: 2) {
                        EpisodeTileTitle(number: episode.indexNumber, name: episode.name ?? "")
                        if let detail {
                            Text(verbatim: detail).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
                        }
                        if hidden {
                            Text("Not watched yet: summary hidden.").font(MiraFont.footnote).italic().foregroundStyle(Palette.faint).lineLimit(2)
                        } else if case let overview = OverviewText.plain(episode.overview, flat: true), !overview.isEmpty {
                            Text(verbatim: overview).font(MiraFont.footnote).foregroundStyle(Palette.faint).lineLimit(2).multilineTextAlignment(.leading)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                if app.party.guest == nil { KeepButton(item: episode, compact: true) }
            }
            .padding(.top, 8)
        }
        .titleMenu(episode)
    }

    private var heading: String {
        [episode.indexNumber.map { n in episode.indexNumberEnd.map { "\(n)–\($0)" } ?? "\(n)" }, episode.name]
            .compactMap(\.self).filter { !$0.isEmpty }.joined(separator: ". ")
    }

    /// « 45 min · Oct 25 », « 32 min left · Oct 25 » (EpisodeTiles.Meta of Core).
    private var detail: String? {
        let length = ItemText.remaining(episode) ?? episode.runTimeTicks.flatMap { $0 > 0 ? ItemText.duration(seconds: $0 / ItemText.ticksPerSecond) : nil }
        let day = EpisodeCalendar.airDay(episode).map { EpisodeShelf.shortDay($0, today: .today()) }
        let parts = [length, day].compactMap(\.self)
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

/// What « Mark the season as watched » marks: the season as Jellyfin holds it (one call, every episode with it), else
/// each episode (split between two of Jellyfin's seasons); `played`: all its episodes are watched, and the button
/// offers « unwatched » (seasonMark of Mira web, EpisodePages of Core, cases of tests/fixtures/seasons.json).
struct SeasonMark: Equatable {
    let ids: [String]
    let played: Bool

    /// `episodes`: those of one season.
    static func of(_ episodes: [Item]) -> SeasonMark {
        let held = Set(episodes.map(\.seasonId))
        let ids = held.count == 1 && episodes.first?.seasonId != nil ? [episodes[0].seasonId!] : episodes.map(\.id)
        return SeasonMark(ids: ids, played: !episodes.isEmpty && episodes.allSatisfy { $0.userData?.played == true })
    }
}

/// The episode Play opens on a series when Jellyfin's next up says none (a series watched to its end, or watched out of
/// order; Shows/NextUp also leaves the specials out): one left halfway, else the first not watched, else the one after
/// the episode watched last (a series watched again), else the first, to watch it again. The numbered seasons come
/// before the specials, which Jellyfin lists first (season 0): « Watch again » starts at S01E01, not at a making-of
/// (checked on 12.1). EpisodePages.Start of Core, cases of tests/fixtures/series-start.json.
enum SeriesStart {
    static func start(_ episodes: [Item]) -> Item? { unfinished(episodes) ?? again(episodes) ?? numbered(episodes).first }

    /// A series watched again: the episode after the one watched last, so that S01E03 follows S01E02 watched again
    /// yesterday (Shows/NextUp, without enableRewatching, answers none once every episode is watched, checked on 12.1).
    /// Nil when that one is a special, or the last episode (watched to its end, or marked watched at once: Jellyfin dates
    /// each episode in turn, in their order), or shares its date with another. EpisodePages.Again of Core.
    static func again(_ episodes: [Item]) -> Item? {
        let dated: [(item: Item, date: Date)] = episodes
            .compactMap { item in item.userData?.lastPlayedDate.flatMap(HomeContent.parseDate).map { (item: item, date: $0) } }
            .sorted { $0.date > $1.date }
        guard let last = dated.first, dated.count < 2 || dated[1].date < last.date else { return nil }
        let ordered = episodes.filter { ($0.parentIndexNumber ?? 1) != 0 }
        guard let at = ordered.firstIndex(where: { $0.id == last.item.id }), at + 1 < ordered.count else { return nil }
        return ordered[at + 1]
    }

    /// One left halfway (short of its last 10 %, as Resumable of Core), else the first not watched; nil when all are.
    static func unfinished(_ episodes: [Item]) -> Item? {
        let ordered = numbered(episodes)
        return ordered.first { ($0.userData?.playbackPositionTicks ?? 0) > 0 && ItemText.progress($0) < 0.9 }
            ?? ordered.first { $0.userData?.played != true }
    }

    /// The numbered seasons' episodes, then the specials, each in Jellyfin's order (an episode without a season is in the first).
    private static func numbered(_ episodes: [Item]) -> [Item] {
        episodes.filter { ($0.parentIndexNumber ?? 1) != 0 } + episodes.filter { ($0.parentIndexNumber ?? 1) == 0 }
    }
}
