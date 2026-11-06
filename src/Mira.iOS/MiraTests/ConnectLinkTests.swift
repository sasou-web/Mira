import Foundation
import Testing
@testable import Mira

/// The QR code of « Mira on your iPhone » on Windows and Mac: tests/fixtures/connect.json, shared with Core (PhoneLink).
@Suite("Connexion par QR code")
struct ConnectLinkTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "connect", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    @Test("Les liens faits par Windows et Mac se lisent")
    func made() throws {
        let rows = try #require(try fixture()["links"] as? [[String: Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let text = try #require(row["link"] as? String)
            let url = try #require(URL(string: text))
            let link = try #require(ConnectLink.parse(url), "\(text)")
            let written = try #require(row["server"] as? String)
            let server = try ServerAddress.normalize(written)
            #expect(link.server.absoluteString.lowercased() == server.absoluteString.lowercased(), "\(text)")
        }
    }

    @Test("Ce que l'iPhone lit d'un lien")
    func read() throws {
        let rows = try #require(try fixture()["read"] as? [[String: Any]])
        for row in rows {
            let text = try #require(row["link"] as? String)
            let url = try #require(URL(string: text))
            let link = ConnectLink.parse(url)
            #expect(link?.server.absoluteString == row["server"] as? String, "\(text)")
            #expect(link?.home?.absoluteString == row["home"] as? String, "\(text)")
        }
    }

    @Test("Un lien sans serveur, ou d'autre chose, est refusé")
    func refused() throws {
        let rows = try #require(try fixture()["refused"] as? [String])
        for text in rows {
            let url = try #require(URL(string: text))
            #expect(ConnectLink.parse(url) == nil, "\(text)")
        }
    }
}
