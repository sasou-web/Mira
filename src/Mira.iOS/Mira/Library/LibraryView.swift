import SwiftUI
import UIKit

/// Films or Shows: the whole library in a grid of posters, a page of 60 at a time as it scrolls, with Mira web's sorts
/// and filters (library.js), and its libraries side by side when there are several of the kind (Shows | Anime).
struct LibraryView: View {
    enum Kind: String { case movies, shows }
    let kind: Kind
    @Environment(AppModel.self) private var app
    @State private var query: BrowseQuery
    @State private var items: [Item] = []
    @State private var total: Int?
    @State private var loading = false
    @State private var failure: Error?
    @State private var libraries: [Library] = []
    @State private var genres: [String] = []
    @State private var years: [String] = []
    /// Audio and subtitle languages offered: Jellyfin 12 or later (LibraryFilters.languagesSupported).
    @State private var languages = false
    @State private var width: CGFloat = 402
    /// On screen: a change of progress or of a mark is read again at once; otherwise once the grid shows again.
    @State private var shown = false
    @State private var stale = false
    /// « L'étagère » (CollectionShelf): the collections grouped for this account, the titles Jellyfin sent (where the
    /// next page starts: a collection's card stands for several), those a card took the place of, the cards shown.
    @State private var grouping = CollectionShelf.Index.none
    @State private var fetched = 0
    @State private var absorbed = 0
    @State private var placed = Set<String>()
    @Namespace private var zoom

    init(kind: Kind, genre: String? = nil) {
        self.kind = kind
        var query = BrowseQuery(types: kind == .movies ? "Movie" : "Series")
        query.genre = genre
        _query = State(initialValue: query)
    }

    private var title: LocalizedStringKey { kind == .movies ? "Movies" : "Shows" }

    /// The status bar's height (0 when hidden, in landscape), read again whenever the page's width changes.
    private var statusBar: CGFloat {
        _ = width
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        return scene?.keyWindow?.safeAreaInsets.top ?? 0
    }
    private var collection: String { kind == .movies ? "movies" : "tvshows" }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                if libraries.count > 1 {
                    Picker("Library", selection: $query.library) {
                        Text("All").tag(String?.none)
                        ForEach(libraries) { library in Text(verbatim: library.name).tag(String?.some(library.id)) }
                    }
                    .pickerStyle(.segmented)
                    .padding(.horizontal, 16)
                }
                filters
                if let total, total > 0 {
                    Text(verbatim: countText(total)).font(MiraFont.footnote).foregroundStyle(Palette.muted).padding(.horizontal, 16)
                }
                grid
            }
            .padding(.bottom, 40)
        }
        .scrollIndicators(.hidden)
        .foldsTabBar()
        // No header: the tab bar already names the page, and a bar with its blur only hid the posters. The sort
        // button floats alone at the top right; a shade under the status bar keeps the time readable over a poster.
        .scrollEdgeEffectHidden(true, for: .top)
        .toolbarBackgroundVisibility(.hidden, for: .navigationBar)
        .overlay(alignment: .top) {
            LinearGradient(colors: [Palette.bg.opacity(0.85), Palette.bg.opacity(0)], startPoint: .top, endPoint: .bottom)
                .frame(height: statusBar + 12)
                .ignoresSafeArea(edges: .top)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
        .background(Palette.bg)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            // The title stays the page's name for the back button's menu, never shown in the bar.
            ToolbarItem(placement: .principal) { Color.clear.frame(width: 1, height: 1).accessibilityHidden(true) }
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Picker("Sort by", selection: $query.sort) {
                        ForEach(BrowseQuery.Sort.allCases, id: \.self) { sort in Text(sortName(sort)).tag(sort) }
                    }
                } label: {
                    Label("Sort by", systemImage: "arrow.up.arrow.down")
                }
                .accessibilityIdentifier("library.sort")
            }
        }
        .refreshable { await reload() }
        .task(id: query) { await reload() }
        // A way to Jellyfin found again: what failed is asked again.
        .onChange(of: app.way) { _, way in
            if way != nil, failure != nil { Task { await reload() } }
        }
        .task { await loadOptions() }
        .onChange(of: app.playback) {
            // The collections' cards say how far their titles are: read again.
            ShelfGrouping.shared.forget()
            if shown { Task { await refresh() } } else { stale = true }
        }
        .onAppear {
            shown = true
            if stale { stale = false; Task { await refresh() } }
        }
        .onDisappear { shown = false }
        .navigationDestination(for: TitleRoute.self) { route in
            TitleView(route: route).navigationTransition(.zoom(sourceID: route.source, in: zoom))
        }
        .navigationDestination(for: ShelfRoute.self) { route in
            ShelfView(route: route).navigationTransition(.zoom(sourceID: route.source, in: zoom))
        }
    }

    // MARK: Filters

    private var filters: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 8) {
                Menu {
                    Picker("Genre", selection: $query.genre) {
                        Text("All genres").tag(String?.none)
                        ForEach(genres, id: \.self) { genre in Text(verbatim: genre).tag(String?.some(genre)) }
                    }
                } label: {
                    chip(query.genre.map { Text(verbatim: $0) } ?? Text("Genre"), on: query.genre != nil, menu: true)
                }
                Menu {
                    Picker("Year", selection: $query.year) {
                        Text("All years").tag(String?.none)
                        ForEach(years, id: \.self) { year in Text(verbatim: year).tag(String?.some(year)) }
                    }
                } label: {
                    chip(query.year.map { Text(verbatim: $0) } ?? Text("Year"), on: query.year != nil, menu: true)
                }
                Menu {
                    Picker("Picture", selection: $query.quality) {
                        Text("All").tag(String?.none)
                        ForEach(LibraryFilters.qualities, id: \.self) { key in qualityText(key, short: false).tag(String?.some(key)) }
                    }
                } label: {
                    chip(query.quality.map { qualityText($0, short: true) } ?? Text("Picture"), on: query.quality != nil, menu: true)
                }
                .accessibilityIdentifier("library.picture")
                Menu {
                    Picker("Rating", selection: $query.minRating) {
                        Text("All").tag(Int?.none)
                        ForEach(LibraryFilters.ratings, id: \.self) { rating in Text("\(rating) and up").tag(Int?.some(rating)) }
                    }
                } label: {
                    chip(query.minRating.map { Text("\($0) and up") } ?? Text("Rating"), on: query.minRating != nil, menu: true)
                }
                if languages {
                    Menu {
                        Picker("Audio language", selection: $query.audio) {
                            Text("All").tag(String?.none)
                            ForEach(LibraryFilters.languages.map(\.key), id: \.self) { key in languageText(key).tag(String?.some(key)) }
                        }
                    } label: {
                        chip(query.audio.map { Text("Audio · \(languageText($0))") } ?? Text("Audio language"), on: query.audio != nil, menu: true)
                    }
                }
                Menu {
                    Picker("Subtitles", selection: $query.subtitles) {
                        Text("With or without").tag(String?.none)
                        Text("With subtitles").tag(String?.some("any"))
                        Text("Without subtitles").tag(String?.some("none"))
                        if languages {
                            ForEach(LibraryFilters.languages.map(\.key), id: \.self) { key in languageText(key).tag(String?.some(key)) }
                        }
                    }
                } label: {
                    chip(subtitlesChip, on: query.subtitles != nil, menu: true)
                }
                Button { query.unplayed.toggle() } label: { chip(Text("Unwatched"), on: query.unplayed) }
                    .accessibilityAddTraits(query.unplayed ? .isSelected : [])
                Button { query.favorite.toggle() } label: { chip(Text("Favorites"), on: query.favorite) }
                    .accessibilityAddTraits(query.favorite ? .isSelected : [])
                    .accessibilityIdentifier("library.favorites")
                if query.filtered {
                    Button {
                        query.genre = nil
                        query.year = nil
                        query.unplayed = false
                        query.favorite = false
                        query.quality = nil
                        query.audio = nil
                        query.subtitles = nil
                        query.minRating = nil
                    } label: {
                        chip(Text("Reset"), on: false, symbol: "xmark")
                    }
                }
            }
            .padding(.horizontal, 16)
        }
        .scrollIndicators(.hidden)
        // The row goes on past the right edge: it fades there, so that it reads as a row to swipe.
        .mask {
            HStack(spacing: 0) {
                Rectangle()
                LinearGradient(colors: [.black, .black.opacity(0)], startPoint: .leading, endPoint: .trailing).frame(width: 28)
            }
        }
        .buttonStyle(.plain)
    }

    private func chip(_ label: Text, on: Bool, menu: Bool = false, symbol: String? = nil) -> some View {
        HStack(spacing: 4) {
            if let symbol { Image(systemName: symbol).font(.caption.weight(.bold)) }
            label.font(MiraFont.callout).lineLimit(1)
            if menu { Image(systemName: "chevron.down").font(.caption2.weight(.bold)) }
        }
        .foregroundStyle(on ? Palette.onAccent : Palette.ink)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(on ? Palette.accent : Palette.raised, in: .capsule)
    }

    // MARK: The grid

    /// Mira web's posters: at least three across, 156 points at most.
    private var columns: (count: Int, width: CGFloat) {
        let inner = width - 32
        let count = max(3, Int((inner + 12) / (140 + 12)))
        return (count, (inner - CGFloat(count - 1) * 12) / CGFloat(count))
    }

    @ViewBuilder private var grid: some View {
        let layout = columns
        if let client = app.client, !items.isEmpty {
            LazyVGrid(columns: Array(repeating: GridItem(.fixed(layout.width), spacing: 12, alignment: .top), count: layout.count), alignment: .leading, spacing: 18) {
                ForEach(items) { item in
                    Group {
                        if item.shelf != nil {
                            NavigationLink(value: ShelfRoute(box: item, source: "grid-\(item.id)")) {
                                ShelfCard(item: item, client: client, width: layout.width)
                                    .matchedTransitionSource(id: "grid-\(item.id)", in: zoom)
                            }
                            .buttonStyle(.plain)
                            .contextMenu { shelfMenu(item, client) }
                        } else {
                            NavigationLink(value: TitleRoute(item: item, source: "grid-\(item.id)")) {
                                PosterCard(item: item, client: client, width: layout.width)
                                    .matchedTransitionSource(id: "grid-\(item.id)", in: zoom)
                            }
                            .buttonStyle(.plain)
                            .titleMenu(item)
                        }
                    }
                    .onAppear { if item.id == items.last?.id { Task { await more() } } }
                }
            }
            .padding(.horizontal, 16)
            .accessibilityIdentifier("library.grid")
            if loading { ProgressView().frame(maxWidth: .infinity).padding(.vertical, 20) }
        } else if loading {
            ProgressView().frame(maxWidth: .infinity).padding(.top, 80)
        } else if let failure {
            EmptyState(symbol: "wifi.exclamationmark", title: Text("Jellyfin isn’t answering"), text: Text(Messages.text(failure))) {
                Button {
                    Task {
                        await Way.shared.retry()
                        await reload()
                    }
                } label: { Text("Try again").font(MiraFont.headline).padding(.horizontal, 8) }
                    .buttonStyle(.glass)
            }
            .frame(maxWidth: .infinity)
        } else if total == 0 {
            if query.filtered {
                EmptyState(symbol: "line.3.horizontal.decrease.circle", title: Text("No titles with these filters"), text: Text("Remove a filter to see more."))
                    .frame(maxWidth: .infinity)
            } else {
                EmptyState(symbol: "film.stack", title: Text("Nothing here yet"), text: Text("Titles you add to Jellyfin will show up here."))
                    .frame(maxWidth: .infinity)
            }
        }
    }

    // MARK: Loading

    /// Movies and Shows as a whole (no person's titles): where a grouped collection is one card.
    private var groups: Bool { query.search == nil && query.person == nil }

    /// A page received: a grouped collection's titles taken in by its card (CollectionShelf.collapse).
    private func receive(_ page: [Item], more: Bool) -> [Item] {
        if !more { fetched = 0; absorbed = 0; placed = [] }
        fetched += page.count
        guard groups, !grouping.shelves.isEmpty else { return page }
        var cards = placed
        let shown = CollectionShelf.collapse(page, grouping, query.types, placed: &cards)
        placed = cards
        absorbed += page.count - shown.count(where: { $0.shelf == nil })
        return shown
    }

    /// The collections grouped for this account, asked beside the page; none for a person's titles.
    private func askGrouping(_ client: JellyfinClient) -> Task<(index: CollectionShelf.Index, automatic: [CollectionShelf.Shelf]), Never>? {
        groups ? Task { await ShelfGrouping.shared.index(client) } : nil
    }

    /// The collections grouped; the first time sagas are grouped on their own, said once with « Undo ».
    private func apply(_ asked: Task<(index: CollectionShelf.Index, automatic: [CollectionShelf.Shelf]), Never>?, _ client: JellyfinClient) async {
        guard let asked else { grouping = .none; return }
        let (index, automatic) = await asked.value
        grouping = index
        let told = "mira.collectionsGroupedTold"
        guard !automatic.isEmpty, !UserDefaults.standard.bool(forKey: told) else { return }
        UserDefaults.standard.set(true, forKey: told)
        let boxes = automatic.map(\.box)
        app.together.say(CollectionShelf.automaticNotice(automatic.reduce(0) { $0 + $1.titles.count }, automatic.count)) {
            Task {
                for box in boxes { _ = try? await client.setShelfChoice(box.id) { var x = $0; x.grouped = false; return x } }
                ShelfGrouping.shared.forget()
                app.playbackChanged()
            }
        }
    }

    /// « Group in the library » or « Stop grouping » in a collection's card menu, with « Undo ».
    @ViewBuilder private func shelfMenu(_ box: Item, _ client: JellyfinClient) -> some View {
        Button {
            Task { await setGrouped(box, false, client, undoable: true) }
        } label: {
            Label(CollectionShelf.words("Ne plus regrouper dans la bibliothèque", "Stop grouping in the library"), systemImage: "rectangle.stack.badge.minus")
        }
        let favorite = box.userData?.isFavorite == true
        Button {
            Task { await app.set(box, favorite: !favorite) }
        } label: {
            Label(favorite ? CollectionShelf.words("Retirer des favoris", "Remove from favorites") : CollectionShelf.words("Ajouter aux favoris", "Add to favorites"), systemImage: favorite ? "heart.slash" : "heart")
        }
    }

    private func setGrouped(_ box: Item, _ grouped: Bool, _ client: JellyfinClient, undoable: Bool) async {
        do {
            let all = try await client.setShelfChoice(box.id) { var x = $0; x.grouped = grouped; return x }
            ShelfGrouping.shared.took(client, all)
        } catch {
            app.together.notice = Messages.text(error)
            return
        }
        let titles = box.shelf?.count ?? box.childCount ?? 0
        let said = grouped ? CollectionShelf.groupedNotice(titles, box.name ?? "") : CollectionShelf.ungroupedNotice(box.name ?? "")
        if undoable { app.together.say(said) { Task { await setGrouped(box, !grouped, client, undoable: false) } } } else { app.together.notice = said }
        app.playbackChanged()
    }

    private func reload() async {
        guard let client = app.client else { return }
        loading = true
        defer { loading = false }
        do {
            let asked = askGrouping(client)
            let page = try await client.browse(query, start: 0)
            await apply(asked, client)
            items = receive(page.items ?? [], more: false)
            total = page.totalRecordCount ?? fetched
            failure = nil
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch is CancellationError {
        } catch {
            if items.isEmpty { failure = error }
        }
    }

    /// The grid read again in place, as far as it was scrolled, without a wheel: progress and marks change, nothing jumps.
    private func refresh() async {
        guard let client = app.client, !loading, !items.isEmpty else { return await reload() }
        let asked = query
        let asking = askGrouping(client)
        guard let page = try? await client.browse(asked, start: 0, limit: max(60, fetched)), asked == query else { return }
        await apply(asking, client)
        items = receive(page.items ?? [], more: false)
        total = page.totalRecordCount ?? fetched
    }

    private func more() async {
        guard let client = app.client, !loading, let total, fetched > 0, fetched < total else { return }
        loading = true
        defer { loading = false }
        let asked = query
        // A page whose titles all went into cards already shown adds none: the next one is asked, a few at most.
        for _ in 0..<5 {
            guard let page = try? await client.browse(asked, start: fetched), asked == query else { return }
            if (page.items ?? []).isEmpty { self.total = fetched; return }
            let known = Set(items.map(\.id))
            let added = receive(page.items ?? [], more: true).filter { !known.contains($0.id) }
            items += added
            if !added.isEmpty || fetched >= total { return }
        }
    }

    private func loadOptions() async {
        guard let client = app.client else { return }
        libraries = (try? await client.libraries())?.filter { $0.kind == collection } ?? []
        genres = (try? await client.genres(query.types)) ?? []
        years = (try? await client.years(query.types)) ?? []
        languages = LibraryFilters.languagesSupported(await client.serverVersion())
    }

    /// « 4K », « HD », « SD » on the chip; the list says what HD is.
    private func qualityText(_ key: String, short: Bool) -> Text {
        switch key {
        case "4k": Text(verbatim: "4K")
        case "hd": short ? Text(verbatim: "HD") : Text("HD (720p, 1080p)")
        default: Text(verbatim: "SD")
        }
    }

    private func languageText(_ key: String) -> Text {
        switch key {
        case "fr": Text("French")
        case "en": Text("English")
        case "ja": Text("Japanese")
        case "es": Text("Spanish")
        case "de": Text("German")
        case "it": Text("Italian")
        case "ko": Text("Korean")
        case "zh": Text("Chinese")
        default: Text(verbatim: key)
        }
    }

    private var subtitlesChip: Text {
        switch query.subtitles {
        case nil: Text("Subtitles")
        case "any": Text("With subtitles")
        case "none": Text("Without subtitles")
        case let key?: Text("Subtitles · \(languageText(key))")
        }
    }

    private func sortName(_ sort: BrowseQuery.Sort) -> LocalizedStringKey {
        switch sort {
        case .recent: "Recently added"
        case .title: "Title"
        case .year: "Year"
        case .rating: "Rating"
        case .played: "Recently watched"
        case .runtime: "Shortest first"
        }
    }

    private func countText(_ count: Int) -> String {
        let cards = items.count(where: { $0.shelf != nil })
        if cards > 0 { return CollectionShelf.gridCount(max(0, count - absorbed), cards, movies: kind == .movies) }
        return kind == .movies ? String(localized: "\(count) movies") : String(localized: "\(count) shows")
    }
}
