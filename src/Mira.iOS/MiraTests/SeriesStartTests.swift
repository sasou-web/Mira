import Foundation
import Testing
@testable import Mira

/// Where Play starts a series when Jellyfin's next up says none: tests/fixtures/series-start.json, shared with Core
/// (EpisodePages.Start, Unfinished).
@Suite("Lecture d'une série sans « à suivre »")
struct SeriesStartTests {
    private final class Bundled {}

    @Test("Les cas communs : une reprise, le premier non vu, l'épisode après celui revu en dernier, sinon S01E01 avant les épisodes spéciaux")
    func shared() throws {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "series-start", withExtension: "json"))
        let fixture = try #require(try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url)).object)
        guard case let .array(cases)? = fixture["cases"] else { Issue.record("cases"); return }
        #expect(cases.count > 5)
        for entry in cases {
            guard case let .array(x) = entry, x.count == 4, case let .array(rows) = x[1] else { Issue.record("case \(entry)"); continue }
            let episodes: [Item] = rows.compactMap { row in
                guard case let .array(e) = row, e.count == 4 || e.count == 5, let id = e[0].string else { return nil }
                var item = Item(id: id, type: "Episode")
                item.parentIndexNumber = e[1].number.map(Int.init)
                item.runTimeTicks = 100
                item.userData = UserData(playbackPositionTicks: Int64(e[3].number ?? 0), played: e[2].bool ?? false)
                // Last played, in minutes from a moment: written as Jellyfin does, read back by HomeContent.parseDate.
                if e.count == 5, let minutes = e[4].number {
                    item.userData?.lastPlayedDate = ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: 1_767_225_600 + minutes * 60))
                }
                return item
            }
            #expect(episodes.count == rows.count, "\(x[0].string ?? "")")
            let name = x[0].string ?? ""
            #expect(SeriesStart.unfinished(episodes)?.id == x[2].string, "\(name): unfinished")
            #expect(SeriesStart.start(episodes)?.id == x[3].string, "\(name): start")
        }
    }
}
