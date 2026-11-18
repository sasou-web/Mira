import Foundation
import Testing
@testable import Mira

/// « L'étagère »: tests/fixtures/shelf.json, shared with Mira for Windows and Mac (CollectionShelf of Core): a
/// collection grouped or not, the choices kept on Jellyfin, the watch order, the cards in a page of the library.
@Suite("Collections groupées")
struct ShelfTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "shelf", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    private func titles(_ x: [String: Any]) throws -> [String: Item] {
        let items = try JSON.decoder.decode([Item].self, from: JSONSerialization.data(withJSONObject: x["titles"] ?? []))
        return Dictionary(uniqueKeysWithValues: items.map { ($0.id, $0) })
    }

    @Test("Regroupée : une saga de TMDb d'elle-même, sinon le choix du compte")
    func grouped() throws {
        let rows = try #require(try fixture()["grouped"] as? [[Any]])
        for row in rows {
            let box = Item(id: "b", type: "BoxSet", providerIds: row[0] as? [String: String])
            let choice = (row[1] as? Bool).map { CollectionShelf.Choice(grouped: $0) }
            #expect(CollectionShelf.grouped(box, choice) == (row[2] as? Bool), "\(row)")
        }
    }

    @Test("Choix relus et écrits comme Windows et Mac les écrivent")
    func choices() throws {
        let rows = try #require(try fixture()["read"] as? [[String]])
        for row in rows {
            #expect(CollectionShelf.write(CollectionShelf.read(.string(row[0]))) == row[1], "\(row[0])")
        }
    }

    @Test("Ordre de sortie, ordre de visionnage, un titre déplacé, à suivre, barre, faits")
    func order() throws {
        let x = try fixture()
        let all = try titles(x)
        let release = try #require(x["release"] as? [String])
        let list = release.compactMap { all[$0] }
        let shuffled = Array(list.reversed())
        #expect(CollectionShelf.byRelease(shuffled).map(\.id) == release)
        for row in try #require(x["order"] as? [[Any]]) {
            #expect(CollectionShelf.inOrder(shuffled, row[0] as? [String]).map(\.id) == row[1] as? [String], "\(row[0])")
        }
        for row in try #require(x["move"] as? [[Any]]) {
            #expect(CollectionShelf.move(list, row[0] as! Int, row[1] as! Int) == row[2] as? [String], "\(row)")
        }
        for row in try #require(x["upNext"] as? [[Any]]) {
            #expect(CollectionShelf.upNext(CollectionShelf.inOrder(list, row[0] as? [String])) == row[1] as? Int, "\(row[0])")
        }
        let share = try #require(x["share"] as? Double)
        #expect(abs(CollectionShelf.share(list) - share) < 1e-6)
        let facts = try #require(x["facts"] as? [String])
        #expect(CollectionShelf.facts(list, french: true) == facts[0])
        #expect(CollectionShelf.facts(list, french: false) == facts[1])
    }

    @Test("La carte prend la place des titres d'une page, une fois, dès deux titres du genre de la page")
    func collapse() throws {
        let x = try fixture()
        let all = try titles(x)
        let shelves = try #require(x["shelves"] as? [String: [String]])
        let index = CollectionShelf.Index(shelves.map { id, titles in
            CollectionShelf.Shelf(box: Item(id: id, name: id, type: "BoxSet"), titles: titles.compactMap { all[$0] })
        })
        for row in try #require(x["collapse"] as? [[Any]]) {
            var placed = Set(try #require(row[2] as? [String]))
            let page = try #require(row[1] as? [String]).compactMap { all[$0] }
            let shown = CollectionShelf.collapse(page, index, try #require(row[0] as? String), placed: &placed)
            #expect(shown.map(\.id) == row[3] as? [String], "\(row[1])")
        }
    }
}
