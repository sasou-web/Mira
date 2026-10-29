import SwiftUI

/// « Ton bilan »: what this account finished watching since a date, from Jellyfin's own record (each title's last
/// play), as Mira web (report.js) and Windows and Mac (WatchReport in Core), with the cases of tests/fixtures/report.json.
/// A title watched twice counts once.
enum WatchReport {
    struct Series: Sendable, Equatable {
        let name: String
        let episodes: Int
        let minutes: Int
    }

    struct Summary: Sendable, Equatable {
        var minutes = 0
        var films = 0
        var episodes = 0
        /// The series all watched, last watched first.
        var finished: [String] = []
        /// The three genres watched longest.
        var genres: [String] = []
        /// The three series watched longest.
        var series: [Series] = []
    }

    private static func minutes(_ item: Item) -> Int { Int((Double(max(0, item.runTimeTicks ?? 0)) / 600_000_000).rounded()) }
    private static func playedAt(_ item: Item) -> Double {
        item.userData?.lastPlayedDate.flatMap(HomeContent.parseDate).map { $0.timeIntervalSince1970 * 1000 } ?? 0
    }
    private static func before(_ a: String, _ b: String) -> Bool { a.localizedStandardCompare(b) == .orderedAscending }

    /// « This month » starts on the 1st at midnight here, « This year » on 1 January.
    static func start(_ now: Date, year: Bool, calendar: Calendar = .current) -> Date {
        var parts = calendar.dateComponents([.year, .month], from: now)
        if year { parts.month = 1 }
        parts.day = 1
        return calendar.date(from: parts) ?? now
    }

    /// played: films and episodes played to the end; series: theirs, with their genres and whether all is watched;
    /// from: ms since 1970.
    static func summary(_ played: [Item], series: [Item], from: Double) -> Summary {
        let shows = Dictionary(series.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let items = played.filter { ($0.type == "Movie" || $0.type == "Episode") && playedAt($0) >= from }
        var genres: [String: Int] = [:]
        for item in items {
            let kinds = item.isEpisode && item.seriesId.flatMap({ shows[$0] }) != nil ? shows[item.seriesId!]?.genres ?? [] : item.genres ?? []
            for genre in Set(kinds) { genres[genre, default: 0] += minutes(item) }
        }
        var order: [String] = [], groups: [String: [Item]] = [:]
        for item in items where item.isEpisode {
            guard let id = item.seriesId else { continue }
            if groups[id] == nil { order.append(id) }
            groups[id, default: []].append(item)
        }
        func name(_ id: String) -> String {
            if let name = shows[id]?.name, !name.isEmpty { return name }
            return groups[id]?.first?.seriesName ?? String(localized: "Series")
        }
        func latest(_ id: String) -> Double { groups[id]?.map(playedAt).max() ?? 0 }
        var summary = Summary()
        summary.minutes = items.reduce(0) { $0 + minutes($1) }
        summary.films = items.filter { $0.type == "Movie" }.count
        summary.episodes = items.filter(\.isEpisode).count
        summary.finished = order.filter { shows[$0]?.userData?.played == true }.sorted { latest($0) > latest($1) }.map(name)
        summary.genres = genres.sorted { $0.value != $1.value ? $0.value > $1.value : before($0.key, $1.key) }.prefix(3).map(\.key)
        summary.series = order.map { id in
            Series(name: name(id), episodes: groups[id]?.count ?? 0, minutes: groups[id]?.reduce(0) { $0 + minutes($1) } ?? 0)
        }
        .sorted { $0.minutes != $1.minutes ? $0.minutes > $1.minutes : before($0.name, $1.name) }
        .prefix(3).map { $0 }
        return summary
    }

    /// « 45 min », « 3 h », « 12 h 05 ».
    static func time(_ minutes: Int) -> String {
        let m = max(0, minutes)
        if m < 60 { return String(localized: "\(m) min") }
        if m % 60 == 0 { return String(localized: "\(m / 60) h") }
        let rest = m % 60 < 10 ? "0\(m % 60)" : "\(m % 60)"
        return String(localized: "\(m / 60) h \(rest)")
    }
}

extension JellyfinClient {
    /// The films and episodes played to the end since `from` (by their last play), newest first, a few pages at most,
    /// and the series of those episodes (their genres, whether all is watched).
    func playedSince(_ from: Date) async throws -> (played: [Item], series: [Item]) {
        let page = 300, fromMs = from.timeIntervalSince1970 * 1000
        var played: [Item] = []
        while played.count < 3000 {
            let list: ItemList = try await get("Items", [
                "userId": userId, "recursive": "true", "includeItemTypes": "Movie,Episode", "isPlayed": "true", "sortBy": "DatePlayed",
                "sortOrder": "Descending", "fields": "Genres", "enableImages": "false", "enableUserData": "true",
                "enableTotalRecordCount": "false", "startIndex": String(played.count), "limit": String(page),
            ])
            let items = list.items ?? []
            played += items
            let last = items.last?.userData?.lastPlayedDate.flatMap(HomeContent.parseDate).map { $0.timeIntervalSince1970 * 1000 } ?? 0
            if items.count < page || last < fromMs { break }
        }
        let recent = played.filter { ($0.userData?.lastPlayedDate.flatMap(HomeContent.parseDate)?.timeIntervalSince1970 ?? 0) * 1000 >= fromMs }
        var ids: [String] = []
        for id in recent.compactMap({ $0.isEpisode ? $0.seriesId : nil }) where !ids.contains(id) { ids.append(id) }
        var series: [Item] = []
        for start in stride(from: 0, to: ids.count, by: 100) {
            let list: ItemList = try await get("Items", [
                "userId": userId, "ids": ids[start..<min(ids.count, start + 100)].joined(separator: ","), "fields": "Genres",
                "enableImages": "false", "enableUserData": "true",
            ])
            series += list.items ?? []
        }
        return (recent, series)
    }
}

/// Settings → Your recap: this month or this year.
struct ReportView: View {
    @Environment(AppModel.self) private var app
    @State private var year = false
    @State private var summary: WatchReport.Summary?
    @State private var failure: String?

    var body: some View {
        List {
            Section {
                Picker("Period", selection: $year) {
                    Text("This month").tag(false)
                    Text("This year").tag(true)
                }
                .pickerStyle(.segmented)
                .listRowBackground(Color.clear)
                .listRowInsets(EdgeInsets())
            }
            if let failure {
                Section { Text(verbatim: failure).foregroundStyle(Palette.muted) }.listRowBackground(Palette.surface)
            } else if let summary {
                if summary.films == 0 && summary.episodes == 0 {
                    Section {
                        if year {
                            Text("No movies or episodes finished this year yet.").foregroundStyle(Palette.muted)
                        } else {
                            Text("No movies or episodes finished this month yet.").foregroundStyle(Palette.muted)
                        }
                    }
                    .listRowBackground(Palette.surface)
                } else {
                    content(summary)
                }
            } else {
                Section {
                    HStack(spacing: 10) {
                        ProgressView()
                        Text("Putting your recap together…").foregroundStyle(Palette.muted)
                    }
                }
                .listRowBackground(Palette.surface)
            }
        }
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .foldsTabBar()
        .background(Palette.bg)
        .navigationTitle("Your recap")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: year) { await load() }
    }

    @ViewBuilder private func content(_ summary: WatchReport.Summary) -> some View {
        Section {
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: WatchReport.time(summary.minutes)).font(.system(.largeTitle, design: .rounded, weight: .bold)).foregroundStyle(Palette.ink)
                Group {
                    if year { Text("of watching this year") } else { Text("of watching this month") }
                }
                .foregroundStyle(Palette.muted)
                Text(verbatim: counts(summary)).font(MiraFont.footnote).foregroundStyle(Palette.muted).padding(.top, 4)
            }
            .accessibilityElement(children: .combine)
            .padding(.vertical, 6)
        } footer: {
            Text("From Jellyfin: each film or episode watched to the end counts once, at its last play, on all your devices.")
        }
        .listRowBackground(Palette.surface)
        if !summary.series.isEmpty {
            Section {
                ForEach(summary.series, id: \.name) { series in
                    LabeledContent {
                        Text(verbatim: "\(String(localized: "\(series.episodes) episodes")) · \(WatchReport.time(series.minutes))")
                    } label: {
                        Text(verbatim: series.name).foregroundStyle(Palette.ink)
                    }
                }
            } header: {
                Text("Most watched shows")
            }
            .listRowBackground(Palette.surface)
        }
        if !summary.finished.isEmpty {
            Section {
                ForEach(summary.finished, id: \.self) { name in
                    Label { Text(verbatim: name) } icon: { Image(systemName: "checkmark").foregroundStyle(Palette.accent) }
                }
            } header: {
                if summary.finished.count == 1 { Text("Show finished") } else { Text("Shows finished") }
            }
            .listRowBackground(Palette.surface)
        }
        if !summary.genres.isEmpty {
            Section {
                Text(verbatim: summary.genres.joined(separator: " · ")).foregroundStyle(Palette.ink)
            } header: {
                Text("Favorite genres")
            }
            .listRowBackground(Palette.surface)
        }
    }

    private func counts(_ summary: WatchReport.Summary) -> String {
        [summary.films > 0 ? String(localized: "\(summary.films) movies") : nil, summary.episodes > 0 ? String(localized: "\(summary.episodes) episodes") : nil]
            .compactMap { $0 }.joined(separator: " · ")
    }

    private func load() async {
        guard let client = app.client else { return }
        summary = nil
        failure = nil
        let from = WatchReport.start(.now, year: year)
        do {
            let found = try await client.playedSince(from)
            summary = WatchReport.summary(found.played, series: found.series, from: from.timeIntervalSince1970 * 1000)
        } catch is CancellationError {
        } catch {
            failure = Messages.text(error)
        }
    }
}
