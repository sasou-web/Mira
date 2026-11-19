import Foundation
import Testing
@testable import Mira

/// Episodes numbered from the series' start: tests/fixtures/absolute.json, shared with Mira for Windows and Mac
/// (AbsoluteNumbers of Core). The renames are Mira for Windows' only.
@Suite("Épisodes numérotés depuis le début de la série")
struct AbsoluteNumbersTests {
    private final class Bundled {}

    /// « S2E26-37 »: S2E26 to S2E37, each its own episode; `named`: with a title of its own, as TheTVDB gives it.
    private func expand(_ list: Any?, kind: String, named: Bool = false) -> [Item] {
        ((list as? [String]) ?? []).flatMap { code -> [Item] in
            let parts = code.dropFirst().split(separator: "E")
            let season = Int(parts[0]) ?? 0
            let span = parts[1].split(separator: "-").compactMap { Int($0) }
            return (span[0]...(span.last ?? span[0])).map { n in
                var item = Item(id: "\(kind)-\(season)-\(n)")
                item.type = "Episode"
                item.locationType = kind == "v" || kind == "c" ? "Virtual" : "FileSystem"
                item.parentIndexNumber = season
                item.indexNumber = n
                if named { item.name = "A title" }
                // « virtualComing »: not aired yet.
                if kind == "c" { item.premiereDate = "2999-01-01T00:00:00.0000000Z" }
                return item
            }
        }
    }

    private func codes(_ items: [Item]) -> String {
        items.sorted { ($0.parentIndexNumber ?? 0, $0.indexNumber ?? 0) < ($1.parentIndexNumber ?? 0, $1.indexNumber ?? 0) }
            .map { "S\($0.parentIndexNumber ?? 0)E\($0.indexNumber ?? 0)" }.joined(separator: ",")
    }

    @Test("Pas manquants, à leur numéro de saison")
    func cases() throws {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "absolute", withExtension: "json"))
        let root = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        let rows = try #require(root["cases"] as? [[String: Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let name = row["name"] as? String ?? ""
            // « heldDouble »: one file for two episodes (« S1E3-4 »).
            let doubles = ((row["heldDouble"] as? [String]) ?? []).map { code -> Item in
                let parts = code.dropFirst().split(separator: "E")
                let span = parts[1].split(separator: "-").compactMap { Int($0) }
                var item = Item(id: "d-\(code)")
                item.type = "Episode"
                item.locationType = "FileSystem"
                item.parentIndexNumber = Int(parts[0])
                item.indexNumber = span[0]
                item.indexNumberEnd = span.last
                return item
            }
            let held = expand(row["held"], kind: "h") + expand(row["heldNamed"], kind: "n", named: true) + doubles
            let virtuals = expand(row["virtual"], kind: "v") + expand(row["virtualComing"], kind: "c")
            #expect(codes(AbsoluteNumbers.seasonal(held, virtuals: virtuals)) == codes(expand(row["seasonal"], kind: "x")), "\(name)")
            #expect(codes(AbsoluteNumbers.unheld(virtuals, held: held)) == codes(expand(row["unheld"], kind: "x")), "\(name)")
        }
    }
}
