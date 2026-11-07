import Foundation
import SwiftUI

/// Collections made by hand (« Movie nights », « To show Léa »), beside the sagas Jellyfin makes, as Windows and Mac
/// make them (TitleCollections of Core; the rules in tests/fixtures/collections.json). Both are Jellyfin's collections:
/// a list of titles kept in its data folder (« Soirées [boxset] »), never a file moved or copied. Jellyfin lets an
/// administrator make and fill them, or an account given « Manage collections » (EnableCollectionManagement).
enum TitleCollections {
    /// The longest name Mira gives a collection.
    static let maxName = 100

    /// Why a name is refused (`nameProblem`): `taken` names the collection that has it.
    enum Problem: String, Sendable { case empty, long, unusable, taken }
    struct Refusal: Equatable, Sendable, Error {
        let problem: Problem
        var taken: String?

        var text: String {
            switch problem {
            case .empty: String(localized: "Give the collection a name.")
            case .long: String(localized: "This name is too long: \(TitleCollections.maxName) characters at most.")
            case .unusable: String(localized: "This name can’t be used: put letters or numbers in it.")
            case .taken: String(localized: "This name is already taken by the collection “\(taken ?? "")”.")
            }
        }
    }

    /// What this account may do with collections: make, fill and empty them; rename them (an administrator).
    struct Rights: Equatable, Sendable {
        let manage: Bool
        let rename: Bool
        static let none = Rights(manage: false, rename: false)

        /// From the policy of Users/Me: an administrator, or an account allowed to manage collections (Jellyfin 10.9 and later).
        static func from(_ policy: AccountInfo.Policy?) -> Rights {
            let admin = policy?.isAdministrator == true
            return Rights(manage: admin || policy?.enableCollectionManagement == true, rename: admin)
        }
    }

    /// The titles a collection takes from a card's menu or a page: films and series.
    static func holds(_ item: Item) -> Bool { item.type == "Movie" || item.type == "Series" }

    /// A name as typed, without the spaces around it.
    static func clean(_ name: String?) -> String { (name ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }

    /// Why a new name is refused, or nil. Jellyfin keeps a collection in a folder named after it, its forbidden
    /// characters made spaces, and a second collection of the same folder takes the place of the first, which loses its
    /// titles (checked on 12.1): a name is refused when its folder, or its name, is already another collection's,
    /// whatever the case. `renamed`: the id of the one being renamed, which may keep its own.
    static func nameProblem(_ name: String?, _ collections: [Item], renamed: String? = nil) -> Refusal? {
        let typed = Self.clean(name)
        if typed.isEmpty { return Refusal(problem: .empty) }
        if typed.utf16.count > maxName { return Refusal(problem: .long) }
        let wanted = Self.key(typed)
        if wanted.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return Refusal(problem: .unusable) }
        for other in collections {
            if let renamed, other.id.caseInsensitiveCompare(renamed) == .orderedSame { continue }
            if Self.key(other.name ?? "") == wanted || Self.folder(other.path).map(Self.key) == wanted { return Refusal(problem: .taken, taken: other.name) }
        }
        return nil
    }

    /// The name of a collection's folder without « [boxset] » (the last part of its path, from Windows or not); nil without one.
    static func folder(_ path: String?) -> String? {
        guard let path, !path.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        var trimmed = Substring(path)
        while let last = trimmed.last, last == "/" || last == "\\" { trimmed = trimmed.dropLast() }
        let name = trimmed.split(omittingEmptySubsequences: false, whereSeparator: { $0 == "/" || $0 == "\\" }).last.map(String.init) ?? ""
        let suffix = " [boxset]"
        return name.lowercased().hasSuffix(suffix) ? String(name.dropLast(suffix.count)) : name
    }

    /// What two names that would share a folder have in common: Jellyfin's forbidden characters made spaces, lower case.
    static func key(_ name: String) -> String {
        var scalars = String.UnicodeScalarView()
        for scalar in name.precomposedStringWithCanonicalMapping.unicodeScalars {
            scalars.append(scalar.value < 32 || "\"<>|:*?\\/".unicodeScalars.contains(scalar) ? " " : scalar)
        }
        return String(scalars).lowercased()
    }

    /// Collections by name, as a menu lists them.
    static func sorted(_ collections: [Item]) -> [Item] {
        collections.sorted { ($0.name ?? "").localizedCaseInsensitiveCompare($1.name ?? "") == .orderedAscending }
    }

    static func added(_ collection: String) -> String { String(localized: "Added to “\(collection)”.") }
    static func removed(_ collection: String) -> String { String(localized: "Removed from “\(collection)”.") }
    /// Said when Jellyfin refuses: this account may not manage collections.
    static var refused: String {
        String(localized: "Your Jellyfin account isn’t allowed to manage collections: its administrator can allow it (Dashboard → Users).")
    }
}

extension JellyfinClient {
    /// Every collection this account sees, sagas and those made by hand, with their folder (TitleCollections.nameProblem).
    func collections() async throws -> [Item] {
        let list: ItemList = try await get("Items", [
            "userId": userId, "recursive": "true", "includeItemTypes": "BoxSet", "sortBy": "SortName", "limit": "500",
            "fields": "ChildCount,Path", "enableImages": "false", "enableUserData": "false",
        ])
        return list.items ?? []
    }

    /// The ids of the collections holding a title; nil when Jellyfin cannot say (404: older than 12.1).
    func collections(holding id: String) async throws -> Set<String>? {
        do {
            let list: ItemList = try await get("Items/\(id)/Collections", ["userId": userId, "enableImages": "false"])
            return Set((list.items ?? []).filter { $0.type == "BoxSet" }.map(\.id))
        } catch JellyfinError.http(404) {
            return nil
        }
    }

    /// A new collection holding `ids`: its id. The name is checked first against those already there: Jellyfin would
    /// put a second collection of the same folder in the first's place.
    func createCollection(_ name: String, ids: [String]) async throws -> String {
        let clean = TitleCollections.clean(name)
        let existing = try await collections()
        if let refusal = TitleCollections.nameProblem(clean, existing) { throw refusal }
        var query: [String: String?] = ["name": clean]
        if !ids.isEmpty { query["ids"] = ids.joined(separator: ",") }
        let data = try await send("POST", "Collections", query)
        struct Made: Decodable { var id: String? }
        guard let id = (try? JSON.decoder.decode(Made.self, from: data))?.id, !id.isEmpty else { throw JellyfinError.notJellyfin }
        return id
    }

    /// Titles put in a collection, or taken out of it (the titles themselves stay where they are).
    func setInCollection(_ collection: String, ids: [String], inside: Bool) async throws {
        try await send(inside ? "POST" : "DELETE", "Collections/\(collection)/Items", ["ids": ids.joined(separator: ",")])
    }
}

/// « Add to a collection… » of a film or a series, as on Windows and Mac: the collections that hold it ticked (a touch
/// takes it out), or a new one named with it. A name is refused before Jellyfin sees it when it is already a
/// collection's name or folder.
struct CollectionSheet: View {
    let item: Item
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var collections: [Item] = []
    /// The collections holding the title; nil while unknown (Jellyfin 10.9 and 10.10 cannot say).
    @State private var holding: Set<String>?
    @State private var loaded = false
    @State private var name = ""
    @State private var status = ""
    @State private var failed = false
    @State private var busy = false
    @FocusState private var typing: Bool

    var body: some View {
        NavigationStack {
            List {
                Section {
                    if !loaded {
                        HStack(spacing: 10) {
                            ProgressView()
                            Text("Jellyfin is looking for your collections…").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                        }
                    } else if collections.isEmpty {
                        Text("No collections yet: name the first one.").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    }
                    ForEach(TitleCollections.sorted(collections), id: \.id) { collection in row(collection) }
                } header: {
                    Text(verbatim: item.name ?? "")
                } footer: {
                    Text("It goes in the ticked collections: nothing is copied or moved.")
                }
                Section {
                    HStack(spacing: 10) {
                        TextField("Movie nights, To show Léa…", text: $name)
                            .focused($typing)
                            .submitLabel(.done)
                            .onSubmit { Task { await create() } }
                            .accessibilityLabel(Text("Collection name"))
                            .accessibilityIdentifier("collection.name")
                        Button("Create") { Task { await create() } }
                            .buttonStyle(.glassProminent)
                            .disabled(busy || TitleCollections.clean(name).isEmpty)
                            .accessibilityIdentifier("collection.create")
                    }
                } header: {
                    Text("Or a new collection with it")
                } footer: {
                    if !status.isEmpty {
                        Text(verbatim: status).foregroundStyle(failed ? Palette.danger : Palette.muted).accessibilityIdentifier("collection.status")
                    }
                }
                .onChange(of: name) { if failed { say("") } }
            }
            .scrollContentBackground(.hidden)
            .background(Palette.bg)
            .navigationTitle(Text("Add to a collection"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }.accessibilityIdentifier("collection.done")
                }
            }
        }
        .presentationDetents([.medium, .large])
        .task { await load() }
    }

    /// A collection: its name and count, ticked when it holds the title; a touch puts the title in or takes it out.
    private func row(_ collection: Item) -> some View {
        let inside = holding?.contains(collection.id) == true
        let count = (collection.childCount ?? 0) > 0 ? String(localized: "\(collection.childCount ?? 0) titles") : String(localized: "Empty")
        return Button {
            Task { await toggle(collection, inside: inside) }
        } label: {
            HStack(spacing: 12) {
                Image(systemName: "rectangle.stack").foregroundStyle(Palette.muted).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(verbatim: collection.name ?? "").font(MiraFont.callout).foregroundStyle(Palette.ink)
                    Text(verbatim: count).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
                Spacer(minLength: 0)
                Image(systemName: "checkmark").font(.body.weight(.semibold)).foregroundStyle(Palette.ink).opacity(inside ? 1 : 0).accessibilityHidden(true)
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .disabled(busy)
        .accessibilityAddTraits(inside ? .isSelected : [])
        .accessibilityIdentifier("collection.row.\(collection.name ?? "")")
    }

    private func load() async {
        guard let client = app.client else { return }
        do {
            async let all = client.collections()
            async let held = client.collections(holding: item.id)
            collections = try await all
            holding = try await held
            loaded = true
            // Jellyfin 10.9 and 10.10 cannot say which collections hold a title: a touch adds it, again harmlessly.
            if holding == nil, !collections.isEmpty { say(String(localized: "Your Jellyfin doesn’t say which collections it’s already in.")) }
        } catch JellyfinError.unauthorized {
            dismiss()
            app.sessionEnded()
        } catch {
            loaded = true
            say(Messages.text(error), failed: true)
        }
    }

    private func toggle(_ collection: Item, inside: Bool) async {
        guard let client = app.client, !busy else { return }
        busy = true
        defer { busy = false }
        do {
            try await client.setInCollection(collection.id, ids: [item.id], inside: !inside)
            var held = holding ?? []
            if inside { held.remove(collection.id) } else { held.insert(collection.id) }
            holding = held
            if let at = collections.firstIndex(where: { $0.id == collection.id }) {
                collections[at].childCount = max(0, (collections[at].childCount ?? 0) + (inside ? -1 : 1))
            }
            say(inside ? TitleCollections.removed(collection.name ?? "") : TitleCollections.added(collection.name ?? ""))
        } catch {
            fail(error)
        }
    }

    /// The name typed: refused at once while it cannot be one, then sent (Jellyfin's list is read again before). The new
    /// collection comes in the list, ticked.
    private func create() async {
        guard let client = app.client, !busy else { return }
        let clean = TitleCollections.clean(name)
        if let refusal = TitleCollections.nameProblem(clean, collections) {
            say(refusal.text, failed: true)
            return
        }
        busy = true
        defer { busy = false }
        do {
            let id = try await client.createCollection(clean, ids: [item.id])
            var made = Item(id: id, name: clean, type: "BoxSet")
            made.childCount = 1
            collections.append(made)
            var held = holding ?? []
            held.insert(id)
            holding = held
            name = ""
            typing = false
            say(String(localized: "Collection “\(clean)” created with “\(item.name ?? "")”."))
        } catch {
            fail(error)
        }
    }

    private func fail(_ error: Error) {
        switch error {
        case let refusal as TitleCollections.Refusal: say(refusal.text, failed: true)
        case let failure as JellyfinError where failure == .forbidden: say(TitleCollections.refused, failed: true)
        case let failure as JellyfinError where failure == .unauthorized:
            dismiss()
            app.sessionEnded()
        default: say(Messages.text(error), failed: true)
        }
    }

    private func say(_ text: String, failed: Bool = false) {
        status = text
        self.failed = failed
    }
}
