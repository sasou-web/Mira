import Foundation
import Testing
@testable import Mira

/// Episodes numbered from the series' start: tests/fixtures/absolute.json, shared with Mira for Windows and Mac
/// (AbsoluteNumbers of Core). The renames are Mira for Windows' only.
@Suite("Épisodes numérotés depuis le début de la série")
struct AbsoluteNumbersTests {
    private final class Bundled {}

    /// « S2E26-37 »: S2E26 to S2E37, each its own episode.
    private func expand(_ list: Any?, kind: String) -> [Item] {
        ((list as? [String]) ?? []).flatMap { code -> [Item] in
            let parts = code.dropFirst().split(separator: "E")
            let season = Int(parts[0]) ?? 0
            let span = parts[1].split(separator: "-").compactMap { Int($0) }
            return (span[0]...(span.last ?? span[0])).map { n in
                var item = Item(id: "\(kind)-\(season)-\(n)")
                item.type = "Episode"
                item.locationType = kind == "v" ? "Virtual" : "FileSystem"
                item.parentIndexNumber = season
                item.indexNumber = n
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
            let held = expand(row["held"], kind: "h")
            let virtuals = expand(row["virtual"], kind: "v")
            #expect(codes(AbsoluteNumbers.seasonal(held, virtuals: virtuals)) == codes(expand(row["seasonal"], kind: "x")), "\(name)")
            #expect(codes(AbsoluteNumbers.unheld(virtuals, held: held)) == codes(expand(row["unheld"], kind: "x")), "\(name)")
        }
    }
}
