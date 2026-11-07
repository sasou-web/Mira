import Foundation
import Testing
@testable import Mira

@Suite("Spoilers")
struct SpoilersTests {
    @Test("Les mêmes cas que Windows et Mac (spoilers.json)")
    func shared() throws {
        let url = try #require(Bundle(for: SpoilersBundle.self).url(forResource: "spoilers", withExtension: "json"))
        let fixture = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        for case let entry as [Any] in try #require(fixture["cases"] as? [Any]) {
            var item = Item(id: "e")
            item.type = entry[0] as? String
            let position = try #require(entry[2] as? NSNumber).int64Value
            item.userData = UserData(playbackPositionTicks: position, played: entry[1] as? Bool)
            #expect(Spoilers.hides(item, on: try #require(entry[3] as? Bool)) == (entry[4] as? Bool), "\(entry)")
        }
    }

    @Test("Une carte large d'un épisode pas commencé : l'image de sa saison ou de sa série, jamais la sienne")
    func wideCard() {
        var episode = Item(id: "e")
        episode.type = "Episode"
        episode.imageTags = ["Primary": "still"]
        episode.imageBlurHashes = ["Primary": ["still": "LEHV6nWB2yk8pyo0adR*.7kCMdnj"]]
        episode.parentBackdropItemId = "series"
        episode.parentBackdropImageTags = ["backdrop"]
        #expect(!Spoilers.hides(episode, on: false))
        #expect(Spoilers.hides(episode, on: true))
        #expect(Spoilers.stillBlurHash(episode) == "LEHV6nWB2yk8pyo0adR*.7kCMdnj")
        #expect(Artwork.of(episode, .wide, hidingSpoilers: true)?.itemId == "series")
        #expect(Artwork.of(episode, .wide, hidingSpoilers: false)?.itemId == "e")
        episode.userData = UserData(playbackPositionTicks: 1)
        #expect(Artwork.of(episode, .wide, hidingSpoilers: true)?.itemId == "e")
    }
}

private final class SpoilersBundle {}
