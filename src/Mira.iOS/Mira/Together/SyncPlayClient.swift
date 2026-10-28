import Foundation

/// Jellyfin's SyncPlay routes, as Mira web asks them (api.js).
extension JellyfinClient {
    func syncGroups() async throws -> [SyncGroup] {
        try await get("SyncPlay/List", as: [SyncGroup].self)
    }

    func syncNew(_ name: String) async throws {
        try await post("SyncPlay/New", ["GroupName": name])
    }

    func syncJoin(_ groupId: String) async throws {
        try await post("SyncPlay/Join", ["GroupId": groupId])
    }

    func syncLeave() async throws {
        try await send("POST", "SyncPlay/Leave", timeout: 6)
    }

    /// Puts a title on for the group, from `startTicks`.
    func syncQueue(_ itemId: String, startTicks: Int64) async throws {
        try await post("SyncPlay/SetNewQueue", ["PlayingQueue": [itemId], "PlayingItemPosition": 0, "StartPositionTicks": max(0, startTicks)])
    }

    /// Pause, Unpause, Seek, Buffering, Ready, Ping, SetIgnoreWait, with their body already in JSON.
    func syncSend(_ what: String, json: Data) async throws {
        try await send("POST", "SyncPlay/\(what)", body: json, timeout: 10)
    }

    /// Jellyfin's clock: when the request arrived, when the answer left.
    func utcTime() async throws -> (received: String?, sent: String?) {
        let data = try await send("GET", "GetUtcTime", timeout: 5)
        let answer = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        return (answer?["RequestReceptionTime"] as? String, answer?["ResponseTransmissionTime"] as? String)
    }

    /// Tells Jellyfin this session plays video and takes remote commands: its WebSocket then carries SyncPlay's.
    func capabilities() async {
        _ = try? await send("POST", "Sessions/Capabilities", ["playableMediaTypes": "Video", "supportsMediaControl": "true"], timeout: 6)
    }

    /// Jellyfin's WebSocket for this session, its token in the header rather than the address.
    func socketRequest() -> URLRequest? {
        guard var parts = URLComponents(url: server.appending(path: "socket"), resolvingAgainstBaseURL: false) else { return nil }
        parts.scheme = parts.scheme == "https" ? "wss" : "ws"
        parts.queryItems = [URLQueryItem(name: "deviceId", value: device.id)]
        guard let address = parts.url else { return nil }
        var request = URLRequest(url: address)
        request.setValue(authorization, forHTTPHeaderField: "Authorization")
        return request
    }

    private func post(_ path: String, _ body: [String: Any]) async throws {
        let data = try JSONSerialization.data(withJSONObject: body)
        try await send("POST", path, body: data, timeout: 10)
    }
}
