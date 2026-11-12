import Foundation

/// « Télécharger sur le PC serveur »: what a release name says (show or film, episode, resolution, source, codec,
/// languages, group), the release Mira puts forward for each title or episode, and the order of the others. The rules
/// of Mira web and Mira for Windows and Mac (js/releases.js, Mira.Core ReleaseRanking), with their shared cases in
/// tests/fixtures/releases.json.
enum Releases {
    /// What a release name says; a field it does not say is nil.
    struct Release: Equatable, Sendable {
        var title: String
        var year: Int?
        /// The other name given in parentheses (« JoJo no Kimyou na Bouken: Steel Ball Run »).
        var alternate: String?
        var season: Int?
        var episode: Int?
        var lastEpisode: Int?
        /// Numbered without a season (« Show - 05 »).
        var absolute = false
        /// A whole season or more.
        var pack = false
        var resolution: Int?
        /// remux, bluray, webdl, webrip, hdrip, hdtv, dvd, cam.
        var source: String?
        var codec: String?
        var tenBit = false
        var hdr: String?
        /// MULTI, VFF, VFQ, VF, VOSTFR.
        var languages: [String] = []
        var group: String?
        var repack = false
        /// The group was given in brackets in front, a fansub's (« [Erai-raws] »).
        var fansub = false
        /// An episode between two, a recap or a special: « Show - 07.5 » (7; `episode` is nil).
        var half: Int?
    }

    /// A result of a search, as the relay gives it (without its magnet link).
    struct Hit: Decodable, Sendable, Equatable, Identifiable {
        var id: String
        var name: String
        var size: Int64?
        var seeders: Int?
        var leechers: Int?
        var source: String?
        var added: Int64?
        /// movies, tv or anime, for a search of every category.
        var category: String?
    }

    struct Entry: Sendable, Equatable {
        var hit: Hit
        var release: Release
        var order: Int
        var score: Int
    }

    /// A title, an episode, a season or a film, its best release put forward.
    struct Group: Sendable, Equatable, Identifiable {
        var key: String
        var show: String
        var title: String
        var label: String
        var best: Entry
        var others: [Entry]
        var pack: Bool
        var release: Release
        var category: String?
        var id: String { key }
    }

    // MARK: Reading a name

    private static let sourceLabels = ["remux": "Remux", "bluray": "BluRay", "webdl": "WEB-DL", "webrip": "WEBRip", "hdrip": "HDRip", "hdtv": "HDTV", "dvd": "DVD"]
    // The source first: a 1080p WEB-DL beats a 720p BluRay, a 1080p BluRay beats a 2160p WEB-DL (heavy to convert on a
    // small server, and no better on a phone). A version filmed in a theatre is always last.
    private static let sourceScore = ["remux": 14, "bluray": 12, "webdl": 10, "webrip": 7, "hdrip": 5, "hdtv": 4, "dvd": 2, "cam": -100]
    private static let resolutionScore = [2160: 20, 1080: 30, 720: 16, 576: 4, 480: 4]
    private static let languageScore = ["MULTI": 8, "VFF": 5, "VFQ": 5, "VF": 5, "VOSTFR": 3]

    /// Words a title never goes past: the first of them ends it.
    private static let tags: Set<String> = Set("""
        4k 8k uhd hdr hdr10 hdr10+ hdr10plus dv dovi hlg sdr bluray blu-ray bdrip brrip bdremux remux bd bdmv
        web-dl webdl webrip web-rip web hdtv pdtv hdrip dvdrip dvd dvdr dvd5 dvd9 dvdscr screener scr cam camrip hdcam ts hdts
        telesync tc hdtc telecine tvrip xvid divx hevc avc av1 vp9 x264 x265 h264 h265 hi10 hi10p 10bit 8bit aac ac3 eac3 ddp
        dts truehd atmos flac opus mp3 dual dual-audio multi multi-audio multisub multi-subs msubs proper repack rerip internal
        nf amzn dsnp hmax atvp pcok cr french truefrench vff vfq vfi vf vostfr subfrench complete integrale intégrale batch
        extended unrated uncut remastered imax
        """.split(whereSeparator: { $0 == " " || $0 == "\n" }).map(String.init))
    /// Tags that are also words of titles (« Charlottes Web 2006 », « The French Dispatch », « A Complete Unknown »,
    /// « Mr Hollands Opus », « Star Wars The Bad Batch S03E15 »): one ends a title only before another tag or at the end,
    /// and those no title ends with (« Film FRENCH 2019 », « Show Integrale 2005-2009 ») before a year too.
    private static let titleWords: Set<String> = ["web", "french", "opus", "complete", "integrale", "intégrale", "batch"]
    private static let beforeYears: Set<String> = ["french", "complete", "integrale", "intégrale"]
    private static let packWords: Set<String> = ["complete", "integrale", "intégrale", "batch"]

    private struct Token { var text: String; var index: Int }

    private static func tokenize(_ text: String) -> [Token] {
        Regex.all(#"[^\s\[\](){}]+"#, in: text, options: []).map { Token(text: $0.text(0) ?? "", index: $0.start) }
    }

    private static func bare(_ token: String) -> String {
        Regex.replace(#"^[-.,:;+~]+|[-.,:;~]+$"#, in: token.lowercased(), with: "", options: [])
    }

    private static func isTag(_ token: String) -> Bool {
        let t = bare(token)
        if tags.contains(t) { return true }
        for pattern in [#"^\d{3,4}[pi]$"#, #"^[hx]\.?26[45]$"#, #"^(?:dd|ddp|eac3|ac3|aac|dts)[\d.+]*$"#, #"^\d{1,2}-?bits?$"#]
        where Regex.test(pattern, t, options: []) { return true }
        if let dash = t.firstIndex(of: "-"), tags.contains(String(t[..<dash])) { return true }
        return false
    }

    /// « Batch-GRP » is « batch »; « WEB-DL » stays itself.
    private static func wordOf(_ token: String) -> String {
        let t = bare(token)
        if let dash = t.firstIndex(of: "-"), dash != t.startIndex, !tags.contains(t) { return String(t[..<dash]) }
        return t
    }

    /// Whether the token at `i` ends a title: a tag, except a word of titles that another word, an episode or a year
    /// follows (« Charlottes Web 2006 », « A Complete Unknown », « The Bad Batch S03E15 »). « Complete » and « Intégrale »
    /// also end it before what makes every episode or every film (« Complete Series », « COMPLETE S01-S05 », « Complete Movie
    /// Series », « Complete 8-Film Collection », « Complete Trilogy », « Integrale Livres I a VI »).
    private static func endsTitle(_ tokens: [Token], _ i: Int) -> Bool {
        guard isTag(tokens[i].text) else { return false }
        let word = wordOf(tokens[i].text)
        guard titleWords.contains(word) else { return true }
        var next = i + 1
        while next < tokens.count, !tokens[next].text.contains(where: { $0.isLetter || $0.isNumber }) { next += 1 }
        guard next < tokens.count else { return true }
        let following = bare(tokens[next].text)
        if beforeYears.contains(word), Regex.test(#"^(?:19|20)\d\d(?:[-~](?:19|20)?\d\d)?$"#, following, options: []) { return true }
        guard packWords.contains(word) else { return endsTitle(tokens, next) }
        // A pack word before another (« The Bad Batch Complete Series ») is the title's own.
        return !packWords.contains(wordOf(tokens[next].text)) && endsTitle(tokens, next)
            || word != "batch" && Regex.test(#"^(?:series|seasons?|saisons?|s\d{1,2}(?:[-~]s?\d{1,2})?|collection|saga|box|boxset|pack|movies?|films?|(?:tri|duo|di|tetra|quadri|penta|hexa|antho)log(?:y|ie)|livres?|\d{1,2}-?(?:films?|movies?)|\d{1,3}[-~]\d{1,3})$"#, following, options: [])
    }

    private static func has(_ text: String, _ pattern: String) -> Bool {
        Regex.test("(?<![a-z0-9])(?:\(pattern))(?![a-z0-9])", text)
    }

    private static func isYear(_ text: String) -> Bool { Regex.test(#"^(?:19|20)\d\d$"#, text, options: []) }

    /// What a release gives in parentheses that is not a name: languages and subtitles, platforms, notes, editions.
    private static let notNames: Set<String> = Set("""
        vostfr vostf vost vosta vf vff vfq vfi vf2 vo french truefrench subfrench multi multisub multisubs msubs sub subs subbed
        subtitles softsubs hardsub hardsubs dub dubbed dual audio raw raws eng english jpn jap japanese ad wkn wakanim adn cr
        crunchyroll funi funimation hidive netflix amazon prime disney hulu bili bilibili b-global abema iqiyi viki readnfo nfofix
        nfo read uncensored censored batch complete integrale intégrale extended unrated uncut remastered restored criterion imax
        """.split(whereSeparator: { $0 == " " || $0 == "\n" }).map(String.init))

    /// Words of a release that name nothing: a language or subtitles (« VOSTFR », « Multi Subs »), a platform (« WKN »,
    /// « ADN »), a note (« READNFO »), a season or a part alone (« Season 2 », « Part 2 »), or one word in capitals.
    static func namesNothing(_ text: String) -> Bool {
        let value = text.trimmingCharacters(in: .whitespaces)
        if value.isEmpty || Regex.test(#"^(?:(?:part|partie|cour|vol(?:ume)?|chapter|chapitre|arc|ep(?:isode)?|e|s|season|saison)\s*\d{1,3}|\d{1,2}(?:st|nd|rd|th)\s+(?:season|cour|part))$"#, value) {
            return true
        }
        let words = value.split(whereSeparator: { " ._+/&".contains($0) }).map(String.init)
        func plain(_ word: String) -> Bool { isTag(word) || notNames.contains(word.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "-"))) }
        if words.allSatisfy({ word in
            let parts = word.lowercased().split(separator: "-").map(String.init)
            return plain(word) || parts.count > 1 && parts.allSatisfy(plain)
        }) { return true }
        guard words.count == 1, let word = words.first else { return false }
        return word.utf16.count <= 8 && word.allSatisfy { $0.isASCII && $0.isLetter } && word == word.uppercased()
    }

    /// A title to compare, its letters and digits only: lower case, no accent, ligature or exponent (« œ » as « oe », « ³ »
    /// as « 3 »), « & » as « and », without apostrophes, spaces or punctuation. Release names write a title in plain letters,
    /// an elided article as a word of its own (« L.Arnacoeur », « Le.Fabuleux.Destin.D.Amelie.Poulain ») and an English
    /// apostrophe as nothing (« Charlottes.Web »): « L'Arnacœur », « Charlotte's Web » and « Alien³ » are them.
    static func titleKey(_ title: String?) -> String {
        var text = (title ?? "").decomposedStringWithCompatibilityMapping.lowercased().replacingOccurrences(of: "&", with: "and")
        for (ligature, letters) in [("œ", "oe"), ("æ", "ae"), ("ß", "ss"), ("ø", "o"), ("ł", "l"), ("đ", "d")] {
            text = text.replacingOccurrences(of: ligature, with: letters)
        }
        return Regex.replace(#"[^\p{L}\p{Nd}]+"#, in: text, with: "", options: [])
    }

    private static func tidy(_ text: String) -> String {
        var title = Regex.replace(#"\s+"#, in: text, with: " ", options: []).trimmingCharacters(in: .whitespaces)
        title = Regex.replace(#"^[\s\-–—.:;,_+~|]+|[\s\-–—.:;,_+~|(\[]+$"#, in: title, with: "", options: [])
        guard title == title.lowercased(), Regex.test(#"\p{L}"#, title, options: []) else { return title }
        // All in lower case: each word gets its capital.
        var out = "", start = true
        for c in title {
            out += start && c.isLetter ? c.uppercased() : String(c)
            start = c.isWhitespace
        }
        return out
    }

    /// What a release name says.
    static func parse(_ name: String?) -> Release {
        var text = Regex.replace("[\u{0000}-\u{001f}\u{007f}-\u{009f}]", in: name ?? "", with: " ", options: []).trimmingCharacters(in: .whitespacesAndNewlines)
        text = Regex.replace(#"\.(?:mkv|mp4|m4v|avi|webm|ts|torrent)$"#, in: text, with: "")
        text = Regex.replace(#"^\s*\[?\s*(?:www\.)?[\w-]+\.(?:com|org|net|to|se|cc|tv|xyz|io|me|info|ws|mx|lol|nz|ch|la|in|is|am|li|pw|site|club|top|rs|ru|eu|co|uk|fr)\s*\]?\s*[-–—:|]+\s*"#, in: text, with: "")
        var group: String?
        var fansub = false
        if let lead = Regex.first(#"^\s*\[([^\[\]]{1,40})\]\s*"#, in: text, options: []) {
            group = lead.text(1)?.trimmingCharacters(in: .whitespaces)
            text = lead.after
            fansub = true
        }

        // The other name, in parentheses after the release's own: « (JoJo no Kimyou na Bouken: Steel Ball Run, Dual-Audio) »,
        // « (VOSTFR, Battle Spirits Re Zekkai no Kuu) »; never a tag, a platform or a note (« (READNFO, …) », « (WKN) »).
        var alternate: String?
        parentheses: for m in Regex.all(#"\(([^()]*)\)"#, in: text, options: []) {
            for element in (m.text(1) ?? "").split(separator: ",", omittingEmptySubsequences: false) {
                var first = String(element)
                if let dash = first.range(of: " - ") { first = String(first[..<dash.lowerBound]) }
                first = first.trimmingCharacters(in: .whitespaces)
                if first.utf16.count < 3 || !Regex.test(#"\p{L}"#, first, options: []) || first.utf16.count <= 6 && Regex.test(#"(?<![0-9])(?:19|20)[0-9]{2}(?![0-9])"#, first, options: [])
                    || Regex.test(#"^[0-9A-F]{8}$"#, first) || tokenize(first).contains(where: { isTag($0.text) }) || namesNothing(first) { continue }
                // « Kikansha no Mahou wa Tokubetsu desu 2nd Season »: the show, as its title would be read.
                let title = parse(first).title
                if !title.isEmpty, !namesNothing(title) { alternate = title; break parentheses }
                break
            }
        }

        // The group at the end of a scene name: « …H.264-VARYG (…) ».
        if group == nil || group?.isEmpty == true,
           let tail = Regex.first(#"-\s?([A-Za-z0-9][A-Za-z0-9_]{1,24})\s*(?:\([^()]*\)|\[[^\[\]]*\]|\{[^{}]*\})*\s*$"#, in: text, options: []),
           let found = tail.text(1), !Regex.test(#"^\d+$"#, found, options: []), !isTag(found), !Regex.test("web$", tail.before) {
            group = found
        }

        // Scene names put dots or underscores for spaces; « Mr. Robot » keeps its dot, « AAC2.0 » and « 5.1 » too.
        var spaced = text.replacingOccurrences(of: "_", with: " ")
        if spaced.filter({ $0 == "." }).count >= spaced.filter({ $0 == " " }).count {
            let units = Array(spaced.utf16)
            func digit(_ i: Int) -> Bool { i >= 0 && i < units.count && units[i] >= 48 && units[i] <= 57 }
            var out = [UInt16]()
            for (i, unit) in units.enumerated() {
                if unit == 46, !(digit(i - 1) && digit(i + 1) && !digit(i + 2)) { out.append(32) } else { out.append(unit) }
            }
            spaced = String(decoding: out, as: UTF16.self)
        }
        var plain = Regex.replace(#"\[[^\]]*\]|\{[^}]*\}"#, in: spaced, with: " ", options: [])
        plain = Regex.replace(#"\(([^()]*)\)"#, in: plain, options: []) { m in
            let inner = m.text(1) ?? ""
            return Regex.test(#"^\s*(?:19|20)\d\d\s*$"#, inner, options: []) ? " \(inner) " : " "
        }
        let plainLength = plain.utf16.count

        // Season and episode.
        var season: Int?, episode: Int?, lastEpisode: Int?, half: Int?, absolute = false, pack = false, cut = plainLength
        func mark(_ index: Int) { if index >= 0, index < cut { cut = index } }
        var found = Regex.first(#"(?<![a-z0-9])s(\d{1,2})[ ._-]?e(\d{1,4})(?:-?e(\d{1,4})|-(\d{1,4})(?![0-9p]))?(?![0-9])"#, in: plain)
        if let m = found {
            season = m.int(1); episode = m.int(2); lastEpisode = m.int(3) ?? m.int(4); mark(m.start)
        }
        if found == nil, let m = Regex.first(#"(?<![a-z0-9])(\d{1,2})x(\d{2,3})(?![a-z0-9])"#, in: plain) {
            found = m
            season = m.int(1); episode = m.int(2); mark(m.start)
        }
        if episode == nil {
            // Fansub numbering: « Show - 05 », « Show - 01 ~ 12 » (a batch); never a year.
            let numbered = Regex.first(#"(?:^|\s)[-–]\s*(?:ep?\.?\s*|episode\s*|#)?(\d{1,4})(?:v\d)?(?:\s*[-~]\s*(\d{1,4})(?:v\d)?)?(?=\s|$)"#, in: plain)
            // « Show - 07.5 », « Show S3 - Episode 12.5 »: an episode between two (a recap, a special), never its whole season.
            let between = Regex.first(#"(?:^|\s)[-–]\s*(?:ep?\.?\s*|episode\s*|#)?(\d{1,4})[.,]5(?:v\d)?(?=\s|$)"#, in: plain)
            var range = Regex.first(#"(?<![a-z0-9])(?:s\d{1,2}\s*[-~]\s*s\d{1,2}|(?:seasons?|saisons?)\s*\d{1,2}\s*[-~]\s*\d{1,2})(?![0-9])"#, in: plain)
            // « Show Season 2 - 05 » is season 2, episode 5, never seasons 2 to 5: the dash is the episode's.
            if let r = range, let n = numbered ?? between, n.start > r.start, n.start < r.start + r.result.range.length,
               let words = r.text(0)?.lowercased(), !words.hasPrefix("seasons"), !words.hasPrefix("saisons") { range = nil }
            let single = Regex.first(#"(?<![a-z0-9])(?:s(\d{1,2})(?![0-9a-z])|(?:season|saison|temporada)\s*(\d{1,2})(?![0-9])|(\d{1,2})(?:st|nd|rd|th)\s+season)"#, in: plain)
            if let range {
                pack = true; mark(range.start)
            } else if let single {
                season = single.int(1) ?? single.int(2) ?? single.int(3); pack = true; mark(single.start)
            }
            if let between, let number = between.text(1), (group?.isEmpty == false) || season != nil || number.count <= 3 {
                half = Int(number); mark(between.start)
                pack = false; absolute = season == nil
            }
            if half == nil, let number = numbered,
               let first = number.text(1), !isYear(first), (group?.isEmpty == false) || season != nil || first.count <= 3 {
                episode = Int(first); mark(number.start)
                if let last = number.int(2) { lastEpisode = last; pack = true }
                if pack, number.text(2) == nil { pack = false }
                absolute = season == nil
            }
        }
        let tokens = tokenize(plain)
        for complete in Regex.all(#"(?<![a-z0-9])(?:complete(?:\s+series)?|int[ée]grale|batch)(?![a-z0-9])"#, in: plain) {
            // « A Complete Unknown », « Star Wars The Bad Batch S03E15 »: words of the title, not every episode.
            if let at = tokens.firstIndex(where: { $0.index == complete.start }), !(complete.text(0) ?? "").contains(" "), !endsTitle(tokens, at) { continue }
            if (episode == nil && half == nil) || lastEpisode != nil { pack = true }
            mark(complete.start)
            break
        }

        // The title ends at the first marker: an episode, a year after its first word, a tag.
        var year: Int?
        let nextYear = Calendar(identifier: .gregorian).component(.year, from: Date()) + 1
        var i = 1
        while i < tokens.count, tokens[i].index < cut {
            let t = bare(tokens[i].text)
            if isYear(t), let value = Int(t), value <= nextYear { year = value; i += 1; continue }
            if endsTitle(tokens, i) { mark(tokens[i].index); break }
            i += 1
        }
        if year != nil {
            // The last plausible year before the tags wins (« Blade Runner 2049 2017 ») and ends the title.
            let years = tokens.enumerated().filter { $0.offset > 0 && $0.element.index < cut && isYear(bare($0.element.text)) }
            if let chosen = years.last?.element {
                year = Int(bare(chosen.text)); mark(chosen.index)
            }
        }
        let units = Array(plain.utf16)
        var title = tidy(String(decoding: units[0..<min(cut, units.count)], as: UTF16.self))
        if title.isEmpty { title = tidy(plain) }
        if title.isEmpty { title = name ?? "" }
        // What the release says of itself comes after its title: « The French Dispatch » is not in French, « Cam » (2018)
        // was not filmed in a theatre. The title's place in the name is known while nothing in brackets comes before its end.
        var end = min(cut, units.count)
        while end > 0, let unit = Unicode.Scalar(units[end - 1]), unit.properties.isWhitespace { end -= 1 }
        let spacedUnits = Array(spaced.utf16), textUnits = Array(text.utf16)
        let lower = (end > 0 && spacedUnits.count >= end && spacedUnits[0..<end] == units[0..<end]
            ? String(decoding: textUnits[end...], as: UTF16.self) : text).lowercased()

        // Picture and sound.
        let resolution: Int? = has(lower, "2160p|4k|uhd") ? 2160 : has(lower, "1080[pi]") ? 1080 : has(lower, "720p") ? 720
            : has(lower, "576p") ? 576 : has(lower, "480p|360p") ? 480 : nil
        let source: String? = has(lower, "cam|camrip|hdcam|ts|hdts|telesync|tc|hdtc|telecine|scr|screener|dvdscr|pre-?dvd") ? "cam"
            : has(lower, "remux|bdremux") ? "remux"
            : has(lower, "blu-?ray|bdrip|brrip|bd|bdmv|bd-?rip") ? "bluray"
            : has(lower, "web-?rip|web rip") ? "webrip"
            : has(lower, "web-?dl|web dl|web|amzn|nf|dsnp|hmax|atvp|cr") ? "webdl"
            : has(lower, "hdrip") ? "hdrip"
            : has(lower, "hdtv|pdtv|tvrip|dsr|satrip") ? "hdtv"
            : has(lower, "dvdrip|dvd|dvd5|dvd9|dvdr") ? "dvd" : nil
        let codec: String? = has(lower, #"hevc|x\.?265|h\.?\s?265"#) ? "HEVC" : has(lower, "av1") ? "AV1" : has(lower, #"avc|x\.?264|h\.?\s?264"#) ? "H.264"
            : has(lower, "xvid|divx") ? "XviD" : has(lower, "vp9") ? "VP9" : nil
        let vision = has(lower, "dv|dovi|dolby[ .]?vision")
        let hdr: String? = has(lower, #"hdr10\+|hdr10plus"#) ? "HDR10+" : has(lower, "hdr|hdr10") ? "HDR" : nil
        var languages: [String] = []
        if has(lower, "multi(?![-. ]?subs?)(?:[-. ]?audio)?|multi[-. ]?vf2?|multilang") { languages.append("MULTI") }
        if has(lower, "truefrench|vff") { languages.append("VFF") }
        if has(lower, "vfq") { languages.append("VFQ") }
        if !languages.contains(where: { $0 == "VFF" || $0 == "VFQ" }), has(lower, "french|vfi|vf") { languages.append("VF") }
        if has(lower, "vostfr|subfrench") { languages.append("VOSTFR") }

        return Release(
            title: title, year: year, alternate: alternate, season: season, episode: episode, lastEpisode: lastEpisode,
            absolute: absolute, pack: pack, resolution: resolution, source: source, codec: codec, tenBit: has(lower, "10-?bits?|hi10p?|main10"),
            hdr: vision ? (hdr.map { "DV \($0)" } ?? "DV") : hdr, languages: languages, group: group,
            repack: has(lower, "repack|proper|rerip"), fansub: fansub, half: half)
    }

    // MARK: Words

    /// « 1080p · WEB-DL · HEVC · MULTI · ToonsHub »: the group only when it is a fansub's, in brackets in front.
    static func qualityLine(_ release: Release) -> String {
        let resolution = release.resolution == 2160 ? "4K" : release.resolution.map { "\($0)p" } ?? ""
        let source = release.source == "cam" ? String(localized: "Theater cam") : release.source.flatMap { sourceLabels[$0] } ?? ""
        return ([resolution, source, release.codec ?? "", release.hdr ?? ""] + release.languages + [release.fansub ? release.group ?? "" : ""])
            .filter { !$0.isEmpty }.joined(separator: " · ")
    }

    private static func two(_ n: Int) -> String { n < 10 ? "0\(n)" : "\(n)" }

    /// « S06E05 », « S01E01-E03 », « Episode 5 », « S03E12.5 », « Complete season 2 », « All seasons », « Episodes 1–12 »; empty
    /// for a film.
    static func episodeLabel(_ release: Release) -> String {
        if let half = release.half {
            if release.absolute { return String(localized: "Episode \(half).5") }
            return "S\(release.season.map(two) ?? "null")E\(two(half)).5"
        }
        if release.pack {
            if let first = release.episode, let last = release.lastEpisode {
                if let season = release.season { return String(localized: "Season \(season), episodes \(first)–\(last)") }
                return String(localized: "Episodes \(first)–\(last)")
            }
            if let season = release.season { return String(localized: "Complete season \(season)") }
            return String(localized: "All seasons")
        }
        guard let episode = release.episode else { return "" }
        if release.absolute {
            if let last = release.lastEpisode, last != 0 { return String(localized: "Episodes \(episode)–\(last)") }
            return String(localized: "Episode \(episode)")
        }
        let season = release.season.map(two) ?? "null"
        let range = (release.lastEpisode ?? 0) > episode ? "-E\(two(release.lastEpisode ?? 0))" : ""
        return "S\(season)E\(two(episode))\(range)"
    }

    /// What a download is called on the screen: its title read from its name, and the name itself to show under it
    /// (nil when nothing more can be read).
    static func readable(_ name: String?) -> (title: String, full: String?) {
        let raw = (name ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let release = parse(raw)
        let label = episodeLabel(release)
        let year = release.year.map { label.isEmpty ? " (\($0))" : "" } ?? ""
        let title = ["\(release.title)\(year)", label].filter { !$0.isEmpty }.joined(separator: " · ")
        return !release.title.isEmpty && titleKey(title) != titleKey(raw) ? (title, raw) : (raw, nil)
    }

    /// Whether a site of TorLink counts who shares its results: SubsPlease never does.
    static func reportsHealth(_ source: String?) -> Bool { (source ?? "").lowercased() != "subsplease" }

    /// How good a result is for us, the higher the better.
    static func score(_ hit: Hit, _ release: Release? = nil, category: String? = nil) -> Int {
        let release = release ?? parse(hit.name)
        var score = (release.resolution.flatMap { resolutionScore[$0] } ?? 12) + (release.source.flatMap { sourceScore[$0] } ?? 5)
        score += max(0, release.languages.map { languageScore[$0] ?? 0 }.max() ?? 0)
        if release.repack { score += 1 }
        if release.codec == "AV1" { score -= 2 }
        if release.codec == "XviD" { score -= 4 }
        if release.tenBit, release.codec == "H.264" { score -= 4 }
        if release.hdr == "DV" { score -= 3 }
        let bytes = Double(max(0, hit.size ?? 0)), gib = 1_073_741_824.0
        let kind = hit.category ?? category
        let episodic = release.episode != nil || release.pack || kind == "tv" || kind == "anime"
        if bytes > 0, !release.pack, (release.resolution ?? 0) >= 1080, bytes < (episodic ? 0.25 : 1.2) * gib { score -= 4 }
        let seeders = max(0, hit.seeders ?? 0)
        if reportsHealth(hit.source) {
            if seeders == 0 { score -= 200 } else if seeders < 3 { score -= 6 }
        }
        return score
    }

    /// Under the best release: what makes it worth less, in a few words; empty when nothing does.
    static func warning(_ entry: Entry) -> String {
        if entry.release.source == "cam" { return String(localized: "Filmed in a movie theater: poor picture and sound.") }
        let seeders = max(0, entry.hit.seeders ?? 0)
        if reportsHealth(entry.hit.source) {
            if seeders == 0 { return String(localized: "Nobody is sharing this file, so it probably won’t download.") }
            if seeders < 3 { return String(localized: "Few seeders: the download will be slow.") }
        }
        return ""
    }

    // MARK: Grouping

    /// The results of a search gathered by title and episode (or season, or film and year), the best release of each
    /// put forward. A release giving another name in parentheses meets the releases under that name. Titles keep the
    /// order of their best-shared result; inside a title, whole seasons first, then the latest episodes. Hits of a
    /// search in every category meet only those of the same category.
    static func group(_ hits: [Hit], category: String? = nil) -> [Group] {
        let entries = hits.enumerated().map { order, hit in
            let release = parse(hit.name)
            return Entry(hit: hit, release: release, order: order, score: score(hit, release, category: category))
        }
        // A title known under another name: its other name points to it.
        var names: [String: String] = [:]
        for entry in entries {
            let own = titleKey(entry.release.title)
            if let alternate = entry.release.alternate, titleKey(alternate) != own { names[titleKey(alternate)] = own }
        }
        func showOf(_ release: Release) -> String { names[titleKey(release.title)] ?? titleKey(release.title) }
        func filmLike(_ release: Release) -> Bool { release.episode == nil && release.half == nil && !release.pack }
        func text(_ n: Int?) -> String { n.map(String.init) ?? "" }

        struct Bucket { var key: String; var show: String; var category: String?; var entries: [Entry] }
        var keys: [String] = []
        var groups: [String: Bucket] = [:]
        for entry in entries {
            let release = entry.release
            let show = showOf(release)
            let kind = entry.hit.category
            let rest = filmLike(release) ? "\(show)|\(text(release.year))"
                : release.pack ? "\(show)|pack|\(text(release.season))|\(text(release.episode))-\(text(release.lastEpisode))"
                : release.half != nil ? "\(show)|half|\(release.absolute ? "" : release.season.map(String.init) ?? "null")|\(text(release.half))"
                : "\(show)|e|\(release.absolute ? "" : release.season.map(String.init) ?? "null")|\(text(release.episode))"
            let key = (kind.map { "\($0)|" } ?? "") + rest
            if groups[key] == nil { keys.append(key); groups[key] = Bucket(key: key, show: show, category: kind, entries: []) }
            groups[key]?.entries.append(entry)
        }
        // An episode numbered without a season meets the same number of the same show with one, when only one season has it.
        for key in keys {
            guard let group = groups[key], let first = group.entries.first, first.release.absolute, !first.release.pack else { continue }
            let matches = keys.filter { other in
                guard other != key, let g = groups[other], let head = g.entries.first else { return false }
                return g.show == group.show && g.category == group.category && !head.release.absolute && !head.release.pack
                    && head.release.episode == first.release.episode && head.release.half == first.release.half
            }
            if matches.count == 1 {
                groups[matches[0]]?.entries.append(contentsOf: group.entries)
                groups[key] = nil
            }
        }
        func ranks(_ a: Entry, _ b: Entry) -> Bool {
            if a.score != b.score { return a.score > b.score }
            let (sa, sb) = (a.hit.seeders ?? 0, b.hit.seeders ?? 0)
            if sa != sb { return sa > sb }
            return a.order < b.order
        }
        var showOrder: [String: Int] = [:]
        for entry in entries where showOrder[showOf(entry.release)] == nil { showOrder[showOf(entry.release)] = entry.order }
        let made: [Group] = keys.compactMap { groups[$0] }.map { group in
            let sorted = group.entries.sorted(by: ranks)
            // The name of the group: the one of a release giving a season (« JoJos Bizarre Adventure »), else the best's.
            let named = sorted.first { !$0.release.absolute && titleKey($0.release.title) == group.show } ?? sorted[0]
            let shown = sorted.first { !$0.release.absolute && !$0.release.pack && ($0.release.episode != nil || $0.release.half != nil) } ?? sorted[0]
            let year = filmLike(named.release) ? named.release.year.map { " (\($0))" } ?? "" : ""
            return Group(key: group.key, show: group.show, title: "\(named.release.title)\(year)", label: episodeLabel(shown.release),
                         best: sorted[0], others: Array(sorted.dropFirst()), pack: shown.release.pack, release: shown.release, category: group.category)
        }
        return made.sorted { a, b in
            if a.show != b.show { return (showOrder[a.show] ?? 0) < (showOrder[b.show] ?? 0) }
            if a.pack != b.pack { return a.pack }
            if (a.release.season ?? 0) != (b.release.season ?? 0) { return (a.release.season ?? 0) > (b.release.season ?? 0) }
            // A half (« 07.5 ») between its two episodes.
            func place(_ release: Release) -> Int { (release.episode ?? release.half ?? 0) * 2 + (release.half == nil ? 0 : 1) }
            if place(a.release) != place(b.release) { return place(a.release) > place(b.release) }
            return a.best.order < b.best.order
        }
    }
}

/// NSRegularExpression (ICU, which has the look-behinds the rules of Mira web use) in a few words, with offsets in
/// UTF-16 units as JavaScript counts them.
enum Regex {
    struct Match {
        let result: NSTextCheckingResult
        let source: NSString
        var start: Int { result.range.location }
        func text(_ group: Int) -> String? {
            let range = result.range(at: group)
            return range.location == NSNotFound ? nil : source.substring(with: range)
        }
        func int(_ group: Int) -> Int? { text(group).flatMap { $0.isEmpty ? nil : Int($0) } }
        var before: String { source.substring(to: result.range.location) }
        var after: String { source.substring(from: result.range.location + result.range.length) }
    }

    nonisolated(unsafe) private static var cache: [String: NSRegularExpression] = [:]
    private static let lock = NSLock()

    private static func regex(_ pattern: String, _ options: NSRegularExpression.Options) -> NSRegularExpression {
        let key = "\(options.rawValue)|\(pattern)"
        lock.lock(); defer { lock.unlock() }
        if let made = cache[key] { return made }
        // The patterns are Mira's own: one that does not compile is a mistake of ours.
        let made = try! NSRegularExpression(pattern: pattern, options: options)
        cache[key] = made
        return made
    }

    static func first(_ pattern: String, in text: String, options: NSRegularExpression.Options = [.caseInsensitive]) -> Match? {
        let source = text as NSString
        return regex(pattern, options).firstMatch(in: text, range: NSRange(location: 0, length: source.length)).map { Match(result: $0, source: source) }
    }

    static func all(_ pattern: String, in text: String, options: NSRegularExpression.Options = [.caseInsensitive]) -> [Match] {
        let source = text as NSString
        return regex(pattern, options).matches(in: text, range: NSRange(location: 0, length: source.length)).map { Match(result: $0, source: source) }
    }

    static func test(_ pattern: String, _ text: String, options: NSRegularExpression.Options = [.caseInsensitive]) -> Bool {
        first(pattern, in: text, options: options) != nil
    }

    static func replace(_ pattern: String, in text: String, with template: String, options: NSRegularExpression.Options = [.caseInsensitive]) -> String {
        let source = text as NSString
        return regex(pattern, options).stringByReplacingMatches(in: text, range: NSRange(location: 0, length: source.length), withTemplate: NSRegularExpression.escapedTemplate(for: template))
    }

    static func replace(_ pattern: String, in text: String, options: NSRegularExpression.Options = [.caseInsensitive], _ by: (Match) -> String) -> String {
        let source = text as NSString
        var out = "", at = 0
        for m in regex(pattern, options).matches(in: text, range: NSRange(location: 0, length: source.length)) {
            out += source.substring(with: NSRange(location: at, length: m.range.location - at))
            out += by(Match(result: m, source: source))
            at = m.range.location + m.range.length
        }
        return out + source.substring(from: at)
    }
}
