import Foundation
import Testing
@testable import Mira

/// « Missing episodes and what comes next »: tests/fixtures/calendar.json, shared with Mira for Windows and Mac
/// (EpisodeCalendar of Core). Its texts are French: said here in French.
@Suite("Épisodes manquants et à venir")
struct CalendarTests {
    private final class Bundled {}

    private func fixture() throws -> [String: Any] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "calendar", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    private func items(_ list: Any?) throws -> [Item] {
        let data = try JSONSerialization.data(withJSONObject: list ?? [])
        return try JSON.decoder.decode([Item].self, from: data)
    }

    /// « S2E4 » as an episode of the fixture.
    private func episode(_ code: String) -> Item {
        let parts = code.dropFirst().split(separator: "E").compactMap { Int($0) }
        var item = Item(id: code)
        item.type = "Episode"
        item.parentIndexNumber = parts[0]
        item.indexNumber = parts[1]
        return item
    }

    private func code(_ item: Item) -> String { "S\(item.parentIndexNumber ?? 0)E\(item.indexNumber ?? 0)" }

    @Test("Manquants et à venir, dans l'ordre des sorties ; les textes de la fiche")
    func read() throws {
        let x = try fixture()
        let today = try #require(EpisodeCalendar.Day(x["today"] as? String))
        let episodes = try items(x["episodes"])
        let (missing, coming) = EpisodeCalendar.read(episodes, today: today)
        #expect(missing.map(code) == x["missing"] as? [String])
        #expect(coming.map(code) == x["coming"] as? [String])
        let texts = try #require(x["texts"] as? [String: String])
        #expect(EpisodeCalendar.missingText(missing, french: true) == texts["missing"])
        #expect(EpisodeCalendar.nextText(coming, today: today, french: true) == texts["next"])
        let days = try #require(x["byDay"] as? [[Any]])
        let grouped = EpisodeCalendar.byDay(coming)
        #expect(grouped.map(\.day) == days.compactMap { EpisodeCalendar.Day($0[0] as? String) })
        #expect(grouped.map { $0.episodes.map(code) } == days.compactMap { $0[1] as? [String] })
    }

    @Test("Les épisodes à la suite en un seul « S1 · É3–5 »")
    func ranges() throws {
        let rows = try #require(try fixture()["ranges"] as? [[Any]])
        #expect(!rows.isEmpty)
        for row in rows {
            let codes = try #require(row[0] as? [String])
            #expect(EpisodeCalendar.missingText(codes.map(episode), french: true) == row[1] as? String, "\(codes)")
        }
    }

    @Test("Un jour dit comme dans une phrase")
    func days() throws {
        let x = try fixture()
        let today = try #require(EpisodeCalendar.Day(x["today"] as? String))
        let rows = try #require(x["days"] as? [[String]])
        for row in rows {
            let day = try #require(EpisodeCalendar.Day(row[0]))
            #expect(EpisodeCalendar.dayText(day, today: today, french: true) == row[1], "\(row[0])")
        }
        let saturday = try #require(EpisodeCalendar.Day("2026-10-31"))
        let thursday = try #require(EpisodeCalendar.Day("2026-11-05"))
        #expect(EpisodeCalendar.dayTitle(saturday, today: today, french: true) == "Samedi")
        #expect(EpisodeCalendar.dayText(saturday, today: today, french: false) == "Saturday")
        #expect(EpisodeCalendar.dayText(thursday, today: today, french: false) == "Thursday, November 5")
    }
}
