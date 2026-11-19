import Foundation
import Synchronization
import UIKit

/// The home's own Tailscale node (« Le foyer » away from home), as HomeNode of Mira for Windows and Mac: a second node of
/// the library, beside Built-in Tailscale (the person's own network, if any), in its own folder with its own state key
/// and relay, logged in once by a home link's key on the tailnet of the home's administrator, as a device of the
/// tag:mira-home tag that reaches nothing but that Jellyfin. The person needs no Tailscale account. Its state stays (the
/// key is single-use, the device keeps its place) until this iPhone leaves the home.
@MainActor
final class HomeNode {
    /// The home's name while its node carries the Jellyfin signed in to (a profile opened from a home link): Way reads
    /// this node then (TailscaleLibrary.snapshot), and Messages speak of the home, never of signing in to Tailscale.
    nonisolated static let routing = Mutex<String?>(nil)

    /// The relay of the node while it runs: http://127.0.0.1:<port>/<secret>.
    private(set) var relay: URL?
    private(set) var state = "off"
    private var watching: Task<Void, Never>?

    /// How long a key may take to bring the node on the tailnet.
    var joinWait: Duration = .seconds(45)

    /// Starts the node for the home's Jellyfin, logged in by `key` when its state has no login yet (a home link's; nil
    /// once joined): nil once it is on the tailnet, otherwise why not (the library's codes: « needs-login » for a key
    /// spent, taken back or expired, or this iPhone removed from the tailnet).
    func start(key: String?) async -> String? {
        guard let config = Self.config(key: key) else { return "keychain" }
        var status = await TailscaleLibrary.homeStart(config)
        let clock = ContinuousClock(), began = clock.now
        while true {
            apply(status)
            if let error = status.error { return error.code }
            if status.state == "running", relay != nil { return nil }
            if status.state == "expired" || status.state == "machine-auth" { return status.state }
            // Without a key nothing will log it in; with one, Tailscale had its time to take it.
            if status.state == "needs-login", key == nil || clock.now - began > .seconds(12) { return "needs-login" }
            if clock.now - began > joinWait { return "unreachable" }
            try? await Task.sleep(for: .milliseconds(250))
            status = await TailscaleLibrary.homeStatus()
        }
    }

    /// The base of the home's Jellyfin through the node's relay (nil while it doesn't run).
    func base(_ server: URL) -> URL? { relay.flatMap { WayPlan.relayed(server, relay: $0) } }

    /// Carries the Jellyfin signed in to (its name for Messages), or no longer; Way looks again either way.
    func route(_ name: String?) {
        HomeNode.routing.withLock { $0 = name }
        push()
        if name != nil { watch() } else { watching?.cancel() }
    }

    /// Mira back on screen: the node started again (its saved state logs it in), or its relay checked.
    func foreground() async {
        guard HomeNode.routing.withLock({ $0 }) != nil else { return }
        if state == "off" { _ = await start(key: nil) } else { apply(await TailscaleLibrary.homeCheck()) }
        push()
        watch()
    }

    /// Mira off screen: the node stops unless a title plays on, with the few seconds iOS grants to finish it.
    func background(playing: Bool) {
        guard state != "off", !playing else { return }
        let task = UIApplication.shared.beginBackgroundTask(withName: "Home") {}
        Task {
            await stop()
            UIApplication.shared.endBackgroundTask(task)
        }
    }

    /// The node and its relay stop; its state stays for the next start.
    func stop() async {
        watching?.cancel()
        await TailscaleLibrary.homeStop()
        apply(.off)
        push()
    }

    /// This iPhone left the home: the node signs out of the tailnet when it can, stops, and its state leaves the iPhone.
    func leave() async {
        if state != "off" { await TailscaleLibrary.homeLogout() }
        route(nil)
        await stop()
        Self.forget()
    }

    private func apply(_ status: TailnetStatus) {
        state = status.error == nil ? status.state : "off"
        relay = status.state == "off" ? nil : status.relay.flatMap(URL.init(string:))
    }

    /// Way hears of the node while it carries the home's Jellyfin, every 5 s (its PC online or not).
    private func watch() {
        watching?.cancel()
        watching = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(5))
                guard !Task.isCancelled, let self, HomeNode.routing.withLock({ $0 }) != nil, self.state != "off" else { return }
                self.apply(await TailscaleLibrary.homeStatus())
                self.push()
            }
        }
    }

    private func push() {
        Task {
            let view = await TailscaleLibrary.snapshot()
            await Way.shared.tailnet(view)
        }
    }

    // MARK: The node's configuration

    private static var folder: URL { URL.applicationSupportDirectory.appending(path: "HomeTailscale", directoryHint: .isDirectory) }

    /// Its folder (never in a backup), its name on the home's network, the keys of its state and of its relay (Keychain,
    /// this device only, apart from Built-in Tailscale's).
    private static func config(key: String?) -> TailscaleLibrary.Config? {
        var folder = folder
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? folder.setResourceValues(values)
        guard let stateKey = Secrets.value("home.tailscale.state", bytes: 32), let relaySecret = Secrets.value("home.tailscale.relay", bytes: 24) else { return nil }
        var config = TailscaleLibrary.Config(dir: folder.path(percentEncoded: false),
                                             hostname: UIDevice.current.userInterfaceIdiom == .pad ? "mira-ipad" : "mira-iphone",
                                             stateKey: stateKey, relayPort: 48097, relaySecret: relaySecret, authKey: key)
        #if DEBUG
        let arguments = ProcessInfo.processInfo.arguments
        if let at = arguments.firstIndex(of: "-mira-tailscale-control"), at + 1 < arguments.count { config.controlURL = arguments[at + 1] }
        #endif
        return config
    }

    /// The node's state gone from this iPhone (left the home, or the UI tests' reset).
    nonisolated static func forget() {
        try? FileManager.default.removeItem(at: URL.applicationSupportDirectory.appending(path: "HomeTailscale", directoryHint: .isDirectory))
    }
}
