import Foundation
import Testing
@testable import Mira

/// « Marquer la saison comme vue »: the marks of tests/fixtures/seasons.json, shared with Mira web and Mira for
/// Windows and Mac (a season read as 1 when Jellyfin gives none).
@Suite("Marquer la saison comme vue")
struct SeasonMarkTests {
    private final class Bundled {}

    @Test("Les cas communs")
    func shared() throws {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "seasons", withExtension: "json"))
        let fixture = try #require(try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url)).object)
        guard case let .array(cases)? = fixture["cases"] else { Issue.record("cases"); return }
        for entry in cases {
            guard case let .array(x) = entry, x.count >= 4, case let .array(rows) = x[1], case let .array(marks) = x[3] else { continue }
            let episodes: [(item: Item, season: Int)] = rows.compactMap { row in
                guard case let .array(e) = row, let id = e[0].string else { return nil }
                var item = Item(id: id)
                item.type = "Episode"
                item.seasonId = e[1].string
                item.userData = UserData(played: e[3].bool ?? false)
                return (item, e[2].number.map(Int.init) ?? 1)
            }
            for mark in marks {
                guard case let .array(m) = mark, let season = m[0].number, case let .array(ids) = m[1], let played = m[2].bool else { continue }
                let found = SeasonMark.of(episodes.filter { $0.season == Int(season) }.map(\.item))
                #expect(found == SeasonMark(ids: ids.compactMap(\.string), played: played), "\(x[0].string ?? "") · \(season)")
            }
        }
    }
}
