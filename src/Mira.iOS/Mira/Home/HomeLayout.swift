import SwiftUI

/// A library as Home offers it a row of its own (UserViews).
struct HomeLibrary: Codable, Sendable, Hashable {
    let id: String
    let name: String
    let collectionType: String?
}

/// « Accueil à ta main »: which rows Home shows, in which order, as Mira web (home-rows.js) and Windows and Mac
/// (HomeLayout in Core), with the cases of tests/fixtures/home.json. Kept as one line on this iPhone,
/// « continue,favorites,-latest,library:id »: the order, a « - » before a row put away. Rows not in it yet (a new
/// library) come after: the built-in rows shown, a library's own row put away.
enum HomeLayout {
    static let continueKey = "continue", latestKey = "latest", favoritesKey = "favorites", collectionsKey = "collections", libraryPrefix = "library:"
    /// Rows the rules know (the line stays the same as elsewhere) but this iPhone does not show yet: Collections, until
    /// it has a page for a collection. Left out of Home and of Settings.
    static let notOnIPhone: Set<String> = [collectionsKey]

    struct Row: Sendable, Hashable, Identifiable {
        let key: String
        let title: String
        var shown: Bool
        /// The library of « New in … ».
        let library: String?
        var id: String { key }
    }

    /// The libraries that get a row of their own: films, series and mixed ones, not Jellyfin's collections.
    static func offered(_ library: HomeLibrary) -> Bool {
        [nil, "movies", "tvshows", "mixed"].contains(library.collectionType)
    }

    static func rows(_ saved: String, libraries: [HomeLibrary]) -> [Row] {
        let all = [
            Row(key: continueKey, title: String(localized: "Continue watching"), shown: true, library: nil),
            Row(key: latestKey, title: String(localized: "Recently added"), shown: true, library: nil),
            Row(key: favoritesKey, title: String(localized: "Your favorites"), shown: true, library: nil),
            Row(key: collectionsKey, title: "Collections", shown: true, library: nil),
        ] + libraries.filter(offered).map { Row(key: libraryPrefix + $0.id, title: String(localized: "New in \($0.name)"), shown: false, library: $0.id) }
        var rows: [Row] = []
        for part in saved.split(separator: ",").map({ $0.trimmingCharacters(in: .whitespaces) }) where !part.isEmpty {
            let hidden = part.hasPrefix("-"), key = hidden ? String(part.dropFirst()) : part
            // A library gone from Jellyfin, a row of a later version: left out, not kept as an empty place.
            guard var row = all.first(where: { $0.key == key }), !rows.contains(where: { $0.key == key }) else { continue }
            row.shown = !hidden
            rows.append(row)
        }
        rows += all.filter { row in !rows.contains { $0.key == row.key } }
        return rows
    }

    static func save(_ rows: [Row]) -> String { rows.map { ($0.shown ? "" : "-") + $0.key }.joined(separator: ",") }

    /// The row moved up (-1) or down (+1); as it was at either end.
    static func move(_ saved: String, libraries: [HomeLibrary], key: String, step: Int) -> String {
        var rows = rows(saved, libraries: libraries)
        guard let index = rows.firstIndex(where: { $0.key == key }) else { return save(rows) }
        let target = index + step.signum()
        guard rows.indices.contains(target) else { return save(rows) }
        rows.swapAt(index, target)
        return save(rows)
    }

    static func show(_ saved: String, libraries: [HomeLibrary], key: String, shown: Bool) -> String {
        save(rows(saved, libraries: libraries).map { row in
            var row = row
            if row.key == key { row.shown = shown }
            return row
        })
    }
}

extension JellyfinClient {
    /// The account's libraries, every kind (Home keeps those HomeLayout offers).
    func homeLibraries() async throws -> [HomeLibrary] {
        let views: ItemList = try await get("UserViews", ["userId": userId])
        return (views.items ?? []).map { HomeLibrary(id: $0.id, name: $0.name ?? "", collectionType: $0.collectionType) }
    }
}

/// Settings → Home rows: their order, and which show, on this iPhone.
struct HomeRowsView: View {
    @Environment(AppModel.self) private var app
    @State private var libraries: [HomeLibrary] = []
    @State private var loaded = false

    var body: some View {
        let rows = HomeLayout.rows(app.preferences.homeRows, libraries: libraries).filter { !HomeLayout.notOnIPhone.contains($0.key) }
        List {
            Section {
                ForEach(rows) { row in
                    Toggle(isOn: Binding(get: { row.shown }, set: { shown in
                        app.preferences.homeRows = HomeLayout.show(app.preferences.homeRows, libraries: libraries, key: row.key, shown: shown)
                    })) {
                        Text(verbatim: row.title).font(MiraFont.body).foregroundStyle(Palette.ink)
                    }
                    .accessibilityIdentifier("homeRows.\(row.key)")
                    .accessibilityActions {
                        Button("Move up") { move(row.key, -1) }
                        Button("Move down") { move(row.key, 1) }
                    }
                }
                .onMove { from, to in
                    var moved = rows
                    moved.move(fromOffsets: from, toOffset: to)
                    // The rows not shown here keep their place among the others.
                    let all = HomeLayout.rows(app.preferences.homeRows, libraries: libraries)
                    for (index, row) in all.enumerated() where HomeLayout.notOnIPhone.contains(row.key) { moved.insert(row, at: min(index, moved.count)) }
                    app.preferences.homeRows = HomeLayout.save(moved)
                }
            } footer: {
                if loaded {
                    Text("Drag a row to move it. A library’s row shows what was added to it lately.")
                } else {
                    Text("Reading your libraries…")
                }
            }
            .listRowBackground(Palette.surface)
        }
        .environment(\.editMode, .constant(.active))
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .foldsTabBar()
        .background(Palette.bg)
        .navigationTitle("Home rows")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if let client = app.client, let found = try? await client.homeLibraries() { libraries = found }
            loaded = true
        }
    }

    /// Past the rows this iPhone does not show, to the next one seen.
    private func move(_ key: String, _ step: Int) {
        var line = app.preferences.homeRows
        while true {
            let before = HomeLayout.rows(line, libraries: libraries)
            guard let index = before.firstIndex(where: { $0.key == key }), before.indices.contains(index + step.signum()) else { break }
            line = HomeLayout.move(line, libraries: libraries, key: key, step: step)
            if !HomeLayout.notOnIPhone.contains(before[index + step.signum()].key) { break }
        }
        app.preferences.homeRows = line
    }
}
