import SwiftUI
import UIKit

/// A title's page: its artwork, what it is, how far it was watched, its summary, Play in Apple's player, and the
/// languages to play it in (Apple's player cannot change a conversion's audio, as on Mira web). A series plays its
/// next episode and lists its seasons and episodes.
struct TitleView: View {
    let route: TitleRoute
    @Environment(AppModel.self) private var app
    @Environment(\.displayScale) private var displayScale
    @State private var item: Item
    @State private var width: CGFloat = 402
    /// Scrolled past the picture: the bar then shows the title's name and its own edge, which over the picture
    /// would only veil it.
    @State private var pastArtwork = false
    /// Jellyfin answered for this title (its files, its next episode): until then Play and the languages keep their
    /// place without changing the page's height.
    @State private var loaded = false
    /// What Play plays: the title, or a series' next episode.
    @State private var next: Item?
    @State private var audioIndex: Int?
    @State private var subtitleIndex: Int?
    /// Lecture of another title than the group's: asked first, naming the group's title.
    @State private var replacing: (item: Item, fromStart: Bool, current: String)?

    init(route: TitleRoute) {
        self.route = route
        _item = State(initialValue: route.item)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                artwork
                VStack(alignment: .leading, spacing: 14) {
                    if !hasBackdrop, let client = app.client, let poster = Artwork.of(item, .poster) {
                        // No wide picture in Jellyfin (a home video, a film without metadata): its poster, as on the
                        // wide page of Mira web.
                        RemoteImage(url: client.imageURL(poster, width: 140, scale: displayScale), blurHash: poster.blurHash, label: item.name ?? "", width: 140)
                            .frame(width: 140, height: 210)
                            .clipShape(.rect(cornerRadius: 12, style: .continuous))
                            .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.white.opacity(0.06)) }
                            .accessibilityHidden(true)
                    }
                    if item.isEpisode, let series = item.seriesName, let seriesId = item.seriesId {
                        // The series' page, from one of its episodes.
                        NavigationLink(value: TitleRoute(item: Item(id: seriesId, name: series, type: "Series"), source: "series-\(seriesId)")) {
                            HStack(spacing: 4) {
                                Text(verbatim: series)
                                Image(systemName: "chevron.right").font(.caption.weight(.bold)).accessibilityHidden(true)
                            }
                            .font(MiraFont.headline)
                            .foregroundStyle(Palette.muted)
                        }
                        .buttonStyle(.plain)
                        .accessibilityHint(Text("Opens the show"))
                    }
                    Text(heading)
                        .font(MiraFont.title)
                        .foregroundStyle(Palette.ink)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityAddTraits(.isHeader)
                        .accessibilityIdentifier("title.name")
                    let meta = [ItemText.meta(item), ItemText.ratings(item)].filter { !$0.isEmpty }.joined(separator: " · ")
                    if !meta.isEmpty {
                        Text(meta).font(MiraFont.subheadline).foregroundStyle(Palette.muted)
                    }
                    let progress = ItemText.progress(item)
                    if progress > 0 {
                        HStack(spacing: 12) {
                            ProgressLine(value: progress).frame(maxWidth: 160)
                            if let left = ItemText.remaining(item) {
                                Text(left).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                            }
                        }
                    }
                    play.padding(.top, 6)
                    TitleActions(item: $item)
                    let overview = OverviewText.plain(item.overview)
                    if !overview.isEmpty {
                        // Four lines, then « More »: a series' episodes come right under it.
                        ClampedText(text: overview)
                            .font(MiraFont.body)
                            .foregroundStyle(Palette.ink)
                            .lineSpacing(3)
                            .padding(.top, 10)
                    }
                    if let genres = item.genres, !genres.isEmpty, !item.isEpisode {
                        // Each genre opens Movies or Shows with only that genre, as on Mira web.
                        ScrollView(.horizontal) {
                            HStack(spacing: 8) {
                                ForEach(genres, id: \.self) { genre in
                                    NavigationLink(value: GenreRoute(kind: item.isSeries ? .shows : .movies, genre: genre)) {
                                        Text(verbatim: genre)
                                            .font(MiraFont.callout)
                                            .foregroundStyle(Palette.ink)
                                            .padding(.horizontal, 14)
                                            .padding(.vertical, 8)
                                            .background(Palette.raised, in: .capsule)
                                    }
                                    .buttonStyle(.plain)
                                }
                            }
                        }
                        .scrollIndicators(.hidden)
                        .accessibilityLabel(Text("Genres"))
                    }
                    if let playable { TogetherSection(item: playable).padding(.top, 8) }
                    if item.isSeries {
                        EpisodeList(series: item, next: next).padding(.top, 18)
                    }
                }
                .padding(.horizontal, 20)
                .padding(.top, 4)
                .padding(.bottom, 40)
                .frame(maxWidth: 720, alignment: .leading)
            }
        }
        .scrollIndicators(.hidden)
        .ignoresSafeArea(edges: .top)
        .onScrollGeometryChange(for: Bool.self) { geometry in
            geometry.contentOffset.y + geometry.contentInsets.top > artworkHeight - 110
        } action: { _, past in
            withAnimation(.easeOut(duration: 0.2)) { pastArtwork = past }
        }
        .scrollEdgeEffectHidden(!pastArtwork, for: .top)
        .background(Palette.bg)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .navigationTitle(item.name ?? "")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .principal) {
                Text(verbatim: item.name ?? "")
                    .font(MiraFont.headline)
                    .foregroundStyle(Palette.ink)
                    .lineLimit(1)
                    .opacity(pastArtwork ? 1 : 0)
                    .accessibilityHidden(!pastArtwork)
            }
        }
        .navigationDestination(for: GenreRoute.self) { route in LibraryView(kind: route.kind, genre: route.genre) }
        .task(id: route.item.id) { await load() }
        // A title stopped or marked: read again in place, what is shown stays until the new answer.
        .onChange(of: app.playback) { Task { await load() } }
    }

    private func load() async {
        guard let client = app.client else { return }
        do {
            let fresh = try await client.item(route.item.id)
            if fresh.isSeries { next = try await client.nextUp(series: fresh.id) }
            item = fresh
            loaded = true
        } catch JellyfinError.unauthorized { app.sessionEnded() } catch {}
    }

    private var heading: String {
        guard item.isEpisode else { return item.name ?? "" }
        return [ItemText.episodeCode(item), item.name ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
    }

    /// The bars' height over the page: the status bar and the navigation bar's back button.
    private var bars: CGFloat {
        _ = width
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        return max(20, scene?.keyWindow?.safeAreaInsets.top ?? 0) + 44
    }

    /// The picture whole (16:9 across the page, never wider than 560 points tall), under the bars.
    private var pictureHeight: CGFloat { min(width * 9 / 16, 560 - bars) }

    /// A wide picture of the title (an episode's own, when its series has no backdrop); never a poster cut to 16:9.
    private var heroArt: Artwork? { Artwork.of(item, .backdrop) ?? (item.isEpisode ? Artwork.of(item, .wide) : nil) }

    private var hasBackdrop: Bool { heroArt != nil }

    private var artworkHeight: CGFloat { hasBackdrop ? bars + pictureHeight : bars + 8 }

    /// The backdrop whole, just under the bars, never cut by them: above it, the same picture blurred carries on
    /// behind the bars; under it, it fades into the page. Pulled down, both grow.
    @ViewBuilder private var artwork: some View {
        let height = artworkHeight
        if let client = app.client, let art = heroArt {
            let url = client.imageURL(art, width: width, scale: displayScale)
            ZStack(alignment: .bottom) {
                RemoteImage(url: url, blurHash: art.blurHash, width: width)
                    .frame(width: width, height: height)
                    .blur(radius: 30, opaque: true)
                    .overlay(Color.black.opacity(0.25))
                RemoteImage(url: url, blurHash: art.blurHash, width: width)
                    .frame(width: width, height: pictureHeight)
                    .mask {
                        LinearGradient(stops: [
                            .init(color: .clear, location: 0),
                            .init(color: .black, location: 0.18),
                        ], startPoint: .top, endPoint: .bottom)
                    }
            }
            .frame(width: width, height: height)
            .overlay {
                LinearGradient(stops: [
                    .init(color: .clear, location: 0.55),
                    .init(color: Palette.bg.opacity(0.6), location: 0.8),
                    .init(color: Palette.bg, location: 1),
                ], startPoint: .top, endPoint: .bottom)
            }
            .clipped()
            .stretchesWhenPulled(height: height)
            .accessibilityHidden(true)
        } else {
            Color.clear.frame(height: height)
        }
    }

    /// The film or episode Play plays.
    private var playable: Item? { item.isSeries ? next : item }

    /// Play (Resume, « Play S1 · E3 » for a series), From the beginning when it was started, the languages.
    @ViewBuilder private var play: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let playable {
                let resume = ItemText.progress(playable) > 0
                Button {
                    start(playable, fromStart: false)
                } label: {
                    HStack(spacing: 8) {
                        Image(systemName: "play.fill")
                        Text(playLabel(playable, resume: resume)).font(MiraFont.headline).lineLimit(2).multilineTextAlignment(.center)
                    }
                    .foregroundStyle(Palette.onAccent)
                    .frame(maxWidth: .infinity, minHeight: 32)
                }
                .buttonStyle(.glassProminent)
                .accessibilityIdentifier("title.play")
                if resume {
                    Button {
                        start(playable, fromStart: true)
                    } label: {
                        Label("From the beginning", systemImage: "backward.end.fill").font(MiraFont.callout)
                    }
                    .buttonStyle(.glass)
                    .accessibilityIdentifier("title.fromStart")
                }
                if let source = playable.mediaSources?.first {
                    ChipRow {
                        // What will play: chosen here, else the series' last tracks or the languages of Settings.
                        let preferences = app.preferences
                        let auto = TrackChoice.choose(source, memory: preferences.tracks(for: playable.seriesId),
                                                      audioOrder: preferences.audioOrder, subtitleOrder: preferences.subtitleOrder)
                        TrackMenus(source: source, audioIndex: Binding(get: { audioIndex ?? auto.audio }, set: { index in
                            audioIndex = index
                            remember(playable, source, audio: index, subtitle: subtitleIndex ?? auto.subtitle)
                        }), subtitleIndex: Binding(get: { subtitleIndex ?? auto.subtitle }, set: { index in
                            subtitleIndex = index
                            remember(playable, source, audio: audioIndex ?? auto.audio, subtitle: index)
                        }))
                    }
                } else if !loaded {
                    TrackPlaceholder()
                }
                SleepMenu(episode: playable.isEpisode)
            } else if !loaded {
                // A series' next episode not known yet: Play holds its place, then says which episode.
                Button {} label: {
                    HStack(spacing: 8) {
                        Image(systemName: "play.fill")
                        Text("Play").font(MiraFont.headline)
                    }
                    .foregroundStyle(Palette.onAccent)
                    .frame(maxWidth: .infinity, minHeight: 32)
                }
                .buttonStyle(.glassProminent)
                .disabled(true)
                TrackPlaceholder()
            }
        }
        .frame(maxWidth: 420, alignment: .leading)
        .onChange(of: playable?.id) {
            audioIndex = nil
            subtitleIndex = nil
        }
        .confirmationDialog(Text("Watch together"), isPresented: Binding(get: { replacing != nil }, set: { if !$0 { replacing = nil } }), titleVisibility: .visible, presenting: replacing) { asked in
            Button("Replace for the group") {
                app.play(asked.item, fromStart: asked.fromStart, audioIndex: audioIndex, subtitleIndex: subtitleIndex)
            }
            Button("Watch with the group") { joinGroupTitle() }
            Button("Cancel", role: .cancel) {}
        } message: { asked in
            Text(verbatim: SyncRules.replaceQuestion(asked.current) + "\n" + String(localized: "The group is watching it now. Play replaces it for everyone, at the same moment."))
        }
    }

    /// Play: alone, or for the group this iPhone is in (its own title joined where it is; another one asked first).
    private func start(_ playable: Item, fromStart: Bool) {
        let together = app.together
        if together.group != nil, let queue = together.queue, queue.itemId == playable.id {
            joinGroupTitle()
            return
        }
        if together.replaces(playable.id), let queue = together.queue {
            Task {
                let current = (try? await app.client?.item(queue.itemId)).map(SyncRules.titleName) ?? ""
                replacing = (playable, fromStart, current)
            }
            return
        }
        app.play(playable, fromStart: fromStart, audioIndex: audioIndex, subtitleIndex: subtitleIndex)
    }

    /// Tracks chosen for an episode: the series' next episodes start with the same languages, on every device.
    private func remember(_ playable: Item, _ source: MediaSource, audio: Int?, subtitle: Int?) {
        guard let series = playable.seriesId else { return }
        app.preferences.remember(TrackChoice.memory(source, audio: audio, subtitle: subtitle), for: series)
    }

    /// The group's title, where the group is: Jellyfin holds the others until this iPhone is ready.
    private func joinGroupTitle() {
        guard let queue = app.together.queue, let client = app.client else { return }
        Task {
            guard let title = try? await client.item(queue.itemId) else { return }
            app.play(title, groupStart: queue.startTicks)
        }
    }

    private func playLabel(_ playable: Item, resume: Bool) -> String {
        if app.together.group != nil {
            let code = item.isSeries ? ItemText.episodeCode(playable) : ""
            let resumeAt = resume ? Double(playable.userData?.playbackPositionTicks ?? 0) / Double(ItemText.ticksPerSecond) : 0
            return SyncRules.groupPlayLabel(code: code, resumeAt: resumeAt, groupTitle: app.together.queue?.itemId == playable.id)
        }
        if item.isSeries {
            let code = ItemText.episodeCode(playable)
            return resume ? String(localized: "Resume \(code)") : String(localized: "Watch \(code)")
        }
        return resume ? String(localized: "Resume") : String(localized: "Play")
    }
}

/// Movies or Shows with one genre only, opened from a title's page.
struct GenreRoute: Hashable {
    let kind: LibraryView.Kind
    let genre: String
}

/// The place of Audio and Subtitles while Jellyfin has not said the file's tracks: two grey chips of their size.
struct TrackPlaceholder: View {
    var body: some View {
        HStack(spacing: 10) {
            Capsule().fill(Palette.raised).frame(width: 128, height: 44)
            Capsule().fill(Palette.raised).frame(width: 150, height: 44)
        }
        .accessibilityHidden(true)
    }
}

/// Audio and Subtitles: a menu each, the file's tracks by Mira's names, Jellyfin's choice until another is picked.
struct TrackMenus: View {
    let source: MediaSource
    @Binding var audioIndex: Int?
    @Binding var subtitleIndex: Int?

    var body: some View {
        let audio = source.audio, subtitles = source.subtitles
        let audioNames = TrackNames.names(audio), subtitleNames = TrackNames.names(subtitles)
        if audio.count > 1 {
            let current = audioIndex ?? source.defaultAudioStreamIndex ?? audio.first?.index
            Menu {
                Picker("Audio", selection: Binding(get: { current ?? -1 }, set: { audioIndex = $0 })) {
                    ForEach(audio, id: \.index) { stream in
                        Text(verbatim: audioNames[stream.index] ?? "").tag(stream.index)
                    }
                }
            } label: {
                Label(current.flatMap { audioNames[$0] } ?? String(localized: "Audio"), systemImage: "speaker.wave.2.fill")
                    .font(MiraFont.callout)
                    .lineLimit(1)
            }
            .buttonStyle(.glass)
            .accessibilityLabel(Text("Audio"))
            .accessibilityValue(Text(verbatim: current.flatMap { audioNames[$0] } ?? ""))
            .accessibilityIdentifier("title.audio")
        }
        if !subtitles.isEmpty {
            let current = subtitleIndex ?? source.defaultSubtitleStreamIndex ?? -1
            let name = current >= 0 ? subtitleNames[current] : nil
            Menu {
                Picker("Subtitles", selection: Binding(get: { current }, set: { subtitleIndex = $0 })) {
                    Text("Off").tag(-1)
                    ForEach(subtitles, id: \.index) { stream in
                        Text(verbatim: subtitleNames[stream.index] ?? "").tag(stream.index)
                    }
                }
            } label: {
                Label(name ?? String(localized: "Subtitles off"), systemImage: "captions.bubble.fill")
                    .font(MiraFont.callout)
                    .lineLimit(1)
            }
            .buttonStyle(.glass)
            .accessibilityLabel(Text("Subtitles"))
            .accessibilityValue(Text(verbatim: name ?? String(localized: "Off")))
            .accessibilityIdentifier("title.subtitles")
        }
    }
}
