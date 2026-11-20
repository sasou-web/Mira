import Foundation
import Observation
import Security
import UIKit

/// A friend's invite opened on the iPhone, as on Windows and Mac (ogsas, 2026-11-20: maquette A « Le lien »):
/// mira://join#… brings this iPhone onto the friend's tailnet through the home's own node (HomeNode, which reaches
/// nothing but that Jellyfin), then waits for the friend to let it in (« Let in »), even across restarts. Said before
/// anything: what the friend sees of this iPhone. Then a session of the account the friend made (« mira-friend-… »).
@MainActor @Observable
final class FriendInvite {
    enum Stage: Equatable {
        case none
        /// The invite, nothing done yet.
        case invited
        /// Used: waiting for the friend to let this iPhone in.
        case waiting
        /// Over: why (heading) and what to do (line).
        case ended(String, String)
    }

    private(set) var stage = Stage.none
    /// What the page says while something runs (« Reaching your friend's server… »).
    private(set) var busy: String?
    /// What went wrong, the page staying as it is.
    var said: String?
    /// The invite on the page; nil once used (then the wait).
    private var link: JoinLink?
    private var waiting: FriendWait?
    private var polling: Task<Void, Never>?

    /// The device id the extension gives the sessions of friends' devices (FriendController of Mira.Jellyfin).
    static let devicePrefix = "mira-friend-"

    @ObservationIgnored weak var app: AppModel?

    var shown: Bool { stage != .none }
    private var node: HomeNode? { app?.family.node }

    /// Whether the session of this iPhone is a friend's (its device id is a friend device's).
    func isFriendSession(_ session: Session?) -> Bool { session?.deviceId?.hasPrefix(Self.devicePrefix) == true }

    /// At Mira's start: a wait left open goes on (its page shown), and a friend's session has the node carry its Jellyfin.
    func start() {
        if let saved = FriendStore.load() {
            wait(saved)
            Task { _ = await node?.start(key: nil) }
            return
        }
        guard let session = app?.session, isFriendSession(session) else { return }
        node?.route(session.serverName)
        Task {
            // Signed out without a key: the friend took this iPhone off their network.
            if await node?.start(key: nil) == "needs-login" { app?.together.notice = FriendText.removed }
        }
    }

    // MARK: The invite

    /// A friend's invite opened (mira://join#…): its page, nothing done before « Start watching ».
    func openLink(_ link: JoinLink) {
        // One node of the kind per iPhone: a home joined by a link holds it.
        if app?.family.membership?.throughTailnet == true {
            self.link = nil
            said = nil
            stage = .ended(FriendText.invited, FriendText.inHomeAlready)
            return
        }
        self.link = link
        said = nil
        stage = .invited
    }

    /// « Start watching »: the node on the friend's tailnet by the link's key, the invite used, then the wait.
    func begin() async {
        guard busy == nil, let link, let node, let app else { return }
        // Already a friend of another server: its node leaves for this one (the key would not move it).
        if let session = app.session, isFriendSession(session), !WayPlan.same(session.server, link.address) {
            await node.leave()
            app.signOut()
        }
        said = nil
        busy = FriendText.reaching
        defer { busy = nil }
        if let code = await node.start(key: link.key) {
            // A spent key: the invite was used, or it ended.
            if code == "needs-login" || code == "expired" {
                self.link = nil
                stage = .ended(FriendText.used, FriendText.usedLine)
            } else {
                said = switch code {
                case "offline": HomeText.offline
                case "config", "relay", "start", "library", "keychain", "locked": HomeText.noNode
                default: FriendText.noAnswer
                }
            }
            return
        }
        guard let base = node.base(link.address) else {
            said = HomeText.noNode
            return
        }
        do {
            let token = try await HomeAPI(base: base, device: app.device).friendRedeem(invite: link.key, name: String(localized: "Mira on \(UIDevice.current.model)"))
            self.link = nil
            let saved = FriendWait(server: link.address, wait: token)
            FriendStore.save(saved)
            wait(saved)
        } catch let refused as FriendRefused where refused.state == "used" || refused.state == "ended" {
            self.link = nil
            stage = refused.state == "used" ? .ended(FriendText.used, FriendText.usedLine) : .ended(FriendText.ended, FriendText.endedLine)
        } catch let refused as FriendRefused {
            said = refused.message
        } catch {
            said = FriendText.noAnswer
        }
    }

    // MARK: Waiting to be let in

    private func wait(_ saved: FriendWait) {
        waiting = saved
        said = nil
        stage = .waiting
        polling?.cancel()
        polling = Task { [weak self] in await self?.poll(saved) }
    }

    /// Asked every few seconds while the page shows; after a few misses, said (the friend's PC asleep), still asked.
    private func poll(_ saved: FriendWait) async {
        var quiet = 0
        while !Task.isCancelled {
            do {
                guard let node, let app, let base = node.base(saved.server) else { throw JellyfinError.notJellyfin }
                if let opened = try await HomeAPI(base: base, device: app.device).friendWait(saved.wait) {
                    guard !Task.isCancelled else { return }
                    await letIn(saved, opened)
                    return
                }
                quiet = 0
                if stage == .waiting { said = nil }
            } catch let refused as FriendRefused where refused.state == "refused" || refused.state == "ended" {
                await forget()
                if stage == .waiting || stage == .none {
                    stage = .ended(refused.state == "ended" ? FriendText.ended : FriendText.refused, FriendText.usedLine)
                }
                return
            } catch {
                if Task.isCancelled { return }
                quiet += 1
                if quiet >= 4, stage == .waiting { said = FriendText.notAnswering }
            }
            try? await Task.sleep(for: .seconds(quiet > 0 ? 10 : 4))
        }
    }

    /// Let in: the session saved and opened as any other, the page gone.
    private func letIn(_ saved: FriendWait, _ opened: HomeOpened) async {
        guard let app, let node else { return }
        polling = nil
        waiting = nil
        FriendStore.delete()
        var name = "Jellyfin"
        if let base = node.base(saved.server), let read = await Way.askJellyfin(base, 10)?.serverName, !read.isEmpty { name = read }
        let previous = app.session
        node.route(name)
        app.switchSession(Session(server: saved.server, serverId: opened.serverId ?? "", serverName: name, userId: opened.userId,
                                  userName: opened.userName, token: opened.token, deviceId: opened.deviceId), typed: nil)
        stage = .none
        // Signed in with an account of its own before: that session ends on its Jellyfin, nothing here could end it later.
        if let previous, !isFriendSession(previous), !(previous.deviceId?.hasPrefix(Family.devicePrefix) ?? false),
           !(WayPlan.same(previous.server, saved.server) && previous.userId == opened.userId) {
            let client = JellyfinClient(server: previous.server, device: app.device, token: previous.token, userId: previous.userId)
            Task { await client.signOut() }
        }
    }

    /// The wait over: forgotten, and the node off the tailnet unless a friend's session still goes through it.
    private func forget() async {
        polling?.cancel()
        polling = nil
        waiting = nil
        FriendStore.delete()
        if !isFriendSession(app?.session) { await node?.leave() }
    }

    // MARK: Leaving the page

    /// « Not now » (the invite stays usable) or « Close »: back to what was there; a wait goes on at the next opening.
    func close() {
        polling?.cancel()
        polling = nil
        link = nil
        said = nil
        stage = .none
    }

    /// The friend's session signed out here: the node stops carrying it (its state stays; the friend can remove it).
    func signedOut() {
        node?.route(nil)
        Task { await node?.stop() }
    }

    // MARK: Mira on and off the screen

    func foreground() async {
        guard isFriendSession(app?.session) else { return }
        await node?.foreground()
    }

    func background(playing: Bool) {
        guard isFriendSession(app?.session) else { return }
        node?.background(playing: playing)
    }

    /// The UI tests start from nothing.
    nonisolated static func reset() { FriendStore.delete() }
}

/// A wait left open: where the friend's Jellyfin is on their tailnet, and the token it asks with (FriendWait of Core).
struct FriendWait: Codable, Sendable, Hashable {
    var server: URL
    var wait: String
}

/// The extension's refusal of a friend's link, in its words, and its state (« used », « ended », « refused »…).
struct FriendRefused: Error, Sendable {
    var message: String
    var state: String
}

/// The answers of /Mira/friend for the friend's side, every field optional.
private struct FriendAnswer: Decodable {
    var state: String?
    var wait: String?
    var error: String?
    var accessToken: String?
    var userId: String?
    var userName: String?
    var serverId: String?
    var deviceId: String?
}

extension HomeAPI {
    /// The link's invitation traded for a wait: the extension holds this iPhone at the door until the friend lets it in.
    func friendRedeem(invite: String, name: String) async throws -> String {
        let answer = try await friend("Mira/friend/redeem", ["invite": invite, "device": device.id, "name": name, "client": "Mira", "version": device.version])
        guard answer.status == 202, let wait = answer.answer?.wait, !wait.isEmpty else { throw JellyfinError.notJellyfin }
        return wait
    }

    /// Whether the friend let this iPhone in yet: nil while it waits, its session once in.
    func friendWait(_ wait: String) async throws -> HomeOpened? {
        let answer = try await friend("Mira/friend/wait", ["wait": wait, "client": "Mira", "version": device.version])
        if answer.status == 202 { return nil }
        guard let read = answer.answer, let access = read.accessToken, !access.isEmpty, let user = read.userId, !user.isEmpty,
              let deviceId = read.deviceId, !deviceId.isEmpty else { throw JellyfinError.notJellyfin }
        return HomeOpened(token: access, userId: user, userName: read.userName.flatMap { $0.isEmpty ? nil : $0 } ?? user, serverId: read.serverId, deviceId: deviceId)
    }

    private func friend(_ path: String, _ body: [String: Any]) async throws -> (status: Int, answer: FriendAnswer?) {
        let (status, data, json) = try await send("POST", path, token: nil, body: body, timeout: 20)
        let answer = json ? try? JSONDecoder().decode(FriendAnswer.self, from: data) : nil
        if (200..<300).contains(status) { return (status, answer) }
        // No extension, or one older than friends' links: Jellyfin's own 404.
        if answer == nil && status == 404 { throw FriendRefused(message: FriendText.noExtension, state: "old") }
        let words = answer?.error?.trimmingCharacters(in: .whitespacesAndNewlines)
        throw FriendRefused(message: words.flatMap { $0.isEmpty ? nil : $0 } ?? String(localized: "Jellyfin answered with an error (\(status)). Try again in a moment."),
                            state: answer?.state ?? "unreachable")
    }
}

/// A wait left open, in the Keychain (this device only): the next opening of Mira goes on with it.
enum FriendStore {
    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "Mira", kSecAttrAccount as String: "friend-wait"]
    }

    static func load() -> FriendWait? {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var found: CFTypeRef?
        guard SecItemCopyMatching(request as CFDictionary, &found) == errSecSuccess, let data = found as? Data else { return nil }
        return try? JSONDecoder().decode(FriendWait.self, from: data)
    }

    static func save(_ wait: FriendWait) {
        guard let data = try? JSONEncoder().encode(wait) else { return }
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

/// What Mira says of a friend's invite, the same words as Mira for Windows and Mac (FriendText of Core).
enum FriendText {
    static var invited: String { String(localized: "You’re invited to watch") }
    static var privateLine: String { String(localized: "A private line to your friend’s PC, made for you. Nothing to install or sign up for.") }
    static var visible: String {
        String(localized: "Your friend sees this device’s name, its system, its public IP address and when it last connected, and what you watch on their server, like on any shared server.")
    }
    static var start: String { String(localized: "Start watching") }
    static var notNow: String { String(localized: "Not now") }
    static var reaching: String { String(localized: "Reaching your friend’s server…") }
    static var almostThere: String { String(localized: "Almost there") }
    static var waitingForHost: String { String(localized: "Your friend needs to let this device in: they see it in Mira. Keep Mira open.") }
    static var used: String { String(localized: "This invite was already used") }
    static var usedLine: String { String(localized: "An invite works once. Ask your friend for a new one: it takes them one click.") }
    static var ended: String { String(localized: "This invite has ended") }
    static var endedLine: String { String(localized: "An invite lasts 48 hours. Ask your friend for a new one: it takes them one click.") }
    static var refused: String { String(localized: "Your friend didn’t let this device in. Ask them for a new invite.") }
    static var haveAddress: String { String(localized: "I have an address instead") }
    static var noExtension: String { String(localized: "This server doesn’t have the up-to-date Mira extension yet: ask your friend to update Mira.") }
    static var inHomeAlready: String { String(localized: "This device is already in a home joined by a link. Leave that home first, then open the invite again.") }
    static var removed: String { String(localized: "Your friend took this device off their server. Ask them for a new invite.") }
    static var noAnswer: String { String(localized: "Your friend’s server didn’t answer. Try again in a moment.") }
    static var notAnswering: String { String(localized: "Your friend’s server isn’t answering right now. Mira keeps trying.") }
}
