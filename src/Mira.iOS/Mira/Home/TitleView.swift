import SwiftUI
import UIKit

/// A title's page, in the order it is used: the picture with the title on it, what it is in one line, Play beside
/// Favorite, Watched and « … », the languages on one quiet line, the summary in three lines, then a series' seasons and
/// episodes, and the genres at the end. A series plays its next episode.
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
    /// « Watch together » opened from « … ».
    @State private var showsTogether = false
    /// The round buttons beside Play, growing with the text.
    @ScaledMetric(relativeTo: .body) private var side: CGFloat = 48

    init(route: TitleRoute) {
        self.route = route
        _item = State(initialValue: route.item)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                artwork
                VStack(alignment: .leading, spacing: 18) {
                    header
                    actions
                    if let playable { TrackLine(playable: playable, loaded: loaded, audioIndex: $audioIndex, subtitleIndex: $subtitleIndex) }
                    if app.together.group != nil, let playable { TogetherSection(item: playable) }
                    else if !app.together.open.isEmpty { TogetherOpen() }
                    let overview = OverviewText.plain(item.overview)
                    if !overview.isEmpty {
                        // Three lines, then « More »: a series' episodes come right under it.
                        ClampedText(text: overview, lines: 3)
                            .font(MiraFont.body)
                            .foregroundStyle(Palette.ink.opacity(0.92))
                            .lineSpacing(2)
                    }
                    if item.isSeries {
                        EpisodeList(series: item, next: next).padding(.top, 6)
                    }
                    genres
                }
                .padding(.horizontal, 20)
                // Over the picture's fading bottom: the name sits on the film, not under it.
                .padding(.top, hasBackdrop ? -64 : 4)
                .padding(.bottom, 40)
                .frame(maxWidth: 720, alignment: .leading)
            }
        }
        .scrollIndicators(.hidden)
        .foldsTabBar()
        .ignoresSafeArea(edges: .top)
        .onScrollGeometryChange(for: Bool.self) { geometry in
            // The title reaching the bars: from there the bar has its edge and the title's name.
            geometry.contentOffset.y + geometry.contentInsets.top > artworkHeight - (hasBackdrop ? 64 : -4) - bars
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
        .sheet(isPresented: $showsTogether) { TogetherSheet() }
        .task(id: route.item.id) { await load() }
        // A title stopped or marked: read again in place, what is shown stays until the new answer.
        .onChange(of: app.playback) { Task { await load() } }
    }

    /// The series of an episode, the title (its logo when Jellyfin has one), what it is in one line, how far it was
    /// watched.
    @ViewBuilder private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            if !hasBackdrop, let client = app.client, let poster = Artwork.of(item, .poster) {
                // No wide picture in Jellyfin (a home video, a film without metadata): its poster, as on the wide page
                // of Mira web.
                RemoteImage(url: client.imageURL(poster, width: 140, scale: displayScale), blurHash: poster.blurHash, label: item.name ?? "", width: 140)
                    .frame(width: 140, height: 210)
                    .clipShape(.rect(cornerRadius: 12, style: .continuous))
                    .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.white.opacity(0.06)) }
                    .accessibilityHidden(true)
                    .padding(.bottom, 4)
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
            if !item.isEpisode, let client = app.client, let logo = Artwork.of(item, .logo) {
                LogoImage(url: client.imageURL(logo, width: 280, scale: displayScale), name: heading)
                    .frame(maxWidth: min(280, width * 0.72), maxHeight: 88, alignment: .bottomLeading)
                    .accessibilityAddTraits(.isHeader)
                    .accessibilityIdentifier("title.name")
            } else {
                Text(heading)
                    .font(MiraFont.title)
                    .foregroundStyle(Palette.ink)
                    .fixedSize(horizontal: false, vertical: true)
                    .shadow(color: .black.opacity(hasBackdrop ? 0.5 : 0), radius: 8)
                    .accessibilityAddTraits(.isHeader)
                    .accessibilityIdentifier("title.name")
            }
            let meta = ItemText.facts(item)
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
        }
    }

    /// Play, wide; beside it Favorite, Watched and « … » (From the beginning, the sleep timer, Watch together), round:
    /// one row of what a title does. At the largest text sizes, Play alone on its line.
    @ViewBuilder private var actions: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 8) { playButton.layoutPriority(1); TitleActions(item: $item); moreMenu }
            VStack(alignment: .leading, spacing: 12) { playButton; HStack(spacing: 8) { TitleActions(item: $item); moreMenu } }
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
        .onChange(of: playable?.id) {
            audioIndex = nil
            subtitleIndex = nil
        }
    }

    /// Play (Resume, « Play S1 · E3 » for a series); held in place, without its words, until Jellyfin answered.
    @ViewBuilder private var playButton: some View {
        if let playable {
            Button {
                start(playable, fromStart: false)
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: "play.fill")
                    Text(playLabel(playable, resume: ItemText.progress(playable) > 0)).font(MiraFont.headline).lineLimit(1).minimumScaleFactor(0.8)
                }
                .foregroundStyle(Palette.onAccent)
                .frame(maxWidth: .infinity, minHeight: 36)
            }
            .buttonStyle(.glassProminent)
            .accessibilityIdentifier("title.play")
        } else {
            Button {} label: {
                HStack(spacing: 8) {
                    Image(systemName: "play.fill")
                    Text("Play").font(MiraFont.headline)
                }
                .foregroundStyle(Palette.onAccent)
                .frame(maxWidth: .infinity, minHeight: 36)
            }
            .buttonStyle(.glassProminent)
            .disabled(true)
        }
    }

    /// « … »: what a title does less often, in one menu.
    private var moreMenu: some View {
        Menu {
            if let playable, ItemText.progress(playable) > 0 {
                Button {
                    start(playable, fromStart: true)
                } label: {
                    Label("From the beginning", systemImage: "backward.end.fill")
                }
                .accessibilityIdentifier("title.fromStart")
            }
            SleepPicker(episode: playable?.isEpisode == true)
            Button {
                showsTogether = true
            } label: {
                Label("Watch together", systemImage: "person.2.fill")
            }
            .accessibilityIdentifier("title.together")
        } label: {
            Image(systemName: "ellipsis").font(.body.weight(.bold)).foregroundStyle(Palette.ink).frame(width: side, height: side).contentShape(.circle)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular.interactive(), in: .circle)
        .accessibilityLabel(Text("More"))
        .accessibilityIdentifier("title.more")
    }

    /// The genres, each opening Movies or Shows with only that genre, as on Mira web: at the end, after what to watch.
    @ViewBuilder private var genres: some View {
        if let genres = item.genres, !genres.isEmpty, !item.isEpisode {
            ScrollView(.horizontal) {
                HStack(spacing: 8) {
                    ForEach(genres, id: \.self) { genre in
                        NavigationLink(value: GenreRoute(kind: item.isSeries ? .shows : .movies, genre: genre)) {
                            Text(verbatim: genre)
                                .font(MiraFont.footnote)
                                .foregroundStyle(Palette.ink)
                                .padding(.horizontal, 12)
                                .padding(.vertical, 7)
                                .background(Palette.raised, in: .capsule)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .scrollIndicators(.hidden)
            .padding(.top, 6)
            .accessibilityLabel(Text("Genres"))
        }
    }

    private func load() async {
        guard let client = app.client else { return }
        do {
            let fresh = try await client.item(route.item.id)
            if fresh.isSeries { next = try await client.nextUp(series: fresh.id) }
            item = fresh
            loaded = true
        } catch JellyfinError.unauthorized { app.sessionEnded() } catch {}
        // The groups open on Jellyfin: one to join shows under Play.
        if app.together.group == nil { await app.together.refresh() }
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

    /// A wide picture of the title (an episode's own, when its series has no backdrop); never a poster cut to 16:9.
    private var heroArt: Artwork? { Artwork.of(item, .backdrop) ?? (item.isEpisode ? Artwork.of(item, .wide) : nil) }

    private var hasBackdrop: Bool { heroArt != nil }

    /// The picture from the top of the screen: the bars over it, then 16:9 across the page (560 points at most).
    private var artworkHeight: CGFloat { hasBackdrop ? bars + min(width * 9 / 16, 560 - bars) : bars + 8 }

    /// The backdrop from the top of the screen, filling its height (its sides cut a little rather than a band of
    /// blur above it): darkened under the bars so that the back button reads, faded into the page at its bottom, where
    /// the title sits. Pulled down, it grows.
    @ViewBuilder private var artwork: some View {
        let height = artworkHeight
        if let client = app.client, let art = heroArt {
            let drawn = min(max(width, height * 16 / 9), 1920 / max(1, displayScale))
            RemoteImage(url: client.imageURL(art, width: drawn, scale: displayScale), blurHash: art.blurHash, width: drawn)
                .frame(width: max(width, height * 16 / 9), height: height)
                .frame(width: width, height: height)
                .overlay {
                    // Eased: no edge where the fade starts or ends.
                    LinearGradient(stops: [
                        .init(color: .black.opacity(0.45), location: 0),
                        .init(color: .black.opacity(0.18), location: 0.16),
                        .init(color: .clear, location: 0.32),
                        .init(color: .clear, location: 0.46),
                        .init(color: Palette.bg.opacity(0.22), location: 0.58),
                        .init(color: Palette.bg.opacity(0.55), location: 0.7),
                        .init(color: Palette.bg.opacity(0.82), location: 0.82),
                        .init(color: Palette.bg.opacity(0.96), location: 0.93),
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

/// The languages a title starts in, on one quiet line under Play (Apple's player cannot change a conversion's audio):
/// Audio and Subtitles, a menu each, the file's tracks by Mira's names; what Settings and the series' last episodes chose
/// until another is picked here, kept then for the next episodes. The sleep timer joins the line once set. The line
/// keeps its height while Jellyfin has not said the file's tracks.
struct TrackLine: View {
    let playable: Item
    let loaded: Bool
    @Binding var audioIndex: Int?
    @Binding var subtitleIndex: Int?
    @Environment(AppModel.self) private var app

    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 20) { menus; Spacer(minLength: 0); SleepStatus() }
            VStack(alignment: .leading, spacing: 10) { menus; SleepStatus() }
        }
        .frame(minHeight: 30)
    }

    @ViewBuilder private var menus: some View {
        if let source = playable.mediaSources?.first {
            // What will play: chosen here, else the series' last tracks or the languages of Settings.
            let preferences = app.preferences
            let auto = TrackChoice.choose(source, memory: preferences.tracks(for: playable.seriesId),
                                          audioOrder: preferences.audioOrder, subtitleOrder: preferences.subtitleOrder)
            TrackMenus(source: source, audioIndex: Binding(get: { audioIndex ?? auto.audio }, set: { index in
                audioIndex = index
                remember(source, audio: index, subtitle: subtitleIndex ?? auto.subtitle)
            }), subtitleIndex: Binding(get: { subtitleIndex ?? auto.subtitle }, set: { index in
                subtitleIndex = index
                remember(source, audio: audioIndex ?? auto.audio, subtitle: index)
            }))
        } else if !loaded {
            HStack(spacing: 20) {
                Capsule().fill(Palette.raised).frame(width: 96, height: 14)
                Capsule().fill(Palette.raised).frame(width: 120, height: 14)
            }
            .accessibilityHidden(true)
        }
    }

    /// Tracks chosen for an episode: the series' next episodes start with the same languages, on every device.
    private func remember(_ source: MediaSource, audio: Int?, subtitle: Int?) {
        guard let series = playable.seriesId else { return }
        app.preferences.remember(TrackChoice.memory(source, audio: audio, subtitle: subtitle), for: series)
    }
}

/// Audio and Subtitles: a menu each, read as a line of text with its symbol and a chevron, not two more buttons.
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
                TrackLabel(symbol: "speaker.wave.2.fill", text: current.flatMap { audioNames[$0] } ?? String(localized: "Audio"))
            }
            .buttonStyle(.plain)
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
                TrackLabel(symbol: "captions.bubble.fill", text: name ?? String(localized: "Subtitles off"))
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("Subtitles"))
            .accessibilityValue(Text(verbatim: name ?? String(localized: "Off")))
            .accessibilityIdentifier("title.subtitles")
        }
    }
}

private struct TrackLabel: View {
    let symbol: String
    let text: String

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: symbol).font(.footnote).foregroundStyle(Palette.muted)
            Text(verbatim: text).font(MiraFont.subheadline).foregroundStyle(Palette.ink).lineLimit(1)
            Image(systemName: "chevron.down").font(.caption2.weight(.bold)).foregroundStyle(Palette.muted)
        }
        .padding(.vertical, 6)
        .contentShape(.rect)
    }
}
