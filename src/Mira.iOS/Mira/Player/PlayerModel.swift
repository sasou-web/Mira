import Foundation
import Observation
import UIKit

/// What Mira's player asks of the title playing (PlayerSession): the controls and gestures call these.
@MainActor
protocol PlayerActions: AnyObject {
    func togglePlay()
    func skip(_ seconds: Double)
    func seek(to seconds: Double)
    /// The finger is on the timeline (or sweeps the picture): the picture follows, the group hears of it once it stops.
    func scrubbing(_ active: Bool)
    func selectAudio(_ index: Int)
    func selectSubtitles(_ index: Int)
    func setSpeed(_ speed: Float)
    /// Held down on the picture: twice as fast while the finger stays.
    func boost(_ on: Bool)
    func setQuality(_ quality: String)
    func setSleep(_ minutes: Int?)
    func passageTapped()
    func playNext()
    func pictureInPicture()
    func minimize()
    func close()
    func leaveGroup()
}

/// One track in the player's menus: its Jellyfin index and its name in Mira's words.
struct PlayerTrack: Identifiable, Hashable, Sendable {
    let index: Int
    let name: String
    var id: Int { index }
}

/// Everything Mira's player draws over the picture, and what the gestures on it have under way. The session
/// (PlayerSession) fills what plays; the controls (PlayerChrome) and the gestures (PlayerController) read and change
/// the rest.
@MainActor @Observable
final class PlayerModel {
    // MARK: What plays

    var title = ""
    var subtitle: String?
    /// Where the title is, in seconds; where the finger is while it moves on the timeline.
    var time: Double = 0
    var duration: Double = 0
    var buffered: Double = 0
    /// Playing, or about to (the person has not paused it).
    var playing = false
    /// The picture waits for its data: a spinner where the play button is.
    var loading = true
    /// Chapters' starts, drawn as gaps in the timeline, and their names under the thumbnail.
    var chapters: [(start: Double, name: String?)] = []
    /// « Skip intro », « Next episode »: over the picture while it applies.
    var passage: String?
    var passageIsNext = false
    /// How far the countdown into the next episode is, 0 to 1, while it counts.
    var nextCountdown: Double?
    /// The group's line: « Waiting for Marc… », someone who came or went.
    var groupLine: String?
    /// Connection to Jellyfin lost: said over the picture while Mira waits for it.
    var waitingForServer = false
    /// Playing on a television (AirPlay): its name, said where the picture was.
    var externalName: String?
    /// The episode after this one, offered at the bottom.
    var hasNext = false

    // MARK: The controls

    private(set) var controlsShown = true
    var locked = false {
        didSet { if locked { hideControls() } }
    }
    /// The padlock shown for a moment over locked controls, to unlock them.
    var lockHint = false
    /// The finger moves on the timeline: the thumbnail over it, the time it would go to.
    var scrubbing = false
    var scrubTime: Double = 0 {
        didSet { if scrubbing { onScrub?(scrubTime) } }
    }
    var preview: UIImage?
    var previewSize = CGSize(width: 16, height: 9)
    /// Brightness or volume, as the finger sweeps one side of the picture: 0 to 1.
    var level: Level?
    /// The circles of a double tap: which side, how many seconds so far.
    var skipFlash: SkipFlash?
    /// Twice as fast while held down.
    var boosted = false
    /// The picture fills the screen (pinched open), else it fits.
    var fills = false
    /// The picture follows the finger down, before Mira reduces it to the bar over the tabs.
    var pulled: CGFloat = 0

    // MARK: The menus

    var audio: [PlayerTrack] = []
    var subtitles: [PlayerTrack] = []
    var audioIndex: Int?
    var subtitleIndex = -1
    var speed: Float = 1
    var quality = "auto"
    /// What the quality is now: « Original file », « Converted · 8 Mb/s ».
    var qualityNow: String?
    var sleep: SleepTimer.Status?
    /// The sleep timer's choice: minutes, 0 for the end of the episode, -1 when off.
    var sleepChoice = -1
    var isEpisode = false
    var inGroup = false
    var groupName: String?
    var canPictureInPicture = false
    /// The interface turned sideways, or upright.
    var landscape = false

    @ObservationIgnored weak var actions: PlayerActions?
    /// The controls appeared or went: the status bar and the home bar follow them.
    @ObservationIgnored var onControls: (() -> Void)?
    /// The finger moved on the timeline: the thumbnail of that moment.
    @ObservationIgnored var onScrub: ((Double) -> Void)?
    /// The interface should turn: sideways when true.
    @ObservationIgnored var onRotate: ((Bool) -> Void)?
    @ObservationIgnored private var hideTask: Task<Void, Never>?
    @ObservationIgnored private var hintTask: Task<Void, Never>?
    @ObservationIgnored private var flashTask: Task<Void, Never>?
    @ObservationIgnored private var levelTask: Task<Void, Never>?
    /// A menu or a button was touched: the controls stay until the picture is touched again.
    @ObservationIgnored var held = false
    /// The UI tests' captures (debug builds): the held state shown.
    @ObservationIgnored var showcaseStep = 0
    /// Where the controls are on the screen (window points), by name: a touch there is theirs, not the picture's.
    @ObservationIgnored var regions: [String: CGRect] = [:]

    /// A touch at this point of the window lands on a control.
    func onControl(_ point: CGPoint) -> Bool {
        regions.values.contains { $0.insetBy(dx: -4, dy: -4).contains(point) }
    }

    struct Level: Equatable {
        enum Kind { case brightness, volume }
        let kind: Kind
        let value: Double
    }

    struct SkipFlash: Equatable {
        let forward: Bool
        let seconds: Int
        /// Bumped by each tap, so that the circles start again.
        let tap: Int
    }

    // MARK: Showing and hiding the controls

    /// Shown, then hidden again after a while if the title plays.
    func showControls() {
        guard !locked else {
            showLockHint()
            return
        }
        if !controlsShown {
            controlsShown = true
            onControls?()
        }
        scheduleHide()
    }

    func hideControls() {
        hideTask?.cancel()
        held = false
        guard controlsShown else { return }
        controlsShown = false
        onControls?()
    }

    func toggleControls() {
        if controlsShown { hideControls() } else { showControls() }
    }

    /// Something was touched on the controls: they stay a while longer.
    func touched(holding: Bool = false) {
        if holding { held = true }
        scheduleHide()
    }

    /// The controls go by themselves 4 s after the last touch while the title plays; never with VoiceOver, never while
    /// a menu is open or the finger is on the timeline.
    func scheduleHide() {
        hideTask?.cancel()
        guard playing, !scrubbing, !held, !UIAccessibility.isVoiceOverRunning else { return }
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(4))
            guard let self, !Task.isCancelled, self.playing, !self.scrubbing, !self.held else { return }
            self.hideControls()
        }
    }

    func showLockHint() {
        lockHint = true
        hintTask?.cancel()
        hintTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(3))
            guard !Task.isCancelled else { return }
            self?.lockHint = false
        }
    }

    /// One more tap of a double tap: 10 s more each, the circles on that side.
    func flash(forward: Bool) {
        let seconds = skipFlash.map { $0.forward == forward ? $0.seconds + 10 : 10 } ?? 10
        skipFlash = SkipFlash(forward: forward, seconds: seconds, tap: (skipFlash?.tap ?? 0) + 1)
        flashTask?.cancel()
        flashTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(900))
            guard !Task.isCancelled else { return }
            self?.skipFlash = nil
        }
    }

    func show(_ level: Level) {
        self.level = level
        levelTask?.cancel()
    }

    /// The finger left the side: the level stays a moment.
    func levelDone() {
        levelTask?.cancel()
        levelTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(700))
            guard !Task.isCancelled else { return }
            self?.level = nil
        }
    }

    /// Changed only when it differs: what the session reads several times a second redraws nothing when it stays.
    func set<T: Equatable>(_ path: ReferenceWritableKeyPath<PlayerModel, T>, _ value: T) {
        if self[keyPath: path] != value { self[keyPath: path] = value }
    }

    // MARK: Text

    /// « 1:05:09 », « 4:07 ».
    static func clock(_ seconds: Double) -> String {
        let total = Int(max(0, seconds.isFinite ? seconds : 0))
        let h = total / 3600, m = total / 60 % 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
    }

    /// The chapter at this moment, by its name.
    func chapter(at seconds: Double) -> String? {
        chapters.last { $0.start <= seconds }?.name
    }

    static let speeds: [Float] = [0.5, 0.75, 1, 1.25, 1.5, 2]
}

#if DEBUG
extension PlayerModel {
    /// For the UI tests' captures only: what a finger holds, which a test cannot hold while it captures the screen
    /// (the timeline and its thumbnail, the volume, twice as fast, the padlock), one after the other.
    func showcase() {
        showcaseStep += 1
        switch showcaseStep {
        case 1:
            showControls()
            scrubTime = time
            actions?.scrubbing(true)
            scrubTime = duration * 0.62
        case 2:
            actions?.scrubbing(false)
            show(Level(kind: .volume, value: 0.65))
        case 3:
            level = nil
            boosted = true
        case 4:
            boosted = false
            locked = true
            lockHint = true
        default:
            showcaseStep = 0
            locked = false
            lockHint = false
            showControls()
        }
    }
}
#endif
