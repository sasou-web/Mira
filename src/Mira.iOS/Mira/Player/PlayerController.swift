import AVFoundation
import MediaPlayer
import SwiftUI
import UIKit

/// The picture: an AVPlayerLayer as the view's own layer.
final class PlayerVideoView: UIView {
    override class var layerClass: AnyClass { AVPlayerLayer.self }
    var playerLayer: AVPlayerLayer { layer as! AVPlayerLayer }

    init(player: AVPlayer?) {
        super.init(frame: .zero)
        backgroundColor = .black
        playerLayer.player = player
        playerLayer.videoGravity = .resizeAspect
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }
}

/// Mira's player on the screen: the picture, Mira's controls over it (PlayerChrome), and the gestures on the picture,
/// as YouTube has them: a tap shows or hides the controls; a double tap on a side goes back or forward 10 s, each more
/// tap 10 s more; a sweep up or down on the left changes the brightness, on the right the volume; across, it moves in
/// the title with the thumbnails; down from the middle, the player shrinks into the bar over the tabs; held down, the
/// title plays twice as fast; pinched, the picture fills the screen. A padlock keeps all of that still.
final class PlayerController: UIViewController, UIGestureRecognizerDelegate {
    let model: PlayerModel
    let video: PlayerVideoView
    /// The picture and the controls, which follow the finger down together.
    private let stage = UIView()
    private var hosting: UIHostingController<PlayerChrome>!
    /// Apple's volume control, off the screen: Mira's sweep changes the volume through it, and iOS shows no volume
    /// window of its own while it is there.
    private let volumeView = MPVolumeView(frame: CGRect(x: -2000, y: -2000, width: 10, height: 10))
    var onDismissed: (@MainActor () -> Void)?
    var onAppeared: (@MainActor () -> Void)?

    /// Upright or sideways on iPhone: sideways for a film unless the person turns it back (Mira opens a title the way
    /// it is watched, as Apple TV and Netflix do). iPad turns everywhere.
    private(set) var orientations: UIInterfaceOrientationMask = UIDevice.current.userInterfaceIdiom == .phone ? .landscape : .all

    // The gestures under way.
    private enum Sweep { case none, seek(from: Double), brightness(from: CGFloat), volume(from: Float), pull }
    private var sweep = Sweep.none
    private var lastTap: (at: Date, forward: Bool?)?
    private var tapTask: Task<Void, Never>?
    /// A double tap was seen: more taps on that side go on skipping until this time.
    private var skippingUntil = Date.distantPast
    private var skippingForward = true
    /// The brightness before Mira changed it, given back when the player goes.
    private var brightnessBefore: CGFloat?
    private weak var brightnessScreen: UIScreen?

    init(model: PlayerModel, player: AVPlayer) {
        self.model = model
        video = PlayerVideoView(player: player)
        super.init(nibName: nil, bundle: nil)
        modalPresentationStyle = .fullScreen
        modalPresentationCapturesStatusBarAppearance = true
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        view.accessibilityIdentifier = "player"
        stage.frame = view.bounds
        stage.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        stage.backgroundColor = .black
        view.addSubview(stage)
        video.frame = stage.bounds
        video.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        video.isAccessibilityElement = false
        stage.addSubview(video)

        hosting = UIHostingController(rootView: PlayerChrome(model: model))
        hosting.view.backgroundColor = .clear
        hosting.view.frame = stage.bounds
        hosting.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        addChild(hosting)
        stage.addSubview(hosting.view)
        hosting.didMove(toParent: self)

        volumeView.alpha = 0.01
        volumeView.isUserInteractionEnabled = false
        volumeView.accessibilityElementsHidden = true
        view.addSubview(volumeView)

        let tap = UITapGestureRecognizer(target: self, action: #selector(tapped(_:)))
        let pan = UIPanGestureRecognizer(target: self, action: #selector(panned(_:)))
        pan.maximumNumberOfTouches = 1
        let hold = UILongPressGestureRecognizer(target: self, action: #selector(held(_:)))
        hold.minimumPressDuration = 0.45
        let pinch = UIPinchGestureRecognizer(target: self, action: #selector(pinched(_:)))
        for gesture in [tap, pan, hold, pinch] as [UIGestureRecognizer] {
            gesture.delegate = self
            stage.addGestureRecognizer(gesture)
        }

        model.landscape = view.bounds.width > view.bounds.height
        model.onControls = { [weak self] in
            self?.setNeedsStatusBarAppearanceUpdate()
            self?.setNeedsUpdateOfHomeIndicatorAutoHidden()
            self?.setNeedsUpdateOfScreenEdgesDeferringSystemGestures()
        }
        model.onRotate = { [weak self] sideways in self?.turn(sideways: sideways) }
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        onAppeared?()
    }

    override func viewWillTransition(to size: CGSize, with coordinator: any UIViewControllerTransitionCoordinator) {
        super.viewWillTransition(to: size, with: coordinator)
        model.landscape = size.width > size.height
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        // Leaving (closed, reduced, or Picture in Picture): the pages under it are upright on iPhone (AppDelegate).
        guard isBeingDismissed, let scene = view.window?.windowScene, UIDevice.current.userInterfaceIdiom == .phone else { return }
        presentingViewController?.setNeedsUpdateOfSupportedInterfaceOrientations()
        scene.requestGeometryUpdate(.iOS(interfaceOrientations: .portrait))
    }

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        stage.transform = .identity
        view.backgroundColor = .black
        model.pulled = 0
        if isBeingDismissed || presentingViewController == nil { onDismissed?() }
    }

    override var supportedInterfaceOrientations: UIInterfaceOrientationMask { orientations }
    override var preferredInterfaceOrientationForPresentation: UIInterfaceOrientation {
        orientations.contains(.landscapeRight) ? .landscapeRight : .portrait
    }
    override var prefersStatusBarHidden: Bool { !model.controlsShown || model.locked || model.landscape }
    override var preferredStatusBarStyle: UIStatusBarStyle { .lightContent }
    override var prefersHomeIndicatorAutoHidden: Bool { true }
    /// Locked, an edge sweep (Control Center, Home) needs a second sweep: a hand on the screen does not leave the film.
    override var preferredScreenEdgesDeferringSystemGestures: UIRectEdge { model.locked ? .all : [] }

    /// Sideways or upright, at the tap of the controls' button, whatever way the iPhone is held.
    private func turn(sideways: Bool) {
        guard UIDevice.current.userInterfaceIdiom == .phone, let scene = view.window?.windowScene else { return }
        orientations = sideways ? .landscape : .portrait
        setNeedsUpdateOfSupportedInterfaceOrientations()
        scene.requestGeometryUpdate(.iOS(interfaceOrientations: orientations))
    }

    /// Opened again from the bar over the tabs or Picture in Picture: sideways again for a film.
    func prepareToShow() {
        guard UIDevice.current.userInterfaceIdiom == .phone else { return }
        orientations = .landscape
    }

    /// The brightness as it was before the sweeps.
    func giveBrightnessBack() {
        guard let brightnessBefore, let brightnessScreen else { return }
        brightnessScreen.brightness = brightnessBefore
        self.brightnessBefore = nil
    }

    // MARK: Gestures

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        // The controls' own touches (buttons, menus, the timeline) are theirs.
        !model.onControl(touch.location(in: nil))
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        false
    }

    /// One tap shows or hides the controls; two quick ones on a side skip 10 s there, and each more tap on that side
    /// while its pill shows skips 10 s more. The middle has no double tap: it answers at once.
    @objc private func tapped(_ tap: UITapGestureRecognizer) {
        if model.locked {
            model.showLockHint()
            return
        }
        let point = tap.location(in: stage), width = max(1, stage.bounds.width)
        let side: Bool? = point.x < width * 0.35 ? false : point.x > width * 0.65 ? true : nil
        let now = Date()
        if let forward = side, now < skippingUntil, forward == skippingForward {
            skip(forward: forward, now: now)
            return
        }
        if let forward = side, let last = lastTap, last.forward == forward, now.timeIntervalSince(last.at) < 0.3 {
            tapTask?.cancel()
            lastTap = nil
            skip(forward: forward, now: now)
            return
        }
        lastTap = (now, side)
        tapTask?.cancel()
        guard side != nil else {
            toggle()
            return
        }
        tapTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(280))
            guard !Task.isCancelled else { return }
            self?.toggle()
        }
    }

    private func toggle() {
        model.held = false
        model.toggleControls()
    }

    private func skip(forward: Bool, now: Date) {
        skippingForward = forward
        skippingUntil = now.addingTimeInterval(0.8)
        model.flash(forward: forward)
        model.actions?.skip(forward ? 10 : -10)
    }

    @objc private func panned(_ pan: UIPanGestureRecognizer) {
        guard !model.locked else { return }
        let size = stage.bounds.size
        let moved = pan.translation(in: stage)
        switch pan.state {
        case .began:
            let speed = pan.velocity(in: stage), start = pan.location(in: stage)
            if abs(speed.x) > abs(speed.y) {
                guard model.duration > 0 else { return }
                sweep = .seek(from: model.time)
                model.scrubTime = model.time
                model.actions?.scrubbing(true)
            } else if start.x < size.width * 0.3, let screen = view.window?.windowScene?.screen {
                if brightnessBefore == nil {
                    brightnessBefore = screen.brightness
                    brightnessScreen = screen
                }
                sweep = .brightness(from: screen.brightness)
            } else if start.x > size.width * 0.7 {
                sweep = .volume(from: AVAudioSession.sharedInstance().outputVolume)
            } else if speed.y > 0 {
                sweep = .pull
            } else {
                sweep = .none
            }
        case .changed:
            switch sweep {
            case let .seek(from):
                // Across the whole screen: three minutes, finer than the timeline for a long film.
                model.scrubTime = min(model.duration, max(0, from + Double(moved.x / max(1, size.width)) * 180))
            case let .brightness(from):
                let value = min(1, max(0, from - moved.y / max(1, size.height * 0.75)))
                view.window?.windowScene?.screen.brightness = value
                model.show(.init(kind: .brightness, value: value))
            case let .volume(from):
                let value = min(1, max(0, from - Float(moved.y / max(1, size.height * 0.75))))
                volumeSlider?.value = value
                model.show(.init(kind: .volume, value: Double(value)))
            case .pull:
                let down = max(0, moved.y)
                model.pulled = down
                let scale = 1 - min(0.18, down / max(1, size.height) * 0.4)
                stage.transform = CGAffineTransform(translationX: 0, y: down).scaledBy(x: scale, y: scale)
                view.backgroundColor = .black.withAlphaComponent(max(0.25, 1 - down / max(1, size.height)))
            case .none:
                break
            }
        case .ended, .cancelled, .failed:
            switch sweep {
            case .seek:
                model.actions?.scrubbing(false)
                if pan.state == .ended { model.actions?.seek(to: model.scrubTime) }
            case .brightness, .volume:
                model.levelDone()
            case .pull:
                let down = max(0, moved.y), speed = pan.velocity(in: stage).y
                if pan.state == .ended && (down > size.height * 0.22 || speed > 900) {
                    model.actions?.minimize()
                } else {
                    UIView.animate(withDuration: 0.3, delay: 0, usingSpringWithDamping: 0.85, initialSpringVelocity: 0) {
                        self.stage.transform = .identity
                        self.view.backgroundColor = .black
                    } completion: { _ in
                        self.model.pulled = 0
                    }
                }
            case .none:
                break
            }
            sweep = .none
        default:
            break
        }
    }

    private var volumeSlider: UISlider? {
        volumeView.subviews.lazy.compactMap { $0 as? UISlider }.first
    }

    /// Held down: twice as fast until the finger lifts.
    @objc private func held(_ hold: UILongPressGestureRecognizer) {
        guard !model.locked else { return }
        switch hold.state {
        case .began: model.actions?.boost(true)
        case .ended, .cancelled, .failed: model.actions?.boost(false)
        default: break
        }
    }

    /// Pinched open: the picture fills the screen (its edges cut); pinched closed: all of it again.
    @objc private func pinched(_ pinch: UIPinchGestureRecognizer) {
        guard !model.locked, pinch.state == .ended, abs(pinch.scale - 1) > 0.06 else { return }
        let fills = pinch.scale > 1
        guard fills != model.fills else { return }
        model.fills = fills
        CATransaction.begin()
        CATransaction.setAnimationDuration(0.3)
        video.playerLayer.videoGravity = fills ? .resizeAspectFill : .resizeAspect
        CATransaction.commit()
    }
}
