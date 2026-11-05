import Foundation
import Testing
@testable import Mira

/// « En ce moment sur Jellyfin »: tests/fixtures/viewers.json, shared with Mira web and Mira for Windows and Mac. Its
/// texts are French; the iPhone's are English first, so only what they say is compared: the sessions kept, whose they
/// are, where the device is, the clock, how it plays.
@Suite("En ce moment sur Jellyfin")
struct ViewersTests {
    private final class Bundled {}

    private struct Expected: Decodable {
        var id: String
        var who: String
        var how: String
        var why: String?
        var canStop: Bool
    }

    private struct Fixture: Decodable {
        var ownDevice: String
        var sessions: [Viewers.Session]
        var viewers: [Expected]
        var places: [[JSONValue]]
        var clocks: [[JSONValue]]
    }

    private func fixture() throws -> Fixture {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "viewers", withExtension: "json"))
        return try JSON.decoder.decode(Fixture.self, from: Data(contentsOf: url))
    }

    private static func place(_ french: String?) -> Viewers.Place? {
        switch french {
        case "Tailscale": .tailscale
        case "réseau de la maison": .home
        case "Internet": .internet
        default: nil
        }
    }

    @Test("Les sessions qui lisent quelque chose")
    func sessions() throws {
        let fixture = try fixture()
        let viewers = Viewers.parse(fixture.sessions, ownDevice: fixture.ownDevice)
        #expect(viewers.map(\.id) == fixture.viewers.map(\.id))
        for (viewer, expected) in zip(viewers, fixture.viewers) {
            #expect(viewer.canStop == expected.canStop, "\(expected.id)")
            // « ce PC »: this device's own session.
            #expect(viewer.own == (expected.who == "ce PC"), "\(expected.id)")
            if !viewer.own {
                #expect(Viewers.place(viewer.address) == Self.place(expected.who.components(separatedBy: " · ").last), "\(expected.id)")
            }
            let delivery: Viewers.Delivery = expected.how.hasPrefix("Lecture directe") ? .direct
                : expected.how.hasPrefix("Fichier remis") ? .remuxed : .converted
            #expect(Viewers.delivery(viewer) == delivery, "\(expected.id)")
            #expect((Viewers.why(viewer) == nil) == (expected.why == nil), "\(expected.id)")
        }
    }

    @Test("Où est l'appareil")
    func places() throws {
        for row in try fixture().places {
            guard row.count == 2 else { Issue.record("place"); continue }
            let address: String? = if case let .string(text) = row[0] { text } else { nil }
            let expected: String? = if case let .string(text) = row[1] { text } else { nil }
            #expect(Viewers.place(address) == Self.place(expected), "\(address ?? "null")")
        }
    }

    @Test("Heure de lecture")
    func clocks() throws {
        for row in try fixture().clocks {
            guard row.count == 2, case let .number(seconds) = row[0], case let .string(text) = row[1] else { Issue.record("clock"); continue }
            #expect(Viewers.clock(seconds) == text)
        }
    }

    @Test("Codecs et raisons")
    func words() {
        #expect(Viewers.codec("vp9") == "VP9")
        #expect(Viewers.codec("pcm_s16le") == "PCM")
        #expect(Viewers.codec("weird") == "WEIRD")
        #expect(Viewers.codec(nil) == "?")
        #expect(Viewers.reason("Bogus") == "Bogus")
        #expect(Viewers.reason("VideoRangeTypeNotSupported") == "HDR")
    }
}
