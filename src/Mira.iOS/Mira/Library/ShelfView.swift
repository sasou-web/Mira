import SwiftUI

/// A collection grouped in the library, opened (« L'étagère », as Windows and Mac: CollectionShelf of Core).
struct ShelfRoute: Hashable {
    let box: Item
    let source: String
}

/// A collection's page, as on Windows and Mac: its picture, name and facts, « Resume » across the whole collection,
/// Favorite, Watched and « … », a bar with one notch per title, then its titles numbered in their watch order (or by
/// release date), « UP NEXT » on the one it resumes at; each one opens its own page.
struct ShelfView: View {
    let route: ShelfRoute
    @Environment(AppModel.self) private var app
    @Environment(\.displayScale) private var displayScale
    @State private var box: Item
    @State private var titles: [Item]
    @State private var order: [String]?
    @State private var byRelease = false
    @State private var loading = true
    @State private var failure: Error?
    /// What « Resume » plays on a series: the episode it picks up at.
    @State private var target: Item?
    @State private var editing = false
    @State private var width: CGFloat = 402
    @State private var shown = false
    @State private var stale = false

    init(route: ShelfRoute) {
        self.route = route
        _box = State(initialValue: route.box)
        _titles = State(initialValue: route.box.shelf ?? [])
        _loading = State(initialValue: (route.box.shelf ?? []).isEmpty)
    }

    private func words(_ fr: String, _ en: String) -> String { CollectionShelf.words(fr, en) }
    private var ordered: [Item] { CollectionShelf.inOrder(titles, order) }
    private var upNext: Item? { CollectionShelf.upNext(ordered).map { ordered[$0] } }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                header
                if !titles.isEmpty {
                    actions.padding(.horizontal, 16)
                    if titles.count > 1 { steps.padding(.horizontal, 16) }
                    HStack {
                        Text(verbatim: CollectionShelf.titlesHeading(titles.count)).font(MiraFont.headline).foregroundStyle(Palette.ink)
                        Spacer()
                    }
                    .padding(.horizontal, 16)
                    .padding(.top, 6)
                    if titles.count > 1 {
                        Picker(selection: $byRelease) {
                            Text(verbatim: words("Ordre de visionnage", "Watch order")).tag(false)
                            Text(verbatim: words("Date de sortie", "Release date")).tag(true)
                        } label: {
                            Text(verbatim: words("Ordre", "Order"))
                        }
                        .pickerStyle(.segmented)
                        .padding(.horizontal, 16)
                        .accessibilityIdentifier("shelf.order")
                    }
                    grid
                } else if loading {
                    ProgressView().frame(maxWidth: .infinity).padding(.top, 60)
                } else if let failure {
                    EmptyState(symbol: "wifi.exclamationmark", title: Text(verbatim: words("Jellyfin ne répond pas", "Jellyfin isn’t answering")), text: Text(Messages.text(failure))) {
                        Button {
                            Task { await load() }
                        } label: { Text(verbatim: words("Réessayer", "Try again")).font(MiraFont.headline).padding(.horizontal, 8) }
                            .buttonStyle(.glass)
                    }
                } else {
                    EmptyState(symbol: "rectangle.stack", title: Text(verbatim: words("Rien dans cette collection", "Nothing in this collection yet")),
                               text: Text(verbatim: words("Ajoute des films ou des séries depuis leur menu : « Ajouter à une collection… ».", "Add movies or shows from their menu: “Add to a collection…”.")))
                }
            }
            .padding(.bottom, 40)
        }
        .scrollIndicators(.hidden)
        .ignoresSafeArea(edges: .top)
        .background(Palette.bg)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .navigationTitle(Text(verbatim: box.name ?? ""))
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackgroundVisibility(.hidden, for: .navigationBar)
        .toolbar {
            ToolbarItem(placement: .principal) { Color.clear.frame(width: 1, height: 1).accessibilityHidden(true) }
            ToolbarItem(placement: .topBarTrailing) { more }
        }
        .task { await load() }
        .refreshable { await load() }
        .onChange(of: app.playback) {
            ShelfGrouping.shared.forget()
            if shown { Task { await load() } } else { stale = true }
        }
        .onAppear {
            shown = true
            if stale { stale = false; Task { await load() } }
        }
        .onDisappear { shown = false }
        .sheet(isPresented: $editing) {
            ShelfOrderSheet(titles: ordered) { ids in Task { await save(order: ids) } }
        }
    }

    // MARK: The top

    private var header: some View {
        let picture = (upNext ?? ordered.first).flatMap { Artwork.of($0, .backdrop) } ?? Artwork.of(box, .backdrop)
        return ZStack(alignment: .bottomLeading) {
            RemoteImage(url: picture.map { app.client?.imageURL($0, width: width, scale: displayScale) } ?? nil, blurHash: picture?.blurHash, label: "", width: width)
                .frame(width: width, height: 300)
                .clipped()
                .overlay {
                    LinearGradient(colors: [Palette.bg.opacity(0.2), Palette.bg.opacity(0), Palette.bg.opacity(0.75), Palette.bg], startPoint: .top, endPoint: .bottom)
                }
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 6) {
                Text(verbatim: "COLLECTION").font(MiraFont.bold(12, .caption)).foregroundStyle(Palette.muted).tracking(1.2)
                Text(verbatim: box.name ?? "").font(MiraFont.title).foregroundStyle(Palette.ink).lineLimit(2)
                    .accessibilityAddTraits(.isHeader)
                if !titles.isEmpty {
                    Text(verbatim: CollectionShelf.facts(titles)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 4)
        }
        .frame(height: 300)
    }

    private var actions: some View {
        let favorite = box.userData?.isFavorite == true
        let watched = !titles.isEmpty && titles.allSatisfy(CollectionShelf.watched)
        return HStack(spacing: 8) {
            Button {
                play()
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: "play.fill")
                    Text(verbatim: playLabel).font(MiraFont.headline).lineLimit(1).minimumScaleFactor(0.75)
                }
                .foregroundStyle(Palette.onAccent)
                .frame(maxWidth: .infinity, minHeight: 36)
            }
            .buttonStyle(.glassProminent)
            .accessibilityIdentifier("shelf.play")
            round(favorite ? "heart.fill" : "heart", favorite ? words("Dans tes favoris", "In favorites") : words("Ajouter aux favoris", "Favorite"), on: favorite) {
                setFavorite(!favorite)
            }
            .accessibilityIdentifier("shelf.favorite")
            round(watched ? "checkmark.circle.fill" : "checkmark.circle", watched ? words("Toute la collection est vue", "The whole collection is watched") : words("Marquer la collection comme vue", "Mark collection as watched"), on: watched) {
                setWatched(!watched)
            }
            .accessibilityIdentifier("shelf.watched")
        }
    }

    private func round(_ symbol: String, _ label: String, on: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.title3.weight(.semibold))
                .foregroundStyle(Palette.ink)
                .contentTransition(.symbolEffect(.replace))
                .frame(width: 48, height: 48)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular.interactive(), in: .circle)
        .accessibilityLabel(Text(verbatim: label))
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    /// « Resume · Nisemonogatari · S1 E4 », « Watch · … », or « Watch again » from the first title once all are watched.
    private var playLabel: String {
        guard let title = upNext ?? ordered.first else { return words("Regarder", "Watch") }
        let episode = target?.isEpisode == true && target?.seriesId == title.id ? target : nil
        let playing = episode ?? (title.isSeries ? nil : title)
        let resume = playing.map { ItemText.progress($0) > 0 } ?? false
        let verb = upNext == nil ? words("Revoir", "Watch again") : resume ? words("Reprendre", "Resume") : words("Regarder", "Watch")
        let code = episode.map { "  ·  S\($0.parentIndexNumber ?? 1) E\($0.indexNumber ?? 1)" } ?? ""
        return "\(verb)  ·  \(title.name ?? "")\(code)"
    }

    /// One notch per title in the watch order: full once watched, partly while under way.
    private var steps: some View {
        let list = ordered
        return HStack(spacing: 3) {
            ForEach(list) { title in
                Capsule().fill(Palette.raised)
                    .overlay(alignment: .leading) {
                        GeometryReader { geometry in
                            Capsule().fill(Palette.ink).frame(width: geometry.size.width * CollectionShelf.progress(title))
                        }
                    }
                    .frame(height: 4)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: words("\(list.count(where: CollectionShelf.watched)) sur \(list.count) vus", "\(list.count(where: CollectionShelf.watched)) of \(list.count) watched")))
    }

    private var more: some View {
        Menu {
            if let client = app.client {
                let grouped = ShelfGrouping.shared.isGrouped(client, box)
                Button {
                    Task { await setGrouped(!grouped, undoable: true) }
                } label: {
                    Label(grouped ? words("Ne plus regrouper dans la bibliothèque", "Stop grouping in the library") : words("Regrouper dans la bibliothèque", "Group in the library"),
                          systemImage: grouped ? "rectangle.stack.badge.minus" : "rectangle.stack")
                }
                if titles.count > 1 {
                    Button {
                        editing = true
                    } label: {
                        Label(words("Changer l’ordre de visionnage…", "Change the watch order…"), systemImage: "arrow.up.arrow.down")
                    }
                }
                if order != nil {
                    Button {
                        Task { await save(order: nil) }
                    } label: {
                        Label(words("Revenir à l’ordre de sortie", "Back to release order"), systemImage: "arrow.counterclockwise")
                    }
                }
            }
        } label: {
            Label(words("Plus d’actions", "More actions"), systemImage: "ellipsis")
        }
        .accessibilityIdentifier("shelf.more")
    }

    // MARK: The titles

    private var columns: (count: Int, width: CGFloat) {
        let inner = width - 32
        let count = max(3, Int((inner + 12) / (140 + 12)))
        return (count, (inner - CGFloat(count - 1) * 12) / CGFloat(count))
    }

    @ViewBuilder private var grid: some View {
        let layout = columns
        let list = ordered
        let shown = byRelease ? CollectionShelf.byRelease(titles) : list
        let next = upNext
        if let client = app.client {
            LazyVGrid(columns: Array(repeating: GridItem(.fixed(layout.width), spacing: 12, alignment: .top), count: layout.count), alignment: .leading, spacing: 18) {
                ForEach(shown) { title in
                    // Numbered in the watch order, whichever order the page shows.
                    let place = list.firstIndex { $0.id == title.id } ?? 0
                    NavigationLink(value: TitleRoute(item: title, source: "shelf-\(title.id)")) {
                        NumberedPoster(item: title, client: client, width: layout.width, number: CollectionShelf.number(place), line: CollectionShelf.titleLine(title), upNext: title.id == next?.id)
                    }
                    .buttonStyle(.plain)
                    .titleMenu(title)
                }
            }
            .padding(.horizontal, 16)
            .accessibilityIdentifier("shelf.grid")
        }
    }

    // MARK: Loading and actions

    /// The collection's titles from Jellyfin (nested collections flattened), this account's order, then what « Resume » plays.
    private func load() async {
        guard let client = app.client else { return }
        if titles.isEmpty { loading = true }
        defer { loading = false }
        do {
            async let read = client.collectionTitles(box.id)
            async let chosen = client.shelfChoices()
            async let own = client.item(box.id)
            titles = try await read
            order = (try await chosen)[box.id]?.order
            if let fresh = try? await own { box.userData = fresh.userData; box.name = fresh.name ?? box.name }
            failure = nil
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
            return
        } catch is CancellationError {
            return
        } catch {
            // The titles of the card, if it had them, still show: only their order and progress may be older.
            if titles.isEmpty { failure = error } else { app.together.notice = Messages.text(error) }
            return
        }
        target = nil
        if let up = upNext, up.isSeries { target = try? await client.nextUp(series: up.id) }
    }

    private func play() {
        let list = ordered
        guard !list.isEmpty else { return }
        let up = CollectionShelf.upNext(list).map { list[$0] }
        let title = up ?? list[0]
        if !title.isSeries { app.play(title, fromStart: up == nil); return }
        if up != nil, let episode = target, episode.isEpisode, episode.seriesId == title.id { app.play(episode); return }
        Task {
            guard let client = app.client, let episode = try? await client.nextUp(series: title.id) else { return }
            app.play(episode, fromStart: up == nil)
        }
    }

    private func setFavorite(_ favorite: Bool) {
        var data = box.userData ?? UserData()
        data.isFavorite = favorite
        let was = box
        box.userData = data
        Task { await app.set(was, favorite: favorite) }
    }

    /// One request for all its titles: Jellyfin marks each one of a collection (UserPlayedItems on a BoxSet, checked on 12.1).
    private func setWatched(_ played: Bool) {
        titles = titles.map { title in
            var marked = title
            var data = marked.userData ?? UserData()
            data.played = played
            if played { data.playbackPositionTicks = 0 }
            data.playedPercentage = played ? 100 : 0
            marked.userData = data
            return marked
        }
        Task { await app.set(box, played: played) }
    }

    /// The order kept for this account (nil: by release date again), then the library drawn again.
    private func save(order wanted: [String]?) async {
        guard let client = app.client else { return }
        let was = order
        order = wanted
        byRelease = false
        do {
            let all = try await client.setShelfChoice(box.id) { var x = $0; x.order = wanted; return x }
            ShelfGrouping.shared.took(client, all)
            if let up = upNext, up.isSeries { target = try? await client.nextUp(series: up.id) }
        } catch {
            order = was
            app.together.notice = Messages.text(error)
        }
    }

    private func setGrouped(_ grouped: Bool, undoable: Bool) async {
        guard let client = app.client else { return }
        do {
            let all = try await client.setShelfChoice(box.id) { var x = $0; x.grouped = grouped; return x }
            ShelfGrouping.shared.took(client, all)
        } catch {
            app.together.notice = Messages.text(error)
            return
        }
        let said = grouped ? CollectionShelf.groupedNotice(titles.count, box.name ?? "") : CollectionShelf.ungroupedNotice(box.name ?? "")
        if undoable { app.together.say(said) { Task { await setGrouped(!grouped, undoable: false) } } } else { app.together.notice = said }
        app.playbackChanged()
    }
}

/// A poster on a collection's page: its number in the watch order before its name, its year and length (or episodes)
/// under it, and « UP NEXT » with a play disc on the title the collection resumes at.
struct NumberedPoster: View {
    let item: Item
    let client: JellyfinClient
    let width: CGFloat
    let number: String
    let line: String
    let upNext: Bool
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        let art = Artwork.of(item, .poster)
        let name = item.name ?? ""
        let progress = CollectionShelf.progress(item)
        VStack(alignment: .leading, spacing: 8) {
            RemoteImage(url: art.map { client.imageURL($0, width: width, scale: displayScale) }, blurHash: art?.blurHash, label: name, width: width)
                .frame(width: width, height: width * 3 / 2)
                .overlay(alignment: .topLeading) {
                    if upNext {
                        Text(verbatim: CollectionShelf.words("À SUIVRE", "UP NEXT"))
                            .font(.caption2.weight(.bold))
                            .foregroundStyle(Palette.onAccent)
                            .padding(.horizontal, 7)
                            .padding(.vertical, 4)
                            .background(Palette.accent, in: .rect(cornerRadius: 6))
                            .padding(7)
                    }
                }
                .overlay {
                    if upNext {
                        Image(systemName: "play.fill")
                            .font(.system(size: 16, weight: .bold))
                            .foregroundStyle(Palette.onAccent)
                            .frame(width: 40, height: 40)
                            .background(Palette.accent, in: .circle)
                    }
                }
                .overlay(alignment: .topTrailing) {
                    if CollectionShelf.watched(item) {
                        Image(systemName: "checkmark")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(Palette.onAccent)
                            .frame(width: 24, height: 24)
                            .background(Palette.accent, in: .circle)
                            .padding(7)
                    }
                }
                .overlay(alignment: .bottom) {
                    if progress > 0, progress < 1 { ProgressLine(value: progress).padding(8) }
                }
                .clipShape(.rect(cornerRadius: 12, style: .continuous))
                .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.white.opacity(0.06)) }
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: "\(number)  \(name)").font(MiraFont.subheadline).foregroundStyle(Palette.ink).lineLimit(2)
                Text(verbatim: line).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
            }
        }
        .frame(width: width, alignment: .leading)
        .contentShape(.rect)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: [number, name, line, upNext ? CollectionShelf.words("à suivre", "up next") : ""].filter { !$0.isEmpty }.joined(separator: ", ")))
        .accessibilityValue(CollectionShelf.watched(item) ? Text(verbatim: CollectionShelf.words("Vu", "Watched")) : Text(verbatim: ""))
        .accessibilityAddTraits(.isButton)
    }
}

/// A grouped collection's card in Movies and Shows: its poster on two edges of the ones behind it, a pile, how far its
/// titles are, and « Collection · 17 titles » under it.
struct ShelfCard: View {
    let item: Item
    let client: JellyfinClient
    let width: CGFloat
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        let titles = item.shelf ?? []
        // Its own poster, else its first title's.
        let art = Artwork.of(item, .poster) ?? CollectionShelf.byRelease(titles).lazy.compactMap { Artwork.of($0, .poster) }.first
        let name = item.name ?? ""
        let share = CollectionShelf.share(titles)
        VStack(alignment: .leading, spacing: 8) {
            ZStack(alignment: .bottom) {
                edge(2)
                edge(1)
                RemoteImage(url: art.map { client.imageURL($0, width: width, scale: displayScale) }, blurHash: art?.blurHash, label: name, width: width)
                    .frame(width: width, height: width * 3 / 2)
                    .overlay(alignment: .topTrailing) {
                        if !titles.isEmpty, titles.allSatisfy(CollectionShelf.watched) {
                            Image(systemName: "checkmark")
                                .font(.caption.weight(.bold))
                                .foregroundStyle(Palette.onAccent)
                                .frame(width: 24, height: 24)
                                .background(Palette.accent, in: .circle)
                                .padding(7)
                        }
                    }
                    .overlay(alignment: .bottom) {
                        if share > 0, share < 1 { ProgressLine(value: share).padding(8) }
                    }
                    .clipShape(.rect(cornerRadius: 12, style: .continuous))
                    .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.white.opacity(0.06)) }
            }
            .padding(.top, 10)
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: name).font(MiraFont.subheadline).foregroundStyle(Palette.ink).lineLimit(2)
                Text(verbatim: CollectionShelf.cardLine(titles.count)).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1)
            }
        }
        .frame(width: width, alignment: .leading)
        .contentShape(.rect)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: "\(name), \(CollectionShelf.cardLine(titles.count))"))
        .accessibilityAddTraits(.isButton)
    }

    /// An edge of the posters under its own, higher and narrower the further back.
    private func edge(_ depth: CGFloat) -> some View {
        RoundedRectangle(cornerRadius: 12, style: .continuous)
            .fill(depth == 1 ? Palette.raised : Palette.surface)
            .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.white.opacity(0.08)) }
            .frame(width: width - 12 * depth, height: width * 3 / 2)
            .offset(y: -5 * depth)
            .accessibilityHidden(true)
    }
}

/// « Change the watch order… »: the titles as rows, dragged to their place, kept with « Done ».
struct ShelfOrderSheet: View {
    @State var titles: [Item]
    let done: ([String]) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                ForEach(Array(titles.enumerated()), id: \.element.id) { place, title in
                    HStack(spacing: 12) {
                        Text(verbatim: CollectionShelf.number(place)).font(MiraFont.headline).foregroundStyle(Palette.muted).monospacedDigit()
                        VStack(alignment: .leading, spacing: 2) {
                            Text(verbatim: title.name ?? "").font(MiraFont.headline).foregroundStyle(Palette.ink).lineLimit(1)
                            Text(verbatim: CollectionShelf.titleLine(title)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                        }
                    }
                }
                .onMove { from, to in titles.move(fromOffsets: from, toOffset: to) }
            }
            .environment(\.editMode, .constant(.active))
            .navigationTitle(Text(verbatim: CollectionShelf.words("Ordre de visionnage", "Watch order")))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button { dismiss() } label: { Text(verbatim: CollectionShelf.words("Annuler", "Cancel")) }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button {
                        done(titles.map(\.id))
                        dismiss()
                    } label: { Text(verbatim: CollectionShelf.words("Terminé", "Done")) }
                        .accessibilityIdentifier("shelf.orderDone")
                }
            }
        }
    }
}
