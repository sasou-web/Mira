import SwiftUI

/// « Play on… » from « … » of a title: the devices of this account that take Jellyfin's remote control (Mira on a PC or a
/// Mac, a Jellyfin app on a TV), read again every few seconds. A touch plays the title there, from where this account
/// left it, then opens the small remote; « Remote » steers what a device already plays.
struct PlayOnSheet: View {
    let item: Item
    var fromStart = false
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var phase
    @State private var targets: [RemoteRules.Target]?
    @State private var failure: String?
    @State private var sending: String?
    @State private var steering: RemoteRules.Target?

    var body: some View {
        NavigationStack {
            List {
                if let targets {
                    Section {
                        if targets.isEmpty {
                            Text("No other device can play right now. Open Mira on your PC or Mac, signed in to this server, and it shows up here.")
                                .foregroundStyle(Palette.muted)
                                .accessibilityIdentifier("playOn.empty")
                        }
                        ForEach(targets) { target in row(target) }
                    } footer: {
                        if !targets.isEmpty { Text("Jellyfin passes the title on: the other device plays it from its own copy or from the server.") }
                    }
                    .listRowBackground(Palette.surface)
                } else if failure == nil {
                    Section {
                        HStack(spacing: 10) {
                            ProgressView()
                            Text("Looking for your devices…").foregroundStyle(Palette.muted)
                        }
                    }
                    .listRowBackground(Palette.surface)
                }
                if let failure {
                    Section {
                        Text(verbatim: failure).font(MiraFont.footnote).foregroundStyle(Palette.danger).accessibilityIdentifier("playOn.failure")
                    }
                    .listRowBackground(Palette.surface)
                }
            }
            .font(MiraFont.body)
            .scrollContentBackground(.hidden)
            .background(Palette.bg)
            .navigationTitle(Text("Play on"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close") { dismiss() }
                }
            }
            .navigationDestination(item: $steering) { target in RemoteView(first: target) }
            .task(id: phase == .active) {
                guard phase == .active else { return }
                while !Task.isCancelled {
                    await load()
                    try? await Task.sleep(for: .seconds(4))
                }
            }
        }
        .presentationDetents([.medium, .large])
    }

    private func row(_ target: RemoteRules.Target) -> some View {
        HStack(spacing: 12) {
            Button {
                Task { await play(on: target) }
            } label: {
                HStack(spacing: 12) {
                    Image(systemName: target.client == "Mira" ? "desktopcomputer" : "tv")
                        .font(.title3)
                        .foregroundStyle(Palette.ink)
                        .frame(width: 32)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(verbatim: RemoteRules.name(target)).font(MiraFont.headline).foregroundStyle(Palette.ink)
                        if let title = RemoteRules.title(target) {
                            Text("Playing: \(title)").font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(2)
                        } else {
                            Text("Ready to play").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    if sending == target.id { ProgressView().accessibilityLabel(Text("Loading")) }
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .disabled(sending != nil)
            .accessibilityLabel(Text("Play on \(RemoteRules.name(target))"))
            .accessibilityIdentifier("playOn.\(target.deviceId)")
            if target.playing != nil {
                Button("Remote") { steering = target }
                    .buttonStyle(.bordered)
                    .accessibilityLabel(Text("Steer \(RemoteRules.name(target))"))
            }
        }
        .padding(.vertical, 4)
    }

    private func load() async {
        guard let client = app.client else { return }
        do {
            targets = try await client.remoteTargets()
            failure = nil
        } catch is CancellationError {
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch {
            failure = Messages.text(error)
        }
    }

    private func play(on target: RemoteRules.Target) async {
        guard let client = app.client, sending == nil else { return }
        sending = target.id
        defer { sending = nil }
        do {
            try await client.remotePlay(target.id, item: item.id, startTicks: RemoteRules.startTicks(item, fromStart: fromStart))
            failure = nil
            steering = target
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch JellyfinError.forbidden {
            failure = String(localized: "Jellyfin refused: this account can’t steer that device.")
        } catch {
            failure = Messages.text(error)
        }
    }
}

/// The small remote of a device playing elsewhere: what it plays and where, pause, ±10 s, next episode, stop, volume.
/// What it shows is read again every 2 seconds (the other device tells Jellyfin as it plays).
struct RemoteView: View {
    let first: RemoteRules.Target
    @Environment(AppModel.self) private var app
    @Environment(\.scenePhase) private var phase
    @State private var target: RemoteRules.Target?
    @State private var gone = false
    @State private var failure: String?
    @State private var volume: Double = 100
    @State private var sliding = false
    @ScaledMetric(relativeTo: .body) private var side: CGFloat = 56

    var body: some View {
        let shown = target ?? first
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                VStack(alignment: .leading, spacing: 6) {
                    Text(verbatim: RemoteRules.name(shown)).font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
                    if let title = RemoteRules.title(shown) {
                        Text(verbatim: title).font(MiraFont.body).foregroundStyle(Palette.ink).fixedSize(horizontal: false, vertical: true)
                            .accessibilityIdentifier("remote.title")
                        if shown.duration > 0 {
                            ProgressView(value: min(shown.position, shown.duration), total: shown.duration).tint(Palette.accent)
                                .accessibilityHidden(true)
                        }
                        Text(verbatim: time(shown)).font(MiraFont.footnote).foregroundStyle(Palette.muted).monospacedDigit()
                    } else {
                        Text("Nothing playing on it right now.").foregroundStyle(Palette.muted)
                    }
                }
                HStack(spacing: 18) {
                    roundButton("gobackward.10", Text("Back 10 seconds")) { await state("Rewind") }
                    roundButton(shown.paused ? "play.fill" : "pause.fill", shown.paused ? Text("Play") : Text("Pause"), large: true) {
                        target = shown.with(paused: !shown.paused)
                        await state("PlayPause")
                    }
                    .accessibilityIdentifier("remote.pause")
                    roundButton("goforward.10", Text("Forward 10 seconds")) { await state("FastForward") }
                }
                .frame(maxWidth: .infinity)
                .disabled(shown.playing == nil || gone)
                HStack(spacing: 18) {
                    if shown.series != nil {
                        roundButton("forward.end.fill", Text("Next episode")) { await state("NextTrack") }
                    }
                    roundButton("stop.fill", Text("Stop")) { await state("Stop") }
                        .accessibilityIdentifier("remote.stop")
                }
                .frame(maxWidth: .infinity)
                .disabled(shown.playing == nil || gone)
                HStack(spacing: 12) {
                    Button {
                        target = shown.with(muted: !shown.muted)
                        Task { await command("ToggleMute") }
                    } label: {
                        Image(systemName: shown.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                            .font(.body.weight(.semibold)).foregroundStyle(Palette.ink).frame(width: 44, height: 44).contentShape(.circle)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(shown.muted ? Text("Turn the sound on") : Text("Turn the sound off"))
                    Slider(value: $volume, in: 0...100, step: 5, onEditingChanged: { editing in
                        sliding = editing
                        if !editing { Task { await command("SetVolume", ["Volume": String(Int(volume))]) } }
                    })
                    .tint(Palette.accent)
                    .accessibilityLabel(Text("Volume"))
                    .accessibilityValue(Text(verbatim: "\(Int(volume)) %"))
                }
                .disabled(gone)
                if gone {
                    Text("This device no longer answers: Mira was closed there, or the computer went to sleep.").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let failure {
                    Text(verbatim: failure).font(MiraFont.footnote).foregroundStyle(Palette.danger).fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(24)
        }
        .background(Palette.bg)
        .navigationTitle(Text("Remote"))
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { volume = Double(first.volume) }
        .task(id: phase == .active) {
            guard phase == .active else { return }
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(2))
            }
        }
    }

    private func roundButton(_ symbol: String, _ label: Text, large: Bool = false, action: @escaping @MainActor () async -> Void) -> some View {
        Button {
            Task { await action() }
        } label: {
            Image(systemName: symbol)
                .font(large ? .title.weight(.bold) : .title3.weight(.semibold))
                .foregroundStyle(large ? Palette.onAccent : Palette.ink)
                .frame(width: large ? side * 1.25 : side, height: large ? side * 1.25 : side)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .glassEffect(large ? Glass.regular.tint(Palette.accent).interactive() : Glass.regular.interactive(), in: .circle)
        .accessibilityLabel(label)
    }

    private func time(_ target: RemoteRules.Target) -> String {
        let clock = target.duration > 0 ? "\(Viewers.clock(target.position)) / \(Viewers.clock(target.duration))" : Viewers.clock(target.position)
        return target.paused ? String(localized: "\(clock), paused") : clock
    }

    private func load() async {
        guard let client = app.client else { return }
        do {
            let found = try await client.remoteTargets().first { $0.deviceId == first.deviceId }
            gone = found == nil
            if let found {
                target = found
                if !sliding { volume = Double(found.volume) }
            }
        } catch is CancellationError {
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch {
            failure = Messages.text(error)
        }
    }

    private func state(_ command: String) async {
        guard let client = app.client else { return }
        await run { try await client.remoteState((target ?? first).id, command) }
    }

    private func command(_ name: String, _ arguments: [String: String] = [:]) async {
        guard let client = app.client else { return }
        await run { try await client.remoteCommand((target ?? first).id, name, arguments) }
    }

    private func run(_ work: () async throws -> Void) async {
        do {
            try await work()
            failure = nil
            // The other device takes a moment to do it and tell Jellyfin.
            try? await Task.sleep(for: .seconds(0.7))
            await load()
        } catch JellyfinError.unauthorized {
            app.sessionEnded()
        } catch JellyfinError.forbidden {
            failure = String(localized: "Jellyfin refused: this account can’t steer that device.")
        } catch JellyfinError.http(404) {
            gone = true
        } catch {
            failure = Messages.text(error)
        }
    }
}

private extension RemoteRules.Target {
    func with(paused: Bool) -> Self { var copy = self; copy.paused = paused; return copy }
    func with(muted: Bool) -> Self { var copy = self; copy.muted = muted; return copy }
}
