import SwiftUI

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
    @State private var width: CGFloat = 402
    /// On screen: a change of progress or of a mark is read again at once; otherwise once the grid shows again.
    @State private var shown = false
    @State private var stale = false
    @Namespace private var zoom

    init(kind: Kind, genre: String? = nil) {
        self.kind = kind
        var query = BrowseQuery(types: kind == .movies ? "Movie" : "Series")
        query.genre = genre
        _query = State(initialValue: query)
    }

    private var title: LocalizedStringKey { kind == .movies ? "Movies" : "Shows" }
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
                    Text(countText(total)).font(MiraFont.footnote).foregroundStyle(Palette.muted).padding(.horizontal, 16)
                }
                grid
            }
            .padding(.bottom, 40)
        }
        .scrollIndicators(.hidden)
        .foldsTabBar()
        .background(Palette.bg)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .navigationTitle(title)
        .toolbar {
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
                    NavigationLink(value: TitleRoute(item: item, source: "grid-\(item.id)")) {
                        PosterCard(item: item, client: client, width: layout.width)
                            .matchedTransitionSource(id: "grid-\(item.id)", in: zoom)
                    }
                    .buttonStyle(.plain)
                    .titleMenu(item)
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

    private func reload() async {
        guard let client = app.client else { return }
        loading = true
        defer { loading = false }
        do {
            let page = try await client.browse(query, start: 0)
            items = page.items ?? []
            total = page.totalRecordCount ?? items.count
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
        guard let page = try? await client.browse(asked, start: 0, limit: max(60, items.count)), asked == query else { return }
        items = page.items ?? []
        total = page.totalRecordCount ?? items.count
    }

    private func more() async {
        guard let client = app.client, !loading, let total, items.count < total else { return }
        loading = true
        defer { loading = false }
        let asked = query
        guard let page = try? await client.browse(asked, start: items.count), asked == query else { return }
        let known = Set(items.map(\.id))
        items += (page.items ?? []).filter { !known.contains($0.id) }
    }

    private func loadOptions() async {
        guard let client = app.client else { return }
        libraries = (try? await client.libraries())?.filter { $0.kind == collection } ?? []
        genres = (try? await client.genres(query.types)) ?? []
        years = (try? await client.years(query.types)) ?? []
    }

    private func sortName(_ sort: BrowseQuery.Sort) -> LocalizedStringKey {
        switch sort {
        case .recent: "Recently added"
        case .title: "Title"
        case .year: "Year"
        case .rating: "Rating"
        case .played: "Recently watched"
        }
    }

    private func countText(_ count: Int) -> String {
        kind == .movies ? String(localized: "\(count) movies") : String(localized: "\(count) shows")
    }
}
