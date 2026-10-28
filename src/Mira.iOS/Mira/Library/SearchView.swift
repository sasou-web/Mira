import SwiftUI

/// Search, as on Mira web: movies and shows by their name as you type, then episodes by theirs; the last searches of
/// this account stay on this iPhone to search again with a touch.
struct SearchView: View {
    @Environment(AppModel.self) private var app
    @State private var text = ""
    @State private var titles: [Item] = []
    @State private var episodes: [Item] = []
    @State private var searched: String?
    @State private var searching = false
    @State private var failure: Error?
    @State private var recent: [String] = []
    @State private var width: CGFloat = 402
    @Namespace private var zoom

    private var term: String { text.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                if term.isEmpty {
                    recentSearches
                } else if let client = app.client {
                    results(client)
                }
            }
            .padding(.top, 8)
            .padding(.bottom, 40)
        }
        .scrollIndicators(.hidden)
        .scrollDismissesKeyboard(.immediately)
        .background(Palette.bg)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .navigationTitle("Search")
        .searchable(text: $text, prompt: Text("Movies, shows, episodes"))
        .onSubmit(of: .search) { remember(term) }
        .task(id: term) {
            guard !term.isEmpty else {
                titles = []
                episodes = []
                searched = nil
                failure = nil
                return
            }
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
            await search(term)
        }
        .onAppear { recent = RecentSearches.load(app.session?.userId) }
        .navigationDestination(for: TitleRoute.self) { route in
            TitleView(route: route).navigationTransition(.zoom(sourceID: route.source, in: zoom))
        }
    }

    // MARK: Before typing

    @ViewBuilder private var recentSearches: some View {
        if recent.isEmpty {
            EmptyState(symbol: "magnifyingglass", title: Text("Search your library"), text: Text("Find a movie, a show or an episode by its name."))
        } else {
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text("Recent searches").font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
                    Spacer()
                    Button("Clear") {
                        RecentSearches.clear(app.session?.userId)
                        recent = []
                    }
                    .font(MiraFont.callout)
                    .foregroundStyle(Palette.muted)
                }
                .padding(.bottom, 6)
                ForEach(recent, id: \.self) { entry in
                    Button {
                        text = entry
                    } label: {
                        HStack(spacing: 12) {
                            Image(systemName: "clock.arrow.circlepath").foregroundStyle(Palette.muted).accessibilityHidden(true)
                            Text(verbatim: entry).font(MiraFont.body).foregroundStyle(Palette.ink).lineLimit(1)
                            Spacer()
                        }
                        .padding(.vertical, 10)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 16)
        }
    }

    // MARK: Results

    /// Posters three across at least, as in the libraries.
    private var poster: (count: Int, width: CGFloat) {
        let inner = width - 32
        let count = max(3, Int((inner + 12) / (140 + 12)))
        return (count, (inner - CGFloat(count - 1) * 12) / CGFloat(count))
    }

    @ViewBuilder private func results(_ client: JellyfinClient) -> some View {
        if titles.isEmpty && episodes.isEmpty {
            if searching || searched != term {
                ProgressView().frame(maxWidth: .infinity).padding(.top, 80)
            } else if let failure {
                EmptyState(symbol: "wifi.exclamationmark", title: Text("Jellyfin isn’t answering"), text: Text(Messages.text(failure))) {
                    Button { Task { await search(term) } } label: { Text("Try again").font(MiraFont.headline).padding(.horizontal, 8) }
                        .buttonStyle(.glass)
                }
            } else {
                EmptyState(symbol: "magnifyingglass", title: Text("Nothing found for “\(term)”"), text: Text("Check the spelling, or try another word of the title."))
            }
        } else {
            if !titles.isEmpty {
                let layout = poster
                VStack(alignment: .leading, spacing: 12) {
                    Text("Movies and shows").font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
                        .padding(.horizontal, 16)
                    LazyVGrid(columns: Array(repeating: GridItem(.fixed(layout.width), spacing: 12, alignment: .top), count: layout.count), alignment: .leading, spacing: 18) {
                        ForEach(titles) { item in
                            NavigationLink(value: TitleRoute(item: item, source: "search-\(item.id)")) {
                                PosterCard(item: item, client: client, width: layout.width)
                                    .matchedTransitionSource(id: "search-\(item.id)", in: zoom)
                            }
                            .buttonStyle(.plain)
                            .titleMenu(item)
                            .simultaneousGesture(TapGesture().onEnded { remember(term) })
                        }
                    }
                    .padding(.horizontal, 16)
                    .accessibilityIdentifier("search.titles")
                }
            }
            if !episodes.isEmpty {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Episodes").font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
                        .padding(.horizontal, 16)
                    ScrollView(.horizontal) {
                        LazyHStack(alignment: .top, spacing: 12) {
                            ForEach(episodes) { item in
                                NavigationLink(value: TitleRoute(item: item, source: "search-\(item.id)")) {
                                    WideCard(item: item, client: client, width: 240)
                                        .matchedTransitionSource(id: "search-\(item.id)", in: zoom)
                                }
                                .buttonStyle(.plain)
                                .simultaneousGesture(TapGesture().onEnded { remember(term) })
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

    // MARK: Asking Jellyfin

    private func search(_ asked: String) async {
        guard let client = app.client else { return }
        searching = true
        defer { searching = false }
        var query = BrowseQuery(types: "Movie,Series")
        query.sort = .title
        query.search = asked
        do {
            async let found = client.browse(query, start: 0, limit: 48)
            async let parts = client.searchEpisodes(asked)
            let (page, list) = try await (found, parts)
            guard asked == term else { return }
            titles = page.items ?? []
            episodes = list
            failure = nil
            searched = asked
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch is CancellationError {
        } catch {
            guard asked == term else { return }
            titles = []
            episodes = []
            failure = error
            searched = asked
        }
    }

    private func remember(_ entry: String) {
        guard !entry.isEmpty else { return }
        recent = RecentSearches.add(entry, for: app.session?.userId)
    }
}

/// The last eight searches of an account, on this iPhone only, newest first.
enum RecentSearches {
    private static func key(_ userId: String?) -> String { "mira.search.recent.\(userId ?? "")" }

    static func load(_ userId: String?) -> [String] { UserDefaults.standard.stringArray(forKey: key(userId)) ?? [] }

    static func add(_ entry: String, for userId: String?) -> [String] {
        var list = load(userId).filter { $0.caseInsensitiveCompare(entry) != .orderedSame }
        list.insert(entry, at: 0)
        list = Array(list.prefix(8))
        UserDefaults.standard.set(list, forKey: key(userId))
        return list
    }

    static func clear(_ userId: String?) { UserDefaults.standard.removeObject(forKey: key(userId)) }
}
