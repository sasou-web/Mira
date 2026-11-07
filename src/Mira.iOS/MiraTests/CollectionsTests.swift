import Foundation
import Testing
@testable import Mira

/// Collections made by hand: tests/fixtures/collections.json, shared with Mira for Windows and Mac (TitleCollections
/// of Core): the names refused, a collection's folder, what an account may do.
@Suite("Collections")
struct CollectionsTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "collections", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    @Test("Les noms refusés")
    func names() throws {
        let x = try fixture()
        let collections = try JSON.decoder.decode([Item].self, from: JSONSerialization.data(withJSONObject: x["collections"] ?? []))
        let rows = try #require(x["names"] as? [[Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let name = row[0] as? String
            let refusal = TitleCollections.nameProblem(name, collections, renamed: row[1] as? String)
            #expect(refusal?.problem.rawValue == row[2] as? String, "\(name ?? "null")")
            #expect(refusal?.taken == row[3] as? String, "\(name ?? "null")")
        }
    }

    @Test("Le dossier d'une collection")
    func folders() throws {
        let rows = try #require(try fixture()["folders"] as? [[Any]])
        for row in rows {
            #expect(TitleCollections.folder(row[0] as? String) == row[1] as? String, "\(row[0])")
        }
    }

    @Test("Les droits du compte")
    func rights() throws {
        let rows = try #require(try fixture()["rights"] as? [[Any]])
        for row in rows {
            let policy = try JSON.decoder.decode(AccountInfo.Policy.self, from: JSONSerialization.data(withJSONObject: row[0]))
            let rights = TitleCollections.Rights.from(policy)
            #expect(rights.manage == row[1] as? Bool && rights.rename == row[2] as? Bool, "\(row[0])")
        }
    }
}
