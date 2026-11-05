import Foundation
import Observation

/// « Ta party » of another Jellyfin (a mira://party/… link whose Jellyfin is not the one this iPhone is signed in to,
/// or no account here): joined as its guest, with no account there, as Mira for Windows and Mac do. The extension of
/// that Jellyfin opens a guest for this iPhone (POST Mira/guest/open), whose session reaches only its gateway
/// (`<server>/Mira/guest/`): the host's group, its title and its playback. While a guest, `AppModel.client` is the
/// guest's; the party's card (PartyView) takes the screen, the player opens above it when the group plays. « Leave the
/// party » ends the guest's session and brings this iPhone's own account back. The first name is kept for the next party.
@MainActor @Observable
final class Party {
    enum Stage: Equatable, Sendable { case name, opening, waiting, ended }

    /// The party's card is on screen.
    private(set) var shown = false
    private(set) var stage = Stage.name
    /// Who invites, once the party's page said it.
    private(set) var host: String?
    var name: String
    private(set) var error: String?
    /// The guest's session on the friend's Jellyfin, while this iPhone is its guest; never kept in the Keychain.
    private(set) var guest: Session?

    @ObservationIgnored weak var app: AppModel?
    @ObservationIgnored private var asked: (server: URL, code: String)?
    @ObservationIgnored private var waiting: Task<Void, Never>?
    @ObservationIgnored private var leaving = false

    init() {
        name = UserDefaults.standard.string(forKey: Keys.name) ?? ""
    }

    /// How this iPhone introduces itself to the friend's Jellyfin: its device id drawn from this iPhone's own, never that one.
    func device(_ own: DeviceIdentity) -> DeviceIdentity {
        DeviceIdentity(id: SyncRules.guestDevice(own.id), name: own.name, version: own.version)
    }

    /// The guest's way to the friend's Jellyfin.
    func client(_ own: DeviceIdentity) -> JellyfinClient? {
        guest.map { JellyfinClient(server: $0.server, device: device(own), token: $0.token, userId: $0.userId) }
    }

    /// Whose party a code is, as its Jellyfin says it (GET Mira/party/<code>, JSON).
    private struct PartyHost: Decodable, Sendable { let host: String }

    /// A party of another Jellyfin: its card, the first name asked; who invites named once its page says.
    func open(code: String, server: URL) async {
        guard let app else { return }
        if guest != nil, asked?.code == code {
            shown = true
            return
        }
        asked = (server, code)
        host = nil
        error = nil
        stage = .name
        shown = true
        let page = JellyfinClient(server: server, device: device(app.device), token: nil, userId: nil)
        do {
            let found = try await page.get("Mira/party/\(code)", as: PartyHost.self).host
            if asked?.code == code { host = found }
        } catch JellyfinError.http(404) {
            if asked?.code == code, stage == .name {
                asked = nil
                stage = .ended
            }
        } catch {
            // Not reached now: asked anyway, joining says why.
        }
    }

    /// « Join the party »: a guest of its own for this iPhone, under the first name typed; then Mira is theirs.
    func join() async {
        guard let asked, stage == .name, let app else { return }
        let typed = SyncRules.partyName(name)
        guard !typed.isEmpty else {
            error = String(localized: "Type your first name: it’s what the group sees.")
            return
        }
        stage = .opening
        error = nil
        let opened: Opened
        do {
            opened = try await Self.openGuest(server: asked.server, code: asked.code, name: typed, device: device(app.device))
        } catch let refused as Refused {
            guard self.asked?.code == asked.code else { return }
            // Taken back or made anew by its host: nothing more to try with this link.
            if refused.gone {
                self.asked = nil
                stage = .ended
            } else {
                stage = .name
                error = refused.message
            }
            return
        } catch {
            guard self.asked?.code == asked.code else { return }
            stage = .name
            self.error = Messages.text(error)
            return
        }
        guard self.asked?.code == asked.code else { return }
        name = typed
        UserDefaults.standard.set(typed, forKey: Keys.name)
        if let found = opened.host, !found.isEmpty { host = found }
        app.becomingGuest()
        guest = Session(server: SyncRules.guestServer(asked.server), serverId: opened.serverId ?? "", serverName: host ?? "",
                        userId: opened.userId, userName: opened.userName ?? typed, token: opened.token)
        stage = .waiting
        waitForGroup()
    }

    /// The host's group joined as soon as it is open (read every 4 seconds, three hours at most).
    func waitForGroup() {
        guard guest != nil, !leaving, stage != .ended else { return }
        waiting?.cancel()
        waiting = Task { [weak self] in
            let until = Date().addingTimeInterval(3 * 3600)
            while !Task.isCancelled, Date() < until {
                guard let self, self.guest != nil, let app = self.app, let client = app.client else { return }
                if app.together.group != nil { return }
                do {
                    let groups = try await client.syncGroups()
                    if let found = SyncRules.findGroup(groups, id: nil, host: self.host) {
                        try await app.together.join(found.groupId)
                        if app.together.group != nil { return }
                    }
                } catch JellyfinError.unauthorized {
                    // The host took the link back (the guest's account is gone with it), or made a new one.
                    self.ended()
                    return
                } catch {}
                try? await Task.sleep(for: .seconds(4))
            }
        }
    }

    /// Out of the host's group (it ended, Jellyfin let this iPhone go): waited for again, unless the party is over.
    func groupGone() {
        guard guest != nil, !leaving else { return }
        waitForGroup()
    }

    /// Jellyfin no longer knows the guest (401): this link's party is over.
    func ended() {
        guard guest != nil, stage != .ended else { return }
        waiting?.cancel()
        waiting = nil
        stage = .ended
        app?.player?.close()
        app?.together.signedOut()
    }

    /// « Leave the party », « Close », « Cancel »: out of the group, the guest's session ended, this iPhone's own account back.
    func leave() async {
        guard guest != nil else {
            asked = nil
            shown = false
            return
        }
        guard !leaving else { return }
        leaving = true
        defer { leaving = false }
        waiting?.cancel()
        waiting = nil
        let client = app?.client
        app?.player?.close()
        if app?.together.group != nil { await app?.together.leave() } else { app?.together.signedOut() }
        await client?.signOut()
        guest = nil
        asked = nil
        host = nil
        error = nil
        stage = .name
        shown = false
        app?.leftGuest()
    }

    // MARK: Opening the guest

    /// What the extension gives a guest it opened.
    private struct Opened: Decodable, Sendable {
        let token: String
        let userId: String
        let userName: String?
        let serverId: String?
        let host: String?
    }

    /// The extension's refusal, in its words (the link taken back, the party full, too many tries).
    private struct Refused: Error {
        let message: String
        let gone: Bool
    }

    private struct Answer: Decodable, Sendable { let error: String? }

    /// POST Mira/guest/open at the friend's Jellyfin, with no session: the guest's token, or why not.
    private static func openGuest(server: URL, code: String, name: String, device: DeviceIdentity) async throws -> Opened {
        let client = JellyfinClient(server: server, device: device, token: nil, userId: nil)
        var request = URLRequest(url: await Way.shared.map(client.url("Mira/guest/open")))
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue(client.authorization, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(Bundle.main.preferredLocalizations.first ?? "en", forHTTPHeaderField: "Accept-Language")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["party": code, "device": device.id, "name": device.name, "guest": name])
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await JellyfinClient.session.data(for: request)
        } catch let failure as URLError where failure.code == .timedOut {
            throw JellyfinError.timeout
        } catch is URLError {
            throw JellyfinError.unreachable
        }
        guard let http = response as? HTTPURLResponse else { throw JellyfinError.notJellyfin }
        if http.statusCode == 200 {
            do { return try JSONDecoder().decode(Opened.self, from: data) } catch { throw JellyfinError.notJellyfin }
        }
        if let said = try? JSONDecoder().decode(Answer.self, from: data).error, !said.isEmpty {
            throw Refused(message: said, gone: http.statusCode == 404)
        }
        // No answer of Mira's: an extension from before guests of a party.
        if http.statusCode == 404 {
            throw Refused(message: String(localized: "This Jellyfin doesn’t welcome guests yet: whoever invites you needs to update Mira."), gone: false)
        }
        throw JellyfinError.http(http.statusCode)
    }

    private enum Keys {
        static let name = "mira.party.name"
    }
}
