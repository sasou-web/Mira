import Darwin
import Foundation
import SwiftUI

/// « En ce moment sur Jellyfin »: who plays what on the server (its Sessions), and how: the file as it is, or converted,
/// on which hardware and why. The rules of Mira for Windows and Mac (Mira.Core/Viewers.cs), with their shared cases in
/// tests/fixtures/viewers.json.
enum Viewers {
    /// A session as Jellyfin 12.1 lists it (Sessions?activeWithinSeconds=960); everything optional, as a session
    /// Jellyfin half describes is left out rather than the whole list refused.
    struct Session: Decodable, Sendable {
        struct PlayState: Decodable, Sendable {
            var positionTicks: Int64?
            var isPaused: Bool?
            var playMethod: String?
        }
        struct Conversion: Decodable, Sendable, Equatable {
            var audioCodec: String?
            var videoCodec: String?
            var isVideoDirect: Bool?
            var isAudioDirect: Bool?
            var bitrate: Int?
            var hardwareAccelerationType: String?
            var transcodeReasons: [String]?
        }
        struct Playing: Decodable, Sendable {
            var name: String?
            var type: String?
            var seriesName: String?
            var parentIndexNumber: Int?
            var indexNumber: Int?
            var runTimeTicks: Int64?
        }
        var id: String?
        var userName: String?
        var client: String?
        var deviceName: String?
        var deviceId: String?
        var remoteEndPoint: String?
        var supportsRemoteControl: Bool?
        var playState: PlayState?
        var transcodingInfo: Conversion?
        var nowPlayingItem: Playing?
    }

    struct Viewer: Identifiable, Sendable, Equatable {
        var id: String
        /// This iPhone's own session.
        var own: Bool
        var user: String
        var device: String
        var address: String?
        var type: String
        var name: String
        var series: String?
        var season: Int?
        var episode: Int?
        var position: Double
        var duration: Double
        var paused: Bool
        var method: String
        var conversion: Session.Conversion?
        /// Its device takes « Stop » (a WebSocket that takes commands).
        var canStop: Bool
    }

    /// The sessions playing something; `ownDevice` is this iPhone's id.
    static func parse(_ sessions: [Session], ownDevice: String?) -> [Viewer] {
        sessions.compactMap { session in
            guard let id = nonEmpty(session.id), let item = session.nowPlayingItem else { return nil }
            let state = session.playState
            return Viewer(
                id: id, own: ownDevice.map { !$0.isEmpty && nonEmpty(session.deviceId) == $0 } ?? false,
                user: nonEmpty(session.userName) ?? "", device: nonEmpty(session.deviceName) ?? nonEmpty(session.client) ?? "",
                address: nonEmpty(session.remoteEndPoint), type: nonEmpty(item.type) ?? "", name: nonEmpty(item.name) ?? "",
                series: nonEmpty(item.seriesName), season: item.parentIndexNumber, episode: item.indexNumber,
                position: Double(state?.positionTicks ?? 0) / 1e7, duration: Double(item.runTimeTicks ?? 0) / 1e7,
                paused: state?.isPaused == true, method: nonEmpty(state?.playMethod) ?? "", conversion: session.transcodingInfo,
                canStop: session.supportsRemoteControl == true)
        }
    }

    private static func nonEmpty(_ text: String?) -> String? { text.flatMap { $0.isEmpty ? nil : $0 } }

    // MARK: Where

    enum Place: Equatable, Sendable {
        case tailscale, home, internet

        var text: String {
            switch self {
            case .tailscale: "Tailscale"
            case .home: String(localized: "home network")
            case .internet: "Internet"
            }
        }
    }

    /// Where the device is: Tailscale, the home network, the Internet; nil for the server's own PC or no address.
    static func place(_ address: String?) -> Place? {
        var raw = (address ?? "").trimmingCharacters(in: .whitespaces)
        if raw.hasPrefix("["), raw.hasSuffix("]") { raw = String(raw.dropFirst().dropLast()) }
        if let scope = raw.firstIndex(of: "%") { raw = String(raw[..<scope]) }
        if let v4 = ipv4(raw) { return place(v4) }
        var v6 = in6_addr()
        guard raw.contains(":"), inet_pton(AF_INET6, raw, &v6) == 1 else { return nil }
        let bytes = withUnsafeBytes(of: &v6) { Array($0) }
        // ::ffff:a.b.c.d, an IPv4 address written as IPv6.
        if bytes[0..<10].allSatisfy({ $0 == 0 }), bytes[10] == 0xff, bytes[11] == 0xff { return place(Array(bytes[12..<16])) }
        if bytes[0..<15].allSatisfy({ $0 == 0 }), bytes[15] == 1 { return nil }
        // Tailscale's own IPv6 range, fd7a:115c:a1e0::/48, before the private ranges it belongs to.
        if bytes[0..<6] == [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0] { return .tailscale }
        let home = (bytes[0] == 0xfe && bytes[1] & 0xc0 == 0x80) || bytes[0] & 0xfe == 0xfc
        return home ? .home : .internet
    }

    private static func place(_ v4: [UInt8]) -> Place? {
        let (a, b) = (v4[0], v4[1])
        if a == 127 { return nil }
        if a == 100, b >= 64, b < 128 { return .tailscale }
        let home = a == 10 || (a == 172 && b >= 16 && b < 32) || (a == 192 && b == 168) || (a == 169 && b == 254)
        return home ? .home : .internet
    }

    /// Four numbers of 0 to 255, nothing else (inet_pton would take « 010.1.1.1 » the same, Mira web does not care).
    private static func ipv4(_ text: String) -> [UInt8]? {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        let bytes = parts.compactMap { part -> UInt8? in
            guard (1...3).contains(part.count), part.allSatisfy(\.isASCII), part.allSatisfy(\.isNumber) else { return nil }
            return UInt8(part)
        }
        return bytes.count == 4 ? bytes : nil
    }

    // MARK: Words

    /// « ogsas · iPhone · Tailscale »; `own` for this iPhone's own session.
    static func who(_ viewer: Viewer, own: String = String(localized: "this iPhone")) -> String {
        if viewer.own { return own }
        return [viewer.user, viewer.device, place(viewer.address)?.text].compactMap { $0?.isEmpty == false ? $0 : nil }.joined(separator: " · ")
    }

    /// « Courte HLS · S1 E2 · Le départ », « Signes Test (2022) ».
    static func title(_ viewer: Viewer) -> String {
        guard viewer.type == "Episode", let series = viewer.series else { return viewer.name }
        let code: String? = viewer.episode.map { episode in
            viewer.season.map { String(localized: "S\($0) E\(episode)") } ?? String(localized: "E\(episode)")
        }
        return [series, code, viewer.name].compactMap { $0?.isEmpty == false ? $0 : nil }.joined(separator: " · ")
    }

    /// « Signes Test (2022) — 12:34 / 40:00, paused ».
    static func what(_ viewer: Viewer) -> String {
        let title = title(viewer)
        let time = viewer.duration > 0 ? "\(clock(viewer.position)) / \(clock(viewer.duration))" : clock(viewer.position)
        return viewer.paused ? String(localized: "\(title) — \(time), paused") : "\(title) — \(time)"
    }

    /// « 1:02:03 », « 12:34 ».
    static func clock(_ seconds: Double) -> String {
        let total = seconds.isFinite ? max(0, Int(seconds.rounded(.down))) : 0
        let (h, m, s) = (total / 3600, total / 60 % 60, total % 60)
        func two(_ n: Int) -> String { n < 10 ? "0\(n)" : "\(n)" }
        return h >= 1 ? "\(h):\(two(m)):\(two(s))" : "\(total / 60):\(two(s))"
    }

    /// How it plays.
    enum Delivery: Equatable, Sendable { case direct, remuxed, converted }

    static func delivery(_ viewer: Viewer) -> Delivery {
        let c = viewer.conversion
        if viewer.method == "DirectPlay" || (viewer.method.isEmpty && c == nil) { return .direct }
        if viewer.method == "DirectStream" || (c?.isVideoDirect == true && c?.isAudioDirect == true) { return .remuxed }
        return .converted
    }

    /// « Direct play: the file as it is », « File repackaged, not converted », or « Converting: video to VP9, audio to
    /// Opus, by the processor · 1.6 Mb/s ».
    static func how(_ viewer: Viewer) -> String {
        switch delivery(viewer) {
        case .direct: return String(localized: "Direct play: the file as it is")
        case .remuxed: return String(localized: "File repackaged, not converted")
        case .converted:
            guard let c = viewer.conversion else { return String(localized: "Converting") }
            let video = c.isVideoDirect == true ? String(localized: "video copied") : String(localized: "video to \(codec(c.videoCodec))")
            let audio = c.isAudioDirect == true ? String(localized: "audio copied") : String(localized: "audio to \(codec(c.audioCodec))")
            var parts = [video, audio]
            if c.isVideoDirect != true {
                if let hardware = c.hardwareAccelerationType, !hardware.isEmpty, hardware != "none" {
                    parts.append(String(localized: "by the graphics card (\(makers[hardware] ?? hardware))"))
                } else {
                    parts.append(String(localized: "by the processor"))
                }
            }
            var text = String(localized: "Converting: \(parts.joined(separator: ", "))")
            if let bitrate = c.bitrate, bitrate > 0 {
                let rate = (Double(bitrate) / 1e6).formatted(.number.precision(.fractionLength(1)).grouping(.never))
                text += " · " + String(localized: "\(rate) Mb/s")
            }
            return text
        }
    }

    /// « Why: file format, video codec », nil when Jellyfin says nothing (or plays the file as it is).
    static func why(_ viewer: Viewer) -> String? {
        guard delivery(viewer) != .direct, let reasons = viewer.conversion?.transcodeReasons, !reasons.isEmpty else { return nil }
        var seen = Set<String>()
        let words = reasons.map(reason).filter { seen.insert($0).inserted }
        return String(localized: "Why: \(words.joined(separator: ", "))")
    }

    private static let makers = ["nvenc": "NVIDIA", "qsv": "Intel", "amf": "AMD"]

    private static let codecs = [
        "h264": "H.264", "hevc": "HEVC", "h265": "HEVC", "av1": "AV1", "vp9": "VP9", "vp8": "VP8", "mpeg2video": "MPEG-2",
        "mpeg4": "MPEG-4", "vc1": "VC-1", "aac": "AAC", "ac3": "Dolby Digital", "eac3": "Dolby Digital Plus",
        "truehd": "Dolby TrueHD", "dts": "DTS", "flac": "FLAC", "mp3": "MP3", "opus": "Opus", "vorbis": "Vorbis", "alac": "ALAC",
    ]

    static func codec(_ name: String?) -> String {
        let key = (name ?? "").trimmingCharacters(in: .whitespaces).lowercased()
        if key.isEmpty { return "?" }
        return codecs[key] ?? (key.hasPrefix("pcm") ? "PCM" : key.uppercased())
    }

    /// Jellyfin's TranscodeReasons in words; the name itself for one Mira does not know.
    static func reason(_ name: String) -> String {
        switch name {
        case "ContainerNotSupported": String(localized: "file format")
        case "VideoCodecNotSupported": String(localized: "video codec")
        case "AudioCodecNotSupported": String(localized: "audio codec")
        case "SubtitleCodecNotSupported": String(localized: "subtitles to burn in")
        case "AudioIsExternal": String(localized: "separate audio track")
        case "SecondaryAudioNotSupported": String(localized: "secondary audio track")
        case "VideoProfileNotSupported": String(localized: "video profile")
        case "VideoLevelNotSupported": String(localized: "video level")
        case "VideoResolutionNotSupported": String(localized: "resolution")
        case "VideoBitDepthNotSupported": String(localized: "10-bit video")
        case "VideoFramerateNotSupported": String(localized: "frame rate")
        case "RefFramesNotSupported": String(localized: "reference frames")
        case "AnamorphicVideoNotSupported": String(localized: "anamorphic video")
        case "InterlacedVideoNotSupported": String(localized: "interlaced video")
        case "AudioChannelsNotSupported": String(localized: "audio channels")
        case "AudioProfileNotSupported": String(localized: "audio profile")
        case "AudioSampleRateNotSupported": String(localized: "audio sample rate")
        case "AudioBitDepthNotSupported": String(localized: "high-resolution audio")
        case "ContainerBitrateExceedsLimit": String(localized: "bitrate above the chosen quality")
        case "VideoBitrateNotSupported": String(localized: "video bitrate")
        case "AudioBitrateNotSupported": String(localized: "audio bitrate")
        case "UnknownVideoStreamInfo": String(localized: "video poorly described")
        case "UnknownAudioStreamInfo": String(localized: "audio poorly described")
        case "DirectPlayError": String(localized: "direct play failed")
        case "VideoRangeTypeNotSupported": "HDR"
        case "VideoCodecTagNotSupported": String(localized: "video codec tag")
        case "StreamCountExceedsLimit": String(localized: "too many tracks")
        default: name
        }
    }
}

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
