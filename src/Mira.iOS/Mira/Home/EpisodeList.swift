import SwiftUI

/// A series' episodes on its page: its seasons to choose from, then each episode with its picture, how far it was
/// watched and a Play of its own; the season shown first is the one of the episode to watch next.
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

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if seasons.count > 1 {
                ScrollView(.horizontal) {
                    HStack(spacing: 8) {
                        ForEach(seasons) { entry in
                            let chosen = entry.id == season
                            Button {
                                picked = true
                                season = entry.id
                            } label: {
                                Text(verbatim: entry.name ?? "")
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
            } else if let only = seasons.first?.name {
                Text(verbatim: only).font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
            }
            // « Mark the season as watched »: only beside another season (with one, the series' own button does it).
            if seasons.count > 1, !episodes.isEmpty {
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
            if failed && episodes.isEmpty {
                Text(verbatim: Messages.text(JellyfinError.unreachable)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
            LazyVStack(alignment: .leading, spacing: 16) {
                ForEach(episodes) { episode in
                    EpisodeRow(episode: episode)
                }
            }
        }
        .task(id: series.id) { await loadSeasons() }
        .task(id: season) { await loadEpisodes() }
        // A title stopped or marked: the season shown is read again in place (same episodes, same rows).
        .onChange(of: app.playback) { Task { await loadEpisodes() } }
        // The episode to watch next, known after the seasons: its season comes first.
        .onChange(of: next?.seasonId) { _, id in
            if !picked, let id, seasons.contains(where: { $0.id == id }) { season = id }
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
            if season == nil || !seasons.contains(where: { $0.id == season }) {
                season = seasons.first { $0.id == next?.seasonId }?.id ?? seasons.first?.id
            } else {
                await loadEpisodes()
            }
        } catch JellyfinError.unauthorized { app.sessionEnded() } catch { failed = true }
    }

    private func loadEpisodes() async {
        guard let client = app.client, let season else { return }
        do {
            episodes = try await client.episodes(series.id, season: season)
            failed = false
        } catch JellyfinError.unauthorized { app.sessionEnded() } catch is CancellationError {} catch { failed = true }
    }
}

/// One episode: its picture with its progress and a play mark, its number, name, length and summary. The row opens the
/// episode's page (its languages); the picture plays it at once.
struct EpisodeRow: View {
    let episode: Item
    @Environment(AppModel.self) private var app
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        let width: CGFloat = 150
        HStack(alignment: .top, spacing: 12) {
            Button {
                app.play(episode)
            } label: {
                ZStack {
                    if let client = app.client {
                        let art = Artwork.of(episode, .wide)
                        RemoteImage(url: art.map { client.imageURL($0, width: width, scale: displayScale) }, blurHash: art?.blurHash, label: episode.name ?? "", width: width)
                    }
                    Image(systemName: "play.fill")
                        .font(.footnote.weight(.bold))
                        .foregroundStyle(Palette.ink)
                        .frame(width: 34, height: 34)
                        .glassEffect(.clear, in: .circle)
                }
                .frame(width: width, height: width * 9 / 16)
                .overlay(alignment: .bottom) {
                    let progress = ItemText.progress(episode)
                    if progress > 0 { ProgressLine(value: progress).padding(6) }
                }
                .clipShape(.rect(cornerRadius: 10, style: .continuous))
                .overlay { RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(.white.opacity(0.06)) }
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("Play \(heading)"))
            .accessibilityIdentifier("episode.play.\(episode.indexNumber ?? 0)")

            NavigationLink(value: TitleRoute(item: episode, source: "episode-\(episode.id)")) {
                VStack(alignment: .leading, spacing: 3) {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(verbatim: heading)
                            .font(MiraFont.headline)
                            .foregroundStyle(Palette.ink)
                            .lineLimit(2)
                            .multilineTextAlignment(.leading)
                        if episode.userData?.played == true {
                            Image(systemName: "checkmark.circle.fill")
                                .font(.footnote)
                                .foregroundStyle(Palette.muted)
                                .accessibilityLabel(Text("Watched"))
                        }
                    }
                    if let detail {
                        Text(verbatim: detail).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    }
                    if let overview = episode.overview, !overview.isEmpty {
                        Text(verbatim: overview).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(2).multilineTextAlignment(.leading)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
        }
        .titleMenu(episode)
    }

    private var heading: String {
        [episode.indexNumber.map { n in episode.indexNumberEnd.map { "\(n)–\($0)" } ?? "\(n)" }, episode.name]
            .compactMap(\.self).filter { !$0.isEmpty }.joined(separator: ". ")
    }

    /// « 45 min », « 32 min left ».
    private var detail: String? {
        if let left = ItemText.remaining(episode) { return left }
        guard let ticks = episode.runTimeTicks, ticks > 0 else { return nil }
        return ItemText.duration(seconds: ticks / ItemText.ticksPerSecond)
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
