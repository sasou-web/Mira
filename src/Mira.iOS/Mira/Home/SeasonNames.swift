import Foundation

/// The name of a season's pill from the name Jellyfin gives it, as Windows and the Mac (EpisodePages.Named of Core,
/// ogsas 2026-11-19): its own name (« Bakemonogatari »), else Mira's « Saison N » when it says nothing but the season
/// (« Season 2 », « L'attaque des Titans saison 2 »); a season and its part, or the final season, in Mira's words.
/// Cases of tests/fixtures/season-names.json.
enum SeasonNames {
    private static let word = "(?:season|saison|staffel|temporada|stagione|seizoen)"
    private static let before = "(?:^|[\\s\\-–—:·,(])"

    /// The pill of season `season` named `name` by Jellyfin.
    static func label(_ season: Int, _ name: String?, french: Bool = EpisodeShelf.french) -> String {
        own(season, name, french: french) ?? (french ? "Saison \(season)" : "Season \(season)")
    }

    /// Its own name, or nil for Mira's « Saison N ».
    static func own(_ season: Int, _ name: String?, french: Bool) -> String? {
        guard let name = name?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty else { return nil }
        let number = String(season)
        if let part = groups(before + word + "\\s*0*(\\d+)\\s*[\\-–—:·,]?\\s*(?:part|partie|teil|parte|cour)\\s*0*(\\d+)\\s*\\)?$", name), part[0] == number {
            return french ? "Saison \(season) · partie \(part[1])" : "Season \(season) · part \(part[1])"
        }
        if groups(before + "(?:" + word + "\\s+finale?|(?:the\\s+)?final\\s+season|la\\s+saison\\s+finale|finale)\\s*\\)?$", name) != nil {
            return french ? "Saison finale" : "Final Season"
        }
        if let ending = groups(before + word + "\\s*0*(\\d+)\\s*\\)?$", name), ending[0] == number { return nil }
        if let bare = groups("^\\p{L}+(?:\\s\\p{L}+)?\\s*0*(\\d+)$", name, ignoringCase: false), bare[0] == number { return nil }
        return name
    }

    /// The captured groups of `pattern` in `text`, or nil when it does not match.
    private static func groups(_ pattern: String, _ text: String, ignoringCase: Bool = true) -> [String]? {
        guard let expression = try? NSRegularExpression(pattern: pattern, options: ignoringCase ? [.caseInsensitive] : []),
              let match = expression.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return nil }
        return (1..<match.numberOfRanges).map { index in Range(match.range(at: index), in: text).map { String(text[$0]) } ?? "" }
    }
}
