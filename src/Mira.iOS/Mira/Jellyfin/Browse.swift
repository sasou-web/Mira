import Foundation

/// What a grid of the library asks Jellyfin (api.js browse of Mira web): titles of one kind, sorted, filtered, a page
/// at a time.
struct BrowseQuery: Sendable, Hashable {
    enum Sort: String, Sendable, CaseIterable { case recent, title, year, rating, played }

    var types = "Movie,Series"
    var sort = Sort.recent
    var genre: String?
    var year: String?
    var unplayed = false
    var favorite = false
    /// One library of this kind (Shows or Anime), nil for all of them.
    var library: String?
    var search: String?

    var filtered: Bool { genre != nil || year != nil || unplayed || favorite }

    var parameters: [String: String?] {
        let (sortBy, order): (String, String) = switch sort {
        case .title: ("SortName", "Ascending")
        case .year: ("ProductionYear,SortName", "Descending")
        case .rating: ("CommunityRating,SortName", "Descending")
        case .played: ("DatePlayed,SortName", "Descending")
        case .recent: ("DateCreated,SortName", "Descending")
        }
        return [
            "recursive": "true", "includeItemTypes": types, "sortBy": sortBy, "sortOrder": order,
            "genres": genre, "years": year, "isPlayed": unplayed ? "false" : nil, "isFavorite": favorite ? "true" : nil,
            "parentId": library, "searchTerm": search,
        ]
    }
}

/// A library of the account (UserViews): Films, Séries, Animes…
struct Library: Sendable, Hashable, Identifiable {
    let id: String
    let name: String
    let kind: String
}

extension JellyfinClient {
    /// One page of titles, with how many there are in all.
    func browse(_ query: BrowseQuery, start: Int, limit: Int = 60) async throws -> ItemList {
        try await get("Items", query.parameters.merging([
            "userId": userId, "startIndex": String(start), "limit": String(limit), "enableTotalRecordCount": "true",
            "fields": "ChildCount,RecursiveItemCount", "enableImageTypes": "Primary,Backdrop,Thumb,Logo", "imageTypeLimit": "1",
        ]) { $1 })
    }

    /// The account's libraries of films (« movies ») and series (« tvshows »).
    func libraries() async throws -> [Library] {
        let views: ItemList = try await get("UserViews", ["userId": userId])
        return (views.items ?? []).compactMap { view in
            guard let kind = view.collectionType, kind == "movies" || kind == "tvshows" else { return nil }
            return Library(id: view.id, name: view.name ?? "", kind: kind)
        }
    }

    /// The genres of the titles of a kind (Items/Filters2).
    func genres(_ types: String) async throws -> [String] {
        struct Filters: Decodable, Sendable { struct Genre: Decodable, Sendable { var name: String? }; var genres: [Genre]? }
        let filters: Filters = try await get("Items/Filters2", ["userId": userId, "includeItemTypes": types, "recursive": "true"])
        return (filters.genres ?? []).compactMap(\.name)
    }

    /// The years titles of a kind came out, newest first.
    func years(_ types: String) async throws -> [String] {
        let list: ItemList = try await get("Years", [
            "userId": userId, "includeItemTypes": types, "recursive": "true", "enableImages": "false", "sortBy": "SortName", "sortOrder": "Descending",
        ])
        return (list.items ?? []).compactMap(\.name)
    }

    /// Episodes by their own name, for Search (those Jellyfin lists as missing left out).
    func searchEpisodes(_ text: String) async throws -> [Item] {
        try await get("Items", [
            "userId": userId, "recursive": "true", "includeItemTypes": "Episode", "searchTerm": text, "limit": "12", "isMissing": "false",
            "enableImageTypes": "Primary,Backdrop,Thumb", "imageTypeLimit": "1",
        ], as: ItemList.self).items ?? []
    }
}
