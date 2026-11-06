import UIKit

/// « Are you still watching? », as Mira for Windows and Mac have it (StillWatching.cs; cases shared in
/// tests/fixtures/still-watching.json): after `episodes` episodes in a row with nobody touching the iPhone, the next one
/// does not start by itself; the player stops at the end of the one playing and asks, so a night asleep in front of a
/// series does not mark ten episodes watched. Never in a group. On by default (Settings → Playback).
struct StillWatching: Equatable, Sendable {
    static let episodes = 3

    private var starts: [Date] = []

    /// An episode or a film starts: by hand (Play, the next-episode button, « Keep watching ») a new run from this one;
    /// by itself (the countdown, the end of the previous episode) one more in the run.
    mutating func started(_ at: Date, byItself: Bool) {
        if !byItself { starts.removeAll() }
        starts.append(at)
    }

    /// The next episode would start by itself now: true when it should ask instead. `lastInput`: the last touch,
    /// button or Lock Screen command Mira saw (nil: none); an episode is untouched when it started after it.
    func asks(lastInput: Date?, enabled: Bool) -> Bool {
        guard enabled else { return false }
        return untouched(since: lastInput) >= Self.episodes
    }

    func untouched(since input: Date?) -> Int {
        guard let input else { return starts.count }
        return starts.filter { $0 >= input }.count
    }
}

/// The last time someone touched the iPhone in Mira: a recognizer on each window that never takes a touch, the Lock
/// Screen's and the headphones' commands (PlayerSession). iOS says nothing of touches outside the app.
@MainActor
enum TouchWatch {
    private(set) static var last: Date?

    static func touched() { last = .now }

    /// On every window of the app, once: touches anywhere, the player and its menus included.
    static func install() {
        for case let scene as UIWindowScene in UIApplication.shared.connectedScenes {
            for window in scene.windows where !(window.gestureRecognizers ?? []).contains(where: { $0 is Recognizer }) {
                window.addGestureRecognizer(Recognizer(target: nil, action: nil))
            }
        }
    }

    /// Sees each touch begin, then steps aside: it never recognizes, cancels nor delays anything.
    private final class Recognizer: UIGestureRecognizer, UIGestureRecognizerDelegate {
        override init(target: Any?, action: Selector?) {
            super.init(target: target, action: action)
            cancelsTouchesInView = false
            delaysTouchesBegan = false
            delaysTouchesEnded = false
            delegate = self
        }

        override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent) {
            TouchWatch.touched()
            state = .failed
        }

        override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent) {
            TouchWatch.touched()
            super.pressesBegan(presses, with: event)
        }

        func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool { true }
    }
}
