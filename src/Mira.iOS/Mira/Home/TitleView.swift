import SwiftUI

/// A title's page: its artwork, what it is, how far it was watched, its summary, Play in Apple's player, and the
/// languages to play it in (Apple's player cannot change a conversion's audio, as on Mira web). A series plays its
/// next episode and lists its seasons and episodes.
struct TitleView: View {
    let route: TitleRoute
    @Environment(AppModel.self) private var app
    @State private var item: Item
    @State private var width: CGFloat = 402
    /// What Play plays: the title, or a series' next episode.
    @State private var next: Item?
    @State private var audioIndex: Int?
    @State private var subtitleIndex: Int?

    init(route: TitleRoute) {
        self.route = route
        _item = State(initialValue: route.item)
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                artwork
                VStack(alignment: .leading, spacing: 14) {
                    if item.isEpisode, let series = item.seriesName {
                        Text(series).font(MiraFont.headline).foregroundStyle(Palette.muted)
                    }
                    Text(heading)
                        .font(MiraFont.title)
                        .foregroundStyle(Palette.ink)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityAddTraits(.isHeader)
                        .accessibilityIdentifier("title.name")
                    let meta = ItemText.meta(item)
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
                    if let overview = item.overview, !overview.isEmpty {
                        Text(overview)
                            .font(MiraFont.body)
                            .foregroundStyle(Palette.ink)
                            .lineSpacing(3)
                            .fixedSize(horizontal: false, vertical: true)
                            .padding(.top, 10)
                    }
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
        .background(Palette.bg)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .navigationTitle(item.name ?? "")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: "\(route.item.id)-\(app.playback)") {
            guard let client = app.client else { return }
            do {
                item = try await client.item(route.item.id)
                if item.isSeries { next = try await client.nextUp(series: item.id) }
            } catch JellyfinError.unauthorized { app.sessionEnded() } catch {}
        }
    }

    private var heading: String {
        guard item.isEpisode else { return item.name ?? "" }
        return [ItemText.episodeCode(item), item.name ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
    }

    /// The backdrop, under the status bar, fading into the page.
    @ViewBuilder private var artwork: some View {
        let height = min(width * 9 / 16 + 60, 520)
        if let client = app.client, let art = Artwork.of(item, .backdrop) ?? Artwork.of(item, .wide) {
            RemoteImage(url: client.imageURL(art, width: width), blurHash: art.blurHash, width: width)
                .frame(width: width, height: height)
                .overlay {
                    LinearGradient(stops: [
                        .init(color: .black.opacity(0.45), location: 0),
                        .init(color: .clear, location: 0.3),
                        .init(color: Palette.bg.opacity(0.6), location: 0.75),
                        .init(color: Palette.bg, location: 1),
                    ], startPoint: .top, endPoint: .bottom)
                }
                .accessibilityHidden(true)
        } else {
            Color.clear.frame(height: 110)
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
                    app.play(playable, audioIndex: audioIndex, subtitleIndex: subtitleIndex)
                } label: {
                    HStack(spacing: 8) {
                        Image(systemName: "play.fill")
                        Text(playLabel(playable, resume: resume)).font(MiraFont.headline)
                    }
                    .foregroundStyle(Palette.onAccent)
                    .frame(maxWidth: .infinity, minHeight: 32)
                }
                .buttonStyle(.glassProminent)
                .accessibilityIdentifier("title.play")
                if resume {
                    Button {
                        app.play(playable, fromStart: true, audioIndex: audioIndex, subtitleIndex: subtitleIndex)
                    } label: {
                        Label("From the beginning", systemImage: "backward.end.fill").font(MiraFont.callout)
                    }
                    .buttonStyle(.glass)
                    .accessibilityIdentifier("title.fromStart")
                }
                if let source = playable.mediaSources?.first {
                    HStack(spacing: 10) {
                        TrackMenus(source: source, audioIndex: $audioIndex, subtitleIndex: $subtitleIndex)
                    }
                }
            }
        }
        .frame(maxWidth: 420, alignment: .leading)
        .onChange(of: playable?.id) {
            audioIndex = nil
            subtitleIndex = nil
        }
    }

    private func playLabel(_ playable: Item, resume: Bool) -> String {
        if item.isSeries {
            let code = ItemText.episodeCode(playable)
            return resume ? String(localized: "Resume \(code)") : String(localized: "Watch \(code)")
        }
        return resume ? String(localized: "Resume") : String(localized: "Play")
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
