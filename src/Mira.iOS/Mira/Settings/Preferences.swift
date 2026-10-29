import Foundation
import Observation

/// What Settings changes, kept on this iPhone. The languages, the quality, the intros, the tracks of each series and the
/// titles removed from Continue watching also go to the account's Jellyfin (« Tes choix gardés sur ton Jellyfin »,
/// AccountChoices), as on Mira web, Windows and Mac; resuming, the next episode and Home's rows stay this iPhone's.
@MainActor @Observable
final class Preferences {
    // MARK: This iPhone

    /// A title resumes where it was left (otherwise from the start).
    var resume: Bool { didSet { defaults.set(resume, forKey: Keys.resume) } }
    /// « Resume a little earlier », in seconds (0, 5, 10 or 15; resume.json).
    var resumeBack: Int { didSet { defaults.set(resumeBack, forKey: Keys.resumeBack) } }
    /// The next episode starts by itself at the end of one.
    var autoNext: Bool { didSet { defaults.set(autoNext, forKey: Keys.autoNext) } }
    /// Home's rows in order, « - » before one put away (HomeLayout); empty: as Mira shows them.
    var homeRows: String { didSet { defaults.set(homeRows, forKey: Keys.homeRows) } }

    // MARK: Kept on Jellyfin too

    /// « auto », « max », or a bitrate in bits per second.
    var quality: String { didSet { defaults.set(quality, forKey: Keys.quality); changed() } }
    /// The audio languages in order (« jpn,ja,fre,fra,fr… »); nil: Mira's order for its language.
    var audioLanguages: String? { didSet { defaults.set(audioLanguages, forKey: Keys.audio); changed() } }
    var subtitleLanguages: String? { didSet { defaults.set(subtitleLanguages, forKey: Keys.subtitles); changed() } }
    /// Openings and recaps skipped without a touch.
    var autoSkip: Bool { didSet { defaults.set(autoSkip, forKey: Keys.autoSkip); changed() } }
    /// The tracks chosen for each series (AccountChoices.track), oldest first, for its next episodes; per account.
    private(set) var seriesTracks: [(id: String, track: JSONValue)] = []
    /// The films and series taken out of Continue watching, with when (ms); per account.
    private(set) var hidden: [String: Double] = [:]

    /// Told of each change of what goes to Jellyfin (a look at Jellyfin 2 s later).
    @ObservationIgnored var onChange: (() -> Void)?
    @ObservationIgnored private var applying = false
    @ObservationIgnored private let defaults = UserDefaults.standard
    @ObservationIgnored private(set) var account: String?

    static let resumeBackChoices = [0, 5, 10, 15]
    static let qualities = ["auto", "max", "20000000", "10000000", "6000000", "3000000", "1500000"]
    /// Mira web's orders (AUDIO_LANGUAGES, SUBTITLE_LANGUAGES of tracks.js).
    static let audioOrders = ["jpn,ja,fre,fra,fr,eng,en", "jpn,ja,eng,en,fre,fra,fr", "fre,fra,fr,eng,en", "eng,en,fre,fra,fr"]
    static let subtitleOrders = ["fre,fra,fr,eng,en", "eng,en,fre,fra,fr", "eng,en", "jpn,ja"]

    init() {
        resume = defaults.object(forKey: Keys.resume) as? Bool ?? true
        resumeBack = defaults.object(forKey: Keys.resumeBack) as? Int ?? 5
        autoNext = defaults.object(forKey: Keys.autoNext) as? Bool ?? true
        homeRows = defaults.string(forKey: Keys.homeRows) ?? ""
        quality = defaults.string(forKey: Keys.quality) ?? "auto"
        audioLanguages = defaults.string(forKey: Keys.audio)
        subtitleLanguages = defaults.string(forKey: Keys.subtitles)
        autoSkip = defaults.bool(forKey: Keys.autoSkip)
    }

    var audioOrder: String { audioLanguages ?? AccountChoices.defaultAudioLanguages(AccountChoices.language) }
    var subtitleOrder: String { subtitleLanguages ?? AccountChoices.defaultSubtitleLanguages(AccountChoices.language) }

    private func changed() { if !applying { onChange?() } }

    // MARK: The account's

    /// The account signed in: its series' tracks and its titles removed, read from this iPhone.
    func use(account: String?) {
        self.account = account
        applying = true
        defer { applying = false }
        seriesTracks = []
        hidden = [:]
        guard let account, let doc = defaults.string(forKey: Keys.document(account)) else { return }
        let applied = AccountChoices.apply(.string(doc))
        seriesTracks = applied.tracks
        hidden = applied.hidden
    }

    /// The tracks last chosen for a series.
    func tracks(for series: String?) -> JSONValue? {
        guard let series else { return nil }
        return seriesTracks.last { $0.id == series }?.track
    }

    /// Tracks chosen for a series: its next episodes start with the same languages, here and on the other devices.
    func remember(_ track: JSONValue, for series: String) {
        let track = AccountChoices.track(track)
        if tracks(for: series) == track { return }
        seriesTracks.removeAll { $0.id == series }
        seriesTracks.append((id: series, track: track))
        if seriesTracks.count > 100 { seriesTracks.removeFirst(seriesTracks.count - 100) }
        changed()
    }

    /// A film or series taken out of Continue watching (its id, or its series' for an episode), or put back.
    func hide(_ group: String, _ hide: Bool) {
        if hide { hidden[group] = Date().timeIntervalSince1970 * 1000 } else { hidden[group] = nil }
        changed()
    }

    /// What this iPhone uses now, as AccountChoices.capture reads it.
    var local: AccountChoices.Local {
        var settings: [String: JSONValue] = [
            "audioLanguages": .string(audioOrder), "subtitleLanguages": .string(subtitleOrder), "autoSkip": .bool(autoSkip),
        ]
        settings["quality"] = Double(quality).map { JSONValue.number($0) } ?? .string(quality)
        return AccountChoices.Local(settings: settings, tracks: Dictionary(seriesTracks.map { ($0.id, $0.track) }, uniquingKeysWith: { $1 }), hidden: hidden)
    }

    /// The document of choices of this iPhone for the account, with the time of each.
    func document() -> JSONValue? { account.flatMap { defaults.string(forKey: Keys.document($0)) }.map(JSONValue.string) }

    func keep(_ doc: AccountChoices.Document) {
        guard let account else { return }
        defaults.set(JSONValue.object(doc).text, forKey: Keys.document(account))
    }

    /// What the account's Jellyfin brought, in this iPhone's settings (no new look at Jellyfin for them).
    func apply(_ doc: AccountChoices.Document) {
        let used = AccountChoices.apply(.object(doc))
        applying = true
        defer { applying = false }
        if let value = used.settings["audioLanguages"]?.string, value != audioOrder { audioLanguages = value }
        if let value = used.settings["subtitleLanguages"]?.string, value != subtitleOrder { subtitleLanguages = value }
        if let value = used.settings["autoSkip"]?.bool, value != autoSkip { autoSkip = value }
        switch used.settings["quality"] {
        case let .string(value)?: if value != quality { quality = value }
        case let .number(value)?: if String(Int(value)) != quality { quality = String(Int(value)) }
        default: break
        }
        if used.tracks.map(\.id) != seriesTracks.map(\.id) || used.tracks.map(\.track) != seriesTracks.map(\.track) { seriesTracks = used.tracks }
        if used.hidden != hidden { hidden = used.hidden }
    }

    /// Signed out: the account's document goes with it.
    func forget(account: String) {
        defaults.removeObject(forKey: Keys.document(account))
        if self.account == account { use(account: nil) }
    }

    private enum Keys {
        static let resume = "mira.resume"
        static let resumeBack = "mira.resumeBack"
        static let autoNext = "mira.autoNext"
        static let homeRows = "mira.homeRows"
        static let quality = "mira.quality"
        static let audio = "mira.audioLanguages"
        static let subtitles = "mira.subtitleLanguages"
        static let autoSkip = "mira.autoSkip"
        static func document(_ account: String) -> String { "mira.choices.\(account)" }
    }
}

extension JellyfinClient {
    /// Mira's display preferences of the account (« mira » of the client « mira »), whole: sent back whole, since
    /// Jellyfin replaces them all.
    func choices() async throws -> JSONValue {
        let data = try await send("GET", "DisplayPreferences/mira", ["userId": userId, "client": "mira"])
        do { return try JSONDecoder().decode(JSONValue.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }

    func saveChoices(_ preferences: JSONValue) async throws {
        let body = try JSONEncoder().encode(preferences)
        try await send("POST", "DisplayPreferences/mira", ["userId": userId, "client": "mira"], body: body)
    }

    /// A title's resume point, set by hand: 0 when it is taken out of Continue watching, as on Mira web.
    func setPosition(_ itemId: String, ticks: Int64) async throws {
        let body = try JSONSerialization.data(withJSONObject: ["PlaybackPositionTicks": max(0, ticks)])
        try await send("POST", "UserItems/\(itemId)/UserData", ["userId": userId], body: body)
    }
}

/// The look at Jellyfin: what changed here goes, what changed on the other devices comes (account-choices.js).
@MainActor
final class ChoicesSync {
    private let preferences: Preferences
    private var running: Task<Void, Never>?
    private var again = false
    private var soonTask: Task<Void, Never>?
    private var lastAt = Date.distantPast

    init(preferences: Preferences) { self.preferences = preferences }

    /// A change here goes a moment later: several together go at once.
    func soon(_ client: @escaping @MainActor () -> JellyfinClient?, after delay: Duration = .seconds(2)) {
        soonTask?.cancel()
        soonTask = Task { [weak self] in
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled, let client = client() else { return }
            self?.run(client)
        }
    }

    /// Back on the screen after a while: what changed on the other devices meanwhile.
    func lookAgain(_ client: @escaping @MainActor () -> JellyfinClient?) {
        if Date().timeIntervalSince(lastAt) > 60 { soon(client, after: .zero) }
    }

    func stop() {
        soonTask?.cancel()
        again = false
        lastAt = .distantPast
    }

    /// Never throws: Jellyfin not answering keeps what changed here, with its time, for the next look.
    func run(_ client: JellyfinClient) {
        if running != nil { again = true; return }
        let account = client.userId
        lastAt = Date()
        running = Task { [weak self] in
            guard let self else { return }
            let now = { Date().timeIntervalSince1970 * 1000 }
            let doc = AccountChoices.capture(preferences.document(), preferences.local, now: now())
            preferences.keep(doc)
            if let fetched = try? await client.choices(), preferences.account == account {
                var whole = fetched.object ?? [:]
                let custom = whole["CustomPrefs"]?.object ?? [:]
                let remote = custom[AccountChoices.preference] ?? .null
                // A choice made here while Jellyfin answered counts too.
                let merged = AccountChoices.merge(.object(AccountChoices.capture(.object(doc), preferences.local, now: now())), remote)
                preferences.apply(merged)
                preferences.keep(merged)
                if AccountChoices.differs(.object(merged), remote) {
                    var shared = merged
                    shared["seen"] = nil
                    var prefs = custom
                    prefs[AccountChoices.preference] = .string(JSONValue.object(shared).text)
                    whole["CustomPrefs"] = .object(prefs)
                    try? await client.saveChoices(.object(whole))
                }
            }
            running = nil
            if again {
                again = false
                run(client)
            }
        }
    }
}
