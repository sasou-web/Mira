import Foundation
import Testing
@testable import Mira

/// « Accueil à ta main »: tests/fixtures/home.json, shared with Mira web and Mira for Windows and Mac.
@Suite("Accueil à ta main")
struct HomeLayoutTests {
    private final class Bundled {}

    private func fixture() throws -> [String: JSONValue] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "home", withExtension: "json"))
        return try #require(try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url)).object)
    }

    private func libraries(_ fixture: [String: JSONValue]) -> [HomeLibrary] {
        guard case let .array(list)? = fixture["libraries"] else { return [] }
        return list.compactMap { x in
            guard let o = x.object, let id = o["Id"]?.string else { return nil }
            return HomeLibrary(id: id, name: o["Name"]?.string ?? "", collectionType: o["CollectionType"]?.string)
        }
    }

    @Test("Les rangées, leur ordre, déplacées, montrées")
    func shared() throws {
        let fixture = try fixture()
        let libraries = libraries(fixture)
        guard case let .array(rows)? = fixture["rows"], case let .array(moves)? = fixture["moves"], case let .array(shows)? = fixture["shows"] else {
            Issue.record("fixture")
            return
        }
        for row in rows {
            guard case let .array(x) = row, let saved = x[0].string, case let .array(expected) = x[1] else { continue }
            let found = HomeLayout.rows(saved, libraries: libraries).map { ($0.shown ? "" : "-") + $0.key }
            #expect(found == expected.compactMap(\.string), "« \(saved) »")
        }
        for move in moves {
            guard case let .array(x) = move, let saved = x[0].string, let key = x[1].string, let step = x[2].number, let after = x[3].string else { continue }
            #expect(HomeLayout.move(saved, libraries: libraries, key: key, step: Int(step)) == after)
        }
        for show in shows {
            guard case let .array(x) = show, let saved = x[0].string, let key = x[1].string, let shown = x[2].bool, let after = x[3].string else { continue }
            #expect(HomeLayout.show(saved, libraries: libraries, key: key, shown: shown) == after)
        }
    }
}
