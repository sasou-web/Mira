import AVKit
import SwiftUI

/// Mira's controls over the picture, on the lines of Mira web's own (player.js) and of YouTube's: the title and the
/// tools at the top, back 10 s, play and forward 10 s in the middle, the timeline at the bottom with its thumbnails;
/// everything goes after a few seconds of play and comes back with a tap. The gestures on the picture itself (double
/// tap, sweeps, hold, pinch) are PlayerController's.
struct PlayerChrome: View {
    @Bindable var model: PlayerModel

    private var shown: Bool { model.controlsShown && !model.locked && model.pulled == 0 }

    var body: some View {
        ZStack {
            if let name = model.externalName { ExternalScreen(name: name) }
            Scrims().opacity(shown ? 1 : 0).allowsHitTesting(false)
            if shown {
                VStack(spacing: 0) {
                    TopBar(model: model)
                        .opacity(model.scrubbing ? 0 : 1)
                    Spacer(minLength: 0)
                    BottomBar(model: model)
                }
                .padding(.horizontal, 16)
                .padding(.top, 8)
                .padding(.bottom, 6)
                .transition(.opacity)
            }
            CenterControls(model: model, shown: shown && !model.scrubbing)
            Overlays(model: model, shown: shown)
            #if DEBUG
            ShowcaseButton(model: model)
            #endif
        }
        .foregroundStyle(.white)
        .tint(.white)
        .dynamicTypeSize(...DynamicTypeSize.xxxLarge)
        .animation(.easeOut(duration: 0.22), value: shown)
        .animation(.easeOut(duration: 0.18), value: model.scrubbing)
        .environment(\.colorScheme, .dark)
    }
}

/// The picture dimmed under the controls, as YouTube does, and darker still at the top and the bottom, where the text
/// is, so that they read on any picture (a white sky, a test card).
private struct Scrims: View {
    var body: some View {
        ZStack {
            Color.black.opacity(0.3)
            VStack(spacing: 0) {
                LinearGradient(colors: [.black.opacity(0.5), .clear], startPoint: .top, endPoint: .bottom)
                    .frame(maxHeight: .infinity)
                Color.clear.frame(maxHeight: .infinity)
                LinearGradient(colors: [.clear, .black.opacity(0.6)], startPoint: .top, endPoint: .bottom)
                    .frame(maxHeight: .infinity)
            }
        }
        .ignoresSafeArea()
    }
}

// MARK: The top

private struct TopBar: View {
    @Bindable var model: PlayerModel

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            RoundButton(symbol: "chevron.down", label: "Reduce", id: "player.minimize") { model.actions?.minimize() }
            VStack(alignment: .leading, spacing: 1) {
                Text(verbatim: model.title)
                    .font(MiraFont.headline)
                    .lineLimit(1)
                if let subtitle = model.subtitle {
                    Text(verbatim: subtitle)
                        .font(MiraFont.footnote)
                        .foregroundStyle(.white.opacity(0.72))
                        .lineLimit(1)
                }
            }
            .shadow(color: .black.opacity(0.35), radius: 6, y: 1)
            .frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isHeader)
            ToolCapsule(model: model)
        }
        .region("top", model)
    }
}

/// AirPlay, Picture in Picture, audio and subtitles, then speed, quality and the sleep timer, in one capsule of glass.
private struct ToolCapsule: View {
    @Bindable var model: PlayerModel

    var body: some View {
        HStack(spacing: 0) {
            AirPlayButton()
                .frame(width: 44, height: 44)
                .accessibilityIdentifier("player.airplay")
            if model.canPictureInPicture {
                ToolButton(symbol: "pip.enter", label: "Picture in Picture", id: "player.pip") { model.actions?.pictureInPicture() }
            }
            if model.audio.count > 1 || !model.subtitles.isEmpty {
                TracksMenu(model: model)
            }
            MoreMenu(model: model)
        }
        .padding(.horizontal, 4)
        .glassEffect(.regular.interactive(), in: .capsule)
    }
}

private struct TracksMenu: View {
    @Bindable var model: PlayerModel

    var body: some View {
        Menu {
            // Ticks in titled sections: an inline picker shows no title in a menu, and « French » twice says nothing.
            if model.audio.count > 1 {
                Section("Audio") {
                    ForEach(model.audio) { track in
                        Toggle(isOn: Binding(get: { model.audioIndex == track.index },
                                             set: { if $0 { model.actions?.selectAudio(track.index) } })) {
                            Text(verbatim: track.name)
                        }
                    }
                }
            }
            if !model.subtitles.isEmpty {
                Section("Subtitles") {
                    Toggle(isOn: Binding(get: { model.subtitleIndex < 0 }, set: { if $0 { model.actions?.selectSubtitles(-1) } })) {
                        Text("Off")
                    }
                    ForEach(model.subtitles) { track in
                        Toggle(isOn: Binding(get: { model.subtitleIndex == track.index },
                                             set: { if $0 { model.actions?.selectSubtitles(track.index) } })) {
                            Text(verbatim: track.name)
                        }
                    }
                }
            }
        } label: {
            ToolIcon(symbol: model.subtitleIndex >= 0 ? "captions.bubble.fill" : "captions.bubble")
        }
        .menuOrder(.fixed)
        .accessibilityLabel(Text("Audio and subtitles"))
        .accessibilityIdentifier("player.tracks")
    }
}

private struct MoreMenu: View {
    @Bindable var model: PlayerModel

    var body: some View {
        Menu {
            if !model.inGroup {
                Menu {
                    Picker(selection: Binding(get: { model.speed }, set: { model.actions?.setSpeed($0) })) {
                        ForEach(PlayerModel.speeds, id: \.self) { speed in
                            Text(verbatim: Self.speedName(speed)).tag(speed)
                        }
                    } label: {
                        Text("Speed")
                    }
                    .pickerStyle(.inline)
                } label: {
                    Text("Speed")
                    Text(verbatim: Self.speedName(model.speed))
                    Image(systemName: "gauge.with.dots.needle.67percent")
                }
            }
            Menu {
                Picker(selection: Binding(get: { model.quality }, set: { model.actions?.setQuality($0) })) {
                    ForEach(Preferences.qualities, id: \.self) { value in
                        Text(verbatim: Self.qualityName(value)).tag(value)
                    }
                } label: {
                    Text("Quality")
                }
                .pickerStyle(.inline)
            } label: {
                Text("Quality")
                Text(verbatim: model.qualityNow ?? Self.qualityName(model.quality))
                Image(systemName: "dial.medium")
            }
            Menu {
                Picker(selection: Binding(get: { model.sleepChoice }, set: { model.actions?.setSleep($0 < 0 ? nil : $0) })) {
                    Text("Timer off").tag(-1)
                    ForEach(SleepTimer.minutes, id: \.self) { minutes in
                        Text(verbatim: SleepTimer.duration(minutes)).tag(minutes)
                    }
                    if model.isEpisode { Text("End of the episode").tag(0) }
                } label: {
                    Text("Sleep timer")
                }
                .pickerStyle(.inline)
            } label: {
                Text("Sleep timer")
                if let status = Self.sleepName(model.sleep) { Text(verbatim: status) }
                Image(systemName: model.sleep == nil ? "moon.zzz" : "moon.zzz.fill")
            }
            Button {
                model.actions?.pictureInPicture()
            } label: {
                Label("Picture in Picture", systemImage: "pip.enter")
            }
            .disabled(!model.canPictureInPicture)
            if model.inGroup {
                Section {
                    Button(role: .destructive) {
                        model.actions?.leaveGroup()
                    } label: {
                        Label("Leave the group", systemImage: "person.2.slash")
                    }
                } header: {
                    Text(verbatim: model.groupName ?? "")
                }
            }
        } label: {
            ToolIcon(symbol: "gearshape.fill")
        }
        .menuOrder(.fixed)
        .accessibilityLabel(Text("Speed, quality and sleep timer"))
        .accessibilityIdentifier("player.more")
    }

    static func speedName(_ speed: Float) -> String {
        speed == 1 ? String(localized: "Normal") : speed.formatted(.number.precision(.fractionLength(0...2))) + "×"
    }

    static func qualityName(_ value: String) -> String {
        switch value {
        case "auto": String(localized: "Automatic")
        case "max": String(localized: "Maximum")
        default: String(localized: "\(((Double(value) ?? 0) / 1_000_000).formatted(.number.precision(.fractionLength(0...1)))) Mb/s")
        }
    }

    static func sleepName(_ status: SleepTimer.Status?) -> String? {
        switch status {
        case .endOfEpisode?: String(localized: "Stop at the end of the episode")
        case let .pause(left)?: String(localized: "Pause in \(left)")
        case nil: nil
        }
    }
}

// MARK: The middle

private struct CenterControls: View {
    @Bindable var model: PlayerModel
    let shown: Bool

    var body: some View {
        HStack(spacing: 40) {
            if shown {
                RoundButton(symbol: "gobackward.10", label: "Back 10 seconds", id: "player.back10", size: 46, glyph: 19) {
                    model.actions?.skip(-10)
                }
                .transition(.opacity)
            }
            ZStack {
                if model.loading && !model.waitingForServer {
                    ProgressView()
                        .controlSize(.large)
                        .tint(.white)
                        .frame(width: 62, height: 62)
                        .accessibilityLabel(Text("Loading"))
                } else if shown {
                    RoundButton(symbol: model.playing ? "pause.fill" : "play.fill", label: model.playing ? "Pause" : "Play", id: "player.play", size: 62, glyph: 25) {
                        model.actions?.togglePlay()
                    }
                    .contentTransition(.symbolEffect(.replace))
                    .transition(.opacity)
                }
            }
            .frame(width: 62, height: 62)
            if shown {
                RoundButton(symbol: "goforward.10", label: "Forward 10 seconds", id: "player.forward10", size: 46, glyph: 19) {
                    model.actions?.skip(10)
                }
                .transition(.opacity)
            }
        }
        .frame(height: 62)
        .region("center", model, when: shown)
    }
}

// MARK: The bottom

private struct BottomBar: View {
    @Bindable var model: PlayerModel

    var body: some View {
        VStack(spacing: 4) {
            // While the finger is on the timeline, only the timeline and its thumbnail.
            HStack(alignment: .center, spacing: 10) {
                TimeText(model: model)
                Spacer(minLength: 8)
                if model.speed != 1 && !model.inGroup {
                    Text(verbatim: MoreMenu.speedName(model.speed))
                        .font(MiraFont.footnote)
                        .monospacedDigit()
                        .padding(.horizontal, 10)
                        .frame(height: 32)
                        .glassEffect(.regular, in: .capsule)
                        .accessibilityLabel(Text("Speed \(MoreMenu.speedName(model.speed))"))
                }
                if model.hasNext && model.passage == nil {
                    Button {
                        model.actions?.playNext()
                    } label: {
                        Label("Next episode", systemImage: "forward.end.fill")
                            .font(MiraFont.footnote)
                            .padding(.horizontal, 12)
                            .frame(height: 36)
                    }
                    .buttonStyle(.plain)
                    .glassEffect(.regular.interactive(), in: .capsule)
                    .accessibilityIdentifier("player.next")
                }
                RoundButton(symbol: "lock.fill", label: "Lock", id: "player.lock", size: 36, glyph: 15) {
                    model.locked = true
                }
                if UIDevice.current.userInterfaceIdiom == .phone {
                    RoundButton(symbol: model.landscape ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right",
                                label: model.landscape ? "Portrait" : "Landscape", id: "player.rotate", size: 36, glyph: 15) {
                        model.onRotate?(!model.landscape)
                    }
                }
            }
            .opacity(model.scrubbing ? 0 : 1)
            ScrubBar(model: model)
        }
        .region("bottom", model)
    }
}

/// « 12:04 / 1:44:10 · Chapter 3 »: where the title is, its length, the chapter.
private struct TimeText: View {
    @Bindable var model: PlayerModel

    var body: some View {
        HStack(spacing: 6) {
            Text(verbatim: PlayerModel.clock(model.time))
                .font(MiraFont.footnote)
                .monospacedDigit()
            if model.duration > 0 {
                Text(verbatim: "/ " + PlayerModel.clock(model.duration))
                    .font(MiraFont.footnote)
                    .monospacedDigit()
                    .foregroundStyle(.white.opacity(0.66))
            }
            if let chapter = model.chapter(at: model.time) {
                Text(verbatim: "· " + chapter)
                    .font(MiraFont.footnote)
                    .foregroundStyle(.white.opacity(0.66))
                    .lineLimit(1)
            }
        }
        .shadow(color: .black.opacity(0.4), radius: 4)
        .accessibilityElement(children: .combine)
    }
}

/// The timeline: what was played, what came already, chapter gaps; the finger on it shows the thumbnail of where it
/// points and goes there when lifted.
private struct ScrubBar: View {
    @Bindable var model: PlayerModel

    var body: some View {
        GeometryReader { box in
            let width = max(1, box.size.width)
            let total = max(1, model.duration)
            let at = model.scrubbing ? model.scrubTime : model.time
            let played = min(1, max(0, at / total))
            let loaded = min(1, max(played, model.buffered / total))
            let thick: CGFloat = model.scrubbing ? 8 : 4
            ZStack(alignment: .leading) {
                Capsule().fill(.white.opacity(0.24)).frame(height: thick)
                Capsule().fill(.white.opacity(0.38)).frame(width: width * loaded, height: thick)
                Capsule().fill(Palette.accent).frame(width: max(thick, width * played), height: thick)
                ForEach(Array(model.chapters.enumerated()), id: \.offset) { _, chapter in
                    if chapter.start > 1 && chapter.start < total - 1 {
                        Rectangle()
                            .fill(.black.opacity(0.6))
                            .frame(width: 2, height: thick)
                            .offset(x: width * chapter.start / total - 1)
                    }
                }
                Circle()
                    .fill(Palette.accent)
                    .frame(width: 16, height: 16)
                    .shadow(color: .black.opacity(0.5), radius: 3, y: 1)
                    .scaleEffect(model.scrubbing ? 1.2 : 0.8)
                    .offset(x: width * played - 8)
            }
            .frame(height: 36)
            .contentShape(.rect)
            .gesture(DragGesture(minimumDistance: 0).onChanged { drag in
                if !model.scrubbing {
                    model.scrubTime = model.time
                    model.actions?.scrubbing(true)
                }
                model.scrubTime = min(total, max(0, Double(drag.location.x / width) * total))
            }.onEnded { drag in
                let target = min(total, max(0, Double(drag.location.x / width) * total))
                model.scrubTime = target
                model.actions?.scrubbing(false)
                model.actions?.seek(to: target)
            })
            .overlay(alignment: .bottomLeading) {
                // Over the finger, kept inside the bar at both ends.
                if model.scrubbing {
                    let tip = ScrubTip.width
                    ScrubTip(model: model)
                        .frame(width: tip)
                        .offset(x: min(max(0, width * played - tip / 2), max(0, width - tip)), y: -40)
                        .transition(.opacity)
                }
            }
        }
        .frame(height: 36)
        .accessibilityElement()
        .accessibilityLabel(Text("Position"))
        .accessibilityValue(Text(verbatim: PlayerModel.clock(model.time)))
        .accessibilityAdjustableAction { direction in
            model.actions?.skip(direction == .increment ? 10 : -10)
        }
        .accessibilityIdentifier("player.timeline")
    }
}

/// Over the timeline while the finger moves: the thumbnail of that moment (Jellyfin's trickplay pictures, when it made
/// them), the time, the chapter.
private struct ScrubTip: View {
    @Bindable var model: PlayerModel

    var body: some View {
        VStack(spacing: 6) {
            if let preview = model.preview {
                Image(uiImage: preview)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .frame(width: Self.width, height: Self.width * model.previewSize.height / max(1, model.previewSize.width))
                    .clipShape(.rect(cornerRadius: 10))
                    .overlay { RoundedRectangle(cornerRadius: 10).strokeBorder(.white.opacity(0.9), lineWidth: 2) }
                    .shadow(color: .black.opacity(0.5), radius: 12, y: 4)
            }
            VStack(spacing: 0) {
                Text(verbatim: PlayerModel.clock(model.scrubTime))
                    .font(MiraFont.headline)
                    .monospacedDigit()
                if let chapter = model.chapter(at: model.scrubTime) {
                    Text(verbatim: chapter)
                        .font(MiraFont.footnote)
                        .foregroundStyle(.white.opacity(0.8))
                        .lineLimit(1)
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 4)
            .glassEffect(.regular, in: .rect(cornerRadius: 10))
        }
        .accessibilityHidden(true)
    }

    static let width: CGFloat = 176
}

// MARK: Over the picture, controls or not

private struct Overlays: View {
    @Bindable var model: PlayerModel
    let shown: Bool

    var body: some View {
        ZStack {
            if let flash = model.skipFlash { SkipBadge(flash: flash).transition(.opacity.combined(with: .scale(scale: 0.9))) }
            VStack(spacing: 10) {
                if let level = model.level {
                    LevelBar(level: level).transition(.opacity.combined(with: .move(edge: .top)))
                }
                if model.boosted {
                    Label("2× speed", systemImage: "forward.fill")
                        .font(MiraFont.subheadline)
                        .padding(.horizontal, 16)
                        .frame(height: 38)
                        .glassEffect(.regular, in: .capsule)
                        .transition(.opacity.combined(with: .move(edge: .top)))
                }
                if let line = model.groupLine {
                    Text(verbatim: line)
                        .font(MiraFont.footnote)
                        .multilineTextAlignment(.center)
                        .lineLimit(2)
                        .padding(.horizontal, 14)
                        .padding(.vertical, 8)
                        .glassEffect(.regular, in: .capsule)
                        .accessibilityIdentifier("player.group")
                }
                Spacer()
            }
            .padding(.top, shown ? 66 : 14)
            .padding(.horizontal, 24)
            if model.waitingForServer { WaitingCard() }
            if model.passage != nil || model.lockHint {
                VStack {
                    Spacer()
                    HStack {
                        Spacer()
                        if model.lockHint { UnlockButton(model: model) }
                        else if let passage = model.passage { PassageButton(model: model, text: passage) }
                    }
                }
                .padding(.trailing, 20)
                .padding(.bottom, shown ? 92 : 28)
            }
        }
        .animation(.easeOut(duration: 0.2), value: model.level)
        .animation(.easeOut(duration: 0.18), value: model.skipFlash != nil)
        .animation(.easeOut(duration: 0.2), value: model.boosted)
        .animation(.easeOut(duration: 0.2), value: model.groupLine)
        .animation(.spring(duration: 0.3), value: model.passage)
        .animation(.easeOut(duration: 0.2), value: model.lockHint)
    }
}

/// A double tap on one side: a small dark pill on that side, « 10 s », « 20 s »… while the taps go on. No circle
/// over the picture.
private struct SkipBadge: View {
    let flash: PlayerModel.SkipFlash

    var body: some View {
        GeometryReader { box in
            Label {
                Text("\(flash.seconds) s")
                    .font(MiraFont.subheadline)
                    .monospacedDigit()
                    .contentTransition(.numericText())
            } icon: {
                Image(systemName: flash.forward ? "forward.fill" : "backward.fill")
                    .font(.system(size: 13, weight: .bold))
                    .symbolEffect(.bounce, value: flash.tap)
            }
            .labelStyle(SkipLabelStyle(forward: flash.forward))
            .padding(.horizontal, 14)
            .frame(height: 36)
            .glassEffect(.regular.tint(.black.opacity(0.4)), in: .capsule)
            .position(x: box.size.width * (flash.forward ? 0.8 : 0.2), y: box.size.height / 2)
        }
        .allowsHitTesting(false)
        .animation(.snappy(duration: 0.2), value: flash.seconds)
        .accessibilityHidden(true)
    }
}

/// The arrows on the side the title goes: after « 10 s » forward, before it back.
private struct SkipLabelStyle: LabelStyle {
    let forward: Bool

    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 7) {
            if forward {
                configuration.title
                configuration.icon
            } else {
                configuration.icon
                configuration.title
            }
        }
    }
}

/// Brightness or volume while the finger sweeps a side.
private struct LevelBar: View {
    let level: PlayerModel.Level

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.system(size: 16, weight: .semibold))
                .frame(width: 22)
                .contentTransition(.symbolEffect(.replace))
            GeometryReader { box in
                ZStack(alignment: .leading) {
                    Capsule().fill(.white.opacity(0.25))
                    Capsule().fill(.white).frame(width: box.size.width * min(1, max(0, level.value)))
                }
            }
            .frame(width: 150, height: 5)
        }
        .padding(.horizontal, 16)
        .frame(height: 40)
        .glassEffect(.regular, in: .capsule)
        .accessibilityHidden(true)
    }

    private var symbol: String {
        switch level.kind {
        case .brightness: level.value < 0.35 ? "sun.min.fill" : "sun.max.fill"
        case .volume: level.value <= 0.001 ? "speaker.slash.fill" : level.value < 0.35 ? "speaker.wave.1.fill" : level.value < 0.7 ? "speaker.wave.2.fill" : "speaker.wave.3.fill"
        }
    }
}

/// « Skip intro », « Next episode »: white, as Mira web's, and filling up while the next episode counts down.
private struct PassageButton: View {
    @Bindable var model: PlayerModel
    let text: String

    var body: some View {
        Button {
            model.actions?.passageTapped()
        } label: {
            Label(text, systemImage: model.passageIsNext ? "forward.end.fill" : "forward.fill")
                .font(MiraFont.callout)
                .foregroundStyle(Palette.onAccent)
                .padding(.horizontal, 20)
                .frame(height: 48)
                .background {
                    GeometryReader { box in
                        ZStack(alignment: .leading) {
                            Palette.accent
                            if let countdown = model.nextCountdown {
                                Color.black.opacity(0.14).frame(width: box.size.width * min(1, max(0, countdown)))
                            }
                        }
                    }
                }
                .clipShape(.capsule)
                .shadow(color: .black.opacity(0.35), radius: 12, y: 4)
        }
        .buttonStyle(.plain)
        .region("passage", model)
        .accessibilityIdentifier("player.passage")
    }
}

/// Locked: only this, for a moment after a tap.
private struct UnlockButton: View {
    @Bindable var model: PlayerModel

    var body: some View {
        Button {
            model.locked = false
            model.lockHint = false
            model.showControls()
        } label: {
            Label("Locked · Tap to unlock", systemImage: "lock.fill")
                .font(MiraFont.callout)
                .padding(.horizontal, 18)
                .frame(height: 46)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular.interactive(), in: .capsule)
        .region("unlock", model)
        .accessibilityIdentifier("player.unlock")
    }
}

/// « Connection to the server lost »: over the picture while Mira waits for Jellyfin.
private struct WaitingCard: View {
    var body: some View {
        VStack(spacing: 12) {
            ProgressView().controlSize(.large).tint(.white)
            Text("Connection to the server lost. Playback resumes as soon as it answers.")
                .font(MiraFont.callout)
                .multilineTextAlignment(.center)
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 20)
        .frame(maxWidth: 360)
        .glassEffect(.regular, in: .rect(cornerRadius: 24))
        .padding(.horizontal, 24)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("player.waiting")
    }
}

/// AirPlay: the television plays it; the screen says which.
private struct ExternalScreen: View {
    let name: String

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "tv")
                .font(.system(size: 54, weight: .light))
                .foregroundStyle(.white.opacity(0.85))
            Text("Playing on \(name)")
                .font(MiraFont.headline)
                .multilineTextAlignment(.center)
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Palette.bg)
    }
}

// MARK: Pieces

#if DEBUG
/// The UI tests' way to the states a finger holds (PlayerModel.showcase), in a corner: debug builds only.
private struct ShowcaseButton: View {
    let model: PlayerModel

    var body: some View {
        Button { model.showcase() } label: { Color.clear.frame(width: 44, height: 44).contentShape(.rect) }
            .buttonStyle(.plain)
            .region("showcase", model)
            .accessibilityIdentifier("player.showcase")
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
            .ignoresSafeArea()
    }
}
#endif

/// A round button of glass: an icon, its name for VoiceOver.
private struct RoundButton: View {
    let symbol: String
    let label: LocalizedStringKey
    let id: String
    var size: CGFloat = 44
    var glyph: CGFloat = 18
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: glyph, weight: .semibold))
                .frame(width: size, height: size)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular.interactive(), in: .circle)
        .accessibilityLabel(Text(label))
        .accessibilityIdentifier(id)
    }
}

private struct ToolButton: View {
    let symbol: String
    let label: LocalizedStringKey
    let id: String
    let action: () -> Void

    var body: some View {
        Button(action: action) { ToolIcon(symbol: symbol) }
            .buttonStyle(.plain)
            .accessibilityLabel(Text(label))
            .accessibilityIdentifier(id)
    }
}

private struct ToolIcon: View {
    let symbol: String

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: 18, weight: .semibold))
            .frame(width: 44, height: 44)
            .contentShape(.rect)
    }
}

/// Apple's AirPlay button: the televisions and speakers around, video ones first.
private struct AirPlayButton: UIViewRepresentable {
    func makeUIView(context: Context) -> AVRoutePickerView {
        let picker = AVRoutePickerView()
        picker.prioritizesVideoDevices = true
        picker.tintColor = .white
        picker.activeTintColor = .white
        return picker
    }

    func updateUIView(_ uiView: AVRoutePickerView, context: Context) {}
}

extension View {
    /// Where this control is on the screen, so that a touch there is the control's and not the picture's
    /// (PlayerController's gestures leave it alone).
    fileprivate func region(_ name: String, _ model: PlayerModel, when shown: Bool = true) -> some View {
        onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { frame in
            model.regions[name] = shown ? frame : nil
        }
        .onChange(of: shown) { if !shown { model.regions[name] = nil } }
        .onDisappear { model.regions[name] = nil }
    }
}
