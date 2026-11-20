import CoreImage.CIFilterBuiltins
import SwiftUI
import UIKit

/// Settings → Family & profiles, for an administrator (FamilySection of Mira for Mac, Réglages → Famille & profils of Windows): the home's
/// password (turned on, changed, turned off), the home link for a device away from home (QR code, share), the profiles
/// offered and their PINs, the devices in the home, and Tailscale set up once for the links.
struct HomeSettingsView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.openURL) private var openURL
    @State private var settings: HomeSettings?
    /// Read, and the extension has no home (older than it).
    @State private var missing = false
    @State private var tailnet: HomeTailnetState?
    @State private var loading = true
    @State private var failure: String?
    @State private var said: String?
    @State private var working = false

    @State private var password = ""
    @State private var askChange = false
    @State private var askOff = false
    @State private var link: (link: String, expires: Date)?
    @State private var pinFor: HomeAccount?
    @State private var pin = ""
    @State private var pinOffers = false
    @State private var removing: HomeDeviceInfo?
    @State private var askDisconnect = false
    /// The guide's step on screen (nil: closed).
    @State private var guide: Int?
    @State private var showLines = false

    var body: some View {
        List {
            if loading && settings == nil && !missing {
                Section { HStack { Spacer(); ProgressView(); Spacer() } }.listRowBackground(Palette.surface)
            } else if missing {
                Section {
                    Text(verbatim: HomeText.noExtension).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
                .listRowBackground(Palette.surface)
            } else if let failure, settings == nil {
                Section {
                    Text(verbatim: failure).font(MiraFont.footnote).foregroundStyle(Palette.danger)
                    Button("Try again") { Task { await load() } }
                }
                .listRowBackground(Palette.surface)
            } else if let settings {
                if let said {
                    Section { Text(verbatim: said).font(MiraFont.footnote).foregroundStyle(Palette.muted).accessibilityIdentifier("home.said") }
                        .listRowBackground(Palette.surface)
                }
                passwordSection(settings)
                if settings.home {
                    linkSection
                    profilesSection(settings)
                    devicesSection(settings)
                    tailnetSection
                }
            }
        }
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .background(Palette.bg)
        .navigationTitle(Text("Family & profiles"))
        .disabled(working)
        .task { await load() }
        .refreshable { await load() }
        .confirmationDialog(String(localized: "Change the home password?"), isPresented: $askChange, titleVisibility: .visible) {
            Button("Change") { Task { await setPassword(password) } }
        } message: {
            Text("Every device in the home will need to type the new one.")
        }
        .confirmationDialog(String(localized: "Turn off the home?"), isPresented: $askOff, titleVisibility: .visible) {
            Button("Turn off", role: .destructive) { Task { await setPassword(nil) } }
                .accessibilityIdentifier("home.off.confirm")
        } message: {
            Text("Every device leaves the home and its profiles close. The Jellyfin accounts stay.")
        }
        .confirmationDialog(String(localized: "Remove this device from the home?"), isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } }),
                            titleVisibility: .visible, presenting: removing) { device in
            Button("Remove", role: .destructive) { Task { await remove(device) } }
        } message: { _ in
            Text("Its profile closes. It will need the password, and away from home a new link, to come back.")
        }
        .confirmationDialog(String(localized: "Disconnect Tailscale from the home?"), isPresented: $askDisconnect, titleVisibility: .visible) {
            Button("Disconnect", role: .destructive) { Task { await connect(nil, nil) } }
        } message: {
            Text("No more home links. Devices already away from home keep their access until you remove them.")
        }
        .sheet(isPresented: Binding(get: { guide != nil }, set: { if !$0 { guide = nil } })) {
            HomeGuideView(step: guide ?? 1) { Task { await load() } }
                .environment(app)
        }
        .alert(pinFor.map { HomeText.pinOf($0.name) } ?? "", isPresented: Binding(get: { pinFor != nil }, set: { if !$0 { pinFor = nil } })) {
            TextField("4 digits", text: $pin).keyboardType(.numberPad).accessibilityIdentifier("home.pin")
            Button("Save PIN") { if let account = pinFor { Task { await setPin(account, pin, offering: pinOffers) } } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("A PIN is 4 digits.")
        }
    }

    // MARK: The password

    private func passwordSection(_ settings: HomeSettings) -> some View {
        Section {
            SecureField(settings.home ? String(localized: "New password") : String(localized: "At least \(settings.minPassword ?? 8) characters"), text: $password)
                .textContentType(.newPassword)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .accessibilityIdentifier("home.password")
            if settings.home {
                Button("Change") { askChange = true }
                    .disabled(password.count < (settings.minPassword ?? 8))
                    .accessibilityIdentifier("home.change")
                Button("Turn off the home…", role: .destructive) { askOff = true }
                    .accessibilityIdentifier("home.off")
            } else {
                Button("Turn on the home") { Task { await setPassword(password) } }
                    .disabled(password.count < (settings.minPassword ?? 8))
                    .accessibilityIdentifier("home.on")
            }
        } header: {
            Text(verbatim: HomeText.password)
        } footer: {
            settings.home ? Text("Your family types it once per device, then picks their profile. Changing it signs every device out of the home.")
                : Text("One password for the whole family: on a device, it opens “Who’s watching?” with the profiles you pick below. No administrator is ever one of them.")
        }
        .listRowBackground(Palette.surface)
    }

    // MARK: The home link

    private var linkSection: some View {
        Section {
            if tailnet?.connected == true {
                if let link {
                    if let qr = Self.qr(link.link) {
                        Image(uiImage: qr)
                            .interpolation(.none)
                            .resizable()
                            .scaledToFit()
                            .frame(maxWidth: 220)
                            .padding(12)
                            .background(.white, in: .rect(cornerRadius: 14))
                            .frame(maxWidth: .infinity)
                            .accessibilityLabel(Text("Home link QR code"))
                    }
                    ShareLink(item: link.link) {
                        Label("Send the home link", systemImage: "square.and.arrow.up")
                    }
                    .accessibilityIdentifier("home.share")
                    Text("Ends \(link.expires.formatted(date: .abbreviated, time: .shortened)). Send it with the password, by two different ways if you can.")
                        .font(MiraFont.footnote).foregroundStyle(Palette.muted)
                } else {
                    Button { Task { await makeLink() } } label: {
                        Label("Make a home link", systemImage: "link")
                    }
                    .accessibilityIdentifier("home.link")
                }
            } else {
                Text("First finish “Away from home” below: it’s once only.").font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
        } header: {
            Text("Home link")
        } footer: {
            Text("For a device away from home: the link takes Mira to your Jellyfin through Tailscale, with no address or Tailscale account to create. It works once and ends after 48 hours; the password is still asked.")
        }
        .listRowBackground(Palette.surface)
    }

    // MARK: The profiles

    private func profilesSection(_ settings: HomeSettings) -> some View {
        Section {
            ForEach(settings.profiles) { account in
                VStack(alignment: .leading, spacing: 8) {
                    // An administrator's profile only with a PIN, which it keeps: turned on, the PIN is asked first.
                    Toggle(isOn: Binding(get: { account.offered }, set: { on in
                        if on && account.admin && !account.pin {
                            pin = ""
                            pinOffers = true
                            pinFor = account
                        } else {
                            Task { await offer(account, on) }
                        }
                    })) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(verbatim: account.name).font(MiraFont.body).foregroundStyle(Palette.ink)
                            if account.admin {
                                Text("Administrator: in the home only with a PIN, so the home password alone never opens your server.")
                                    .font(MiraFont.footnote).foregroundStyle(Palette.muted)
                            } else {
                                Text(verbatim: reach(account))
                                    .font(MiraFont.footnote).foregroundStyle(Palette.muted)
                            }
                        }
                    }
                    .disabled(account.disabled)
                    .accessibilityIdentifier("home.offer.\(account.name)")
                    // « Can download »: TorLink without administering Jellyfin (an administrator always may: no switch).
                    if !account.admin, account.download != nil {
                        Toggle("Can download", isOn: Binding(get: { account.download == true }, set: { on in Task { await change { try await $0.homeSetDownload(account.id, on) } } }))
                            .font(MiraFont.footnote)
                            .accessibilityIdentifier("home.download.\(account.name)")
                    }
                    if account.offered {
                        HStack(spacing: 16) {
                            Button(account.pin ? String(localized: "Change PIN") : String(localized: "Add a PIN")) {
                                pin = ""
                                pinOffers = false
                                pinFor = account
                            }
                            .accessibilityIdentifier("home.pinButton.\(account.name)")
                            if account.pin && !account.admin {
                                Button("Remove PIN", role: .destructive) { Task { await setPin(account, nil) } }
                            }
                        }
                        .font(MiraFont.footnote)
                        .buttonStyle(.borderless)
                    }
                }
            }
        } header: {
            Text("Profiles")
        } footer: {
            Text("The Jellyfin accounts the home offers. A 4-digit PIN keeps a profile from the others; an administrator always has one. Can download opens Downloads (TorLink) without being an administrator.")
        }
        .listRowBackground(Palette.surface)
    }

    private func reach(_ account: HomeAccount) -> String {
        let libraries = account.allLibraries ? String(localized: "The whole library") : String(localized: "Part of the library")
        guard let rating = account.maxRating, !rating.isEmpty else { return libraries }
        return libraries + " · " + String(localized: "up to \(rating)")
    }

    // MARK: The devices

    private func devicesSection(_ settings: HomeSettings) -> some View {
        Section {
            if settings.devices.isEmpty {
                Text("No devices yet.").font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
            ForEach(settings.devices) { device in
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(verbatim: device.name).font(MiraFont.body).foregroundStyle(Palette.ink)
                        Text(verbatim: device.profileName.map { String(localized: "\($0)’s profile") } ?? String(localized: "No profile open"))
                            .font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    }
                    Spacer(minLength: 8)
                    Button("Remove…", role: .destructive) { removing = device }
                        .buttonStyle(.borderless)
                        .font(MiraFont.footnote)
                }
            }
        } header: {
            Text("Devices in the home")
        } footer: {
            Text("Each device that typed the password. Removing one closes its profile; it will need the password to come back.")
        }
        .listRowBackground(Palette.surface)
    }

    // MARK: Tailscale, once

    @ViewBuilder private var tailnetSection: some View {
        Section {
            if let tailnet {
                let trouble = tailnet.connected
                    ? HomeGuide.trouble(tailnet.policy == "ok" ? nil : tailnet.policy, missing: tailnet.missing ?? [], broad: tailnet.broad == true, changes: tailnet.fix?.count ?? 0)
                    : nil
                if !tailnet.connected {
                    HStack {
                        Text(verbatim: HomeGuide.entryTitle).font(MiraFont.headline).foregroundStyle(Palette.ink)
                        Spacer()
                        Text(verbatim: HomeGuide.notSetUp).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    }
                    Text(verbatim: HomeGuide.entryText).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    Button(HomeGuide.setUp) { guide = 1 }.accessibilityIdentifier("home.guide.open")
                } else if let trouble {
                    Text(verbatim: trouble.title).font(MiraFont.headline).foregroundStyle(trouble.warn ? Palette.warning : Palette.danger)
                    Text(verbatim: trouble.text).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    ForEach(trouble.remedies, id: \.self) { remedy in
                        Button(remedy == .fix ? HomeGuide.makeChanges(tailnet.fix?.count ?? 0) : HomeGuide.label(remedy)) { Task { await act(remedy) } }
                    }
                    if showLines {
                        Text(verbatim: (tailnet.lines ?? []).joined(separator: "\n")).font(.system(size: 12, design: .monospaced)).foregroundStyle(Palette.ink).textSelection(.enabled)
                    }
                    Button("Disconnect…", role: .destructive) { askDisconnect = true }
                } else {
                    Text(verbatim: HomeGuide.readyTitle).font(MiraFont.headline).foregroundStyle(Palette.ink)
                    Text(verbatim: HomeGuide.readyText).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    if (tailnet.devices ?? []).isEmpty { Text(verbatim: HomeGuide.firstLinkText).font(MiraFont.footnote).foregroundStyle(Palette.muted) }
                    if let until = tailnet.undoUntil, until > .now {
                        Text(verbatim: HomeGuide.changed(until)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                        Button(String(localized: "Undo")) { Task { await undo() } }
                    }
                    Button("Disconnect…", role: .destructive) { askDisconnect = true }
                }
            } else {
                Text("The server says nothing about Tailscale: update the Mira extension.").font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
        } header: {
            Text("Away from home")
        }
        .listRowBackground(Palette.surface)
    }

    private func act(_ remedy: HomeGuide.Remedy) async {
        switch remedy {
        case .redoKey, .setUpAgain: guide = 2
        case .openCredentials: openURL(HomeGuide.credentialsPage)
        case .openPolicy: openURL(HomeGuide.policyPage)
        case .seeLines: showLines.toggle()
        case .checkAgain: await load()
        case .fix: await fixPolicy()
        }
    }

    private func undo() async {
        await change(HomeGuide.undone) { try await $0.homeUndoPolicy() }
    }

    // MARK: Asking the extension

    private func load() async {
        guard let client = app.client else { return }
        loading = true
        defer { loading = false }
        do {
            guard let read = try await client.homeSettings() else {
                missing = true
                return
            }
            settings = read
            failure = nil
            tailnet = read.home ? try? await client.homeTailnet() : nil
        } catch {
            failure = words(error)
        }
    }

    /// Runs a change, then reads the home again; what went wrong is said at the top.
    private func change(_ done: String? = nil, _ action: (JellyfinClient) async throws -> Void) async {
        guard let client = app.client else { return }
        working = true
        defer { working = false }
        do {
            try await action(client)
            said = done
        } catch {
            said = words(error)
        }
        await load()
    }

    private func setPassword(_ value: String?) async {
        await change(value == nil ? String(localized: "The home is off.") : String(localized: "The home password is set.")) { try await $0.homeSetPassword(value) }
        password = ""
        link = nil
    }

    private func offer(_ account: HomeAccount, _ on: Bool) async { await change { try await $0.homeOffer(account.id, on) } }

    private func setPin(_ account: HomeAccount, _ value: String?, offering: Bool = false) async {
        if let value, value.count != 4 || !value.allSatisfy({ $0.isASCII && $0.isNumber }) {
            said = String(localized: "A PIN is 4 digits.")
            return
        }
        if offering, let value {
            await change { try await $0.homeOffer(account.id, true, pin: value) }
        } else {
            await change { try await $0.homeSetPin(account.id, value) }
        }
    }

    private func remove(_ device: HomeDeviceInfo) async { await change { try await $0.homeRemoveDevice(device.id) } }

    /// Tailscale disconnected from the home (the guide connects it).
    private func connect(_ id: String?, _ secret: String?) async {
        await change { try await $0.homeConnectTailnet(id: id, secret: secret) }
    }

    private func fixPolicy() async {
        guard let client = app.client else { return }
        working = true
        defer { working = false }
        do {
            try await client.homeFixPolicy()
            said = nil
            await load()
        } catch let refused as HomeAdminRefused {
            said = HomeGuide.trouble(refused.policy, missing: refused.missing).map { $0.title + ". " + $0.text } ?? refused.message
        } catch {
            said = words(error)
        }
    }

    private func makeLink() async {
        guard let client = app.client else { return }
        working = true
        defer { working = false }
        do {
            link = try await client.homeLink()
            said = nil
        } catch let refused as HomeAdminRefused {
            said = refused.policy.map(HomeText.policy) ?? refused.message
        } catch {
            said = words(error)
        }
    }

    private func words(_ error: Error) -> String {
        if let refused = error as? HomeAdminRefused { return refused.message }
        return Messages.text(error)
    }

    /// A QR code of the link, sharp at any size (drawn without smoothing).
    static func qr(_ text: String) -> UIImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let image = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 8, y: 8)),
              let cg = CIContext().createCGImage(image, from: image.extent) else { return nil }
        return UIImage(cgImage: cg)
    }
}
