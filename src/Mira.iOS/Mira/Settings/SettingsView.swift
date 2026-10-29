import SafariServices
import SwiftUI
import UIKit

/// Settings, as Mira web's: the account, what only an administrator does (on Mira web for now), playback, this
/// iPhone, the account's devices, and what Mira is.
struct SettingsView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.openURL) private var openURL
    @State private var confirmSignOut = false
    @State private var me: AccountInfo?
    @State private var jellyfinVersion: String?
    @State private var quickConnect = false
    @State private var approving = false
    @State private var web: WebPage?

    var body: some View {
        @Bindable var preferences = app.preferences
        List {
            if let session = app.session {
                Section {
                    HStack(spacing: 14) {
                        Avatar(name: session.userName, url: me?.primaryImageTag.map { app.client?.url("Users/\(session.userId)/Images/Primary", ["maxWidth": "180", "tag": $0]) } ?? nil)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(verbatim: session.userName).font(MiraFont.headline).foregroundStyle(Palette.ink)
                            Text("Server “\(session.serverName)”").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                            Text(verbatim: session.server.absoluteString).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                                .lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                        }
                    }
                    .padding(.vertical, 4)
                    .accessibilityElement(children: .combine)
                }
                .listRowBackground(Palette.surface)

                if me?.isAdministrator == true {
                    Section {
                        admin("Now on Jellyfin", "Who watches what, and whether Jellyfin converts it.", symbol: "server.rack", route: "en-ce-moment")
                        admin("Titles to fix", "Those Jellyfin didn’t recognize, without a poster or twice.", symbol: "exclamationmark.triangle", route: "a-corriger")
                        admin("Automatic subtitles", "Jellyfin looks for each new title’s subtitles and syncs them to the video.", symbol: "captions.bubble", route: "sous-titres-auto")
                        admin("Guest links", "Watch together with someone who has no account and no Tailscale.", symbol: "link", route: "invites")
                        admin("Download on the server PC", "Search for a title: TorLink downloads it there, Mira files it in your library.", symbol: "arrow.down.circle", route: "telecharger")
                    } header: {
                        Text("Administration")
                    } footer: {
                        Text("These open in Mira web, inside Mira. Sign in there once.")
                    }
                    .listRowBackground(Palette.surface)
                }

                Section {
                    Picker("Quality", selection: $preferences.quality) {
                        ForEach(Preferences.qualities, id: \.self) { value in Text(verbatim: qualityName(value)).tag(value) }
                    }
                    .accessibilityIdentifier("settings.quality")
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
                        row("Next episode automatically", "At the end of an episode, the next one starts.")
                    }
                    .accessibilityIdentifier("settings.autoNext")
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
                    Text("Languages choose the tracks at the start of each title. On a title’s page, Audio and Subtitles choose others: Mira keeps them for the next episodes of the series. These choices are kept on your Jellyfin, with the tracks of each series and the titles taken out of Continue watching: you find them on Mira web, Windows and Mac.")
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
                    if let url = ServerAddress.miraWeb(session.server) {
                        Button {
                            web = WebPage(url: url)
                        } label: {
                            row("Open Mira web", "For your friends, and for what Mira for iPhone doesn’t do yet.", symbol: "safari")
                        }
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
            } header: {
                Text("About")
            } footer: {
                Text("Mira talks only to your Jellyfin server: no account of ours, no analytics, no ads. Your progress is kept on your Jellyfin: it follows you to Mira web, Windows and Mac.")
            }
            .listRowBackground(Palette.surface)
        }
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .background(Palette.bg)
        .navigationTitle("Settings")
        .confirmationDialog("Sign out of Jellyfin on this iPhone?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { app.signOut() }
                .accessibilityIdentifier("settings.signout.confirm")
        }
        .sheet(isPresented: $approving) { ApproveDeviceSheet() }
        .sheet(item: $web) { page in SafariPage(url: page.url).ignoresSafeArea() }
        .task { await load() }
        .refreshable { await load() }
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

    /// A line of Settings: its name, what it does under it, an icon before it.
    private func row(_ title: LocalizedStringKey, _ detail: LocalizedStringKey, symbol: String? = nil) -> some View {
        HStack(spacing: 14) {
            if let symbol {
                Image(systemName: symbol).font(.body).foregroundStyle(Palette.ink).frame(width: 26).accessibilityHidden(true)
            }
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(MiraFont.body).foregroundStyle(Palette.ink)
                Text(detail).font(MiraFont.footnote).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.vertical, 2)
    }

    private func admin(_ title: LocalizedStringKey, _ detail: LocalizedStringKey, symbol: String, route: String) -> some View {
        Button {
            if let session = app.session, let url = ServerAddress.miraWeb(session.server, route: route) { web = WebPage(url: url) }
        } label: {
            HStack {
                row(title, detail, symbol: symbol)
                Spacer(minLength: 8)
                Image(systemName: "arrow.up.forward.app").font(.footnote).foregroundStyle(Palette.muted).accessibilityHidden(true)
            }
        }
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

/// A page of Mira web shown inside Mira.
struct WebPage: Identifiable {
    let url: URL
    var id: URL { url }
}

struct SafariPage: UIViewControllerRepresentable {
    let url: URL
    func makeUIViewController(context: Context) -> SFSafariViewController {
        let controller = SFSafariViewController(url: url)
        controller.preferredControlTintColor = UIColor(Palette.accent)
        controller.dismissButtonStyle = .close
        return controller
    }
    func updateUIViewController(_ controller: SFSafariViewController, context: Context) {}
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
    struct Policy: Codable, Sendable { var isAdministrator: Bool? }
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

    func approveCode(_ code: String) async throws {
        try await send("POST", "QuickConnect/Authorize", ["code": code, "userId": userId])
    }
}
