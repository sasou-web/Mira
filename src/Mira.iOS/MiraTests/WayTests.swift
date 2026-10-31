import Foundation
import Synchronization
import Testing
@testable import Mira

/// The PC with Jellyfin at ogsas's: its home address, its Tailscale device, and the relay of Built-in Tailscale.
private enum Home {
    static let server = URL(string: "http://192.168.1.198:8096")!
    static let id = "b6c2f1a0-d4e9-4c0a-9f3e-2d1c0b9a8f7e"
    static let relay = URL(string: "http://127.0.0.1:48096/relay-secret-0123456789")!
    static let info = PublicInfo(id: "b6c2f1a0d4e94c0a9f3e2d1c0b9a8f7e", serverName: "DESKTOP-INVEFQH", version: "12.1.0", productName: "Jellyfin Server",
                                 localAddress: "http://192.168.1.198:8096")
    static let pc = WayPlan.Peer(id: "n1", hostName: "DESKTOP-INVEFQH", dnsName: "desktop-invefqh.tail1234.ts.net", ips: ["100.114.247.13", "fd7a:115c:a1e0::1"],
                                 os: "windows", online: true)
    static let phone = WayPlan.Peer(id: "n2", hostName: "iPhone", dnsName: "iphone.tail1234.ts.net", ips: ["100.100.1.2"], os: "iOS", online: true)
    static let laptop = WayPlan.Peer(id: "n3", hostName: "laptop", dnsName: "laptop.tail1234.ts.net", ips: ["100.90.1.3"], os: "linux", online: false)
    static let running = WayPlan.Tailnet(enabled: true, state: "running", online: true, relay: relay, peers: [pc, phone, laptop])
    /// The PC through the relay.
    static let pcBase = "http://127.0.0.1:48096/relay-secret-0123456789/http/100.114.247.13:8096"

    static func route(_ kind: WayPlan.Kind, _ base: String, _ address: String? = nil) -> WayPlan.Route {
        WayPlan.Route(kind: kind, base: URL(string: base)!, address: URL(string: address ?? base)!)
    }
}

@Suite("Chemin vers Jellyfin : les règles")
struct WayPlanTests {
    @Test("À la maison sur le Wi-Fi : l'adresse de la maison, rien d'autre sans Tailscale")
    func home() {
        #expect(WayPlan.candidates(server: Home.server, known: [], network: .local, tailnet: .off) == [Home.route(.home, "http://192.168.1.198:8096")])
        // Away, a home address is someone else's: nothing to try without Tailscale.
        #expect(WayPlan.candidates(server: Home.server, known: [], network: .cellular, tailnet: .off).isEmpty)
    }

    @Test("Loin de la maison : Tailscale intégré essaie les appareils en ligne, ni téléphone ni tablette")
    func away() {
        let routes = WayPlan.candidates(server: Home.server, known: [], network: .cellular, tailnet: Home.running)
        #expect(routes == [Home.route(.builtIn, Home.pcBase, "http://100.114.247.13:8096")])
        // Connecting, or off: nothing through it yet.
        var starting = Home.running
        starting.state = "starting"
        #expect(WayPlan.candidates(server: Home.server, known: [], network: .cellular, tailnet: starting).isEmpty)
    }

    @Test("Adresses connues : la maison, puis l'app Tailscale, puis Tailscale intégré, sans doublon")
    func known() {
        let tailscale = URL(string: "http://100.114.247.13:8096")!
        let routes = WayPlan.candidates(server: Home.server, known: [tailscale, Home.server], network: .local, tailnet: Home.running)
        #expect(routes == [
            Home.route(.home, "http://192.168.1.198:8096"),
            Home.route(.tailscaleApp, "http://100.114.247.13:8096"),
            Home.route(.builtIn, Home.pcBase, "http://100.114.247.13:8096"),
        ])
    }

    @Test("Un autre port : celui de l'adresse, puis 8096")
    func port() {
        let routes = WayPlan.candidates(server: URL(string: "http://192.168.1.198:8097")!, known: [], network: .cellular, tailnet: Home.running)
        #expect(routes.map(\.address.absoluteString) == ["http://100.114.247.13:8097", "http://100.114.247.13:8096"])
    }

    @Test("Nom MagicDNS : l'app Tailscale, puis Tailscale intégré par ce nom et par l'adresse de l'appareil")
    func magicDNS() {
        let server = URL(string: "http://desktop-invefqh.tail1234.ts.net:8096")!
        #expect(WayPlan.candidates(server: server, known: [], network: .cellular, tailnet: Home.running) == [
            Home.route(.tailscaleApp, "http://desktop-invefqh.tail1234.ts.net:8096"),
            Home.route(.builtIn, "http://127.0.0.1:48096/relay-secret-0123456789/http/desktop-invefqh.tail1234.ts.net:8096", "http://desktop-invefqh.tail1234.ts.net:8096"),
            Home.route(.builtIn, Home.pcBase, "http://100.114.247.13:8096"),
        ])
    }

    @Test("Nom court : la maison sur le Wi-Fi, l'app Tailscale ailleurs")
    func shortName() {
        let server = URL(string: "http://desktop-invefqh:8096")!
        #expect(WayPlan.candidates(server: server, known: [], network: .local, tailnet: .off) == [Home.route(.home, "http://desktop-invefqh:8096")])
        #expect(WayPlan.candidates(server: server, known: [], network: .cellular, tailnet: .off) == [Home.route(.tailscaleApp, "http://desktop-invefqh:8096")])
    }

    @Test("Internet : l'adresse telle quelle, partout")
    func internet() {
        let server = URL(string: "https://jellyfin.example.com")!
        #expect(WayPlan.candidates(server: server, known: [], network: .cellular, tailnet: .off) == [Home.route(.direct, "https://jellyfin.example.com")])
    }

    @Test("Seulement Tailscale intégré (tests sur le simulateur) : même pour ce Mac")
    func only() {
        let routes = WayPlan.candidates(server: URL(string: "http://127.0.0.1:18097")!, known: [], network: .local, tailnet: Home.running, only: [.builtIn])
        #expect(routes.map(\.kind) == [.builtIn, .builtIn])
        #expect(routes.map(\.address.absoluteString) == ["http://100.114.247.13:18097", "http://100.114.247.13:8096"])
    }

    @Test("Adresse par le relais : schéma, hôte et port, puis le chemin du serveur")
    func relayed() {
        let relayed = { (text: String) in WayPlan.relayed(URL(string: text)!, relay: Home.relay)?.absoluteString }
        #expect(relayed("http://100.114.247.13:8096") == Home.pcBase)
        #expect(relayed("http://[fd7a:115c:a1e0::1]:8096") == "http://127.0.0.1:48096/relay-secret-0123456789/http/%5Bfd7a:115c:a1e0::1%5D:8096")
        #expect(relayed("https://desktop-invefqh.tail1234.ts.net/jellyfin/") == "http://127.0.0.1:48096/relay-secret-0123456789/https/desktop-invefqh.tail1234.ts.net:443/jellyfin")
        #expect(relayed("ftp://100.114.247.13") == nil)
        let slash = WayPlan.relayed(URL(string: "http://100.114.247.13:8096")!, relay: URL(string: "http://127.0.0.1:48096/relay-secret-0123456789/")!)
        #expect(slash?.absoluteString == Home.pcBase)
    }

    @Test("Une adresse du serveur sur un autre chemin : même chemin, même requête ; celles des autres jamais")
    func map() {
        let base = URL(string: Home.pcBase)!
        let map = { (text: String, server: URL) in WayPlan.map(URL(string: text)!, from: server, to: base)?.absoluteString }
        #expect(map("http://192.168.1.198:8096/Items/abc/Images/Primary?maxWidth=320&tag=t1", Home.server) == Home.pcBase + "/Items/abc/Images/Primary?maxWidth=320&tag=t1")
        #expect(map("http://192.168.1.198:8096", Home.server) == Home.pcBase)
        #expect(map("http://192.168.1.198:8096/Items/a%20b/x", Home.server) == Home.pcBase + "/Items/a%20b/x")
        let proxied = URL(string: "https://example.com/jellyfin")!
        #expect(map("https://example.com/jellyfin/Videos/1/master.m3u8?ApiKey=k#t", proxied) == Home.pcBase + "/Videos/1/master.m3u8?ApiKey=k#t")
        #expect(map("https://example.com/jellyfinX/a", proxied) == nil)
        #expect(map("https://example.com:8443/jellyfin/a", proxied) == nil)
        #expect(map("http://example.com/jellyfin/a", proxied) == nil)
        #expect(map("https://image.tmdb.org/t/p/w500/a.jpg", Home.server) == nil)
    }

    @Test("Le chemin pris : le premier qui répond, une fois ceux d'avant abandonnés ; plus loin après le délai de grâce")
    func choose() {
        #expect(WayPlan.choose([true, nil], graceOver: false) == 0)
        #expect(WayPlan.choose([nil, true], graceOver: false) == nil)
        #expect(WayPlan.choose([nil, true], graceOver: true) == 1)
        #expect(WayPlan.choose([false, true], graceOver: false) == 1)
        #expect(WayPlan.choose([nil, false, true], graceOver: true) == 2)
        #expect(WayPlan.choose([false, false], graceOver: true) == nil)
        #expect(WayPlan.choose([], graceOver: true) == nil)
    }

    @Test("Ce qui est dit quand rien ne répond")
    func hint() {
        let hint = { (network: WayPlan.Network, tailnet: WayPlan.Tailnet, server: URL, known: [URL]) in
            WayPlan.hint(network: network, tailnet: tailnet, server: server, known: known)
        }
        let tailscale = URL(string: "http://100.114.247.13:8096")!
        #expect(hint(.none, Home.running, Home.server, []) == .noNetwork)
        // Off: said only when Tailscale is the way to this server.
        #expect(hint(.cellular, .off, Home.server, []) == .tailscaleOff)
        #expect(hint(.local, .off, Home.server, []) == nil)
        #expect(hint(.local, .off, tailscale, []) == .tailscaleOff)
        #expect(hint(.cellular, .off, URL(string: "https://jellyfin.example.com")!, []) == nil)
        var tailnet = Home.running
        for (state, expected) in [("needs-login", WayPlan.Hint.signedOut), ("expired", .expired), ("machine-auth", .approval), ("starting", .starting), ("off", .starting)] {
            tailnet.state = state
            #expect(hint(.cellular, tailnet, Home.server, []) == expected, "\(state)")
        }
        tailnet.state = "running"
        tailnet.online = false
        #expect(hint(.cellular, tailnet, Home.server, []) == .offline)
        tailnet.online = true
        // The PC known by its Tailscale address, offline on the network: named.
        var offline = Home.pc
        offline.online = false
        tailnet.peers = [offline, Home.phone]
        #expect(hint(.cellular, tailnet, Home.server, [tailscale]) == .serverOffline("desktop-invefqh"))
        #expect(hint(.cellular, tailnet, Home.server, []) == .serverNotFound)
        tailnet.peers = [Home.pc]
        #expect(hint(.cellular, tailnet, Home.server, [tailscale]) == .serverNotFound)
    }

    @Test("L'appareil d'une adresse : son IP Tailscale, son nom MagicDNS entier ou court")
    func device() {
        let peers = [Home.phone, Home.pc]
        let device = { (text: String) in WayPlan.device(of: [URL(string: text)!], in: peers)?.id }
        #expect(device("http://100.114.247.13:8096") == "n1")
        #expect(device("http://[fd7a:115c:a1e0::1]:8096") == "n1")
        #expect(device("http://desktop-invefqh.tail1234.ts.net:8096") == "n1")
        #expect(device("http://DESKTOP-INVEFQH.tail1234.ts.net.:8096") == "n1")
        #expect(device("http://desktop-invefqh:8096") == "n1")
        #expect(device("http://192.168.1.198:8096") == nil)
        #expect(device("https://jellyfin.example.com") == nil)
    }

    @Test("Adresses de Tailscale, de cet appareil, et la même adresse écrite autrement")
    func addresses() {
        let tailnet = { (text: String) in WayPlan.isTailnet(URL(string: text)!) }
        #expect(tailnet("http://100.64.0.1:8096"))
        #expect(tailnet("http://100.127.255.254"))
        #expect(!tailnet("http://100.128.0.1"))
        #expect(!tailnet("http://100.63.255.255"))
        #expect(tailnet("http://[fd7a:115c:a1e0::1]:8096"))
        #expect(!tailnet("http://[fd00::1]:8096"))
        #expect(tailnet("http://desktop-invefqh.tail1234.ts.net:8096"))
        #expect(!tailnet("http://192.168.1.198:8096"))
        let loopback = { (text: String) in WayPlan.isLoopback(URL(string: text)!) }
        #expect(loopback("http://127.0.0.1:18097") && loopback("http://[::1]:8096") && loopback("http://localhost:8096"))
        #expect(!loopback("http://192.168.1.198:8096"))
        let same = { (a: String, b: String) in WayPlan.same(URL(string: a)!, URL(string: b)!) }
        #expect(same("http://NAS", "http://nas:80"))
        #expect(same("http://nas:8096/", "http://nas:8096"))
        #expect(same("https://nas", "https://nas:443"))
        #expect(!same("https://nas", "http://nas"))
        #expect(!same("http://nas:8096/jellyfin", "http://nas:8096"))
    }

    @Test("La connexion de cet iPhone à Tailscale qui expire : dite 14 jours avant, ni plus tôt, ni une fois expirée")
    func expiry() {
        let now = Date(timeIntervalSince1970: 1_793_268_000)
        func device(_ expires: String?) -> WayPlan.Peer { WayPlan.Peer(id: "n0", hostName: "mira-iphone", keyExpiry: expires) }
        #expect(WayPlan.expiry(device("2026-11-12T09:00:00Z"), now: now) == WayPlan.Peer.date("2026-11-12T09:00:00Z"))
        #expect(WayPlan.expiry(device("2026-11-12T11:00:00Z"), now: now) == nil)
        #expect(WayPlan.expiry(device("2026-10-29T09:59:59Z"), now: now) == nil)
        // Key expiry turned off for it in Tailscale's admin console: no date.
        #expect(WayPlan.expiry(device(nil), now: now) == nil)
        #expect(WayPlan.expiry(nil, now: now) == nil)
        // As Go writes a time: with nanoseconds, or with an offset.
        #expect((WayPlan.Peer.date("2026-10-29T10:00:00.123456789Z")?.timeIntervalSince1970 ?? 0) > 1_793_268_000.12)
        #expect(WayPlan.Peer.date("2026-10-29T12:00:00+02:00") == now)
        #expect(WayPlan.Peer.date("hier") == nil)
    }

    @Test("Les appareils de Tailscale intégré, lus dans l'état de sa bibliothèque")
    func status() throws {
        let json = """
        {"state":"running","offline":false,"self":{"id":"n0","hostName":"mira-iphone","dnsName":"mira-iphone.tail1234.ts.net","ips":["100.101.1.1"],"os":"iOS","online":true},
         "user":"ogsas@example.com","tailnet":"example.com","magicDNSSuffix":"tail1234.ts.net",
         "peers":[{"id":"n1","hostName":"DESKTOP-INVEFQH","dnsName":"desktop-invefqh.tail1234.ts.net","ips":["100.114.247.13","fd7a:115c:a1e0::1"],"os":"windows","online":true,
                   "keyExpiry":"2027-04-27T08:00:00Z","lastSeen":"2026-10-29T19:00:00Z"},{"id":"n4","hostName":"nas"}],
         "warnings":["…"],"relay":"http://127.0.0.1:48096/relay-secret-0123456789"}
        """
        let status = try JSONDecoder().decode(TailnetStatus.self, from: Data(json.utf8))
        #expect(status.device?.label == "mira-iphone")
        #expect(status.user == "ogsas@example.com")
        // Without « cache-network-maps » in the network's policy: the setting shows (OutageRows).
        #expect(status.outageCache == nil)
        #expect(try JSONDecoder().decode(TailnetStatus.self, from: Data(#"{"state":"running","outageCache":true}"#.utf8)).outageCache == true)
        #expect(OutageRows.setting.contains(#""attr": ["cache-network-maps"]"#))
        let tailnet = WayPlan.Tailnet(status, enabled: true)
        #expect(tailnet.running && tailnet.online)
        #expect(tailnet.relay == Home.relay)
        var pc = Home.pc
        pc.keyExpiry = "2027-04-27T08:00:00Z"
        pc.lastSeen = "2026-10-29T19:00:00Z"
        #expect(tailnet.peers.first == pc)
        #expect(pc.expires == Date(timeIntervalSince1970: 1_808_812_800))
        #expect(pc.seen == Date(timeIntervalSince1970: 1_793_300_400))
        // What the library leaves out: no name, no address, offline.
        #expect(tailnet.peers.last == WayPlan.Peer(id: "n4", hostName: "nas"))
        #expect(tailnet.peers.last?.label == "nas")
        #expect(!WayPlan.Tailnet(status, enabled: false).running)

        let failed = try JSONDecoder().decode(TailnetStatus.self, from: Data(#"{"state":"off","error":{"code":"config","message":"no folder for the node"}}"#.utf8))
        #expect(failed.error == TailnetFailure(code: "config", message: "no folder for the node"))
        #expect(WayPlan.Tailnet(failed, enabled: true).starting)
        #expect(WayPlan.Tailnet(TailnetStatus(state: "running", offline: true, relay: Home.relay.absoluteString), enabled: true).online == false)
    }
}

/// A stand-in network for the way: what each base answers with, and after how long. A base it does not know answers
/// nothing, as an address where no one is.
private final class Answers: Sendable {
    private let table: Mutex<[String: (delay: Double, info: PublicInfo?)]>
    private let asked = Mutex<[String]>([])

    init(_ table: [String: (delay: Double, info: PublicInfo?)] = [:]) {
        self.table = Mutex(table)
    }

    func set(_ base: String, after delay: Double = 0, _ info: PublicInfo?) {
        table.withLock { $0[base] = (delay, info) }
    }

    func remove(_ base: String) {
        _ = table.withLock { $0.removeValue(forKey: base) }
    }

    var bases: [String] { asked.withLock { $0 } }

    var ask: Way.Ask {
        { base, timeout in
            self.asked.withLock { $0.append(base.absoluteString) }
            guard let entry = self.table.withLock({ $0[base.absoluteString] }) else {
                try? await Task.sleep(for: .seconds(min(timeout, 0.3)))
                return nil
            }
            try? await Task.sleep(for: .seconds(entry.delay))
            return Task.isCancelled ? nil : entry.info
        }
    }
}

/// What Built-in Tailscale tells the way, changed by the test.
private final class TailnetState: Sendable {
    private let states: Mutex<[WayPlan.Tailnet]>
    private let reads = Mutex(0)

    /// The states read in turn, the last one kept.
    init(_ states: [WayPlan.Tailnet]) {
        self.states = Mutex(states)
    }

    var read: @Sendable () async -> WayPlan.Tailnet {
        {
            self.reads.withLock { $0 += 1 }
            return self.states.withLock { $0.count > 1 ? $0.removeFirst() : $0[0] }
        }
    }

    var count: Int { reads.withLock { $0 } }
}

@Suite("Chemin vers Jellyfin : la recherche")
struct WayTests {
    private let timing = Way.Timing(grace: 0.2, afterLost: 0, afterNone: 0, tailnet: 1)
    private let suite = "mira.tests.way.\(UUID().uuidString)"
    private let item = URL(string: "http://192.168.1.198:8096/Items/1?fields=Overview")!

    private func way(_ answers: Answers) -> Way {
        Way(ask: answers.ask, timing: timing, suite: suite)
    }

    private func clean() {
        UserDefaults.standard.removePersistentDomain(forName: suite)
    }

    @Test("À la maison : la maison répond, prise sans attendre, les adresses gardées telles quelles")
    func home() async {
        defer { clean() }
        let answers = Answers(["http://192.168.1.198:8096": (0, Home.info)])
        let way = way(answers)
        await way.use(Home.server, id: Home.id)
        #expect(await way.map(item) == item)
        #expect(await way.current()?.kind == .home)
        // Anything else is never rewritten.
        let poster = URL(string: "https://image.tmdb.org/t/p/w500/a.jpg")!
        #expect(await way.map(poster) == poster)
    }

    @Test("Loin de la maison : l'appareil qui répond avec l'id de ce serveur, par le relais ; son adresse gardée")
    func away() async {
        defer { clean() }
        // Another Jellyfin on the network (a friend's NAS), faster: never taken.
        let nas = WayPlan.Peer(id: "n5", hostName: "nas", dnsName: "nas.tail1234.ts.net", ips: ["100.70.0.9"], os: "linux", online: true)
        let answers = Answers([
            Home.pcBase: (0.05, Home.info),
            "http://127.0.0.1:48096/relay-secret-0123456789/http/100.70.0.9:8096": (0, PublicInfo(id: "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f", productName: "Jellyfin Server")),
        ])
        let way = way(answers)
        var tailnet = Home.running
        tailnet.peers = [nas, Home.pc]
        await way.network(.cellular)
        await way.tailnet(tailnet)
        await way.use(Home.server, id: Home.id)
        #expect(await way.map(item).absoluteString == Home.pcBase + "/Items/1?fields=Overview")
        #expect(await way.current()?.kind == .builtIn)
        #expect(await way.addresses().map(\.absoluteString) == ["http://100.114.247.13:8096"])

        // The next time (Mira opened again), the PC's Tailscale address is known from the start.
        let again = Way(ask: answers.ask, timing: timing, suite: suite)
        await again.use(Home.server, id: Home.id)
        #expect(await again.addresses().map(\.absoluteString) == ["http://100.114.247.13:8096"])
        // Forgotten when the account signs out.
        await again.forget(Home.id)
        #expect(await again.addresses().isEmpty)
        let signedOut = Way(ask: answers.ask, timing: timing, suite: suite)
        await signedOut.use(Home.server, id: Home.id)
        #expect(await signedOut.addresses().isEmpty)
    }

    @Test("Le Wi-Fi d'ailleurs à la même adresse que la maison : un autre Jellyfin n'est jamais pris")
    func otherServer() async {
        defer { clean() }
        let answers = Answers([
            "http://192.168.1.198:8096": (0, PublicInfo(id: "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f", productName: "Jellyfin Server")),
            Home.pcBase: (0.05, Home.info),
        ])
        let way = way(answers)
        await way.tailnet(Home.running)
        await way.use(Home.server, id: Home.id)
        #expect(await way.map(item).absoluteString.hasPrefix(Home.pcBase))
        #expect(await way.current()?.kind == .builtIn)
    }

    @Test("Une adresse plus loin qui répond : prise après le délai de grâce, sans attendre celles d'avant")
    func grace() async {
        defer { clean() }
        let answers = Answers(["http://192.168.1.198:8096": (5, Home.info), Home.pcBase: (0.05, Home.info)])
        let way = way(answers)
        await way.tailnet(Home.running)
        await way.use(Home.server, id: Home.id)
        let started = Date()
        #expect(await way.map(item).absoluteString.hasPrefix(Home.pcBase))
        #expect(Date().timeIntervalSince(started) < 2)
    }

    @Test("Rien ne répond : pourquoi, dans l'ordre de ce qui manque")
    func nothing() async {
        defer { clean() }
        let way = way(Answers())
        var tailnet = Home.running
        tailnet.state = "needs-login"
        await way.network(.cellular)
        await way.tailnet(tailnet)
        await way.use(Home.server, id: Home.id)
        #expect(await way.map(item) == item)
        #expect(await way.current() == nil)
        #expect(await way.hint() == .signedOut)
        // Signed in again: the way is looked for at once.
        let answers = Answers([Home.pcBase: (0, Home.info)])
        let next = Way(ask: answers.ask, timing: timing, suite: suite)
        await next.network(.cellular)
        await next.tailnet(tailnet)
        await next.use(Home.server, id: Home.id)
        _ = await next.map(item)
        await next.tailnet(Home.running)
        #expect(await next.map(item).absoluteString.hasPrefix(Home.pcBase))
        #expect(await next.hint() == nil)
    }

    @Test("Tailscale intégré qui se connecte (Mira vient de s'ouvrir) : attendu avant de dire que rien ne répond")
    func connecting() async {
        defer { clean() }
        var starting = Home.running
        starting.state = "starting"
        starting.relay = nil
        starting.peers = []
        let state = TailnetState([starting, starting, Home.running])
        let answers = Answers([Home.pcBase: (0, Home.info)])
        let way = way(answers)
        await way.setup(readTailnet: state.read, onChange: { _, _ in })
        await way.network(.cellular)
        await way.use(Home.server, id: Home.id)
        #expect(await way.map(item).absoluteString.hasPrefix(Home.pcBase))
        #expect(state.count >= 3)
    }

    @Test("Chemin coupé : un autre est cherché, et la requête repart par lui")
    func lost() async {
        defer { clean() }
        let tailscale = URL(string: "http://100.114.247.13:8096")!
        // Tailscale's app on: the PC's Tailscale address answers as it is, first.
        let answers = Answers(["http://100.114.247.13:8096": (0, Home.info), Home.pcBase: (0, Home.info)])
        UserDefaults(suiteName: suite)?.set([tailscale.absoluteString], forKey: "mira.way.b6c2f1a0d4e94c0a9f3e2d1c0b9a8f7e")
        let way = way(answers)
        await way.network(.cellular)
        await way.tailnet(Home.running)
        await way.use(Home.server, id: Home.id)
        let sent = await way.map(item)
        #expect(sent.absoluteString == "http://100.114.247.13:8096/Items/1?fields=Overview")
        #expect(await way.current()?.kind == .tailscaleApp)
        // Tailscale's app turned off: the request finds the way cut.
        answers.remove("http://100.114.247.13:8096")
        #expect(await way.lost(sent))
        #expect(await way.current()?.kind == .builtIn)
        // A request that left on another way than the one taken says nothing of it.
        #expect(await way.lost(URL(string: "http://192.168.1.198:8096/Items/1")!) == false)
        #expect(await way.current()?.kind == .builtIn)
    }

    @Test("De retour à la maison : le chemin pris reste pendant que la maison est cherchée, puis la maison")
    func backHome() async {
        defer { clean() }
        let answers = Answers(["http://192.168.1.198:8096": (0.1, Home.info), Home.pcBase: (0, Home.info)])
        let way = way(answers)
        await way.network(.cellular)
        await way.tailnet(Home.running)
        await way.use(Home.server, id: Home.id)
        #expect(await way.map(item).absoluteString.hasPrefix(Home.pcBase))
        await way.network(.local)
        // Meanwhile, the relay still takes the requests.
        #expect(await way.map(item).absoluteString.hasPrefix(Home.pcBase))
        try? await Task.sleep(for: .seconds(0.5))
        #expect(await way.current()?.kind == .home)
        #expect(await way.map(item) == item)
        // Away again: the home way is dropped at once.
        await way.network(.cellular)
        #expect(await way.map(item).absoluteString.hasPrefix(Home.pcBase))
    }

    @Test("Un autre serveur choisi : ce que la recherche d'avant trouve ensuite est oublié")
    func otherChoice() async {
        defer { clean() }
        let other = URL(string: "http://192.168.1.50:8096")!
        let answers = Answers([
            "http://192.168.1.198:8096": (0.4, Home.info),
            "http://192.168.1.50:8096": (0, PublicInfo(id: "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f", productName: "Jellyfin Server")),
        ])
        let way = way(answers)
        await way.use(Home.server, id: Home.id)
        await way.use(other, id: "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f")
        let address = URL(string: "http://192.168.1.50:8096/Items/2")!
        #expect(await way.map(address) == address)
        try? await Task.sleep(for: .seconds(0.6))
        #expect(await way.current()?.address == other)
        // An address of the server signed out of is no longer the server's.
        #expect(await way.map(item) == item)
    }
}
