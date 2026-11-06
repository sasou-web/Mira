import Foundation

/// The QR code of « Mira on your iPhone » on Windows and Mac (PhoneLink of Core): mira://connect?server=…&home=…, read by
/// the iPhone's camera. `server` is the address that works everywhere (Tailscale) or the home's; `home`, when there is
/// one, the home network's, learnt too so that the way to Jellyfin picks it at home. Only addresses: an account and a
/// password are still typed, or Quick Connect. Cases shared with Core: tests/fixtures/connect.json.
struct ConnectLink: Equatable, Sendable {
    let server: URL
    let home: URL?

    /// The link's addresses, nil for any other link (a party's, another scheme) or without a server at http(s).
    static func parse(_ url: URL) -> ConnectLink? {
        guard url.scheme?.lowercased() == "mira", url.host()?.lowercased() == "connect",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
              let server = address(items.first(where: { $0.name == "server" })?.value) else { return nil }
        let home = address(items.first(where: { $0.name == "home" })?.value).flatMap { WayPlan.same($0, server) ? nil : $0 }
        return ConnectLink(server: server, home: home)
    }

    /// The addresses to try, the one that works everywhere first.
    var addresses: [URL] { [server] + (home.map { [$0] } ?? []) }

    /// An http(s) address with a host, no credentials, query or fragment, without its final slash.
    private static func address(_ value: String?) -> URL? {
        guard let value, let parts = URLComponents(string: value), parts.query == nil, parts.fragment == nil,
              parts.user == nil, parts.password == nil, parts.host?.isEmpty == false else { return nil }
        return try? ServerAddress.normalize(value)
    }
}

extension ConnectLink {
    /// The server at the first of its addresses that answers as Jellyfin (all tried at once): at home the home's, away
    /// the one of Tailscale (Built-in Tailscale or Tailscale's app). Nil when none answers.
    func find() async -> ServerInfo? {
        await withTaskGroup(of: ServerInfo?.self) { group in
            for address in addresses {
                group.addTask { try? await JellyfinClient.discover(address.absoluteString) }
            }
            for await found in group {
                if let found {
                    group.cancelAll()
                    return found
                }
            }
            return nil
        }
    }
}
