import SwiftUI

/// A series' episodes on its page: its seasons to choose from, then a grid of tiles, each episode with its still, how
/// far it was watched and a Play of its own; the season shown first is the one of the episode to watch next.
struct EpisodeList: View {
    let series: Item
    /// The episode Play plays on the series' page.
    let next: Item?
    /// The series' page read (`next` known): the season shown first waits for it, never Specials first and then a
    /// glide to the season of the episode to watch (audit I6).
    var ready = true
    @Environment(AppModel.self) private var app
    @State private var seasons: [Item] = []
    @State private var seasonsLoaded = false
    @State private var season: String?
    /// Each season's episodes as Jellyfin last gave them: a season seen once comes back at once, read again behind.
    @State private var loaded: [String: [Item]] = [:]
    /// The season whose episodes are on screen: the one before, dimmed and untouchable, until the one chosen is read,
    /// so that « Mark » or « Keep the season » never acts on a season no longer chosen (audit I1).
    @State private var onScreen: String?
    @State private var failed = false
    /// A season chosen by hand: the episode to watch next no longer moves the list.
    @State private var picked = false
    @State private var marking = false
    /// This series' episodes from TheTVDB (through Jellyfin), « à leur place » (EpisodeShelf): the seasons the library
    /// has none of among the seasons, the episodes it lacks between those it has; the thin lines opened.
    @State private var tvdb: [Item] = []
    /// The library's episodes of every season, their numbers only: those numbered from the series' start
    /// (« S02E26 » for S02E01, AbsoluteNumbers) leave TheTVDB's virtual ones out.
    @State private var held: [Item] = []
    private var virtuals: [Item] { AbsoluteNumbers.unheld(tvdb, held: held) }
    @State private var shelfOpen: Set<String> = []
    /// Its bonus videos (« La pastille », SeriesExtras): the chip « Extras » after its seasons shows them.
    @State private var extras: [Item] = []
    /// « Télécharger les manquants » (MissingViews): an administrator's buttons, and how what was asked is going.
    @State private var missing = MissingWatch()
    @Environment(\.dynamicTypeSize) private var typeSize
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// The white pill of the season shown, one for the row (« La gelée », as on Windows and the Mac): sent as a jelly to
    /// the season chosen (JellyPill), the names under it in black, cut by its shape, so no name is ever grey on grey.
    @State private var pill = JellyPill()
    /// The season the pill stands on or goes to; nil before the row's first layout.
    @State private var pillSeason: String?
    @State private var pillMoving = false
    @State private var pillRound = 0
    /// Each chip's place in the row (its own space: scrolling moves none).
    @State private var chipFrames: [String: CGRect] = [:]
    private static let rowSpace = "series.seasons.row"

    var body: some View {
        let today = EpisodeCalendar.Day.today()
        let showMissing = app.preferences.showMissingEpisodes
        let chips = self.chips(today: today, showMissing: showMissing)
        VStack(alignment: .leading, spacing: 14) {
            if chips.count > 1 {
                // The chips slide sideways under a finger (no list that opens, as the numbered pills of Windows and Mac);
                // with many seasons (Monogatari Series: 14), the one shown is brought into sight.
                ScrollViewReader { proxy in
                    ScrollView(.horizontal) {
                        seasonRow(chips)
                    }
                    .scrollIndicators(.hidden)
                    .accessibilityIdentifier("series.seasons")
                    .onAppear { if let season { proxy.scrollTo(season, anchor: .center) } }
                    .onChange(of: season) { _, shown in
                        guard let shown else { return }
                        // The row brought along at the pill's pace, never ahead of it.
                        withAnimation(reduceMotion ? nil : .smooth(duration: 0.5)) { proxy.scrollTo(shown, anchor: .center) }
                    }
                }
            } else if let only = chips.first?.name {
                Text(verbatim: only).font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
            }
            // The season's episodes and the room they take on the server (StorageSize), as Windows and Mac say over their grid.
            if absentSeason == nil, !episodes.isEmpty {
                Text(verbatim: [String(localized: "\(episodes.count) episodes"), StorageSize.text(StorageSize.sum(episodes, StorageSize.sizes(episodes)))].compactMap(\.self).joined(separator: " · "))
                    .font(MiraFont.footnote)
                    .foregroundStyle(Palette.muted)
                    .opacity(stale ? 0.45 : 1)
                    .accessibilityIdentifier("series.seasonSize")
            }
            // The season's counter, « 2 of 6 watched » beside a ring (maquette B, ogsas 2026-11-18), as on Windows and Mac:
            // a tap marks the whole season watched, or unwatched once all are. On every season, one alone too.
            if !episodes.isEmpty, absentSeason == nil {
                let mark = SeasonMark.of(episodes)
                let seen = episodes.count(where: { $0.userData?.played == true })
                Button {
                    Task { await markSeason(mark) }
                } label: {
                    SeasonCounter(seen: seen, total: episodes.count)
                }
                .buttonStyle(.plain)
                .disabled(marking || stale)
                .opacity(stale ? 0.45 : 1)
                .accessibilityLabel(mark.played ? Text("Mark the season as unwatched") : Text("Mark the season as watched"))
                .accessibilityValue(Text(verbatim: SeasonCounter.words(seen: seen, total: episodes.count)))
                .accessibilityIdentifier("series.markSeason")
            }
            KeepSeasonButton(episodes: episodes)
                .disabled(stale)
                .opacity(stale ? 0.45 : 1)
            if failed && episodes.isEmpty {
                Text(verbatim: Messages.text(JellyfinError.unreachable)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
            if season == Self.extrasId {
                ExtrasPage(series: series, extras: extras, columns: typeSize.isAccessibilitySize ? 1 : 2)
                    .transition(.opacity)
            } else if let absent = absentSeason {
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
                // The season before, dimmed, until the one chosen comes; then a fade from one to the other.
                .opacity(stale ? 0.45 : 1)
                .allowsHitTesting(!stale)
                .id(shown)
                .transition(.opacity)
            }
        }
        .animation(reduceMotion ? nil : .easeOut(duration: 0.2), value: shown)
        .animation(reduceMotion ? nil : .easeOut(duration: 0.15), value: stale)
        .environment(missing)
        .task(id: "\(series.id)-\(missing.round)") { await missing.watch(series, app: app) }
        // Episodes asked from this page placed: they leave the hollow tiles for their own.
        .onChange(of: missing.placedRound) {
            Task {
                VirtualEpisodes.forget()
                await loadSeasons()
                await loadVirtuals()
            }
        }
        // Waiting for Jellyfin to list what was placed: the seasons and episodes read again (a held one hides its hollow tile).
        .onChange(of: missing.lookRound) { Task { await loadSeasons() } }
        .sheet(item: Binding(get: { missing.asked }, set: { missing.asked = $0 })) { asked in
            MissingSheet(series: series, asked: asked, before: { await episodesBefore(asked.ask.season) }, watch: missing)
        }
        .task(id: series.id) { await loadSeasons() }
        .task(id: series.id) { await loadExtras() }
        .task(id: series.id) {
            await loadVirtuals()
            // A series the library holds no season of: its first season known to TheTVDB.
            _ = chooseSeason()
        }
        .task(id: season) { await loadEpisodes() }
        .onChange(of: season) { _, chosen in
            // A season read before: on screen at once (read again behind).
            if let chosen, loaded[chosen] != nil { onScreen = chosen }
            placePill()
        }
        .onChange(of: chipFrames) { placePill() }
        .onChange(of: ready) { _ = chooseSeason() }
        // A title stopped or marked: the season shown is read again in place (same episodes, same rows).
        .onChange(of: app.playback) { Task { await loadEpisodes() } }
        // The episode to watch next, known after the seasons: its season comes first.
        .onChange(of: next?.seasonId) { _ = chooseSeason() }
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
        // In their order, those the library lacks among the others (« Season 1 · not in library » before Season 2, audit
        // I5); Specials first, as Jellyfin lists them; a season without a number last.
        let library: [Chip] = seasons.map { Chip(id: $0.id, name: $0.name ?? "", season: $0.indexNumber) }
        let lacking: [Chip] = absent.map { Chip(id: Self.absentPrefix + "\($0)", name: EpisodeShelf.seasonLabel($0, virtuals: virtuals, today: today), season: $0) }
        let all: [Chip] = Self.ordered(library + lacking)
        // « Extras » after the last season, also after a single one (SeriesExtras).
        return SeriesExtras.pills(seasons: all.count, extras: extras.count) && !extras.isEmpty ? all + [Chip(id: Self.extrasId, name: SeriesExtras.name())] : all
    }

    /// By season number, Jellyfin's order kept between equals.
    private static func ordered(_ chips: [Chip]) -> [Chip] {
        let numbered = chips.enumerated().map { (key: $0.element.season ?? Int.max, at: $0.offset, chip: $0.element) }
        return numbered.sorted { $0.key != $1.key ? $0.key < $1.key : $0.at < $1.at }.map(\.chip)
    }

    private static let absentPrefix = "absent-"
    private static let extrasId = "extras"

    /// The season shown, when it is one the library has none of.
    private var absentSeason: Int? {
        guard let season, season.hasPrefix(Self.absentPrefix) else { return nil }
        return Int(season.dropFirst(Self.absentPrefix.count))
    }

    /// The season shown: its episodes, those it lacks at their place.
    private func shelf(today: EpisodeCalendar.Day, showMissing: Bool) -> [EpisodeShelf.Line] {
        guard let number = seasons.first(where: { $0.id == shown })?.indexNumber else { return episodes.map { EpisodeShelf.Line(kind: .episode, episodes: [$0]) } }
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
            seasonsLoaded = true
            if !chooseSeason() { await loadEpisodes() }
        } catch JellyfinError.unauthorized { app.sessionEnded() } catch { failed = true }
    }

    /// The season to show, unless one was chosen by hand and is still there: the season of the episode to watch next,
    /// else the one shown, else the first numbered season (Specials, season 0, only when there is nothing else), else
    /// the first known to TheTVDB. Waits for the seasons and the page (`ready`). True when it changed the season.
    @discardableResult private func chooseSeason() -> Bool {
        guard seasonsLoaded, ready else { return false }
        let ids = chips(today: .today(), showMissing: app.preferences.showMissingEpisodes).map(\.id)
        if picked, let season, ids.contains(season) { return false }
        let wanted = seasons.first { $0.id == next?.seasonId }?.id
            ?? season.flatMap { ids.contains($0) ? $0 : nil }
            ?? seasons.first { ($0.indexNumber ?? 1) > 0 }?.id
            ?? ids.first
        guard let wanted, wanted != season else { return false }
        season = wanted
        return true
    }

    /// The pill sent to the season shown: at once the first time (and with « Reduce Motion »), as a jelly after.
    private func placePill() {
        guard let season, let frame = chipFrames[season] else { return }
        let left = Double(frame.minX), right = Double(frame.maxX)
        if pillSeason == nil || pillSeason == season || reduceMotion {
            if pillSeason == nil || pill.targetLeft != left || pill.targetRight != right { pill.put(left, right) }
            if pillSeason != season { pillMoving = false }
        } else {
            pill.go(left, right, now: Date().timeIntervalSinceReferenceDate)
            pillMoving = true
            pillRound += 1
            let round = pillRound
            Task {
                // Still within half a second (JellyPill); the frames' clock then stops.
                try? await Task.sleep(for: .seconds(1))
                if pillRound == round { pillMoving = false }
            }
        }
        pillSeason = season
    }

    /// The pill as drawn at `date`: the season's chip while still, the jelly while it moves.
    private func pillShape(at date: Date) -> CGRect? {
        guard let season, let frame = chipFrames[season], pillSeason != nil else { return nil }
        guard pillMoving else { return frame }
        let shape = pill.at(date.timeIntervalSinceReferenceDate)
        let height = frame.height * shape.height
        return CGRect(x: shape.left, y: frame.midY - height / 2, width: max(0, shape.right - shape.left), height: height)
    }

    /// The row of seasons: each chip on its grey capsule, white names; over them, the pill and the same names in black
    /// seen only through it.
    private func seasonRow(_ chips: [Chip]) -> some View {
        chipStack(chips, lit: false)
            .coordinateSpace(.named(Self.rowSpace))
            .overlay(alignment: .topLeading) {
                TimelineView(.animation(minimumInterval: nil, paused: !pillMoving)) { context in
                    if let shape = pillShape(at: context.date) {
                        ZStack(alignment: .topLeading) {
                            Capsule().fill(Palette.accent)
                                .frame(width: shape.width, height: shape.height)
                                .offset(x: shape.minX, y: shape.minY)
                            chipStack(chips, lit: true)
                                .mask(alignment: .topLeading) {
                                    Capsule().frame(width: shape.width, height: shape.height).offset(x: shape.minX, y: shape.minY)
                                }
                        }
                    }
                }
                .allowsHitTesting(false)
                .accessibilityHidden(true)
            }
            // Drawn by the pill's own clock: no fade of the page's carries it along.
            .transaction { $0.animation = nil }
    }

    /// The chips; `lit`: their names only, in black, where the pill is.
    private func chipStack(_ chips: [Chip], lit: Bool) -> some View {
        HStack(spacing: 8) {
            ForEach(chips) { entry in
                if entry.id == Self.extrasId {
                    // A thin line between the seasons and « Extras », as on Windows and the Mac.
                    Rectangle().fill(lit ? Color.clear : Palette.line).frame(width: 1, height: 22).padding(.horizontal, 4)
                }
                if lit {
                    chipLabel(entry, ink: Palette.onAccent)
                } else {
                    Button {
                        picked = true
                        season = entry.id
                    } label: {
                        chipLabel(entry, ink: Palette.ink).background(Palette.raised, in: .capsule)
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(entry.id == season ? .isSelected : [])
                    .accessibilityIdentifier(entry.id == Self.extrasId ? "series.chip.extras" : "series.chip")
                    .onGeometryChange(for: CGRect.self) { $0.frame(in: .named(Self.rowSpace)) } action: { chipFrames[entry.id] = $0 }
                    .id(entry.id)
                }
            }
        }
    }

    private func chipLabel(_ entry: Chip, ink: Color) -> some View {
        let coming = entry.season.flatMap { missing.progress(season: $0, episodes: nil) }.map { MissingDownloads.pill($0) } ?? ""
        return Text(verbatim: coming.isEmpty ? entry.name : "\(entry.name)  ·  \(coming)")
            .font(MiraFont.callout)
            .foregroundStyle(ink)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
    }

    /// The season whose episodes are on screen: the one chosen once read, the one before until then.
    private var shown: String? {
        guard let season else { return nil }
        if absentSeason != nil || season == Self.extrasId || loaded[season] != nil { return season }
        return onScreen
    }

    /// The season chosen is still being read: what shows is the one before, dimmed.
    private var stale: Bool { shown != season }

    /// The episodes on screen.
    private var episodes: [Item] {
        guard let shown, absentSeason == nil, shown != Self.extrasId else { return [] }
        return loaded[shown] ?? []
    }

    /// The series' bonus videos, when Jellyfin counts some (or did not say).
    private func loadExtras() async {
        guard let client = app.client, series.specialFeatureCount != 0 else { return }
        if let found = try? await client.extras(series.id) { extras = found }
    }

    /// The episodes of the seasons before `season`, the library's and TheTVDB's (MissingDownloads.before).
    private func episodesBefore(_ season: Int) async -> Int {
        guard season > 1, let client = app.client else { return 0 }
        if !held.isEmpty { return MissingDownloads.before(AbsoluteNumbers.seasonal(held, virtuals: tvdb) + virtuals, season: season) }
        var all = virtuals
        for held in seasons where (held.indexNumber ?? 0) >= 1 && (held.indexNumber ?? 0) < season {
            if let found = try? await client.episodes(series.id, season: held.id) { all += found }
        }
        return MissingDownloads.before(all, season: season)
    }

    /// TheTVDB's episodes of this series, and the library's numbers when there are some (one light request).
    private func loadVirtuals() async {
        let found = await EpisodeShelf.virtuals(of: series, app: app)
        if !found.isEmpty, let client = app.client, let numbers = try? await client.episodeNumbers(series.id) { held = numbers }
        tvdb = found
    }

    private func loadEpisodes() async {
        guard let client = app.client, let season else { return }
        // A season the library has none of: nothing to ask Jellyfin.
        if absentSeason != nil || season == Self.extrasId { failed = false; return }
        do {
            let found = try await client.episodes(series.id, season: season)
            // Jellyfin's specials « aired during » the season come with it (Display specials within seasons): they stay
            // in Specials, never counted, marked or kept with the season (audit I7), as on Windows and the Mac.
            loaded[season] = found.filter { $0.seasonId == nil || $0.seasonId == season }
            // A late answer for a season left meanwhile is kept for later, never shown in place of the one chosen.
            if self.season == season {
                onScreen = season
                failed = false
            }
        } catch JellyfinError.unauthorized { app.sessionEnded() } catch is CancellationError {} catch { if self.season == season { failed = true } }
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

/// One episode as a tile: its still (a check on it once watched, its progress, « UP NEXT » and a white play for the
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
                            // White as the page's Play, as on Windows and the Mac: the tile wears no frame of its own.
                            Image(systemName: "play.fill")
                                .font(.footnote.weight(.bold))
                                .foregroundStyle(Palette.onAccent)
                                .frame(width: 36, height: 36)
                                .background(Palette.ink, in: .circle)
                        }
                    }
                    .clipShape(.rect(cornerRadius: 10, style: .continuous))
                    .overlay {
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .strokeBorder(.white.opacity(0.06), lineWidth: 1)
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

    /// « 45 min · Oct 25 · 1.2 GB », « 32 min left · Oct 25 · 1.2 GB » (EpisodeTiles.Meta of Core): its files' size
    /// last (StorageSize; the season's episodes come with their files).
    private var detail: String? {
        let length = ItemText.remaining(episode) ?? episode.runTimeTicks.flatMap { $0 > 0 ? ItemText.duration(seconds: $0 / ItemText.ticksPerSecond) : nil }
        let day = EpisodeCalendar.airDay(episode).map { EpisodeShelf.shortDay($0, today: .today()) }
        let parts = [length, day, StorageSize.text(StorageSize.of(episode.mediaSources))].compactMap(\.self)
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

/// A season's counter (maquette B, ogsas 2026-11-18): a faint ring, the part watched drawn over it in white from the
/// top, clockwise, the ring and its tick once all are; then « 2 of 6 watched », « All 6 watched », « Watched » for a
/// season of one, as EpisodeTiles.Counter of Core. Grey, as the line above it.
struct SeasonCounter: View {
    let seen: Int
    let total: Int

    var body: some View {
        HStack(spacing: 8) {
            ZStack {
                if seen >= total {
                    Circle().stroke(Palette.muted, lineWidth: 2)
                    Tick().stroke(Palette.muted, style: StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round))
                } else {
                    Circle().stroke(Palette.muted.opacity(0.35), lineWidth: 2)
                    Circle()
                        .trim(from: 0, to: Double(seen) / Double(max(total, 1)))
                        .stroke(Palette.ink, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                }
            }
            .frame(width: 17, height: 17)
            .padding(1)
            .accessibilityHidden(true)
            Text(verbatim: Self.words(seen: seen, total: total))
        }
        .font(MiraFont.callout)
        .foregroundStyle(Palette.muted)
        .contentShape(Rectangle())
    }

    static func words(seen: Int, total: Int) -> String {
        if total > 0, seen >= total { return total == 1 ? String(localized: "Watched") : String(localized: "All \(total) watched") }
        return String(localized: "\(seen) of \(total) watched")
    }

    /// The tick of the « Watched » glyph (IconPaths' Tick), on the ring's 18-point square.
    private struct Tick: Shape {
        func path(in rect: CGRect) -> Path {
            let scale = rect.width / 18
            var path = Path()
            path.move(to: CGPoint(x: 4.8 * scale, y: 9.4 * scale))
            path.addLine(to: CGPoint(x: 7.6 * scale, y: 12.2 * scale))
            path.addLine(to: CGPoint(x: 13.2 * scale, y: 6 * scale))
            return path
        }
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
