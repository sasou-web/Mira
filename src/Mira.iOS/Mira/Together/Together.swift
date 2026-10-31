import Foundation
import Observation

/// What the player does for the group (PlayerSession): load the group's title where it starts, take its commands.
@MainActor
protocol GroupPlayer: AnyObject {
    var groupItemId: String? { get }
    var groupPosition: Double { get }
    /// The group's title again, from `startTicks`: paused there, then « ready ».
    func groupPrepare(_ startTicks: Int64)
    /// The group put another title on (the next episode, a title chosen on another device).
    func groupSwitch(to itemId: String, startTicks: Int64)
    func groupCommand(_ plan: SyncPlan)
    func groupLeft()
}

/// « Watch together »: Jellyfin's SyncPlay, as Mira web (together.js), Windows and Mac have it. A group on the server
/// plays one title at the same moment on every device in it; Lecture puts a title on for the group, and pause, play and
/// seeks go to Jellyfin, which tells each device what to do and when, on its clock. Its messages come through Jellyfin's
/// WebSocket, open while in a group. Jellyfin lets a device go with its socket (Mira in the background, Wi-Fi to 4G):
/// back, it joins the group again, or makes it again under the same name when it made it and the group ended empty, so
/// the invitation sent still finds it.
@MainActor @Observable
final class Together {
    private(set) var group: SyncGroup?
    private(set) var queue: SyncQueue?
    /// The groups open on Jellyfin, read with Home and a title's page while this iPhone is in none.
    private(set) var open: [SyncGroup] = []
    /// A line said over the app for a few seconds: someone came or went, the group ended.
    var notice: String?
    /// Someone came or went: the player's pill says it a few seconds.
    private(set) var member: (text: String, at: Date)?

    @ObservationIgnored weak var app: AppModel?
    @ObservationIgnored private weak var player: GroupPlayer?
    @ObservationIgnored private var socket: URLSessionWebSocketTask?
    @ObservationIgnored private var connection: Task<Void, Never>?
    @ObservationIgnored private var socketOpen = false
    @ObservationIgnored private var beat: Task<Void, Never>?
    @ObservationIgnored private var clock: Task<Void, Never>?
    /// Jellyfin's clock minus this iPhone's, in milliseconds.
    @ObservationIgnored private var offset: Double = 0
    /// The group was made here: made again if Jellyfin ends it.
    @ObservationIgnored private var made = false
    /// The player closed here: Jellyfin is told not to wait for this iPhone.
    @ObservationIgnored private var away = false
    /// Back in the group: Jellyfin sends its title again, and holds the others until this iPhone is ready.
    @ObservationIgnored private var rejoined = false
    @ObservationIgnored private var recovering = false
    /// Creating or joining: the socket stays open before the group is known.
    @ObservationIgnored private var entering = false
    @ObservationIgnored private var joinedCount = 0
    @ObservationIgnored private var sending: Task<Void, Never>?
    @ObservationIgnored private var failures = 0
    /// Jellyfin refused SyncPlay to this account (403): not asked again until the next sign-in.
    @ObservationIgnored private var refused = false

    private static let session: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 3600
        return URLSession(configuration: configuration)
    }()

    // MARK: What the pages use

    /// The groups open on Jellyfin, unless this iPhone is in one.
    func refresh() async {
        guard group == nil, !refused, let client = app?.client else {
            if group != nil { open = [] }
            return
        }
        do {
            open = try await client.syncGroups()
        } catch JellyfinError.forbidden {
            refused = true
            open = []
        } catch {}
    }

    func create() async throws {
        let name = SyncRules.groupName(app?.session?.userName ?? "Mira")
        made = true
        try await enter { try await $0.syncNew(name) }
    }

    func join(_ groupId: String) async throws {
        made = false
        try await enter { try await $0.syncJoin(groupId) }
    }

    /// Whose party a code is, as its Jellyfin says it (GET Mira/party/<code>, JSON).
    private struct PartyHost: Decodable, Sendable { let host: String }
    /// The party link opened last: a newer one ends the wait of the one before.
    @ObservationIgnored private var partyAsked = 0

    /// A party link opened in Mira (its page's « Open in Mira »): on this iPhone's Jellyfin, straight into its host's
    /// group, or waiting a few minutes for them to open it. Another Jellyfin's party (or one made anew since), or no
    /// account here, gives the page to open in Safari instead, where Mira web brings this iPhone in as a guest.
    func openParty(_ url: URL) async -> URL? {
        guard let link = SyncRules.partyLink(url) else {
            notice = String(localized: "Mira can’t open this link.")
            return nil
        }
        let code = link.code
        partyAsked += 1
        let asked = partyAsked
        let web = SyncRules.partyWebLink(server: link.server, code: code)
        guard let client = app?.client else { return web }
        let host: String
        do {
            host = try await client.get("Mira/party/\(code)", as: PartyHost.self).host
        } catch JellyfinError.http(404) {
            return web
        } catch {
            notice = Messages.text(error)
            return nil
        }
        if host == app?.session?.userName {
            notice = String(localized: "This is your own party link: send it to whoever wants to join you.")
            return nil
        }
        // Ten minutes at most, a look every 4 seconds, until the host opens their group.
        for attempt in 0..<150 {
            guard asked == partyAsked, app?.client != nil else { return nil }
            let groups: [SyncGroup]
            do {
                groups = try await client.syncGroups()
            } catch JellyfinError.forbidden {
                notice = String(localized: "Jellyfin doesn’t let this account watch together.")
                return nil
            } catch {
                groups = []
            }
            if let found = SyncRules.findGroup(groups, id: nil, host: host) {
                if SyncRules.sameGroup(group?.groupId, found.groupId) { return nil }
                do { try await join(found.groupId) } catch { notice = Messages.text(error) }
                return nil
            }
            if attempt == 0 { notice = String(localized: "Waiting for \(host) to open their group…") }
            try? await Task.sleep(for: .seconds(4))
        }
        if asked == partyAsked { notice = String(localized: "\(host) hasn’t opened their group yet: open the link again once they have.") }
        return nil
    }

    func leave() async {
        try? await app?.client?.syncLeave()
        gone()
    }

    /// Puts a title on for the group, from `startTicks`: every device in it opens it, this one included.
    func start(_ itemId: String, startTicks: Int64) async {
        guard group != nil, let client = app?.client else { return }
        do {
            try await client.syncQueue(itemId, startTicks: startTicks)
        } catch JellyfinError.forbidden {
            notice = String(localized: "Jellyfin doesn’t allow this account to put a title on for the group.")
        } catch {
            notice = Messages.text(error)
        }
    }

    /// Lecture of `itemId` would replace the title the group has on.
    func replaces(_ itemId: String) -> Bool { SyncRules.asksToReplace(group, queue, itemId: itemId) }

    /// Mira is back on screen: the socket opened again at once, without waiting out the delay of the failures.
    func foreground() {
        guard group != nil, !socketOpen else { return }
        connection?.cancel()
        connection = nil
        failures = 0
        connect()
    }

    /// Signed out: the group is the account's (Jellyfin drops the session from it).
    func signedOut() {
        refused = false
        open = []
        gone()
    }

    // MARK: The player's link to the group

    func bind(_ hooks: GroupPlayer) {
        player = hooks
        setAway(false)
    }

    func unbind(_ hooks: GroupPlayer) {
        guard player === hooks else { return }
        player = nil
        setAway(true)
    }

    var bound: Bool { group != nil }

    func pause() { if group != nil { send("Pause") } }

    func unpause() {
        guard group != nil else { return }
        // The group made again has no title (Jellyfin ended the old one with it): the one here is put on, from here.
        if queue == nil, let player, let itemId = player.groupItemId {
            let ticks = Int64(player.groupPosition * 10_000_000)
            Task { await start(itemId, startTicks: ticks) }
            return
        }
        send("Unpause")
    }

    func seek(_ seconds: Double) {
        if group != nil { send("Seek", ["PositionTicks": Int64(max(0, seconds) * 10_000_000)]) }
    }

    func buffering(_ seconds: Double, playing: Bool) { report("Buffering", seconds, playing) }

    func ready(_ seconds: Double, playing: Bool) { report("Ready", seconds, playing) }

    // MARK: Entering and leaving

    private func enter(_ start: (JellyfinClient) async throws -> Void) async throws {
        guard let client = app?.client else { return }
        entering = true
        defer { entering = false }
        connect()
        // Jellyfin's answer (GroupJoined) comes through the socket: open first, 6 s at most.
        for _ in 0..<60 where !socketOpen { try? await Task.sleep(for: .milliseconds(100)) }
        let before = joinedCount
        do {
            try await start(client)
        } catch {
            if group == nil { gone() }
            if case JellyfinError.forbidden = error {
                throw TogetherError.text(String(localized: "Jellyfin doesn’t allow this account to watch together (SyncPlay)."))
            }
            throw error
        }
        for _ in 0..<60 where joinedCount == before { try? await Task.sleep(for: .milliseconds(100)) }
        if group == nil {
            gone()
            throw TogetherError.text(String(localized: "Jellyfin didn’t answer. Try again in a moment."))
        }
    }

    /// Out of the group here: the socket closes.
    private func gone(_ text: String? = nil) {
        let was = group
        group = nil
        queue = nil
        offset = 0
        away = false
        made = false
        rejoined = false
        clock?.cancel()
        clock = nil
        connection?.cancel()
        connection = nil
        beat?.cancel()
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        socketOpen = false
        if was != nil { player?.groupLeft() }
        if let text { notice = text }
    }

    /// Back in the group after Jellyfin let this iPhone go: joined again where it is, or made again under its name.
    /// Out of it when there is none to go back to, or when Jellyfin twice says nothing; still in it while Jellyfin cannot
    /// be reached (the socket's next opening tries again).
    private func recover() {
        guard let lost = group, !recovering, let client = app?.client else { return }
        recovering = true
        Task {
            defer { recovering = false }
            for _ in 0..<2 {
                guard let groups = try? await client.syncGroups() else { return }
                guard SyncRules.sameGroup(group?.groupId, lost.groupId) else { return }
                guard let plan = SyncRules.rejoinPlan(groups, lost: lost, made: made) else {
                    gone(String(localized: "The group no longer exists."))
                    return
                }
                let before = joinedCount
                do {
                    switch plan {
                    case let .join(id): try await client.syncJoin(id)
                    case let .create(name): try await client.syncNew(name)
                    }
                } catch { return }
                for _ in 0..<60 where joinedCount == before { try? await Task.sleep(for: .milliseconds(100)) }
                if joinedCount != before { return }
                guard SyncRules.sameGroup(group?.groupId, lost.groupId) else { return }
            }
            gone(String(localized: "The group no longer exists."))
        }
    }

    // MARK: The socket

    private func connect() {
        guard connection == nil, let client = app?.client else { return }
        connection = Task { [weak self] in
            await client.capabilities()
            while !Task.isCancelled {
                guard let self, self.group != nil || self.entering else { break }
                // Asked each time: the way to Jellyfin may have changed since the last opening (out of the home's Wi-Fi).
                guard let request = await client.socketRequest() else { break }
                let task = Self.session.webSocketTask(with: request)
                self.socket = task
                task.resume()
                do {
                    try await task.send(.string(#"{"MessageType":"KeepAlive"}"#))
                    self.failures = 0
                    self.socketOpen = true
                    // Opened again after a break: Jellyfin may have let the session go, with its place in the group.
                    if self.group != nil { self.recover() }
                    while !Task.isCancelled {
                        switch try await task.receive() {
                        case let .string(text): self.received(text)
                        case let .data(data): self.received(String(decoding: data, as: UTF8.self))
                        @unknown default: break
                        }
                    }
                } catch {}
                task.cancel(with: .goingAway, reason: nil)
                if self.socket === task {
                    self.socket = nil
                    self.socketOpen = false
                    self.beat?.cancel()
                }
                guard !Task.isCancelled, self.group != nil || self.entering else { break }
                let delay = min(30, pow(2, Double(self.failures)))
                self.failures += 1
                try? await Task.sleep(for: .seconds(delay))
            }
            if let self, !Task.isCancelled { self.connection = nil }
        }
    }

    private func received(_ text: String) {
        guard let message = try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any] else { return }
        switch message["MessageType"] as? String {
        case "ForceKeepAlive":
            // Jellyfin closes a socket it has not heard from: it says how often (in seconds), Mira answers twice as often.
            let every = max(5, ((message["Data"] as? NSNumber)?.doubleValue ?? 60) / 2)
            beat?.cancel()
            beat = Task { [weak self] in
                while !Task.isCancelled {
                    try? await self?.socket?.send(.string(#"{"MessageType":"KeepAlive"}"#))
                    try? await Task.sleep(for: .seconds(every))
                }
            }
        case "SyncPlayGroupUpdate":
            if let data = message["Data"] as? [String: Any] { groupUpdate(data) }
        case "SyncPlayCommand":
            if let data = message["Data"] as? [String: Any] { command(data) }
        default:
            break
        }
    }

    private func groupUpdate(_ update: [String: Any]) {
        let data = update["Data"]
        switch update["Type"] as? String {
        case "GroupJoined":
            guard let fields = data as? [String: Any], let json = try? JSONSerialization.data(withJSONObject: fields),
                  let joined = try? JSON.decoder.decode(SyncGroup.self, from: json) else { return }
            // The same group again (back after a break): its title stays until Jellyfin sends it again.
            let again = group.map { SyncRules.sameGroup($0.groupId, joined.groupId) } ?? false
            group = joined
            if !again { queue = nil }
            rejoined = again && queue != nil
            away = false
            open = []
            joinedCount += 1
            clock?.cancel()
            clock = Task { [weak self] in
                while !Task.isCancelled {
                    await self?.syncClock()
                    try? await Task.sleep(for: .seconds(60))
                }
            }
        case "UserJoined", "UserLeft":
            guard group != nil, let name = data as? String else { return }
            let said = SyncRules.memberText(name, joined: update["Type"] as? String == "UserJoined")
            member = (said, Date())
            if player == nil { notice = said }
            // Jellyfin names each account once, whatever its devices: its own list, rather than a count kept here.
            Task {
                guard let groups = try? await app?.client?.syncGroups(), let current = group,
                      let info = groups.first(where: { SyncRules.sameGroup($0.groupId, current.groupId) }) else { return }
                group?.participants = info.participants
            }
        case "GroupLeft":
            gone()
        case "NotInGroup", "GroupDoesNotExist":
            if group != nil { recover() } else { gone() }
        case "StateUpdate":
            if group != nil, let state = (data as? [String: Any])?["State"] as? String { group?.state = state }
        case "LibraryAccessDenied":
            notice = String(localized: "Someone in the group can’t access this title on Jellyfin.")
        case "PlayQueue":
            playQueue(data as? [String: Any] ?? [:])
        default:
            break
        }
    }

    private func playQueue(_ data: [String: Any]) {
        guard let next = SyncRules.queueItem(data) else {
            queue = nil
            return
        }
        let same = queue?.playlistItemId == next.playlistItemId
        // The list changed around the same title: nothing to load again, unless this iPhone is back in the group.
        if same && !rejoined { return }
        rejoined = false
        queue = next
        if same && player == nil {
            setAway(true)
            return
        }
        setAway(false)
        // Loading: Jellyfin holds the others until this iPhone says it is ready.
        report("Buffering", Double(next.startTicks) / 10_000_000, false)
        if let player, player.groupItemId == next.itemId {
            player.groupPrepare(next.startTicks)
        } else if let player {
            player.groupSwitch(to: next.itemId, startTicks: next.startTicks)
        } else {
            app?.playForGroup(next.itemId, startTicks: next.startTicks)
        }
    }

    private func command(_ data: [String: Any]) {
        // The Stop Jellyfin sends on joining names no title; a command for another title is not for this one.
        guard let player, let queue, data["PlaylistItemId"] as? String == queue.playlistItemId else { return }
        let ticks = (data["PositionTicks"] as? NSNumber)?.int64Value
        let now = Date().timeIntervalSince1970 * 1000
        guard let plan = SyncRules.commandPlan(command: data["Command"] as? String, when: data["When"] as? String, positionTicks: ticks, offset: offset, now: now) else { return }
        player.groupCommand(plan)
    }

    private func syncClock() async {
        guard let client = app?.client else { return }
        var samples: [SyncRules.ClockSample] = []
        for _ in 0..<3 {
            let sent = Date().timeIntervalSince1970 * 1000
            guard let answer = try? await client.utcTime() else { break }
            let received = Date().timeIntervalSince1970 * 1000
            guard let serverIn = SyncRules.serverTime(answer.received), let serverOut = SyncRules.serverTime(answer.sent) else { break }
            samples.append(.init(sent: sent, received: received, serverIn: serverIn, serverOut: serverOut))
        }
        guard let best = SyncRules.clockOffset(samples), group != nil else { return }
        offset = best.offset
        // Jellyfin waits for the slowest device of the group before it says when to play.
        send("Ping", ["Ping": max(1, Int(best.trip))])
    }

    /// Out of the player, the group plays on: without this, Jellyfin would hold every seek until this iPhone is ready.
    private func setAway(_ value: Bool) {
        guard group != nil, away != value else { return }
        away = value
        send("SetIgnoreWait", ["IgnoreWait": value])
    }

    private func report(_ what: String, _ seconds: Double, _ playing: Bool) {
        guard group != nil, let queue else { return }
        let when = SyncRules.serverDate(Date().timeIntervalSince1970 * 1000 + offset)
        send(what, ["When": when, "PositionTicks": Int64(max(0, seconds) * 10_000_000), "IsPlaying": playing, "PlaylistItemId": queue.playlistItemId])
    }

    /// What this iPhone tells the group, one request at a time, in order: two at once may reach Jellyfin crossed, and a
    /// Buffering taken after the Ready that follows it holds the whole group for this iPhone. A failure lets the next go.
    private func send(_ what: String, _ body: [String: Any] = [:]) {
        guard let client = app?.client, let json = try? JSONSerialization.data(withJSONObject: body) else { return }
        let previous = sending
        sending = Task {
            await previous?.value
            _ = try? await client.syncSend(what, json: json)
        }
    }
}

enum TogetherError: LocalizedError {
    case text(String)

    var errorDescription: String? {
        switch self {
        case let .text(text): text
        }
    }
}
