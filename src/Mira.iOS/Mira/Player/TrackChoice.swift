import Foundation

/// The audio and subtitle tracks a title starts with, as Mira web chooses them (chooseTracks and trackMemory of
/// tracks.js): the tracks last chosen for its series (same languages), then the language orders of Settings, then
/// Jellyfin's own choice. Subtitles in the language of the audio stay off, except forced ones.
enum TrackChoice {
    /// The languages Mira knows by their codes: one key per language, whatever its code (« fre », « fra », « fr »).
    private static let codes: [String: String] = {
        let languages = [
            "ja": "jpn ja", "fr": "fra fre fr", "en": "eng en", "de": "deu ger de", "es": "spa es", "it": "ita it", "pt": "por pt",
            "ru": "rus ru", "ko": "kor ko", "zh": "zho chi zh", "ar": "ara ar", "hi": "hin hi", "pl": "pol pl", "nl": "nld dut nl",
            "sv": "swe sv", "no": "nor nob no nb", "da": "dan da", "fi": "fin fi", "tr": "tur tr", "he": "heb he", "th": "tha th",
            "vi": "vie vi", "id": "ind id", "uk": "ukr uk", "cs": "ces cze cs", "hu": "hun hu", "el": "ell gre el", "ro": "ron rum ro",
            "ca": "cat ca", "ms": "msa may ms", "fil": "fil", "tl": "tgl",
        ]
        var map: [String: String] = [:]
        for (key, list) in languages { for code in list.split(separator: " ") { map[String(code)] = key } }
        return map
    }()

    static func key(_ code: String?) -> String? {
        let main = (code ?? "").trimmingCharacters(in: .whitespaces).split(whereSeparator: { $0 == "-" || $0 == "_" }).first.map { String($0).lowercased() } ?? ""
        guard !main.isEmpty, !["und", "zxx", "mis", "mul"].contains(main) else { return nil }
        return codes[main] ?? main
    }

    static func same(_ a: String?, _ b: String?) -> Bool {
        guard let key = key(a) else { return false }
        return key == self.key(b)
    }

    private static func rank(_ code: String?, _ order: String) -> Int? {
        guard let key = key(code) else { return nil }
        return order.split(separator: ",").firstIndex { self.key(String($0)) == key }
    }

    /// The best stream in an order: its language first, then the one the file marks as default, then the file's order.
    private static func best(_ streams: [MediaStream], _ order: String) -> MediaStream? {
        streams.enumerated().compactMap { offset, stream in rank(stream.language, order).map { (stream, offset, $0) } }
            .min { a, b in
                if a.2 != b.2 { return a.2 < b.2 }
                let da = a.0.isDefault == true, db = b.0.isDefault == true
                if da != db { return da }
                return a.1 < b.1
            }?.0
    }

    /// Jellyfin's indexes of the audio and subtitles to start with (-1: no subtitles). `memory` is the series' last
    /// choice (AccountChoices.track).
    static func choose(_ source: MediaSource?, memory: JSONValue?, audioOrder: String, subtitleOrder: String) -> (audio: Int?, subtitle: Int) {
        let audios = source?.audio ?? [], subtitles = source?.subtitles ?? []
        let fallback = audios.first { $0.index == source?.defaultAudioStreamIndex } ?? audios.first
        let remembered = memory?.object
        let audio = remembered?["audio"]?.string.flatMap { language in audios.first { same($0.language, language) } }
            ?? best(audios, audioOrder) ?? fallback
        // Text before pictures: pictures need a conversion that draws them into the video.
        let readable = subtitles.enumerated().sorted { a, b in
            let ta = a.element.isTextSubtitleStream == true, tb = b.element.isTextSubtitleStream == true
            return ta != tb ? ta : a.offset < b.offset
        }.map(\.element)
        func forced(in language: String?) -> MediaStream? { readable.first { $0.isForced == true && same($0.language, language) } }

        var subtitle: Int?
        if let remembered {
            if remembered["subtitles"]?.bool != true {
                subtitle = -1
            } else {
                let language = remembered["subtitle"]?.string
                let wantForced = remembered["forced"]?.bool == true
                subtitle = (readable.first { same($0.language, language) && ($0.isForced == true) == wantForced } ?? readable.first { same($0.language, language) })?.index
            }
        }
        if subtitle == nil, let audio, key(audio.language) != nil {
            let first = subtitleOrder.split(separator: ",").first.map(String.init)
            if same(audio.language, first) {
                subtitle = forced(in: audio.language)?.index ?? -1
            } else {
                subtitle = (best(readable.filter { $0.isForced != true }, subtitleOrder) ?? forced(in: audio.language))?.index
            }
        }
        return (audio?.index, subtitle ?? source?.defaultSubtitleStreamIndex ?? -1)
    }

    /// What a choice of tracks leaves for the next episodes of the series: languages and names, not indexes.
    static func memory(_ source: MediaSource?, audio: Int?, subtitle: Int?) -> JSONValue {
        let streams = source?.mediaStreams ?? []
        let a = streams.first { $0.type == "Audio" && $0.index == audio }
        let s = streams.first { $0.type == "Subtitle" && $0.index == subtitle }
        func text(_ value: String?) -> JSONValue { value.flatMap { $0.isEmpty ? nil : JSONValue.string($0) } ?? .null }
        return AccountChoices.track(.object([
            "audio": text(a?.language), "audioTitle": text(a?.title), "subtitles": .bool(s != nil),
            "subtitle": text(s?.language), "subtitleTitle": text(s?.title), "forced": .bool(s?.isForced == true),
        ]))
    }
}
