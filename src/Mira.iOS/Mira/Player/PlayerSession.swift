import AVFoundation
import AVKit
import UIKit

/// One title playing in Apple's player (AVPlayerViewController, full screen): Picture in Picture, AirPlay, the Lock
/// Screen and Control Center come with it. The session asks Jellyfin what to play (PlaybackInfo), tells it where this
/// iPhone is (Sessions/Playing), asks again with more converted when the player refuses a stream (the cascade of Mira
/// web), and goes on to the next episode of a series in the same player.
@MainActor
final class PlayerSession: NSObject {
    private(set) var item: Item
    private let client: JellyfinClient
    private weak var app: AppModel?
    private var audioIndex: Int?
    private var subtitleIndex: Int?
    private var stage = 0
    private var source: MediaSource?
    private var playSessionId: String?
    private var playMethod = "DirectPlay"
    private var inband = false
    private var bitrate: Int?
    private var miraHls: Bool?
    private let device: PlaybackProfile.Device

    private let player = AVPlayer()
    private let controller = MiraPlayerController()
    private var loop: Task<Void, Never>?
    private var observers: [NSObjectProtocol] = []
    /// Bumped by each opening: what an older one finds afterwards is dropped.
    private var opening = 0
    /// Where the stream being opened starts (seconds), until the player is ready for it.
    private var pendingStart: Double?
    private var started = false
    private var stopped = false
    private var closed = false
    private var wasPaused = false
    private var lastReport = Date.distantPast
    private var pictureInPicture = false
    private var restoring = false
    private var failure: String?

    init(item: Item, client: JellyfinClient, app: AppModel, audioIndex: Int?, subtitleIndex: Int?) {
        self.item = item
        self.client = client
        self.app = app
        self.audioIndex = audioIndex
        self.subtitleIndex = subtitleIndex
        device = PlaybackProfile.Device.read()
        super.init()
        controller.player = player
        controller.delegate = self
        controller.allowsPictureInPicturePlayback = true
        controller.canStartPictureInPictureAutomaticallyFromInline = true
        controller.modalPresentationStyle = .fullScreen
        controller.view.accessibilityIdentifier = "player"
        controller.onDismissed = { [weak self] in self?.dismissed() }
        player.allowsExternalPlayback = true
    }

    /// Opens the player over the app, then the stream: where the title was left unless asked from the start.
    func start(fromStart: Bool) {
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
        try? AVAudioSession.sharedInstance().setActive(true)
        Self.topController()?.present(controller, animated: true)
        loop = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(500))
                self?.tick()
            }
        }
        let ticks = PlaybackPlan.startTicks(item, fromStart: fromStart)
        Task { await open(at: ticks) }
    }

    // MARK: Opening a stream

    private func open(at ticks: Int64) async {
        opening += 1
        let attempt = opening
        if started { await report(.progress) }
        do {
            let fileBitrate = source?.bitrate ?? item.mediaSources?.first?.bitrate ?? 0
            if bitrate == nil { bitrate = await app?.bitrate(for: client, fileBitrate: fileBitrate) ?? PlaybackPlan.fullBitrate }
            if miraHls == nil { miraHls = await client.hasMiraExtension() }
            guard attempt == opening, !closed else { return }
            func ask(_ limit: Int) async throws -> PlaybackInfo {
                try await client.playbackInfo(item.id, request: PlaybackRequest(
                    bitrate: limit, stage: stage, startTicks: ticks, audioIndex: audioIndex, subtitleIndex: subtitleIndex,
                    mediaSourceId: source?.id ?? item.mediaSources?.first?.id, device: device))
            }
            let limit = bitrate ?? PlaybackPlan.fullBitrate
            var info = try await ask(limit)
            if info.errorCode == nil, let first = info.mediaSources?.first, let full = PlaybackPlan.uncappedBitrate(first, stage: stage, bitrate: limit) {
                info = try await ask(full)
            }
            guard attempt == opening, !closed else { return }
            if let code = info.errorCode { throw PlayerError.refused(code) }
            guard let next = info.mediaSources?.first else { throw PlayerError.nothing }
            let previous = playSessionId, previousMethod = playMethod
            source = next
            playSessionId = info.playSessionId
            if let previous, previous != playSessionId, previousMethod != "DirectPlay" { Task { await client.stopEncoding(previous) } }
            audioIndex = audioIndex ?? next.defaultAudioStreamIndex
            subtitleIndex = subtitleIndex ?? next.defaultSubtitleStreamIndex ?? -1
            guard let stream = PlaybackPlan.stream(next, itemId: item.id, deviceId: client.device.id, stage: stage, subtitleIndex: subtitleIndex, miraHls: miraHls ?? false),
                  let url = client.signed(stream.path) else { throw PlayerError.nothing }
            playMethod = stream.playMethod
            inband = stream.inband
            attach(url, startSeconds: Double(ticks) / Double(ItemText.ticksPerSecond))
        } catch is CancellationError {
        } catch {
            guard attempt == opening, !closed else { return }
            fail(PlayerError.text(error))
        }
    }

    private func attach(_ url: URL, startSeconds: Double) {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers = []
        let next = AVPlayerItem(url: url)
        next.externalMetadata = metadata()
        player.replaceCurrentItem(with: next)
        pendingStart = startSeconds
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVPlayerItem.didPlayToEndTimeNotification, object: next, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.ended() }
        })
        observers.append(center.addObserver(forName: AVPlayerItem.failedToPlayToEndTimeNotification, object: next, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.streamFailed() }
        })
        loadArtwork()
    }

    /// Twice a second: the stream ready (start it where it should), refused (the cascade), paused or played (told to
    /// Jellyfin at once), and every 10 s where it is.
    private func tick() {
        guard !closed, let current = player.currentItem else { return }
        switch current.status {
        case .failed:
            streamFailed()
            return
        case .readyToPlay:
            if let start = pendingStart {
                pendingStart = nil
                if start > 0 { player.seek(to: CMTime(seconds: start, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) }
                player.play()
                if inband { Task { await chooseSubtitles() } }
                if !started {
                    started = true
                    Task { await report(.start) }
                }
            }
        default:
            return
        }
        let paused = player.timeControlStatus == .paused
        if paused != wasPaused || Date().timeIntervalSince(lastReport) >= 10 {
            wasPaused = paused
            Task { await report(.progress) }
        }
    }

    /// Text subtitles in the HLS stream: the one chosen on the title's page shown, or none.
    private func chooseSubtitles() async {
        guard let current = player.currentItem else { return }
        let asset = current.asset
        guard let source, let group = try? await asset.loadMediaSelectionGroup(for: .legible), player.currentItem === current else { return }
        guard let index = subtitleIndex, index >= 0, let stream = source.subtitles.first(where: { $0.index == index }) else {
            if group.allowsEmptySelection { current.select(nil, in: group) }
            return
        }
        let name = TrackNames.menuNames(source)[index]
        let language = TrackNames.language(stream.language)
        let option = group.options.first { $0.displayName == name }
            ?? group.options.first { language != nil && $0.displayName == language }
            ?? group.options.first { $0.extendedLanguageTag?.lowercased().hasPrefix(String((stream.language ?? "-").prefix(2)).lowercased()) == true }
        if let option { current.select(option, in: group) }
    }

    // MARK: Failures

    /// The player refused the stream: asked again with more of it converted, from where it was; after everything
    /// converted, « Can't play ».
    private func streamFailed() {
        guard !closed, let current = player.currentItem else { return }
        let error = current.error
        let at = pendingStart ?? max(0, player.currentTime().seconds.isFinite ? player.currentTime().seconds : 0)
        pendingStart = nil
        player.replaceCurrentItem(with: nil)
        guard let next = PlaybackPlan.nextStage(stage) else {
            fail(error.map { String(localized: "The player refused this video, even fully converted by Jellyfin. (\($0.localizedDescription))") }
                ?? String(localized: "The player refused this video, even fully converted by Jellyfin."))
            return
        }
        stage = next
        Task { await open(at: Int64(at * Double(ItemText.ticksPerSecond))) }
    }

    /// Closes the player, then says why over the page it was started from.
    private func fail(_ message: String) {
        failure = message
        close()
    }

    // MARK: The end of a title

    /// Played to the end: watched; an episode goes on to the next one, with the same languages.
    private func ended() {
        guard !closed, !stopped else { return }
        stopped = true
        let total = item.runTimeTicks ?? source?.runTimeTicks ?? position
        let finished = state(position: max(total, position))
        Task {
            await client.report(.stop, finished)
            if playMethod != "DirectPlay", let id = playSessionId { await client.stopEncoding(id) }
            await nextEpisode()
        }
    }

    private func nextEpisode() async {
        guard item.isEpisode, let next = try? await client.episode(after: item), !closed else {
            close()
            return
        }
        let streams = (try? await client.item(next.id))?.mediaSources?.first?.mediaStreams ?? next.mediaSources?.first?.mediaStreams ?? []
        guard !closed else { return }
        let audio = source?.audio.first { $0.index == audioIndex }
        let subtitle = source?.subtitles.first { $0.index == subtitleIndex }
        audioIndex = TrackNames.same(audio, in: streams.filter { $0.type == "Audio" })?.index
        subtitleIndex = subtitleIndex == -1 ? -1 : TrackNames.same(subtitle, in: streams.filter { $0.type == "Subtitle" })?.index ?? -1
        item = next
        source = nil
        playSessionId = nil
        playMethod = "DirectPlay"
        stage = 0
        started = false
        stopped = false
        app?.playbackChanged()
        await open(at: 0)
    }

    /// The player is gone (closed, or a failure): Jellyfin told where it stopped, its conversion ended.
    func close() {
        guard !closed else { return }
        closed = true
        loop?.cancel()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers = []
        let last = state(position: position)
        let report = started && !stopped
        let encoding = playMethod != "DirectPlay" ? playSessionId : nil
        player.pause()
        player.replaceCurrentItem(with: nil)
        let failure = failure
        if controller.presentingViewController != nil && !controller.isBeingDismissed {
            controller.dismiss(animated: true) { [weak self] in self?.app?.playerMessage = failure }
        } else if let failure {
            app?.playerMessage = failure
        }
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        let client = client
        Task {
            if report { await client.report(.stop, last) }
            if let encoding { await client.stopEncoding(encoding) }
            self.app?.playerClosed(self)
        }
    }

    private func dismissed() {
        // Picture in Picture takes the video out of the player, which leaves the screen: still playing.
        guard !pictureInPicture, !restoring else { return }
        close()
    }

    // MARK: Reports

    private var position: Int64 {
        let seconds = pendingStart ?? player.currentTime().seconds
        return seconds.isFinite ? Int64(max(0, seconds) * Double(ItemText.ticksPerSecond)) : 0
    }

    private func state(position: Int64) -> PlaybackState {
        PlaybackState(itemId: item.id, mediaSourceId: source?.id, playSessionId: playSessionId, positionTicks: position,
                      isPaused: player.timeControlStatus == .paused, playMethod: playMethod, audioIndex: audioIndex, subtitleIndex: subtitleIndex)
    }

    private func report(_ kind: JellyfinClient.Report) async {
        guard started, !stopped else { return }
        lastReport = Date()
        await client.report(kind, state(position: position))
    }

    // MARK: What the Lock Screen and AirPlay show

    private var heading: (title: String, subtitle: String?) {
        if item.isEpisode {
            let code = ItemText.episodeCode(item)
            return ([code, item.name ?? ""].filter { !$0.isEmpty }.joined(separator: " · "), item.seriesName)
        }
        return (item.name ?? "", item.productionYear.map(String.init))
    }

    private func metadata(artwork: Data? = nil) -> [AVMetadataItem] {
        func entry(_ identifier: AVMetadataIdentifier, _ value: any NSCopying & NSObjectProtocol) -> AVMetadataItem {
            let entry = AVMutableMetadataItem()
            entry.identifier = identifier
            entry.value = value
            entry.extendedLanguageTag = "und"
            return entry.copy() as! AVMetadataItem
        }
        var items = [entry(.commonIdentifierTitle, heading.title as NSString)]
        if let subtitle = heading.subtitle { items.append(entry(.iTunesMetadataTrackSubTitle, subtitle as NSString)) }
        if let overview = item.overview, !overview.isEmpty { items.append(entry(.commonIdentifierDescription, overview as NSString)) }
        if let artwork { items.append(entry(.commonIdentifierArtwork, artwork as NSData)) }
        return items
    }

    private func loadArtwork() {
        guard let art = Artwork.of(item, .wide) ?? Artwork.of(item, .poster) else { return }
        let url = client.imageURL(art, width: 640)
        let id = item.id
        Task {
            guard let answer = try? await JellyfinClient.session.data(from: url), item.id == id, let current = player.currentItem else { return }
            current.externalMetadata = metadata(artwork: answer.0)
        }
    }

    static func topController() -> UIViewController? {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        let scene = scenes.first { $0.activationState == .foregroundActive } ?? scenes.first
        var top = scene?.keyWindow?.rootViewController
        while let next = top?.presentedViewController, !next.isBeingDismissed { top = next }
        return top
    }
}

extension PlayerSession: @preconcurrency AVPlayerViewControllerDelegate {
    func playerViewControllerWillStartPictureInPicture(_ playerViewController: AVPlayerViewController) {
        pictureInPicture = true
    }

    func playerViewControllerDidStopPictureInPicture(_ playerViewController: AVPlayerViewController) {
        pictureInPicture = false
        // Closed from its window, not brought back into the app: the title stops there.
        if !restoring && controller.presentingViewController == nil { close() }
        restoring = false
    }

    func playerViewController(_ playerViewController: AVPlayerViewController,
                              restoreUserInterfaceForPictureInPictureStopWithCompletionHandler completionHandler: @escaping (Bool) -> Void) {
        guard controller.presentingViewController == nil, let top = Self.topController() else {
            completionHandler(true)
            return
        }
        restoring = true
        top.present(controller, animated: true) { completionHandler(true) }
    }
}

/// Apple's player, which says when it leaves the screen (closed, or swiped down).
final class MiraPlayerController: AVPlayerViewController {
    var onDismissed: (@MainActor () -> Void)?

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        if isBeingDismissed || presentingViewController == nil { onDismissed?() }
    }
}

enum PlayerError: Error {
    case refused(String)
    case nothing

    static func text(_ error: Error) -> String {
        switch error {
        case PlayerError.refused("NotAllowed"):
            return String(localized: "Jellyfin doesn’t allow this account to play this title.")
        case PlayerError.refused("NoCompatibleStream"):
            return String(localized: "Jellyfin has no stream this iPhone can play for this title.")
        case PlayerError.refused(let code):
            return String(localized: "Jellyfin refused to play this title (\(code)).")
        case PlayerError.nothing:
            return String(localized: "Jellyfin has no stream this iPhone can play for this title.")
        default:
            return Messages.text(error)
        }
    }
}
