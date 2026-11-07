import Foundation

/// « Play on… » and the small remote: the devices this iPhone can play on or steer through Jellyfin's remote control
/// (another Mira on a PC or a Mac, a Jellyfin app on a TV). The rules of Mira.Core/RemoteControl.cs (Targets), with
/// their shared cases in tests/fixtures/remote.json. Nothing new leaves the iPhone: Jellyfin already holds every session,
/// and the commands go to it as « Stop » of « Now on Jellyfin » already did.
enum RemoteRules {
    /// A session as Sessions?controllableByUserId=… lists it; everything optional, as a session Jellyfin half describes
    /// is left out rather than the whole list refused.
    struct Session: Decodable, Sendable {
        struct PlayState: Decodable, Sendable {
            var positionTicks: Int64?
            var isPaused: Bool?
            var volumeLevel: Int?
            var isMuted: Bool?
        }
        struct Playing: Decodable, Sendable {
            var id: String?
            var name: String?
            var type: String?
            var seriesName: String?
            var parentIndexNumber: Int?
            var indexNumber: Int?
            var runTimeTicks: Int64?
        }
        var id: String?
        var userName: String?
        var client: String?
        var deviceName: String?
        var deviceId: String?
        var supportsRemoteControl: Bool?
        var playableMediaTypes: [String]?
        var playState: PlayState?
        var nowPlayingItem: Playing?
    }

    /// A device to play on. `playing` is the title it plays now (an episode's own name, its series in `series`), nil
    /// when idle; position and duration in seconds; volume 0 to 100.
    struct Target: Identifiable, Sendable, Hashable {
        var id: String
        var deviceId: String
        var device: String
        var client: String
        var user: String
        var playing: String?
        var playingId: String?
        var series: String?
        var season: Int?
        var episode: Int?
        var paused: Bool
        var position: Double
        var duration: Double
        var volume: Int
        var muted: Bool
    }

    /// The devices taking the remote control and videos, never this iPhone, one per device (Jellyfin may keep an older
    /// session of it), in Jellyfin's order (the most recently active first).
    static func targets(_ sessions: [Session], ownDevice: String) -> [Target] {
        var devices = Set<String>()
        var found: [Target] = []
        for session in sessions {
            guard let id = nonEmpty(session.id), session.supportsRemoteControl == true else { continue }
            let device = nonEmpty(session.deviceId) ?? id
            guard device != ownDevice, !devices.contains(device), session.playableMediaTypes?.contains("Video") == true else { continue }
            devices.insert(device)
            let item = session.nowPlayingItem
            let state = session.playState
            let playing = item != nil
            found.append(Target(
                id: id, deviceId: device, device: nonEmpty(session.deviceName) ?? "", client: nonEmpty(session.client) ?? "", user: nonEmpty(session.userName) ?? "",
                playing: playing ? item?.name ?? "" : nil, playingId: plainId(item?.id), series: nonEmpty(item?.seriesName),
                season: item?.parentIndexNumber, episode: item?.indexNumber, paused: playing && state?.isPaused == true,
                position: seconds(state?.positionTicks), duration: seconds(item?.runTimeTicks),
                volume: min(100, max(0, state?.volumeLevel ?? 100)), muted: state?.isMuted == true))
        }
        return found
    }

    /// « Arcane · S1 E1 · Welcome to the Playground », « Spider-Man: Across the Spider-Verse »; nil when idle.
    static func title(_ target: Target) -> String? {
        guard let name = target.playing else { return nil }
        guard let series = target.series else { return name }
        let code: String? = target.episode.map { episode in
            target.season.map { String(localized: "S\($0) E\(episode)") } ?? String(localized: "E\(episode)")
        }
        return [series, code, name].compactMap { $0?.isEmpty == false ? $0 : nil }.joined(separator: " · ")
    }

    /// « MacBook Air · Mira », « Salon · Jellyfin Android TV ».
    static func name(_ target: Target) -> String {
        [target.device, target.client].filter { !$0.isEmpty }.joined(separator: " · ")
    }

    /// Where to start a title elsewhere, in Jellyfin's ticks: where this account left it, 0 from the start (another Mira
    /// then starts it over rather than where it was).
    static func startTicks(_ item: Item, fromStart: Bool) -> Int64 {
        guard !fromStart, item.userData?.played != true, let done = item.userData?.playbackPositionTicks, done > 0 else { return 0 }
        if let total = item.runTimeTicks, total > 0, done >= total { return 0 }
        return done
    }

    /// An id as Jellyfin writes it in a session: 32 hexadecimal digits, lowercase, without dashes.
    static func plainId(_ id: String?) -> String? {
        guard let id else { return nil }
        let plain = id.replacingOccurrences(of: "-", with: "").lowercased()
        return !plain.isEmpty && plain.allSatisfy(\.isHexDigit) ? plain : nil
    }

    private static func seconds(_ ticks: Int64?) -> Double { max(0, Double(ticks ?? 0) / 1e7) }
    private static func nonEmpty(_ text: String?) -> String? { text?.isEmpty == false ? text : nil }
}

extension JellyfinClient {
    /// The devices this account can play on or steer (RemoteRules).
    func remoteTargets() async throws -> [RemoteRules.Target] {
        let sessions: [RemoteRules.Session] = try await get("Sessions", ["controllableByUserId": userId, "activeWithinSeconds": "960"])
        return RemoteRules.targets(sessions, ownDevice: device.id)
    }

    /// « Play on… »: a title on another device, from `startTicks`.
    func remotePlay(_ session: String, item: String, startTicks: Int64) async throws {
        try await send("POST", "Sessions/\(session)/Playing", ["playCommand": "PlayNow", "itemIds": item, "startPositionTicks": String(max(0, startTicks))])
    }

    /// Pause, Unpause, PlayPause, Stop, NextTrack, PreviousTrack, Rewind, FastForward, or Seek with `seekTicks`.
    func remoteState(_ session: String, _ command: String, seekTicks: Int64? = nil) async throws {
        try await send("POST", "Sessions/\(session)/Playing/\(command)", ["seekPositionTicks": seekTicks.map { String(max(0, $0)) }])
    }

    /// A general command: SetVolume with Volume, Mute, Unmute…
    func remoteCommand(_ session: String, _ name: String, _ arguments: [String: String] = [:]) async throws {
        let body = try JSONSerialization.data(withJSONObject: ["Name": name, "Arguments": arguments])
        try await send("POST", "Sessions/\(session)/Command", body: body)
    }
}
