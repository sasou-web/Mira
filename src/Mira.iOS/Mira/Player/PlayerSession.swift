import AVFoundation
import AVKit
import MediaPlayer
import UIKit

/// One title playing in Mira's own player (PlayerController, full screen): its controls, the gestures on the picture,
/// Picture in Picture, AirPlay, the Lock Screen and Control Center. The session asks Jellyfin what to play
/// (PlaybackInfo), tells it where this iPhone is (Sessions/Playing), asks again with more converted when the player
/// refuses a stream (the cascade of Mira web), and goes on to the next episode of a series in the same player.
/// Reduced, it goes on in the bar over the tabs (MiniPlayer).
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

    let player: AVPlayer
    /// What the controls draw.
    let model: PlayerModel
    private let controller: PlayerController
    private var pip: AVPictureInPictureController?
    private var nowPlaying: MPNowPlayingSession?
    private var loop: Task<Void, Never>?
    private var clock: Any?
    private var observers: [NSObjectProtocol] = []
    /// Bumped by each opening: what an older one finds afterwards is dropped.
    private var opening = 0
    /// The tracks to start with chosen once (the next episodes carry them by their languages).
    private var tracksChosen = false
    /// Where the stream being opened starts (seconds), until the player is ready for it.
    private var pendingStart: Double?
    /// The stream being opened stays paused there (another track or quality chosen while paused).
    private var pendingPaused = false
    private var started = false
    private var stopped = false
    private var closed = false
    private var wasPaused = false
    private var lastReport = Date.distantPast
    private var inPictureInPicture = false
    private var restoring = false
    /// Reduced to the bar over the tabs: the player left the screen, the title goes on.
    private(set) var minimized = false
    private var minimizing = false
    private var failure: String?
    /// The stream watched for stalls; the server asked about one, or waited for.
    private var look = StallWatch()
    private var askingServer = false
    private var waitingForServer = false
    /// Where a stream opened again after a stall started (seconds), until it played on past it.
    private var reopenedAt: Double?
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
    // The title's timeline (Timeline): its passages, those skipped by themselves, where it started, its next episode.
    private var passages: [Passage] = []
    private var passagesFor: String?
    private var skipped: Set<Passage> = []
    private var startedAt: Double = 0
    private var nextItem: Item?
    private var nextAsked = false
    private var trickplay: TrickplayTiles?
    /// The speed chosen in the menu (1 in a group); twice it while held down.
    private var speed: Float = 1

    init(item: Item, client: JellyfinClient, app: AppModel, audioIndex: Int?, subtitleIndex: Int?) {
        self.item = item
        self.client = client
        self.app = app
        self.audioIndex = audioIndex
        self.subtitleIndex = subtitleIndex
        device = PlaybackProfile.Device.read()
        let player = AVPlayer(), model = PlayerModel()
        self.player = player
        self.model = model
        controller = PlayerController(model: model, player: player)
        super.init()
        model.actions = self
        model.onScrub = { [weak self] seconds in self?.showThumbnail(seconds) }
        controller.onDismissed = { [weak self] in self?.dismissed() }
        player.allowsExternalPlayback = true
        player.usesExternalPlaybackWhileExternalScreenIsActive = true
        let pip = AVPictureInPictureController(contentSource: .init(playerLayer: controller.video.playerLayer))
        pip.canStartPictureInPictureAutomaticallyFromInline = true
        pip.delegate = self
        self.pip = pip
        model.canPictureInPicture = AVPictureInPictureController.isPictureInPictureSupported()
        rateObserver = NotificationCenter.default.addObserver(forName: AVPlayer.rateDidChangeNotification, object: player, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.rateChanged() }
        }
        describe()
    }

    private var together: Together? { app?.together }
    private var inGroup: Bool { app?.together.group != nil }

    /// Opens the player over the app, then the stream: where the title was left unless asked from the start.
    func start(fromStart: Bool, groupStart: Int64? = nil) {
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
        try? AVAudioSession.sharedInstance().setActive(true)
        present()
        setUpNowPlaying()
        loop = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(500))
                self?.tick()
            }
        }
        clock = player.addPeriodicTimeObserver(forInterval: CMTime(value: 1, timescale: 4), queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        let preferences = app?.preferences
        var ticks = PlaybackPlan.startTicks(item, fromStart: fromStart, resume: preferences?.resume ?? true, back: preferences?.resumeBack ?? 0)
        if let together, together.group != nil {
            together.bind(self)
            if let groupStart {
                // The group's title: loading, then ready where the group starts it; it plays when Jellyfin says.
                ticks = groupStart
                readyPending = true
                together.buffering(Double(ticks) / Double(ItemText.ticksPerSecond), playing: false)
            }
        }
        startedAt = Double(ticks) / Double(ItemText.ticksPerSecond)
        model.time = startedAt
        let chosen = groupStart == nil
        Task {
            await open(at: ticks)
            // A title chosen here while in a group is put on for the group, from where it starts here.
            if chosen, let together, together.group != nil, together.queue?.itemId != item.id {
                await together.start(item.id, startTicks: ticks)
            }
        }
    }

    /// The player over the app: sideways for a film on iPhone.
    private func present() {
        controller.prepareToShow()
        Self.topController()?.present(controller, animated: true)
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
            if !tracksChosen {
                tracksChosen = true
                await chooseTracks()
            }
            if passagesFor != item.id { loadPassages() }
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
            describeTracks()
            attach(url, startSeconds: Double(ticks) / Double(ItemText.ticksPerSecond))
        } catch is CancellationError {
        } catch {
            guard attempt == opening, !closed else { return }
            fail(PlayerError.text(error))
        }
    }

    /// Nothing chosen on the title's page: the series' last tracks, else the languages of Settings (TrackChoice, as
    /// Mira web), from the file's tracks.
    private func chooseTracks() async {
        guard audioIndex == nil, subtitleIndex == nil, let preferences = app?.preferences else { return }
        var file = item.mediaSources?.first
        if file?.mediaStreams?.isEmpty != false { file = (try? await client.item(item.id))?.mediaSources?.first }
        guard let file, file.mediaStreams?.isEmpty == false, audioIndex == nil, subtitleIndex == nil else { return }
        let chosen = TrackChoice.choose(file, memory: preferences.tracks(for: item.seriesId), audioOrder: preferences.audioOrder, subtitleOrder: preferences.subtitleOrder)
        audioIndex = chosen.audio
        subtitleIndex = chosen.subtitle
    }

    private func attach(_ url: URL, startSeconds: Double) {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers = []
        let next = AVPlayerItem(url: url)
        next.nowPlayingInfo = nowPlayingInfo()
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
        refresh()
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
                    if pendingPaused { player.pause() } else { player.play() }
                }
                pendingPaused = false
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
        watchPassages()
        if inGroup { watchGroupStall() }
        model.set(\.groupLine, groupLine())
        let paused = player.timeControlStatus == .paused
        if paused != wasPaused || Date().timeIntervalSince(lastReport) >= 10 {
            wasPaused = paused
            Task { await report(.progress) }
        }
    }

    /// What the controls show: where the title is, what came already, playing or not, loading, AirPlay.
    private func refresh() {
        guard !closed else { return }
        let now = player.currentTime().seconds
        if !model.scrubbing { model.set(\.time, pendingStart ?? (now.isFinite ? max(0, now) : 0)) }
        model.set(\.duration, duration)
        if let range = player.currentItem?.loadedTimeRanges.last?.timeRangeValue {
            let end = range.end.seconds
            model.set(\.buffered, end.isFinite ? end : 0)
        }
        let status = player.timeControlStatus
        let playing = pendingStart != nil ? !pendingPaused : status != .paused
        if playing != model.playing {
            model.playing = playing
            if playing { model.scheduleHide() } else { model.touched() }
        }
        let ready = player.currentItem?.status == .readyToPlay && pendingStart == nil
        let reason = player.reasonForWaitingToPlay
        let stalled = status == .waitingToPlayAtSpecifiedRate && (reason == .toMinimizeStalls || reason == .evaluatingBufferingRate)
        model.set(\.loading, !waitingForServer && (!ready || stalled))
        let television = AVAudioSession.sharedInstance().currentRoute.outputs.first { $0.portType == .airPlay }?.portName
        model.set(\.externalName, player.isExternalPlaybackActive ? television ?? String(localized: "AirPlay") : nil)
        let sleep = app?.sleep
        model.set(\.sleep, sleep?.status(Date()))
        model.set(\.sleepChoice, sleep?.chosen ?? -1)
        model.set(\.inGroup, inGroup)
        model.set(\.groupName, together?.group?.groupName)
        model.set(\.quality, app?.preferences.quality ?? "auto")
        model.set(\.waitingForServer, waitingForServer)
    }

    /// Text subtitles in the HLS stream: the one chosen shown, or none.
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

    // MARK: What the controls say

    /// The title, its line under it, and what the menus offer of it.
    private func describe() {
        model.title = heading.title
        model.subtitle = heading.subtitle
        model.isEpisode = item.isEpisode
        model.hasNext = false
        model.chapters = []
        model.passage = nil
        model.nextCountdown = nil
    }

    private func describeTracks() {
        guard let source else { return }
        let audio = source.audio, subtitles = source.subtitles
        let audioNames = TrackNames.names(audio), subtitleNames = TrackNames.names(subtitles)
        model.audio = audio.map { PlayerTrack(index: $0.index, name: audioNames[$0.index] ?? "") }
        model.subtitles = subtitles.map { PlayerTrack(index: $0.index, name: subtitleNames[$0.index] ?? "") }
        model.audioIndex = audioIndex
        model.subtitleIndex = subtitleIndex ?? -1
        switch playMethod {
        case "DirectPlay": model.qualityNow = String(localized: "Original file")
        case "DirectStream": model.qualityNow = String(localized: "Original quality")
        default:
            let rate = (Double(bitrate ?? PlaybackPlan.fullBitrate) / 1_000_000).formatted(.number.precision(.fractionLength(0...1)))
            model.qualityNow = bitrate.map { $0 < PlaybackPlan.fullBitrate } == true
                ? String(localized: "Converted · \(rate) Mb/s") : String(localized: "Converted")
        }
    }

    // MARK: Failures

    /// The player refused the stream. The server silent: Mira waits for it, then opens the stream again where it was;
    /// the connection cut while it answers: opened again, once; otherwise asked again with more of it converted, from
    /// where it was, and after everything converted, « Can't play ».
    private func streamFailed() {
        guard !closed, !askingServer, let current = player.currentItem else { return }
        let error = current.error
        let cut = Self.cut(error)
        let refused = error.map { String(localized: "The player refused this video, even fully converted by Jellyfin. (\($0.localizedDescription))") }
            ?? String(localized: "The player refused this video, even fully converted by Jellyfin.")
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
            if cut, reopenedAt == nil {
                reopenedAt = at
                reopen(at)
                return
            }
            guard let next = PlaybackPlan.nextStage(stage) else {
                fail(refused)
                return
            }
            stage = next
            reopen(at)
        }
    }

    /// An error of the way to the server, not of the video.
    private static func cut(_ error: Error?) -> Bool {
        guard let error else { return false }
        let list: [URLError.Code] = [.networkConnectionLost, .notConnectedToInternet, .timedOut, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed, .resourceUnavailable]
        let codes = Set(list.map(\.rawValue))
        let outer = error as NSError
        var chain: [NSError] = [outer]
        if let underlying = outer.userInfo[NSUnderlyingErrorKey] as? NSError { chain.append(underlying) }
        return chain.contains { $0.domain == NSURLErrorDomain && codes.contains($0.code) }
    }

    /// Where the title is, in seconds: where it was asked to start while it opens.
    private var resumeSeconds: Double {
        let now = player.currentTime().seconds
        return pendingStart ?? max(0, now.isFinite ? now : 0)
    }

    private func reopen(_ at: Double, paused: Bool = false) {
        // In a group: it waits for this iPhone, then everyone goes on together.
        if let together, together.group != nil {
            readyPending = true
            readyPlaying = true
            together.buffering(at, playing: true)
        }
        pendingPaused = paused && !inGroup
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
        // A jump (the timeline, ±10 s) starts the clock again: a conversion takes a while to start from there.
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
        model.waitingForServer = true
        UIAccessibility.post(notification: .announcement, argument: String(localized: "Connection to the server lost. Playback resumes as soon as it answers."))
        Task { [weak self] in
            while true {
                try? await Task.sleep(for: .seconds(3))
                guard let self, !self.closed else { return }
                if await self.client.ping() { break }
            }
            guard let self, !self.closed else { return }
            self.waitingForServer = false
            self.model.waitingForServer = false
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

    /// `asked`: by the Next episode button, whatever Settings say.
    private func nextEpisode(asked: Bool = false) async {
        // « End of the episode » on the sleep timer: this one was the last.
        if app?.sleep.atEnd == true && !asked {
            close()
            return
        }
        // « Next episode automatically » off: the episode ends there (the group's choice in a group).
        var found = nextItem
        if found == nil, item.isEpisode { found = try? await client.episode(after: item) }
        guard item.isEpisode, asked || app?.preferences.autoNext != false || inGroup, let next = found, !closed else {
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
        newTitle(at: 0)
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

    // MARK: Passages and the next episode

    /// Another title in the same player: its own timeline.
    private func newTitle(at seconds: Double) {
        passages = []
        passagesFor = nil
        skipped = []
        nextItem = nil
        nextAsked = false
        startedAt = seconds
        trickplay = nil
        model.time = seconds
        describe()
    }

    /// Jellyfin's segments, filled by what the file's chapters name; its trickplay pictures; the next episode.
    private func loadPassages() {
        let id = item.id, known = item
        passagesFor = id
        Task {
            let server = await client.passages(id)
            let full = (try? await client.item(id)) ?? known
            guard item.id == id, !closed else { return }
            passages = Timeline.withChapters(server, Timeline.chapterPassages(full.chapters, totalTicks: full.runTimeTicks ?? source?.runTimeTicks))
            var chapters: [(start: Double, name: String?)] = []
            for chapter in full.chapters ?? [] {
                guard let ticks = chapter.startPositionTicks, ticks >= 0 else { continue }
                let name = chapter.name?.trimmingCharacters(in: .whitespaces) ?? ""
                chapters.append((start: Timeline.seconds(ticks), name: name.isEmpty ? nil : name))
            }
            model.chapters = chapters.sorted { $0.start < $1.start }
            if let set = TrickplaySet.pick(id, full.trickplay, sourceId: source?.id ?? full.mediaSources?.first?.id) {
                let tiles = TrickplayTiles(set: set, client: client)
                tiles.loaded = { [weak self] in
                    guard let self, self.model.scrubbing else { return }
                    self.showThumbnail(self.model.scrubTime)
                }
                trickplay = tiles
                model.previewSize = CGSize(width: set.info.width ?? 16, height: set.info.height ?? 9)
            }
        }
        guard known.isEpisode else { return }
        Task {
            let next = try? await client.episode(after: known)
            guard item.id == id, !closed else { return }
            if nextItem == nil { nextItem = next }
            model.hasNext = nextItem != nil
        }
    }

    /// The thumbnail of that moment; the last one stays while the next one's tile comes.
    private func showThumbnail(_ seconds: Double) {
        guard let trickplay else {
            model.preview = nil
            return
        }
        if let image = trickplay.thumbnail(seconds) { model.preview = image }
    }

    private var duration: Double {
        if let ticks = item.runTimeTicks ?? source?.runTimeTicks, ticks > 0 { return Timeline.seconds(ticks) }
        let seconds = player.currentItem?.duration.seconds ?? 0
        return seconds.isFinite ? seconds : 0
    }

    /// Twice a second: an intro or recap skipped by itself, the Skip button over a passage, the Next episode button
    /// from the end credits (or the last seconds), and the next episode a countdown into credits (timeline.js).
    private func watchPassages() {
        let at = player.currentTime().seconds
        guard pendingStart == nil, !stopped, at.isFinite else { return }
        let end = duration
        let passage = Timeline.skippable(passages, at: at)
        // In a group, skips are the person's: the group follows them (never by themselves).
        if !inGroup, Timeline.skipsItself(passage, autoSkip: app?.preferences.autoSkip == true, skipped: skipped), let passage {
            skipped.insert(passage)
            player.seek(to: CMTime(seconds: passage.end, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
            return
        }
        guard end > 0, item.isEpisode else {
            showPassage(passage.flatMap { Timeline.skipLabel($0.type) })
            return
        }
        let nextFrom = Timeline.nextFrom(passages, end: end)
        if !nextAsked && at >= nextFrom - 30 {
            nextAsked = true
            let id = item.id, current = item
            Task {
                let next = try? await client.episode(after: current)
                if item.id == id, let next { nextItem = next }
                model.hasNext = nextItem != nil
            }
        }
        if nextItem != nil && at >= nextFrom {
            let auto = !inGroup && app?.preferences.autoNext != false && app?.sleep.atEnd != true
                ? Timeline.nativeNextAt(passages, end: end, startedAt: startedAt) : nil
            showPassage(String(localized: "Next episode"), next: true, countdown: auto.map { 1 - max(0, $0 - at) / Timeline.countdown })
            if let auto, at >= auto { goToNext() }
        } else {
            showPassage(passage.flatMap { Timeline.skipLabel($0.type) })
        }
    }

    private func showPassage(_ text: String?, next: Bool = false, countdown: Double? = nil) {
        if text != model.passage, let text { UIAccessibility.post(notification: .announcement, argument: text) }
        model.passage = text
        model.passageIsNext = next && text != nil
        model.nextCountdown = countdown
    }

    // MARK: The end, Picture in Picture, the bar over the tabs

    /// The rest of the episode is credits: watched, and the next one now.
    private func goToNext() {
        guard !closed, !stopped, nextItem != nil else { return }
        stopped = true
        showPassage(nil)
        let finished = state(position: max(item.runTimeTicks ?? 0, position))
        let encoding = playMethod != "DirectPlay" ? playSessionId : nil
        player.pause()
        Task {
            await client.report(.stop, finished)
            if let encoding { await client.stopEncoding(encoding) }
            await nextEpisode(asked: true)
        }
    }

    /// The player is gone (closed, or a failure): Jellyfin told where it stopped, its conversion ended.
    func close() {
        guard !closed else { return }
        closed = true
        loop?.cancel()
        if let clock { player.removeTimeObserver(clock) }
        clock = nil
        model.waitingForServer = false
        showPassage(nil)
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
        if pip?.isPictureInPictureActive == true { pip?.stopPictureInPicture() }
        controller.giveBrightnessBack()
        let failure = failure
        if failure != nil { app?.retryItem = item }
        // The picture stays on the player while it slides away; emptied once it is gone (no black frame going down).
        if controller.presentingViewController != nil && !controller.isBeingDismissed {
            controller.dismiss(animated: true) { [weak self] in
                self?.player.replaceCurrentItem(with: nil)
                self?.app?.playerMessage = failure
            }
        } else {
            player.replaceCurrentItem(with: nil)
            if let failure { app?.playerMessage = failure }
        }
        nowPlaying = nil
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        let client = client
        Task {
            if report { await client.report(.stop, last) }
            if let encoding { await client.stopEncoding(encoding) }
            self.app?.playerClosed(self)
        }
    }

    private func dismissed() {
        // Picture in Picture takes the video out of the player, which leaves the screen; reduced, it goes on in the
        // bar over the tabs: still playing.
        if minimizing {
            minimizing = false
            return
        }
        guard !inPictureInPicture, !restoring else { return }
        close()
    }

    /// Back to the full player, from the bar over the tabs.
    func expand() {
        guard !closed, minimized else { return }
        minimized = false
        app?.playerMinimized(self, false)
        present()
        model.showControls()
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

    /// Paused or played in the player, from the Lock Screen or Control Center: for the whole group. Played, it waits
    /// until Jellyfin says when everyone plays.
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

    /// Moved in the player: for the whole group, once the finger stops.
    private func jumped() {
        guard inGroup, !closed, pendingStart == nil, Date() >= quietUntil, !model.scrubbing else { return }
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

    private func nowPlayingInfo(artwork: UIImage? = nil) -> [String: Any] {
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: heading.title,
            MPNowPlayingInfoPropertyMediaType: MPNowPlayingInfoMediaType.video.rawValue,
        ]
        if let subtitle = heading.subtitle { info[MPMediaItemPropertyArtist] = subtitle }
        if let artwork {
            info[MPMediaItemPropertyArtwork] = MPMediaItemArtwork(boundsSize: artwork.size) { @Sendable _ in artwork }
        }
        return info
    }

    private func loadArtwork() {
        guard let art = Artwork.of(item, .wide) ?? Artwork.of(item, .poster) else { return }
        let url = client.imageURL(art, width: 640, scale: 1)
        let id = item.id
        Task {
            guard let answer = try? await JellyfinClient.session.data(from: url), item.id == id, let current = player.currentItem,
                  let image = UIImage(data: answer.0) else { return }
            current.nowPlayingInfo = nowPlayingInfo(artwork: image)
        }
    }

    /// The Lock Screen, Control Center and the headphones' buttons: Apple's player did this itself.
    private func setUpNowPlaying() {
        let session = MPNowPlayingSession(players: [player])
        session.automaticallyPublishesNowPlayingInfo = true
        let commands = session.remoteCommandCenter
        commands.playCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.player.play() }
            return .success
        }
        commands.pauseCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.player.pause() }
            return .success
        }
        commands.togglePlayPauseCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.togglePlay() }
            return .success
        }
        commands.skipForwardCommand.preferredIntervals = [10]
        commands.skipForwardCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.skip(10) }
            return .success
        }
        commands.skipBackwardCommand.preferredIntervals = [10]
        commands.skipBackwardCommand.addTarget { @Sendable [weak self] _ in
            Task { @MainActor in self?.skip(-10) }
            return .success
        }
        commands.changePlaybackPositionCommand.addTarget { @Sendable [weak self] event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
            let seconds = event.positionTime
            Task { @MainActor in self?.seek(to: seconds) }
            return .success
        }
        nowPlaying = session
        session.becomeActiveIfPossible(completion: nil)
    }

    static func topController() -> UIViewController? {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        let scene = scenes.first { $0.activationState == .foregroundActive } ?? scenes.first
        var top = scene?.keyWindow?.rootViewController
        while let next = top?.presentedViewController, !next.isBeingDismissed { top = next }
        return top
    }
}

// MARK: The controls' requests

extension PlayerSession: PlayerActions {
    func togglePlay() {
        guard !closed else { return }
        if player.timeControlStatus == .paused && pendingStart == nil {
            player.play()
        } else if pendingStart != nil {
            pendingPaused.toggle()
        } else {
            player.pause()
        }
        model.touched()
        refresh()
    }

    func skip(_ seconds: Double) {
        let now = player.currentTime().seconds
        guard !closed, now.isFinite, player.currentItem?.status == .readyToPlay else { return }
        let end = duration
        seek(to: min(max(0, now + seconds), end > 0 ? end - 1 : now + seconds))
    }

    func seek(to seconds: Double) {
        guard !closed, player.currentItem?.status == .readyToPlay else { return }
        model.time = seconds
        player.seek(to: CMTime(seconds: max(0, seconds), preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        model.touched()
    }

    func scrubbing(_ active: Bool) {
        model.scrubbing = active
        if active {
            model.showControls()
            showThumbnail(model.scrubTime)
        } else {
            model.preview = nil
            model.scheduleHide()
        }
    }

    func selectAudio(_ index: Int) {
        guard index != audioIndex, !closed else { return }
        audioIndex = index
        model.audioIndex = index
        rememberTracks()
        // Another audio track: Jellyfin sends the file again with it (copied into HLS when it is not the first).
        let paused = player.timeControlStatus == .paused
        let at = resumeSeconds
        player.replaceCurrentItem(with: nil)
        reopen(at, paused: paused)
    }

    func selectSubtitles(_ index: Int) {
        guard index != (subtitleIndex ?? -1), !closed else { return }
        let target = source?.subtitles.first { $0.index == index }
        let current = source?.subtitles.first { $0.index == subtitleIndex }
        // Pictures are drawn into the video by Jellyfin: leaving them needs a new stream, as does choosing them.
        let burned = current != nil && current?.isTextSubtitleStream != true && playMethod != "DirectPlay"
        subtitleIndex = index
        model.subtitleIndex = index
        rememberTracks()
        if inband && !burned && (index < 0 || target?.isTextSubtitleStream == true) {
            Task { await chooseSubtitles() }
            Task { await report(.progress) }
            return
        }
        if index < 0 && !burned {
            return
        }
        let paused = player.timeControlStatus == .paused
        let at = resumeSeconds
        player.replaceCurrentItem(with: nil)
        reopen(at, paused: paused)
    }

    private func rememberTracks() {
        guard let series = item.seriesId, let source else { return }
        app?.preferences.remember(TrackChoice.memory(source, audio: audioIndex, subtitle: subtitleIndex), for: series)
    }

    func setSpeed(_ speed: Float) {
        guard !inGroup else { return }
        self.speed = speed
        model.speed = speed
        player.defaultRate = speed
        if player.rate > 0 { player.rate = speed }
    }

    func boost(_ on: Bool) {
        if on {
            guard !inGroup, player.timeControlStatus == .playing else { return }
            model.boosted = true
            player.rate = 2
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
        } else if model.boosted {
            model.boosted = false
            if player.rate > 0 { player.rate = speed }
        }
    }

    func setQuality(_ quality: String) {
        guard !closed, let app else { return }
        app.preferences.quality = quality
        model.quality = quality
        // Asked again from scratch: the file as it is when the new quality allows it.
        bitrate = nil
        stage = 0
        let paused = player.timeControlStatus == .paused
        let at = resumeSeconds
        player.replaceCurrentItem(with: nil)
        reopen(at, paused: paused)
    }

    func setSleep(_ minutes: Int?) {
        app?.sleep.choose(minutes, now: .now)
        refresh()
    }

    func passageTapped() {
        let at = player.currentTime().seconds
        guard !closed, at.isFinite else { return }
        if model.passageIsNext {
            goToNext()
        } else if let passage = Timeline.skippable(passages, at: at) {
            skipped.insert(passage)
            showPassage(nil)
            player.seek(to: CMTime(seconds: passage.end, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        }
    }

    func playNext() {
        goToNext()
    }

    func pictureInPicture() {
        guard let pip, pip.isPictureInPicturePossible else { return }
        pip.startPictureInPicture()
    }

    /// Reduced to the bar over the tabs (MiraTabBar).
    func minimize() {
        guard !closed else { return }
        minimizing = true
        minimized = true
        model.hideControls()
        app?.playerMinimized(self, true)
        controller.dismiss(animated: true)
    }

    func leaveGroup() {
        Task { await together?.leave() }
    }
}

extension PlayerSession: @preconcurrency AVPictureInPictureControllerDelegate {
    func pictureInPictureControllerWillStartPictureInPicture(_ pictureInPictureController: AVPictureInPictureController) {
        inPictureInPicture = true
    }

    /// In its window, the title leaves the player: the app is free under it.
    func pictureInPictureControllerDidStartPictureInPicture(_ pictureInPictureController: AVPictureInPictureController) {
        if controller.presentingViewController != nil && !controller.isBeingDismissed { controller.dismiss(animated: true) }
    }

    func pictureInPictureControllerDidStopPictureInPicture(_ pictureInPictureController: AVPictureInPictureController) {
        inPictureInPicture = false
        // Closed from its window, not brought back into the app: the title stops there.
        if !restoring && controller.presentingViewController == nil && !minimized { close() }
        restoring = false
    }

    func pictureInPictureController(_ pictureInPictureController: AVPictureInPictureController,
                                    restoreUserInterfaceForPictureInPictureStopWithCompletionHandler completionHandler: @escaping (Bool) -> Void) {
        guard controller.presentingViewController == nil else {
            completionHandler(true)
            return
        }
        restoring = true
        if minimized {
            minimized = false
            app?.playerMinimized(self, false)
        }
        controller.prepareToShow()
        guard let top = Self.topController() else {
            completionHandler(true)
            return
        }
        top.present(controller, animated: true) { completionHandler(true) }
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
            newTitle(at: Double(startTicks) / Double(ItemText.ticksPerSecond))
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

