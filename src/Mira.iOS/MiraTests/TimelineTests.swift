import Foundation
import Testing
@testable import Mira

/// The player's timeline: the cases of tools/web/unit/timeline.test.mjs, and tests/fixtures/chapters.json shared with
/// Mira web and Mira for Windows and Mac.
@Suite("Passages et épisode suivant")
struct TimelineTests {
    private final class Bundled {}

    private let intro = Passage(type: "Intro", start: 10, end: 40)
    private let outro = Passage(type: "Outro", start: 1300, end: 1400)
    private var segments: [Passage] { [intro, Passage(type: "Unknown", start: 50, end: 60), outro] }

    @Test("Passages à passer, jusqu'à leur dernière seconde")
    func skippable() {
        #expect(Timeline.skippable(segments, at: 9.9) == nil)
        #expect(Timeline.skippable(segments, at: 10) == intro)
        #expect(Timeline.skippable(segments, at: 38.9) == intro)
        #expect(Timeline.skippable(segments, at: 39) == nil)
        #expect(Timeline.skippable(segments, at: 55) == nil)
        // The end credits are no passage to skip: the next episode is offered there.
        #expect(Timeline.skippable(segments, at: 1350) == nil)
    }

    @Test("Intro et récap passés d'eux-mêmes, une fois")
    func skipsItself() {
        var skipped: Set<Passage> = []
        #expect(Timeline.skipsItself(intro, autoSkip: true, skipped: skipped))
        #expect(!Timeline.skipsItself(intro, autoSkip: false, skipped: skipped))
        #expect(!Timeline.skipsItself(Passage(type: "Preview", start: 0, end: 10), autoSkip: true, skipped: skipped))
        #expect(!Timeline.skipsItself(nil, autoSkip: true, skipped: skipped))
        skipped.insert(intro)
        #expect(!Timeline.skipsItself(intro, autoSkip: true, skipped: skipped))
        #expect(!Timeline.skipsItself(Passage(type: "Intro", start: 11, end: 39), autoSkip: true, skipped: skipped))
        #expect(Timeline.skipsItself(Passage(type: "Recap", start: 0, end: 10), autoSkip: true, skipped: skipped))
    }

    @Test("Épisode suivant : dès le générique, sinon les dernières secondes")
    func next() {
        #expect(Timeline.nextFrom(segments, end: 1400) == 1300)
        #expect(Timeline.nextFrom([intro], end: 1400) == 1400 - Timeline.countdown)
        let credits = Passage(type: "Outro", start: 1200, end: 1290)
        let preview = Passage(type: "Preview", start: 1290, end: 1398)
        #expect(Timeline.nextFrom([credits, preview], end: 1400) == 1200)
        #expect(Timeline.nextFrom([credits], end: 1400) == 1400 - Timeline.countdown)
        #expect(Timeline.nativeNextAt([intro, credits, preview], end: 1400) == 1210)
        #expect(Timeline.nativeNextAt(segments, end: 1400, startedAt: 600) == 1310)
        #expect(Timeline.nativeNextAt([intro], end: 1400) == nil)
        #expect(Timeline.nativeNextAt([credits], end: 1400) == nil)
        #expect(Timeline.nativeNextAt([Passage(type: "Outro", start: 1392, end: 1400)], end: 1400) == nil)
        #expect(Timeline.nativeNextAt([credits, preview], end: 1400, startedAt: 1250) == nil)
        #expect(Timeline.nativeNextAt([credits, preview], end: 1400, startedAt: 1199.5) == nil)
        #expect(Timeline.nativeNextAt([credits, preview], end: 1400, startedAt: 1100) == 1210)
    }

    private func ticks(_ seconds: Double) -> Int64 { Int64((seconds * 10_000_000).rounded()) }

    @Test("Titres des chapitres : les mêmes passages que Windows, Mac et Mira web")
    func chapters() throws {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "chapters", withExtension: "json"))
        let fixture = try #require(try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url)).object)
        guard case let .array(titles)? = fixture["titles"], case let .array(positions)? = fixture["positions"],
              case let .array(episodes)? = fixture["episodes"] else { Issue.record("fixture"); return }
        for row in titles {
            guard case let .array(x) = row, x.count == 2 else { continue }
            #expect(Timeline.chapterKind(x[0].string) == x[1].string, "« \(x[0].string ?? "null") »")
        }
        for row in positions {
            guard case let .array(x) = row, x.count == 4, let start = x[1].number, let duration = x[2].number else { continue }
            #expect(Timeline.chapterKind(x[0].string, start: start, duration: duration) == x[3].string)
        }
        for episode in episodes {
            guard let e = episode.object, let duration = e["duration"]?.number, case let .array(chapters)? = e["chapters"],
                  case let .array(expected)? = e["passages"] else { continue }
            let list = chapters.compactMap { c -> Chapter? in
                guard case let .array(x) = c, let start = x[0].number else { return nil }
                return Chapter(name: x[1].string, startPositionTicks: ticks(start))
            }
            let found = Timeline.chapterPassages(list, totalTicks: ticks(duration))
            let wanted = expected.compactMap { p -> Passage? in
                guard case let .array(x) = p, let type = x[0].string, let start = x[1].number, let end = x[2].number else { return nil }
                return Passage(type: type, start: start, end: end)
            }
            #expect(found == wanted)
        }
        #expect(Timeline.chapterPassages(nil, totalTicks: ticks(100)).isEmpty)
        // Without the title's length, the last chapter has no end.
        #expect(Timeline.chapterPassages([Chapter(name: "Partie A", startPositionTicks: 0), Chapter(name: "Ending", startPositionTicks: ticks(1300))], totalTicks: nil).isEmpty)
    }

    @Test("Les segments de Jellyfin l'emportent sur les chapitres")
    func withChapters() {
        let server = [Passage(type: "Intro", start: 60, end: 150)]
        let names: [(Double, String)] = [(0, "Partie A"), (50, "Opening"), (140, "Partie B"), (1290, "Ending"), (1380, "Preview"), (1418, "Fin")]
        let guessed = Timeline.chapterPassages(names.map { Chapter(name: $0.1, startPositionTicks: ticks($0.0)) }, totalTicks: ticks(1420))
        let merged = Timeline.withChapters(server, guessed)
        #expect(merged == [Passage(type: "Intro", start: 60, end: 150), Passage(type: "Outro", start: 1290, end: 1380), Passage(type: "Preview", start: 1380, end: 1418)])
        #expect(Timeline.withChapters([], guessed) == guessed)
        #expect(Timeline.skippable(merged, at: 1390) == merged[2])
        #expect(Timeline.nextFrom(merged, end: 1420) == 1290)
        let noise = [Passage(type: "Intro", start: 0, end: 2), Passage(type: "Outro", start: -5, end: 1300)]
        #expect(Timeline.withChapters(noise, guessed) == guessed)
    }
}
