import Foundation
import Testing
@testable import Mira

/// « Share a report »: tests/fixtures/diagnostics.json, shared with Mira for Windows and Mac (DiagnosticReport).
@Suite("Rapport de diagnostic")
struct DiagnosticsTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "diagnostics", withExtension: "json"))
        let object = try JSONSerialization.jsonObject(with: Data(contentsOf: url))
        return try #require(object as? [String: Any])
    }

    @Test("Rien de privé ne sort")
    func redacts() throws {
        let rows = try #require(try fixture()["redact"] as? [[String]])
        for row in rows { #expect(Diagnostics.redact(row[0]) == row[1], "\(row[0])") }
    }

    @Test("Réseau nommé par sa sorte")
    func networks() throws {
        let rows = try #require(try fixture()["network"] as? [[Any]])
        for row in rows {
            let server = row[0] as? String
            #expect(Diagnostics.network(server) == row[1] as? String, "\(server ?? "nil")")
        }
    }

    @Test("Le rapport ne nomme ni le compte ni le serveur")
    func report() {
        let text = Diagnostics.build(version: "0.9.5", server: "http://100.114.247.13:8096", jellyfin: "12.1.0",
                                     facts: [("Device", "ogsas’s iPhone"), ("Quality", "auto")], playbacks: ["2026-11-06 20:00 Episode · mkv · file as it is · Tailscale"],
                                     now: Date(), names: ["ogsas", nil])
        #expect(text.contains("Reached by: Tailscale"))
        #expect(text.contains("Jellyfin: 12.1.0"))
        #expect(text.contains("Device: [name]’s iPhone"))
        #expect(!text.contains("ogsas") && !text.contains("100.114"))
        #expect(Diagnostics.fileName(Date()).hasPrefix("mira-report-"))
    }
}
