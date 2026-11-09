import Foundation
import Testing
@testable import Mira

/// The languages of the tracks: tests/fixtures/languages.json, shared with Mira for Windows and Mac (TrackLanguages).
@Suite("Langues des pistes")
struct TrackLanguagesTests {
    private final class Bundled {}

    private func fixture() throws -> [String: JSONValue] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "languages", withExtension: "json"))
        let value = try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url))
        return try #require(value.object)
    }

    @Test("Les mêmes langues que Windows et Mac, chacune avec tous ses codes")
    func languages() throws {
        guard case let .array(rows)? = try fixture()["languages"] else { Issue.record("languages"); return }
        let listed = rows.map { row -> String in
            guard case let .array(pair) = row else { return "" }
            return pair.compactMap(\.string).joined(separator: "|")
        }
        #expect(listed == TrackLanguages.known.map { "\($0.two)|\($0.codes)" })
        for language in TrackLanguages.known {
            for code in language.codes.split(separator: ",").map(String.init) {
                #expect(TrackLanguages.two(code) == language.two, "\(code)")
                #expect(TrackChoice.same(code, language.two), "Same track language: \(code)")
            }
        }
    }

    @Test("La langue de l'appareil d'abord, sauf l'anglais et le français")
    func start() throws {
        guard case let .array(rows)? = try fixture()["start"] else { Issue.record("start"); return }
        for row in rows {
            let x = row.object ?? [:]
            let device = x["device"]?.string ?? "", mira = x["mira"]?.string ?? ""
            #expect(TrackLanguages.start(device: device, mira: mira) == x["start"]?.string, "\(device) in \(mira)")
        }
    }

    @Test("Les ordres qu'écrit un choix, et ce que les Réglages en montrent")
    func orders() throws {
        let doc = try fixture()
        guard case let .array(compose)? = doc["compose"], case let .array(read)? = doc["read"] else { Issue.record("compose, read"); return }
        for row in compose {
            let x = row.object ?? [:]
            let first = x["first"]?.string ?? "", japanese = x["japaneseFirst"]?.bool ?? false
            #expect(TrackLanguages.audio(first, japaneseFirst: japanese) == x["audio"]?.string, "audio \(first) \(japanese)")
            #expect(TrackLanguages.subtitles(first) == x["subtitles"]?.string, "subtitles \(first)")
        }
        for row in read {
            let x = row.object ?? [:]
            let order = x["order"]?.string ?? ""
            #expect(TrackLanguages.first(of: order) == x["first"]?.string, "first of \(order)")
            #expect(TrackLanguages.japaneseFirst(order) == (x["japaneseFirst"]?.bool ?? false), "Japanese first in \(order)")
        }
    }

    @Test("Une langue proposée pour chacune, nommée")
    func choices() {
        let choices = TrackLanguages.choices(locale: Locale(identifier: "en"))
        #expect(choices.count == TrackLanguages.known.count)
        #expect(choices.first?.two == TrackLanguages.start)
        #expect(choices.contains { $0.two == "de" && $0.name == "German" })
    }
}
