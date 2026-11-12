import SwiftUI
import UIKit

/// « Keep on this iPhone » of a film or an episode: the same round button whatever it shows, only its glyph changes
/// (an arrow, a ring that fills while it comes, the arrow filled once here, a mark when it stopped). A touch keeps it,
/// or offers what can be done with it then (stop, remove, try again).
struct KeepButton: View {
    let item: Item
    /// In an episode's row: smaller, without glass.
    var compact = false
    @Environment(AppModel.self) private var app
    @State private var asking = false
    @ScaledMetric(relativeTo: .body) private var side: CGFloat = 48
    @ScaledMetric(relativeTo: .body) private var small: CGFloat = 36

    var body: some View {
        let state = app.keeper.state(item.id)
        Button {
            if state == .notKept { app.keeper.keep([item]) } else { asking = true }
        } label: {
            KeepGlyph(state: state)
                .font(compact ? .body.weight(.semibold) : .title3.weight(.semibold))
                .foregroundStyle(compact ? Palette.muted : Palette.ink)
                .frame(width: compact ? small : side, height: compact ? small : side)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .modifier(KeepGlass(on: !compact))
        .accessibilityLabel(Text(Self.label(state)))
        .accessibilityValue(Text(Self.value(state)))
        .accessibilityIdentifier(compact ? "episode.keep.\(item.indexNumber ?? 0)" : "title.keep")
        .sensoryFeedback(.selection, trigger: state == .notKept)
        .confirmationDialog(Text(Self.label(state)), isPresented: $asking, titleVisibility: .visible) {
            switch state {
            case .failed:
                Button("Try again") { app.keeper.retry(item.id) }
                Button("Remove from this iPhone", role: .destructive) { app.keeper.remove(item.id) }
            case .kept:
                Button("Remove from this iPhone", role: .destructive) { app.keeper.remove(item.id) }
                    .accessibilityIdentifier("keep.remove")
            default:
                Button("Stop keeping it", role: .destructive) { app.keeper.remove(item.id) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(verbatim: Self.message(state, app.keeper))
        }
    }

    static func label(_ state: Keeper.State) -> String {
        switch state {
        case .notKept: String(localized: "Keep on this iPhone")
        case .waiting, .fetching: String(localized: "Keeping on this iPhone")
        case .kept: String(localized: "On this iPhone")
        case .failed: String(localized: "Couldn’t keep it")
        }
    }

    static func value(_ state: Keeper.State) -> String {
        switch state {
        case let .fetching(done): done.formatted(.percent.precision(.fractionLength(0)))
        case .waiting: String(localized: "Waiting")
        default: ""
        }
    }

    static func message(_ state: Keeper.State, _ keeper: Keeper) -> String {
        switch state {
        case let .failed(text): text
        case .kept: String(localized: "It plays here without a connection. Removed, it leaves this iPhone only: it stays on Jellyfin.")
        case .waiting where keeper.waitingForWiFi: String(localized: "On mobile data, titles wait for Wi-Fi: they’re whole movies, several gigabytes. It goes on by itself on Wi-Fi.")
        case .waiting where keeper.waiting: String(localized: "Mira can’t reach Jellyfin: it goes on as soon as it can.")
        default: String(localized: "It goes on in the background as long as iOS lets it, otherwise when you open Mira again.")
        }
    }
}

private struct KeepGlass: ViewModifier {
    let on: Bool
    @ViewBuilder func body(content: Content) -> some View {
        if on { content.glassEffect(.regular.interactive(), in: .circle) } else { content }
    }
}

/// The glyph of a state: an arrow, a ring around it while it comes, filled once here.
struct KeepGlyph: View {
    let state: Keeper.State

    var body: some View {
        switch state {
        case .notKept:
            Image(systemName: "arrow.down.circle")
        case .kept:
            Image(systemName: "arrow.down.circle.fill").transition(.scale.combined(with: .opacity))
        case .failed:
            Image(systemName: "exclamationmark.circle")
        case .waiting:
            ring(0)
        case let .fetching(done):
            ring(done)
        }
    }

    private func ring(_ done: Double) -> some View {
        Image(systemName: "circle")
            .hidden()
            .overlay {
                GeometryReader { geometry in
                    let line = max(2, geometry.size.width * 0.09)
                    ZStack {
                        Circle().stroke(.secondary.opacity(0.35), lineWidth: line)
                        Circle()
                            .trim(from: 0, to: max(0.02, done))
                            .stroke(style: StrokeStyle(lineWidth: line, lineCap: .round))
                            .rotationEffect(.degrees(-90))
                            .animation(.easeOut(duration: 0.3), value: done)
                        Image(systemName: done == 0 ? "ellipsis" : "arrow.down")
                            .font(.system(size: geometry.size.width * 0.42, weight: .bold))
                    }
                    .padding(line / 2)
                }
            }
    }
}

/// The picture kept with a copy, read from this iPhone; the app's grey without one.
struct KeptPicture: View {
    let copy: KeptCopy
    let width: CGFloat
    @Environment(AppModel.self) private var app
    @Environment(\.displayScale) private var displayScale
    @State private var image: UIImage?

    var body: some View {
        ZStack {
            Palette.raised
            if let image {
                Image(uiImage: image).resizable().scaledToFill()
            } else {
                Image(systemName: copy.item.isEpisode ? "tv" : "film").font(.title3).foregroundStyle(Palette.muted)
            }
        }
        .frame(width: width, height: width * 9 / 16)
        .clipShape(.rect(cornerRadius: 10, style: .continuous))
        .accessibilityHidden(true)
        .task(id: copy.hasPicture) {
            guard let url = app.keeper.pictureURL(copy) else { return }
            let pixels = width * displayScale
            image = await Task.detached(priority: .utility) {
                UIImage(contentsOfFile: url.path)?.preparingThumbnail(of: CGSize(width: pixels, height: pixels * 9 / 16))
            }.value
        }
    }
}

/// « On this iPhone » (Settings): every title kept here, a series' episodes together; a touch plays one, a swipe takes
/// it off this iPhone.
struct KeptView: View {
    @Environment(AppModel.self) private var app
    @State private var asking: KeptCopy?

    var body: some View {
        let keeper = app.keeper
        let copies = KeptRules.ordered(keeper.copies)
        List {
            if copies.isEmpty {
                Section {
                    EmptyState(symbol: "arrow.down.circle", title: Text("Nothing on this iPhone yet"),
                               text: Text("On a movie or an episode, touch Keep on this iPhone: it plays here without a connection, even far from Jellyfin."))
                }
                .listRowBackground(Color.clear)
            } else {
                Section {
                    ForEach(copies) { copy in
                        Button {
                            if copy.complete { app.play(copy.item) } else { asking = copy }
                        } label: {
                            KeptRow(copy: copy, state: keeper.state(copy.id), waiting: keeper.waiting, waitingForWiFi: keeper.waitingForWiFi)
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("kept.row")
                        .swipeActions {
                            Button(role: .destructive) {
                                keeper.remove(copy.id)
                            } label: {
                                Label("Remove", systemImage: "trash")
                            }
                        }
                    }
                } footer: {
                    Text("\(Keeper.size(keeper.used)) used by Mira · \(Keeper.size(KeptStore.freeSpace() ?? 0)) free on this iPhone. Mira always leaves \(Keeper.size(KeptRules.reserve)) free. Signing out removes these titles from this iPhone.")
                }
                .listRowBackground(Palette.surface)
            }
            Section {
                Toggle(isOn: Binding(get: { app.preferences.keepOnMobileData }, set: {
                    app.preferences.keepOnMobileData = $0
                    keeper.connectionChanged()
                })) {
                    SettingsRow("Keep over mobile data", "Off, titles wait for Wi-Fi: a movie is several gigabytes.")
                }
                .accessibilityIdentifier("kept.mobileData")
            }
            .listRowBackground(Palette.surface)
        }
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .foldsTabBar()
        .background(Palette.bg)
        .navigationTitle("On this iPhone")
        .navigationBarTitleDisplayMode(.inline)
        .accessibilityIdentifier("kept")
        .confirmationDialog(Text(asking.map { KeepButton.label(keeper.state($0.id)) } ?? ""), isPresented: Binding(get: { asking != nil }, set: { if !$0 { asking = nil } }),
                            titleVisibility: .visible, presenting: asking) { copy in
            if copy.failure != nil {
                Button("Try again") { keeper.retry(copy.id) }
                Button("Remove from this iPhone", role: .destructive) { keeper.remove(copy.id) }
            } else {
                Button("Stop keeping it", role: .destructive) { keeper.remove(copy.id) }
            }
            Button("Cancel", role: .cancel) {}
        } message: { copy in
            Text(verbatim: KeepButton.message(keeper.state(copy.id), keeper))
        }
    }
}

/// A title kept here: its picture, its name (a series' episode under the series' name), and how far it came.
struct KeptRow: View {
    let copy: KeptCopy
    let state: Keeper.State
    let waiting: Bool
    var waitingForWiFi = false

    var body: some View {
        HStack(spacing: 12) {
            KeptPicture(copy: copy, width: 112)
            VStack(alignment: .leading, spacing: 3) {
                Text(verbatim: KeptRow.title(copy.item)).font(MiraFont.headline).foregroundStyle(Palette.ink).lineLimit(1)
                if let detail = KeptRow.detail(copy.item) {
                    Text(verbatim: detail).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
                }
                status
            }
            Spacer(minLength: 0)
            KeepGlyph(state: state).font(.body.weight(.semibold)).foregroundStyle(Palette.muted).frame(width: 26, height: 26)
                .accessibilityHidden(true)
        }
        .padding(.vertical, 4)
        .contentShape(.rect)
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder private var status: some View {
        switch state {
        case let .failed(text):
            Text(verbatim: text).font(MiraFont.footnote).foregroundStyle(Palette.danger).lineLimit(3)
        case let .fetching(done):
            Text("Keeping… \(done.formatted(.percent.precision(.fractionLength(0))))").font(MiraFont.footnote).foregroundStyle(Palette.muted)
        case .waiting:
            Group {
                if waitingForWiFi { Text("Waiting for Wi-Fi") } else if waiting { Text("Waiting for Jellyfin") } else { Text("Waiting its turn") }
            }
            .font(MiraFont.footnote).foregroundStyle(Palette.muted)
        default:
            Text(verbatim: Keeper.size(copy.totalSize)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
        }
    }

    static func title(_ item: Item) -> String { item.isEpisode ? (item.seriesName ?? item.name ?? "") : (item.name ?? "") }

    static func detail(_ item: Item) -> String? {
        guard item.isEpisode else { return item.productionYear.map(String.init) }
        let text = [ItemText.episodeCode(item), item.name ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
        return text.isEmpty ? nil : text
    }
}

/// On Home while Jellyfin can't be reached: what is kept here, to play at once.
struct KeptShelf: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        let kept = KeptRules.ordered(app.keeper.kept)
        if !kept.isEmpty, app.party.guest == nil {
            VStack(alignment: .leading, spacing: 12) {
                Text("On this iPhone")
                    .font(MiraFont.section)
                    .foregroundStyle(Palette.ink)
                    .padding(.horizontal, 16)
                    .accessibilityAddTraits(.isHeader)
                ScrollView(.horizontal) {
                    LazyHStack(alignment: .top, spacing: 12) {
                        ForEach(kept) { copy in
                            Button {
                                app.play(copy.item)
                            } label: {
                                VStack(alignment: .leading, spacing: 8) {
                                    KeptPicture(copy: copy, width: 220)
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(verbatim: KeptRow.title(copy.item)).font(MiraFont.headline).foregroundStyle(Palette.ink).lineLimit(1)
                                        if let detail = KeptRow.detail(copy.item) {
                                            Text(verbatim: detail).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
                                        }
                                    }
                                }
                                .frame(width: 220, alignment: .leading)
                                .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            .accessibilityElement(children: .combine)
                            .accessibilityHint(Text("Plays it from this iPhone"))
                            .accessibilityIdentifier("home.kept")
                        }
                    }
                    .scrollTargetLayout()
                }
                .scrollIndicators(.hidden)
                .contentMargins(.horizontal, 16, for: .scrollContent)
                .scrollTargetBehavior(.viewAligned)
            }
        }
    }
}

/// « Keep the season on this iPhone », under a series' episodes: those not kept yet, in their order.
struct KeepSeasonButton: View {
    let episodes: [Item]
    @Environment(AppModel.self) private var app

    var body: some View {
        let keeper = app.keeper
        let left = episodes.filter { keeper.state($0.id) == .notKept }
        if app.party.guest == nil, !episodes.isEmpty {
            Button {
                keeper.keep(left)
            } label: {
                Group {
                    if left.isEmpty {
                        Label("The season is on this iPhone", systemImage: "arrow.down.circle.fill")
                    } else {
                        Label("Keep the season on this iPhone", systemImage: "arrow.down.circle")
                    }
                }
                .font(MiraFont.callout)
                .foregroundStyle(Palette.ink)
            }
            .buttonStyle(.plain)
            .disabled(left.isEmpty)
            .accessibilityIdentifier("series.keepSeason")
        }
    }
}
