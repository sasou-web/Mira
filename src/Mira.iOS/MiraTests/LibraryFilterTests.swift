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

    @Test("Plus courts d'abord")
    func runtime() throws {
        let sort = try #require(try fixture()["sort"] as? [String: [String: String]])
        let wanted = try #require(sort["runtime"])
        var query = BrowseQuery(types: "Movie")
        query.sort = .runtime
        let parameters = query.parameters
        for (name, value) in wanted {
            #expect((parameters[name] ?? nil) == value, "\(name)")
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
