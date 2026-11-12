import Foundation
import Testing
@testable import Mira

/// « Télécharger sur le PC serveur »: tests/fixtures/releases.json, torlink-remote.json and torlink-lines.json, shared
/// with Mira web and Mira for Windows and Mac. Their texts are French; the iPhone's are English first, so what does not
/// depend on the language is compared: titles, years, episodes as S01E05, qualities, groups and their order, links and
/// searches, numbers, readiness.
@Suite("Télécharger sur le PC serveur")
struct DownloadsTests {
    private final class Bundled {}

    private func fixture(_ name: String) throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: name, withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    private func rows(_ fixture: [String: Any], _ key: String) -> [[Any]] { fixture[key] as? [[Any]] ?? [] }

    @Test("Ce que dit le nom d'une release")
    func names() throws {
        let cases = rows(try fixture("releases"), "names")
        #expect(!cases.isEmpty)
        for row in cases {
            let name = row[0] as? String ?? ""
            let release = Releases.parse(name)
            #expect(release.title == row[1] as? String, "\(name)")
            #expect(release.year == (row[2] as? NSNumber)?.intValue, "\(name)")
            let label = row[3] as? String ?? ""
            // « S06E05 » is the same in every language; « Épisode 5 » is not.
            if label.isEmpty || label.range(of: #"^S\d+E\d+"#, options: .regularExpression) != nil { #expect(Releases.episodeLabel(release) == label, "\(name)") }
            let quality = row[4] as? String ?? ""
            if release.source != "cam" { #expect(Releases.qualityLine(release) == quality, "\(name)") }
        }
    }

    @Test("Nom lisible d'un téléchargement")
    func readable() throws {
        for row in rows(try fixture("releases"), "readable") {
            let got = Releases.readable(row[0] as? String)
            #expect(got.full == row[2] as? String, "\(row[0])")
            if let title = row[1] as? String, !title.contains(" · ") || title.range(of: #" · S\d+E\d+"#, options: .regularExpression) != nil {
                #expect(got.title == title, "\(row[0])")
            }
        }
    }

    @Test("Releases regroupées, la meilleure en avant")
    func ranked() throws {
        let sets = try #require(try fixture("releases")["ranked"] as? [[String: Any]])
        #expect(!sets.isEmpty)
        for set in sets {
            let hits = (set["hits"] as? [[Any]] ?? []).map { hit in
                Releases.Hit(id: hit[0] as? String ?? "", name: hit[1] as? String ?? "", size: (hit[2] as? NSNumber)?.int64Value, seeders: (hit[3] as? NSNumber)?.intValue,
                             leechers: nil, source: hit[4] as? String, added: nil, category: hit.count > 5 ? hit[5] as? String : nil)
            }
            let groups = Releases.group(hits, category: set["category"] as? String)
            let expected = set["groups"] as? [[Any]] ?? []
            #expect(groups.count == expected.count)
            for (group, wanted) in zip(groups, expected) {
                #expect(group.title == wanted[0] as? String)
                let label = wanted[1] as? String ?? ""
                if label.isEmpty || label.range(of: #"^S\d+E\d+"#, options: .regularExpression) != nil { #expect(group.label == label) }
                #expect(group.best.hit.id == wanted[2] as? String, "\(group.title) \(group.label)")
                #expect(group.others.map(\.hit.id) == wanted[3] as? [String], "\(group.title) \(group.label)")
            }
        }
    }

    @Test("Liens magnet, recherches et noms")
    func checks() throws {
        let remote = try fixture("torlink-remote")
        for row in rows(remote, "magnets") { #expect(RemoteLines.infoHash(row[0] as? String ?? "") == row[1] as? String, "\(row[0])") }
        for row in rows(remote, "queries") { #expect(RemoteLines.cleanQuery(row[0] as? String ?? "") == row[1] as? String, "\(row[0])") }
        for row in rows(remote, "names") { #expect(RemoteLines.displayName(row[0] as? String ?? "") == row[1] as? String, "\(row[0])") }
    }

    @Test("Tailles, débits et temps restant")
    func numbers() throws {
        let lines = try fixture("torlink-lines")
        for row in rows(lines, "sizes") {
            let parts = RemoteLines.sizeParts((row[0] as? NSNumber)?.int64Value, french: true)
            let words = (row[1] as? String ?? "").split(separator: " ").map(String.init)
            #expect(parts.number == words.first, "\(row[0])")
            #expect(parts.unit == (words.last == "Go" ? .gigabytes : .megabytes), "\(row[0])")
        }
        for row in rows(lines, "speeds") {
            let parts = RemoteLines.speedParts((row[0] as? NSNumber)?.doubleValue, french: true)
            let text = row[1] as? String ?? ""
            if text.isEmpty { #expect(parts == nil); continue }
            let words = text.split(separator: " ").map(String.init)
            #expect(parts?.number == words.first, "\(row[0])")
            #expect(parts?.unit == (words.last == "Mo/s" ? .megabytesPerSecond : .kilobytesPerSecond), "\(row[0])")
        }
        for row in rows(lines, "remaining") {
            let text = row[1] as? String ?? ""
            let numbers = text.split(whereSeparator: { !$0.isNumber }).compactMap { Int($0) }
            let wanted: RemoteLines.Left = text.hasPrefix("moins") ? .underMinute : text.contains("jour") ? .days(numbers[0])
                : text.contains(" h ") ? .hours(numbers[0], numbers[1]) : .minutes(numbers[0])
            #expect(RemoteLines.left((row[0] as? NSNumber)?.doubleValue ?? 0) == wanted, "\(row[0])")
        }
    }

    @Test("Le PC serveur, les demandes et les téléchargements")
    func states() throws {
        let lines = try fixture("torlink-lines")
        let now = try #require(RemoteLines.date(lines["now"] as? String))
        for case let row as [String: Any] in lines["agents"] as? [Any] ?? [] {
            let agent: RemoteLines.Agent? = try (row["agent"] as? [String: Any]).map {
                try JSON.decoder.decode(RemoteLines.Agent.self, from: JSONSerialization.data(withJSONObject: $0))
            }
            let line = RemoteLines.agentLine(agent, now: now)
            #expect(line.ready == (row["ready"] as? Bool), "\(String(describing: agent))")
            if let problem = agent?.problem, agent?.online == true { #expect(line.text == problem) }
        }
        for row in rows(lines, "downloads") {
            let download = try JSON.decoder.decode(RemoteLines.Download.self, from: JSONSerialization.data(withJSONObject: row[0]))
            let text = row[1] as? String ?? ""
            let wanted: RemoteLines.Failure = text.contains("plus de place") ? .diskFull : text.contains("ne peut pas écrire") ? .cannotWrite
                : download.status == "failed" && text.contains("Invalid") ? .other("Invalid torrent identifier") : .none
            if download.status == "failed" { #expect(RemoteLines.failureKind(download.error) == wanted, "\(text)") }
            if download.status != "completed" { #expect(text.contains("\(RemoteLines.percent(download.progress)) %"), "\(text)") }
        }
        for row in rows(lines, "placed") { #expect(RemoteLines.placedLine(row[0] as? String ?? "", french: true) == row[1] as? String) }
        #expect(RemoteLines.controllable(.init(id: "a", name: "x", status: "paused")))
        #expect(!RemoteLines.controllable(.init(id: "a", name: "x", status: "completed")))
    }

    /// The cases of RemoteDownloads.Offered (Mira.Core.Tests RemoteDownloadTests), but the PC of Jellyfin's own.
    @Test("Proposé dans les Réglages seulement à un administrateur, avec l'extension de Mira sur Jellyfin")
    func offered() {
        #expect(RemoteLines.offered(administrator: true, miraExtension: true, before: false), "Un administrateur avec l'extension : le relais.")
        #expect(!RemoteLines.offered(administrator: true, miraExtension: false, before: true), "Jellyfin sans l'extension : jamais une page qui ne peut pas marcher.")
        #expect(!RemoteLines.offered(administrator: false, miraExtension: nil, before: true), "Un compte qui n'administre pas : rien.")
        #expect(!RemoteLines.offered(administrator: false, miraExtension: true, before: true), "Un compte qui n'administre pas : rien.")
        #expect(RemoteLines.offered(administrator: true, miraExtension: nil, before: true), "Jellyfin n'a pas répondu : l'entrée reste.")
        #expect(!RemoteLines.offered(administrator: true, miraExtension: nil, before: false), "Jellyfin n'a pas répondu : l'entrée reste absente.")
        #expect(!RemoteLines.offered(administrator: nil, miraExtension: nil, before: false), "Jellyfin n'a rien dit : rien.")
    }
}
