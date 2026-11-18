import Foundation
import Testing
@testable import Mira

/// « La pastille »: tests/fixtures/series-extras.json, shared with Mira for Windows and Mac (SeriesExtras of Core): a
/// series' bonus videos read from their names, sorted by kind, their titles, lines and heading.
@Suite("Bonus d'une série")
struct SeriesExtrasTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "series-extras", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    @Test("Openings et endings lus dans le nom, les genres de Jellyfin jamais")
    func reading() throws {
        for row in try #require(try fixture()["read"] as? [[Any]]) {
            let extra = Item(id: "x", name: row[0] as? String, extraType: row[1] as? String)
            let read = SeriesExtras.reading(extra, show: row[2] as? String)
            #expect(read == SeriesExtras.Reading(kind: row[3] as? String, number: row[4] as? Int, from: row[5] as? String, copy: row[6] as? Int), "\(row[0])")
        }
    }

    @Test("Titre et ligne d'une vignette")
    func titles() throws {
        let x = try fixture()
        for row in try #require(x["title"] as? [[Any]]) {
            let extra = Item(id: "x", name: row[0] as? String, extraType: row[1] as? String)
            #expect(SeriesExtras.title(extra, show: row[2] as? String) == row[3] as? String, "\(row[0])")
        }
        for row in try #require(x["meta"] as? [[Any]]) {
            let extra = Item(id: "x", name: row[0] as? String, runTimeTicks: (row[2] as? NSNumber)?.int64Value, extraType: "Unknown")
            #expect(SeriesExtras.meta(extra, show: row[1] as? String, french: true) == row[3] as? String, "\(row[0])")
            #expect(SeriesExtras.meta(extra, show: row[1] as? String, french: false) == row[4] as? String, "\(row[0])")
        }
    }

    @Test("Rangés par genre, en-tête, pastilles")
    func groups() throws {
        let x = try fixture()
        let extras = try JSON.decoder.decode([Item].self, from: JSONSerialization.data(withJSONObject: x["extras"] ?? []))
        let expected = try #require(x["groups"] as? [[Any]])
        for (french, at) in [(true, 1), (false, 2)] {
            let groups = SeriesExtras.groups(extras, show: "Monogatari Series", french: french)
            #expect(groups.map(\.id) == expected.map { $0[0] as? String ?? "" })
            for (group, row) in zip(groups, expected) {
                #expect(group.name == row[at] as? String)
                #expect(group.items.map(\.id) == row[3] as? [String], "\(group.id)")
            }
        }
        let heading = try #require(x["heading"] as? [String])
        #expect(SeriesExtras.heading(extras, french: true) == heading[0])
        #expect(SeriesExtras.heading(extras, french: false) == heading[1])
        for row in try #require(x["pills"] as? [[Any]]) {
            #expect(SeriesExtras.pills(seasons: row[0] as! Int, extras: row[1] as! Int) == row[2] as? Bool, "\(row)")
        }
        #expect(SeriesExtras.shown(48, columns: 2, open: false) == 2 && SeriesExtras.shown(48, columns: 2, open: true) == 48)
    }
}
