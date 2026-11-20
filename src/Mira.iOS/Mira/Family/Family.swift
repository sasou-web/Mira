import Foundation
import Observation
import Security
import UIKit

/// « Le foyer » on the iPhone, as on Windows and Mac (ogsas, 2026-11-19: maquette A « Le hall », with the quick switch
/// of C): the iPhone joins a home with the home's password, at the address of its Jellyfin or from a home link
/// (mira://home#…, whose key brings the home's own Tailscale node in, HomeNode), then « Who's watching? » at each
/// opening of Mira, a profile's PIN checked by the extension, and the profiles again from a long press on the Settings
/// tab. Each profile is a real Jellyfin session of its account, opened by the extension (/Mira/home), under the home's
/// device id. Never an administrator's account.
@MainActor @Observable
final class Family {
    /// What a page over the app shows.
    enum Stage: Equatable {
        case none
        case hall
        case pin(HomeProfile)
        case join(JoinCard)
    }

    /// The join card: joining the home found at an address typed, or the one a link names.
    struct JoinCard: Equatable {
        var title: String
        var fromLink: Bool
        var heading: String
        var note: String
        var ownLabel: String
        var canJoin: Bool
        var server: URL
        var name: String
        var serverId: String?
        var link: HomeLink?
    }

    /// This iPhone in a home (nil in none): read at the start, kept in the Keychain as it changes.
    private(set) var membership: HomeMembership?
    private(set) var stage = Stage.none
    /// What a page says while something runs (« Opening Sam's profile… »).
    private(set) var busy: String?
    /// What the hall, the PIN or the join card says went wrong, and whether « Try again » helps.
    var said: String?
    private(set) var retry = false
    /// The PIN asked from the switch sheet, over the library: « Back » goes back to it, not to the hall.
    private(set) var pinFromMenu = false
    /// The quick switch's sheet (a long press on the Settings tab).
    var switching = false
    /// The server found at the address typed has a home, and the person chose their own account: not offered again.
    private var declined = false
    /// The device id the extension gives the sessions of a home's profiles (HomeController of Mira.Jellyfin).
    static let devicePrefix = "mira-home-"

    @ObservationIgnored weak var app: AppModel?
    let node = HomeNode()

    init() {
        membership = HomeStore.load()
    }

    var shown: Bool { stage != .none }

    /// Whether the session of this iPhone is one of the home's profiles (its device id is the home's).
    func isHomeSession(_ session: Session?) -> Bool {
        guard let session, let membership, let device = session.deviceId else { return false }
        return device.hasPrefix(Self.devicePrefix) && WayPlan.same(session.server, membership.server)
    }

    /// The home's API where its Jellyfin is reached now: its address, or through the home's node away from home.
    private func api(_ home: HomeMembership) -> HomeAPI? {
        guard let device = app?.device else { return nil }
        if home.throughTailnet {
            guard let base = node.base(home.server) else { return nil }
            return HomeAPI(base: base, device: device)
        }
        return HomeAPI(base: home.server, device: device)
    }

    /// A profile's picture for the hall and the switch sheet.
    func picture(_ profile: HomeProfile) -> URL? {
        guard let membership else { return nil }
        return api(membership)?.picture(profile)
    }

    // MARK: Opening Mira, and the hall

    /// At Mira's start, in a home: « Who's watching? », its profiles as last read, then as the home says now. The home's
    /// node carries the profile's Jellyfin when it was joined from a link. Signed in last time with an account of its own
    /// (« I have my own account »): that account opens, as it would without a home; the hall stays in the switch sheet.
    func start() {
        guard let membership else { return }
        if let session = app?.session, !isHomeSession(session) {
            if membership.throughTailnet, WayPlan.same(session.server, membership.server) { node.route(membership.name) }
            return
        }
        if membership.throughTailnet, isHomeSession(app?.session) { node.route(membership.name) }
        showHall()
        Task { await refresh() }
    }

    func showHall(_ text: String? = nil, retry: Bool = false) {
        guard let membership else { return }
        pinFromMenu = false
        said = text ?? (membership.profiles.isEmpty ? HomeText.noProfiles : nil)
        self.retry = retry
        stage = .hall
    }

    /// The home's profiles as it says now; the hall says why not when it cannot (the home's node, the network, out of the home).
    func refresh() async {
        guard let home = membership else { return }
        if case .hall = stage, said == HomeText.unreachable(home.name) { said = nil; retry = false }
        guard await reach(home, key: nil), let api = api(home) else { return }
        do {
            let now = try await api.profiles(token: home.token)
            guard membership == home else { return }
            save(HomeMembership(server: home.server, serverId: home.serverId, name: home.name, token: home.token, deviceId: now.deviceId,
                                throughTailnet: home.throughTailnet, profiles: now.profiles, current: now.current))
            if stage == .hall { showHall() }
        } catch let refused as HomeRefused where refused.kind == .outOfHome {
            askPasswordAgain(home, refused.message)
        } catch let refused as HomeRefused where refused.kind == .noHome {
            hallSays(refused.message, retry: false)
        } catch {
            hallSays(HomeText.unreachable(home.name), retry: true)
        }
    }

    private func hallSays(_ text: String, retry: Bool) {
        guard stage == .hall else { return }
        said = text
        self.retry = retry
    }

    /// The home's Jellyfin reachable: through the home's node when it was joined from a link (the link's key the first
    /// time, then its saved state). False, said on the page shown, when the node cannot take this iPhone there.
    private func reach(_ home: HomeMembership, key: String?) async -> Bool {
        guard home.throughTailnet || key != nil else { return true }
        if node.relay == nil { busy = HomeText.reaching(home.name) }
        let code = await node.start(key: key)
        busy = nil
        guard let code else { return true }
        let text = HomeText.node(code, name: home.name, withKey: key != nil)
        switch stage {
        case .join: said = text
        case .none: app?.together.notice = text
        default: showHall(text, retry: code != "needs-login")
        }
        return false
    }

    // MARK: A profile chosen

    /// A tile, or a profile of the switch sheet: the profile open on this iPhone goes on with its session; another one
    /// asks for its PIN when it has one, then the extension opens its session.
    func choose(_ profile: HomeProfile, fromMenu: Bool = false) async {
        guard busy == nil, let home = membership, let app else { return }
        if profile.id == home.current, let session = app.session, session.userId == profile.id, isHomeSession(session) {
            if home.throughTailnet, !(await reach(home, key: nil)) { return }
            stage = .none
            return
        }
        if profile.pin {
            pinFromMenu = fromMenu
            askPin(profile)
            return
        }
        await open(profile, pin: nil)
    }

    func open(_ profile: HomeProfile, pin: String?) async {
        guard busy == nil, let home = membership, let app else { return }
        if case .pin = stage { said = nil }
        busy = String(localized: "Opening \(profile.name)’s profile…")
        defer { busy = nil }
        guard await reach(home, key: nil), let api = api(home) else { return }
        do {
            let opened = try await api.open(token: home.token, profile: profile.id, pin: pin)
            guard membership == home else { return }
            var now = home
            now.current = profile.id
            now.deviceId = opened.deviceId
            save(now)
            let previous = app.session
            if home.throughTailnet { node.route(home.name) } else { node.route(nil) }
            app.switchSession(Session(server: home.server, serverId: opened.serverId ?? home.serverId ?? "", serverName: home.name, userId: opened.userId,
                                      userName: opened.userName, token: opened.token, deviceId: opened.deviceId),
                              typed: home.throughTailnet ? nil : home.server.absoluteString)
            pinFromMenu = false
            stage = .none
            // Signed in with an account of its own before: that session ends on its Jellyfin, nothing here could end it later.
            if let previous, !(previous.deviceId?.hasPrefix(Self.devicePrefix) ?? false),
               !(WayPlan.same(previous.server, home.server) && previous.userId == opened.userId) {
                let client = JellyfinClient(server: previous.server, device: app.device, token: previous.token, userId: previous.userId)
                Task { await client.signOut() }
            }
        } catch let refused as HomeRefused {
            switch refused.kind {
            case .pinAsked: askPin(profile)
            case .pinWrong: askPin(profile, HomeText.pinLeft(refused.left ?? 0))
            case .pinWait: askPin(profile, HomeText.pinWait(refused.wait ?? 300))
            case .outOfHome: askPasswordAgain(home, refused.message)
            default:
                failed(refused.message)
                await refresh()
            }
        } catch {
            failed(HomeText.unreachable(home.name))
        }
    }

    /// A failure said where the person is: on the PIN page, the hall, the join card, or as a notice from the sheet.
    private func failed(_ text: String) {
        switch stage {
        case .none: app?.together.notice = text
        case .hall: hallSays(text, retry: true)
        default: said = text
        }
    }

    // MARK: The PIN

    private func askPin(_ profile: HomeProfile, _ error: String? = nil) {
        said = error
        stage = .pin(profile)
    }

    func pinBack() {
        said = nil
        // Opened from the switch sheet, over the library: back to it.
        if pinFromMenu, app?.session != nil {
            pinFromMenu = false
            stage = .none
            return
        }
        showHall()
    }

    /// The hall over the library goes away (a profile already open here).
    func closeHall() {
        guard app?.session != nil, isHomeSession(app?.session) || membership == nil else { return }
        stage = .none
    }

    // MARK: Joining

    /// A home link opened in Mira (mira://home#…, from its page): the home's card, its password asked.
    func openLink(_ text: String) {
        guard let link = HomeLink.read(text) else {
            if shown { said = HomeText.linkBroken } else if app?.session != nil { app?.together.notice = HomeText.linkBroken } else { app?.notice = HomeText.linkBroken }
            return
        }
        // Already in this home: its hall.
        if let membership, link.is(membership.serverId) {
            showHall()
            Task { await refresh() }
            return
        }
        let expired = link.expired()
        said = expired ? HomeText.linkExpired(link.name) : nil
        stage = .join(JoinCard(title: HomeText.join(link.name), fromLink: true, heading: HomeText.fromLink(link.name), note: HomeText.fromLinkNote,
                               ownLabel: HomeText.ownAccount, canJoin: !expired, server: link.address, name: link.name, serverId: link.serverId.isEmpty ? nil : link.serverId, link: link))
    }

    /// The sign-in found a server at the address typed, and it has a home: its password, no account to type (« Use my
    /// own account » goes back to the form). True when the card shows.
    @discardableResult
    func offer(_ found: ServerInfo) async -> Bool {
        guard !declined, membership == nil, let app, app.session == nil, !app.party.shown, case .none = stage else { return false }
        guard await HomeAPI(base: found.address, device: app.device).on() == true, app.session == nil, case .none = stage else { return false }
        said = nil
        stage = .join(JoinCard(title: String(localized: "Sign in"), fromLink: false, heading: HomeText.homeOf(found.name), note: HomeText.foundHere,
                               ownLabel: String(localized: "Use my own account"), canJoin: true, server: found.address, name: found.name, serverId: found.id, link: nil))
        return true
    }

    /// Out of the home (removed, its password changed): the password again, where it was.
    private func askPasswordAgain(_ home: HomeMembership, _ text: String) {
        said = text
        stage = .join(JoinCard(title: HomeText.join(home.name), fromLink: home.throughTailnet, heading: HomeText.homeOf(home.name),
                               note: home.throughTailnet ? HomeText.fromLinkNote : HomeText.foundHere, ownLabel: HomeText.ownAccount, canJoin: true,
                               server: home.server, name: home.name, serverId: home.serverId, link: nil))
    }

    func join(_ password: String) async {
        guard busy == nil, case let .join(card) = stage, app != nil else { return }
        guard !password.isEmpty else {
            said = String(localized: "Type the home’s password.")
            return
        }
        if let link = card.link, link.expired(), membership?.throughTailnet != true {
            said = HomeText.linkExpired(link.name)
            return
        }
        said = nil
        busy = String(localized: "Signing in…")
        defer { busy = nil }
        let through = card.link != nil || (membership?.throughTailnet == true && membership.map { WayPlan.same($0.server, card.server) } == true)
        let trial = HomeMembership(server: card.server, serverId: card.serverId, name: card.name, token: "", deviceId: "", throughTailnet: through)
        if through, !(await reach(trial, key: card.link?.key)) { return }
        guard let api = api(trial) else { return }
        // The Jellyfin the link names, and no other: its own id first.
        let id = await api.serverId()
        if let wanted = card.serverId, HomeLink.plain(id ?? "") != HomeLink.plain(wanted) {
            said = id == nil ? HomeText.unreachable(card.name) : HomeText.notThisServer(card.name)
            return
        }
        do {
            let joined = try await api.join(password: password, name: String(localized: "Mira on \(UIDevice.current.model)"))
            save(HomeMembership(server: card.server, serverId: id ?? card.serverId, name: card.name, token: joined.token, deviceId: joined.deviceId,
                                throughTailnet: through, profiles: joined.profiles, current: nil))
            showHall()
        } catch let refused as HomeRefused {
            said = refused.message
        } catch {
            said = HomeText.unreachable(card.name)
        }
    }

    /// « I have my own account »: the usual sign-in (or the account open), the home left aside (a home of this iPhone stays).
    func ownAccount() {
        // The home found at the address typed, set aside: not offered again this time (a link's card may come back).
        if case let .join(card) = stage, !card.fromLink { declined = true }
        said = nil
        stage = .none
        node.route(nil)
    }

    // MARK: Leaving

    /// « Leave this home »: the extension told, the home's node off the tailnet and its state gone, the profile's
    /// session closed here.
    func leave() async {
        guard busy == nil, let home = membership, let app else { return }
        busy = String(localized: "Leaving the home…")
        var told = false
        if await reach(home, key: nil), let api = api(home) { told = await api.leave(token: home.token) }
        if home.throughTailnet { await node.leave() } else { node.route(nil) }
        membership = nil
        HomeStore.delete()
        busy = nil
        said = nil
        stage = .none
        let text = told ? HomeText.left(home.name) : HomeText.leftHere(home.name)
        // The profile's session was the home's: it ended with the device on the server.
        if let session = app.session, session.deviceId?.hasPrefix(Self.devicePrefix) == true {
            app.signOut()
            app.notice = text
        } else {
            app.together.notice = text
        }
    }

    /// The profile's session ended (signed out here, or Jellyfin refused its token): the hall again.
    func sessionClosed(_ text: String? = nil) {
        guard membership != nil else { return }
        node.route(nil)
        var home = membership
        home?.current = nil
        if let home { save(home) }
        showHall(text)
    }

    // MARK: Mira on and off the screen

    func foreground() async {
        guard let home = membership, home.throughTailnet, isHomeSession(app?.session) else { return }
        await node.foreground()
    }

    func background(playing: Bool) {
        guard membership?.throughTailnet == true else { return }
        node.background(playing: playing)
    }

    private func save(_ home: HomeMembership) {
        membership = home
        HomeStore.save(home)
    }

    /// The UI tests start from nothing: no home, its node's state gone.
    nonisolated static func reset() {
        HomeStore.delete()
        HomeNode.forget()
    }
}

/// This iPhone's home in the Keychain, readable after the first unlock, on this device only.
enum HomeStore {
    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "Mira", kSecAttrAccount as String: "home"]
    }

    static func load() -> HomeMembership? {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var found: CFTypeRef?
        guard SecItemCopyMatching(request as CFDictionary, &found) == errSecSuccess, let data = found as? Data else { return nil }
        return try? JSONDecoder().decode(HomeMembership.self, from: data)
    }

    static func save(_ home: HomeMembership) {
        guard let data = try? JSONEncoder().encode(home) else { return }
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
