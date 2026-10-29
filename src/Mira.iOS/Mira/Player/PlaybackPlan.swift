import Foundation

/// What the player asks Jellyfin for and what it does when a stream fails, as plain functions of their inputs
/// (stream.js and tracks.js of Mira web), so that the tests read them without a player.
enum PlaybackPlan {
    /// Full quality: the file as it is whenever Jellyfin can send it.
    static let fullBitrate = 120_000_000
    /// Asked when the connection could not be measured.
    static let unmeasuredBitrate = 8_000_000
    /// A connection that holds this many times the file's bitrate plays the file as it is.
    static let fullMargin = 1.5

    /// The bitrate known without measuring: the one chosen (« max », or bits per second), full quality at home on
    /// « auto », or what a measure younger than an hour allows for this file; nil when the connection must be measured
    /// (knownBitrate of Mira web, StreamQuality.KnownBitrate of Windows and Mac: tests/fixtures/bitrate.json).
    static func knownBitrate(_ choice: String, atHome: Bool, measure: (at: Date, speed: Double)?, now: Date = Date(), fileBitrate: Int) -> Int? {
        if let chosen = Int(choice), chosen > 0 { return chosen }
        if choice == "max" || atHome { return fullBitrate }
        if let measure, now.timeIntervalSince(measure.at) < 3600 { return autoBitrate(speed: measure.speed, fileBitrate: fileBitrate) }
        return nil
    }

    /// What a measured speed (bits per second) allows: the file as it is when the connection holds about one and a
    /// half times its bitrate, else 70 % of the speed, between 1.5 and 40 Mb/s (autoBitrate of Mira web).
    static func autoBitrate(speed: Double, fileBitrate: Int) -> Int {
        if fileBitrate > 0 && speed >= Double(fileBitrate) * fullMargin { return fullBitrate }
        return max(1_500_000, min(40_000_000, Int((speed * 0.7).rounded())))
    }

    // MARK: The cascade
    // Stage 0: as Jellyfin decides (the file as it is when it can, its video or audio copied into HLS); 1: its video
    // converted; 2: everything converted (PlaybackProfile.lastResort). Kept for the title, 0 for the next one.

    static let lastStage = 2

    struct StageRequest: Equatable {
        var lastResort: Bool
        var directPlay: Bool
        var directStream: Bool
        var videoCopy: Bool
        var audioCopy: Bool
    }

    static func stageRequest(_ stage: Int) -> StageRequest {
        StageRequest(lastResort: stage >= 2, directPlay: stage == 0, directStream: stage == 0, videoCopy: stage == 0, audioCopy: stage < 2)
    }

    /// The stage after this one; nil once everything is converted.
    static func nextStage(_ stage: Int) -> Int? { stage >= lastStage ? nil : stage + 1 }

    /// Jellyfin offers neither the file nor a conversion at stage 0 (an account it does not let convert, a file above
    /// the quality asked): the bitrate to ask again with, the file as it is rather than nothing.
    static func uncappedBitrate(_ source: MediaSource, stage: Int, bitrate: Int) -> Int? {
        stage == 0 && source.supportsDirectPlay != true && source.transcodingUrl == nil && bitrate < fullBitrate ? fullBitrate : nil
    }

    struct Stream: Equatable {
        /// The address under the server's, before it is signed.
        var path: String
        var playMethod: String
        var hls: Bool
        /// Text subtitles come in the HLS stream itself.
        var inband: Bool
    }

    /// The stream to play from PlaybackInfo's media source: the file as it is at stage 0 when Jellyfin allows it (MP4
    /// first among its containers), else its conversion, with every text subtitle in the HLS stream for Apple's menu
    /// (appleStream). `miraHls`: Jellyfin's Mira extension answers, its playlists time those subtitles right on fMP4.
    static func stream(_ source: MediaSource, itemId: String, deviceId: String, stage: Int, subtitleIndex: Int?, miraHls: Bool) -> Stream? {
        if source.supportsDirectPlay == true, source.transcodingUrl == nil, stage == 0 {
            let containers = (source.container ?? "mp4").split(separator: ",").map(String.init)
            let container = containers.first { $0 == "mp4" } ?? containers.first ?? "mp4"
            var path = "Videos/\(itemId)/stream.\(container)?Static=true&mediaSourceId=\(encode(source.id ?? itemId))&deviceId=\(encode(deviceId))"
            if let tag = source.eTag, !tag.isEmpty { path += "&Tag=\(encode(tag))" }
            return Stream(path: path, playMethod: "DirectPlay", hls: false, inband: false)
        }
        guard let address = source.transcodingUrl else { return nil }
        let hls = source.transcodingSubProtocol?.lowercased() == "hls" || address.range(of: ".m3u8", options: .caseInsensitive) != nil
        var stream = Stream(path: address, playMethod: copiesVideo(address) ? "DirectStream" : "Transcode", hls: hls, inband: false)
        if hls { (stream.path, stream.inband) = appleStream(address, source: source, subtitleIndex: subtitleIndex, miraHls: miraHls) }
        return stream
    }

    /// Why Jellyfin converts, as its address says (TranscodeReasons): none of these, and the video is copied as it is.
    static func copiesVideo(_ address: String) -> Bool {
        if address.range(of: "allowVideoStreamCopy=false", options: .caseInsensitive) != nil { return false }
        let reasons = URLComponents(string: "http://x/" + address.drop { $0 == "/" })?.queryItems?.first { $0.name == "TranscodeReasons" }?.value ?? ""
        return !["Video", "RefFrames", "ContainerBitrate", "SubtitleCodec"].contains { reasons.contains($0) }
    }

    /// The address of a conversion for Apple's player: every text subtitle in the HLS stream, the chosen one first.
    /// Jellyfin lists them there only with SubtitleMethod=Hls; it writes SubtitleMethod=Encode even with none chosen.
    /// Never for pictures: Jellyfin draws those into the video. Jellyfin times these subtitles for MPEG-TS segments
    /// (10 s late on fMP4 in Apple's player): fMP4 goes through Mira's extension, which serves the same playlists with
    /// the subtitles in time and with Mira's names.
    static func appleStream(_ address: String, source: MediaSource, subtitleIndex: Int?, miraHls: Bool) -> (String, Bool) {
        let chosen = source.subtitles.first { $0.index == subtitleIndex }
        if source.textSubtitles.isEmpty || (chosen != nil && chosen?.isTextSubtitleStream != true) { return (address, false) }
        var next = address.replacingOccurrences(of: "&SubtitleMethod=[^&]*", with: "", options: [.regularExpression, .caseInsensitive])
        if let chosen, next.range(of: "[?&]SubtitleStreamIndex=", options: [.regularExpression, .caseInsensitive]) == nil {
            next += "&SubtitleStreamIndex=\(chosen.index)"
        }
        next += (next.contains("?") ? "&" : "?") + "SubtitleMethod=Hls"
        if miraHls, next.range(of: "[?&]SegmentContainer=mp4", options: [.regularExpression, .caseInsensitive]) != nil {
            next = next.replacingOccurrences(of: "^/?videos/([0-9a-fA-F-]+)/master\\.m3u8", with: "Mira/hls/$1/master.m3u8", options: [.regularExpression, .caseInsensitive])
            let names = TrackNames.menuNames(source).sorted { $0.key < $1.key }.map { "\($0.key):\($0.value.replacingOccurrences(of: "|", with: "/"))" }.joined(separator: "|")
            next += "&MiraNames=\(encode(names))"
        }
        return (next, true)
    }

    // MARK: Where a title starts

    /// Under this much watched (seconds), a title resumes exactly where it was left (resume.json).
    static let resumeBackFrom: Double = 30

    /// « Resume a little earlier »: `back` seconds before where the title was left, to pick up the thread rather than
    /// land mid-sentence; exactly there under 30 s (resumeBack of Mira web, PlaybackSession.ResumeBack of Core).
    static func resumeBack(_ position: Double, back: Double) -> Double {
        back > 0 && position >= resumeBackFrom ? max(0, position - back) : position
    }

    /// Where a title starts (ticks): where it was left, `back` seconds before, unless asked from the start, resuming
    /// is off, or it was nearly done.
    static func startTicks(_ item: Item, fromStart: Bool, resume: Bool = true, back: Int = 0) -> Int64 {
        let progress = ItemText.progress(item)
        guard !fromStart, resume, progress > 0, progress < 0.95, let position = item.userData?.playbackPositionTicks else { return 0 }
        let seconds = Double(position) / Double(ItemText.ticksPerSecond)
        let at = resumeBack(seconds, back: Double(back))
        return at == seconds ? position : Int64((at * Double(ItemText.ticksPerSecond)).rounded())
    }

    private static func encode(_ text: String) -> String {
        text.addingPercentEncoding(withAllowedCharacters: CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? text
    }
}

/// The names of a file's tracks, in Mira's words (trackText of Mira web): its title, else its language, with what tells
/// two apart.
enum TrackNames {
    /// The ISO 639-2 « bibliographic » codes Jellyfin also writes (fre, ger…), as the codes Apple reads.
    private static let bibliographic = [
        "fre": "fr", "ger": "de", "chi": "zh", "dut": "nl", "cze": "cs", "gre": "el", "per": "fa", "rum": "ro", "slo": "sk",
        "alb": "sq", "arm": "hy", "baq": "eu", "bur": "my", "geo": "ka", "ice": "is", "mac": "mk", "mao": "mi", "may": "ms",
        "tib": "bo", "wel": "cy",
    ]

    static func language(_ code: String?, locale: Locale = .current) -> String? {
        guard var code = code?.trimmingCharacters(in: .whitespaces).lowercased(), !code.isEmpty, code != "und", code != "mis", code != "zxx" else { return nil }
        code = String(code.split(whereSeparator: { $0 == "-" || $0 == "_" }).first ?? "")
        code = bibliographic[code] ?? code
        // ISO 639-2 (jpn, eng) as the two letters Apple names best.
        if code.count == 3, let short = Locale.LanguageCode(code).identifier(.alpha2) { code = short }
        return locale.localizedString(forLanguageCode: code).map { $0.prefix(1).uppercased() + $0.dropFirst() }
    }

    private static let codecs = [
        "aac": "AAC", "ac3": "Dolby Digital", "eac3": "Dolby Digital Plus", "truehd": "Dolby TrueHD", "dts": "DTS", "flac": "FLAC",
        "mp3": "MP3", "opus": "Opus", "vorbis": "Vorbis", "alac": "ALAC", "pcm_s16le": "PCM", "pcm_s24le": "PCM",
        "subrip": "SRT", "srt": "SRT", "ass": "ASS", "ssa": "SSA", "webvtt": "WebVTT", "vtt": "WebVTT", "mov_text": "MP4",
        "pgssub": "PGS", "hdmv_pgs_subtitle": "PGS", "dvdsub": "DVD", "dvd_subtitle": "DVD", "dvbsub": "DVB",
    ]

    static func channels(_ count: Int) -> String {
        switch count {
        case 1: String(localized: "Mono")
        case 2: String(localized: "Stereo")
        case 6: "5.1"
        case 8: "7.1"
        default: String(localized: "\(count) channels")
        }
    }

    /// « English », « Commentary (English · AAC · Stereo) »: the label and its details.
    static func text(_ stream: MediaStream, number: Int, locale: Locale = .current) -> (label: String, details: String) {
        let name = language(stream.language, locale: locale)
        let title = (stream.title ?? "").trimmingCharacters(in: .whitespaces)
        let label = !title.isEmpty ? title : name ?? String(localized: "Track \(number)")
        var details: [String] = []
        if !title.isEmpty, let name, !title.localizedCaseInsensitiveContains(name) { details.append(name) }
        if let codec = stream.codec.flatMap({ codecs[$0.lowercased()] }) { details.append(codec) }
        if stream.type == "Audio", let count = stream.channels, count > 0 { details.append(channels(count)) }
        if stream.isForced == true { details.append(String(localized: "forced")) }
        if stream.isExternal == true { details.append(String(localized: "external file")) }
        return (label.prefix(1).uppercased() + label.dropFirst(), details.joined(separator: " · "))
    }

    /// A track's name in a list of its kind: with its details when another one has the same label, numbered when they
    /// still match (« French », « French 2 »). Numbered « Track n » by their place among `all` (the list they come from).
    static func names(_ streams: [MediaStream], among all: [MediaStream]? = nil, locale: Locale = .current) -> [Int: String] {
        let texts = streams.enumerated().map { offset, stream in
            (stream: stream, text: text(stream, number: ((all ?? streams).firstIndex(of: stream) ?? offset) + 1, locale: locale))
        }
        var names: [Int: String] = [:], counts: [String: Int] = [:]
        for entry in texts {
            let twin = texts.contains { $0.stream.index != entry.stream.index && $0.text.label == entry.text.label }
            let name = twin && !entry.text.details.isEmpty ? "\(entry.text.label) (\(entry.text.details))" : entry.text.label
            let count = counts[name, default: 0] + 1
            counts[name] = count
            names[entry.stream.index] = count > 1 ? "\(name) \(count)" : name
        }
        return names
    }

    /// Each text subtitle's name in Apple's menu, by Jellyfin index (menuNames of Mira web).
    static func menuNames(_ source: MediaSource, locale: Locale = .current) -> [Int: String] {
        names(source.textSubtitles, among: source.subtitles, locale: locale)
    }

    /// The track of the same language in another episode's file (and forced or not alike, for subtitles), to keep the
    /// languages chosen from one episode to the next; nil when it has none.
    static func same(_ stream: MediaStream?, in streams: [MediaStream]) -> MediaStream? {
        guard let stream, let language = stream.language?.lowercased(), !language.isEmpty else { return nil }
        let alike = streams.filter { $0.language?.lowercased() == language }
        return alike.first { ($0.isForced ?? false) == (stream.isForced ?? false) && ($0.title ?? "") == (stream.title ?? "") }
            ?? alike.first { ($0.isForced ?? false) == (stream.isForced ?? false) } ?? alike.first
    }
}
