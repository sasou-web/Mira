import Foundation
import Synchronization

/// The way to Jellyfin from this iPhone: at home its address on the home network, away the address as it is (the
/// Internet, or Tailscale with its app on), else Built-in Tailscale through its relay in Mira. Every request is written
/// with the server's address as signed in (pictures and pages keep their addresses), and takes the way chosen as it
/// leaves (`map`). Chosen again when the network changes, Mira comes back, Built-in Tailscale connects, or a request
/// finds the way cut; a way is taken only once the server there answered with its id, before any token goes.
actor Way {
    static let shared = Way(followsApp: true)

    /// What no way reaching Jellyfin comes from, read by Messages on any thread.
    static let hint = Mutex<WayPlan.Hint?>(nil)
    /// The kind of the way taken, read on any thread (the quality asked at home).
    static let taken = Mutex<WayPlan.Kind?>(nil)
    /// The server chosen by the app, taken by the way before anything else it does: a request sent before the way heard
    /// of the server already goes its way.
    private static let chosen = Mutex<(server: URL, id: String)?>(nil)

    /// How long the way waits, in seconds: for a better way once one further down answered, before looking again after
    /// a way was found cut or none at all, and for Built-in Tailscale to connect.
    struct Timing: Sendable {
        var grace = 1.0
        var afterLost = 3.0
        var afterNone = 5.0
        var tailnet = 10.0
    }

    /// Jellyfin's public information at a base (System/Info/Public), within a time; nil when nothing answers.
    typealias Ask = @Sendable (_ base: URL, _ timeout: TimeInterval) async -> PublicInfo?

    /// The app's own way follows the server it chose (`select`) and says what it finds to Messages.
    private let followsApp: Bool
    private let ask: Ask
    private let timing: Timing
    /// Where the addresses learned are kept (UserDefaults; a suite of their own in the tests).
    private let suite: String?

    private var server: URL?
    private var serverId: String?
    private var known: [URL] = []
    private var route: WayPlan.Route?
    private var resolving: Task<WayPlan.Route?, Never>?
    /// Bumped by each search: what an older one finds afterwards is dropped.
    private var generation = 0
    private var searchedAt = Date.distantPast
    private var network = WayPlan.Network.local
    private var tailnet = WayPlan.Tailnet.off
    private var readTailnet: (@Sendable () async -> WayPlan.Tailnet)?
    private var onChange: (@Sendable (WayPlan.Route?, WayPlan.Hint?) -> Void)?
    /// The kinds of way the UI tests allow (a way through Built-in Tailscale only, though the stand-in Jellyfin is here).
    private var only: Set<WayPlan.Kind>?
    private var said: WayPlan.Hint?

    private static let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.waitsForConnectivity = false
        // Every way through the relay has the same host (127.0.0.1): all of them asked at once.
        configuration.httpMaximumConnectionsPerHost = 32
        configuration.urlCache = nil
        return URLSession(configuration: configuration)
    }()

    init(followsApp: Bool = false, ask: @escaping Ask = Way.askJellyfin, timing: Timing = Timing(), suite: String? = nil) {
        self.followsApp = followsApp
        self.ask = ask
        self.timing = timing
        self.suite = suite
    }

    /// `readTailnet` gives Built-in Tailscale's state as it is now (its devices online); `onChange` hears of each way
    /// taken (nil: none reaches the server, and why).
    func setup(readTailnet: @escaping @Sendable () async -> WayPlan.Tailnet, onChange: @escaping @Sendable (WayPlan.Route?, WayPlan.Hint?) -> Void,
               only: Set<WayPlan.Kind>? = nil) {
        self.readTailnet = readTailnet
        self.onChange = onChange
        self.only = only
    }

    /// The server signed in to, or about to be (nil: signed out): its other addresses known from before, and a way
    /// chosen at once. Taken at once by the next request, whatever the order the tasks run in.
    static func select(_ server: URL?, id: String?) {
        let value: (server: URL, id: String)? = if let server, let id { (server, id) } else { nil }
        chosen.withLock { $0 = value }
        Task { await shared.adopt() }
    }

    /// The server chosen by the app, if the way does not have it yet.
    private func adopt() {
        guard followsApp else { return }
        let chosen = Self.chosen.withLock { $0 }
        guard chosen?.server != server || chosen?.id != serverId else { return }
        use(chosen?.server, id: chosen?.id)
    }

    /// The server to find a way to (nil: none), its other addresses known from before.
    func use(_ server: URL?, id: String?) {
        self.server = server
        serverId = id
        known = id.map(knownAddresses) ?? []
        generation += 1
        resolving?.cancel()
        resolving = nil
        route = nil
        searchedAt = .distantPast
        publish(nil, hint: nil)
        if server != nil, id != nil { resolve() }
    }

    func network(_ network: WayPlan.Network) {
        adopt()
        guard network != self.network else { return }
        self.network = network
        // A home way on another network, or none taken: chosen again. Any other way works on as it is while the next
        // one is looked for (the home network may be back).
        again(keep: route.map { $0.kind != .home || network == .local } ?? false)
    }

    func tailnet(_ tailnet: WayPlan.Tailnet) {
        adopt()
        let before = self.tailnet
        self.tailnet = tailnet
        if route?.kind == .builtIn, !tailnet.running || tailnet.relay != before.relay {
            again(keep: false)
        } else if route == nil, tailnet.running, !before.running {
            again(keep: false)
        } else if route == nil, resolving == nil, let server {
            // Nothing taken: what to say follows Built-in Tailscale (signed out, then connecting…).
            publish(nil, hint: WayPlan.hint(network: network, tailnet: tailnet, server: server, known: known))
        }
    }

    /// Mira is back on screen: the way is looked for again, the one taken kept meanwhile.
    func wake() {
        adopt()
        again(keep: route != nil)
    }

    /// Asked again by the person (« Try again »): a way is looked for now.
    func retry() {
        adopt()
        again(keep: route != nil)
    }

    /// The way taken (nil while none).
    func current() -> WayPlan.Route? { route }

    /// Why no way is taken, once a search found none.
    func hint() -> WayPlan.Hint? { said }

    /// The other addresses of the server known so far, the latest first.
    func addresses() -> [URL] { known }

    /// An address of the server on the way taken; an address of anything else as it is. While no way is known, the
    /// search going on is waited for; one that just failed is not run again for 5 s (each request would wait for it).
    func map(_ url: URL) async -> URL {
        adopt()
        guard let server, WayPlan.map(url, from: server, to: server) != nil else { return url }
        var current = route
        // A search may end without a word when a newer one replaced it (the network changed meanwhile): that one is
        // waited for too, three at most.
        for _ in 0..<3 where current == nil {
            guard let search = resolving ?? (Date().timeIntervalSince(searchedAt) < timing.afterNone ? nil : resolve()) else { break }
            current = await search.value ?? route
            guard self.server == server else { return url }
        }
        guard let current, !WayPlan.same(current.base, server) else { return url }
        return WayPlan.map(url, from: server, to: current.base) ?? url
    }

    /// A request on `url` (as it left, on its way) found it cut: another way is looked for, once every 3 s at most
    /// (`Timing.afterLost`).
    /// True when another way was found, to send the request again there.
    @discardableResult
    func lost(_ url: URL) async -> Bool {
        adopt()
        guard let failed = route, url.absoluteString.hasPrefix(failed.base.absoluteString) else { return false }
        if Date().timeIntervalSince(searchedAt) < timing.afterLost {
            // Just looked for: the way found then stands.
            return false
        }
        again(keep: false)
        guard let next = await resolving?.value else { return false }
        return next != failed
    }

    /// The base of a Tailscale address through Built-in Tailscale's relay, when it is connected (the sign-in screen
    /// finds a server there).
    func builtIn(_ address: URL) async -> URL? {
        let tailnet = await fresh()
        guard tailnet.running, WayPlan.isTailnet(address), let relay = tailnet.relay else { return nil }
        return WayPlan.relayed(address, relay: relay)
    }

    /// The Jellyfin servers of the Tailscale network, through Built-in Tailscale: each device online asked on 8096.
    func servers() async -> [(info: ServerInfo, device: String)] {
        let tailnet = await fresh()
        guard tailnet.running, let relay = tailnet.relay else { return [] }
        let ask = self.ask
        var asked: [(URL, URL, String)] = []
        for peer in WayPlan.peers(tailnet.peers) {
            guard let ip = peer.ips.first(where: { ServerAddress.IPv4($0) != nil }) ?? peer.ips.first,
                  let address = URL(string: "http://\(ip.contains(":") ? "[\(ip)]" : ip):8096"),
                  let base = WayPlan.relayed(address, relay: relay) else { continue }
            asked.append((address, base, peer.label))
        }
        return await withTaskGroup(of: (Int, ServerInfo?, String).self) { group in
            for (index, (address, base, name)) in asked.enumerated() {
                group.addTask {
                    let info = await ask(base, 8)
                    guard let info, let id = info.id, info.productName.map({ $0.localizedCaseInsensitiveContains("Jellyfin") }) ?? true else { return (index, nil, name) }
                    return (index, ServerInfo(address: address, id: id, name: info.serverName ?? name, version: info.version ?? ""), name)
                }
            }
            var found: [(Int, ServerInfo, String)] = []
            for await (index, info, name) in group {
                if let info, !found.contains(where: { $0.1.id == info.id }) { found.append((index, info, name)) }
            }
            return found.sorted { $0.0 < $1.0 }.map { (info: $0.1, device: $0.2) }
        }
    }

    // MARK: Looking for a way

    private func again(keep: Bool) {
        guard server != nil, serverId != nil else { return }
        generation += 1
        resolving?.cancel()
        resolving = nil
        if !keep, route != nil {
            route = nil
            publish(nil, hint: nil)
        }
        resolve()
    }

    @discardableResult
    private func resolve() -> Task<WayPlan.Route?, Never> {
        if let resolving { return resolving }
        let mine = generation
        let task = Task { await self.search(mine) }
        resolving = task
        return task
    }

    private func search(_ mine: Int) async -> WayPlan.Route? {
        guard let server, let serverId else { return nil }
        var tailnet = await fresh()
        var found = await probe(WayPlan.candidates(server: server, known: known, network: network, tailnet: tailnet, only: only), id: serverId)
        // Built-in Tailscale still connecting (Mira just opened): 10 s more for it before giving up.
        if found == nil, tailnet.starting {
            for _ in 0..<Int(timing.tailnet * 2) where !Task.isCancelled && generation == mine {
                try? await Task.sleep(for: .milliseconds(500))
                tailnet = await fresh()
                if !tailnet.starting { break }
            }
            if tailnet.running, generation == mine {
                let routes = WayPlan.candidates(server: server, known: known, network: network, tailnet: tailnet, only: only).filter { $0.kind == .builtIn }
                found = await probe(routes, id: serverId)
            }
        }
        guard generation == mine, self.server == server else { return nil }
        resolving = nil
        searchedAt = Date()
        if let found {
            learn(found.route.address, found.local)
            if found.route != route { route = found.route }
            publish(found.route, hint: nil)
        } else {
            route = nil
            publish(nil, hint: WayPlan.hint(network: network, tailnet: tailnet, server: server, known: known))
        }
        return route
    }

    /// Built-in Tailscale as it is now, from the library when the app gave a way to read it.
    private func fresh() async -> WayPlan.Tailnet {
        if let readTailnet { tailnet = await readTailnet() }
        return tailnet
    }

    /// Asks every way at once; the first in order that answers with this server is taken once those before it gave up,
    /// one further down after a second of grace.
    private func probe(_ routes: [WayPlan.Route], id: String) async -> (route: WayPlan.Route, local: URL?)? {
        guard !routes.isEmpty else { return nil }
        enum Event: Sendable { case answer(Int, PublicInfo?), grace }
        let wanted = Self.plain(id)
        let (ask, grace) = (self.ask, timing.grace)
        return await withTaskGroup(of: Event.self) { group in
            for (index, route) in routes.enumerated() {
                let timeout: TimeInterval = route.kind == .builtIn ? 8 : route.kind == .home ? 3 : 4
                group.addTask { .answer(index, await ask(route.base, timeout)) }
            }
            var answers = [Bool?](repeating: nil, count: routes.count)
            var locals = [URL?](repeating: nil, count: routes.count)
            var graceStarted = false, graceOver = false
            while let event = await group.next() {
                switch event {
                case let .answer(index, info):
                    answers[index] = info?.id.map(Self.plain) == wanted
                    locals[index] = info?.localAddress.flatMap { try? ServerAddress.normalize($0) }
                case .grace:
                    graceOver = true
                }
                if let chosen = WayPlan.choose(answers, graceOver: graceOver) {
                    group.cancelAll()
                    return (routes[chosen], locals[chosen])
                }
                if !answers.contains(nil) {
                    group.cancelAll()
                    return nil
                }
                if !graceStarted, answers.contains(true) {
                    graceStarted = true
                    group.addTask {
                        try? await Task.sleep(for: .seconds(grace))
                        return .grace
                    }
                }
            }
            return nil
        }
    }

    /// Jellyfin's public information at a base, within `timeout`. Never throws.
    static let askJellyfin: Ask = { base, timeout in
        var request = URLRequest(url: base.appending(path: "System/Info/Public"), cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        guard let (data, response) = try? await Way.session.data(for: request), (response as? HTTPURLResponse)?.statusCode == 200, data.count < 1_000_000,
              let info = try? JSON.decoder.decode(PublicInfo.self, from: data), info.id?.isEmpty == false else { return nil }
        return info
    }

    private static func plain(_ id: String) -> String { id.lowercased().filter { $0 != "-" } }

    // MARK: What the way says

    private func publish(_ route: WayPlan.Route?, hint: WayPlan.Hint?) {
        said = hint
        if followsApp {
            Self.taken.withLock { $0 = route?.kind }
            Self.hint.withLock { $0 = hint }
        }
        onChange?(route, hint)
    }

    // MARK: Addresses known

    /// Another address of the server that answered: kept for the next times (its Tailscale address, found by Built-in
    /// Tailscale, and the one Jellyfin gives for the home network).
    private func learn(_ address: URL, _ local: URL?) {
        guard let server, let serverId else { return }
        var list = known
        for address in [address, local].compactMap({ $0 }) where !WayPlan.same(address, server) && !list.contains(where: { WayPlan.same($0, address) }) {
            list.insert(address, at: 0)
        }
        list = Array(list.prefix(6))
        guard list != known else { return }
        known = list
        defaults.set(list.map(\.absoluteString), forKey: Self.key(serverId))
    }

    private func knownAddresses(_ serverId: String) -> [URL] {
        (defaults.stringArray(forKey: Self.key(serverId)) ?? []).compactMap { try? ServerAddress.normalize($0) }
    }

    private var defaults: UserDefaults { suite.flatMap(UserDefaults.init(suiteName:)) ?? .standard }

    /// The addresses known of a server, forgotten when its account signs out.
    func forget(_ serverId: String) {
        defaults.removeObject(forKey: Self.key(serverId))
        if Self.plain(serverId) == self.serverId.map(Self.plain) { known = [] }
    }

    /// The UI tests start from nothing: no address known of any server.
    static func forgetAll() {
        let defaults = UserDefaults.standard
        for key in defaults.dictionaryRepresentation().keys where key.hasPrefix("mira.way.") { defaults.removeObject(forKey: key) }
    }

    private static func key(_ serverId: String) -> String { "mira.way.\(plain(serverId))" }
}
