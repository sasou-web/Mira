import Foundation

/// The sleep timer (« Minuterie »), as Mira web, Windows and Mac have it (sleep.js, SleepTimer.cs; cases shared in
/// tests/fixtures/sleep.json): a pause after some minutes of the clock, or a stop at the end of the episode rather than
/// the next one. Chosen on a title's page (Apple's player has no room for Mira's menus); it carries on into the next
/// episode, and goes once it has done its work or the player closes.
struct SleepTimer: Equatable, Sendable {
    static let minutes = [15, 30, 60]

    enum Status: Equatable, Sendable {
        /// « Pause in 23 min »: the time left, as `duration` writes it.
        case pause(String)
        case endOfEpisode
    }

    /// The menu's choice: minutes, 0 for the end of the episode, nil when off.
    private(set) var chosen: Int?
    private var until: Date?

    /// The episode playing is the last: the next one does not start.
    var atEnd: Bool { chosen == 0 }

    mutating func choose(_ minutes: Int?, now: Date) {
        chosen = minutes
        until = minutes.flatMap { $0 > 0 ? now.addingTimeInterval(Double($0) * 60) : nil }
    }

    mutating func clear() { choose(nil, now: .now) }

    /// True once, when the minutes are over: the player pauses, and the timer is off.
    mutating func due(_ now: Date) -> Bool {
        guard let until, now >= until else { return false }
        clear()
        return true
    }

    func status(_ now: Date) -> Status? {
        if chosen == 0 { return .endOfEpisode }
        guard let until else { return nil }
        return .pause(Self.duration(max(1, Int((until.timeIntervalSince(now) / 60).rounded(.up)))))
    }

    /// « 15 min », « 1 h », « 1 h 30 », « 2 h 05 », the same in English and French.
    static func duration(_ minutes: Int) -> String {
        if minutes < 60 { return "\(minutes) min" }
        let rest = minutes % 60
        return rest == 0 ? "\(minutes / 60) h" : "\(minutes / 60) h " + (rest < 10 ? "0\(rest)" : "\(rest)")
    }
}
