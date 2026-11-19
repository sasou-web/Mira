import Foundation
import Testing
@testable import Mira

/// The pills' season names: the cases of tests/fixtures/season-names.json, shared with Windows and the Mac.
@Suite("Noms des saisons")
struct SeasonNamesTests {
    private final class Bundled {}

    @Test("Les cas communs")
    func shared() throws {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "season-names", withExtension: "json"))
        let fixture = try #require(try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url)).object)
        guard case let .array(cases)? = fixture["cases"] else { Issue.record("cases"); return }
        for entry in cases {
            guard case let .array(x) = entry, x.count >= 5, let season = x[1].number else { continue }
            let what = x[0].string ?? ""
            #expect(SeasonNames.own(Int(season), x[2].string, french: true) == x[3].string, "\(what) (fr)")
            #expect(SeasonNames.own(Int(season), x[2].string, french: false) == x[4].string, "\(what) (en)")
        }
        #expect(SeasonNames.label(2, "Season 2", french: true) == "Saison 2")
    }
}
