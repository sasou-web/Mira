import Foundation
import SwiftUI

extension JellyfinClient {
    /// The sessions Jellyfin heard from lately (all of them for an administrator), as Mira for Windows reads them.
    func sessions() async throws -> [Viewers.Session] {
        try await get("Sessions", ["activeWithinSeconds": "960"])
    }

    /// Asks a session to stop playing: one whose WebSocket takes commands.
    func stopSession(_ id: String) async throws {
        try await send("POST", "Sessions/\(id)/Playing/Stop")
    }
}

/// « Now on Jellyfin », from Settings → Administration: read again every 5 seconds while it shows and Mira is in front.
struct NowOnJellyfinView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.scenePhase) private var phase
    @State private var viewers: [Viewers.Viewer]?
    @State private var failure: String?
    @State private var notice: String?
    @State private var stopping: Viewers.Viewer?
    @State private var round = 0

    var body: some View {
        List {
            if let viewers {
                Section {
                    if viewers.isEmpty {
                        Text("Nobody is watching anything on Jellyfin right now.").foregroundStyle(Palette.muted)
                    }
                    ForEach(viewers) { viewer in row(viewer) }
                } header: {
                    if !viewers.isEmpty { Text("Playing now: \(viewers.count)") }
                } footer: {
                    if !viewers.isEmpty {
                        Text("Jellyfin says how it sends each title: the file as it is, or converted, and why. Converting on the processor can make playback stutter; Mira for Windows can hand it to the graphics card. « Stop » ends playback on the device that watches, without changing anything on its account.")
                    }
                }
                .listRowBackground(Palette.surface)
            } else if let failure {
                Section {
                    Text(verbatim: failure).foregroundStyle(Palette.muted)
                    Button("Try again") { Task { await load() } }
                }
                .listRowBackground(Palette.surface)
            } else {
                Section {
                    HStack(spacing: 10) {
                        ProgressView()
                        Text("Reading Jellyfin’s sessions…").foregroundStyle(Palette.muted)
                    }
                }
                .listRowBackground(Palette.surface)
            }
            if let notice {
                Section {
                    Text(verbatim: notice).font(MiraFont.footnote).foregroundStyle(Palette.muted).accessibilityIdentifier("now.notice")
                }
                .listRowBackground(Palette.surface)
            }
        }
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .foldsTabBar()
        .background(Palette.bg)
        .navigationTitle("Now on Jellyfin")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await load() }
        .task(id: TaskKey(active: phase == .active, round: round)) {
            guard phase == .active else { return }
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: .seconds(5))
            }
        }
        .confirmationDialog("Stop this playback?", isPresented: Binding(get: { stopping != nil }, set: { if !$0 { stopping = nil } }), titleVisibility: .visible, presenting: stopping) { viewer in
            Button("Stop playback", role: .destructive) { Task { await stop(viewer) } }
        } message: { viewer in
            Text("« \(Viewers.title(viewer)) » stops on \(Viewers.who(viewer)), which says so on screen. Nothing else changes for this account.")
        }
    }

    private struct TaskKey: Hashable { var active: Bool; var round: Int }

    private func row(_ viewer: Viewers.Viewer) -> some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text(verbatim: Viewers.who(viewer)).font(MiraFont.headline).foregroundStyle(Palette.ink)
                Text(verbatim: Viewers.what(viewer)).foregroundStyle(Palette.ink)
                Text(verbatim: Viewers.how(viewer)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                if let why = Viewers.why(viewer) {
                    Text(verbatim: why).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityElement(children: .combine)
            if viewer.canStop, !viewer.own {
                Button("Stop") { stopping = viewer }
                    .buttonStyle(.bordered)
                    .accessibilityLabel(Text("Stop playback on \(Viewers.who(viewer))"))
            }
        }
        .padding(.vertical, 4)
        .accessibilityIdentifier("now.\(viewer.id)")
    }

    private func load() async {
        guard let client = app.client else { return }
        do {
            let found = Viewers.parse(try await client.sessions(), ownDevice: app.device.id)
            viewers = found
            failure = nil
        } catch is CancellationError {
        } catch {
            // A list already shown stays (a hiccup on the way); an empty screen says why.
            if viewers == nil { failure = Messages.text(error) } else { notice = Messages.text(error) }
        }
    }

    private func stop(_ viewer: Viewers.Viewer) async {
        guard let client = app.client else { return }
        let name = Viewers.who(viewer)
        do {
            try await client.stopSession(viewer.id)
            notice = String(localized: "Asked \(name) to stop.")
        } catch JellyfinError.forbidden {
            notice = String(localized: "Only a Jellyfin administrator can stop another account’s playback.")
        } catch JellyfinError.http(404) {
            notice = String(localized: "This playback has already ended.")
        } catch {
            notice = Messages.text(error)
        }
        // The device takes a moment to stop and tell Jellyfin.
        try? await Task.sleep(for: .seconds(1.5))
        round += 1
    }
}
