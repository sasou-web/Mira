import Foundation
import Observation
import Security
import UIKit

/// The account this iPhone is signed in with: the server, who, and the session's token.
struct Session: Codable, Sendable, Hashable {
    var server: URL
    var serverId: String
    var serverName: String
    var userId: String
    var userName: String
    var token: String
}

/// What the whole app shares: the session, kept in the Keychain (only on this iPhone, never in a backup), and this
/// installation's device id.
@MainActor @Observable
final class AppModel {
    private(set) var session: Session?
    /// Said once on the sign-in screen when Jellyfin ended the session (401).
    var notice: String?
    let device: DeviceIdentity

    init() {
        let defaults = UserDefaults.standard
        // The UI tests start from nothing: no account, no address typed before.
        if ProcessInfo.processInfo.arguments.contains("-mira-reset") {
            SessionStore.delete()
            defaults.removeObject(forKey: Keys.lastServer)
            HomeCache.clearAll()
        }
        var id = defaults.string(forKey: Keys.deviceId) ?? ""
        if id.isEmpty {
            id = UUID().uuidString.lowercased()
            defaults.set(id, forKey: Keys.deviceId)
        }
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
        device = DeviceIdentity(id: id, name: UIDevice.current.model, version: version)
        session = SessionStore.load()
    }

    var client: JellyfinClient? {
        session.map { JellyfinClient(server: $0.server, device: device, token: $0.token, userId: $0.userId) }
    }

    var lastServer: String { UserDefaults.standard.string(forKey: Keys.lastServer) ?? "" }

    func signedIn(_ session: Session, typed: String) {
        SessionStore.save(session)
        UserDefaults.standard.set(typed, forKey: Keys.lastServer)
        notice = nil
        self.session = session
    }

    /// Signed out on this iPhone at once, then on Jellyfin; the account's Home kept for a quick start goes too.
    func signOut() {
        let client = client
        if let session { HomeCache.clear(userId: session.userId) }
        SessionStore.delete()
        session = nil
        Task { await client?.signOut() }
    }

    /// Jellyfin no longer knows the token (401): back to the sign-in screen, which says why.
    func sessionEnded() {
        guard let session else { return }
        HomeCache.clear(userId: session.userId)
        SessionStore.delete()
        notice = String(localized: "Your session on Jellyfin has ended. Sign in again.")
        self.session = nil
    }

    private enum Keys {
        static let deviceId = "mira.deviceId"
        static let lastServer = "mira.lastServer"
    }
}

/// The session in the Keychain, readable after the first unlock (a refresh in the background), on this device only.
enum SessionStore {
    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "Mira", kSecAttrAccount as String: "session"]
    }

    static func load() -> Session? {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var found: CFTypeRef?
        guard SecItemCopyMatching(request as CFDictionary, &found) == errSecSuccess, let data = found as? Data else { return nil }
        return try? JSONDecoder().decode(Session.self, from: data)
    }

    static func save(_ session: Session) {
        guard let data = try? JSONEncoder().encode(session) else { return }
        delete()
        var item = query
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }

    static func delete() {
        SecItemDelete(query as CFDictionary)
    }
}
