import Foundation

/// A home link of « Le foyer », as the extension makes it (HomeTailnet.Link of Mira.Jellyfin, HomeLink.cs of Core): its
/// page https://miracinema.app/home#<data>, or mira://home#<data> that the page hands to Mira, the data a base64url JSON
/// {v, name, id, url, key, until}. It says where the home's Jellyfin is on its administrator's tailnet and carries a
/// single-use key for this iPhone's node there; never the home's password. Anything else is not a home link (nil).
struct HomeLink: Sendable, Hashable {
    let name: String
    let serverId: String
    /// The home's Jellyfin on the tailnet: http(s)://<Tailscale address>[:port]/
    let address: URL
    let key: String
    let until: Date

    static let page = "https://miracinema.app/home#"
    static let app = "mira://home#"

    /// The address's host: what the home's node alone reaches.
    var host: String { address.host(percentEncoded: false)?.trimmingCharacters(in: CharacterSet(charactersIn: "[]")) ?? "" }

    /// Whether its key can still bring a new device in: 48 h after it was made.
    func expired(_ now: Date = Date()) -> Bool { now >= until }

    /// Whether a server's own id (System/Info/Public) is the link's: Jellyfin writes it with or without dashes.
    func `is`(_ serverId: String?) -> Bool {
        guard let serverId, !serverId.isEmpty else { return false }
        return Self.plain(serverId) == Self.plain(self.serverId)
    }

    /// Whether a text is a home link at all (its page or Mira's), read or not.
    static func isHomeLink(_ text: String?) -> Bool {
        guard let text = text?.lowercased() else { return false }
        return text.hasPrefix(app) || text.hasPrefix("mira://home/#") || text.hasPrefix(page)
    }

    private struct Payload: Decodable {
        var v: Int?
        var name: String?
        var id: String?
        var url: String?
        var key: String?
        var until: Double?
    }

    /// The link read, or nil: another link, damaged data, an address outside Tailscale's, a key that is not one.
    static func read(_ text: String?) -> HomeLink? {
        // The short link (JoinLink) of a home: no name or id in it, both read from the server once reached.
        if JoinLink.isJoinLink(text) {
            guard let join = JoinLink.read(text), join.kind == .home else { return nil }
            return HomeLink(name: "Jellyfin", serverId: "", address: join.address, key: join.key, until: .distantFuture)
        }
        guard let text, isHomeLink(text), text.count <= 4200, let hash = text.firstIndex(of: "#") else { return nil }
        let data = text[text.index(after: hash)...].trimmingCharacters(in: .whitespacesAndNewlines)
        guard !data.isEmpty, data.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || "-_=".contains($0)) }) else { return nil }
        var base64 = data.replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard let bytes = Data(base64Encoded: base64), let read = try? JSONDecoder().decode(Payload.self, from: bytes),
              read.v == 1, let name = read.name, let id = read.id, let address = read.url, let key = read.key else { return nil }
        // Only a device of a tailnet, by its address: the home's node reaches that one host, nothing a link could aim elsewhere.
        guard let url = URL(string: address), let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https", url.user == nil, url.password == nil,
              let host = url.host(percentEncoded: false)?.trimmingCharacters(in: CharacterSet(charactersIn: "[]")),
              ServerAddress.IPv4(host) != nil || host.contains(":"), WayPlan.isTailnetHost(host) else { return nil }
        guard key.hasPrefix("tskey-"), key.count <= 200, key.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }) else { return nil }
        guard (1...64).contains(id.count), id.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }) else { return nil }
        var clean = String(name.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }).trimmingCharacters(in: .whitespaces)
        if clean.count > 80 { clean = String(clean.prefix(80)) }
        var root = url.absoluteString
        while root.hasSuffix("/") { root.removeLast() }
        guard let base = URL(string: root + "/") else { return nil }
        let seconds = min(max(read.until ?? 0, 0), 253_402_300_799)
        return HomeLink(name: clean.isEmpty ? "Jellyfin" : clean, serverId: id, address: base, key: key, until: Date(timeIntervalSince1970: seconds))
    }

    static func plain(_ id: String) -> String { id.lowercased().filter { $0 != "-" } }
}
