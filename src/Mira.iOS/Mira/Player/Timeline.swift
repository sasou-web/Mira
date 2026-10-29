import Foundation

/// A passage of a title: Jellyfin's segment (MediaSegments), or one the file's chapters name. Times in seconds.
struct Passage: Sendable, Hashable {
    /// Jellyfin's segment type: Intro, Recap, Preview, Commercial, Outro, Unknown.
    let type: String
    let start: Double
    let end: Double
}

/// The title's timeline in Apple's player: the passages to skip, when the next episode comes. The rules of Mira web
/// (player/timeline.js) and of Windows and Mac (PlaybackMarkers in Core), with the cases of tests/fixtures/chapters.json.
enum Timeline {
    /// Seconds the next episode is offered before it starts by itself.
    static let countdown: Double = 10
    /// Kinds with a button over them; intros and recaps are skipped by themselves when Settings say so.
    static let skippableKinds = ["Intro", "Recap", "Preview", "Commercial"]
    static let autoSkipKinds = ["Intro", "Recap"]
    /// Shorter passages are noise; a chapter longer than the second is no opening, recap or preview, whatever its title.
    static let minPassage: Double = 3
    static let maxShortPassage: Double = 360

    /// The button over a passage, by its kind.
    static func skipLabel(_ type: String) -> String? {
        switch type {
        case "Intro": String(localized: "Skip intro")
        case "Recap": String(localized: "Skip recap")
        case "Preview": String(localized: "Skip preview")
        case "Commercial": String(localized: "Skip ad")
        default: nil
        }
    }

    /// The passage that can be skipped at this point (until its last second), if any.
    static func skippable(_ passages: [Passage], at: Double) -> Passage? {
        passages.first { skippableKinds.contains($0.type) && at >= $0.start && at < $0.end - 1 }
    }

    /// Skipped by itself (intro, recap) when Settings say so, once: going back into it plays it. One of the same kind
    /// over a passage already skipped is that passage.
    static func skipsItself(_ passage: Passage?, autoSkip: Bool, skipped: Set<Passage>) -> Bool {
        guard let passage, autoSkip, autoSkipKinds.contains(passage.type) else { return false }
        return !skipped.contains { $0.type == passage.type && $0.start < passage.end && passage.start < $0.end }
    }

    /// Where the next episode is offered: from end credits that only credits or a preview follow, else over the last
    /// seconds, so that nothing of the end is cut.
    static func nextFrom(_ passages: [Passage], end: Double) -> Double {
        creditsFrom(passages, end: end) ?? end - countdown
    }

    static func creditsFrom(_ passages: [Passage], end: Double) -> Double? {
        passages.first { $0.type == "Outro" && endsPlayback(passages, $0, end: end) }?.start
    }

    /// When the next episode starts by itself: a countdown into such credits; nil when it waits for the end of the
    /// title (no such credits, too short), and for a title started in its credits (the person came back to them).
    static func nativeNextAt(_ passages: [Passage], end: Double, startedAt: Double = 0) -> Double? {
        guard let from = creditsFrom(passages, end: end), startedAt < from - 1, from + countdown < end - 1 else { return nil }
        return from + countdown
    }

    private static func endsPlayback(_ passages: [Passage], _ passage: Passage, end: Double) -> Bool {
        var last = passage.end
        for p in passages.sorted(by: { $0.start < $1.start })
        where p.start >= passage.start && p.start <= last + 2 && (p.type == "Outro" || p.type == "Preview") {
            last = max(last, p.end)
        }
        return end - last < 5
    }

    // MARK: Chapters

    // .NET's \b and \d know every alphabet and digit: so do these, with the joiners .NET's \b counts as letters.
    private static let word = "[\\p{L}\\p{Nd}\\p{Pc}\\u200C\\u200D]"
    private static let recap = "recap|recapitulatif|previously|precedemment|resume de l[^\\n]episode precedent"
    private static let preview = "preview|next episode|prochain episode|apercu|bande annonce|yokoku"
    private static let intro = "opening|intro|introduction|ouverture|theme song|op\\s?\\p{Nd}{0,2}|generique (?:de )?(?:debut|d[^\\n]ouverture)"
    private static let outro = "ending|outro|credits|end credits|ed\\s?\\p{Nd}{0,2}|generique (?:de )?fin"
    private static let generic = "generique"

    private static func plain(_ text: String) -> String {
        let marks = text.decomposedStringWithCanonicalMapping.unicodeScalars.filter { $0.properties.generalCategory != .nonspacingMark }
        return String(String.UnicodeScalarView(marks)).replacingOccurrences(of: "[_\\-’]", with: " ", options: .regularExpression).lowercased()
    }

    /// One of the words, not inside another word (a chapter title is short: compiled each time).
    private static func finds(_ alternatives: String, _ text: String) -> Bool {
        guard let expression = try? NSRegularExpression(pattern: "(?<!\(word))(?:\(alternatives))(?!\(word))") else { return false }
        return expression.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil
    }

    /// The kind of passage a chapter title names; a bare « Générique » is the opening before the middle, the credits after.
    static func chapterKind(_ title: String?, start: Double = 0, duration: Double = 0) -> String? {
        guard let title, !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        let text = plain(title)
        if finds(recap, text) { return "Recap" }
        if finds(preview, text) { return "Preview" }
        if finds(intro, text) { return "Intro" }
        if finds(outro, text) { return "Outro" }
        // « Avant-générique » is the cold open: part of the story, never skipped.
        if finds(generic, text) && !text.contains("avant generique") { return duration > 0 && start > duration / 2 ? "Outro" : "Intro" }
        return nil
    }

    /// Jellyfin's chapters as passages.
    static func chapterPassages(_ chapters: [Chapter]?, totalTicks: Int64?) -> [Passage] {
        let ordered = (chapters ?? []).compactMap { c in c.startPositionTicks.flatMap { $0 >= 0 ? (c, $0) : nil } }.sorted { $0.1 < $1.1 }
        // Without the title's length, the last chapter has no end.
        let duration = totalTicks.map(seconds) ?? 0
        var passages: [Passage] = []
        for (i, (chapter, startTicks)) in ordered.enumerated() {
            let start = seconds(startTicks)
            guard let endTicks = i + 1 < ordered.count ? ordered[i + 1].1 : totalTicks else { continue }
            let end = seconds(endTicks)
            guard end - start >= minPassage, let kind = chapterKind(chapter.name, start: start, duration: duration),
                  kind == "Outro" || end - start <= maxShortPassage else { continue }
            passages.append(Passage(type: kind, start: start, end: end))
        }
        return passages
    }

    /// Jellyfin's segments are authoritative, those of 3 s or more: a chapter's passage only fills a kind and a time
    /// they leave empty.
    static func withChapters(_ server: [Passage], _ fromChapters: [Passage]) -> [Passage] {
        let kept = server.filter { $0.start >= 0 && $0.end - $0.start >= minPassage }
        let filled = fromChapters.filter { c in kept.allSatisfy { $0.type != c.type && (c.end <= $0.start || c.start >= $0.end) } }
        return (kept + filled).sorted { $0.start < $1.start }
    }

    static func seconds(_ ticks: Int64) -> Double { Double(ticks) / Double(ItemText.ticksPerSecond) }
}

struct MediaSegment: Codable, Sendable {
    var type: String?
    var startTicks: Int64?
    var endTicks: Int64?
}

private struct MediaSegments: Codable, Sendable {
    var items: [MediaSegment]?
}

extension JellyfinClient {
    /// The title's passages marked by Jellyfin (10.10 and later; none before, or when nothing marked them).
    func passages(_ itemId: String) async -> [Passage] {
        guard let list = try? await get("MediaSegments/\(itemId)", [:], as: MediaSegments.self) else { return [] }
        return (list.items ?? []).compactMap { s in
            guard let type = s.type, let start = s.startTicks, let end = s.endTicks else { return nil }
            return Passage(type: type, start: Timeline.seconds(start), end: Timeline.seconds(end))
        }
    }
}
