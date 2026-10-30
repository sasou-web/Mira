import Foundation

/// How this iPhone introduces itself to Jellyfin (the dashboard shows it): « Mira » on « iPhone », one device id kept
/// for the installation.
struct DeviceIdentity: Sendable, Hashable {
    let id: String
    let name: String
    let version: String
}

enum JellyfinError: Error, Sendable, Equatable {
    /// 401: the session ended (signed out from Jellyfin's dashboard, password changed) or the password is wrong.
    case unauthorized
    /// 403: this account may not do that.
    case forbidden
    case http(Int)
    /// The server does not answer: PC off, Wi-Fi of another place, Tailscale off.
    case unreachable
    case timeout
    /// It answers, but not as Jellyfin does.
    case notJellyfin
    case certificate
    case tooOld(String)
    /// Built-in Tailscale's relay could not reach the server, and why (its X-Mira-Tailscale: needs-login, offline,
    /// unreachable…).
    case tailscale(String)
}

/// Mira's way to Jellyfin's API: one server, one account. Every request carries Jellyfin's authorization header with
/// this device and, once signed in, the session's token; the token never goes in an address.
struct JellyfinClient: Sendable {
    let server: URL
    let device: DeviceIdentity
    var token: String?
    var userId: String?

    /// Jellyfin 10.9 introduced the user routes Mira uses (UserItems, UserViews…).
    static let minimumVersion = [10, 9]
    static let probeTimeout: TimeInterval = 6

    static let session: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 20
        configuration.waitsForConnectivity = false
        configuration.httpMaximumConnectionsPerHost = 6
        // Pictures carry their tag in the address: Jellyfin lets them be kept, so a second look costs nothing.
        configuration.urlCache = URLCache(memoryCapacity: 24 << 20, diskCapacity: 256 << 20)
        return URLSession(configuration: configuration)
    }()

    var authorization: String {
        func quote(_ value: String) -> String { value.replacingOccurrences(of: "\"", with: "").replacingOccurrences(of: "\\", with: "") }
        var header = "MediaBrowser Client=\"Mira\", Device=\"\(quote(device.name))\", DeviceId=\"\(quote(device.id))\", Version=\"\(quote(device.version))\""
        if let token { header += ", Token=\"\(quote(token))\"" }
        return header
    }

    func url(_ path: String, _ query: [String: String?] = [:]) -> URL {
        var parts = URLComponents(url: server.appending(path: path), resolvingAgainstBaseURL: false)!
        let items = query.compactMap { key, value in value.map { URLQueryItem(name: key, value: $0) } }.sorted { $0.name < $1.name }
        if !items.isEmpty { parts.queryItems = items }
        return parts.url!
    }

    func get<T: Decodable & Sendable>(_ path: String, _ query: [String: String?] = [:], as type: T.Type = T.self) async throws -> T {
        let data = try await send("GET", path, query)
        do { return try JSON.decoder.decode(T.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }

    /// Sent on the way to the server taken now (Way); found cut, another way is looked for, and a request that may be
    /// sent twice (reading, deleting) goes once more on it.
    @discardableResult
    func send(_ method: String, _ path: String, _ query: [String: String?] = [:], body: Data? = nil, contentType: String = "application/json", timeout: TimeInterval? = nil) async throws -> Data {
        var again = ["GET", "HEAD", "DELETE"].contains(method)
        while true {
            var request = URLRequest(url: await Way.shared.map(url(path, query)))
            request.httpMethod = method
            request.setValue(authorization, forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue(Bundle.main.preferredLocalizations.first ?? "en", forHTTPHeaderField: "Accept-Language")
            if let body {
                request.httpBody = body
                request.setValue(contentType, forHTTPHeaderField: "Content-Type")
            }
            if let timeout { request.timeoutInterval = timeout }
            let (data, response): (Data, URLResponse)
            do { (data, response) = try await Self.session.data(for: request) } catch {
                let failure = Self.failure(error)
                if Self.cut(failure), let sent = request.url, await Way.shared.lost(sent), again {
                    again = false
                    continue
                }
                throw failure
            }
            guard let http = response as? HTTPURLResponse else { throw JellyfinError.notJellyfin }
            if let code = Self.relayFailure(http) {
                if let sent = request.url, await Way.shared.lost(sent), again {
                    again = false
                    continue
                }
                throw JellyfinError.tailscale(code)
            }
            switch http.statusCode {
            case 200..<300: return data
            case 401: throw JellyfinError.unauthorized
            case 403: throw JellyfinError.forbidden
            default: throw JellyfinError.http(http.statusCode)
            }
        }
    }

    /// An error of the way to the server, not of Jellyfin.
    static func cut(_ error: Error) -> Bool {
        (error as? JellyfinError).map { $0 == .unreachable || $0 == .timeout } ?? false
    }

    /// Built-in Tailscale's relay answered instead of the server it could not reach: why (src/Mira.Tailscale/tailnet/relay.go).
    static func relayFailure(_ response: URLResponse) -> String? {
        guard let http = response as? HTTPURLResponse, [403, 502, 504].contains(http.statusCode) else { return nil }
        return http.value(forHTTPHeaderField: "X-Mira-Tailscale")
    }

    static func failure(_ error: Error) -> Error {
        if error is CancellationError { return error }
        guard let error = error as? URLError else { return JellyfinError.unreachable }
        switch error.code {
        case .cancelled: return CancellationError()
        case .timedOut: return JellyfinError.timeout
        case .serverCertificateUntrusted, .serverCertificateHasBadDate, .serverCertificateNotYetValid, .serverCertificateHasUnknownRoot,
             .secureConnectionFailed, .clientCertificateRejected, .clientCertificateRequired:
            return JellyfinError.certificate
        default: return JellyfinError.unreachable
        }
    }

    // MARK: Signing in

    /// Users/AuthenticateByName: a session for this device, its token kept by the caller.
    func signIn(username: String, password: String) async throws -> Authentication {
        let body = try JSONSerialization.data(withJSONObject: ["Username": username, "Pw": password])
        let data = try await send("POST", "Users/AuthenticateByName", body: body)
        do { return try JSON.decoder.decode(Authentication.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }

    /// Ends this device's session on Jellyfin (its token stops working there), within a few seconds at most.
    func signOut() async {
        _ = try? await send("POST", "Sessions/Logout", timeout: 5)
    }

    // MARK: Home

    /// Fields the cards and the banner need, as Mira web asks them (api.js CARD_FIELDS, CARD_IMAGES).
    private var cards: [String: String?] {
        ["userId": userId, "fields": "Genres,ChildCount,RecursiveItemCount", "enableImageTypes": "Primary,Backdrop,Thumb,Logo", "imageTypeLimit": "1"]
    }

    func resume(limit: Int = 16) async throws -> [Item] {
        try await get("UserItems/Resume", cards.merging(["limit": String(limit), "mediaTypes": "Video", "enableUserData": "true"]) { $1 }, as: ItemList.self).items ?? []
    }

    func nextUp(limit: Int = 16) async throws -> [Item] {
        try await get("Shows/NextUp", cards.merging(["limit": String(limit), "enableResumable": "false", "enableRewatching": "false"]) { $1 }, as: ItemList.self).items ?? []
    }

    /// What was watched lately, to order Continue watching by the last activity on each film or series.
    func history() async throws -> [Item] {
        try await get("Items", [
            "userId": userId, "recursive": "true", "includeItemTypes": "Movie,Episode", "sortBy": "DatePlayed", "sortOrder": "Descending",
            "limit": "40", "fields": "SeriesInfo", "enableImages": "false", "enableUserData": "true",
        ], as: ItemList.self).items ?? []
    }

    /// Films and series, newest first (of one library: « New in … »); or the favourites, by title.
    func browse(favorites: Bool = false, library: String? = nil, limit: Int = 24) async throws -> [Item] {
        try await get("Items", cards.merging([
            "recursive": "true", "includeItemTypes": "Movie,Series", "limit": String(limit), "parentId": library,
            "sortBy": favorites ? "SortName" : "DateCreated,SortName", "sortOrder": favorites ? "Ascending" : "Descending",
            "isFavorite": favorites ? "true" : nil, "enableTotalRecordCount": "false",
        ]) { $1 }, as: ItemList.self).items ?? []
    }

    func item(_ id: String) async throws -> Item {
        try await get("Items/\(id)", ["userId": userId])
    }

    // MARK: Pictures

    /// A picture of Jellyfin at the width it is shown, `width` points on a screen of `scale` pixels per point, in steps
    /// of 80 pixels so that the server keeps fewer sizes. Pictures need no token.
    func imageURL(_ art: Artwork, width: CGFloat, scale: CGFloat, quality: Int = 90) -> URL {
        let pixels = min(3840, Int((width * max(1, scale) / 80).rounded(.up)) * 80)
        let path = art.type == "Backdrop" ? "Items/\(art.itemId)/Images/Backdrop/0" : "Items/\(art.itemId)/Images/\(art.type)"
        return url(path, ["maxWidth": String(pixels), "quality": String(quality), "tag": art.tag])
    }
}

// MARK: Finding the server

extension JellyfinClient {
    /// The server at what was typed: every candidate address is asked at once, and the first one in order that answers
    /// as Jellyfin wins; a less preferred one that already answered waits 1.5 s for it, no more.
    static func discover(_ input: String) async throws -> ServerInfo {
        let candidates = try ServerAddress.candidates(input)
        enum Event: Sendable { case probe(Int, Probe), grace }
        return try await withThrowingTaskGroup(of: Event.self, returning: ServerInfo.self) { group in
            for (index, candidate) in candidates.enumerated() {
                group.addTask {
                    let result = await Self.probe(candidate)
                    return .probe(index, result)
                }
            }
            var results = [Probe?](repeating: nil, count: candidates.count)
            var graceStarted = false, graceOver = false
            // A name of the Internet never ends in http:// once https:// failed on its certificate: someone on the way
            // can make HTTPS fail just so, then answer in plain HTTP.
            let internet = candidates.first.map { ServerAddress.exposesPassword(URL(string: "http://\($0.host(percentEncoded: false) ?? "")")!) } ?? false
            func refused(_ index: Int) -> Bool {
                internet && results[index]?.info?.address.scheme == "http"
                    && results[..<index].contains { $0?.failure == .certificate }
            }
            func answered(_ index: Int) -> Bool { results[index]?.info != nil && !refused(index) }
            while let event = try await group.next() {
                switch event {
                case let .probe(index, result): results[index] = result
                case .grace: graceOver = true
                }
                guard let first = results.indices.first(where: { results[$0] == nil || answered($0) }) else { break }
                if answered(first) {
                    group.cancelAll()
                    return try Self.supported(results[first]!.info!)
                }
                if let other = results.indices.first(where: answered) {
                    if graceOver {
                        group.cancelAll()
                        return try Self.supported(results[other]!.info!)
                    }
                    if !graceStarted {
                        graceStarted = true
                        group.addTask {
                            try? await Task.sleep(for: .seconds(1.5))
                            return .grace
                        }
                    }
                }
            }
            let failures = results.compactMap { $0?.failure }
            if results.contains(where: { $0?.info != nil }) || failures.contains(.certificate) { throw JellyfinError.certificate }
            if failures.contains(.notJellyfin) { throw JellyfinError.notJellyfin }
            // Through Built-in Tailscale: why its relay could not reach the server (signed out, PC off…).
            for case let .tailscale(code) in failures where code != "unreachable" { throw JellyfinError.tailscale(code) }
            if !failures.isEmpty && failures.allSatisfy({ $0 == .timeout }) { throw JellyfinError.timeout }
            throw JellyfinError.unreachable
        }
    }

    struct Probe: Sendable {
        var info: ServerInfo?
        var failure: JellyfinError?
    }

    /// Jellyfin's public information at an address, within six seconds. Never throws. A Tailscale address is asked
    /// through Built-in Tailscale's relay too, when it is connected (away from home without Tailscale's app).
    static func probe(_ address: URL) async -> Probe {
        guard WayPlan.isTailnet(address), let relayed = await Way.shared.builtIn(address) else { return await probe(address, at: address) }
        return await withTaskGroup(of: Probe.self) { group in
            group.addTask { await probe(address, at: address) }
            group.addTask { await probe(address, at: relayed) }
            var failure: Probe?
            for await result in group {
                if result.info != nil {
                    group.cancelAll()
                    return result
                }
                failure = failure ?? result
            }
            return failure ?? Probe(failure: .unreachable)
        }
    }

    /// Jellyfin's public information for `address`, asked at `base`: the address itself, or its way through the relay.
    private static func probe(_ address: URL, at base: URL) async -> Probe {
        var request = URLRequest(url: base.appending(path: "System/Info/Public"), timeoutInterval: probeTimeout)
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        do {
            let (data, response) = try await session.data(for: request)
            if let code = relayFailure(response) { return Probe(failure: .tailscale(code)) }
            guard let http = response as? HTTPURLResponse, http.statusCode == 200, data.count < 1_000_000,
                  let info = try? JSON.decoder.decode(PublicInfo.self, from: data), let id = info.id, !id.isEmpty else {
                return Probe(failure: .notJellyfin)
            }
            // Emby answers the same request: its product name tells them apart.
            if let product = info.productName, !product.localizedCaseInsensitiveContains("Jellyfin") { return Probe(failure: .notJellyfin) }
            // A proxy may redirect http:// to https://: keep the address that actually answered (never the relay's).
            var final = address
            if base == address, let answered = http.url?.absoluteString, answered.hasSuffix("System/Info/Public"),
               let url = try? ServerAddress.normalize(String(answered.dropLast("System/Info/Public".count))) {
                final = url
            }
            return Probe(info: ServerInfo(address: final, id: id, name: info.serverName ?? "Jellyfin", version: info.version ?? ""))
        } catch {
            if Task.isCancelled { return Probe(failure: .unreachable) }
            return Probe(failure: failure(error) as? JellyfinError ?? .unreachable)
        }
    }

    static func supported(_ info: ServerInfo) throws -> ServerInfo {
        let parts = info.version.split(whereSeparator: { $0 == "-" || $0 == "+" }).first?.split(separator: ".").compactMap { Int($0) } ?? []
        guard parts.count >= 2 else { return info }
        if parts[0] < minimumVersion[0] || parts[0] == minimumVersion[0] && parts[1] < minimumVersion[1] { throw JellyfinError.tooOld(info.version) }
        return info
    }
}

/// A picture of an item, by its tag: a new picture gets a new tag, so an address never shows an old one.
struct Artwork: Sendable, Hashable {
    let itemId: String
    let type: String
    let tag: String
    var blurHash: String?

    enum Kind { case poster, wide, backdrop, logo }

    /// Falls back as Mira web does (images.js artFor): an episode shows its own picture first, then its series'.
    static func of(_ item: Item, _ kind: Kind) -> Artwork? {
        func make(_ id: String?, _ type: String, _ tag: String?) -> Artwork? {
            guard let id, let tag, !id.isEmpty, !tag.isEmpty else { return nil }
            let hashes = item.imageBlurHashes?.first { $0.key.caseInsensitiveCompare(type) == .orderedSame }?.value
            return Artwork(itemId: id, type: type, tag: tag, blurHash: hashes?[tag])
        }
        func own(_ type: String) -> String? { item.imageTags?.first { $0.key.caseInsensitiveCompare(type) == .orderedSame }?.value }
        let backdrop = { make(item.id, "Backdrop", item.backdropImageTags?.first) ?? make(item.parentBackdropItemId, "Backdrop", item.parentBackdropImageTags?.first) }
        switch kind {
        case .poster:
            return make(item.id, "Primary", own("Primary")) ?? make(item.seriesId, "Primary", item.seriesPrimaryImageTag)
        case .wide:
            if item.isEpisode {
                return make(item.id, "Primary", own("Primary")) ?? make(item.parentThumbItemId, "Thumb", item.parentThumbImageTag) ?? backdrop()
            }
            return make(item.id, "Thumb", own("Thumb")) ?? backdrop() ?? make(item.id, "Primary", own("Primary"))
        case .backdrop:
            return backdrop() ?? make(item.id, "Thumb", own("Thumb")) ?? make(item.parentThumbItemId, "Thumb", item.parentThumbImageTag)
        case .logo:
            return make(item.id, "Logo", own("Logo")) ?? make(item.parentLogoItemId, "Logo", item.parentLogoImageTag)
        }
    }
}
