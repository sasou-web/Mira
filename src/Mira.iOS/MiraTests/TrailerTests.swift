import Foundation
import Testing
@testable import Mira

/// « Trailer » of a title's page: tests/fixtures/trailers.json, shared with Core (Trailers). Its « first » cases are
/// Windows' and Mac's only (« Trailer before the film »).
@Suite("Bandes-annonces")
struct TrailerTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "trailers", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    @Test("Celle du web seulement en https, ouverte dans Safari")
    func links() throws {
        let rows = try #require(try fixture()["links"] as? [[Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let url = row[0] as? String
            let wanted = row[1] as? String
            #expect(Trailers.webLink(url)?.absoluteString == wanted, "\(url ?? "null")")
        }
    }

    @Test("Le site nommé par le menu")
    func sites() throws {
        let rows = try #require(try fixture()["sites"] as? [[String]])
        for row in rows {
            let link = try #require(URL(string: row[0]))
            #expect(Trailers.site(link) == row[1], "\(row[0])")
        }
    }

    @Test("Celle du serveur d'abord, sinon celle du web, sinon rien")
    func sources() throws {
        let rows = try #require(try fixture()["sources"] as? [[String: Any]])
        for row in rows {
            let described = try #require(row["item"] as? [String: Any])
            var object = described
            object["Id"] = "title"
            let data = try JSONSerialization.data(withJSONObject: object)
            let item = try JSON.decoder.decode(Item.self, from: data)
            let source = Trailers.source(item)
            #expect(source.rawValue == row["source"] as? String, "\(described)")
            if let link = row["link"] as? String { #expect(Trailers.webLink(item)?.absoluteString == link, "\(described)") }
        }
    }
}
