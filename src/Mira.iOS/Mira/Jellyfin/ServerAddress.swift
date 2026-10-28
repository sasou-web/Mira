import Foundation

/// What someone types on the sign-in screen, turned into the Jellyfin addresses to try, as Mira for Windows and Mac do
/// (ServerAddress.cs): « 192.168.1.20 », « nas:8096 », « jellyfin.example.com », or an address copied from Jellyfin's
/// own page (« …:8096/web/#/home ») or from Mira web (« …:8096/Mira/ ») all lead to the server.
enum ServerAddress {
    enum Problem: Error, Equatable {
        case empty
        case invalid
    }

    /// Addresses to try, most secure first. An address typed with its scheme is the only candidate.
    static func candidates(_ input: String) throws -> [URL] {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { throw Problem.empty }
        if text.contains("://") { return [try normalize(text)] }
        let authority = String(text.split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false).first ?? "")
        func build(_ scheme: String, port: Int? = nil) throws -> URL {
            let url = try normalize("\(scheme)://\(text)")
            guard let port, var parts = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return url }
            parts.port = port
            guard let ported = parts.url else { throw Problem.invalid }
            return ported
        }
        if let port = explicitPort(authority) {
            // 8920 is Jellyfin's HTTPS port; every other port is tried in plain HTTP first.
            return port == 443 || port == 8920 ? [try build("https"), try build("http")] : [try build("http"), try build("https")]
        }
        // A name behind a reverse proxy usually answers in HTTPS; a PC or NAS on the network on Jellyfin's port 8096.
        var unique: [URL] = []
        for url in [try build("https"), try build("http", port: 8096), try build("http")] where !unique.contains(url) { unique.append(url) }
        return unique
    }

    /// The server's address: http or https, no credentials, no query, and nothing from Jellyfin's or Mira web's page
    /// (`/web…`, `/Mira…`); the path of a reverse proxy (« https://example.com/jellyfin ») stays, without a final slash.
    static func normalize(_ text: String) throws -> URL {
        var text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if let cut = text.firstIndex(where: { $0 == "#" || $0 == "?" }) { text = String(text[..<cut]) }
        guard var parts = URLComponents(string: text), let scheme = parts.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = parts.host, !host.isEmpty, parts.user == nil, parts.password == nil else { throw Problem.invalid }
        parts.scheme = scheme
        var segments = parts.path.split(separator: "/").map(String.init)
        if let page = segments.firstIndex(where: { ["web", "mira"].contains($0.lowercased()) }) { segments = Array(segments[..<page]) }
        parts.path = segments.isEmpty ? "" : "/" + segments.joined(separator: "/")
        parts.query = nil
        parts.fragment = nil
        guard let url = parts.url else { throw Problem.invalid }
        return url
    }

    /// The port written at the end of « host:port », if any.
    static func explicitPort(_ authority: String) -> Int? {
        guard let colon = authority.lastIndex(of: ":") else { return nil }
        let digits = authority[authority.index(after: colon)...]
        guard (1...5).contains(digits.count), digits.allSatisfy(\.isASCII), digits.allSatisfy(\.isNumber), let port = Int(digits), port > 0, port < 65536 else { return nil }
        return port
    }

    /// Mira web, served by Jellyfin's Mira extension: `<server>/Mira/`, at a route of its own (« #/titre/<id> »).
    static func miraWeb(_ server: URL, route: String = "") -> URL? {
        URL(string: server.absoluteString + "/Mira/" + (route.isEmpty ? "" : "#/" + route))
    }

    /// Whether signing in at this address sends the password in clear across the Internet: plain http:// to a server
    /// that is neither on the home network nor on Tailscale. A dotted name of the Internet always counts (the network's
    /// DNS may lie); a single name (« nas ») or an address of a home network or of Tailscale does not.
    static func exposesPassword(_ server: URL) -> Bool {
        guard server.scheme == "http", var host = server.host(percentEncoded: false)?.lowercased() else { return false }
        host = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]."))
        if let ip = IPv4(host) { return !ip.isPrivate }
        if host.contains(":") { return !(host == "::1" || host.hasPrefix("fe80:") || host.hasPrefix("fc") || host.hasPrefix("fd") || host.hasPrefix("fd7a:115c:a1e0:")) }
        guard host.contains(".") else { return false }
        return !homeSuffixes.contains { host.hasSuffix($0) }
    }
    /// A server at home, where Mira asks for full quality: an address of the home network or a name of it, never
    /// Tailscale (it reaches the home from anywhere, on any connection).
    static func isHome(_ server: URL) -> Bool {
        guard var host = server.host(percentEncoded: false)?.lowercased() else { return false }
        host = host.trimmingCharacters(in: CharacterSet(charactersIn: "[]."))
        if let ip = IPv4(host) { return ip.isPrivate && !(ip.bytes[0] == 100 && (64...127).contains(ip.bytes[1])) }
        if host.contains(":") { return host == "::1" || host.hasPrefix("fe80:") || host.hasPrefix("fc") || (host.hasPrefix("fd") && !host.hasPrefix("fd7a:115c:a1e0:")) }
        // As StreamQuality.AtHome of Mira for Windows and Mac (tests/fixtures/bitrate.json).
        return !host.contains(".") || [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".fritz.box", ".localhost"].contains { host.hasSuffix($0) }
    }

    private static let homeSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".ts.net"]

    struct IPv4 {
        let bytes: [UInt8]
        init?(_ text: String) {
            let parts = text.split(separator: ".", omittingEmptySubsequences: false)
            guard parts.count == 4 else { return nil }
            var bytes: [UInt8] = []
            for part in parts {
                guard !part.isEmpty, part.count <= 3, part.allSatisfy(\.isNumber), let value = UInt8(part) else { return nil }
                bytes.append(value)
            }
            self.bytes = bytes
        }
        /// This device, the home network (RFC 1918, link-local) or Tailscale (100.64.0.0/10).
        var isPrivate: Bool {
            let (a, b) = (bytes[0], bytes[1])
            return a == 127 || a == 10 || (a == 172 && (16...31).contains(b)) || (a == 192 && b == 168) || (a == 169 && b == 254)
                || (a == 100 && (64...127).contains(b))
        }
    }
}
