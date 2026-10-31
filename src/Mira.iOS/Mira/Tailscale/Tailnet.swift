import AuthenticationServices
import Foundation
import Observation
import Security
import UIKit

/// Built-in Tailscale: Mira joins the person's Tailscale network itself, without Tailscale's app nor a VPN profile, only
/// while it is open (and while a title plays on in Picture in Picture or AirPlay); its relay on this iPhone takes the
/// requests of the player and of the whole app to the PC with Jellyfin. Off until turned on in Settings; signed in once,
/// on Tailscale's page. Its logs never leave the iPhone (src/Mira.Tailscale).
@MainActor @Observable
final class Tailnet {
    enum Phase: Equatable { case off, starting, signIn, approval, running, failed(String) }

    private(set) var enabled: Bool
    private(set) var status = TailnetStatus.off
    /// Tailscale's sign-in page is open over Mira.
    private(set) var signingIn = false
    /// Why the last sign-in or sign-out failed.
    var failure: String?
    /// Connecting for longer than Tailscale ever takes: its coordination server isn't answering (an outage at Tailscale's,
    /// or a network that blocks it). The node keeps trying.
    private(set) var slow = false

    @ObservationIgnored private var watching: Task<Void, Never>?
    @ObservationIgnored private var page: SignInPage?
    @ObservationIgnored private var leftAt: Date?
    /// When the node began connecting, while it does.
    @ObservationIgnored private var startingSince: Date?
    /// What Way was last told, in order: a newer state never goes before an older one.
    @ObservationIgnored private var pushing: Task<Void, Never>?

    init() {
        enabled = Self.isEnabled
    }

    nonisolated static var isEnabled: Bool { UserDefaults.standard.bool(forKey: Keys.enabled) }

    var phase: Phase {
        guard enabled else { return .off }
        if let error = status.error { return .failed(Self.text(error)) }
        switch status.state {
        case "running": return .running
        case "needs-login", "expired": return .signIn
        case "machine-auth": return .approval
        default: return .starting
        }
    }

    /// Turned on or off in Settings (or on the sign-in screen, to find Jellyfin there).
    func turn(_ on: Bool) async {
        enabled = on
        UserDefaults.standard.set(on, forKey: Keys.enabled)
        push()
        if on { await start() } else { await stop() }
    }

    func start() async {
        guard enabled, status.state == "off" || status.error != nil else { return }
        guard let config = Self.config() else {
            apply(TailnetStatus(state: "off", error: TailnetFailure(code: "keychain", message: "")))
            return
        }
        apply(TailnetStatus(state: "starting"))
        apply(await TailscaleLibrary.start(config))
        watch()
    }

    func stop() async {
        watching?.cancel()
        watching = nil
        page?.close()
        await TailscaleLibrary.stop()
        apply(.off)
    }

    func refresh() async {
        guard enabled, status.state != "off" else { return }
        apply(await TailscaleLibrary.status())
    }

    /// Mira is back on screen: started again, or the network looked at again and its relay checked. Kept on through a
    /// long while in the background (a title playing on), it starts afresh when nothing plays any more: iOS may have
    /// taken its sockets while Mira slept.
    func foreground(playing: Bool) async {
        let away = leftAt.map { Date().timeIntervalSince($0) } ?? 0
        leftAt = nil
        guard enabled else { return }
        if status.state == "off" {
            await start()
        } else if away > 30 && !playing && !signingIn {
            await TailscaleLibrary.stop()
            apply(.off)
            await start()
        } else {
            apply(await TailscaleLibrary.check())
            watch()
        }
    }

    /// Mira leaves the screen: the node stops unless a title plays on (Picture in Picture, AirPlay) or Tailscale's
    /// sign-in page is open (the person may be fetching a code in another app), with the few seconds iOS grants to
    /// finish it.
    func background(playing: Bool) {
        leftAt = Date()
        guard enabled, status.state != "off", !playing, !signingIn else { return }
        let task = UIApplication.shared.beginBackgroundTask(withName: "Tailscale") {}
        Task {
            await stop()
            UIApplication.shared.endBackgroundTask(task)
        }
    }

    /// Tailscale's sign-in page, over Mira, until the node runs: closed by Mira then (Tailscale's page does not come back
    /// to the app), or by the person. Only when this iPhone needs it: asked of a node signed in already, it would sign it
    /// in again.
    func signIn() async {
        failure = nil
        if status.state == "off" || status.error != nil { await start() }
        await settle()
        guard enabled, ["needs-login", "expired"].contains(status.state) else {
            watch()
            return
        }
        do {
            let url = try await TailscaleLibrary.login()
            let page = SignInPage()
            self.page = page
            signingIn = true
            page.open(url) { [weak self] in self?.signingIn = false }
            while signingIn && enabled {
                try await Task.sleep(for: .seconds(1))
                apply(await TailscaleLibrary.status())
                if ["running", "machine-auth"].contains(status.state) { break }
            }
            page.close()
            self.page = nil
            signingIn = false
            watch()
        } catch is CancellationError {
            page?.close()
            signingIn = false
        } catch {
            signingIn = false
            failure = Self.text(error)
        }
    }

    /// Waits, 10 s at most, for a node that is starting to say where it stands (its saved state may sign it in).
    private func settle() async {
        for _ in 0..<40 where enabled && ["off", "starting", "stopped"].contains(status.state) && status.error == nil {
            try? await Task.sleep(for: .milliseconds(250))
            apply(await TailscaleLibrary.status())
        }
    }

    /// Signs this iPhone out of the Tailscale network (Tailscale's admin console shows it signed out); turned on, the
    /// node waits for the next sign-in.
    func signOut() async {
        failure = nil
        do { try await TailscaleLibrary.logout() } catch { failure = Self.text(error) }
        await refresh()
        watch()
    }

    /// The node's state read while Mira is on screen: every second while it connects, every 3 s while a sign-in or an
    /// approval is waited for, every 5 s once it runs (its devices online or not, a sign-in that expired).
    private func watch() {
        watching?.cancel()
        watching = Task { [weak self] in
            while !Task.isCancelled {
                guard let state = self?.status.state, self?.enabled == true, state != "off" else { return }
                let pause: Double = switch state {
                case "running": 5
                case "needs-login", "expired", "machine-auth": 3
                default: 1
                }
                try? await Task.sleep(for: .seconds(pause))
                guard !Task.isCancelled, let self, self.status.state != "off" else { return }
                self.apply(await TailscaleLibrary.status())
                self.notice()
            }
        }
    }

    private func apply(_ status: TailnetStatus) {
        guard status != self.status else { return }
        self.status = status
        notice()
        push()
    }

    /// Starting, or connecting again, for over 20 s: said, instead of a spinner that never ends.
    private func notice() {
        let connecting = enabled && phase == .starting && status.state != "off"
        if !connecting { startingSince = nil } else if startingSince == nil { startingSince = Date() }
        let late = startingSince.map { Date().timeIntervalSince($0) > Self.patience } ?? false
        if late != slow { slow = late }
    }

    /// How long Tailscale may take to connect before Mira says its servers aren't answering.
    static let patience: TimeInterval = 20

    /// Way hears of each change, in order.
    private func push() {
        let view = WayPlan.Tailnet(status, enabled: enabled)
        let previous = pushing
        pushing = Task {
            await previous?.value
            await Way.shared.tailnet(view)
        }
    }

    /// A failure in Mira's words.
    static func text(_ error: Error) -> String {
        guard let failure = error as? TailnetFailure else { return String(localized: "Tailscale didn’t answer. Check this iPhone’s connection, then try again.") }
        switch failure.code {
        case "offline":
            return String(localized: "Tailscale didn’t answer. Check this iPhone’s connection, then try again.")
        case "timeout", "tailscale":
            return String(localized: "Tailscale’s servers didn’t answer. Try again in a moment; if it lasts, see status.tailscale.com.")
        case "login":
            return String(localized: "Tailscale didn’t give a sign-in page. Try again in a moment.")
        case "keychain":
            return String(localized: "The iPhone’s Keychain isn’t available yet: unlock the iPhone, then try again.")
        default:
            return String(localized: "Built-in Tailscale couldn’t start. Close Mira and open it again.")
        }
    }

    // MARK: The node's configuration

    /// Its folder (never in a backup: its keys stay on this iPhone), its name on the network, the keys of its state and
    /// of its relay (Keychain, this device only).
    private static func config() -> TailscaleLibrary.Config? {
        var folder = URL.applicationSupportDirectory.appending(path: "Tailscale", directoryHint: .isDirectory)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? folder.setResourceValues(values)
        guard let stateKey = Secrets.value("tailscale.state", bytes: 32), let relaySecret = Secrets.value("tailscale.relay", bytes: 24) else { return nil }
        var config = TailscaleLibrary.Config(dir: folder.path(percentEncoded: false),
                                             hostname: UIDevice.current.userInterfaceIdiom == .pad ? "mira-ipad" : "mira-iphone",
                                             stateKey: stateKey, relayPort: 48096, relaySecret: relaySecret)
        #if DEBUG
        // The UI tests: a stand-in Tailscale network (its coordination server), never in the app people install.
        let arguments = ProcessInfo.processInfo.arguments
        if let at = arguments.firstIndex(of: "-mira-tailscale-control"), at + 1 < arguments.count { config.controlURL = arguments[at + 1] }
        #endif
        return config
    }

    /// The UI tests start from nothing: Built-in Tailscale off, its node's state gone.
    static func reset() {
        UserDefaults.standard.removeObject(forKey: Keys.enabled)
        try? FileManager.default.removeItem(at: URL.applicationSupportDirectory.appending(path: "Tailscale", directoryHint: .isDirectory))
    }

    private enum Keys {
        static let enabled = "mira.tailscale.enabled"
    }
}

/// Random keys made once and kept in the Keychain, readable after the first unlock, on this device only.
enum Secrets {
    /// The key named so, `bytes` random bytes in base64url (made the first time).
    static func value(_ name: String, bytes: Int) -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "Mira", kSecAttrAccount as String: name]
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var found: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &found)
        if status == errSecSuccess, let data = found as? Data, let text = String(data: data, encoding: .utf8), !text.isEmpty { return text }
        // Locked since the iPhone started: nothing made now, it would replace the key kept.
        guard status == errSecItemNotFound else { return nil }
        var random = [UInt8](repeating: 0, count: bytes)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes, &random) == errSecSuccess else { return nil }
        let text = Data(random).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
        var item = query
        item[kSecValueData as String] = Data(text.utf8)
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        guard SecItemAdd(item as CFDictionary, nil) == errSecSuccess else { return nil }
        return text
    }
}

/// Tailscale's sign-in page in a browser sheet of its own (no cookie shared with Safari), opened and closed by Mira.
@MainActor
private final class SignInPage: NSObject {
    private var session: ASWebAuthenticationSession?

    func open(_ url: URL, closed: @escaping @MainActor @Sendable () -> Void) {
        let session = ASWebAuthenticationSession(url: url, callback: .customScheme("mira")) { _, _ in
            Task { @MainActor in closed() }
        }
        session.prefersEphemeralWebBrowserSession = true
        session.presentationContextProvider = self
        self.session = session
        session.start()
    }

    func close() {
        session?.cancel()
        session = nil
    }
}

extension SignInPage: @preconcurrency ASWebAuthenticationPresentationContextProviding {
    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        let scene = scenes.first { $0.activationState == .foregroundActive } ?? scenes.first
        return scene?.keyWindow ?? ASPresentationAnchor()
    }
}
