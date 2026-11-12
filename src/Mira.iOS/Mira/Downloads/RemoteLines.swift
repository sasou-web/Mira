import Foundation

/// « Télécharger sur le PC serveur »: what the relay of Mira's extension says (Mira.Jellyfin TorLinkRelay, Mira.Core
/// RemoteProtocol), what the iPhone checks before sending a search or a magnet link (as RemoteDownloads checks it
/// again there), and the lines the screen writes of the PC, a result, a request and a download: the rules of Mira for
/// Windows and Mac (Mira.Core RemoteLines), with their shared cases in tests/fixtures/torlink-lines.json.
enum RemoteLines {
    // MARK: Where it is offered

    /// « Download on the server PC » in Settings: only where the page can work, the relay of Mira's extension, which only
    /// an administrator may use (RemoteDownloads.Offered of Mira for Windows and Mac). Without the extension (most
    /// servers: Jellyfin answers 404 at Mira/torlink), the page could only say the extension is out of date.
    /// `administrator` and `miraExtension` are nil when Jellyfin did not say: `before` stays.
    static func offered(administrator: Bool?, miraExtension: Bool?, before: Bool) -> Bool {
        if administrator == false || miraExtension == false { return false }
        return administrator == true && miraExtension == true || before
    }

    // MARK: What the relay says

    /// Mira for Windows on the PC of Jellyfin, as the relay last heard from it.
    struct Agent: Decodable, Sendable, Equatable {
        var name: String
        var online: Bool
        var seen: String?
        var accepting: Bool
        /// Why its downloads wait, in the words of this iPhone's language (the relay translates).
        var problem: String?
        var qBittorrent: Bool?
    }

    struct Request: Decodable, Sendable, Equatable, Identifiable {
        var id: String
        var name: String
        var size: Int64?
        var infoHash: String?
        /// waiting, claimed, sent, failed.
        var state: String
        var error: String?
        var replace: String?
    }

    struct Control: Decodable, Sendable, Equatable {
        var id: String
        var target: String
        /// pause, resume, delete.
        var action: String
        var state: String
    }

    struct Download: Decodable, Sendable, Equatable, Identifiable {
        var id: String
        var name: String
        /// downloading, queued, paused, stopped, failed, completed, seeding.
        var status: String
        var progress: Double?
        var speed: Double?
        var peers: Int?
        var size: Int64?
        var downloaded: Int64?
        var eta: Double?
        var error: String?
    }

    struct Placed: Decodable, Sendable, Equatable, Identifiable {
        var id: String
        var title: String
        var status: String
        var itemId: String?
        var ok: Bool?
    }

    struct State: Decodable, Sendable, Equatable {
        var agent: Agent?
        var requests: [Request]?
        var controls: [Control]?
        var downloads: [Download]?
        var placed: [Placed]?
    }

    struct Search: Decodable, Sendable, Equatable {
        /// running, done, failed.
        var state: String
        var results: [Releases.Hit]?
        var error: String?
    }

    struct Made: Decodable, Sendable { var id: String }

    // MARK: Checks before sending

    static let maxQuery = 120, maxMagnet = 8192, maxName = 300

    private static func controlFree(_ text: String) -> String {
        Regex.replace("[\u{0000}-\u{001f}\u{007f}-\u{009f}]", in: text, with: " ", options: [])
    }

    /// A search as TorLink takes it: spaces gathered, no dash in front (TorLink would read « --x » as one of its
    /// options), at most 120 characters; nil when nothing is left.
    static func cleanQuery(_ query: String) -> String? {
        var clean = Regex.replace(#"\s+"#, in: controlFree(query), with: " ", options: [])
        clean = Regex.replace("^[ -]+", in: clean, with: "", options: [])
        while clean.hasSuffix(" ") { clean.removeLast() }
        let units = Array(clean.utf16)
        if units.count > maxQuery {
            clean = String(decoding: units[0..<maxQuery], as: UTF16.self)
            while clean.hasSuffix(" ") { clean.removeLast() }
        }
        return clean.isEmpty ? nil : clean
    }

    /// The info hash of a magnet link, 40 hexadecimal digits in lower case (a base32 one converted, as TorLink does),
    /// or nil when it is not one.
    static func infoHash(_ magnet: String) -> String? {
        let text = magnet.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.utf16.count <= maxMagnet, Regex.test(#"^magnet:\?"#, text), controlFree(text) == text,
              let found = Regex.first(#"[?&]xt=urn:btih:([0-9a-f]{40}|[a-z2-7]{32})(?:&|$)"#, in: text), let hash = found.text(1) else { return nil }
        return hash.count == 40 ? hash.lowercased() : base32ToHex(hash)
    }

    private static func base32ToHex(_ value: String) -> String? {
        let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")
        var hex = "", buffer = 0, bits = 0
        for c in value.uppercased() {
            guard let index = alphabet.firstIndex(of: c) else { return nil }
            buffer = (buffer << 5) | index; bits += 5
            if bits < 8 { continue }
            bits -= 8
            hex += String(format: "%02x", (buffer >> bits) & 0xff)
            buffer &= (1 << bits) - 1
        }
        return hex.count == 40 ? hex : nil
    }

    /// The name a magnet link carries (its « dn »), cleaned as cleanName; nil without one.
    static func displayName(_ magnet: String) -> String? {
        let text = magnet.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let start = text.firstIndex(of: "?") else { return nil }
        for pair in text[text.index(after: start)...].split(separator: "&", omittingEmptySubsequences: false) where pair.lowercased().hasPrefix("dn=") {
            let raw = pair.dropFirst(3).replacingOccurrences(of: "+", with: " ")
            return cleanName(raw.removingPercentEncoding ?? raw)
        }
        return nil
    }

    /// A name to show: without control characters, spaces gathered, at most 300 characters; nil when empty.
    static func cleanName(_ name: String?) -> String? {
        guard let name else { return nil }
        var clean = Regex.replace(#"\s+"#, in: controlFree(name), with: " ", options: []).trimmingCharacters(in: .whitespaces)
        let units = Array(clean.utf16)
        if units.count > maxName {
            clean = String(decoding: units[0..<maxName], as: UTF16.self).trimmingCharacters(in: .whitespaces) + "…"
        }
        return clean.isEmpty ? nil : clean
    }

    // MARK: Words

    /// Whether this iPhone shows Mira in French: « 4,2 » then, « 4.2 » in English (no grouping either way).
    static var french: Bool { (Bundle.main.preferredLocalizations.first ?? "en").hasPrefix("fr") }

    static func decimal(_ value: Double, _ digits: Int, french: Bool = RemoteLines.french) -> String {
        let text = String(format: "%.\(digits)f", value)
        return french ? text.replacingOccurrences(of: ".", with: ",") : text
    }

    enum Unit: Sendable, Equatable { case gigabytes, megabytes, megabytesPerSecond, kilobytesPerSecond }

    /// The number of a size and its unit: « 850 » MB, « 4,2 » GB, « 12 » GB.
    static func sizeParts(_ bytes: Int64?, french: Bool = RemoteLines.french) -> (number: String, unit: Unit) {
        let n = Double(max(0, bytes ?? 0)), gib = 1_073_741_824.0, mib = 1_048_576.0
        if n >= gib { return (decimal(n / gib, n >= 10 * gib ? 0 : 1, french: french), .gigabytes) }
        return (String(max(1, Int((n / mib).rounded(.toNearestOrAwayFromZero)))), .megabytes)
    }

    /// The number of a speed and its unit; nil when nothing comes.
    static func speedParts(_ bytesPerSecond: Double?, french: Bool = RemoteLines.french) -> (number: String, unit: Unit)? {
        let n = bytesPerSecond ?? 0, mib = 1_048_576.0
        guard n > 0 else { return nil }
        if n >= mib { return (decimal(n / mib, n >= 10 * mib ? 0 : 1, french: french), .megabytesPerSecond) }
        return (String(max(1, Int((n / 1024).rounded(.toNearestOrAwayFromZero)))), .kilobytesPerSecond)
    }

    private static func words(_ parts: (number: String, unit: Unit)) -> String {
        let n = parts.number
        switch parts.unit {
        case .gigabytes: return String(localized: "\(n) GB")
        case .megabytes: return String(localized: "\(n) MB")
        case .megabytesPerSecond: return String(localized: "\(n) MB/s")
        case .kilobytesPerSecond: return String(localized: "\(n) KB/s")
        }
    }

    /// « 850 MB », « 4.2 GB », « 12 GB », as Mira for Windows writes a size.
    static func size(_ bytes: Int64?) -> String { words(sizeParts(bytes)) }

    /// « 2.4 MB/s », « 350 KB/s »; empty when nothing comes.
    static func speed(_ bytesPerSecond: Double?) -> String { speedParts(bytesPerSecond).map(words) ?? "" }

    static func date(_ text: String?) -> Date? {
        guard let text else { return nil }
        let precise = ISO8601DateFormatter()
        precise.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return precise.date(from: text) ?? ISO8601DateFormatter().date(from: text)
    }

    static func ago(_ seconds: Double) -> String {
        if seconds < 90 { return String(localized: "just now") }
        if seconds < 5400 { return String(localized: "\(Int((seconds / 60).rounded(.toNearestOrAwayFromZero))) min ago") }
        if seconds < 129_600 { return String(localized: "\(Int((seconds / 3600).rounded(.toNearestOrAwayFromZero)))h ago") }
        return String(localized: "\(Int((seconds / 86400).rounded(.toNearestOrAwayFromZero))) days ago")
    }

    /// Where the PC of Jellyfin stands: ready when it takes searches (online), even while its downloads must wait.
    static func agentLine(_ agent: Agent?, now: Date = Date()) -> (text: String, ready: Bool) {
        guard let agent else {
            return (String(localized: "Mira hasn’t been opened on the server PC yet: that’s where it searches and downloads, with TorLink. Open it there, signed in with an administrator account."), false)
        }
        if !agent.online {
            let name = agent.name
            if let seen = date(agent.seen) {
                let when = ago(max(0, now.timeIntervalSince(seen)))
                return (String(localized: "Mira isn’t open on \(name) (seen \(when)). Your requests will wait for it and go through when it’s back."), false)
            }
            return (String(localized: "Mira isn’t open on \(name). Your requests will wait for it and go through when it’s back."), false)
        }
        if let problem = agent.problem, !problem.isEmpty { return (problem, true) }
        return (String(localized: "\(agent.name) is ready: TorLink downloads there, then Mira adds the title to your library."), true)
    }

    /// What a title found in a category of TorLink is, on its card; empty when the relay does not say.
    static func kindLabel(_ category: String?) -> String {
        switch category {
        case "movies": String(localized: "Movie")
        case "tv": String(localized: "Show")
        case "anime": String(localized: "Anime")
        default: ""
        }
    }

    /// A search result: « 1.4 GB · 120 seeders · YTS »; nothing of seeders for a site that does not count them (SubsPlease).
    static func hitLine(_ hit: Releases.Hit) -> String {
        let seeders = max(0, hit.seeders ?? 0)
        let sources = !Releases.reportsHealth(hit.source) && seeders == 0 ? "" : seeders == 0 ? String(localized: "no seeders") : String(localized: "\(seeders) seeders")
        return [size(hit.size), sources, hit.source ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
    }

    /// A request not yet in TorLink's list: waiting for the PC, on its way, or refused (and why).
    static func requestLine(_ request: Request) -> String {
        switch request.state {
        case "waiting": return String(localized: "Waiting for the server PC")
        case "claimed": return String(localized: "Sending to TorLink…")
        case "sent":
            if let error = request.error, !error.isEmpty { return String(localized: "Sent to TorLink: \(error)") }
            return String(localized: "Sent to TorLink")
        default:
            if let error = request.error, !error.isEmpty { return error }
            return String(localized: "TorLink refused this download.")
        }
    }

    /// What is left, in seconds, in pieces: under a minute, minutes, hours and minutes, days.
    enum Left: Sendable, Equatable { case underMinute, minutes(Int), hours(Int, Int), days(Int) }

    static func left(_ seconds: Double) -> Left {
        let n = max(0, seconds)
        if n < 60 { return .underMinute }
        let minutes = Int((n / 60).rounded(.toNearestOrAwayFromZero))
        if minutes < 60 { return .minutes(minutes) }
        if minutes < 1440 { return .hours(minutes / 60, minutes % 60) }
        return .days(Int((n / 86400).rounded(.toNearestOrAwayFromZero)))
    }

    /// « less than a minute », « 12 min », « 1h 05m », « 3 days ».
    static func remaining(_ seconds: Double) -> String {
        switch left(seconds) {
        case .underMinute: return String(localized: "less than a minute")
        case let .minutes(m): return String(localized: "\(m) min")
        case let .hours(h, m):
            let mm = m < 10 ? "0\(m)" : "\(m)"
            return String(localized: "\(h)h \(mm)m")
        case let .days(d): return String(localized: "\(d) days")
        }
    }

    /// Seconds a download still needs: TorLink's own guess, or what is left of its size at its speed; nil when nothing
    /// comes or the size is unknown.
    static func timeLeft(_ download: Download) -> Double? {
        guard download.status == "downloading" else { return nil }
        if let eta = download.eta, eta > 0 { return eta }
        let total = Double(download.size ?? 0), rate = download.speed ?? 0
        guard total > 0, rate > 0 else { return nil }
        return max(0, total - Double(download.downloaded ?? 0)) / rate
    }

    /// « 1.2 GB of 2.9 GB », or « 2.9 GB » before anything came; empty when TorLink does not know the size yet.
    static func amountLine(_ download: Download) -> String {
        let total = download.size ?? 0
        guard total > 0 else { return "" }
        let done = min(total, max(0, download.downloaded ?? 0))
        if done > 0, done < total {
            let (a, b) = (size(done), size(total))
            return String(localized: "\(a) of \(b)")
        }
        return size(total)
    }

    static func percent(_ progress: Double?) -> Int { max(0, min(100, Int((progress ?? 0).rounded(.toNearestOrAwayFromZero)))) }

    /// A download in TorLink: « 42% · 1.2 GB of 2.9 GB · 2.4 MB/s · 12 min left · 12 peers », « Paused · 42% »…
    static func downloadLine(_ download: Download) -> String {
        let value = percent(download.progress)
        // « 42% », « 42 % » in French: the system's way of writing a percentage.
        let progress = (Double(value) / 100).formatted(.percent.precision(.fractionLength(0)))
        let peers = max(0, download.peers ?? 0)
        func join(_ parts: [String]) -> String { parts.filter { !$0.isEmpty }.joined(separator: " · ") }
        switch download.status {
        case "downloading":
            let rate = speed(download.speed)
            if rate.isEmpty {
                return join([progress, amountLine(download), peers > 0 ? String(localized: "\(peers) peers, waiting for data") : String(localized: "looking for peers…")])
            }
            let time = timeLeft(download).map { remaining($0) }
            return join([progress, amountLine(download), rate, time.map { String(localized: "\($0) left") } ?? "", peers > 0 ? String(localized: "\(peers) peers") : ""])
        case "queued": return join([String(localized: "Queued · \(progress)"), amountLine(download)])
        case "paused": return join([String(localized: "Paused · \(progress)"), amountLine(download)])
        case "stopped": return String(localized: "Stopped · \(progress) · resumes when TorLink reopens on the server PC")
        case "failed": return join([String(localized: "Failed · \(progress)"), failure(download.error)])
        case "completed", "seeding": return String(localized: "Done · adding to your library…")
        default: return progress
        }
    }

    enum Failure: Sendable, Equatable { case none, diskFull, cannotWrite, other(String) }

    static func failureKind(_ error: String?) -> Failure {
        let text = (error ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty { return .none }
        if Regex.test("ENOSPC|no space", text) { return .diskFull }
        if Regex.test("EACCES|EPERM", text, options: []) { return .cannotWrite }
        return .other(text)
    }

    /// Why TorLink gave up: a full disk or a folder it cannot write to in words, anything else in TorLink's own.
    static func failure(_ error: String?) -> String {
        switch failureKind(error) {
        case .none: ""
        case .diskFull: String(localized: "the server PC’s disk is full")
        case .cannotWrite: String(localized: "TorLink can’t write to its folder")
        case let .other(text): text
        }
    }

    /// Downloads that can be paused, resumed or cancelled from here: under way, waiting, paused, stopped or failed.
    static func controllable(_ download: Download) -> Bool {
        ["downloading", "queued", "paused", "stopped", "failed"].contains(download.status)
    }

    /// What Mira for Windows on the server PC says of a title it placed, in its own language: read back in this
    /// iPhone's when it is one of those fixed words, as it is else.
    private static let placedWords: [(String, String)] = [
        ("meilleure version en place, l’ancienne dans la Corbeille", "better version in place, the old one in the Recycle Bin"), ("en attente des fichiers", "waiting for the files"),
        ("dossier de bibliothèque à choisir", "library folder to choose"), ("rangement en cours…", "sorting…"), ("disponible dans Jellyfin", "available in Jellyfin"),
        ("Jellyfin l’ajoute", "Jellyfin is adding it"), ("rangé", "in place"), ("rangé en partie", "partly in place"), ("déjà dans la bibliothèque", "already in the library"),
        ("échec", "failed"), ("ignoré", "skipped"),
    ]

    static func placedLine(_ status: String, french: Bool = RemoteLines.french) -> String {
        func pick(_ pair: (String, String)) -> String { french ? pair.0 : pair.1 }
        let lower = status.lowercased()
        if let word = placedWords.first(where: { $0.0.lowercased() == lower || $0.1.lowercased() == lower }) { return pick(word) }
        let prefix = ("pas remplacé : ", "not replaced: ")
        for start in [prefix.0, prefix.1] where lower.hasPrefix(start.lowercased()) {
            return pick(prefix) + String(status.dropFirst(start.count))
        }
        return status
    }
}

extension JellyfinClient {
    /// « Télécharger sur le PC serveur » (administrators): what the PC of Jellyfin has under way, as the relay holds it.
    func torLink() async throws -> RemoteLines.State { try await get("Mira/torlink") }

    /// The id of a search the PC runs with TorLink, in films, series and animes at once.
    func torLinkSearch(_ query: String) async throws -> String {
        let body = try JSONSerialization.data(withJSONObject: ["query": query, "category": "all"])
        return try JSON.decoder.decode(RemoteLines.Made.self, from: try await send("POST", "Mira/torlink/search", body: body)).id
    }

    func torLinkSearchState(_ id: String) async throws -> RemoteLines.Search { try await get("Mira/torlink/search/\(id)") }

    /// A result of a search to download, or a magnet link pasted.
    func torLinkRequest(search: String, hit: Releases.Hit) async throws {
        var fields: [String: Any] = ["search": search, "result": hit.id, "name": hit.name]
        if let size = hit.size { fields["size"] = size }
        try await send("POST", "Mira/torlink/requests", body: try JSONSerialization.data(withJSONObject: fields))
    }

    func torLinkRequest(magnet: String) async throws {
        try await send("POST", "Mira/torlink/requests", body: try JSONSerialization.data(withJSONObject: ["magnet": magnet]))
    }

    func torLinkCancel(_ id: String) async throws { try await send("DELETE", "Mira/torlink/requests/\(id)") }

    /// pause, resume or delete (its files with it) a download TorLink has.
    func torLinkControl(_ id: String, _ action: String) async throws {
        try await send("POST", "Mira/torlink/control", body: try JSONSerialization.data(withJSONObject: ["id": id, "action": action]))
    }
}
