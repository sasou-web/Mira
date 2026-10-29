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
    /// The title playing, in Mira's player, reduced over the tabs, or in Picture in Picture.
    private(set) var player: PlayerSession?
    /// The title playing reduced to the bar over the tabs (MiniPlayer).
    private(set) var minimizedPlayer: PlayerSession?
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
    /// What is new in the series followed, said when Mira opens (NewEpisodes).
    let newEpisodes = NewEpisodesWatch()
    /// Why Jellyfin refused to mark a title watched or a favorite.
    var actionMessage: String?
    /// Bumped when a title stops or another one starts, or is marked watched or a favorite: every page reads Jellyfin again.
    private(set) var playback = 0
    /// The last measure of the connection to Jellyfin (bits per second), kept an hour.
    @ObservationIgnored private var measure: (at: Date, speed: Double, server: URL, way: WayPlan.Kind?)?
    /// Built-in Tailscale: Jellyfin away from home without Tailscale's app.
    let tailnet: Tailnet
    /// The way to Jellyfin taken now (Way), and what to say while there is none.
    private(set) var way: WayPlan.Route?
    private(set) var wayHint: WayPlan.Hint?
    @ObservationIgnored private let network = NetworkWatch()
    @ObservationIgnored private var inBackground = false

    init() {
        let preferences = Preferences()
        self.preferences = preferences
        choices = ChoicesSync(preferences: preferences)
        let defaults = UserDefaults.standard
        // The UI tests start from nothing: no account, no address typed before.
        let arguments = ProcessInfo.processInfo.arguments
        if arguments.contains("-mira-reset") {
            SessionStore.delete()
            defaults.removeObject(forKey: Keys.lastServer)
            HomeCache.clearAll()
            Tailnet.reset()
            Way.forgetAll()
        }
        // Once the UI tests' reset is done: it reads whether it is on.
        tailnet = Tailnet()
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
        // The UI tests reach the stand-in Jellyfin only through Built-in Tailscale, though it is on this Mac.
        var only: Set<WayPlan.Kind>?
        #if DEBUG
        if arguments.contains("-mira-way-builtin") { only = [.builtIn] }
        #endif
        Way.select(session?.server, id: session?.serverId)
        Task { [weak self, only] in
            await Way.shared.setup(readTailnet: { await TailscaleLibrary.snapshot() }, onChange: { route, hint in
                Task { @MainActor in self?.wayChanged(route, hint) }
            }, only: only)
            await Way.shared.wake()
        }
        network.start { network, interface in
            Task { await Way.shared.network(network) }
            if let interface { Task { await TailscaleLibrary.network(interface) } }
        }
        Task { await tailnet.start() }
    }

    private func wayChanged(_ route: WayPlan.Route?, _ hint: WayPlan.Hint?) {
        if route != way { way = route }
        if hint != wayHint { wayHint = hint }
    }

    /// Mira back on screen: Built-in Tailscale started again (or its relay checked), the way to Jellyfin looked at.
    func foreground() {
        let wasAway = inBackground
        inBackground = false
        guard wasAway else { return }
        Task {
            await tailnet.foreground(playing: player != nil)
            await Way.shared.wake()
        }
    }

    /// Mira off screen: Built-in Tailscale stops, unless a title plays on (Picture in Picture, AirPlay).
    func background() {
        inBackground = true
        tailnet.background(playing: player != nil)
    }

    /// What is new in the series followed: at the start, and back after 15 minutes away.
    func lookForNewEpisodes(comingBack: Bool = false) async {
        guard let client, let account = session?.userId else { return }
        await newEpisodes.look(client, account: account, comingBack: comingBack)
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
        Way.select(session.server, id: session.serverId)
        SessionStore.save(session)
        UserDefaults.standard.set(typed, forKey: Keys.lastServer)
        notice = nil
        self.session = session
        preferences.use(account: session.userId)
    }

    // MARK: Playing

    /// Plays a film or an episode in Mira's player, where it was left unless `fromStart`; a title already playing
    /// (reduced, or in Picture in Picture) stops first.
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
            // Closed from Picture in Picture with Mira off screen: Built-in Tailscale stops now.
            if inBackground { tailnet.background(playing: false) }
        }
        if minimizedPlayer === session { minimizedPlayer = nil }
        playback += 1
    }

    /// The player reduced to the bar over the tabs, or opened full again.
    func playerMinimized(_ session: PlayerSession, _ minimized: Bool) {
        guard player === session else { return }
        minimizedPlayer = minimized ? session : nil
    }

    func playbackChanged() { playback += 1 }

    /// The quality chosen in Settings: « auto », « max », or a bitrate in bits per second.
    var quality: String { preferences.quality }

    /// The bitrate to ask Jellyfin for (PlaybackInfo): the one chosen, full quality at home (Tailscale is not home),
    /// else what a measure of the connection allows for this file, kept an hour (knownBitrate of Mira web). Home is the
    /// way taken now: the server signed in at its Tailscale address is at home on the home's Wi-Fi.
    func bitrate(for client: JellyfinClient, fileBitrate: Int) async -> Int {
        let taken = await Way.shared.current()?.kind
        let atHome = taken.map { $0 == .home } ?? ServerAddress.isHome(client.server)
        let known: (at: Date, speed: Double)? = measure.flatMap { $0.server == client.server && $0.way == taken ? (at: $0.at, speed: $0.speed) : nil }
        if let bitrate = PlaybackPlan.knownBitrate(quality, atHome: atHome, measure: known, fileBitrate: fileBitrate) {
            return bitrate
        }
        guard let speed = await client.measureSpeed() else { return PlaybackPlan.unmeasuredBitrate }
        measure = (Date(), speed, client.server, taken)
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
            NewEpisodesWatch.forget(account: session.userId)
        }
        newEpisodes.dismiss()
        choices.stop()
        SessionStore.delete()
        let serverId = session?.serverId
        session = nil
        // Told to Jellyfin on the way taken, then the way forgotten with the server's other addresses.
        Task {
            await client?.signOut()
            if let serverId { await Way.shared.forget(serverId) }
            Way.select(nil, id: nil)
        }
    }

    /// Jellyfin no longer knows the token (401): back to the sign-in screen, which says why.
    func sessionEnded() {
        guard let session else { return }
        player?.close()
        together.signedOut()
        HomeCache.clear(userId: session.userId)
        RecentSearches.clear(session.userId)
        preferences.forget(account: session.userId)
        NewEpisodesWatch.forget(account: session.userId)
        newEpisodes.dismiss()
        choices.stop()
        SessionStore.delete()
        notice = String(localized: "Your session on Jellyfin has ended. Sign in again.")
        self.session = nil
        Way.select(nil, id: nil)
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
