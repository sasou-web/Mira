import SwiftUI

/// A title's page: its artwork, what it is, how far it was watched, its summary, and Play. Until Mira for iPhone has
/// its own player, Play opens Mira web (served by Jellyfin's Mira extension) in Safari, at that title.
struct TitleView: View {
    let route: TitleRoute
    @Environment(AppModel.self) private var app
    @Environment(\.openURL) private var openURL
    @State private var item: Item
    @State private var width: CGFloat = 402

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
        .task(id: route.item.id) {
            guard let client = app.client else { return }
            do { item = try await client.item(route.item.id) } catch JellyfinError.unauthorized { app.sessionEnded() } catch {}
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

    /// Play (Resume) in Mira web for a film or an episode; a series opens its page there, to choose an episode.
    @ViewBuilder private var play: some View {
        if let client = app.client {
            let label: LocalizedStringKey = item.isSeries ? "Open in Mira web" : ItemText.progress(item) > 0 ? "Resume" : "Play"
            let route = item.isSeries ? "titre/\(item.id)" : "lecture/\(item.id)"
            if let url = ServerAddress.miraWeb(client.server, route: route) {
                VStack(alignment: .leading, spacing: 10) {
                    Button {
                        openURL(url)
                    } label: {
                        HStack(spacing: 8) {
                            Image(systemName: item.isSeries ? "safari.fill" : "play.fill")
                            Text(label).font(MiraFont.headline)
                        }
                        .foregroundStyle(Palette.onAccent)
                        .frame(maxWidth: .infinity, minHeight: 32)
                    }
                    .buttonStyle(.glassProminent)
                    .accessibilityIdentifier("title.play")
                    (item.isSeries ? Text("Mira web opens this show in Safari, to choose an episode.")
                        : Text("Plays in Mira web, in Safari, until Mira for iPhone has its own player."))
                        .font(MiraFont.footnote)
                        .foregroundStyle(Palette.muted)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: 420, alignment: .leading)
            }
        }
    }
}
