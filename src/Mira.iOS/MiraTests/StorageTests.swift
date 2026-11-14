import Foundation
import Testing
@testable import Mira

@Suite("Place prise")
struct StorageTests {
    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: StorageBundle.self).url(forResource: "storage", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    private func number(_ value: Any?) -> Int64? { (value as? NSNumber)?.int64Value }

    @Test("Les mêmes cas que Windows et Mac (storage.json)")
    func shared() throws {
        let cases = try fixture()
        for row in cases["text"] as? [[Any]] ?? [] {
            let parts = RemoteLines.sizeParts(number(row[0]), french: true)
            let words = (row[1] as? String ?? "").split(separator: " ").map(String.init)
            let unit: RemoteLines.Unit = switch words.last ?? "" { case "To": .terabytes; case "Go": .gigabytes; default: .megabytes }
            #expect(parts.number == words.first, "\(row)")
            #expect(parts.unit == unit, "\(row)")
            let english = (row[2] as? String ?? "").split(separator: " ").map(String.init)
            #expect(RemoteLines.sizeParts(number(row[0]), french: false).number == english.first, "\(row)")
        }
        for row in cases["files"] as? [[Any]] ?? [] {
            let sources = (row[0] as? [[Any]] ?? []).map { entry in
                var source = MediaSource()
                source.size = number(entry[0])
                source.path = entry[1] as? String
                source.id = entry[2] as? String
                return source
            }
            #expect(StorageSize.of(sources) == number(row[1]), "\(row)")
        }
        for row in cases["series"] as? [[Any]] ?? [] {
            let episodes = (row[0] as? [[Any]] ?? []).map { entry in
                var episode = Item(id: entry[0] as? String ?? "")
                episode.type = "Episode"
                episode.parentIndexNumber = (entry[1] as? NSNumber)?.intValue
                var source = MediaSource()
                source.id = episode.id
                source.size = number(entry[2])
                episode.mediaSources = source.size == nil ? [] : [source]
                return episode
            }
            let sizes = StorageSize.sizes(episodes)
            let season = (row[1] as? NSNumber)?.intValue
            #expect(StorageSize.sum(episodes, sizes) == number(row[2]), "\(row)")
            #expect(StorageSize.sum(episodes.filter { $0.parentIndexNumber == season }, sizes) == number(row[3]), "\(row)")
        }
    }

    @Test("Un film en deux parties : les deux comptées ; la taille en dernier dans la ligne de la fiche")
    func parts() {
        var film = Item(id: "f")
        film.type = "Movie"
        film.partCount = 2
        var first = MediaSource()
        first.id = "f"
        first.path = "/m/F - cd1.mkv"
        first.size = 1000
        film.mediaSources = [first]
        var part = Item(id: "p")
        var second = MediaSource()
        second.id = "p"
        second.path = "/m/F - cd2.mkv"
        second.size = 500
        part.mediaSources = [second]
        #expect(StorageSize.of(film, parts: [part]) == 1500)
        #expect(StorageSize.of(film) == 1000)
        #expect(StorageSize.text(nil) == nil)
        film.productionYear = 2019
        #expect(ItemText.facts(film) == "2019")
        #expect(ItemText.facts(film, size: 1_288_490_189).hasPrefix("2019 · "))
    }
}

private final class StorageBundle {}
