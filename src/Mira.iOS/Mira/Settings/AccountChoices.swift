import Foundation

/// A JSON value kept as it is: Jellyfin's document of choices may hold what a later Mira writes, kept untouched.
enum JSONValue: Codable, Sendable, Hashable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(Double.self) { self = .number(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode([JSONValue].self) { self = .array(value) }
        else { self = .object(try container.decode([String: JSONValue].self)) }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case let .bool(value): try container.encode(value)
        // Times in milliseconds: written as whole numbers, as Mira web writes them.
        case let .number(value): if value.rounded() == value, abs(value) < 9e15 { try container.encode(Int64(value)) } else { try container.encode(value) }
        case let .string(value): try container.encode(value)
        case let .array(value): try container.encode(value)
        case let .object(value): try container.encode(value)
        }
    }

    var object: [String: JSONValue]? { if case let .object(value) = self { value } else { nil } }
    var string: String? { if case let .string(value) = self { value } else { nil } }
    var number: Double? { if case let .number(value) = self { value } else { nil } }
    var bool: Bool? { if case let .bool(value) = self { value } else { nil } }

    /// A JSON text read; nil when it is not JSON.
    static func parse(_ text: String) -> JSONValue? {
        guard let data = text.data(using: .utf8) else { return nil }
        return try? JSONDecoder().decode(JSONValue.self, from: data)
    }

    var text: String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        return (try? encoder.encode(self)).flatMap { String(data: $0, encoding: .utf8) } ?? "null"
    }
}

/// « Tes choix gardés sur ton Jellyfin »: the choices of the account that follow it from one device to another (the
/// tracks of each series, the titles removed from Continue watching, the languages, the quality, the intros), kept in
/// Jellyfin's display preferences « mira » of the client « mira ». Each entry has its time; the newest wins. The rules
/// of Mira web (choices.js) and of Windows and Mac (AccountChoices in Core), with their cases in
/// tests/fixtures/choices.json.
enum AccountChoices {
    typealias Document = [String: JSONValue]

    /// The custom preference that holds them.
    static let preference = "mira.choices"
    /// The series whose tracks are kept, and the titles removed or put back, newest first.
    static let limits = ["tracks": 100, "hidden": 200]
    private static let sections = ["settings", "tracks", "hidden"]

    /// The orders of the tracks before any choice, in Mira's language (PlayerSettings.DefaultAudioLanguage in Core).
    static func defaultAudioLanguages(_ language: String) -> String { language == "fr" ? "jpn,ja,fre,fra,fr,eng,en" : "jpn,ja,eng,en,fre,fra,fr" }
    static func defaultSubtitleLanguages(_ language: String) -> String { language == "fr" ? "fre,fra,fr,eng,en" : "eng,en,fre,fra,fr" }

    /// Mira's language on this iPhone: French when Mira is shown in French, English otherwise.
    static var language: String { Bundle.main.preferredLocalizations.first?.hasPrefix("fr") == true ? "fr" : "en" }

    /// The settings kept on Jellyfin, with Mira's defaults: a setting never changed anywhere is not written.
    static func defaults(_ language: String) -> [String: JSONValue] {
        ["audioLanguages": .string(defaultAudioLanguages(language)), "subtitleLanguages": .string(defaultSubtitleLanguages(language)),
         "autoSkip": .bool(false), "quality": .string("auto")]
    }

    /// A value this version of Mira can use for a setting it keeps on Jellyfin.
    static func fits(_ key: String, _ value: JSONValue?, language: String = AccountChoices.language) -> Bool {
        guard let value, let base = defaults(language)[key] else { return false }
        if key == "quality" {
            if case let .string(text) = value { return text == "auto" || text == "max" }
            if case let .number(rate) = value { return rate.isFinite && rate > 0 }
            return false
        }
        switch (value, base) {
        case (.string, .string), (.bool, .bool), (.number, .number): return true
        default: return false
        }
    }

    /// A series' tracks as every Mira keeps them; `subtitles` false: subtitles off.
    static func track(_ value: JSONValue?) -> JSONValue {
        let x = value?.object ?? [:]
        func text(_ key: String) -> JSONValue { if let s = x[key]?.string, !s.isEmpty { .string(s) } else { .null } }
        let on = truthy(x["subtitles"])
        return .object([
            "audio": text("audio"), "audioTitle": text("audioTitle"), "subtitles": .bool(on),
            "subtitle": on ? text("subtitle") : .null, "subtitleTitle": on ? text("subtitleTitle") : .null,
            "forced": .bool(on && truthy(x["forced"])),
        ])
    }

    private static func truthy(_ value: JSONValue?) -> Bool {
        switch value {
        case let .bool(b): b
        case let .number(n): n != 0 && !n.isNaN
        case let .string(s): !s.isEmpty
        case .array, .object: true
        default: false
        }
    }

    private static func time(_ value: JSONValue?) -> Double { value?.number.flatMap { $0.isFinite ? $0 : nil } ?? 0 }

    /// What a document read from Jellyfin (or kept on this iPhone) holds; anything unreadable counts as nothing.
    static func read(_ value: JSONValue?) -> Document {
        var doc: Document
        switch value {
        case let .string(text)?: doc = JSONValue.parse(text)?.object ?? [:]
        case let .object(object)?: doc = object
        default: doc = [:]
        }
        for section in sections { doc[section] = .object(doc[section]?.object ?? [:]) }
        return doc
    }

    /// What this iPhone uses now: its settings, the tracks of each series, the titles removed (with when, in ms).
    struct Local: Sendable, Equatable {
        var settings: [String: JSONValue] = [:]
        var tracks: [String: JSONValue] = [:]
        var hidden: [String: Double] = [:]
    }

    /// The document of this iPhone with what changed here since it was last kept: a change made here takes `now`; the
    /// first time (a document never kept), what this iPhone had before is the oldest choice (0), so what Jellyfin
    /// already keeps wins over it, and a setting left as Mira's default is not written at all.
    static func capture(_ doc: JSONValue?, _ local: Local, now: Double, language: String = AccountChoices.language) -> Document {
        var doc = read(doc)
        let first = !truthy(doc["seen"])
        var settings = doc["settings"]?.object ?? [:], tracks = doc["tracks"]?.object ?? [:], hidden = doc["hidden"]?.object ?? [:]
        let base = defaults(language)
        for (key, value) in local.settings {
            let known = settings[key]?.object?["value"] ?? base[key]
            if value != known { settings[key] = .object(["value": value, "at": .number(first ? 0 : now)]) }
        }
        for (id, value) in local.tracks {
            let mine = track(value)
            if let known = tracks[id], track(known) == mine { continue }
            var entry = mine.object ?? [:]
            entry["at"] = .number(first ? 0 : now)
            tracks[id] = .object(entry)
        }
        for (id, at) in local.hidden {
            let known = hidden[id]
            if known == nil || at > time(known?.object?["at"]) { hidden[id] = .object(["hidden": .bool(true), "at": .number(at)]) }
        }
        // Put back here: Continue watching shows it again on the other devices too.
        for (id, known) in hidden where truthy(known.object?["hidden"]) && local.hidden[id] == nil {
            hidden[id] = .object(["hidden": .bool(false), "at": .number(now)])
        }
        doc["settings"] = .object(settings)
        doc["tracks"] = .object(tracks)
        doc["hidden"] = .object(hidden)
        doc["seen"] = .bool(true)
        return doc
    }

    /// The newest `limit` entries (then by key, the same on every device).
    private static func newest(_ entries: [String: JSONValue], _ limit: Int) -> [String: JSONValue] {
        let sorted = entries.sorted { a, b in
            let ta = time(a.value.object?["at"]), tb = time(b.value.object?["at"])
            return ta != tb ? ta > tb : a.key < b.key
        }
        return Dictionary(uniqueKeysWithValues: sorted.prefix(limit).map { ($0.key, $0.value) })
    }

    /// This iPhone's document and Jellyfin's, together: each entry from the side that changed it last (Jellyfin's when
    /// both did at the same time). What Jellyfin keeps that Mira does not know (a later version's) stays.
    static func merge(_ doc: JSONValue?, _ remote: JSONValue?) -> Document {
        let doc = read(doc), remote = read(remote)
        var merged = remote
        merged["seen"] = nil
        for section in sections {
            var all = doc[section]?.object ?? [:]
            for (key, theirs) in remote[section]?.object ?? [:] {
                guard case .object = theirs else { continue }
                if let mine = all[key], time(theirs.object?["at"]) < time(mine.object?["at"]) { continue }
                all[key] = theirs
            }
            merged[section] = .object(section == "settings" ? all : newest(all, limits[section] ?? 100))
        }
        if truthy(doc["seen"]) { merged["seen"] = .bool(true) }
        return merged
    }

    /// Whether Jellyfin must be told: the document differs from what it keeps.
    static func differs(_ merged: JSONValue?, _ remote: JSONValue?) -> Bool {
        let merged = read(merged), remote = read(remote)
        return sections.contains { merged[$0] != remote[$0] }
    }

    /// What this iPhone uses from a document: the settings it holds, the tracks of each series (oldest first) and the
    /// titles removed, with when.
    struct Applied: Sendable, Equatable {
        var settings: [String: JSONValue] = [:]
        var tracks: [(id: String, track: JSONValue)] = []
        var hidden: [String: Double] = [:]

        static func == (a: Applied, b: Applied) -> Bool {
            a.settings == b.settings && a.hidden == b.hidden && a.tracks.map(\.id) == b.tracks.map(\.id) && a.tracks.map(\.track) == b.tracks.map(\.track)
        }
    }

    static func apply(_ value: JSONValue?, language: String = AccountChoices.language) -> Applied {
        let doc = read(value)
        var applied = Applied()
        for (key, entry) in doc["settings"]?.object ?? [:] {
            let value = entry.object?["value"]
            if fits(key, value, language: language), let value { applied.settings[key] = value }
        }
        func byTime(_ entries: [String: JSONValue]) -> [(String, JSONValue)] {
            entries.sorted { a, b in
                let ta = time(a.value.object?["at"]), tb = time(b.value.object?["at"])
                return ta != tb ? ta < tb : a.key < b.key
            }.map { ($0.key, $0.value) }
        }
        applied.tracks = byTime(doc["tracks"]?.object ?? [:]).map { (id: $0.0, track: track($0.1)) }
        for (id, entry) in byTime(doc["hidden"]?.object ?? [:]) where truthy(entry.object?["hidden"]) {
            applied.hidden[id] = time(entry.object?["at"])
        }
        return applied
    }
}
