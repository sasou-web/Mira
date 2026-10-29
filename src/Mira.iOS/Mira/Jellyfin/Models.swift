import Foundation

/// A film, series, episode or library as Jellyfin describes it (BaseItemDto), with only what Mira shows. Jellyfin
/// writes its keys in PascalCase, and in camelCase for a few answers: `JSON.decoder` reads both.
struct Item: Codable, Sendable, Identifiable, Hashable {
    let id: String
    var name: String?
    var type: String?
    var collectionType: String?
    var productionYear: Int?
    var runTimeTicks: Int64?
    /// When Jellyfin added it (fields=DateCreated): the mark of « New episode ».
    var dateCreated: String?
    var overview: String?
    var genres: [String]?
    var officialRating: String?
    var communityRating: Double?
    var childCount: Int?
    var seriesId: String?
    var seriesName: String?
    var seasonId: String?
    var parentIndexNumber: Int?
    var indexNumber: Int?
    var indexNumberEnd: Int?
    var imageTags: [String: String]?
    var backdropImageTags: [String]?
    var imageBlurHashes: [String: [String: String]]?
    var parentBackdropItemId: String?
    var parentBackdropImageTags: [String]?
    var parentThumbItemId: String?
    var parentThumbImageTag: String?
    var parentLogoItemId: String?
    var parentLogoImageTag: String?
    var seriesPrimaryImageTag: String?
    var userData: UserData?
    var mediaSources: [MediaSource]?
    /// The file's chapters (Items/{id} only): their titles name openings and credits (Timeline).
    var chapters: [Chapter]?

    var isEpisode: Bool { type == "Episode" }
    var isSeries: Bool { type == "Series" }
}

struct Chapter: Codable, Sendable, Hashable {
    var name: String?
    var startPositionTicks: Int64?
}

struct UserData: Codable, Sendable, Hashable {
    var playbackPositionTicks: Int64?
    var playedPercentage: Double?
    var played: Bool?
    var isFavorite: Bool?
    var unplayedItemCount: Int?
    var lastPlayedDate: String?
}

struct ItemList: Codable, Sendable {
    var items: [Item]?
    var totalRecordCount: Int?
}

/// System/Info/Public: what a Jellyfin server says of itself before anyone signs in.
struct PublicInfo: Codable, Sendable, Hashable {
    var id: String?
    var serverName: String?
    var version: String?
    var productName: String?
}

/// Users/AuthenticateByName's answer.
struct Authentication: Codable, Sendable {
    struct User: Codable, Sendable { let id: String; var name: String? }
    let user: User
    let accessToken: String
    var serverId: String?
}

/// The server found at an address the user typed.
struct ServerInfo: Sendable, Hashable {
    let address: URL
    let id: String
    let name: String
    let version: String
}

enum JSON {
    /// Jellyfin's keys, PascalCase or camelCase, read as Swift's camelCase: only the first letter changes.
    static var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .custom { keys in
            let last = keys.last!.stringValue
            return Key(last.prefix(1).lowercased() + last.dropFirst())
        }
        return decoder
    }
    static var encoder: JSONEncoder { JSONEncoder() }

    struct Key: CodingKey {
        let stringValue: String
        var intValue: Int? { nil }
        init(_ string: String) { stringValue = string }
        init?(stringValue: String) { self.stringValue = stringValue }
        init?(intValue: Int) { return nil }
    }
}

/// Lengths and codes as the other Mira apps write them (dom.js of Mira web).
enum ItemText {
    static let ticksPerSecond: Int64 = 10_000_000

    /// « 45 min », « 1 h 05 » / « 1h 5m », « 2 h » / « 2h ».
    static func duration(seconds: Int64) -> String {
        let minutes = max(1, Int((Double(seconds) / 60).rounded()))
        if minutes < 60 { return String(localized: "\(minutes) min") }
        let hours = minutes / 60, rest = minutes % 60
        if rest == 0 { return String(localized: "\(hours) h") }
        return String(localized: "\(hours) h \(String(format: "%02d", rest))")
    }

    /// « S2 · É5 » (« S2 · E5 » in English), « É5 » without a season.
    static func episodeCode(_ item: Item) -> String {
        var parts: [String] = []
        if let season = item.parentIndexNumber { parts.append("S\(season)") }
        if let number = item.indexNumber {
            let range = item.indexNumberEnd.map { "\(number)–\($0)" } ?? "\(number)"
            parts.append(String(localized: "E\(range)", comment: "An episode's number: É5 in French"))
        }
        return parts.joined(separator: " · ")
    }

    /// Time left from where the title was stopped, « reste 32 min »; nil before it was started.
    static func remaining(_ item: Item) -> String? {
        guard let total = item.runTimeTicks, let done = item.userData?.playbackPositionTicks, total > 0, done > 0, done < total else { return nil }
        return String(localized: "\(duration(seconds: (total - done) / ticksPerSecond)) left")
    }

    /// How far it was watched, 0 to 1; 0 when not started or already watched.
    static func progress(_ item: Item) -> Double {
        guard item.userData?.played != true, let total = item.runTimeTicks, let done = item.userData?.playbackPositionTicks, total > 0, done > 0 else { return 0 }
        return min(1, Double(done) / Double(total))
    }

    /// « ★ 7.8 · PG-13 »: Jellyfin's community rating and the age rating, on a title's page.
    static func ratings(_ item: Item) -> String {
        var parts: [String] = []
        if let rating = item.communityRating, rating > 0 { parts.append("★ " + rating.formatted(.number.precision(.fractionLength(1)))) }
        if let official = item.officialRating, !official.isEmpty { parts.append(official) }
        return parts.joined(separator: " · ")
    }

    /// « 2024 · 2 h 05 · Animation, Adventure » for a film, the seasons for a series.
    static func meta(_ item: Item) -> String {
        var parts: [String] = []
        if let year = item.productionYear { parts.append(String(year)) }
        if item.isSeries, let seasons = item.childCount, seasons > 0 { parts.append(String(localized: "\(seasons) seasons")) }
        else if let ticks = item.runTimeTicks, ticks > 0 { parts.append(duration(seconds: ticks / ticksPerSecond)) }
        if let genres = item.genres, !genres.isEmpty { parts.append(genres.prefix(2).joined(separator: ", ")) }
        return parts.joined(separator: " · ")
    }
}
