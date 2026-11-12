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

    // MARK: « À leur place » (EpisodeShelf), the « shelf » cases

    @Test("Manquants et à venir à leur place : lignes, saisons absentes, textes")
    func shelf() throws {
        let x = try fixture()
        let today = try #require(EpisodeCalendar.Day(x["today"] as? String))
        let shelf = try #require(x["shelf"] as? [String: Any])
        let all = try items(x["episodes"]) + items(shelf["more"])
        let library = all.filter { $0.locationType != "Virtual" }
        let virtuals = all.filter { $0.locationType == "Virtual" }
        func find(_ text: String) throws -> Item { try #require(all.first { code($0) == text }, "\(text)") }
        let lines = try #require(shelf["lines"] as? [[Any]])
        #expect(!lines.isEmpty)
        for row in lines {
            let season = try #require(row[0] as? Int)
            let show = try #require(row[1] as? Bool)
            let first = try #require(row[2] as? Bool)
            let last = try #require(row[3] as? Bool)
            let shown = try (try #require(row[4] as? [String])).map(find)
            let made = EpisodeShelf.lines(shown, virtuals: virtuals, season: season, firstPage: first, lastPage: last, today: today, showMissing: show)
                .map { "\($0.kind.rawValue) \(EpisodeShelf.code($0.episodes, french: true))" }
            #expect(made == row[5] as? [String], "Season \(season): \(made)")
        }
        // The seasons the library has none of, as the season list ends with them (« Saison 1 » and « Saison 2 » are the library's).
        let held = Set(library.compactMap(\.parentIndexNumber))
        for row in try #require(shelf["pages"] as? [[Any]]) {
            let show = try #require(row[0] as? Bool)
            let expected = try #require(row[1] as? [String]).filter { $0.contains(" · ") }
            let made = EpisodeShelf.absentSeasons(held: held, virtuals: virtuals, today: today, showMissing: show)
                .map { EpisodeShelf.seasonLabel($0, virtuals: virtuals, today: today, french: true) }
            #expect(made == expected)
        }
        for row in try #require(shelf["texts"] as? [[Any]]) {
            let what = try #require(row[0] as? String)
            let run = try (try #require(row[1] as? [String])).map(find)
            let text: String
            switch what {
            case "missing": text = EpisodeShelf.missingText(run, today: today, french: true)
            case "later": text = EpisodeShelf.laterText(run, today: today, french: true)
            case "season": text = EpisodeShelf.seasonText(run, today: today, french: true)
            default:
                let entry = EpisodeShelf.entry(run[0], today: today, french: true)
                text = "\(entry.name) | \(entry.when)"
            }
            #expect(text == row[2] as? String)
        }
        #expect(EpisodeShelf.show(1, open: false, french: true) == "Voir" && EpisodeShelf.show(25, open: false, french: true) == "Voir les 25")
        let unnamed = try find("S4E1")
        #expect(EpisodeShelf.name(unnamed, french: true) == "Épisode 1")
    }
}
