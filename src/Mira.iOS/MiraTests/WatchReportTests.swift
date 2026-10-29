import Foundation
import Testing
@testable import Mira

/// « Ton bilan »: tests/fixtures/report.json, shared with Mira web and Mira for Windows and Mac.
@Suite("Ton bilan")
struct WatchReportTests {
    private final class Bundled {}

    private struct Fixture: Decodable {
        struct Expected: Decodable {
            struct Series: Decodable { var name: String; var episodes: Int; var minutes: Int }
            var minutes: Int
            var films: Int
            var episodes: Int
            var finished: [String]
            var genres: [String]
            var series: [Series]
        }
        var from: String
        var played: [Item]
        var series: [Item]
        var expected: Expected
    }

    @Test("Les cas communs")
    func shared() throws {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "report", withExtension: "json"))
        let fixture = try JSON.decoder.decode(Fixture.self, from: Data(contentsOf: url))
        let from = try #require(HomeContent.parseDate(fixture.from)).timeIntervalSince1970 * 1000
        let summary = WatchReport.summary(fixture.played, series: fixture.series, from: from)
        #expect(summary.minutes == fixture.expected.minutes)
        #expect(summary.films == fixture.expected.films)
        #expect(summary.episodes == fixture.expected.episodes)
        #expect(summary.finished == fixture.expected.finished)
        #expect(summary.genres == fixture.expected.genres)
        #expect(summary.series == fixture.expected.series.map { WatchReport.Series(name: $0.name, episodes: $0.episodes, minutes: $0.minutes) })
    }

    @Test("Début du mois, de l'année")
    func start() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = try #require(TimeZone(identifier: "Europe/Paris"))
        let now = try #require(calendar.date(from: DateComponents(year: 2026, month: 10, day: 29, hour: 11)))
        #expect(WatchReport.start(now, year: false, calendar: calendar) == calendar.date(from: DateComponents(year: 2026, month: 10, day: 1)))
        #expect(WatchReport.start(now, year: true, calendar: calendar) == calendar.date(from: DateComponents(year: 2026, month: 1, day: 1)))
    }
}
