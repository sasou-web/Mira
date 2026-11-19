import Foundation

/// A profile of the home: a Jellyfin account its administrator offered (never an administrator's), with a PIN or not.
struct HomeProfile: Codable, Sendable, Hashable, Identifiable {
    var id: String
    var name: String
    var imageTag: String?
    var pin: Bool
}

/// This iPhone in a home, kept in the Keychain (this device only): where the home's Jellyfin is, its own id, its name, the
/// home token, the device id of its profiles' sessions, whether it is reached through the home's own node (a home link),
/// and the profiles last read, for the hall to show at once (HomeMembership of Core).
struct HomeMembership: Codable, Sendable, Hashable {
    var server: URL
    var serverId: String?
    var name: String
    var token: String
    var deviceId: String
    /// Joined from a home link: `server` is on the home's tailnet, reached through the home's node.
    var throughTailnet: Bool
    var profiles: [HomeProfile] = []
    /// The profile open on this iPhone, as the home last said.
    var current: String?
}

/// Why the home said no (HomeRefusal of Core).
enum HomeRefusal: Sendable, Equatable {
    /// This Jellyfin has no home (turned off, never on, or its extension older than the home).
    case noHome
    /// Not the home's password.
    case wrong
    /// Too many tries: a few minutes to wait.
    case tooMany
    /// The home has its most devices.
    case full
    /// This iPhone is no longer in the home (removed, the password changed): its password again.
    case outOfHome
    /// The profile is no longer in the home.
    case notInHome
    /// The profile has a PIN: asked.
    case pinAsked
    case pinWrong
    /// Too many wrong PINs: `wait` seconds to wait.
    case pinWait
    /// Jellyfin did not answer the extension, or an administrator must first open the home's settings.
    case notReady
    case other
}

/// The home's refusal, in the extension's words (in Mira's language), and what it means.
struct HomeRefused: Error, Sendable, Equatable {
    var message: String
    var status: Int
    var kind: HomeRefusal
    var left: Int?
    var wait: Int?
}

/// A profile opened on this iPhone: a real session of its Jellyfin account, under the home's device id.
struct HomeOpened: Sendable, Equatable {
    var token: String
    var userId: String
    var userName: String
    var serverId: String?
    var deviceId: String
}

/// The answers of /Mira/home, every field optional (HomeAnswer of Core).
struct HomeAnswer: Decodable, Sendable {
    var home: Bool?
    var token: String?
    var profiles: [HomeProfile]?
    var current: String?
    var deviceId: String?
    var accessToken: String?
    var userId: String?
    var userName: String?
    var serverId: String?
    var error: String?
    var pin: Bool?
    var left: Int?
    var wait: Int?
    var policy: String?
    var link: String?
    var expires: Date?
}

/// « Le foyer », the family's side of /Mira/home: no Jellyfin session, the home token in X-Mira-Home. `base` is where the
/// home's Jellyfin is reached now: its address at home, or its address through the home node's relay away from it.
/// The extension says its refusals in Mira's language (Accept-Language).
struct HomeAPI: Sendable {
    let base: URL
    let device: DeviceIdentity

    static let header = "X-Mira-Home"

    private static let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 15
        configuration.waitsForConnectivity = false
        configuration.urlCache = nil
        return URLSession(configuration: configuration)
    }()

    /// Whether this Jellyfin has a home: true or false; nil when its extension has none or it doesn't answer as one.
    func on() async -> Bool? {
        guard let (status, data, json) = try? await send("GET", "Mira/home", token: nil, body: nil, timeout: 6), status == 200, json else { return nil }
        return (try? Self.decoder.decode(HomeAnswer.self, from: data))?.home
    }

    /// The server's own id (System/Info/Public), to be sure it is the Jellyfin the link names.
    func serverId() async -> String? {
        await Way.askJellyfin(base, 10)?.id
    }

    /// This iPhone joins the home with its password: its home token, the profiles, the device id of its sessions.
    func join(password: String, name: String) async throws -> (token: String, profiles: [HomeProfile], deviceId: String) {
        let answer = try await family("POST", "Mira/home/join", token: nil, body: ["password": password, "device": device.id, "name": name])
        guard let token = answer?.token, !token.isEmpty, let deviceId = answer?.deviceId, !deviceId.isEmpty else { throw JellyfinError.notJellyfin }
        return (token, answer?.profiles ?? [], deviceId)
    }

    /// The profiles this iPhone may open now, and the one it has open.
    func profiles(token: String) async throws -> (profiles: [HomeProfile], current: String?, deviceId: String) {
        let answer = try await family("GET", "Mira/home/profiles", token: token, body: nil)
        guard let deviceId = answer?.deviceId, !deviceId.isEmpty else { throw JellyfinError.notJellyfin }
        return (answer?.profiles ?? [], answer?.current, deviceId)
    }

    /// A profile opened on this iPhone (its PIN when it has one); the session this iPhone had open there ends.
    func open(token: String, profile: String, pin: String?) async throws -> HomeOpened {
        var body: [String: Any] = ["profile": profile, "client": "Mira", "version": device.version]
        if let pin { body["pin"] = pin }
        let answer = try await family("POST", "Mira/home/open", token: token, body: body, pinSent: pin != nil)
        guard let access = answer?.accessToken, !access.isEmpty, let user = answer?.userId, !user.isEmpty, let deviceId = answer?.deviceId, !deviceId.isEmpty else {
            throw JellyfinError.notJellyfin
        }
        return HomeOpened(token: access, userId: user, userName: answer?.userName.flatMap { $0.isEmpty ? nil : $0 } ?? user, serverId: answer?.serverId, deviceId: deviceId)
    }

    /// This iPhone leaves the home: its token stops working, its profile's session ends. True once the home knows.
    func leave(token: String) async -> Bool {
        do {
            _ = try await family("POST", "Mira/home/leave", token: token, body: nil, timeout: 8)
            return true
        } catch let refused as HomeRefused where refused.kind == .outOfHome {
            return true
        } catch {
            return false
        }
    }

    /// A profile's picture for the hall: Jellyfin serves an account's picture without a session, by its tag.
    func picture(_ profile: HomeProfile, width: Int = 320) -> URL? {
        guard let tag = profile.imageTag, !tag.isEmpty else { return nil }
        var parts = URLComponents(url: base.appending(path: "Users/\(profile.id)/Images/Primary"), resolvingAgainstBaseURL: false)
        parts?.queryItems = [URLQueryItem(name: "maxWidth", value: String(width)), URLQueryItem(name: "tag", value: tag), URLQueryItem(name: "quality", value: "90")]
        return parts?.url
    }

    // MARK: Requests

    /// A request of the family's side: its answer, or its refusal as a HomeRefused.
    private func family(_ method: String, _ path: String, token: String?, body: [String: Any]?, pinSent: Bool = false, timeout: TimeInterval = 15) async throws -> HomeAnswer? {
        let (status, data, json) = try await send(method, path, token: token, body: body, timeout: timeout)
        let answer = json && status != 204 ? try? Self.decoder.decode(HomeAnswer.self, from: data) : nil
        if (200..<300).contains(status) { return answer }
        // No extension, or one older than the home: Jellyfin's own 404, not a JSON one.
        if !json && status == 404 { throw HomeRefused(message: HomeText.noExtension, status: status, kind: .noHome) }
        if !json { throw JellyfinError.http(status) }
        throw Self.refusal(path, status: status, answer: answer, pinSent: pinSent)
    }

    /// What a refusal of the family's side means, by its route and status (HomeController of the extension).
    static func refusal(_ path: String, status: Int, answer: HomeAnswer?, pinSent: Bool) -> HomeRefused {
        let join = path.hasSuffix("/join")
        let kind: HomeRefusal = switch status {
        case 404: join ? .noHome : .notInHome
        case 401: join ? .wrong : .outOfHome
        case 409 where join: .full
        // The same 403 for a PIN asked and a wrong one: the app knows whether it sent one.
        case 403 where answer?.pin == true: pinSent ? .pinWrong : .pinAsked
        case 429 where answer?.pin == true: .pinWait
        case 429: .tooMany
        case 502: .notReady
        default: .other
        }
        let words = answer?.error?.trimmingCharacters(in: .whitespacesAndNewlines)
        let message = words.flatMap { $0.isEmpty ? nil : $0 } ?? String(localized: "Jellyfin answered with an error (\(status)). Try again in a moment.")
        return HomeRefused(message: message, status: status, kind: kind, left: answer?.left, wait: answer?.wait)
    }

    private func send(_ method: String, _ path: String, token: String?, body: [String: Any]?, timeout: TimeInterval) async throws -> (Int, Data, Bool) {
        var request = URLRequest(url: base.appending(path: path), cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(Bundle.main.preferredLocalizations.first ?? "en", forHTTPHeaderField: "Accept-Language")
        request.setValue(JellyfinClient(server: base, device: device).authorization, forHTTPHeaderField: "Authorization")
        if let token { request.setValue(token, forHTTPHeaderField: Self.header) }
        if let body {
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response): (Data, URLResponse)
        do { (data, response) = try await Self.session.data(for: request) } catch { throw JellyfinClient.failure(error) }
        guard let http = response as? HTTPURLResponse else { throw JellyfinError.notJellyfin }
        if let code = JellyfinClient.relayFailure(http) { throw JellyfinError.tailscale(code) }
        let json = (http.value(forHTTPHeaderField: "Content-Type") ?? "").lowercased().hasPrefix("application/json")
        return (http.statusCode, data, json)
    }

    static var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            if let date = HomeDates.read(text) { return date }
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Not a date: \(text)"))
        }
        return decoder
    }
}

/// The dates of the extension (System.Text.Json's ISO 8601, with or without fractions of a second).
enum HomeDates {
    static func read(_ raw: String) -> Date? {
        // .NET writes up to 7 digits after the second: 3 are kept, which ISO8601DateFormatter reads.
        var text = raw
        if let dot = text.firstIndex(of: "."), let end = text[dot...].dropFirst().firstIndex(where: { !$0.isNumber }) ?? Optional(text.endIndex) {
            let digits = text[text.index(after: dot)..<end]
            text.replaceSubrange(text.index(after: dot)..<end, with: String((digits + "000").prefix(3)))
        }
        let full = ISO8601DateFormatter()
        full.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = full.date(from: text) { return date }
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        return plain.date(from: text)
    }
}

// MARK: The administrator's side

/// An account of the server as the home's settings show it.
struct HomeAccount: Decodable, Sendable, Hashable, Identifiable {
    var id: String
    var name: String
    var imageTag: String?
    var admin: Bool
    var disabled: Bool
    var offered: Bool
    var pin: Bool
    var allLibraries: Bool
    var maxRating: String?
}

/// A device in the home: its name, when it joined and was last seen, the profile it has open.
struct HomeDeviceInfo: Decodable, Sendable, Hashable, Identifiable {
    var id: String
    var name: String
    var joined: Date?
    var seen: Date?
    var profile: String?
    var profileName: String?
}

/// The home as its administrator sees it (GET Mira/home/settings).
struct HomeSettings: Decodable, Sendable, Hashable {
    var home: Bool
    var changed: Date?
    var minPassword: Int?
    var profiles: [HomeAccount]
    var devices: [HomeDeviceInfo]
}

/// The home on the administrator's tailnet (GET Mira/home/tailnet).
struct HomeTailnetState: Decodable, Sendable, Hashable {
    struct Setup: Decodable, Sendable, Hashable {
        var tag: String
        var tagOwners: String
        var grant: String
        var scopes: [String]
    }
    struct OpenRule: Decodable, Sendable, Hashable {
        var section: String
        var number: Int
        var sources: [String]
    }
    var connected: Bool
    var clientId: String?
    var server: String?
    var port: Int?
    var setup: Setup?
    /// What Mira would change in the policy itself (POST Mira/home/tailnet/fix): « tag », « grant », « narrow ».
    struct Change: Decodable, Sendable, Hashable {
        var kind: String
        var section: String?
        var number: Int?
        var sources: [String]?
    }
    var policy: String?
    var open: [OpenRule]?
    var fix: [Change]?
    /// The OAuth client can do more than the home needs (« All »).
    var broad: Bool?
}

/// What the administrator's side says when it refuses: the extension's words, the policy's verdict for a link.
struct HomeAdminRefused: Error, Sendable {
    var message: String
    var policy: String?
}

extension JellyfinClient {
    /// The home as its administrator sees it; nil when the extension has no home (older than it, or none).
    func homeSettings() async throws -> HomeSettings? {
        let (status, data, json) = try await homeAdmin("GET", "Mira/home/settings", body: nil)
        if status == 404 && !json { return nil }
        try Self.homeCheck(status, data)
        do { return try HomeAPI.decoder.decode(HomeSettings.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }

    /// The home's password set or changed (every device out), or nil to turn the home off.
    func homeSetPassword(_ password: String?) async throws {
        let (status, data, _) = try await homeAdmin("PUT", "Mira/home/password", body: ["password": password ?? NSNull()])
        try Self.homeCheck(status, data)
    }

    /// An account offered as a profile of the home, or no longer.
    func homeOffer(_ userId: String, _ offered: Bool) async throws {
        let (status, data, _) = try await homeAdmin("PUT", "Mira/home/profiles/\(userId)", body: ["offered": offered])
        try Self.homeCheck(status, data)
    }

    /// A profile's PIN (four digits) set, or taken off (nil).
    func homeSetPin(_ userId: String, _ pin: String?) async throws {
        let (status, data, _) = try await homeAdmin("PUT", "Mira/home/profiles/\(userId)/pin", body: ["pin": pin ?? NSNull()])
        try Self.homeCheck(status, data)
    }

    /// A device out of the home.
    func homeRemoveDevice(_ id: String) async throws {
        let (status, data, _) = try await homeAdmin("DELETE", "Mira/home/devices/\(id)", body: nil)
        try Self.homeCheck(status, data)
    }

    /// The home on the administrator's tailnet: the setup to write once, the OAuth client, the policy's verdict.
    func homeTailnet() async throws -> HomeTailnetState {
        let (status, data, _) = try await homeAdmin("GET", "Mira/home/tailnet", body: nil)
        try Self.homeCheck(status, data)
        do { return try HomeAPI.decoder.decode(HomeTailnetState.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }

    /// The home connected to the tailnet with an OAuth client (Tailscale checks it first), or disconnected (both nil).
    func homeConnectTailnet(id: String?, secret: String?) async throws {
        let (status, data, _) = try await homeAdmin("PUT", "Mira/home/tailnet", body: ["clientId": id ?? NSNull(), "clientSecret": secret ?? NSNull()])
        try Self.homeCheck(status, data)
    }

    /// The tailnet's policy put right by the extension, as `HomeTailnetState.fix` said (the client's policy_file scope).
    func homeFixPolicy() async throws {
        let (status, data, _) = try await homeAdmin("POST", "Mira/home/tailnet/fix", body: nil)
        try Self.homeCheck(status, data)
    }

    /// A home link: a key of the home's tag for one device, for 48 hours; refused with the policy's verdict otherwise.
    func homeLink() async throws -> (link: String, expires: Date) {
        let (status, data, _) = try await homeAdmin("POST", "Mira/home/link", body: nil)
        try Self.homeCheck(status, data)
        guard let answer = try? HomeAPI.decoder.decode(HomeAnswer.self, from: data), let link = answer.link, let expires = answer.expires else { throw JellyfinError.notJellyfin }
        return (link, expires)
    }

    /// A refusal of the administrator's side: the extension's words when it gave some, else Jellyfin's status.
    private static func homeCheck(_ status: Int, _ data: Data) throws {
        guard !(200..<300).contains(status) else { return }
        let answer = try? HomeAPI.decoder.decode(HomeAnswer.self, from: data)
        if let words = answer?.error?.trimmingCharacters(in: .whitespacesAndNewlines), !words.isEmpty { throw HomeAdminRefused(message: words, policy: answer?.policy) }
        switch status {
        case 401: throw JellyfinError.unauthorized
        case 403: throw JellyfinError.forbidden
        default: throw JellyfinError.http(status)
        }
    }

    /// A request of the administrator's side, on the way to Jellyfin taken now: its status and body, whatever it says.
    private func homeAdmin(_ method: String, _ path: String, body: [String: Any]?) async throws -> (Int, Data, Bool) {
        var request = URLRequest(url: await Way.shared.map(url(path)))
        request.httpMethod = method
        request.setValue(authorization, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(Bundle.main.preferredLocalizations.first ?? "en", forHTTPHeaderField: "Accept-Language")
        if let body {
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response): (Data, URLResponse)
        do { (data, response) = try await Self.session.data(for: request) } catch { throw Self.failure(error) }
        guard let http = response as? HTTPURLResponse else { throw JellyfinError.notJellyfin }
        if let code = Self.relayFailure(http) { throw JellyfinError.tailscale(code) }
        if http.statusCode == 401, let token { await MainActor.run { Self.refused?(token) } }
        return (http.statusCode, data, (http.value(forHTTPHeaderField: "Content-Type") ?? "").lowercased().hasPrefix("application/json"))
    }
}
