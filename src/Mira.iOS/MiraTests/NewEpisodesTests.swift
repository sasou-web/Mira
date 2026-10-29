import Foundation
import Testing
@testable import Mira

/// « Nouvel épisode »: tests/fixtures/new-episodes.json, shared with Mira web and Mira for Windows. The lines are
/// written in French there: here, which series, which episode and how many.
@Suite("Nouveaux épisodes")
struct NewEpisodesTests {
    private final class Bundled {}

    private struct Fixture: Decodable {
        struct Case: Decodable {
            struct Notice: Decodable { var seriesId: String; var title: String; var episode: String; var count: Int }
            var seen: String
            var notices: [Notice]
            var more: String?
        }
        var episodes: [Item]
        var series: [Item]
        var newest: String
        var cases: [Case]
    }

    private func fixture() throws -> Fixture {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "new-episodes", withExtension: "json"))
        return try JSON.decoder.decode(Fixture.self, from: Data(contentsOf: url))
    }

    @Test("Les cas communs")
    func shared() throws {
        let fixture = try fixture()
        let series = Dictionary(fixture.series.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        #expect(NewEpisodes.newest(fixture.episodes, seen: nil) == NewEpisodes.date(fixture.newest))
        for entry in fixture.cases {
            let seen = try #require(NewEpisodes.date(entry.seen))
            let pick = NewEpisodes.pick(fixture.episodes, series: series, seen: seen)
            #expect(pick.notices.map(\.seriesId) == entry.notices.map(\.seriesId), "\(entry.seen)")
            #expect(pick.notices.map(\.title) == entry.notices.map(\.title))
            #expect(pick.notices.map(\.episode.id) == entry.notices.map(\.episode))
            #expect(pick.notices.map(\.count) == entry.notices.map(\.count))
            #expect((pick.more == nil) == (entry.more == nil))
        }
    }

    @Test("Plus de trois séries : une ligne pour les autres")
    func many() {
        let series = Dictionary(uniqueKeysWithValues: (1...5).map { i in
            var show = Item(id: "s\(i)")
            show.type = "Series"
            show.userData = UserData(isFavorite: true)
            return (show.id, show)
        })
        let episodes = (1...5).map { i in
            var episode = Item(id: "e\(i)")
            episode.type = "Episode"
            episode.seriesId = "s\(i)"
            episode.dateCreated = "2026-10-14T14:0\(i):00Z"
            return episode
        }
        let pick = NewEpisodes.pick(episodes, series: series, seen: 0)
        #expect(pick.series == 5)
        #expect(pick.notices.map(\.seriesId) == ["s5", "s4", "s3"])
        #expect(pick.more != nil)
    }
}
