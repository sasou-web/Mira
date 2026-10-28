import Foundation

/// One file of a title as Jellyfin describes it (MediaSourceInfo): in Items/{id} for its tracks, in PlaybackInfo's
/// answer with what Jellyfin decided for this iPhone (the file as it is, or its conversion).
struct MediaSource: Codable, Sendable, Hashable {
    var id: String?
    var container: String?
    var bitrate: Int?
    var runTimeTicks: Int64?
    var eTag: String?
    var supportsDirectPlay: Bool?
    var supportsDirectStream: Bool?
    var transcodingUrl: String?
    var transcodingSubProtocol: String?
    var defaultAudioStreamIndex: Int?
    var defaultSubtitleStreamIndex: Int?
    var mediaStreams: [MediaStream]?

    var audio: [MediaStream] { (mediaStreams ?? []).filter { $0.type == "Audio" } }
    var subtitles: [MediaStream] { (mediaStreams ?? []).filter { $0.type == "Subtitle" } }
    /// The text subtitles Jellyfin can send as text, in its order: the order of the HLS stream's subtitles.
    var textSubtitles: [MediaStream] { subtitles.filter { $0.isTextSubtitleStream == true } }
}

/// A track of a file: video, audio or subtitles.
struct MediaStream: Codable, Sendable, Hashable {
    var index: Int
    var type: String?
    var codec: String?
    var language: String?
    var title: String?
    var displayTitle: String?
    var isDefault: Bool?
    var isForced: Bool?
    var isExternal: Bool?
    var isTextSubtitleStream: Bool?
    var channels: Int?
    var width: Int?
    var height: Int?
}

/// Items/{id}/PlaybackInfo's answer.
struct PlaybackInfo: Codable, Sendable {
    var mediaSources: [MediaSource]?
    var playSessionId: String?
    var errorCode: String?
}

extension JellyfinClient {
    /// What Jellyfin sends this iPhone for a title, decided from the device profile (PlaybackProfile): the file as it
    /// is, or a conversion with what must be converted.
    func playbackInfo(_ itemId: String, request: PlaybackRequest) async throws -> PlaybackInfo {
        let body = try JSONSerialization.data(withJSONObject: request.body(userId: userId))
        let data = try await send("POST", "Items/\(itemId)/PlaybackInfo", ["userId": userId], body: body)
        do { return try JSON.decoder.decode(PlaybackInfo.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }

    enum Report: String, Sendable {
        case start = "Sessions/Playing", progress = "Sessions/Playing/Progress", stop = "Sessions/Playing/Stopped"
    }

    /// Where this iPhone is in a title, for Jellyfin's resume points and its dashboard; never throws.
    func report(_ kind: Report, _ state: PlaybackState) async {
        guard let body = try? JSONSerialization.data(withJSONObject: state.body) else { return }
        _ = try? await send("POST", kind.rawValue, body: body, timeout: 10)
    }

    /// Ends Jellyfin's conversion for a session that no longer plays it (the PC stops converting at once).
    func stopEncoding(_ playSessionId: String) async {
        _ = try? await send("DELETE", "Videos/ActiveEncodings", ["deviceId": device.id, "playSessionId": playSessionId], timeout: 5)
    }

    /// The episode after this one in its series, in Jellyfin's order (specials where Jellyfin puts them); nil after the
    /// last one.
    func episode(after episode: Item) async throws -> Item? {
        guard let series = episode.seriesId else { return nil }
        let list: ItemList = try await get("Shows/\(series)/Episodes", [
            "userId": userId, "startItemId": episode.id, "limit": "2", "fields": "MediaSources", "enableUserData": "true",
        ])
        guard let items = list.items, items.first?.id == episode.id, items.count > 1 else { return nil }
        return items[1]
    }

    /// The episode Jellyfin would play next in a series: the one started, else the first not watched.
    func nextUp(series: String) async throws -> Item? {
        let list: ItemList = try await get("Shows/NextUp", [
            "userId": userId, "seriesId": series, "limit": "1", "enableResumable": "true", "enableRewatching": "false", "fields": "MediaSources",
        ])
        return list.items?.first
    }

    /// An address the player reads without an Authorization header, carrying the session as Mira web's do (ApiKey):
    /// the only way through AirPlay, where the television fetches the stream itself.
    func signed(_ path: String) -> URL? {
        var text = String(server.absoluteString.reversed().drop { $0 == "/" }.reversed()) + "/" + path.drop { $0 == "/" }
        if let token, !text.localizedCaseInsensitiveContains("ApiKey="), !text.localizedCaseInsensitiveContains("api_key=") {
            let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
            text += (text.contains("?") ? "&" : "?") + "ApiKey=" + (token.addingPercentEncoding(withAllowedCharacters: allowed) ?? token)
        }
        return URL(string: text)
    }

    /// Jellyfin's Mira extension answers here (it serves Mira web): its HLS playlists then time the subtitles of a
    /// conversion right for Apple's player.
    func hasMiraExtension() async -> Bool {
        var request = URLRequest(url: server.appending(path: "Mira/"), timeoutInterval: 6)
        request.httpMethod = "HEAD"
        guard let answer = try? await Self.session.data(for: request) else { return false }
        return (answer.1 as? HTTPURLResponse)?.statusCode == 200
    }

    /// How fast Jellyfin reaches this iPhone, in bits per second: 2 MB of Playback/BitrateTest, 5 s at most.
    func measureSpeed() async -> Double? {
        let size = 2_000_000
        var request = URLRequest(url: url("Playback/BitrateTest", ["size": String(size)]), cachePolicy: .reloadIgnoringLocalAndRemoteCacheData, timeoutInterval: 5)
        request.setValue(authorization, forHTTPHeaderField: "Authorization")
        let started = Date()
        guard let answer = try? await Self.session.data(for: request), (answer.1 as? HTTPURLResponse)?.statusCode == 200, answer.0.count > 100_000 else { return nil }
        return Double(answer.0.count * 8) / max(0.05, Date().timeIntervalSince(started))
    }
}

/// What PlaybackInfo is asked: the device profile, where to start, which tracks, and how much may be sent as it is.
struct PlaybackRequest: Sendable {
    var bitrate: Int
    var stage: Int
    var startTicks: Int64
    var audioIndex: Int?
    var subtitleIndex: Int?
    var mediaSourceId: String?
    var device: PlaybackProfile.Device

    func body(userId: String?) -> [String: Any] {
        let stage = PlaybackPlan.stageRequest(stage)
        var body: [String: Any] = [
            "DeviceProfile": stage.lastResort ? PlaybackProfile.lastResort(bitrate: bitrate) : PlaybackProfile.make(bitrate: bitrate, device: device),
            "MaxStreamingBitrate": bitrate, "StartTimeTicks": startTicks,
            "EnableDirectPlay": stage.directPlay, "EnableDirectStream": stage.directStream, "EnableTranscoding": true,
            "AllowVideoStreamCopy": stage.videoCopy, "AllowAudioStreamCopy": stage.audioCopy, "AutoOpenLiveStream": true,
        ]
        if let userId { body["UserId"] = userId }
        if let audioIndex { body["AudioStreamIndex"] = audioIndex }
        if let subtitleIndex { body["SubtitleStreamIndex"] = subtitleIndex }
        if let mediaSourceId { body["MediaSourceId"] = mediaSourceId }
        return body
    }
}

/// Where the player is, as Jellyfin's PlaybackProgressInfo.
struct PlaybackState: Sendable {
    var itemId: String
    var mediaSourceId: String?
    var playSessionId: String?
    var positionTicks: Int64
    var isPaused: Bool
    var playMethod: String
    var audioIndex: Int?
    var subtitleIndex: Int?

    var body: [String: Any] {
        var body: [String: Any] = [
            "ItemId": itemId, "PositionTicks": positionTicks, "IsPaused": isPaused, "PlayMethod": playMethod, "CanSeek": true,
            "SubtitleStreamIndex": subtitleIndex ?? -1,
        ]
        if let mediaSourceId { body["MediaSourceId"] = mediaSourceId }
        if let playSessionId { body["PlaySessionId"] = playSessionId }
        if let audioIndex { body["AudioStreamIndex"] = audioIndex }
        return body
    }
}
