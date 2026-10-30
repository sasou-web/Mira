import Foundation

/// A text subtitle as Mira draws it itself, when the person shifts it (« Subtitle delay »): Jellyfin's WebVTT of the
/// whole track (Videos/{id}/{source}/Subtitles/{index}/0/Stream.vtt), its lines by time. Apple's player has no delay
/// of its own for the subtitles it draws.
struct SubtitleCues: Sendable, Equatable {
    struct Cue: Sendable, Equatable {
        let start: Double
        let end: Double
        let text: String
    }

    let cues: [Cue]

    /// The lines of a WebVTT file (SRT's commas read too), tags taken out, sorted by their start.
    init(webVTT: String) {
        var cues: [Cue] = []
        let blocks = webVTT.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n").components(separatedBy: "\n\n")
        for block in blocks {
            let lines = block.split(separator: "\n", omittingEmptySubsequences: true).map(String.init)
            guard let at = lines.firstIndex(where: { $0.contains("-->") }) else { continue }
            let times = lines[at].components(separatedBy: "-->")
            guard times.count == 2, let start = Self.seconds(times[0]), let end = Self.seconds(times[1]), end > start else { continue }
            let text = lines[(at + 1)...].map(Self.plain).filter { !$0.isEmpty }.joined(separator: "\n")
            if !text.isEmpty { cues.append(Cue(start: start, end: end, text: text)) }
        }
        self.cues = cues.sorted { $0.start < $1.start }
    }

    /// What shows at this time of the video: the lines that hold it, in their order.
    func text(at time: Double) -> String? {
        // The last line starting at or before `time`, then back over those still on.
        var low = 0, high = cues.count
        while low < high {
            let middle = (low + high) / 2
            if cues[middle].start <= time { low = middle + 1 } else { high = middle }
        }
        var shown: [String] = []
        var index = low - 1
        while index >= 0, index >= low - 8 {
            let cue = cues[index]
            if cue.end > time { shown.append(cue.text) }
            index -= 1
        }
        return shown.isEmpty ? nil : shown.reversed().joined(separator: "\n")
    }

    /// « 00:01:02.500 », « 01:02.500 », « 00:01:02,500 », followed or not by cue settings.
    static func seconds(_ text: String) -> Double? {
        let stamp = text.trimmingCharacters(in: .whitespaces).split(separator: " ").first.map(String.init) ?? ""
        let parts = stamp.replacingOccurrences(of: ",", with: ".").split(separator: ":")
        guard (2...3).contains(parts.count) else { return nil }
        var total = 0.0
        for part in parts {
            guard let value = Double(part) else { return nil }
            total = total * 60 + value
        }
        return total
    }

    /// A line without its tags (<i>, <c.yellow>, {\an8}) and with its entities read.
    static func plain(_ line: String) -> String {
        line.replacingOccurrences(of: "<[^>]*>", with: "", options: .regularExpression)
            .replacingOccurrences(of: "\\{\\\\[^}]*\\}", with: "", options: .regularExpression)
            .replacingOccurrences(of: "&lt;", with: "<").replacingOccurrences(of: "&gt;", with: ">")
            .replacingOccurrences(of: "&nbsp;", with: " ").replacingOccurrences(of: "&amp;", with: "&")
            .trimmingCharacters(in: .whitespaces)
    }

    /// « +0.5 s », « −1.0 s », « 0.0 s »: a delay as the menu says it.
    static func delayName(_ seconds: Double) -> String {
        let value = abs(seconds).formatted(.number.precision(.fractionLength(1)))
        return seconds > 0.04 ? "+\(value) s" : seconds < -0.04 ? "−\(value) s" : "\(value) s"
    }
}
