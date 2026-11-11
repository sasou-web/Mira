import Foundation
import Testing
@testable import Mira

/// A title in several files: tests/fixtures/parts.json, shared with Core (MovieParts).
@Suite("Film en plusieurs fichiers")
struct MoviePartsTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "parts", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    private func item(_ object: [String: Any], id: String) throws -> Item {
        var object = object
        object["Id"] = id
        return try JSON.decoder.decode(Item.self, from: JSONSerialization.data(withJSONObject: object))
    }

    /// The title and its other parts, each with the progress the case gives it (« UserData » of Jellyfin).
    private func parts(_ title: [String: Any], _ others: [[String: Any]]) throws -> MovieParts {
        let first = try item(["Type": "Movie", "PartCount": others.count + 1, "UserData": title], id: "part-0")
        let rest = try others.enumerated().map { try item(["Type": "Video", "UserData": $0.element], id: "part-\($0.offset + 1)") }
        return MovieParts(title: first, others: rest)
    }

    @Test("Seuls un film ou un épisode de plusieurs fichiers ont des parties à demander")
    func has() throws {
        let rows = try #require(try fixture()["has"] as? [[String: Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let described = try #require(row["item"] as? [String: Any])
            let title = try item(described, id: "title")
            #expect(MovieParts.has(title) == (row["has"] as? Bool), "\(described)")
        }
    }

    @Test("« Lecture » reprend dans la partie laissée en cours")
    func start() throws {
        let rows = try #require(try fixture()["start"] as? [[String: Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let title = try #require(row["title"] as? [String: Any])
            let others = try #require(row["parts"] as? [[String: Any]])
            let fromStart = try #require(row["fromStart"] as? Bool)
            let wanted = try #require(row["start"] as? Int)
            let first = try parts(title, others).start(fromStart: fromStart)
            #expect(first.id == "part-\(wanted)", "\(row["why"] ?? "")")
        }
    }

    @Test("Chaque partie suit la précédente, rien après la dernière")
    func after() throws {
        let after = try #require(try fixture()["after"] as? [String: Any])
        let count = try #require(after["parts"] as? Int)
        let list = try parts([:], Array(repeating: [:], count: count - 1))
        for case let pair as [Any] in try #require(after["cases"] as? [Any]) {
            let at = try #require(pair[0] as? Int)
            let wanted = pair[1] as? Int
            #expect(list.after("part-\(at)")?.id == wanted.map { "part-\($0)" }, "\(pair)")
        }
        #expect(list.holds("part-0") && list.holds("part-\(count - 1)") && !list.holds("part-\(count)"))
    }

    @Test("« Partie 2 sur 3 » sous le titre")
    func labels() throws {
        let rows = try #require(try fixture()["labels"] as? [[String: Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let count = try #require(row["parts"] as? Int)
            let at = try #require(row["at"] as? Int)
            let list = try parts([:], Array(repeating: [:], count: count - 1))
            #expect(list.label("part-\(at)", french: row["lang"] as? String == "fr") == row["label"] as? String, "\(row)")
        }
    }

    @Test("La partie déjà donnée par Jellyfin comme titre n'est pas jouée deux fois")
    func titleNotTwice() throws {
        let title = try item(["Type": "Movie", "PartCount": 2], id: "title")
        let second = try item(["Type": "Video"], id: "second")
        let list = MovieParts(title: title, others: [title, second])
        #expect(list.parts.map(\.id) == ["title", "second"])
    }
}
