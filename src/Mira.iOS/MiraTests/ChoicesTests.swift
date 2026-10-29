import Foundation
import Testing
@testable import Mira

/// « Tes choix gardés sur ton Jellyfin »: tests/fixtures/choices.json, shared with Mira web and Mira for Windows and
/// Mac. The cases are written for a Mira in French (tools/web/unit/fr.mjs).
@Suite("Tes choix gardés sur ton Jellyfin")
struct ChoicesTests {
    private final class Bundled {}

    private func fixture() throws -> [String: JSONValue] {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "choices", withExtension: "json"))
        let value = try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url))
        return try #require(value.object)
    }

    private func local(_ value: JSONValue?) -> AccountChoices.Local {
        let x = value?.object ?? [:]
        var local = AccountChoices.Local()
        local.settings = (x["settings"]?.object ?? [:]).filter { $0.value != .null }
        local.tracks = x["tracks"]?.object ?? [:]
        local.hidden = (x["hidden"]?.object ?? [:]).compactMapValues(\.number)
        return local
    }

    @Test("Ce qui a changé sur cet appareil, avec son heure")
    func capture() throws {
        guard case let .array(rows)? = try fixture()["capture"] else { Issue.record("capture"); return }
        for row in rows {
            let x = row.object ?? [:]
            let got = AccountChoices.capture(x["doc"], local(x["local"]), now: x["now"]?.number ?? 0, language: "fr")
            #expect(JSONValue.object(got) == x["expected"], "\(x["name"]?.string ?? ""): \(JSONValue.object(got).text)")
        }
    }

    @Test("Cet appareil et Jellyfin ensemble, et s'il faut écrire")
    func merge() throws {
        guard case let .array(rows)? = try fixture()["merge"] else { Issue.record("merge"); return }
        for row in rows {
            let x = row.object ?? [:]
            let got = AccountChoices.merge(x["doc"], x["remote"])
            #expect(JSONValue.object(got) == x["expected"], "\(x["name"]?.string ?? ""): \(JSONValue.object(got).text)")
            let differs = AccountChoices.differs(.object(got), x["remote"])
            #expect(differs == x["differs"]?.bool, "\(x["name"]?.string ?? "")")
        }
    }

    @Test("Ce que l'appareil en utilise")
    func apply() throws {
        guard case let .array(rows)? = try fixture()["apply"] else { Issue.record("apply"); return }
        for row in rows {
            let x = row.object ?? [:]
            let expected = x["expected"]?.object ?? [:]
            let got = AccountChoices.apply(x["doc"], language: "fr")
            let name = x["name"]?.string ?? ""
            #expect(JSONValue.object(got.settings) == expected["settings"], "\(name)")
            #expect(JSONValue.object(Dictionary(uniqueKeysWithValues: got.tracks.map { ($0.id, $0.track) })) == expected["tracks"], "\(name)")
            #expect(JSONValue.object(got.hidden.mapValues { .number($0) }) == expected["hidden"], "\(name)")
            if case let .array(order)? = expected["order"] {
                #expect(got.tracks.map(\.id) == order.compactMap(\.string), "\(name)")
            }
        }
    }

    @Test("Seuls les plus récents, les mêmes sur chaque appareil")
    func limits() {
        var tracks: [String: JSONValue] = [:]
        for i in 0..<105 { tracks["s\(i)"] = .object(["audio": .string("jpn"), "subtitles": .bool(false), "at": .number(i == 0 ? 1000 : Double(i))]) }
        let merged = AccountChoices.merge(.object(["seen": .bool(true)]), .object(["tracks": .object(tracks)]))
        let kept = merged["tracks"]?.object ?? [:]
        #expect(kept.count == 100)
        for gone in ["s1", "s2", "s3", "s4", "s5"] { #expect(kept[gone] == nil, "\(gone)") }
        #expect(kept["s0"] != nil && kept["s6"] != nil && kept["s104"] != nil)
    }

    @Test("Les ordres des pistes avant tout choix suivent la langue de Mira")
    func languages() {
        #expect(AccountChoices.defaultAudioLanguages("en") == "jpn,ja,eng,en,fre,fra,fr")
        #expect(AccountChoices.defaultSubtitleLanguages("en") == "eng,en,fre,fra,fr")
        let english = AccountChoices.Local(settings: ["audioLanguages": .string("jpn,ja,eng,en,fre,fra,fr"), "subtitleLanguages": .string("eng,en,fre,fra,fr")])
        #expect(AccountChoices.capture(nil, english, now: 5, language: "en")["settings"] == .object([:]))
    }
}

/// The tracks a title starts with: the cases of tools/web/unit/tracks.test.mjs.
@Suite("Pistes au départ")
struct TrackChoiceTests {
    private let source = MediaSource(defaultAudioStreamIndex: 1, mediaStreams: [
        MediaStream(index: 0, type: "Video"),
        MediaStream(index: 1, type: "Audio", codec: "aac", language: "jpn", channels: 2),
        MediaStream(index: 2, type: "Audio", codec: "eac3", language: "fre", channels: 6),
        MediaStream(index: 3, type: "Subtitle", codec: "ass", language: "fre", isTextSubtitleStream: true),
        MediaStream(index: 4, type: "Subtitle", codec: "ass", language: "fre", isForced: true, isTextSubtitleStream: true),
        MediaStream(index: 5, type: "Subtitle", codec: "subrip", language: "eng", isExternal: true, isTextSubtitleStream: true),
        MediaStream(index: 6, type: "Subtitle", codec: "PGSSUB", language: "fre", isTextSubtitleStream: false),
    ])

    @Test("Japonais puis français, sous-titres français : japonais et sous-titres complets")
    func orders() {
        let first = TrackChoice.choose(source, memory: nil, audioOrder: "jpn,ja,fre,fra,fr", subtitleOrder: "fre,fra,fr,eng,en")
        #expect(first.audio == 1 && first.subtitle == 3)
        // French audio with French subtitles first: only the forced ones.
        let french = TrackChoice.choose(source, memory: nil, audioOrder: "fre,fra,fr", subtitleOrder: "fre,fra,fr,eng,en")
        #expect(french.audio == 2 && french.subtitle == 4)
    }

    @Test("Le dernier choix de la série l'emporte, et se retient par langues")
    func remembered() {
        let none = AccountChoices.track(.object(["audio": .string("jpn"), "subtitles": .bool(false)]))
        let chosen = TrackChoice.choose(source, memory: none, audioOrder: "", subtitleOrder: "")
        #expect(chosen.audio == 1 && chosen.subtitle == -1)
        let memory = TrackChoice.memory(source, audio: 1, subtitle: 4)
        #expect(memory == AccountChoices.track(.object(["audio": .string("jpn"), "subtitles": .bool(true), "subtitle": .string("fre"), "forced": .bool(true)])))
        let again = TrackChoice.choose(source, memory: memory, audioOrder: "fre,fra,fr", subtitleOrder: "eng,en")
        #expect(again.audio == 1 && again.subtitle == 4)
    }
}

/// « Reprendre un peu avant »: tests/fixtures/resume.json, shared with Mira web and Mira for Windows and Mac.
@Suite("Reprendre un peu avant")
struct ResumeBackTests {
    private final class Bundled {}

    @Test("Les cas communs") @MainActor
    func shared() throws {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "resume", withExtension: "json"))
        let fixture = try #require(try JSONDecoder().decode(JSONValue.self, from: Data(contentsOf: url)).object)
        #expect(fixture["choices"] == .array(Preferences.resumeBackChoices.map { .number(Double($0)) }))
        #expect(fixture["from"]?.number == PlaybackPlan.resumeBackFrom)
        guard case let .array(cases)? = fixture["cases"] else { Issue.record("cases"); return }
        for row in cases {
            guard case let .array(x) = row, x.count == 3, let left = x[0].number, let back = x[1].number, let at = x[2].number else { Issue.record("case"); continue }
            #expect(abs(PlaybackPlan.resumeBack(left, back: back) - at) < 0.0001, "\(left) - \(back)")
        }
    }
}
