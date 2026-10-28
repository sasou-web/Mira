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
    /// The stream watched for stalls; the server asked about one, or waited for.
    private var look = StallWatch()
    private var askingServer = false
    private var waitingForServer = false
    /// Where a stream opened again after a stall started (seconds), until it played on past it.
    private var reopenedAt: Double?
    private let waiting = WaitingOverlay()
    // « Watch together »: what the player did itself, not to be told to the group as the person's (until this time);
    // the group's play to come; a seek told once the finger stops; a stall told to the group.
    private var quietUntil = Date.distantPast
    private var readyPending = false
    /// The Ready to come says « playing »: the stream was opened again while the group played.
    private var readyPlaying = false
    private var groupPlay: Task<Void, Never>?
    private var groupSeek: Task<Void, Never>?
    private var waitingSince: Date?
    private var stallTold = false
    private var rateObserver: NSObjectProtocol?
    private let pill = GroupPill()

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
        waiting.install(in: controller.contentOverlayView)
        pill.install(in: controller.contentOverlayView)
        rateObserver = NotificationCenter.default.addObserver(forName: AVPlayer.rateDidChangeNotification, object: player, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.rateChanged() }
        }
    }

    private var together: Together? { app?.together }
    private var inGroup: Bool { app?.together.group != nil }

    /// Opens the player over the app, then the stream: where the title was left unless asked from the start.
    func start(fromStart: Bool, groupStart: Int64? = nil) {
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
        try? AVAudioSession.sharedInstance().setActive(true)
        Self.topController()?.present(controller, animated: true)
        loop = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(500))
                self?.tick()
            }
        }
        var ticks = PlaybackPlan.startTicks(item, fromStart: fromStart)
        if let together, together.group != nil {
            together.bind(self)
            if let groupStart {
                // The group's title: loading, then ready where the group starts it; it plays when Jellyfin says.
                ticks = groupStart
                readyPending = true
                together.buffering(Double(ticks) / Double(ItemText.ticksPerSecond), playing: false)
            }
        }
        let chosen = groupStart == nil
        Task {
            await open(at: ticks)
            // A title chosen here while in a group is put on for the group, from where it starts here.
            if chosen, let together, together.group != nil, together.queue?.itemId != item.id {
                await together.start(item.id, startTicks: ticks)
            }
        }
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
        look = StallWatch()
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVPlayerItem.didPlayToEndTimeNotification, object: next, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.ended() }
        })
        observers.append(center.addObserver(forName: AVPlayerItem.failedToPlayToEndTimeNotification, object: next, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.streamFailed() }
        })
        observers.append(center.addObserver(forName: AVPlayerItem.timeJumpedNotification, object: next, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.jumped() }
        })
        loadArtwork()
    }

    /// Twice a second: the stream ready (start it where it should), refused (the cascade), paused or played (told to
    /// Jellyfin at once), and every 10 s where it is.
    private func tick() {
        guard !closed, !askingServer, !waitingForServer, let current = player.currentItem else { return }
        watchStream()
        switch current.status {
        case .failed:
            streamFailed()
            return
        case .readyToPlay:
            if let start = pendingStart {
                pendingStart = nil
                if inGroup {
                    // In a group: there, paused, and ready; Jellyfin says when everyone plays.
                    quiet(3)
                    seekQuietly(start)
                } else {
                    if start > 0 { player.seek(to: CMTime(seconds: start, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) }
                    player.play()
                }
                if inband { Task { await chooseSubtitles() } }
                if !started {
                    started = true
                    Task { await report(.start) }
                }
            }
        default:
            return
        }
        // The sleep timer's minutes are over: a pause, once.
        if app?.sleep.due(Date()) == true { player.pause() }
        if inGroup { watchGroupStall() }
        pill.show(groupLine())
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

    /// The player refused the stream. The server silent: Mira waits for it, then opens the stream again where it was;
    /// the connection cut while it answers: opened again, once; otherwise asked again with more of it converted, from
    /// where it was, and after everything converted, « Can't play ».
    private func streamFailed() {
        guard !closed, !askingServer, let current = player.currentItem else { return }
        let error = current.error
        let at = resumeSeconds
        pendingStart = nil
        player.replaceCurrentItem(with: nil)
        let attempt = opening
        askingServer = true
        Task {
            let up = await client.ping()
            askingServer = false
            guard attempt == opening, !closed else { return }
            if !up {
                waitForServer(at)
                return
            }
            if Self.cut(error), reopenedAt == nil {
                reopenedAt = at
                reopen(at)
                return
            }
            guard let next = PlaybackPlan.nextStage(stage) else {
                fail(error.map { String(localized: "The player refused this video, even fully converted by Jellyfin. (\($0.localizedDescription))") }
                    ?? String(localized: "The player refused this video, even fully converted by Jellyfin."))
                return
            }
            stage = next
            reopen(at)
        }
    }

    /// An error of the way to the server, not of the video.
    private static func cut(_ error: Error?) -> Bool {
        guard let error else { return false }
        let error = error as NSError
        let codes: [URLError.Code] = [.networkConnectionLost, .notConnectedToInternet, .timedOut, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed, .resourceUnavailable]
        let underlying = error.userInfo[NSUnderlyingErrorKey] as? NSError
        return [error, underlying].contains { $0?.domain == NSURLErrorDomain && codes.map(\.rawValue).contains($0?.code ?? 0) }
    }

    /// Where the title is, in seconds: where it was asked to start while it opens.
    private var resumeSeconds: Double {
        let now = player.currentTime().seconds
        return pendingStart ?? max(0, now.isFinite ? now : 0)
    }

    private func reopen(_ at: Double) {
        // In a group: it waits for this iPhone, then everyone goes on together.
        if let together, together.group != nil {
            readyPending = true
            readyPlaying = true
            together.buffering(at, playing: true)
        }
        Task { await open(at: Int64(at * Double(ItemText.ticksPerSecond))) }
    }

    // MARK: Stalls

    /// Twice a second (stream.js stallWatch): a stream that should play and stays still is stuck.
    private func watchStream() {
        let now = player.currentTime().seconds
        let at = now.isFinite ? now : -1
        let playing = player.timeControlStatus == .playing
        // Played on well past where it was opened again: the next stall is a first one.
        if let reopenedAt, playing, at > reopenedAt + StallWatch.forgiven { self.reopenedAt = nil }
        let expecting = pendingStart != nil || player.timeControlStatus != .paused
        // A jump (the scrubber, ±15 s) starts the clock again: a conversion takes a while to start from there.
        let jumped = look.at >= 0 && at >= 0 && abs(at - look.at) > 3
        look = look.next(now: Date(), at: at, expecting: expecting, ready: playing, seeking: jumped)
        if look.stuck { stuck() }
    }

    /// A stall, the server asked: silent, Mira waits for it; answering, the stream is opened again where the title is,
    /// once; stuck again before it played on, « Can't play » (stream.js stallStep).
    private func stuck() {
        let attempt = opening, at = resumeSeconds, wasReopened = reopenedAt != nil
        look = StallWatch()
        askingServer = true
        Task {
            let up = await client.ping()
            askingServer = false
            guard attempt == opening, !closed else { return }
            switch StallWatch.step(up: up, reopened: wasReopened) {
            case .wait:
                player.pause()
                waitForServer(at)
            case .reopen:
                reopenedAt = at
                player.replaceCurrentItem(with: nil)
                reopen(at)
            case .fail:
                fail(String(localized: "Jellyfin answers, but the video no longer reaches this iPhone. Try again, or choose a lower quality if the connection is slow."))
            }
        }
    }

    /// The server is silent: said over the video, asked every 3 s, and the stream opened again where it was once it
    /// answers. Closing the player stops waiting.
    private func waitForServer(_ at: Double) {
        guard !waitingForServer else { return }
        waitingForServer = true
        waiting.show(true)
        Task { [weak self] in
            while true {
                try? await Task.sleep(for: .seconds(3))
                guard let self, !self.closed else { return }
                if await self.client.ping() { break }
            }
            guard let self, !self.closed else { return }
            self.waitingForServer = false
            self.waiting.show(false)
            self.player.replaceCurrentItem(with: nil)
            self.reopen(at)
        }
    }

    /// Closes the player, then says why over the page it was started from.
    private func fail(_ message: String) {
        failure = message
        close()
    }

    // MARK: The end of a title

    /// Played to the end: watched; an episode goes on to the next one, with the same languages.
    private func ended() {
        guard !closed, !stopped, !askingServer, !waitingForServer else { return }
        let total = item.runTimeTicks ?? source?.runTimeTicks ?? position
        // Ended well before its end: the stream was cut, never watched nor followed by the next episode.
        if StallWatch.cut(position: position, total: total) {
            stuck()
            return
        }
        stopped = true
        let finished = state(position: max(total, position))
        Task {
            await client.report(.stop, finished)
            if playMethod != "DirectPlay", let id = playSessionId { await client.stopEncoding(id) }
            await nextEpisode()
        }
    }

    private func nextEpisode() async {
        // « End of the episode » on the sleep timer: this one was the last.
        if app?.sleep.atEnd == true {
            close()
            return
        }
        guard item.isEpisode, let next = try? await client.episode(after: item), !closed else {
            close()
            return
        }
        // In a group, every device reaches the end: the next one is put on for the group once, and comes back here.
        if let together, together.group != nil {
            if together.queue?.itemId == item.id { await together.start(next.id, startTicks: 0) }
            return
        }
        let streams = (try? await client.item(next.id))?.mediaSources?.first?.mediaStreams ?? next.mediaSources?.first?.mediaStreams ?? []
        guard !closed else { return }
        let audio = source?.audio.first { $0.index == audioIndex }
        let subtitle = source?.subtitles.first { $0.index == subtitleIndex }
        audioIndex = TrackNames.same(audio, in: streams.filter { $0.type == "Audio" })?.index
        subtitleIndex = subtitleIndex == -1 ? -1 : TrackNames.same(subtitle, in: streams.filter { $0.type == "Subtitle" })?.index ?? -1
        item = next
        reopenedAt = nil
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
        waiting.show(false)
        groupPlay?.cancel()
        groupSeek?.cancel()
        if let rateObserver { NotificationCenter.default.removeObserver(rateObserver) }
        together?.unbind(self)
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

    // MARK: Watch together

    /// What the player does itself for the next `seconds` is not the person's: not told to the group.
    private func quiet(_ seconds: TimeInterval) {
        quietUntil = max(quietUntil, Date().addingTimeInterval(seconds))
    }

    private func quietPause() {
        quiet(1.5)
        player.pause()
    }

    /// Where the group is, then « ready » once there when the group waits for it.
    private func seekQuietly(_ seconds: Double) {
        quiet(3)
        player.seek(to: CMTime(seconds: max(0, seconds), preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            Task { @MainActor in self?.checkReady() }
        }
    }

    private func checkReady() {
        guard readyPending, inGroup, !closed, player.currentItem?.status == .readyToPlay else { return }
        readyPending = false
        together?.ready(groupPosition, playing: readyPlaying || player.timeControlStatus != .paused)
        readyPlaying = false
    }

    /// Paused or played in Apple's player, from the Lock Screen or Control Center: for the whole group. Played, it
    /// waits until Jellyfin says when everyone plays.
    private func rateChanged() {
        guard inGroup, !closed, pendingStart == nil, Date() >= quietUntil, player.currentItem?.status == .readyToPlay else { return }
        if player.rate == 0 {
            let total = player.currentItem?.duration.seconds ?? 0
            guard !(total.isFinite && total > 0 && groupPosition >= total - 1) else { return }
            groupPlay?.cancel()
            together?.pause()
        } else {
            quietPause()
            together?.unpause()
        }
    }

    /// Moved in Apple's player: for the whole group, once the finger stops.
    private func jumped() {
        guard inGroup, !closed, pendingStart == nil, Date() >= quietUntil else { return }
        groupSeek?.cancel()
        groupSeek = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(400))
            guard let self, !Task.isCancelled, !self.closed else { return }
            self.together?.seek(self.groupPosition)
        }
    }

    /// A stall of more than a moment while playing: the group waits for this iPhone, then goes on with it.
    private func watchGroupStall() {
        let stalled = player.timeControlStatus == .waitingToPlayAtSpecifiedRate && player.reasonForWaitingToPlay == .toMinimizeStalls
        if stalled && Date() >= quietUntil {
            let since = waitingSince ?? Date()
            waitingSince = since
            if !stallTold && Date().timeIntervalSince(since) > 1.5 {
                stallTold = true
                together?.buffering(groupPosition, playing: true)
            }
        } else if player.timeControlStatus == .playing {
            waitingSince = nil
            if stallTold {
                stallTold = false
                together?.ready(groupPosition, playing: true)
            }
        }
    }

    /// The pill over the video: what the group does when it holds the picture, someone who came or went for 4 s.
    private func groupLine() -> String? {
        guard let together, let group = together.group else { return nil }
        if let member = together.member, Date().timeIntervalSince(member.at) < 4 { return member.text }
        let loading = readyPending || player.currentItem?.status != .readyToPlay
        let status = SyncRules.groupStatus(group, me: app?.session?.userName, loading: loading)
        return status.alert ? status.text : nil
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

/// « Connection to the server lost », over the video while Mira waits for Jellyfin (under Apple's controls, which stay).
@MainActor
private final class WaitingOverlay {
    private let box = UIVisualEffectView(effect: UIBlurEffect(style: .systemMaterialDark))

    func install(in view: UIView?) {
        guard let view else { return }
        let spinner = UIActivityIndicatorView(style: .medium)
        spinner.color = .white
        spinner.startAnimating()
        let label = UILabel()
        label.text = String(localized: "Connection to the server lost. Playback resumes as soon as it answers.")
        label.font = UIFontMetrics(forTextStyle: .callout).scaledFont(for: .systemFont(ofSize: 16, weight: .semibold))
        label.adjustsFontForContentSizeCategory = true
        label.textColor = .white
        label.numberOfLines = 0
        label.textAlignment = .center
        let stack = UIStackView(arrangedSubviews: [spinner, label])
        stack.axis = .vertical
        stack.spacing = 12
        stack.alignment = .center
        stack.translatesAutoresizingMaskIntoConstraints = false
        box.contentView.addSubview(stack)
        box.layer.cornerRadius = 22
        box.clipsToBounds = true
        box.translatesAutoresizingMaskIntoConstraints = false
        box.isHidden = true
        box.isAccessibilityElement = true
        box.accessibilityLabel = label.text
        box.accessibilityIdentifier = "player.waiting"
        view.addSubview(box)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: box.contentView.topAnchor, constant: 18),
            stack.bottomAnchor.constraint(equalTo: box.contentView.bottomAnchor, constant: -18),
            stack.leadingAnchor.constraint(equalTo: box.contentView.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(equalTo: box.contentView.trailingAnchor, constant: -20),
            box.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            box.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            box.widthAnchor.constraint(lessThanOrEqualToConstant: 340),
            box.leadingAnchor.constraint(greaterThanOrEqualTo: view.leadingAnchor, constant: 24),
        ])
    }

    func show(_ shown: Bool) {
        box.isHidden = !shown
        if shown { UIAccessibility.post(notification: .announcement, argument: box.accessibilityLabel) }
    }
}

extension PlayerSession: GroupPlayer {
    var groupItemId: String? { closed ? nil : item.id }

    var groupPosition: Double {
        let seconds = pendingStart ?? player.currentTime().seconds
        return seconds.isFinite ? max(0, seconds) : 0
    }

    func groupPrepare(_ startTicks: Int64) {
        guard !closed else { return }
        groupPlay?.cancel()
        quietPause()
        readyPending = true
        let seconds = Double(startTicks) / Double(ItemText.ticksPerSecond)
        if pendingStart != nil { pendingStart = seconds } else { seekQuietly(seconds) }
    }

    func groupSwitch(to itemId: String, startTicks: Int64) {
        guard !closed else { return }
        groupPlay?.cancel()
        quietPause()
        Task {
            guard let next = try? await client.item(itemId), !closed else { return }
            let streams = next.mediaSources?.first?.mediaStreams ?? []
            let audio = source?.audio.first { $0.index == audioIndex }
            let subtitle = source?.subtitles.first { $0.index == subtitleIndex }
            if started && !stopped { await report(.stop) }
            audioIndex = TrackNames.same(audio, in: streams.filter { $0.type == "Audio" })?.index
            subtitleIndex = subtitleIndex == -1 ? -1 : TrackNames.same(subtitle, in: streams.filter { $0.type == "Subtitle" })?.index ?? -1
            item = next
            reopenedAt = nil
            source = nil
            playSessionId = nil
            playMethod = "DirectPlay"
            stage = 0
            started = false
            stopped = false
            readyPending = true
            app?.playbackChanged()
            await open(at: startTicks)
        }
    }

    func groupCommand(_ plan: SyncPlan) {
        guard !closed else { return }
        groupPlay?.cancel()
        switch plan.action {
        case .stop:
            together?.notice = String(localized: "The group stopped playback.")
            close()
        case .pause:
            quietPause()
            if SyncRules.needsSeek(groupPosition, plan.position) { seekQuietly(plan.position) }
        case .seek:
            quietPause()
            readyPending = true
            seekQuietly(plan.position)
        case .play:
            // There at the moment Jellyfin set, from where the others are.
            if SyncRules.needsSeek(groupPosition, plan.position) { seekQuietly(plan.position) }
            groupPlay = Task { [weak self] in
                if plan.wait > 0 { try? await Task.sleep(for: .seconds(plan.wait)) }
                guard let self, !Task.isCancelled, !self.closed else { return }
                self.quiet(1.5)
                self.player.play()
            }
        }
    }

    func groupLeft() {
        groupPlay?.cancel()
        if !closed { together?.notice = String(localized: "You left the group: playback goes on here only.") }
    }
}

/// The group's line over the video, at the top (under Apple's controls, which stay): « Waiting for Marc… ».
@MainActor
private final class GroupPill {
    private let box = UIVisualEffectView(effect: UIBlurEffect(style: .systemMaterialDark))
    private let label = UILabel()

    func install(in view: UIView?) {
        guard let view else { return }
        label.font = UIFontMetrics(forTextStyle: .footnote).scaledFont(for: .systemFont(ofSize: 14, weight: .semibold))
        label.adjustsFontForContentSizeCategory = true
        label.textColor = .white
        label.numberOfLines = 2
        label.textAlignment = .center
        label.translatesAutoresizingMaskIntoConstraints = false
        box.contentView.addSubview(label)
        box.layer.cornerRadius = 16
        box.clipsToBounds = true
        box.translatesAutoresizingMaskIntoConstraints = false
        box.isHidden = true
        box.isAccessibilityElement = true
        box.accessibilityIdentifier = "player.group"
        view.addSubview(box)
        NSLayoutConstraint.activate([
            label.topAnchor.constraint(equalTo: box.contentView.topAnchor, constant: 8),
            label.bottomAnchor.constraint(equalTo: box.contentView.bottomAnchor, constant: -8),
            label.leadingAnchor.constraint(equalTo: box.contentView.leadingAnchor, constant: 14),
            label.trailingAnchor.constraint(equalTo: box.contentView.trailingAnchor, constant: -14),
            box.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            box.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 56),
            box.leadingAnchor.constraint(greaterThanOrEqualTo: view.leadingAnchor, constant: 24),
        ])
    }

    func show(_ text: String?) {
        guard label.text != text || box.isHidden != (text == nil) else { return }
        label.text = text
        box.accessibilityLabel = text
        box.isHidden = text == nil
        if let text { UIAccessibility.post(notification: .announcement, argument: text) }
    }
}
