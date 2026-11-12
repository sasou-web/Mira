import SwiftUI

/// « Nouvel épisode »: the episodes Jellyfin added since the last look, of the series this account follows (one already
/// started, or a favourite), one line for each series, as Mira web says them when it opens (new-episodes.js) and Mira
/// for Windows notifies them (NewEpisodes in Core), with the cases of tests/fixtures/new-episodes.json. The mark is
/// Jellyfin's own date of the newest episode seen, never this iPhone's clock; with it, the episodes the last look listed
/// (`known`): Jellyfin dates an episode by its file, so one can arrive after the mark yet be dated before it (`late`).
/// Said in Mira, never as a notification: nothing leaves the iPhone and Jellyfin.
enum NewEpisodes {
    static let maxNotices = 3

    struct Notice: Sendable, Hashable, Identifiable {
        let seriesId: String
        let title: String
        let text: String
        /// The first of them, to open.
        let episode: Item
        let count: Int
        var id: String { seriesId }
    }

    struct Pick: Sendable, Hashable {
        var notices: [Notice] = []
        var more: String?
        /// How many series in all.
        var series = 0
    }

    /// Jellyfin's date in ms, or nil.
    static func date(_ text: String?) -> Double? { text.flatMap(HomeContent.parseDate).map { $0.timeIntervalSince1970 * 1000 } }

    /// Started (part of it watched: a new episode makes it unwatched again) or in the favourites.
    static func follows(_ series: Item?) -> Bool {
        series?.userData?.isFavorite == true || (series?.userData?.playedPercentage ?? 0) > 0
    }

    /// How much older than the mark (ms) an episode new since the last look may be dated, as `Late` in Core: Jellyfin
    /// dates an episode by its file (its creation time on Windows, kept by a move on the same disk and a hard link), so a
    /// download started before another and finished after it, or a file moved in days later, comes in dated before the
    /// mark. Older than a week, it is not news.
    static let late: Double = 7 * 24 * 3600 * 1000

    /// The ids of the episodes listed, watched or not, dated after the mark (ms) less `late`: what the next look knows
    /// was already there (an episode dated before the mark but not among them came since).
    static func known(_ episodes: [Item], mark: Double) -> [String] {
        var ids: [String] = [], listed = Set<String>()
        for episode in episodes {
            guard let added = date(episode.dateCreated), added > mark - late, listed.insert(episode.id.lowercased()).inserted else { continue }
            ids.append(episode.id)
        }
        return ids
    }

    /// The newest date of these episodes when it is past `seen` (ms, or nil): the next mark.
    static func newest(_ episodes: [Item], seen: Double?) -> Double? {
        var newest = seen
        for episode in episodes {
            if let added = date(episode.dateCreated), newest == nil || added > newest! { newest = added }
        }
        return newest
    }

    private static func label(_ x: Item) -> String {
        let number = x.indexNumber.map { String(localized: "E\(String($0))", comment: "An episode's number: É5 in French") }
        if let season = x.parentIndexNumber { return ["S\(season)", number].compactMap { $0 }.joined(separator: " ") }
        return number ?? ""
    }

    /// « New episode: S2 E5 · The way out », « 3 new episodes: S2 E5 to E7 », « 4 new episodes, starting at S1 E9 ».
    static func text(_ episodes: [Item]) -> String {
        guard let first = episodes.first, let last = episodes.last else { return "" }
        if episodes.count == 1 {
            let line = [label(first), first.name ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
            return String(localized: "New episode: \(line)")
        }
        if first.parentIndexNumber == last.parentIndexNumber, let end = last.indexNumber, first.indexNumber != nil {
            let to = String(localized: "E\(String(end))", comment: "An episode's number: É5 in French")
            return String(localized: "\(episodes.count) new episodes: \(label(first)) to \(to)")
        }
        return String(localized: "\(episodes.count) new episodes, starting at \(label(first))")
    }

    /// The lines for the episodes added after `seen` (ms), not watched yet, of the followed series among `series` (by
    /// id): the most recent series first, three at most, and a line for the rest. Added since the last look: dated
    /// after the mark, or less than `late` before it and not among the episodes that look listed (`known`, nil when it
    /// did not list them all).
    static func pick(_ episodes: [Item], series: [String: Item], seen: Double, known: [String]? = nil) -> Pick {
        let there = known.map { Set($0.map { $0.lowercased() }) }
        var order: [String] = [], groups: [String: [Item]] = [:]
        for x in episodes {
            guard x.isEpisode, let added = date(x.dateCreated),
                  added > seen || (there.map { added > seen - late && !$0.contains(x.id.lowercased()) } ?? false),
                  x.userData?.played != true, let id = x.seriesId, follows(series[id]) else { continue }
            if groups[id] == nil { order.append(id) }
            groups[id, default: []].append(x)
        }
        func newest(_ id: String) -> Double { groups[id]?.compactMap { date($0.dateCreated) }.max() ?? 0 }
        let sorted = order.enumerated().sorted { a, b in
            newest(a.element) != newest(b.element) ? newest(a.element) > newest(b.element) : a.offset < b.offset
        }.map { id in
            (groups[id.element] ?? []).enumerated().sorted { a, b in
                let sa = a.element.parentIndexNumber ?? 0, sb = b.element.parentIndexNumber ?? 0
                if sa != sb { return sa < sb }
                let ea = a.element.indexNumber ?? 0, eb = b.element.indexNumber ?? 0
                return ea != eb ? ea < eb : a.offset < b.offset
            }.map(\.element)
        }
        var pick = Pick(series: sorted.count)
        pick.notices = sorted.prefix(maxNotices).compactMap { group in
            guard let first = group.first, let id = first.seriesId else { return nil }
            let name = series[id]?.name.flatMap { $0.isEmpty ? nil : $0 } ?? first.seriesName ?? ""
            return Notice(seriesId: id, title: name, text: text(group), episode: first, count: group.count)
        }
        let rest = sorted.count - pick.notices.count
        if rest > 0 { pick.more = String(localized: "And \(rest) other shows you follow have new episodes.") }
        return pick
    }
}

extension JellyfinClient {
    /// The most episodes one look reads past the mark (Mira not opened for long, or a whole library added).
    static let mostRecentEpisodes = 2000

    /// The episodes Jellyfin added lately, watched or not, newest first, and their series (whether followed). Read page
    /// by page down to `late` before the mark `since` (ms; before the newest episode on the first look), as
    /// RecentEpisodesAsync in Core: a whole season of another series added at once never hides the episode of a followed
    /// one, and the next look knows every episode there (`NewEpisodes.known`). Whole: listed down to there, not stopped
    /// at the most.
    func recentEpisodes(since: Double? = nil) async throws -> (episodes: [Item], series: [String: Item], whole: Bool) {
        let first = 50, page = 200, most = Self.mostRecentEpisodes
        var episodes: [Item] = [], read = Set<String>(), start = 0, asked = first, whole = false
        var floor = since.map { $0 - NewEpisodes.late }
        while true {
            let found: ItemList = try await get("Items", [
                "userId": userId, "recursive": "true", "includeItemTypes": "Episode", "isMissing": "false",
                "sortBy": "DateCreated", "sortOrder": "Descending", "startIndex": String(start), "limit": String(asked),
                "fields": "DateCreated", "enableImageTypes": "Primary,Thumb", "imageTypeLimit": "1",
            ])
            let items = found.items ?? []
            start += items.count
            let fresh = items.filter { read.insert($0.id.lowercased()).inserted }
            episodes += fresh
            // Without a mark yet, down to a week before the newest episode: the next look must know them all.
            if floor == nil, let newest = NewEpisodes.date(episodes.first?.dateCreated) { floor = newest - NewEpisodes.late }
            // Down to the floor or the end of the list, else enough.
            guard let floor, items.count == asked, start < found.totalRecordCount ?? 0 else { whole = true; break }
            guard let last = NewEpisodes.date(items.last?.dateCreated), !fresh.isEmpty, episodes.count < most else { break }
            if last <= floor { whole = true; break }
            asked = min(page, most - episodes.count)
        }
        var ids: [String] = [], listed = Set<String>()
        for id in episodes.compactMap(\.seriesId) where listed.insert(id).inserted { ids.append(id) }
        // A hundred series a request: a few thousand characters of address, where Jellyfin's server takes 8 KB.
        var series: [String: Item] = [:]
        for offset in stride(from: 0, to: ids.count, by: 100) {
            let chunk = ids[offset..<min(offset + 100, ids.count)]
            let shows: ItemList = try await get("Items", ["userId": userId, "ids": chunk.joined(separator: ","), "enableImages": "false"])
            for show in shows.items ?? [] where show.isSeries && series[show.id] == nil { series[show.id] = show }
        }
        return (episodes, series, whole)
    }
}

/// When Mira opens, or comes back after 15 minutes away: what is new in the series followed, said over the app. The
/// first look only sets the mark; the mark is kept per account and forgotten at sign-out.
@MainActor @Observable
final class NewEpisodesWatch {
    private(set) var pick: NewEpisodes.Pick?
    @ObservationIgnored private var looking = false
    @ObservationIgnored private var lastLook = Date.distantPast

    private static func key(_ account: String) -> String { "mira.home.\(account).episodes" }
    /// The episodes the last look listed (`NewEpisodes.known`), beside the mark.
    private static func knownKey(_ account: String) -> String { "mira.home.\(account).episodes.known" }

    func look(_ client: JellyfinClient, account: String, comingBack: Bool = false) async {
        if looking || comingBack && Date().timeIntervalSince(lastLook) < 15 * 60 { return }
        looking = true
        defer { looking = false }
        let defaults = UserDefaults.standard
        // Read down to the mark: what came since, however much.
        let since = defaults.object(forKey: Self.key(account)) as? Double
        guard let found = try? await client.recentEpisodes(since: since), client.userId == account else { return }
        lastLook = Date()
        let seen = defaults.object(forKey: Self.key(account)) as? Double
        if let seen {
            let known = defaults.stringArray(forKey: Self.knownKey(account))
            let pick = NewEpisodes.pick(found.episodes, series: found.series, seen: seen, known: known)
            if !pick.notices.isEmpty { self.pick = pick }
        }
        // A library without an episode to watch yet: the next one is news.
        let mark = NewEpisodes.newest(found.episodes, seen: seen) ?? 0
        if mark != seen { defaults.set(mark, forKey: Self.key(account)) }
        // And what was there: an episode dated before the mark, but not among them, came since.
        if found.whole { defaults.set(NewEpisodes.known(found.episodes, mark: mark), forKey: Self.knownKey(account)) }
        else { defaults.removeObject(forKey: Self.knownKey(account)) }
    }

    func dismiss() { pick = nil }

    static func forget(account: String) {
        UserDefaults.standard.removeObject(forKey: key(account))
        UserDefaults.standard.removeObject(forKey: knownKey(account))
    }
}

/// The new episodes over the top of the app: one series, its line and See; several, how many and See (a list).
struct NewEpisodesBanner: View {
    @Environment(AppModel.self) private var app
    @State private var list: NewEpisodes.Pick?
    @State private var opened: NewEpisodes.Notice?

    var body: some View {
        // Once Mira's opening is over: under it, the banner would go by unseen.
        let pick = app.opening.phase == .gone ? app.newEpisodes.pick : nil
        ZStack {
            if let pick, let first = pick.notices.first {
                HStack(spacing: 12) {
                    Image(systemName: "sparkles.tv").foregroundStyle(Palette.accent).accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 2) {
                        if pick.notices.count == 1 && pick.more == nil {
                            Text(verbatim: first.title).font(MiraFont.headline).foregroundStyle(Palette.ink).lineLimit(1)
                            Text(verbatim: first.text).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(2)
                        } else {
                            Text("\(pick.series) shows you follow have new episodes.").font(MiraFont.callout).foregroundStyle(Palette.ink)
                        }
                    }
                    Spacer(minLength: 0)
                    Button("See") {
                        if pick.notices.count == 1 && pick.more == nil { opened = first } else { list = pick }
                        app.newEpisodes.dismiss()
                    }
                    .buttonStyle(.glassProminent)
                    .tint(Palette.accent)
                    .accessibilityIdentifier("news.see")
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
                .glassEffect(.regular, in: .rect(cornerRadius: 22))
                .padding(.horizontal, 16)
                .padding(.top, 6)
                .transition(.move(edge: .top).combined(with: .opacity))
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("news")
                .task(id: pick) {
                    try? await Task.sleep(for: .seconds(8))
                    if app.newEpisodes.pick == pick { app.newEpisodes.dismiss() }
                }
            }
        }
        .animation(.spring(duration: 0.35), value: pick)
        .sheet(item: $opened) { notice in
            NavigationStack {
                TitleView(route: TitleRoute(item: notice.episode, source: "news-\(notice.episode.id)"))
                    .navigationDestination(for: TitleRoute.self) { TitleView(route: $0) }
            }
        }
        .sheet(isPresented: Binding(get: { list != nil }, set: { if !$0 { list = nil } })) {
            NavigationStack {
                List(list?.notices ?? []) { notice in
                    NavigationLink(value: TitleRoute(item: notice.episode, source: "news-\(notice.episode.id)")) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(verbatim: notice.title).font(MiraFont.headline).foregroundStyle(Palette.ink)
                            Text(verbatim: notice.text).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                        }
                    }
                    .listRowBackground(Palette.surface)
                }
                .safeAreaInset(edge: .bottom) {
                    if let more = list?.more { Text(verbatim: more).font(MiraFont.footnote).foregroundStyle(Palette.muted).padding() }
                }
                .scrollContentBackground(.hidden)
                .background(Palette.bg)
                .navigationTitle("New episodes")
                .navigationBarTitleDisplayMode(.inline)
                .navigationDestination(for: TitleRoute.self) { TitleView(route: $0) }
            }
            .presentationDetents([.medium, .large])
        }
    }
}
