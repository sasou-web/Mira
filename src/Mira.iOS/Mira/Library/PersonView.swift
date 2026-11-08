import SwiftUI
import UIKit

/// Someone of a title's credits, as the title's page shows them: its directors first, then its actors, each once, as
/// Jellyfin credits them (the order of the film). What a title's page lists, and what opens a person's page.
enum Credits {
    struct Credit: Hashable, Sendable {
        let id: String
        let name: String
        let imageTag: String?
        /// « Director », the character played (« Celia »), « Guest star », or nothing when Jellyfin doesn't say.
        let role: String?
    }

    /// Three directors at most, then twelve actors at most (an episode's guest stars after its actors): only those with
    /// an id (their page) and a name, the same name once in each part.
    static func of(_ item: Item, directors: Int = 3, actors: Int = 12) -> [Credit] {
        credited(item, ["Director"], count: directors) { _ in String(localized: "Director") }
            + credited(item, ["Actor", "GuestStar"], count: actors) { person in
                let role = person.role?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                if !role.isEmpty { return role }
                return person.type?.caseInsensitiveCompare("GuestStar") == .orderedSame ? String(localized: "Guest star") : nil
            }
    }

    private static func credited(_ item: Item, _ types: [String], count: Int, role: (Person) -> String?) -> [Credit] {
        var seen = Set<String>(), found: [Credit] = []
        for type in types {
            for person in item.people ?? [] where person.type?.caseInsensitiveCompare(type) == .orderedSame {
                let name = person.name?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                guard let id = person.id, !id.isEmpty, !name.isEmpty, found.count < count,
                      seen.insert(name.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)).inserted else { continue }
                found.append(Credit(id: id, name: name, imageTag: person.primaryImageTag, role: role(person)))
            }
        }
        return found
    }

    /// Under a person's name: « Born March 12, 1955 · Amsterdam », « March 12, 1918 – June 2, 2009 », or only where
    /// they were born; empty when Jellyfin knows none of it. Jellyfin's dates of a person are days, read in UTC (never a
    /// day earlier west of Greenwich).
    static func life(_ person: Item, locale: Locale = .autoupdatingCurrent) -> String {
        let born = person.premiereDate.flatMap { day($0, locale: locale) }
        let died = person.endDate.flatMap { day($0, locale: locale) }
        let place = person.productionLocations?.first { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        var parts: [String] = []
        switch (born, died) {
        case let (born?, died?): parts.append("\(born) – \(died)")
        case let (born?, nil): parts.append(String(localized: "Born \(born)", locale: locale, comment: "Under a person's name: their birthday"))
        case let (nil, died?): parts.append(String(localized: "Died \(died)", locale: locale, comment: "Under a person's name: the day they died"))
        case (nil, nil): break
        }
        if let place { parts.append(place) }
        return parts.joined(separator: " · ")
    }

    /// « 1955-03-12T00:00:00.0000000Z » as « March 12, 1955 ».
    static func day(_ text: String, locale: Locale) -> String? {
        let numbers = text.prefix(10).split(separator: "-").compactMap { Int($0) }
        guard numbers.count == 3, numbers[0] > 1 else { return nil }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        guard let date = calendar.date(from: DateComponents(year: numbers[0], month: numbers[1], day: numbers[2])) else { return nil }
        return date.formatted(Date.FormatStyle(date: .long, time: .omitted, locale: locale, calendar: calendar, timeZone: calendar.timeZone))
    }
}

/// A person's page, opened from a title's credits or from Search.
struct PersonRoute: Hashable {
    let id: String
    let name: String
    var imageTag: String?
}

/// A person's round photo, their initials on Mira's grey when Jellyfin has none.
struct PersonPhoto: View {
    let id: String
    let name: String
    let imageTag: String?
    let size: CGFloat
    @Environment(AppModel.self) private var app
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        ZStack {
            Palette.raised
            if let client = app.client, let tag = imageTag, !tag.isEmpty {
                RemoteImage(url: client.imageURL(Artwork(itemId: id, type: "Primary", tag: tag), width: size, scale: displayScale), width: size)
            } else {
                Text(verbatim: initials)
                    .font(MiraFont.bold(size * 0.34, .title))
                    .foregroundStyle(Palette.muted)
            }
        }
        .frame(width: size, height: size)
        .clipShape(.circle)
        .overlay { Circle().strokeBorder(.white.opacity(0.06)) }
        .accessibilityHidden(true)
    }

    private var initials: String {
        name.split(separator: " ").prefix(2).compactMap(\.first).map { String($0).uppercased() }.joined()
    }
}

/// One person in a row: their photo, their name, what they did.
struct PersonCard: View {
    let credit: Credits.Credit
    @ScaledMetric(relativeTo: .body) private var size: CGFloat = 84

    var body: some View {
        VStack(spacing: 8) {
            PersonPhoto(id: credit.id, name: credit.name, imageTag: credit.imageTag, size: size)
            VStack(spacing: 2) {
                Text(verbatim: credit.name).font(MiraFont.footnote).foregroundStyle(Palette.ink).lineLimit(2)
                if let role = credit.role {
                    Text(verbatim: role).font(MiraFont.semibold(12, .caption)).foregroundStyle(Palette.muted).lineLimit(2)
                }
            }
            .multilineTextAlignment(.center)
        }
        .frame(width: size + 16, alignment: .top)
        .contentShape(.rect)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: [credit.name, credit.role ?? ""].filter { !$0.isEmpty }.joined(separator: ", ")))
        .accessibilityHint(Text("Opens their movies and shows"))
    }
}

/// « Cast & crew » on a title's page: its directors, then its actors, in a row to swipe; each opens their page.
struct CreditsRow: View {
    let credits: [Credits.Credit]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Cast & crew").font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: 8) {
                    ForEach(Array(credits.enumerated()), id: \.offset) { index, credit in
                        NavigationLink(value: PersonRoute(id: credit.id, name: credit.name, imageTag: credit.imageTag)) {
                            PersonCard(credit: credit)
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("title.person.\(index)")
                    }
                }
                .scrollTargetLayout()
            }
            .scrollIndicators(.hidden)
            // Out to the page's edges: the row goes on past the screen, the first photo in line with the text.
            .contentMargins(.horizontal, 20, for: .scrollContent)
            .padding(.horizontal, -20)
            .scrollTargetBehavior(.viewAligned)
        }
        .padding(.top, 6)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("title.credits")
    }
}

/// A person's page, as on Windows and Mac: their photo, name, birth and biography, then their movies and shows in
/// this library, newest first, a page of 60 at a time as it scrolls.
struct PersonView: View {
    let route: PersonRoute
    @Environment(AppModel.self) private var app
    @State private var person: Item?
    @State private var items: [Item] = []
    @State private var total: Int?
    @State private var loading = false
    @State private var failure: Error?
    @State private var width: CGFloat = 402
    /// Scrolled past the name: the bar then shows it.
    @State private var pastName = false
    @Namespace private var zoom
    @ScaledMetric(relativeTo: .largeTitle) private var photo: CGFloat = 132

    private var query: BrowseQuery {
        var query = BrowseQuery(types: "Movie,Series")
        query.sort = .year
        query.person = route.id
        return query
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                header
                titles
            }
            .padding(.top, 8)
            .padding(.bottom, 40)
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
        }
        .scrollIndicators(.hidden)
        .foldsTabBar()
        .onScrollGeometryChange(for: Bool.self) { geometry in
            geometry.contentOffset.y + geometry.contentInsets.top > photo + 56
        } action: { _, past in
            withAnimation(.easeOut(duration: 0.2)) { pastName = past }
        }
        .background(Palette.bg)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = min(900, $0) }
        .navigationTitle(name)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .principal) {
                Text(verbatim: name)
                    .font(MiraFont.headline)
                    .foregroundStyle(Palette.ink)
                    .lineLimit(1)
                    .opacity(pastName ? 1 : 0)
                    .accessibilityHidden(!pastName)
            }
        }
        .refreshable { await load() }
        .task(id: route.id) { await load() }
        // A way to Jellyfin found again: what failed is asked again.
        .onChange(of: app.way) { _, way in
            if way != nil, failure != nil { Task { await load() } }
        }
        // A title watched or marked from here: its poster says so when the page shows again.
        .onChange(of: app.playback) { Task { await refresh() } }
    }

    private var name: String { person?.name ?? route.name }

    @ViewBuilder private var header: some View {
        VStack(spacing: 12) {
            PersonPhoto(id: route.id, name: name, imageTag: person?.imageTags?["Primary"] ?? route.imageTag, size: photo)
            Text(verbatim: name)
                .font(MiraFont.title)
                .foregroundStyle(Palette.ink)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityAddTraits(.isHeader)
                .accessibilityIdentifier("person.name")
            if let person {
                let life = Credits.life(person)
                if !life.isEmpty {
                    Text(verbatim: life).font(MiraFont.subheadline).foregroundStyle(Palette.muted).multilineTextAlignment(.center)
                }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal, 20)
        let biography = OverviewText.plain(person?.overview)
        if !biography.isEmpty {
            ClampedText(text: biography, lines: 4)
                .font(MiraFont.body)
                .foregroundStyle(Palette.ink.opacity(0.92))
                .lineSpacing(2)
                .padding(.horizontal, 20)
        }
    }

    /// Posters three across at least, as in the libraries.
    private var columns: (count: Int, width: CGFloat) {
        let inner = width - 32
        let count = max(3, Int((inner + 12) / (140 + 12)))
        return (count, (inner - CGFloat(count - 1) * 12) / CGFloat(count))
    }

    @ViewBuilder private var titles: some View {
        let layout = columns
        if let client = app.client, !items.isEmpty {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .firstTextBaseline) {
                    Text("Movies and shows").font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
                    Spacer()
                    if let total {
                        Text(verbatim: total.formatted()).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                            .accessibilityLabel(Text("\(total) titles"))
                    }
                }
                .padding(.horizontal, 16)
                LazyVGrid(columns: Array(repeating: GridItem(.fixed(layout.width), spacing: 12, alignment: .top), count: layout.count), alignment: .leading, spacing: 18) {
                    ForEach(items) { item in
                        NavigationLink(value: TitleRoute(item: item, source: "person-\(item.id)")) {
                            PosterCard(item: item, client: client, width: layout.width)
                                .matchedTransitionSource(id: "person-\(item.id)", in: zoom)
                        }
                        .buttonStyle(.plain)
                        .titleMenu(item)
                        .onAppear { if item.id == items.last?.id { Task { await more() } } }
                    }
                }
                .padding(.horizontal, 16)
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("person.titles")
                if loading { ProgressView().frame(maxWidth: .infinity).padding(.vertical, 20) }
            }
        } else if loading || (total == nil && failure == nil) {
            ProgressView().frame(maxWidth: .infinity).padding(.top, 40)
        } else if let failure {
            EmptyState(symbol: "wifi.exclamationmark", title: Text("Jellyfin isn’t answering"), text: Text(Messages.text(failure))) {
                Button {
                    Task {
                        await Way.shared.retry()
                        await load()
                    }
                } label: { Text("Try again").font(MiraFont.headline).padding(.horizontal, 8) }
                    .buttonStyle(.glass)
            }
            .frame(maxWidth: .infinity)
        } else {
            EmptyState(symbol: "film.stack", title: Text("No titles here"), text: Text("None of their movies or shows is in your library."))
                .frame(maxWidth: .infinity)
        }
    }

    // MARK: Loading

    private func load() async {
        guard let client = app.client else { return }
        loading = true
        defer { loading = false }
        async let details = Self.details(route.id, client)
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
        if let found = await details { person = found }
    }

    /// The person as Jellyfin knows them (photo, birth, biography); nil rather than an error: the titles still show.
    private static func details(_ id: String, _ client: JellyfinClient) async -> Item? {
        try? await client.item(id)
    }

    /// The grid read again in place, as far as it was scrolled: progress and marks change, nothing jumps.
    private func refresh() async {
        guard let client = app.client, !loading, !items.isEmpty else { return }
        guard let page = try? await client.browse(query, start: 0, limit: max(60, items.count)) else { return }
        items = page.items ?? []
        total = page.totalRecordCount ?? items.count
    }

    private func more() async {
        guard let client = app.client, !loading, let total, items.count < total else { return }
        loading = true
        defer { loading = false }
        guard let page = try? await client.browse(query, start: items.count) else { return }
        let known = Set(items.map(\.id))
        items += (page.items ?? []).filter { !known.contains($0.id) }
    }
}
