import Foundation

/// The rules of the way to Jellyfin, without the network (Way does the asking): the addresses to try in order of
/// preference, the one to take once they answered, how an address of the server becomes the same address on that way,
/// and what to say when none answers.
enum WayPlan {
    /// How a way reaches the server: the home network (or this device), the address as it is (the Internet, or Tailscale
    /// through Tailscale's own app), or Built-in Tailscale, through its relay in Mira.
    enum Kind: String, Sendable, Codable, CaseIterable { case home, direct, tailscaleApp, builtIn }

    struct Route: Sendable, Hashable {
        var kind: Kind
        /// What stands for the server's address in every request on this way.
        var base: URL
        /// The server's address this way reaches: `base` itself, or the relay's target.
        var address: URL
    }

    /// Wi-Fi or a cable (a home network may be there), anything else, or nothing.
    enum Network: Sendable, Hashable { case local, cellular, none }

    /// A device of the Tailscale network, as Built-in Tailscale sees it (src/Mira.Tailscale, tailnet.Device).
    struct Peer: Sendable, Hashable, Codable {
        var id = ""
        var hostName = ""
        /// Its MagicDNS name, without the final dot (« desktop-abc.tail1234.ts.net »).
        var dnsName: String?
        /// Its Tailscale addresses, IPv4 first.
        var ips: [String] = []
        var os: String?
        /// Connected to Tailscale.
        var online = false
        /// When its key expires (it signs in again then), unless key expiry is off for it; and when an offline device
        /// was last connected. As Go writes them (RFC 3339).
        var keyExpiry: String?
        var lastSeen: String?

        init(id: String = "", hostName: String = "", dnsName: String? = nil, ips: [String] = [], os: String? = nil, online: Bool = false,
             keyExpiry: String? = nil, lastSeen: String? = nil) {
            self.id = id
            self.hostName = hostName
            self.dnsName = dnsName
            self.ips = ips
            self.os = os
            self.online = online
            self.keyExpiry = keyExpiry
            self.lastSeen = lastSeen
        }

        init(from decoder: any Decoder) throws {
            let values = try decoder.container(keyedBy: CodingKeys.self)
            id = try values.decodeIfPresent(String.self, forKey: .id) ?? ""
            hostName = try values.decodeIfPresent(String.self, forKey: .hostName) ?? ""
            dnsName = try values.decodeIfPresent(String.self, forKey: .dnsName)
            ips = try values.decodeIfPresent([String].self, forKey: .ips) ?? []
            os = try values.decodeIfPresent(String.self, forKey: .os)
            online = try values.decodeIfPresent(Bool.self, forKey: .online) ?? false
            keyExpiry = try values.decodeIfPresent(String.self, forKey: .keyExpiry)
            lastSeen = try values.decodeIfPresent(String.self, forKey: .lastSeen)
        }

        /// Its name in Tailscale's admin console: the first label of its MagicDNS name, else its own.
        var label: String {
            if let first = dnsName?.split(separator: ".").first, !first.isEmpty { return String(first) }
            return hostName
        }

        var expires: Date? { keyExpiry.flatMap(Self.date) }
        var seen: Date? { lastSeen.flatMap(Self.date) }

        /// A time as Go writes it: RFC 3339, with a fraction of a second or not.
        static func date(_ text: String) -> Date? {
            (try? Date.ISO8601FormatStyle(includingFractionalSeconds: true).parse(text)) ?? (try? Date.ISO8601FormatStyle().parse(text))
        }
    }

    /// Days before this device's key expires from which Mira says it, as Mira for Windows and Mac do (TailnetText).
    static let warnDays = 14.0

    /// When this device's sign-in to Tailscale expires, once it is within `warnDays`; nil otherwise, or once expired
    /// (the state says it then).
    static func expiry(_ device: Peer?, now: Date = Date()) -> Date? {
        guard let at = device?.expires, at > now, at.timeIntervalSince(now) <= warnDays * 86_400 else { return nil }
        return at
    }

    /// Built-in Tailscale as the way sees it: on or not, its node's state (« off » until it starts, then the shared
    /// names of src/Mira.Tailscale: starting, needs-login, expired, machine-auth, running, stopped), whether it reaches
    /// Tailscale, its relay and the devices of the network.
    struct Tailnet: Sendable, Hashable {
        var enabled = false
        var state = "off"
        var online = false
        var relay: URL?
        var peers: [Peer] = []

        static let off = Tailnet()
        var running: Bool { enabled && state == "running" && relay != nil }
        var starting: Bool { enabled && ["off", "starting", "stopped"].contains(state) }
    }

    /// Why no way reaches the server, said in Mira's words (Messages).
    enum Hint: Sendable, Hashable {
        case noNetwork
        case tailscaleOff
        case signedOut
        case expired
        case approval
        case starting
        case offline
        case serverOffline(String)
        case serverNotFound
    }

    /// The ways to try, best first. The home network first: the fastest, never through Tailscale, and only on Wi-Fi or
    /// a cable (a home address on a cellular connection is someone else's). Then the address as it is: the Internet, or
    /// Tailscale when its app is on. Then Built-in Tailscale: the server's Tailscale addresses known, then each device
    /// of the network that is online (the PC with Jellyfin is the one that answers with this server's id).
    static func candidates(server: URL, known: [URL], network: Network, tailnet: Tailnet, only: Set<Kind>? = nil) -> [Route] {
        var addresses: [URL] = []
        for address in [server] + known where !addresses.contains(where: { same($0, address) }) { addresses.append(address) }
        var routes: [Route] = []
        func add(_ kind: Kind, _ base: URL, _ address: URL) {
            guard only?.contains(kind) ?? true, !routes.contains(where: { $0.kind == kind && same($0.address, address) }) else { return }
            routes.append(Route(kind: kind, base: base, address: address))
        }
        for address in addresses where isLoopback(address) || (network == .local && ServerAddress.isHome(address)) {
            add(.home, address, address)
        }
        for address in addresses where !isLoopback(address) {
            // A name of one label away from home: a short MagicDNS name, with Tailscale's app.
            if isShortName(address) && network != .local {
                add(.tailscaleApp, address, address)
            } else if !ServerAddress.isHome(address) {
                add(isTailnet(address) ? .tailscaleApp : .direct, address, address)
            }
        }
        guard tailnet.running, let relay = tailnet.relay else { return routes }
        for address in addresses where isTailnet(address) {
            if let base = relayed(address, relay: relay) { add(.builtIn, base, address) }
        }
        // Jellyfin answers on its own port on every network of the PC: the one of the server's address, and 8096.
        var ports: [Int] = []
        for port in [server.port, 8096].compactMap({ $0 }) where !ports.contains(port) { ports.append(port) }
        for peer in peers(tailnet.peers) {
            guard let ip = peer.ips.first(where: { ServerAddress.IPv4($0) != nil }) ?? peer.ips.first else { continue }
            for port in ports {
                guard let address = URL(string: "http://\(ip.contains(":") ? "[\(ip)]" : ip):\(port)"),
                      let base = relayed(address, relay: relay) else { continue }
                add(.builtIn, base, address)
            }
        }
        return routes
    }

    /// The devices that may run Jellyfin: online, not a phone, a tablet nor a TV, 16 at most.
    static func peers(_ peers: [Peer]) -> [Peer] {
        Array(peers.filter { $0.online && !["ios", "ipados", "android", "tvos"].contains(($0.os ?? "").lowercased()) }.prefix(16))
    }

    /// The way to take, from the answers so far (nil: not yet, or none at all): the first that answered with this server
    /// once every way before it gave up; after the grace, one further down that answered, without waiting for the others.
    static func choose(_ answers: [Bool?], graceOver: Bool) -> Int? {
        guard let first = answers.firstIndex(where: { $0 != false }) else { return nil }
        if answers[first] == true { return first }
        return graceOver ? answers.firstIndex(where: { $0 == true }) : nil
    }

    /// An address of a Tailscale network: 100.64.0.0/10, fd7a:115c:a1e0::/48, or a MagicDNS name (….ts.net).
    static func isTailnet(_ url: URL) -> Bool { bareHost(url).map(isTailnetHost) ?? false }

    static func isTailnetHost(_ host: String) -> Bool {
        if let ip = ServerAddress.IPv4(host) { return ip.bytes[0] == 100 && (64...127).contains(ip.bytes[1]) }
        if host.contains(":") { return host.hasPrefix("fd7a:115c:a1e0:") }
        return host.hasSuffix(".ts.net")
    }

    /// A name of one label (« desktop-abc »): the home network's DNS, or Tailscale's MagicDNS with its app.
    static func isShortName(_ url: URL) -> Bool {
        guard let host = bareHost(url) else { return false }
        return !host.contains(".") && !host.contains(":") && host != "localhost"
    }

    /// This device: 127.0.0.0/8, ::1 or localhost.
    static func isLoopback(_ url: URL) -> Bool {
        guard let host = bareHost(url) else { return false }
        if let ip = ServerAddress.IPv4(host) { return ip.bytes[0] == 127 }
        return host == "::1" || host == "localhost"
    }

    /// The same server address: scheme, host and port (the default one of its scheme when not written), and path.
    static func same(_ a: URL, _ b: URL) -> Bool {
        a.scheme?.lowercased() == b.scheme?.lowercased() && bareHost(a) == bareHost(b) && port(a) == port(b)
            && trimmedPath(a) == trimmedPath(b)
    }

    /// The base of an address through Built-in Tailscale's relay: http://127.0.0.1:<port>/<secret>/<scheme>/<host:port><path>.
    static func relayed(_ address: URL, relay: URL) -> URL? {
        guard let scheme = address.scheme?.lowercased(), scheme == "http" || scheme == "https", let host = bareHost(address) else { return nil }
        let authority = (host.contains(":") ? "%5B\(host)%5D" : host) + ":\(port(address))"
        return URL(string: trimmed(relay.absoluteString) + "/\(scheme)/\(authority)" + trimmedPath(address))
    }

    /// An address of `server` on the way whose base is `base`: the same path, query and fragment after it. Nil when the
    /// address is not one of this server.
    static func map(_ url: URL, from server: URL, to base: URL) -> URL? {
        guard url.scheme?.lowercased() == server.scheme?.lowercased(), bareHost(url) == bareHost(server), port(url) == port(server) else { return nil }
        let root = trimmedPath(server)
        var path = url.path(percentEncoded: true)
        guard path.hasPrefix(root) else { return nil }
        path.removeFirst(root.count)
        guard path.isEmpty || path.hasPrefix("/") else { return nil }
        var text = trimmed(base.absoluteString) + path
        if let query = url.query(percentEncoded: true) { text += "?" + query }
        if let fragment = url.fragment(percentEncoded: true) { text += "#" + fragment }
        return URL(string: text)
    }

    /// What to say when no way answers, from what Mira knows of the network and of Built-in Tailscale.
    static func hint(network: Network, tailnet: Tailnet, server: URL, known: [URL]) -> Hint? {
        if network == .none { return .noNetwork }
        // Off: said only when Tailscale is the way to this server (a Tailscale address, or a home one away from home).
        guard tailnet.enabled else { return isTailnet(server) || network != .local && ServerAddress.isHome(server) && !isLoopback(server) ? .tailscaleOff : nil }
        switch tailnet.state {
        case "needs-login": return .signedOut
        case "expired": return .expired
        case "machine-auth": return .approval
        case "running" where !tailnet.online: return .offline
        case "running":
            if let pc = device(of: [server] + known, in: tailnet.peers), !pc.online { return .serverOffline(pc.label) }
            return .serverNotFound
        default: return .starting
        }
    }

    /// The device of the network at one of these addresses (its Tailscale IP or MagicDNS name, short or full).
    static func device(of addresses: [URL], in peers: [Peer]) -> Peer? {
        // A name of one label may be a short MagicDNS name (with Tailscale's app).
        let hosts = Set(addresses.compactMap(bareHost).filter { isTailnetHost($0) || !$0.contains(".") && !$0.contains(":") })
        guard !hosts.isEmpty else { return nil }
        return peers.first { peer in
            if peer.ips.contains(where: { hosts.contains($0.lowercased()) }) { return true }
            guard let name = peer.dnsName?.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: ".")), !name.isEmpty else { return false }
            return hosts.contains(name) || hosts.contains(String(name.split(separator: ".")[0]))
        }
    }

    // MARK: Parts of an address

    /// The host, lowercased, without the brackets of an IPv6 address nor a final dot.
    static func bareHost(_ url: URL) -> String? {
        guard let host = url.host(percentEncoded: false)?.lowercased(), !host.isEmpty else { return nil }
        return host.trimmingCharacters(in: CharacterSet(charactersIn: "[]."))
    }

    static func port(_ url: URL) -> Int { url.port ?? (url.scheme?.lowercased() == "https" ? 443 : 80) }

    private static func trimmedPath(_ url: URL) -> String { trimmed(url.path(percentEncoded: true)) }

    private static func trimmed(_ text: String) -> String {
        var text = text
        while text.hasSuffix("/") { text.removeLast() }
        return text
    }
}
