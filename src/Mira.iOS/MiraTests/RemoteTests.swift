import Foundation
import Testing
@testable import Mira

/// « Play on… »: the devices of tests/fixtures/remote.json, shared with Mira for Windows and Mac (RemoteControl.Targets).
@Suite("Télécommande")
struct RemoteTests {
    private final class Bundled {}

    private struct Expected: Decodable {
        var id: String
        var deviceId: String
        var device: String
        var client: String
        var playing: String?
        var playingId: String?
        var series: String?
        var season: Int?
        var episode: Int?
        var paused: Bool
        var position: Double
        var duration: Double
        var volume: Int
        var muted: Bool
    }

    private struct Targets: Decodable {
        var ownDevice: String
        var sessions: [RemoteRules.Session]
        var expect: [Expected]
    }

    private struct Fixture: Decodable {
        var targets: Targets
    }

    private func fixture() throws -> Fixture {
        let url = try #require(Bundle(for: Bundled.self).url(forResource: "remote", withExtension: "json"))
        return try JSON.decoder.decode(Fixture.self, from: Data(contentsOf: url))
    }

    @Test("Les appareils où lire : pas celui-ci, un par appareil, ceux qui lisent des vidéos")
    func targets() throws {
        let fixture = try fixture().targets
        let found = RemoteRules.targets(fixture.sessions, ownDevice: fixture.ownDevice)
        #expect(found.map(\.id) == fixture.expect.map(\.id))
        for (target, e) in zip(found, fixture.expect) {
            let wanted = RemoteRules.Target(id: e.id, deviceId: e.deviceId, device: e.device, client: e.client, user: "ogsas", playing: e.playing, playingId: e.playingId,
                                            series: e.series, season: e.season, episode: e.episode, paused: e.paused, position: e.position, duration: e.duration,
                                            volume: e.volume, muted: e.muted)
            #expect(target == wanted, "\(e.id)")
        }
    }

    @Test("Lire ailleurs : depuis où ce compte en était, du début pour un titre vu ou demandé du début")
    func start() throws {
        var item = try JSON.decoder.decode(Item.self, from: Data(#"{"Id":"a1","RunTimeTicks":60000000000,"UserData":{"PlaybackPositionTicks":12000000000}}"#.utf8))
        #expect(RemoteRules.startTicks(item, fromStart: false) == 12_000_000_000)
        #expect(RemoteRules.startTicks(item, fromStart: true) == 0)
        item.userData?.played = true
        #expect(RemoteRules.startTicks(item, fromStart: false) == 0)
        #expect(RemoteRules.plainId("90892981-F28A-0CCB-86A6-E55A3A0039C6") == "90892981f28a0ccb86a6e55a3a0039c6")
        #expect(RemoteRules.plainId("../Users") == nil)
    }
}
