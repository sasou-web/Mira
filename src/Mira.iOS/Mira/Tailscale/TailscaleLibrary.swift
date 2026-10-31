import Foundation
import MiraTailscale

/// Built-in Tailscale's node as its library tells it (src/Mira.Tailscale: tailnet.Status, and the relay): its state,
/// the account's network, this device and the others, and the relay in Mira that takes requests to them.
struct TailnetStatus: Codable, Sendable, Hashable {
    /// « off » while no node runs (not started, or stopped by Mira), then: starting, needs-login, expired (its key),
    /// machine-auth (waiting for an administrator's approval), running, stopped.
    var state: String
    /// Running, but this iPhone can't reach the network (no connection, or no Tailscale relay).
    var offline: Bool?
    /// This device on the network.
    var device: WayPlan.Peer?
    /// The Tailscale account it is signed in with, and its network's name.
    var user: String?
    var tailnet: String?
    var peers: [WayPlan.Peer]?
    /// Whether the network's policy lets the node keep its list of devices, to start from it during an outage of Tailscale
    /// (the « cache-network-maps » attribute).
    var outageCache: Bool?
    /// http://127.0.0.1:<port>/<secret>: requests to the network's devices, through the node.
    var relay: String?
    /// Why it could not start.
    var error: TailnetFailure?

    enum CodingKeys: String, CodingKey {
        case state, offline, device = "self", user, tailnet, peers, outageCache, relay, error
    }

    static let off = TailnetStatus(state: "off")
}

/// A failure of the library: a code (as the relay's X-Mira-Tailscale, or the library's own: config, relay, start) and
/// Tailscale's own words.
struct TailnetFailure: Codable, Sendable, Hashable, Error {
    var code: String
    var message: String
}

/// The library's functions, each called off the main thread (some wait: starting, a sign-in page to open) and answering
/// in JSON.
enum TailscaleLibrary {
    /// What the node starts with: its folder, its name on the network, where its keys are encrypted with, the relay's
    /// port and secret; a test's coordination server and key only in the tests.
    struct Config: Encodable, Sendable {
        var dir: String
        var hostname: String
        var stateKey: String
        var relayPort: Int
        var relaySecret: String
        var controlURL: String?
        var authKey: String?
    }

    private struct Answer: Decodable {
        var url: String?
        var error: TailnetFailure?
    }

    private static let queue = DispatchQueue(label: "app.miracinema.Mira.tailscale", qos: .userInitiated, attributes: .concurrent)

    private static func call(_ body: @escaping @Sendable () -> UnsafeMutablePointer<CChar>?) async -> Data {
        await withCheckedContinuation { continuation in
            queue.async {
                guard let text = body() else { return continuation.resume(returning: Data()) }
                let data = Data(bytes: text, count: strlen(text))
                MiraTailscaleFree(text)
                continuation.resume(returning: data)
            }
        }
    }

    private static func status(_ data: Data) -> TailnetStatus {
        (try? JSONDecoder().decode(TailnetStatus.self, from: data)) ?? TailnetStatus(state: "off", error: TailnetFailure(code: "library", message: String(decoding: data, as: UTF8.self)))
    }

    /// Starts the node and its relay (nothing when already started with this configuration); its state once started,
    /// not yet connected.
    static func start(_ config: Config) async -> TailnetStatus {
        guard let json = try? JSONEncoder().encode(config) else { return .off }
        let text = String(decoding: json, as: UTF8.self)
        return status(await call { MiraTailscaleStart(text) })
    }

    static func status() async -> TailnetStatus { status(await call { MiraTailscaleStatus() }) }

    /// A fresh look at the network, and the relay listening again if iOS took its socket while Mira was away.
    static func check() async -> TailnetStatus { status(await call { MiraTailscaleCheck() }) }

    /// The interface iOS reaches the Internet with now (« en0 », « pdp_ip0 »): the node's connections go out there.
    static func network(_ interface: String) async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            queue.async {
                MiraTailscaleNetwork(interface)
                continuation.resume()
            }
        }
    }

    /// The page where the person signs in to Tailscale.
    static func login() async throws -> URL {
        let answer = try? JSONDecoder().decode(Answer.self, from: await call { MiraTailscaleLogin() })
        if let error = answer?.error { throw error }
        guard let text = answer?.url, let url = URL(string: text), ["https", "http"].contains(url.scheme ?? "") else {
            throw TailnetFailure(code: "login", message: "")
        }
        return url
    }

    /// Signs this device out of the network: its key is no longer valid there.
    static func logout() async throws {
        if let error = (try? JSONDecoder().decode(Answer.self, from: await call { MiraTailscaleLogout() }))?.error { throw error }
    }

    /// Stops the node and its relay (Mira leaves the screen).
    static func stop() async { _ = await call { MiraTailscaleStop() } }

    /// Built-in Tailscale as Way sees it, read now (any thread).
    static func snapshot() async -> WayPlan.Tailnet {
        guard Tailnet.isEnabled else { return .off }
        return WayPlan.Tailnet(await status(), enabled: true)
    }
}

extension WayPlan.Tailnet {
    /// Built-in Tailscale as the way sees it, from its library's status.
    init(_ status: TailnetStatus, enabled: Bool) {
        self.init(enabled: enabled, state: status.state, online: !(status.offline ?? false), relay: status.relay.flatMap(URL.init(string:)),
                  peers: status.peers ?? [])
    }
}
