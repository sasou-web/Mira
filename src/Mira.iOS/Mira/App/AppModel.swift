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
    /// A QR code of « Mira on your iPhone » (Windows, Mac) opened while signed out: the sign-in screen fills it in.
    var connectLink: ConnectLink?
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
    /// « Ta party » of another Jellyfin, joined as its guest: while it lasts, `client` is the guest's.
    let party = Party()
    /// The sleep timer, chosen on a title's page, read by the player it opens.
    var sleep = SleepTimer()
    /// Settings, some of them kept on the account's Jellyfin too (choices).
    let preferences: Preferences
    let choices: ChoicesSync
    /// What is new in the series followed, said when Mira opens (NewEpisodes).
    let newEpisodes = NewEpisodesWatch()
    /// Mira's opening, over everything until Home is drawn (OpeningView).
    let opening = Opening()
    /// « Keep on this iPhone »: films and episodes to watch without Jellyfin.
    let keeper = Keeper()
    /// Why Jellyfin refused to mark a title watched or a favorite.
    var actionMessage: String?
    /// What this account may do with collections (TitleCollections), read once from Users/Me for the account signed in.
    private(set) var collectionRights: (user: String, rights: TitleCollections.Rights)?
    @ObservationIgnored private var collectionRightsAsked: String?
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
    /// What the connection costs now (cellular, a personal hotspot, Low Data Mode): the quality's cap.
    private(set) var cost = NetworkWatch.Cost()
    @ObservationIgnored private var costSettling: Task<Void, Never>?
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
            KeptStore.erase(keeping: nil)
            defaults.removeObject(forKey: "mira.tailnet.outageHidden")
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
        party.app = self
        keeper.app = self
        keeper.use(session)
        preferences.use(account: session?.userId)
        preferences.onChange = { [weak self] in self?.choices.soon { [weak self] in self?.ownClient } }
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
        network.start { [weak self] network, interface, cost in
            Task { await Way.shared.network(network) }
            if let interface { Task { await TailscaleLibrary.network(interface) } }
            // No connection says nothing of the next one's cost.
            if network != .none { Task { @MainActor in self?.costChanged(cost) } }
        }
        Task { await tailnet.start() }
    }

    private func wayChanged(_ route: WayPlan.Route?, _ hint: WayPlan.Hint?) {
        // A way to Jellyfin again: the copies that wait go on, what was watched here is told.
        if route != nil, route != way { keeper.resume() }
        if route != way { way = route }
        if hint != wayHint { wayHint = hint }
    }

    /// Mira back on screen: Built-in Tailscale started again (or its relay checked), the way to Jellyfin looked at.
    func foreground() {
        let wasAway = inBackground
        inBackground = false
        guard wasAway else { return }
        Task {
            await tailnet.foreground(playing: player != nil || keeper.busy)
            await Way.shared.wake()
            keeper.resume()
        }
    }

    /// Mira off screen: Built-in Tailscale stops, unless a title plays on (Picture in Picture, AirPlay) or copies are
    /// being kept on this iPhone.
    func background() {
        inBackground = true
        keeper.background()
        tailnet.background(playing: player != nil || keeper.busy)
    }

    /// The copies all fetched (or stopped) while Mira is off screen: Built-in Tailscale stops now.
    func keepingEnded() {
        if inBackground, player == nil { tailnet.background(playing: false) }
    }

    /// What is new in the series followed: at the start, and back after 15 minutes away.
    func lookForNewEpisodes(comingBack: Bool = false) async {
        guard party.guest == nil, let client, let account = session?.userId else { return }
        await newEpisodes.look(client, account: account, comingBack: comingBack)
    }

    /// The account's choices compared with its Jellyfin: at the start, and back on the screen after a while.
    func syncChoices(lookAgain: Bool = false) {
        guard party.guest == nil else { return }
        if lookAgain { choices.lookAgain { [weak self] in self?.ownClient } } else if let client { choices.run(client) }
    }

    /// This iPhone's account on its Jellyfin; a friend's party's guest instead while this iPhone is one.
    var client: JellyfinClient? {
        if party.guest != nil { return party.client(device) }
        return session.map { JellyfinClient(server: $0.server, device: device, token: $0.token, userId: $0.userId) }
    }

    /// This iPhone's own account, for what is kept on its Jellyfin (choices): none while a friend's party's guest.
    var ownClient: JellyfinClient? { party.guest == nil ? client : nil }

    /// Becoming a friend's party's guest: this iPhone's own account let go (its session stays on its Jellyfin, and
    /// comes back on leaving the party), its title and its group closed here.
    func becomingGuest() {
        player?.close()
        together.signedOut()
        choices.stop()
        newEpisodes.dismiss()
        keeper.stop()
    }

    /// Out of a friend's party: this iPhone's own account again, every page read again.
    func leftGuest() {
        playback += 1
        keeper.resume()
    }

    var lastServer: String { UserDefaults.standard.string(forKey: Keys.lastServer) ?? "" }

    /// A mira:// link: a QR code of « Mira on your iPhone » (mira://connect), a group (mira://ensemble), or a party.
    func open(_ url: URL) async {
        if url.host()?.lowercased() == "ensemble" {
            await together.openGroup(url)
            return
        }
        guard url.host()?.lowercased() == "connect" else {
            await together.openParty(url)
            return
        }
        guard let link = ConnectLink.parse(url) else {
            if session == nil { notice = String(localized: "Mira can’t open this link.") } else { together.notice = String(localized: "Mira can’t open this link.") }
            return
        }
        guard let session else {
            connectLink = link
            return
        }
        // Signed in: the same server's addresses are learnt; another server waits until this account signs out.
        if let found = await link.find(), found.id.lowercased().filter({ $0 != "-" }) == session.serverId.lowercased().filter({ $0 != "-" }) {
            await Way.shared.teach(link.addresses, id: session.serverId)
            together.notice = String(localized: "Mira knows \(session.serverName)’s addresses now, at home and away.")
        } else {
            together.notice = String(localized: "You’re signed in to \(session.serverName). To use another server, sign out in Settings first.")
        }
    }

    func signedIn(_ session: Session, typed: String) {
        Way.select(session.server, id: session.serverId)
        SessionStore.save(session)
        UserDefaults.standard.set(typed, forKey: Keys.lastServer)
        notice = nil
        self.session = session
        preferences.use(account: session.userId)
        // Another account's copies leave this iPhone; this one's go on.
        keeper.use(session)
        keeper.resume()
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
    /// Jellyfin too, so that no other device offers it (hideFromResume of Mira web). Its card leaving the row says it.
    func hideFromContinueWatching(_ item: Item) async {
        preferences.hide(item.seriesId ?? item.id, true)
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
    /// way taken now: the server signed in at its Tailscale address is at home on the home's Wi-Fi. Under the cap of
    /// mobile data, whatever was chosen.
    func bitrate(for client: JellyfinClient, fileBitrate: Int) async -> Int {
        let taken = await Way.shared.current()?.kind
        let atHome = taken.map { $0 == .home } ?? ServerAddress.isHome(client.server)
        let known: (at: Date, speed: Double)? = measure.flatMap { $0.server == client.server && $0.way == taken ? (at: $0.at, speed: $0.speed) : nil }
        let cap = mobileCap
        if let bitrate = PlaybackPlan.knownBitrate(quality, atHome: atHome, measure: known, fileBitrate: fileBitrate) {
            return PlaybackPlan.capped(bitrate, by: cap)
        }
        if let cap, !PlaybackPlan.measures(under: cap) { return cap }
        guard let speed = await client.measureSpeed() else { return PlaybackPlan.capped(PlaybackPlan.unmeasuredBitrate, by: cap) }
        measure = (Date(), speed, client.server, taken)
        return PlaybackPlan.capped(PlaybackPlan.autoBitrate(speed: speed, fileBitrate: fileBitrate), by: cap)
    }

    /// The cap of the connection now (Settings → Playback → Mobile data); nil on Wi-Fi or a cable.
    var mobileCap: Int? { PlaybackPlan.mobileCap(preferences.mobileData, expensive: cost.expensive, constrained: cost.constrained) }

    /// Another connection: the title playing follows its cap once it has held 4 s (leaving the home's Wi-Fi, the
    /// iPhone goes back and forth for a moment).
    private func costChanged(_ cost: NetworkWatch.Cost) {
        guard cost != self.cost else { return }
        self.cost = cost
        mobileDataChanged(after: .seconds(4))
    }

    /// Mobile data chosen in Settings, or the connection changed: the title playing follows the new cap.
    func mobileDataChanged(after delay: Duration = .zero) {
        costSettling?.cancel()
        costSettling = Task { [weak self] in
            if delay > .zero { try? await Task.sleep(for: delay) }
            guard !Task.isCancelled, let self else { return }
            self.player?.capChanged(self.mobileCap)
        }
    }

    /// « Add to a collection… » for this account: an administrator, or an account allowed to manage collections; never a
    /// friend's party's guest.
    var managesCollections: Bool {
        party.guest == nil && collectionRights?.user == session?.userId && collectionRights?.rights.manage == true
    }

    /// Reads once what this account may do with collections (Users/Me); asked again after a failure.
    func collectionRightsNeeded() {
        guard party.guest == nil, let client, let user = session?.userId, collectionRightsAsked != user else { return }
        collectionRightsAsked = user
        Task {
            do {
                let me = try await client.me()
                if session?.userId == user { collectionRights = (user, TitleCollections.Rights.from(me.policy)) }
            } catch {
                if collectionRightsAsked == user { collectionRightsAsked = nil }
            }
        }
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
        collectionRights = nil
        collectionRightsAsked = nil
        VirtualEpisodes.forget()
        newEpisodes.dismiss()
        choices.stop()
        // What was kept on this iPhone goes with the account.
        keeper.signedOut()
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
        // A friend's party's guest that Jellyfin no longer knows: the party is over, this iPhone's account stays.
        if party.guest != nil {
            party.ended()
            return
        }
        guard let session else { return }
        player?.close()
        together.signedOut()
        HomeCache.clear(userId: session.userId)
        RecentSearches.clear(session.userId)
        preferences.forget(account: session.userId)
        NewEpisodesWatch.forget(account: session.userId)
        collectionRights = nil
        collectionRightsAsked = nil
        VirtualEpisodes.forget()
        newEpisodes.dismiss()
        choices.stop()
        // The copies stay for the same account signed in again: nothing is fetched meanwhile.
        keeper.stop()
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
