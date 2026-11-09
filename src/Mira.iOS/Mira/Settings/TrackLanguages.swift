import Foundation

/// The languages of the tracks: the orders before any choice and what Settings offers. TrackLanguages of Core, with its
/// cases in tests/fixtures/languages.json. An iPhone that never chose starts with its own language when it is neither
/// English nor French, else Mira's; a choice writes « the language, then English » (English: then French), Japanese
/// first for the audio when asked.
enum TrackLanguages {
    /// Each language Settings offers, with every code a file or Jellyfin may give it (ISO 639-2 B and T, 639-1).
    static let known: [(two: String, codes: String)] = [
        ("ja", "jpn,ja"), ("fr", "fre,fra,fr"), ("en", "eng,en"), ("de", "ger,deu,de"), ("es", "spa,es"), ("it", "ita,it"),
        ("pt", "por,pt"), ("ru", "rus,ru"), ("ko", "kor,ko"), ("zh", "chi,zho,zh"), ("ar", "ara,ar"), ("hi", "hin,hi"),
        ("pl", "pol,pl"), ("nl", "dut,nld,nl"), ("sv", "swe,sv"), ("no", "nor,nob,no,nb"), ("da", "dan,da"), ("fi", "fin,fi"),
        ("tr", "tur,tr"), ("he", "heb,he"), ("th", "tha,th"), ("vi", "vie,vi"), ("id", "ind,id"), ("uk", "ukr,uk"),
        ("cs", "cze,ces,cs"), ("hu", "hun,hu"), ("el", "gre,ell,el"), ("ro", "rum,ron,ro"), ("ca", "cat,ca"), ("ms", "may,msa,ms"),
    ]

    /// The language of a code (« ger », « pt-BR », « nb »), as Settings lists it; nil when not listed.
    static func two(_ code: String?) -> String? {
        let main = (code ?? "").trimmingCharacters(in: .whitespaces).split(whereSeparator: { $0 == "-" || $0 == "_" }).first.map { $0.lowercased() } ?? ""
        guard !main.isEmpty else { return nil }
        return known.first { $0.codes.split(separator: ",").map(String.init).contains(main) }?.two
    }

    static func codes(_ two: String) -> String { known.first { $0.two == two }?.codes ?? two }

    /// The first language of a device that never chose: its own, unless English or French (Mira's then, as before).
    static func start(device: String?, mira: String) -> String {
        if let own = two(device), own != "en", own != "fr" { return own }
        return mira == "fr" ? "fr" : "en"
    }

    /// This iPhone's: its first preferred language (Settings, General, Language & Region), else Mira's.
    static var start: String { start(device: Locale.preferredLanguages.first, mira: AccountChoices.language) }

    /// The audio order a choice writes: Japanese first when asked, the language, then English (English: then French).
    static func audio(_ first: String, japaneseFirst: Bool) -> String { ordered((japaneseFirst ? ["ja"] : []) + [first, then(first)]) }
    /// The subtitles' order a choice writes: the language, then English (English: then French).
    static func subtitles(_ first: String) -> String { ordered([first, then(first)]) }

    /// What Settings shows of an order: its first language other than Japanese (Japanese when the order is Japanese, then
    /// English at most); nil when one of its codes is not listed (« Custom order »).
    static func first(of order: String?) -> String? {
        let languages = split(order).map { two($0) }
        guard !languages.isEmpty, !languages.contains(where: { $0 == nil }) else { return nil }
        var distinct: [String] = []
        for case let language? in languages where !distinct.contains(language) { distinct.append(language) }
        if distinct[0] != "ja" || distinct.count == 1 || (distinct.count == 2 && distinct[1] == "en") { return distinct[0] }
        return distinct[1]
    }

    /// Whether a Japanese track goes before the others (an anime's original language).
    static func japaneseFirst(_ order: String?) -> Bool { two(split(order).first) == "ja" }

    /// The languages Settings offers, named in Mira's language: this iPhone's first, then by name.
    static func choices(locale: Locale = Locale(identifier: AccountChoices.language)) -> [(two: String, name: String)] {
        let start = self.start
        return known.map { (two: $0.two, name: TrackNames.language($0.two, locale: locale) ?? $0.two) }.sorted { a, b in
            if (a.two == start) != (b.two == start) { return a.two == start }
            return a.name.compare(b.name, options: [.caseInsensitive, .diacriticInsensitive], locale: locale) == .orderedAscending
        }
    }

    private static func then(_ first: String) -> String { first == "en" ? "fr" : "en" }

    private static func ordered(_ languages: [String]) -> String {
        var seen: [String] = []
        for language in languages where !seen.contains(language) { seen.append(language) }
        return seen.map { codes($0) }.joined(separator: ",")
    }

    private static func split(_ order: String?) -> [String] {
        (order ?? "").split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }
}
