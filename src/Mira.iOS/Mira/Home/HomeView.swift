import SwiftUI
import UIKit

/// A title opened from a card: the item, and the card it came from (its picture zooms into the title page).
struct TitleRoute: Hashable {
    let item: Item
    let source: String
}

/// Home, as on Mira web, Windows and Mac: the banner, then Continue watching, Recently added and Your favorites.
struct HomeView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.scenePhase) private var scenePhase
    @State private var model = HomeModel()
    @State private var size = CGSize(width: 402, height: 874)
    /// On screen: Continue watching follows a title stopped at once; otherwise once Home shows again.
    @State private var shown = false
    @State private var stale = false
    @Namespace private var zoom

    var body: some View {
        let content = model.content
        let banner = content?.banner ?? []
        // Home runs under the status bar (the banner's picture reaches the top edge); a header without a banner, and
        // everything scrolled up, keeps clear of it, and a shade under the clock keeps the clock readable.
        let top = statusBar
        ScrollView {
            VStack(alignment: .leading, spacing: 30) {
                if let content, let client = app.client {
                    if banner.isEmpty {
                        Text("Home")
                            .font(MiraFont.largeTitle)
                            .foregroundStyle(Palette.ink)
                            .padding(.horizontal, 16)
                            .padding(.top, top + 12)
                            .accessibilityAddTraits(.isHeader)
                    } else {
                        BannerView(entries: banner, client: client, size: size, zoom: zoom)
                    }
                    if case let .failed(error) = model.state {
                        AwayLine(error: error) { Task { await load() } }
                    }
                    HomeGroup()
                    rows(content, client)
                    if content.isEmpty {
                        EmptyState(symbol: "film.stack", title: Text("Your library is empty"), text: Text("Movies and shows you add to Jellyfin will show up here."))
                    }
                } else if case let .failed(error) = model.state {
                    Text("Home")
                        .font(MiraFont.largeTitle)
                        .foregroundStyle(Palette.ink)
                        .padding(.horizontal, 16)
                        .padding(.top, top + 12)
                    EmptyState(symbol: "wifi.exclamationmark", title: Text("Jellyfin isn’t answering"), text: Text(Messages.text(error))) {
                        Button {
                            Task { await load() }
                        } label: {
                            Text("Try again").font(MiraFont.headline).padding(.horizontal, 8)
                        }
                        .buttonStyle(.glass)
                    }
                } else {
                    LoadingHome(size: size)
                }
            }
            .padding(.bottom, 40)
        }
        .scrollIndicators(.hidden)
        .ignoresSafeArea(edges: .top)
        .overlay(alignment: .top) {
            LinearGradient(colors: [Palette.bg.opacity(0.85), Palette.bg.opacity(0)], startPoint: .top, endPoint: .bottom)
                .frame(height: top + 12)
                .ignoresSafeArea(edges: .top)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
        .background(Palette.bg)
        .onGeometryChange(for: CGSize.self) { $0.size } action: { size = $0 }
        .refreshable { await load() }
        .toolbarVisibility(.hidden, for: .navigationBar)
        .navigationTitle("Home")
        .navigationDestination(for: TitleRoute.self) { route in
            TitleView(route: route)
                .navigationTransition(.zoom(sourceID: route.source, in: zoom))
        }
        .task {
            if let userId = app.session?.userId { model.start(userId: userId) }
            await load()
        }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, model.isStale { Task { await load() } }
        }
        // A title stopped or the next episode started: Continue watching moves.
        .onChange(of: app.playback) {
            if shown { Task { await load() } } else { stale = true }
        }
        .onAppear {
            shown = true
            if stale { stale = false; Task { await load() } }
        }
        .onDisappear { shown = false }
        .accessibilityIdentifier("home")
    }

    /// The status bar's height (0 when hidden, in landscape), read again whenever Home's size changes.
    private var statusBar: CGFloat {
        _ = size
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        return scene?.keyWindow?.safeAreaInsets.top ?? 0
    }

    private func load() async {
        guard let client = app.client else { return }
        await model.load(client) { app.sessionEnded() }
        await app.together.refresh()
    }

    @ViewBuilder private func rows(_ content: HomeContent, _ client: JellyfinClient) -> some View {
        let continuing = content.continueWatching
        if !continuing.isEmpty {
            CardRow(title: "Continue watching", items: continuing) { item in
                NavigationLink(value: TitleRoute(item: item, source: "continue-\(item.id)")) {
                    WideCard(item: item, client: client, width: wideWidth)
                        .matchedTransitionSource(id: "continue-\(item.id)", in: zoom)
                }
                .buttonStyle(.plain)
                .titleMenu(item)
            }
        }
        if !content.latest.isEmpty {
            CardRow(title: "Recently added", items: content.latest) { item in
                NavigationLink(value: TitleRoute(item: item, source: "latest-\(item.id)")) {
                    PosterCard(item: item, client: client, width: posterWidth)
                        .matchedTransitionSource(id: "latest-\(item.id)", in: zoom)
                }
                .buttonStyle(.plain)
                .titleMenu(item)
            }
        }
        if !content.favorites.isEmpty {
            CardRow(title: "Your favorites", items: content.favorites) { item in
                NavigationLink(value: TitleRoute(item: item, source: "favorite-\(item.id)")) {
                    PosterCard(item: item, client: client, width: posterWidth)
                        .matchedTransitionSource(id: "favorite-\(item.id)", in: zoom)
                }
                .buttonStyle(.plain)
                .titleMenu(item)
            }
        }
    }

    /// Mira web's sizes: --wide-w (clamp(244px, 74vw, 360px)) and --poster-w (clamp(104px, 29vw, 156px)).
    private var wideWidth: CGFloat { min(360, max(244, size.width * 0.74)) }
    private var posterWidth: CGFloat { min(156, max(104, size.width * 0.29)) }
}

/// A row of cards that scrolls sideways, stopping on a card.
struct CardRow<Card: View>: View {
    let title: LocalizedStringKey
    let items: [Item]
    @ViewBuilder let card: (Item) -> Card

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title)
                .font(MiraFont.section)
                .foregroundStyle(Palette.ink)
                .padding(.horizontal, 16)
                .accessibilityAddTraits(.isHeader)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: 12) {
                    ForEach(items) { item in card(item) }
                }
                .scrollTargetLayout()
            }
            .scrollIndicators(.hidden)
            .contentMargins(.horizontal, 16, for: .scrollContent)
            .scrollTargetBehavior(.viewAligned)
        }
    }
}

/// A card of Continue watching: the 16:9 picture with how far it was watched, the title, the episode or time left.
struct WideCard: View {
    let item: Item
    let client: JellyfinClient
    let width: CGFloat
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        let art = Artwork.of(item, .wide)
        let title = item.isEpisode ? (item.seriesName ?? item.name ?? "") : (item.name ?? "")
        let detail = item.isEpisode ? [ItemText.episodeCode(item), item.name ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
            : (ItemText.remaining(item) ?? ItemText.meta(item))
        VStack(alignment: .leading, spacing: 8) {
            RemoteImage(url: art.map { client.imageURL($0, width: width, scale: displayScale) }, blurHash: art?.blurHash, label: title, width: width)
                .frame(width: width, height: width * 9 / 16)
                .overlay(alignment: .bottom) {
                    let progress = ItemText.progress(item)
                    if progress > 0 { ProgressLine(value: progress).padding(10) }
                }
                .clipShape(.rect(cornerRadius: 12, style: .continuous))
                .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.white.opacity(0.06)) }
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(MiraFont.headline).foregroundStyle(Palette.ink).lineLimit(1)
                Text(detail).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
            }
        }
        .frame(width: width, alignment: .leading)
        .contentShape(.rect)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: [title, detail].filter { !$0.isEmpty }.joined(separator: ", ")))
        .accessibilityAddTraits(.isButton)
    }
}

/// A 2:3 poster, its title and its year under it; a mark when it was watched.
struct PosterCard: View {
    let item: Item
    let client: JellyfinClient
    let width: CGFloat
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        let art = Artwork.of(item, .poster)
        let name = item.name ?? ""
        VStack(alignment: .leading, spacing: 8) {
            RemoteImage(url: art.map { client.imageURL($0, width: width, scale: displayScale) }, blurHash: art?.blurHash, label: name, width: width)
                .frame(width: width, height: width * 3 / 2)
                .overlay(alignment: .topTrailing) {
                    if item.userData?.played == true {
                        Image(systemName: "checkmark")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(Palette.onAccent)
                            .frame(width: 24, height: 24)
                            .background(Palette.accent, in: .circle)
                            .padding(7)
                            .accessibilityHidden(true)
                    }
                }
                .overlay(alignment: .bottom) {
                    let progress = ItemText.progress(item)
                    if progress > 0 { ProgressLine(value: progress).padding(8) }
                }
                .clipShape(.rect(cornerRadius: 12, style: .continuous))
                .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.white.opacity(0.06)) }
            VStack(alignment: .leading, spacing: 2) {
                Text(name).font(MiraFont.subheadline).foregroundStyle(Palette.ink).lineLimit(2)
                if let year = item.productionYear {
                    Text(verbatim: String(year)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
            }
        }
        .frame(width: width, alignment: .leading)
        .contentShape(.rect)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: [name, item.productionYear.map(String.init) ?? ""].filter { !$0.isEmpty }.joined(separator: ", ")))
        .accessibilityValue(item.userData?.played == true ? Text("Watched") : Text(verbatim: ""))
        .accessibilityAddTraits(.isButton)
    }
}

/// How far a title was watched: a thin light bar on a dark track.
struct ProgressLine: View {
    let value: Double
    var body: some View {
        Capsule()
            .fill(.black.opacity(0.55))
            .frame(height: 4)
            .overlay(alignment: .leading) {
                GeometryReader { geometry in
                    Capsule().fill(Palette.accent).frame(width: max(4, geometry.size.width * value))
                }
            }
            .accessibilityHidden(true)
    }
}

/// The banner: wide artwork of what to watch now, one at a time, swiped sideways.
struct BannerView: View {
    let entries: [BannerEntry]
    let client: JellyfinClient
    let size: CGSize
    let zoom: Namespace.ID
    @Environment(AppModel.self) private var app
    @Environment(\.displayScale) private var displayScale
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.accessibilityVoiceOverEnabled) private var voiceOver
    @Environment(\.scenePhase) private var scenePhase
    @State private var current: String?
    /// On screen (not under a title's page): only then does the banner turn by itself.
    @State private var shown = false

    var body: some View {
        let height = min(size.width * 1.2, max(420, size.height * 0.62))
        ScrollView(.horizontal) {
            LazyHStack(spacing: 0) {
                ForEach(entries) { entry in
                    slide(entry, height: height)
                        .containerRelativeFrame(.horizontal)
                        .id(entry.id)
                }
            }
            .scrollTargetLayout()
        }
        .scrollIndicators(.hidden)
        .scrollTargetBehavior(.paging)
        .scrollPosition(id: $current)
        .scrollClipDisabled()
        .frame(height: height)
        // Where the banner is: on the picture's fade, under the buttons, as on Mira web.
        .overlay(alignment: .bottom) {
            if entries.count > 1 {
                HStack(spacing: 6) {
                    ForEach(entries) { entry in
                        Capsule()
                            .fill(entry.id == (current ?? entries.first?.id) ? Palette.accent : Palette.faint.opacity(0.5))
                            .frame(width: entry.id == (current ?? entries.first?.id) ? 18 : 6, height: 6)
                    }
                }
                .animation(.snappy, value: current)
                .padding(.bottom, 10)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(Text("Featured"))
        .onAppear { shown = true }
        .onDisappear { shown = false }
        // As on Mira web: the next one every 9 s, counted again after a swipe; never with Reduce Motion or VoiceOver.
        .task(id: "\(current ?? "")-\(shown)-\(scenePhase == .active)") {
            guard shown, scenePhase == .active, !reduceMotion, !voiceOver, entries.count > 1 else { return }
            try? await Task.sleep(for: .seconds(9))
            guard !Task.isCancelled else { return }
            let index = entries.firstIndex { $0.id == (current ?? entries.first?.id) } ?? 0
            withAnimation(.smooth(duration: 0.7)) { current = entries[(index + 1) % entries.count].id }
        }
    }

    private func slide(_ entry: BannerEntry, height: CGFloat) -> some View {
        let item = entry.item
        let art = Artwork.of(item, .backdrop)
        let logo = Artwork.of(item, .logo)
        let width = size.width
        // A 16:9 picture filling a taller slide is drawn wider than the screen: asked at that width, 1920 pixels at
        // most (Jellyfin's backdrops are 1920 or 3840 wide; beyond, three times the weight for nothing seen).
        let drawn = min(max(width * 1.4, height * 16 / 9), 1920 / max(1, displayScale))
        return ZStack(alignment: .bottomLeading) {
            RemoteImage(url: art.map { client.imageURL($0, width: drawn, scale: displayScale) }, blurHash: art?.blurHash, width: drawn)
                .frame(width: width * 1.4, height: height)
                // Swiped sideways, the picture moves a little slower than its slide (it is wider than the slide, so
                // no edge ever shows).
                .scrollTransition(axis: .horizontal) { content, phase in
                    content.offset(x: phase.value * -width * 0.2)
                }
                .frame(width: width, height: height)
                .overlay {
                    LinearGradient(stops: [
                        .init(color: .black.opacity(0.3), location: 0),
                        .init(color: .clear, location: 0.14),
                        .init(color: .clear, location: 0.4),
                        .init(color: Palette.bg.opacity(0.7), location: 0.7),
                        .init(color: Palette.bg.opacity(0.94), location: 0.9),
                        .init(color: Palette.bg, location: 1),
                    ], startPoint: .top, endPoint: .bottom)
                    .allowsHitTesting(false)
                }
                // Home pulled down to refresh: the picture fills what opens above it.
                .stretchesWhenPulled(height: height)
                .matchedTransitionSource(id: "banner-\(item.id)", in: zoom)
            VStack(alignment: .leading, spacing: 10) {
                if let logo {
                    LogoImage(url: client.imageURL(logo, width: 300, scale: displayScale), name: item.name ?? "")
                        .frame(maxWidth: min(300, width * 0.7), maxHeight: 96, alignment: .bottomLeading)
                } else {
                    Text(item.name ?? "").font(MiraFont.extraBold(34, .largeTitle)).foregroundStyle(Palette.ink).lineLimit(2)
                }
                Text(ItemText.meta(item)).font(MiraFont.subheadline).foregroundStyle(Palette.muted).lineLimit(1)
                    .shadow(color: .black.opacity(0.6), radius: 6)
                ChipRow {
                    if let play = entry.play {
                        Button {
                            app.play(play)
                        } label: {
                            Label {
                                Text(playLabel(entry)).font(MiraFont.headline).lineLimit(1)
                            } icon: {
                                Image(systemName: "play.fill")
                            }
                            .foregroundStyle(Palette.onAccent)
                            .padding(.horizontal, 6)
                        }
                        .buttonStyle(.glassProminent)
                    }
                    NavigationLink(value: TitleRoute(item: item, source: "banner-\(item.id)")) {
                        if entry.play == nil {
                            Label("More info", systemImage: "info.circle").font(MiraFont.headline).padding(.horizontal, 6)
                        } else {
                            Image(systemName: "info").font(.headline).frame(width: 22, height: 22)
                                .accessibilityLabel(Text("More info about \(item.name ?? "")"))
                        }
                    }
                    .buttonStyle(.glass)
                }
                .padding(.top, 4)
            }
            .padding(.horizontal, 20)
            .padding(.bottom, entries.count > 1 ? 32 : 18)
        }
        .frame(width: width, height: height)
        // Cut at the sides and the bottom only: pulled down, the picture grows above the slide.
        .mask(alignment: .bottom) { Rectangle().frame(width: width, height: height + 2000) }
    }

    private func playLabel(_ entry: BannerEntry) -> String {
        guard let play = entry.play else { return "" }
        let resume = ItemText.progress(play) > 0
        if play.isEpisode {
            let code = ItemText.episodeCode(play)
            return resume ? String(localized: "Resume \(code)") : String(localized: "Watch \(code)")
        }
        return resume ? String(localized: "Resume") : String(localized: "Play")
    }
}

/// A title's logo, drawn whole; its name while it loads or when it does not.
struct LogoImage: View {
    let url: URL
    let name: String
    @State private var image: UIImage?
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        Group {
            if let image {
                Image(uiImage: image).resizable().scaledToFit().accessibilityLabel(Text(verbatim: name))
            } else {
                Text(verbatim: name).font(MiraFont.extraBold(34, .largeTitle)).foregroundStyle(Palette.ink).lineLimit(2)
            }
        }
        .task(id: url) { image = await ImagePipeline.shared.image(url, pixels: 300 * displayScale) }
    }
}

/// « Can't reach Jellyfin »: over a Home kept from before, with a way to ask again.
struct AwayLine: View {
    let error: JellyfinError
    let retry: () -> Void
    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: "wifi.exclamationmark").foregroundStyle(Palette.warning).accessibilityHidden(true)
            Text(Messages.text(error)).font(MiraFont.footnote).foregroundStyle(Palette.ink).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button("Retry", action: retry).font(MiraFont.footnote).buttonStyle(.glass)
        }
        .padding(14)
        .background(Palette.surface, in: .rect(cornerRadius: 14, style: .continuous))
        .padding(.horizontal, 16)
    }
}

/// An empty or failed screen: a symbol, what happened, what to do.
struct EmptyState<Actions: View>: View {
    let symbol: String
    let title: Text
    let text: Text
    @ViewBuilder var actions: () -> Actions

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: symbol).font(.system(size: 40, weight: .regular)).foregroundStyle(Palette.muted).padding(.bottom, 4)
                .accessibilityHidden(true)
            title.font(MiraFont.headline).foregroundStyle(Palette.ink).multilineTextAlignment(.center)
            text.font(MiraFont.subheadline).foregroundStyle(Palette.muted).multilineTextAlignment(.center)
            actions().padding(.top, 8)
        }
        .frame(maxWidth: 360)
        .padding(.vertical, 48)
        .padding(.horizontal, 24)
        .frame(maxWidth: .infinity)
    }
}

extension EmptyState where Actions == EmptyView {
    init(symbol: String, title: Text, text: Text) {
        self.init(symbol: symbol, title: title, text: text) { EmptyView() }
    }
}

/// Home's shape while it loads for the first time: the banner and two rows, in grey.
struct LoadingHome: View {
    let size: CGSize
    var body: some View {
        VStack(alignment: .leading, spacing: 30) {
            Rectangle().fill(Palette.surface).frame(height: min(size.width * 1.2, max(420, size.height * 0.62)) * 0.6)
            ForEach(0..<2, id: \.self) { row in
                VStack(alignment: .leading, spacing: 12) {
                    RoundedRectangle(cornerRadius: 6).fill(Palette.raised).frame(width: 180, height: 20).padding(.horizontal, 16)
                    HStack(spacing: 12) {
                        ForEach(0..<4, id: \.self) { _ in
                            RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Palette.raised)
                                .frame(width: row == 0 ? 260 : 120, height: row == 0 ? 146 : 180)
                        }
                    }
                    .padding(.horizontal, 16)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .clipped()
        .accessibilityElement()
        .accessibilityLabel(Text("Loading"))
    }
}

/// A group of « Watch together » open on Jellyfin, under the banner while this iPhone is in none (Together.HomeGroup of
/// Windows and Mac): joined with a touch, its title then opens when the group puts one on.
struct HomeGroup: View {
    @Environment(AppModel.self) private var app
    @State private var working = false
    @State private var failure: String?

    var body: some View {
        let together = app.together
        if together.group == nil, let group = together.open.first {
            HStack(spacing: 12) {
                Image(systemName: "person.2.fill").font(.title3).foregroundStyle(Palette.ink).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Watch together").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    Text(verbatim: SyncRules.groupText(group)).font(MiraFont.headline).foregroundStyle(Palette.ink).lineLimit(2)
                    if let failure { Text(verbatim: failure).font(MiraFont.footnote).foregroundStyle(Palette.danger) }
                }
                Spacer(minLength: 8)
                Button {
                    working = true
                    failure = nil
                    Task {
                        defer { working = false }
                        // Ended meanwhile: said, rather than a group made again by mistake.
                        await together.refresh()
                        guard together.open.contains(where: { $0.groupId == group.groupId }) else {
                            failure = String(localized: "This group no longer exists.")
                            return
                        }
                        do { try await together.join(group.groupId) } catch { failure = Messages.text(error) }
                    }
                } label: {
                    Text("Join").font(MiraFont.callout).foregroundStyle(Palette.onAccent).padding(.horizontal, 4)
                }
                .buttonStyle(.glassProminent)
                .disabled(working)
                .accessibilityIdentifier("home.joinGroup")
            }
            .padding(14)
            .background(Palette.raised, in: .rect(cornerRadius: 18))
            .padding(.horizontal, 16)
            .accessibilityElement(children: .contain)
        }
    }
}
