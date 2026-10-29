import Foundation
import Testing
@testable import Mira

/// A title's summary in plain text: the cases of tests/fixtures/overview.json, shared with Mira web and Mira for
/// Windows and Mac (AniList's « <br> », paragraphs, other tags, entities).
@Suite("Résumé d’un titre")
struct OverviewTests {
    private final class Bundled {}

    @Test("Les cas communs")
    func shared() throws {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "overview", withExtension: "json"))
        let fixture = try #require(try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url)).object)
        guard case let .array(cases)? = fixture["cases"] else { Issue.record("cases"); return }
        #expect(cases.count >= 9)
        for entry in cases {
            guard case let .array(x) = entry, x.count == 4, let name = x[0].string, let text = x[1].string,
                  let plain = x[2].string, let flat = x[3].string else { Issue.record("case"); continue }
            #expect(OverviewText.plain(text) == plain, "\(name)")
            #expect(OverviewText.plain(text, flat: true) == flat, "\(name) (flat)")
        }
        #expect(OverviewText.plain(nil) == "")
    }
}
