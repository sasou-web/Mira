import Foundation
import Testing
@testable import Mira

private final class SyncFixture {
    /// tests/fixtures/syncplay.json, shared with Mira web and Mira for Windows and Mac.
    static func load() throws -> [String: Any] {
        let url = try #require(Bundle(for: SyncFixture.self).url(forResource: "syncplay", withExtension: "json"))
        return try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }
}

@Suite("Regarder ensemble")
struct TogetherTests {
    private func groups(_ value: Any?) throws -> [SyncGroup] {
        let data = try JSONSerialization.data(withJSONObject: value ?? [])
        return try JSON.decoder.decode([SyncGroup].self, from: data)
    }

    @Test("L'heure de Jellyfin et l'écart d'horloge (syncplay.json)")
    func clock() throws {
        let fixture = try SyncFixture.load()
        for case let entry as [Any] in try #require(fixture["times"] as? [Any]) {
            #expect(SyncRules.serverTime(entry[0] as? String) == (entry[1] as? NSNumber)?.doubleValue, "\(entry)")
        }
        for case let entry as [Any] in try #require(fixture["clock"] as? [Any]) {
            let samples = (entry[0] as? [[String: Double]] ?? []).map {
                SyncRules.ClockSample(sent: $0["sent"]!, received: $0["received"]!, serverIn: $0["serverIn"]!, serverOut: $0["serverOut"]!)
            }
            let best = SyncRules.clockOffset(samples)
            #expect(best?.offset == (entry[1] as? NSNumber)?.doubleValue, "\(entry)")
            #expect(best?.trip == (entry[2] as? NSNumber)?.doubleValue, "\(entry)")
        }
    }

    @Test("Ce qu'une commande du groupe demande au lecteur")
    func commands() throws {
        let fixture = try SyncFixture.load()
        for case let entry as [Any] in try #require(fixture["commands"] as? [Any]) {
            let offset = try #require(entry[0] as? NSNumber).doubleValue, now = try #require(entry[1] as? NSNumber).doubleValue
            let data = try #require(entry[2] as? [String: Any])
            let plan = SyncRules.commandPlan(command: data["Command"] as? String, when: data["When"] as? String,
                                             positionTicks: (data["PositionTicks"] as? NSNumber)?.int64Value, offset: offset, now: now)
            if let expected = entry[3] as? [String: Any] {
                #expect(plan?.action.rawValue == expected["action"] as? String, "\(entry)")
                #expect(plan.map { $0.wait * 1000 } == (expected["wait"] as? NSNumber)?.doubleValue, "\(entry)")
                #expect(abs((plan?.position ?? -1) - ((expected["position"] as? NSNumber)?.doubleValue ?? 0)) < 0.000001, "\(entry)")
            } else {
                #expect(plan == nil, "\(entry)")
            }
        }
        #expect(SyncRules.needsSeek(10, 10.6) && !SyncRules.needsSeek(10, 10.4))
    }

    @Test("Le titre d'une file, l'hôte d'un groupe, le groupe d'une invitation, le retour dans le groupe")
    func groupsAndQueue() throws {
        let fixture = try SyncFixture.load()
        for case let entry as [Any] in try #require(fixture["queue"] as? [Any]) {
            let found = SyncRules.queueItem(entry[0] as? [String: Any] ?? [:])
            if let expected = entry[1] as? [String: Any] {
                #expect(found == SyncQueue(itemId: expected["itemId"] as! String, playlistItemId: expected["playlistItemId"] as! String,
                                           startTicks: (expected["startTicks"] as! NSNumber).int64Value))
            } else {
                #expect(found == nil)
            }
        }
        for case let entry as [Any] in try #require(fixture["hosts"] as? [Any]) {
            #expect(SyncRules.groupHost(entry[0] as? String) == entry[1] as? String, "\(entry)")
        }
        #expect(SyncRules.groupHost("mira’s group") == "mira")
        for case let entry as [Any] in try #require(fixture["finds"] as? [Any]) {
            let found = SyncRules.findGroup(try groups(entry[0]), id: entry[1] as? String, host: entry[2] as? String)
            #expect(found?.groupId == entry[3] as? String, "\(entry)")
        }
        for case let entry as [Any] in try #require(fixture["rejoins"] as? [Any]) {
            let lost = try #require(try groups([entry[1]]).first)
            let plan = SyncRules.rejoinPlan(try groups(entry[0]), lost: lost, made: entry[2] as? Bool ?? false)
            let expected: SyncRules.Rejoin? = (entry[3] as? [String: String]).flatMap { $0["join"].map { .join($0) } ?? $0["create"].map { .create($0) } }
            #expect(plan == expected, "\(entry)")
        }
    }

    @Test("Le lien d'invitation")
    func invites() throws {
        let fixture = try SyncFixture.load()
        for case let entry as [Any] in try #require(fixture["invites"] as? [Any]) {
            let server = try #require(URL(string: try #require(entry[0] as? String)))
            #expect(SyncRules.inviteLink(server: server, groupId: entry[1] as! String, host: entry[2] as? String) == entry[3] as? String, "\(entry)")
        }
    }

    @Test("Remplacer le titre du groupe : seulement un autre titre, groupe en cours")
    func replaces() throws {
        let fixture = try SyncFixture.load()
        for case let entry as [Any] in try #require(fixture["replaces"] as? [Any]) {
            let group = try groups(entry[0] is NSNull ? [] : [entry[0]]).first
            let queue = (entry[1] as? [String: Any]).flatMap { fields -> SyncQueue? in
                guard let item = fields["itemId"] as? String else { return nil }
                return SyncQueue(itemId: item, playlistItemId: fields["playlistItemId"] as? String ?? "", startTicks: 0)
            }
            #expect(SyncRules.asksToReplace(group, queue, itemId: entry[2] as! String) == entry[3] as? Bool, "\(entry)")
        }
    }
}
