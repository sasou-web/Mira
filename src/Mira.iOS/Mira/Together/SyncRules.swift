import Foundation

/// A group of « Watch together » as Jellyfin lists it (SyncPlay/List, GroupJoined), read by JSON.decoder.
struct SyncGroup: Codable, Sendable, Hashable, Identifiable {
    var groupId: String
    var groupName: String?
    var state: String?
    var participants: [String]?

    var id: String { groupId }
}

/// The group's title: what a PlayQueue update puts on, and where it starts.
struct SyncQueue: Sendable, Hashable {
    var itemId: String
    var playlistItemId: String
    var startTicks: Int64
}

/// What a group command asks of the player, on this iPhone's clock: play in `wait` seconds from `position` (a command
/// that arrives late starts further on, where the others are), or pause, seek, stop at `position`.
struct SyncPlan: Sendable, Equatable {
    enum Action: String, Sendable { case play, pause, seek, stop }
    var action: Action
    var wait: TimeInterval
    var position: Double
}

/// « Watch together » (Jellyfin's SyncPlay), the part that needs no network: the server's clock, where and when a
/// command of the group puts the player, the group's words. The same rules as Mira web (player/sync.js) and Windows and
/// Mac (Together.cs); tests/fixtures/syncplay.json holds the cases.
enum SyncRules {
    /// Jellyfin's dates carry seven decimals: milliseconds since 1970, nil for anything else.
    static func serverTime(_ text: String?) -> Double? {
        guard var trimmed = text else { return nil }
        // Three decimals exactly, which the formatter reads: more cut, fewer filled.
        if let dot = trimmed.firstIndex(of: "."), let end = trimmed[dot...].firstIndex(where: { !$0.isNumber && $0 != "." }) {
            let digits = String(trimmed[trimmed.index(after: dot)..<end])
            trimmed.replaceSubrange(dot..<end, with: "." + String((digits + "000").prefix(3)))
        }
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        guard let date = fractional.date(from: trimmed) ?? plain.date(from: trimmed) else { return nil }
        return (date.timeIntervalSince1970 * 1000).rounded()
    }

    /// The date Jellyfin reads, from milliseconds since 1970.
    static func serverDate(_ milliseconds: Double) -> String {
        let format = ISO8601DateFormatter()
        format.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return format.string(from: Date(timeIntervalSince1970: milliseconds / 1000))
    }

    struct ClockSample: Sendable {
        var sent: Double
        var received: Double
        var serverIn: Double
        var serverOut: Double
    }

    /// Jellyfin's clock minus this iPhone's, in milliseconds, from GetUtcTime answers: the one with the shortest round
    /// trip, the least delayed. Nil without one.
    static func clockOffset(_ samples: [ClockSample]) -> (offset: Double, trip: Double)? {
        var best: (offset: Double, trip: Double)?
        for sample in samples {
            let trip = (sample.received - sample.sent) - (sample.serverOut - sample.serverIn)
            guard trip.isFinite, trip >= 0 else { continue }
            if best == nil || trip < best!.trip {
                best = ((((sample.serverIn - sample.sent) + (sample.serverOut - sample.received)) / 2).rounded(), trip)
            }
        }
        return best
    }

    /// What a SyncPlayCommand asks of this player. `offset` and `now` in milliseconds.
    static func commandPlan(command: String?, when: String?, positionTicks: Int64?, offset: Double, now: Double) -> SyncPlan? {
        let position = Double(positionTicks ?? 0) / 10_000_000
        switch command {
        case "Unpause":
            guard let at = serverTime(when) else { return SyncPlan(action: .play, wait: 0, position: position) }
            let wait = (at - offset - now).rounded()
            return SyncPlan(action: .play, wait: max(0, wait) / 1000, position: wait < 0 ? position - wait / 1000 : position)
        case "Pause": return SyncPlan(action: .pause, wait: 0, position: position)
        case "Seek": return SyncPlan(action: .seek, wait: 0, position: position)
        case "Stop": return SyncPlan(action: .stop, wait: 0, position: position)
        default: return nil
        }
    }

    /// Under half a second apart, a jump would be seen more than the gap.
    static func needsSeek(_ current: Double, _ target: Double) -> Bool { abs(current - target) >= 0.5 }

    /// The title a PlayQueue update puts on; nil when it plays nothing.
    static func queueItem(_ data: [String: Any]) -> SyncQueue? {
        guard let list = data["Playlist"] as? [[String: Any]], let index = data["PlayingItemIndex"] as? Int, list.indices.contains(index),
              let itemId = list[index]["ItemId"] as? String, !itemId.isEmpty else { return nil }
        let start = (data["StartPositionTicks"] as? NSNumber)?.int64Value ?? 0
        return SyncQueue(itemId: itemId, playlistItemId: list[index]["PlaylistItemId"] as? String ?? "", startTicks: start)
    }

    /// The name a new group takes, in the language shown: « Groupe de mira », « mira’s group », as every Mira names it.
    static func groupName(_ user: String) -> String { String(localized: "\(user)’s group", comment: "A group of Watch together: Groupe de mira") }

    /// Who made a group of Mira, from its name in either language; nil for another name.
    static func groupHost(_ name: String?) -> String? {
        guard let name else { return nil }
        if name.hasPrefix("Groupe de ") {
            let host = String(name.dropFirst("Groupe de ".count))
            return host.isEmpty ? nil : host
        }
        for suffix in ["’s group", "'s group"] where name.hasSuffix(suffix) {
            let host = String(name.dropLast(suffix.count))
            return host.isEmpty ? nil : host
        }
        return nil
    }

    /// A group's name in the language of the app: « Groupe de mira » is « mira’s group » in English, and back.
    static func groupLabel(_ name: String?) -> String {
        groupHost(name).map(groupName) ?? name ?? ""
    }

    /// A party link (mira://party/<code>?server=…, its page's « Open in Mira »): the code (22 characters of base64url)
    /// and its Jellyfin at an http(s) address, nothing else; nil for anything else. As Together.ParseAppLink of Core.
    static func partyLink(_ url: URL) -> (code: String, server: URL)? {
        guard url.scheme == "mira", url.host()?.lowercased() == "party" else { return nil }
        let code = url.path().trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard code.count == 22, code.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }) else { return nil }
        guard let value = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "server" })?.value,
              let server = URL(string: value), ["http", "https"].contains(server.scheme ?? ""), server.host() != nil,
              server.user() == nil, server.query() == nil, server.fragment() == nil else { return nil }
        return (code, server)
    }

    /// The party in Mira web, as a guest joins it without an account (#/party/<code>).
    static func partyWebLink(server: URL, code: String) -> URL? {
        var base = server.absoluteString
        while base.hasSuffix("/") { base.removeLast() }
        return URL(string: base + "/Mira/#/party/" + code)
    }

    /// Jellyfin writes a group's id with or without dashes.
    static func sameGroup(_ a: String?, _ b: String?) -> Bool {
        (a ?? "").replacingOccurrences(of: "-", with: "").lowercased() == (b ?? "").replacingOccurrences(of: "-", with: "").lowercased()
    }

    /// The group an invitation means: the one of its id, else the one its host made again since under the same name,
    /// with the host in it first, then the fullest.
    static func findGroup(_ groups: [SyncGroup], id: String?, host: String?) -> SyncGroup? {
        if let same = groups.first(where: { sameGroup($0.groupId, id) }) { return same }
        guard let host else { return nil }
        func there(_ group: SyncGroup) -> Bool { group.participants?.contains(host) == true }
        return groups.filter { groupHost($0.groupName) == host }
            .sorted { there($0) != there($1) ? there($0) : ($0.participants?.count ?? 0) > ($1.participants?.count ?? 0) }
            .first
    }

    enum Rejoin: Equatable, Sendable {
        case join(String)
        case create(String)
    }

    /// Back in a group Jellyfin let this iPhone go from: joined where it still is, made again under its name when this
    /// iPhone made it and it ended (empty), so the invitations sent still find it; nil when there is nothing to go back to.
    static func rejoinPlan(_ groups: [SyncGroup], lost: SyncGroup, made: Bool) -> Rejoin? {
        if let found = findGroup(groups, id: lost.groupId, host: groupHost(lost.groupName)) { return .join(found.groupId) }
        if made, let name = lost.groupName, !name.isEmpty { return .create(name) }
        return nil
    }

    /// « 2 people ».
    static func people(_ count: Int) -> String { String(localized: "\(max(1, count)) people", comment: "In a group: 1 personne, 2 personnes") }

    /// « mira’s group · 2 people ».
    static func groupText(_ group: SyncGroup) -> String {
        "\(group.groupName == nil ? String(localized: "Group") : groupLabel(group.groupName)) · \(people(group.participants?.count ?? 1))"
    }

    /// Who is in a group: « mira and Marc », « mira, Marc and Léa », « mira, Marc and 3 others ».
    static func namesText(_ names: [String]) -> String {
        if names.count <= 1 { return names.joined() }
        if names.count > 3 { return String(localized: "\(names.prefix(2).joined(separator: ", ")) and \(names.count - 2) others") }
        return String(localized: "\(names.dropLast().joined(separator: ", ")) and \(names.last!)", comment: "The last of the names in a group")
    }

    /// What the group is doing, in a line, and whether it holds the picture (`alert`). Jellyfin says only its state,
    /// never who caused it: a wait while this iPhone is ready is for the others, named when there is one.
    static func groupStatus(_ group: SyncGroup, me: String?, loading: Bool = false) -> (text: String, alert: Bool) {
        let mine = (me ?? "").lowercased()
        let everyone = group.participants ?? []
        let others = everyone.filter { $0.lowercased() != mine }
        switch group.state {
        case "Waiting":
            if loading || others.isEmpty { return (String(localized: "Loading for the group…"), true) }
            return (others.count == 1 ? String(localized: "Waiting for \(others[0])…") : String(localized: "Waiting for someone in the group…"), true)
        case "Paused":
            return (String(localized: "Paused for the group"), true)
        default:
            return (others.isEmpty ? String(localized: "Together · nobody else yet") : String(localized: "Together · \(namesText(everyone))"), false)
        }
    }

    /// « Marc joined the group », « Marc left the group ».
    static func memberText(_ name: String, joined: Bool) -> String {
        joined ? String(localized: "\(name) joined the group") : String(localized: "\(name) left the group")
    }

    /// Lecture of `itemId` while the group has another title on, not over: asked first, rather than changed for everyone.
    static func asksToReplace(_ group: SyncGroup?, _ queue: SyncQueue?, itemId: String) -> Bool {
        guard let group, let queue, !queue.itemId.isEmpty else { return false }
        return queue.itemId != itemId && group.state != "Idle"
    }

    /// A title as a question names it: a film by its name, an episode with its series (« Dark · S1 · E3 »).
    static func titleName(_ item: Item) -> String {
        guard item.isEpisode, let series = item.seriesName else { return item.name ?? "" }
        let code = ItemText.episodeCode(item)
        return code.isEmpty ? series : "\(series) · \(code)"
    }

    /// « Replace “Dune” for the whole group? », or without its name when it is not known.
    static func replaceQuestion(_ current: String?) -> String {
        guard let current, !current.isEmpty else { return String(localized: "Replace the group’s title?") }
        return String(localized: "Replace “\(current)” for the whole group?")
    }

    /// « 42 min », « 1 h 05 », « 30 s ».
    static func atText(_ seconds: Double) -> String {
        let s = max(0, Int(seconds.rounded(.down))), m = s / 60
        if m < 1 { return String(localized: "\(s) s", comment: "Seconds: 30 s") }
        return SleepTimer.duration(m)
    }

    /// Lecture's label in a group, which starts the title for everyone.
    static func groupPlayLabel(code: String = "", resumeAt: Double = 0, groupTitle: Bool = false) -> String {
        if groupTitle { return String(localized: "Watch with the group") }
        if !code.isEmpty { return resumeAt > 0 ? String(localized: "Resume \(code) for the group") : String(localized: "Start \(code) for the group") }
        return resumeAt > 0 ? String(localized: "Resume for the group at \(atText(resumeAt))") : String(localized: "Start for the group")
    }

    /// The link that opens Mira web on the group: the friend signs in with an account of this Jellyfin, then is in it.
    static func inviteLink(server: URL, groupId: String, host: String?) -> String {
        var base = server.absoluteString
        while base.hasSuffix("/") { base.removeLast() }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_.!~*'()"))
        let id = groupId.addingPercentEncoding(withAllowedCharacters: allowed) ?? groupId
        let hostPart = host.flatMap { $0.addingPercentEncoding(withAllowedCharacters: allowed) }.map { "?hote=\($0)" } ?? ""
        return "\(base)/Mira/#/ensemble/\(id)\(hostPart)"
    }
}
