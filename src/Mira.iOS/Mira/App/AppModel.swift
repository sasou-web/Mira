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
    /// The title playing, in Apple's player or in Picture in Picture.
    private(set) var player: PlayerSession?
    /// Why a title could not play, said over the page it was started from.
    var playerMessage: String?
    /// The title that could not play, for « Try again » (Jellyfin back, Tailscale awake): where it was left.
    var retryItem: Item?
    /// « Watch together »: the group this iPhone is in, Jellyfin's SyncPlay.
    let together = Together()
    /// The sleep timer, chosen on a title's page, read by the player it opens.
    var sleep = SleepTimer()
    /// Settings, some of them kept on the account's Jellyfin too (choices).
    let preferences: Preferences
    let choices: ChoicesSync
    /// Why Jellyfin refused to mark a title watched or a favorite.
    var actionMessage: String?
    /// Bumped when a title stops or another one starts, or is marked watched or a favorite: every page reads Jellyfin again.
    private(set) var playback = 0
    /// The last measure of the connection to Jellyfin (bits per second), kept an hour.
    @ObservationIgnored private var measure: (at: Date, speed: Double, server: URL)?

    init() {
        let preferences = Preferences()
        self.preferences = preferences
        choices = ChoicesSync(preferences: preferences)
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
        together.app = self
        preferences.use(account: session?.userId)
        preferences.onChange = { [weak self] in self?.choices.soon { [weak self] in self?.client } }
    }

    /// The account's choices compared with its Jellyfin: at the start, and back on the screen after a while.
    func syncChoices(lookAgain: Bool = false) {
        if lookAgain { choices.lookAgain { [weak self] in self?.client } } else if let client { choices.run(client) }
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
        preferences.use(account: session.userId)
    }

    // MARK: Playing

    /// Plays a film or an episode in Apple's player, where it was left unless `fromStart`; a title already playing
    /// (in Picture in Picture) stops first.
    func play(_ item: Item, fromStart: Bool = false, audioIndex: Int? = nil, subtitleIndex: Int? = nil, groupStart: Int64? = nil) {
        guard let client else { return }
        player?.close()
        let session = PlayerSession(item: item, client: client, app: self, audioIndex: audioIndex, subtitleIndex: subtitleIndex)
        player = session
        session.start(fromStart: fromStart, groupStart: groupStart)
    }

    /// The group put a title on while no player is open here: it opens, paused where the group starts it, until
    /// Jellyfin says when everyone plays.
    func playForGroup(_ itemId: String, startTicks: Int64) {
        guard let client else { return }
        Task {
            guard let item = try? await client.item(itemId), together.queue?.itemId == itemId else { return }
            play(item, groupStart: startTicks)
        }
    }

    /// Marks a title watched or not, or a favorite or not, then has every page read Jellyfin again; says why when
    /// Jellyfin refuses.
    func set(_ item: Item, played: Bool? = nil, favorite: Bool? = nil) async {
        guard let client else { return }
        do {
            if let played { try await client.setPlayed(item.id, played) }
            if let favorite { try await client.setFavorite(item.id, favorite) }
        } catch JellyfinError.unauthorized {
            sessionEnded()
            return
        } catch {
            actionMessage = Messages.text(error)
        }
        playback += 1
    }

    /// Takes a film or series out of Continue watching until it is played again; its resume point goes to 0 on
    /// Jellyfin too, so that no other device offers it (hideFromResume of Mira web).
    func hideFromContinueWatching(_ item: Item) async {
        preferences.hide(item.seriesId ?? item.id, true)
        together.notice = String(localized: "Removed from Continue watching.")
        if (item.userData?.playbackPositionTicks ?? 0) > 0 { try? await client?.setPosition(item.id, ticks: 0) }
        playback += 1
    }

    func playerClosed(_ session: PlayerSession) {
        if player === session {
            player = nil
            sleep.clear()
        }
        playback += 1
    }

    func playbackChanged() { playback += 1 }

    /// The quality chosen in Settings: « auto », « max », or a bitrate in bits per second.
    var quality: String { preferences.quality }

    /// The bitrate to ask Jellyfin for (PlaybackInfo): the one chosen, full quality at home (Tailscale is not home),
    /// else what a measure of the connection allows for this file, kept an hour (knownBitrate of Mira web).
    func bitrate(for client: JellyfinClient, fileBitrate: Int) async -> Int {
        let known: (at: Date, speed: Double)? = measure.flatMap { $0.server == client.server ? (at: $0.at, speed: $0.speed) : nil }
        if let bitrate = PlaybackPlan.knownBitrate(quality, atHome: ServerAddress.isHome(client.server), measure: known, fileBitrate: fileBitrate) {
            return bitrate
        }
        guard let speed = await client.measureSpeed() else { return PlaybackPlan.unmeasuredBitrate }
        measure = (Date(), speed, client.server)
        return PlaybackPlan.autoBitrate(speed: speed, fileBitrate: fileBitrate)
    }

    /// Signed out on this iPhone at once, then on Jellyfin; the account's Home kept for a quick start goes too.
    func signOut() {
        let client = client
        player?.close()
        together.signedOut()
        if let session {
            HomeCache.clear(userId: session.userId)
            RecentSearches.clear(session.userId)
            preferences.forget(account: session.userId)
        }
        choices.stop()
        SessionStore.delete()
        session = nil
        Task { await client?.signOut() }
    }

    /// Jellyfin no longer knows the token (401): back to the sign-in screen, which says why.
    func sessionEnded() {
        guard let session else { return }
        player?.close()
        together.signedOut()
        HomeCache.clear(userId: session.userId)
        RecentSearches.clear(session.userId)
        preferences.forget(account: session.userId)
        choices.stop()
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
