import Foundation
import Testing
@testable import Mira

/// The thumbnails over the timeline: the cases of Core's PlayerTests (TrickplaySet), on Jellyfin's answer of 12.1.
@Suite("Aperçus de la barre")
struct TrickplayTests {
    private let answer = """
        {"Id":"film","Chapters":[{"StartPositionTicks":0,"Name":"Début"}],"Trickplay":{"other":{"320":{"Width":320,"Height":180,"TileWidth":10,"TileHeight":10,"ThumbnailCount":4,"Interval":10000}},"src":{"160":{"Width":160,"Height":90,"TileWidth":10,"TileHeight":10,"ThumbnailCount":250,"Interval":10000},"320":{"Width":320,"Height":180,"TileWidth":10,"TileHeight":10,"ThumbnailCount":1000,"Interval":10000},"0":{"Width":0}}}}
        """

    @Test("La taille juste au-dessus de 240 px, du fichier joué")
    func pick() throws {
        let item = try JSON.decoder.decode(Item.self, from: Data(answer.utf8))
        let set = try #require(TrickplaySet.pick("film", item.trickplay, sourceId: "src"))
        #expect(set.sourceId == "src")
        #expect(set.info.width == 320)
        #expect(set.tilePath(2).path == "Videos/film/Trickplay/320/2.jpg")
        #expect(set.tilePath(2).query["mediaSourceId"] == "src")
        #expect(TrickplaySet.pick("film", item.trickplay, sourceId: nil)?.sourceId == "other")
        #expect(TrickplaySet.pick("x", nil, sourceId: nil) == nil)
        #expect(TrickplaySet.pick("x", ["s": ["0": TrickplayInfo()]], sourceId: "s") == nil)
    }

    @Test("La case d'un moment dans sa tuile")
    func cells() throws {
        let item = try JSON.decoder.decode(Item.self, from: Data(answer.utf8))
        let set = try #require(TrickplaySet.pick("film", item.trickplay, sourceId: "src"))
        #expect(set.cell(0) == TrickplayCell(tile: 0, x: 0, y: 0, width: 320, height: 180))
        #expect(set.cell(125) == TrickplayCell(tile: 0, x: 2 * 320, y: 1 * 180, width: 320, height: 180))
        #expect(set.cell(1005) == TrickplayCell(tile: 1, x: 0, y: 0, width: 320, height: 180))
        // Past the end: the last thumbnail.
        #expect(set.cell(1_000_000).tile == 9)
        #expect(set.cell(-5) == set.cell(0))
    }
}
