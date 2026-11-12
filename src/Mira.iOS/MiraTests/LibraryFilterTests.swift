import Foundation
import Testing
@testable import Mira

/// Filters of Films and Shows beyond genre, year and progress: tests/fixtures/filters.json, shared with Core
/// (LibraryFilters, CatalogQuery).
@Suite("Filtres de la bibliothèque")
struct LibraryFilterTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "filters", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    private static let names = ["is4K", "isHd", "audioLanguages", "subtitleLanguages", "hasSubtitles", "minCommunityRating"]

    @Test("4K, HD, langues, sous-titres et note dans la requête de Jellyfin, ensemble")
    func queries() throws {
        let rows = try #require(try fixture()["queries"] as? [[String: Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let choices = try #require(row["choices"] as? [String: Any])
            let wanted = try #require(row["parameters"] as? [String: String])
            var query = BrowseQuery(types: "Movie")
            query.quality = choices["quality"] as? String
            query.audio = choices["audio"] as? String
            query.subtitles = choices["subtitles"] as? String
            query.minRating = choices["rating"] as? Int
            var asked: [String: String] = [:]
            for (name, value) in query.parameters where Self.names.contains(name) {
                if let value { asked[name] = value }
            }
            #expect(asked == wanted, "\(choices)")
            #expect(query.filtered == !choices.isEmpty, "\(choices)")
        }
    }

    @Test("Tris : plus courts d'abord, ex æquo de A à Z, séries par leur dernier épisode vu")
    func sorts() throws {
        let rows = try #require(try fixture()["sort"] as? [[String: String]])
        #expect(!rows.isEmpty)
        for row in rows {
            let types = try #require(row["types"])
            let sort = try #require(row["sort"])
            var query = BrowseQuery(types: types)
            query.sort = try #require(BrowseQuery.Sort(rawValue: sort))
            let parameters = query.parameters
            for name in ["sortBy", "sortOrder"] {
                #expect((parameters[name] ?? nil) == row[name], "\(row) → \(name)")
            }
        }
    }

    /// Jellyfin 12.1 with « Group movies into collections » (or shows) on lists a collection in place of its titles
    /// unless asked not to, for Movie or Series alone sorted by date added, name, year or last played.
    @Test("Chaque film et chaque série, même sur un serveur qui les regroupe en collections")
    func noCollections() {
        for types in ["Movie", "Series", "Movie,Series"] {
            for sort in BrowseQuery.Sort.allCases {
                var query = BrowseQuery(types: types)
                query.sort = sort
                #expect((query.parameters["collapseBoxSetItems"] ?? nil) == "false", "\(types) \(sort)")
                query.audio = "fr"
                #expect((query.parameters["collapseBoxSetItems"] ?? nil) == "false", "\(types) \(sort) audio")
            }
        }
    }

    /// Home's rows too: the latest titles (of every library, of one) and the favourites.
    @Test("Les rangées de l'Accueil : chaque film et chaque série, même sur un serveur qui les regroupe en collections")
    func homeRowsNoCollections() {
        for favorites in [false, true] {
            for library in [nil, "library-1"] as [String?] {
                let asked = JellyfinClient.homeRow(favorites: favorites, library: library, limit: 24)
                #expect((asked["collapseBoxSetItems"] ?? nil) == "false", "\(favorites) \(library ?? "all")")
                #expect((asked["parentId"] ?? nil) == library && (asked["isFavorite"] ?? nil) == (favorites ? "true" : nil))
            }
        }
    }

    @Test("Langues seulement avec Jellyfin 12 ou plus")
    func languages() throws {
        let rows = try #require(try fixture()["languages"] as? [[Any]])
        for row in rows {
            let version = row[0] as? String
            let wanted = try #require(row[1] as? Bool)
            #expect(LibraryFilters.languagesSupported(version) == wanted, "\(version ?? "null")")
        }
        let offered = try #require(try fixture()["offered"] as? [String: Any])
        #expect(LibraryFilters.languages.map(\.key) == offered["audio"] as? [String])
        #expect(LibraryFilters.ratings == offered["ratings"] as? [Int])
    }
}
