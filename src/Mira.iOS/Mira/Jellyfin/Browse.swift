import Foundation

/// What a grid of the library asks Jellyfin (api.js browse of Mira web): titles of one kind, sorted, filtered, a page
/// at a time.
struct BrowseQuery: Sendable, Hashable {
    enum Sort: String, Sendable, CaseIterable { case recent, title, year, rating, played, runtime }

    var types = "Movie,Series"
    var sort = Sort.recent
    var genre: String?
    var year: String?
    var unplayed = false
    var favorite = false
    /// One library of this kind (Shows or Anime), nil for all of them.
    var library: String?
    var search: String?
    /// Only the titles of this actor or director (a person's page).
    var person: String?
    /// « 4k », « hd » or « sd » (LibraryFilters, as the next three).
    var quality: String?
    /// A language the title can be heard in, a key of LibraryFilters.languages.
    var audio: String?
    /// « any », « none » or a language.
    var subtitles: String?
    /// The community rating at least, out of 10.
    var minRating: Int?

    var filtered: Bool { genre != nil || year != nil || unplayed || favorite || quality != nil || audio != nil || subtitles != nil || minRating != nil }

    var parameters: [String: String?] {
        // A descending sort gives each key its own order (one order only would be given to the title too: titles of a
        // same year Z → A); « Recently watched » of shows sorts them by their last episode played, a series having no
        // play date of its own (Jellyfin 10.9 to 12.4). As Core's CatalogQuery, cases in tests/fixtures/filters.json.
        let (sortBy, order): (String, String) = switch sort {
        case .title: ("SortName", "Ascending")
        case .year: ("ProductionYear,SortName", "Descending,Ascending")
        case .rating: ("CommunityRating,SortName", "Descending,Ascending")
        case .played: (types == "Series" ? "SeriesDatePlayed,SortName" : "DatePlayed,SortName", "Descending,Ascending")
        case .recent: ("DateCreated,SortName", "Descending,Ascending")
        case .runtime: ("Runtime,SortName", "Ascending")
        }
        let base: [String: String?] = [
            "recursive": "true", "includeItemTypes": types, "sortBy": sortBy, "sortOrder": order,
            "genres": genre, "years": year, "isPlayed": unplayed ? "false" : nil, "isFavorite": favorite ? "true" : nil,
            "parentId": library, "searchTerm": search, "personIds": person,
            // Every film and every series, even on a server set to « Group movies into collections » (or shows, from
            // Jellyfin 12; its dashboard, Libraries → Display): Jellyfin would otherwise list a collection in place of
            // its titles in Movies or Shows, sorted by date added, name, year or last played, and this iPhone has no
            // collection page (a collection plays nothing: PlaybackInfo answers 400). Windows and Mac open its page.
            "collapseBoxSetItems": "false",
        ]
        return base.merging(LibraryFilters.parameters(quality: quality, audio: audio, subtitles: subtitles, minRating: minRating).mapValues { Optional($0) }) { $1 }
    }
}

/// The filters beyond genre, year and progress, as Mira.Core's LibraryFilters (Windows and Mac): picture, audio and
/// subtitle languages, community rating; every one a parameter of Jellyfin's Items request, combined with the others.
/// Cases shared in tests/fixtures/filters.json.
enum LibraryFilters {
    /// The languages offered, with every code Jellyfin may have stored for them (seen on 12.1), all asked: « fre » in a
    /// file's tag, « fra » for a « .fr.srt » beside it, and the regions Jellyfin knows in lower case (« .fr-CA.srt » is
    /// « fr-ca », likewise « es-419 », « zh-cn », « zh-tw », « zh-hk », « zh-hans », « zh-hant »). Other regions
    /// (« .fr-FR.srt », « .en-US.srt ») are stored without a language: no filter can find them.
    static let languages: [(key: String, codes: String)] = [
        ("fr", "fre,fra,fr-ca"), ("en", "eng"), ("ja", "jpn"), ("es", "spa,es-419"), ("de", "ger,deu"), ("it", "ita"), ("ko", "kor"),
        ("zh", "chi,zho,zh-cn,zh-tw,zh-hk,zh-hans,zh-hant"),
    ]
    static let qualities = ["4k", "hd", "sd"]
    static let ratings = [6, 7, 8]

    static func codes(_ key: String?) -> String? { languages.first { $0.key == key }?.codes }

    /// Jellyfin filters by audio and subtitle language from 12 on; 10.11 and before ignore it and would give everything.
    static func languagesSupported(_ version: String?) -> Bool {
        guard let version, let major = version.split(separator: ".").first.flatMap({ Int($0) }) else { return false }
        return major >= 12
    }

    static func parameters(quality: String?, audio: String?, subtitles: String?, minRating: Int?) -> [String: String] {
        var asked: [String: String] = [:]
        switch quality {
        case "4k": asked["is4K"] = "true"
        case "hd": asked["isHd"] = "true"
        case "sd": asked["isHd"] = "false"; asked["is4K"] = "false"
        default: break
        }
        if let spoken = codes(audio) { asked["audioLanguages"] = spoken }
        switch subtitles {
        case "any": asked["hasSubtitles"] = "true"
        case "none": asked["hasSubtitles"] = "false"
        default: if let written = codes(subtitles) { asked["subtitleLanguages"] = written }
        }
        if let minRating { asked["minCommunityRating"] = String(minRating) }
        return asked
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

    /// Actors and directors by their name, for Search: those of the titles this account can see.
    func persons(_ text: String) async throws -> [Item] {
        try await get("Persons", ["userId": userId, "searchTerm": text, "limit": "12", "enableImageTypes": "Primary"], as: ItemList.self).items ?? []
    }

    /// Episodes by their own name, for Search (those Jellyfin lists as missing left out).
    func searchEpisodes(_ text: String) async throws -> [Item] {
        try await get("Items", [
            "userId": userId, "recursive": "true", "includeItemTypes": "Episode", "searchTerm": text, "limit": "12", "isMissing": "false",
            "enableImageTypes": "Primary,Backdrop,Thumb", "imageTypeLimit": "1",
        ], as: ItemList.self).items ?? []
    }
}
