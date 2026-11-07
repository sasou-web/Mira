import PhotosUI
import SwiftUI
import UIKit

/// Settings: the account, what only an administrator does here (the rest is in Mira for Windows and Mac), playback,
/// this iPhone, the account's devices, and what Mira is.
struct SettingsView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.openURL) private var openURL
    @State private var confirmSignOut = false
    @State private var me: AccountInfo?
    @State private var jellyfinVersion: String?
    @State private var quickConnect = false
    @State private var approving = false
    @State private var photo: PhotosPickerItem?
    @State private var sendingPhoto = false
    @State private var photoFailure: String?

    var body: some View {
        @Bindable var preferences = app.preferences
        List {
            if let session = app.session {
                Section {
                    HStack(spacing: 14) {
                        PhotosPicker(selection: $photo, matching: .images, photoLibrary: .shared()) {
                            Avatar(name: session.userName, url: me?.primaryImageTag.map { app.client?.url("Users/\(session.userId)/Images/Primary", ["maxWidth": "180", "tag": $0]) } ?? nil)
                                .overlay(alignment: .bottomTrailing) {
                                    Image(systemName: sendingPhoto ? "ellipsis" : "pencil")
                                        .font(.system(size: 11, weight: .bold))
                                        .foregroundStyle(Palette.onAccent)
                                        .frame(width: 22, height: 22)
                                        .background(Palette.accent, in: .circle)
                                        .overlay(Circle().stroke(Palette.surface, lineWidth: 3))
                                        .offset(x: 2, y: 2)
                                }
                        }
                        .buttonStyle(.plain)
                        .disabled(sendingPhoto)
                        .accessibilityLabel(me?.primaryImageTag == nil ? Text("Add your photo") : Text("Change your photo"))
                        .accessibilityIdentifier("settings.photo")
                        VStack(alignment: .leading, spacing: 2) {
                            Text(verbatim: session.userName).font(MiraFont.headline).foregroundStyle(Palette.ink)
                            Text("Server “\(session.serverName)”").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                            Text(verbatim: session.server.absoluteString).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                                .lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                        }
                    }
                    .padding(.vertical, 4)
                    .accessibilityElement(children: .contain)
                    if let photoFailure {
                        Text(verbatim: photoFailure).font(MiraFont.footnote).foregroundStyle(Palette.danger)
                    }
                    NavigationLink {
                        ReportView()
                    } label: {
                        row("Your recap", "What you watched this month and this year.", symbol: "clock.arrow.circlepath")
                    }
                    .accessibilityIdentifier("settings.report")
                    if app.party.guest == nil {
                        NavigationLink {
                            KeptView()
                        } label: {
                            SettingsRow("On this iPhone", verbatim: keptLine, symbol: "arrow.down.circle")
                        }
                        .accessibilityIdentifier("settings.kept")
                    }
                }
                .listRowBackground(Palette.surface)

                TailscaleSection()

                if me?.isAdministrator == true {
                    Section {
                        NavigationLink {
                            NowOnJellyfinView()
                        } label: {
                            row("Now on Jellyfin", "Who watches what, and whether Jellyfin converts it.", symbol: "server.rack")
                        }
                        .accessibilityIdentifier("settings.now")
                        NavigationLink {
                            DownloadsView()
                        } label: {
                            row("Download on the server PC", "Search for a title: TorLink downloads it there, Mira files it in your library.", symbol: "arrow.down.circle")
                        }
                        .accessibilityIdentifier("settings.downloads")
                    } header: {
                        Text("Administration")
                    } footer: {
                        Text("Profiles, titles to fix, automatic subtitles and your party’s guests are in Mira for Windows and Mac.")
                    }
                    .listRowBackground(Palette.surface)
                }

                Section {
                    Picker("Quality", selection: $preferences.quality) {
                        ForEach(Preferences.qualities, id: \.self) { value in Text(verbatim: qualityName(value)).tag(value) }
                    }
                    .accessibilityIdentifier("settings.quality")
                    Picker(selection: $preferences.mobileData) {
                        ForEach(PlaybackPlan.mobileChoices, id: \.self) { value in Text(verbatim: mobileDataName(value)).tag(value) }
                    } label: {
                        row("Mobile data", "On cellular or a personal hotspot. Data saver uses about 0.9 GB an hour, and stays on with Low Data Mode.")
                    }
                    .accessibilityIdentifier("settings.mobileData")
                    .onChange(of: preferences.mobileData) { app.mobileDataChanged() }
                    Toggle(isOn: $preferences.resume) {
                        row("Resume where you left off", "Otherwise, every title starts from the beginning.")
                    }
                    .accessibilityIdentifier("settings.resume")
                    if preferences.resume {
                        Picker("Resume a little earlier", selection: $preferences.resumeBack) {
                            ForEach(Preferences.resumeBackChoices, id: \.self) { seconds in
                                Text(seconds == 0 ? String(localized: "No") : String(localized: "\(seconds) s earlier")).tag(seconds)
                            }
                        }
                        .accessibilityIdentifier("settings.resumeBack")
                    }
                    Toggle(isOn: $preferences.autoNext) {
                        row("Next episode automatically", "10 seconds into the end credits when they are marked, otherwise at the end of the episode. A button offers it sooner.")
                    }
                    .accessibilityIdentifier("settings.autoNext")
                    Toggle(isOn: $preferences.stillWatching) {
                        row("Ask if you’re still watching", "After 3 episodes in a row without touching anything, Mira stops and asks before the next one.")
                    }
                    .accessibilityIdentifier("settings.stillWatching")
                    Toggle(isOn: $preferences.hideSpoilers) {
                        row("Hide spoilers", "Episodes you haven’t started yet show a blurred picture and no summary.")
                    }
                    .accessibilityIdentifier("settings.hideSpoilers")
                    Toggle(isOn: $preferences.autoSkip) {
                        row("Skip intros and recaps", "When Jellyfin or the file’s chapters mark them, playback jumps over them. Otherwise, a button offers it.")
                    }
                    .accessibilityIdentifier("settings.autoSkip")
                    Picker("Audio language", selection: Binding(get: { preferences.audioOrder }, set: { preferences.audioLanguages = $0 })) {
                        ForEach(Preferences.audioOrders, id: \.self) { order in Text(verbatim: orderName(order)).tag(order) }
                    }
                    .accessibilityIdentifier("settings.audio")
                    Picker("Subtitle language", selection: Binding(get: { preferences.subtitleOrder }, set: { preferences.subtitleLanguages = $0 })) {
                        ForEach(Preferences.subtitleOrders, id: \.self) { order in Text(verbatim: orderName(order)).tag(order) }
                    }
                    .accessibilityIdentifier("settings.subtitles")
                } header: {
                    Text("Playback")
                } footer: {
                    Text("Languages choose the tracks at the start of each title. On a title’s page, Audio and Subtitles choose others: Mira keeps them for the next episodes of the series. These choices are kept on your Jellyfin, with the tracks of each series and the titles taken out of Continue watching: you find them on Windows and Mac.")
                }
                .listRowBackground(Palette.surface)

                Section {
                    LabeledContent {
                        EmptyView()
                    } label: {
                        row("Apple’s player", "Subtitles look as set in iPhone Settings: Accessibility, Subtitles & Captioning, Style.", symbol: "captions.bubble")
                    }
                    Button {
                        openSettings()
                    } label: {
                        row("Language", "Mira follows your iPhone’s language. To choose another one for Mira only: Settings, Apps, Mira, Language.", symbol: "globe")
                    }
                    LabeledContent {
                        EmptyView()
                    } label: {
                        row("Text size", "Mira follows the text size of your iPhone: Settings, Display & Brightness, Text Size, or Control Center.", symbol: "textformat.size")
                    }
                    NavigationLink {
                        HomeRowsView()
                    } label: {
                        row("Home rows", "Their order, and which show, on this iPhone.", symbol: "rectangle.grid.1x2")
                    }
                    .accessibilityIdentifier("settings.homeRows")
                    NavigationLink {
                        PrivacyView()
                    } label: {
                        row("Privacy", "Everything that goes out on the network, and how to stop it.", symbol: "hand.raised")
                    }
                    .accessibilityIdentifier("settings.privacy")
                } header: {
                    Text("This iPhone")
                }
                .listRowBackground(Palette.surface)

                Section {
                    if quickConnect {
                        Button {
                            approving = true
                        } label: {
                            row("Authorize a device", "Signs a device that shows a code into your account, without typing your password there.", symbol: "iphone.and.arrow.forward")
                        }
                        .accessibilityIdentifier("settings.approve")
                    }
                    Button(role: .destructive) {
                        confirmSignOut = true
                    } label: {
                        Label("Sign out", systemImage: "rectangle.portrait.and.arrow.right").font(MiraFont.body)
                    }
                    .accessibilityIdentifier("settings.signout")
                } header: {
                    Text("Account")
                }
                .listRowBackground(Palette.surface)
            }

            Section {
                LabeledContent("Version", value: [app.device.version, jellyfinVersion.map { "Jellyfin \($0)" }].compactMap { $0 }.joined(separator: " · "))
                Link(destination: URL(string: "https://github.com/sasou-web/Mira")!) {
                    Label("Source code", systemImage: "chevron.left.forwardslash.chevron.right")
                }
                // A text file the person sends by hand when something goes wrong (Diagnostics): nothing leaves by itself.
                ShareLink(item: report(), preview: SharePreview("Mira report")) {
                    Label("Share a report for a problem", systemImage: "doc.text.magnifyingglass")
                }
                .accessibilityIdentifier("settings.report")
            } header: {
                Text("About")
            } footer: {
                Text("Mira talks only to your Jellyfin server: no account of ours, no analytics, no ads. Your progress is kept on your Jellyfin: it follows you to Windows and Mac.")
            }
            .listRowBackground(Palette.surface)
        }
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .foldsTabBar()
        .background(Palette.bg)
        .navigationTitle("Settings")
        .confirmationDialog("Sign out of Jellyfin on this iPhone?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { app.signOut() }
                .accessibilityIdentifier("settings.signout.confirm")
        } message: {
            if !app.keeper.copies.isEmpty {
                Text("The titles kept on this iPhone are removed from it.")
            }
        }
        .sheet(isPresented: $approving) { ApproveDeviceSheet() }
        .task { await load() }
        .refreshable { await load() }
        .onChange(of: photo) { _, item in
            guard let item else { return }
            photo = nil
            Task { await send(item) }
        }
    }

    /// The report as it stands now, written only when shared: versions, settings, the way to Jellyfin, the last playbacks.
    private func report() -> DiagnosticFile {
        let version = app.device.version, server = app.client?.server.absoluteString, jellyfin = jellyfinVersion
        let names: [String?] = [app.session?.userName, UIDevice.current.name]
        let facts: [(String, String?)] = [
            ("Signed in", app.session == nil ? "no" : "yes"),
            ("Quality", app.preferences.quality),
            ("Mobile data", app.preferences.mobileData),
            ("Connection", app.cost.constrained ? "low data mode" : app.cost.expensive ? "mobile data" : "unmetered"),
            ("Built-in Tailscale", String(describing: app.tailnet.phase)),
            ("System", "\(UIDevice.current.systemName) \(UIDevice.current.systemVersion)"),
            ("Device", UIDevice.current.model),
        ]
        return DiagnosticFile(name: Diagnostics.fileName(Date())) {
            let way = await Way.shared.current()?.kind.rawValue
            let playbacks = await MainActor.run { Diagnostics.playbacks }
            return Diagnostics.build(version: version, server: server, jellyfin: jellyfin, facts: facts + [("Way to Jellyfin", way)], playbacks: playbacks, now: Date(), names: names)
        }
    }

    private func load() async {
        guard let client = app.client else { return }
        async let account = try? client.me()
        async let info = try? client.publicInfo()
        async let quick = client.quickConnectEnabled()
        me = await account
        jellyfinVersion = await info?.version
        quickConnect = await quick
    }

    /// The picture chosen, made a square JPEG of 512 px (AccountPhoto) and given to Jellyfin: the account's photo on
    /// every device. Nothing else of it leaves the iPhone (no place, no date: the JPEG is drawn anew).
    private func send(_ item: PhotosPickerItem) async {
        guard let client = app.client else { return }
        sendingPhoto = true; photoFailure = nil
        defer { sendingPhoto = false }
        do {
            guard let data = try await item.loadTransferable(type: Data.self), let jpeg = AccountPhoto.square(data) else {
                photoFailure = String(localized: "This picture won’t open. Choose another photo.")
                return
            }
            try await client.setPhoto(jpeg)
            me = try? await client.me()
        } catch {
            photoFailure = error.localizedDescription
        }
    }

    /// « 3 titles · 4.2 GB », or how to keep one.
    private var keptLine: String {
        let kept = app.keeper.kept
        guard !kept.isEmpty else { return String(localized: "Movies and episodes kept here play without a connection.") }
        return String(localized: "\(kept.count) titles · \(Keeper.size(kept.reduce(0) { $0 + $1.size }))")
    }

    private func row(_ title: LocalizedStringKey, _ detail: LocalizedStringKey, symbol: String? = nil) -> some View {
        SettingsRow(title, detail, symbol: symbol)
    }

    private func openSettings() {
        if let url = URL(string: UIApplication.openSettingsURLString) { openURL(url) }
    }

    private func qualityName(_ value: String) -> String {
        switch value {
        case "auto": String(localized: "Automatic")
        case "max": String(localized: "Maximum")
        default: String(localized: "\(((Double(value) ?? 0) / 1_000_000).formatted(.number.precision(.fractionLength(0...1)))) Mb/s")
        }
    }

    private func mobileDataName(_ value: String) -> String {
        switch value {
        case "saver": String(localized: "Data saver · 2 Mb/s")
        case "standard": String(localized: "Standard · 6 Mb/s")
        default: String(localized: "Maximum")
        }
    }

    /// « Japanese, then French » for « jpn,ja,fre,fra,fr,eng,en »: the first two languages of an order.
    private func orderName(_ order: String) -> String {
        var names: [String] = []
        for code in order.split(separator: ",").map(String.init) {
            guard let key = TrackChoice.key(code), !names.contains(key) else { continue }
            names.append(key)
        }
        let shown = names.prefix(2).compactMap { TrackNames.language($0) }
        return shown.count == 2 ? String(localized: "\(shown[0]), then \(shown[1])") : shown.first ?? order
    }
}

/// The account's picture, or its initials.
struct Avatar: View {
    let name: String
    let url: URL?

    var body: some View {
        ZStack {
            Circle().fill(Palette.raised2)
            Text(verbatim: initials).font(MiraFont.headline).foregroundStyle(Palette.ink)
            if let url { RemoteImage(url: url, width: 56).clipShape(.circle) }
        }
        .frame(width: 56, height: 56)
        .accessibilityHidden(true)
    }

    private var initials: String {
        name.split(separator: " ").prefix(2).compactMap { $0.first.map(String.init) }.joined().uppercased()
    }
}

/// « Authorize a device »: the six digits shown by a device that signs in with Quick Connect.
struct ApproveDeviceSheet: View {
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var code = ""
    @State private var working = false
    @State private var failure: String?
    @FocusState private var focused: Bool

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Code", text: $code)
                        .keyboardType(.numberPad)
                        .textContentType(.oneTimeCode)
                        .font(MiraFont.extraBold(28, .title))
                        .focused($focused)
                        .accessibilityIdentifier("approve.code")
                } footer: {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Enter the 6-digit code the device to connect shows. It will open your account: only authorize a device you can see.")
                        if let failure { Text(verbatim: failure).foregroundStyle(Palette.danger) }
                    }
                }
                .listRowBackground(Palette.surface)
            }
            .scrollContentBackground(.hidden)
            .background(Palette.bg)
            .navigationTitle("Authorize a device")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(working ? "Authorizing…" : "Authorize") { Task { await approve() } }
                        .disabled(working)
                }
            }
            .onAppear { focused = true }
        }
        .presentationDetents([.medium])
    }

    private func approve() async {
        let digits = code.filter(\.isNumber)
        guard digits.count == 6 else {
            failure = String(localized: "The code has 6 digits.")
            return
        }
        guard let client = app.client else { return }
        working = true
        defer { working = false }
        failure = nil
        // Turned off on the server, Jellyfin answers 401, which would end this session: asked first.
        guard await client.quickConnectEnabled() else {
            failure = String(localized: "Quick Connect is turned off on this server: turn it on in Jellyfin’s dashboard (General).")
            return
        }
        do {
            try await client.approveCode(digits)
            app.together.notice = String(localized: "Device authorized: it signs in to your account.")
            dismiss()
        } catch JellyfinError.http(404) {
            failure = String(localized: "Unknown or expired code. Check it, or ask the device for a new one.")
        } catch {
            failure = Messages.text(error)
        }
    }
}

/// Users/Me: what Settings shows of the account.
struct AccountInfo: Codable, Sendable {
    struct Policy: Codable, Sendable { var isAdministrator: Bool?; var enableCollectionManagement: Bool? }
    var primaryImageTag: String?
    var policy: Policy?
    var isAdministrator: Bool { policy?.isAdministrator == true }
}

extension JellyfinClient {
    func me() async throws -> AccountInfo { try await get("Users/Me") }

    func publicInfo() async throws -> PublicInfo { try await get("System/Info/Public") }

    /// Quick Connect on this server: off (or any answer but a plain yes) when Jellyfin's dashboard turned it off.
    func quickConnectEnabled() async -> Bool {
        guard let data = try? await send("GET", "QuickConnect/Enabled", timeout: 8) else { return false }
        return String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) == "true"
    }

    /// The account's photo: a JPEG, sent in base64 as Jellyfin takes it (the route of 10.9, still answered by 12.1;
    /// any account may change its own).
    func setPhoto(_ jpeg: Data) async throws {
        try await send("POST", "Users/\(userId)/Images/Primary", body: jpeg.base64EncodedData(), contentType: "image/jpeg", timeout: 30)
    }

    func approveCode(_ code: String) async throws {
        try await send("POST", "QuickConnect/Authorize", ["code": code, "userId": userId])
    }
}

/// A photo for the account: cut square around its middle, at most 512 px, drawn upright (UIImage follows the camera's
/// orientation) into a new JPEG.
enum AccountPhoto {
    static let side: CGFloat = 512

    static func square(_ data: Data) -> Data? {
        guard let image = UIImage(data: data), image.size.width > 0, image.size.height > 0 else { return nil }
        let shortest = min(image.size.width, image.size.height)
        let size = min(side, shortest * image.scale)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1; format.opaque = true
        let renderer = UIGraphicsImageRenderer(size: CGSize(width: size, height: size), format: format)
        let scale = size / shortest
        let drawn = CGSize(width: image.size.width * scale, height: image.size.height * scale)
        return renderer.jpegData(withCompressionQuality: 0.9) { _ in
            image.draw(in: CGRect(x: (size - drawn.width) / 2, y: (size - drawn.height) / 2, width: drawn.width, height: drawn.height))
        }
    }
}
