import Foundation

/// A stream that no longer comes, as Mira web watches for it (stream.js stallWatch, stallStep): the bytes stop arriving
/// while Jellyfin answers (Wi-Fi that hiccups, a slow Tailscale relay, a conversion whose ffmpeg stopped), and Apple's
/// player waits for them forever with its spinner, with no error. Mira's own clock decides instead.
struct StallWatch: Equatable, Sendable {
    /// Played, then still for this long while it should play: the stream is stuck.
    static let stallLimit: TimeInterval = 15
    /// A stream opened, or a jump, with nothing played since for this long: stuck too (a conversion starts slowly).
    static let startLimit: TimeInterval = 45
    /// How far a stream opened again after a stall plays (seconds) before a new stall counts as a first one again.
    static let forgiven: Double = 30

    static func limit(moved: Bool) -> TimeInterval { moved ? stallLimit : startLimit }

    private(set) var since: Date?
    private(set) var at: Double = -1
    /// It played since it was opened or moved.
    private(set) var moved = false
    private(set) var seeking = false
    private(set) var stuck = false

    /// One look at the stream, twice a second. at: the player's time; expecting: it should play (not paused by anyone,
    /// the server not waited for); ready: it plays; seeking: a jump. Its time moving while ready is playing on; a jump
    /// starts the clock again, with nothing played from there; still for its limit, `stuck`.
    func next(now: Date, at: Double, expecting: Bool, ready: Bool, seeking: Bool) -> StallWatch {
        var look = StallWatch()
        look.at = at
        look.moved = moved
        look.seeking = seeking
        guard expecting else { return look }
        if seeking && !self.seeking {
            look.since = now
            look.moved = false
            return look
        }
        if at != self.at && ready && !seeking {
            look.moved = true
            return look
        }
        let since = self.since ?? now
        look.since = since
        look.stuck = now.timeIntervalSince(since) >= Self.limit(moved: moved)
        return look
    }

    enum Step: Equatable, Sendable {
        /// The server is silent: wait for it, then open the stream again where the title was.
        case wait
        /// It answers: open the stream again where the title is (Jellyfin may have stopped its conversion), once.
        case reopen
        /// Stuck again before it played on: « Can't play ».
        case fail
    }

    static func step(up: Bool, reopened: Bool) -> Step { !up ? .wait : reopened ? .fail : .reopen }

    /// A title that ends this far before its end did not end: the stream was cut (Windows and Mac do the same).
    static func cut(position: Int64, total: Int64) -> Bool { total > 0 && position < total / 10 * 9 }
}
