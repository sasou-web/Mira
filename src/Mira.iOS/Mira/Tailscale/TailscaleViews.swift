import SwiftUI

/// Settings, « Away from home »: Built-in Tailscale (on or off, signed in, the PC with Jellyfin found), and the way to
/// Jellyfin taken now.
struct TailscaleSection: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        let tailnet = app.tailnet
        Section {
            Toggle(isOn: Binding(get: { tailnet.enabled }, set: { on in Task { await tailnet.turn(on) } })) {
                SettingsRow("Built-in Tailscale", "Reach your Jellyfin away from home without the Tailscale app: Mira joins your Tailscale network itself, only while it’s open.",
                            symbol: "point.3.connected.trianglepath.dotted")
            }
            .accessibilityIdentifier("settings.tailscale")
            TailnetRows()
            WayRow()
        } header: {
            Text("Away from home")
        } footer: {
            TailscalePrivacy()
        }
        .listRowBackground(Palette.surface)
    }
}

/// Built-in Tailscale's state, in rows of a list: connecting, the sign-in, the approval waited for, the network and the
/// PC with Jellyfin, a failure; and signing out.
struct TailnetRows: View {
    @Environment(AppModel.self) private var app
    @State private var confirmSignOut = false

    var body: some View {
        let tailnet = app.tailnet
        Group {
            switch tailnet.phase {
            case .off:
                EmptyView()
            case .starting:
                HStack(spacing: 14) {
                    ProgressView().frame(width: 26)
                    Text("Connecting to Tailscale…").font(MiraFont.body).foregroundStyle(Palette.ink)
                }
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("tailscale.starting")
                if tailnet.slow {
                    Text("Tailscale’s servers aren’t answering. Mira keeps trying: nothing to change on this iPhone. If it lasts, Tailscale may be having an outage.")
                        .font(MiraFont.footnote).foregroundStyle(Palette.warning).fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("tailscale.slow")
                    Link(destination: URL(string: "https://status.tailscale.com")!) {
                        Label("Open Tailscale’s status page", systemImage: "arrow.up.forward.app").font(MiraFont.body)
                    }
                }
            case .signIn:
                Button {
                    Task { await tailnet.signIn() }
                } label: {
                    HStack {
                        if tailnet.status.state == "expired" {
                            SettingsRow("Sign in to Tailscale again", "This iPhone’s sign-in to Tailscale has expired. Your password stays on Tailscale’s page: Mira never sees it.", symbol: "person.badge.key")
                        } else {
                            SettingsRow("Sign in to Tailscale", "Once, with your Tailscale account. Your password stays on Tailscale’s page: Mira never sees it.", symbol: "person.badge.key")
                        }
                        Spacer(minLength: 8)
                        if tailnet.signingIn { ProgressView() }
                    }
                }
                .disabled(tailnet.signingIn)
                .accessibilityIdentifier("tailscale.signin")
            case .approval:
                SettingsRow("Waiting for approval in Tailscale’s admin console",
                            "Your Tailscale network has new devices approved by an administrator: approve “\(tailnet.status.device?.label ?? "mira-iphone")” there.", symbol: "hourglass")
                Link(destination: URL(string: "https://login.tailscale.com/admin/machines")!) {
                    Label("Open Tailscale’s admin console", systemImage: "arrow.up.forward.app").font(MiraFont.body)
                }
            case .running:
                if tailnet.status.offline == true {
                    SettingsRow("Tailscale can’t be reached", "This iPhone has no connection to Tailscale right now. Check its connection.", symbol: "wifi.exclamationmark")
                        .accessibilityIdentifier("tailscale.offline")
                } else {
                    SettingsRow("Connected to Tailscale", verbatim: connectedLine(tailnet.status), symbol: "checkmark.shield")
                        .accessibilityIdentifier("tailscale.connected")
                }
                if let at = WayPlan.expiry(tailnet.status.device) {
                    let name = tailnet.status.device?.label ?? "mira-iphone"
                    Text("This iPhone’s sign-in to Tailscale expires on \(at.formatted(date: .long, time: .omitted)): sign in again then, or turn off key expiry in Tailscale’s admin console (the ⋯ menu of “\(name)”, “Disable key expiry”).")
                        .font(MiraFont.footnote).foregroundStyle(Palette.warning).fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("tailscale.expires")
                }
                if let pc = jellyfinPC(tailnet.status) {
                    SettingsRow(pc.online ? "PC with Jellyfin online" : "PC with Jellyfin offline", verbatim: pcLine(pc),
                                symbol: pc.online ? "desktopcomputer" : "desktopcomputer.trianglebadge.exclamationmark")
                }
            case let .failed(message):
                SettingsRow("Built-in Tailscale couldn’t start", verbatim: message, symbol: "exclamationmark.triangle")
                Button("Try again") { Task { await tailnet.start() } }.font(MiraFont.body)
            }
            if let failure = tailnet.failure {
                Text(verbatim: failure).font(MiraFont.footnote).foregroundStyle(Palette.danger).fixedSize(horizontal: false, vertical: true)
            }
            if tailnet.phase == .running || tailnet.phase == .approval {
                Button(role: .destructive) {
                    confirmSignOut = true
                } label: {
                    Label("Sign out of Tailscale", systemImage: "rectangle.portrait.and.arrow.right").font(MiraFont.body)
                }
                .accessibilityIdentifier("tailscale.signout")
            }
        }
        .confirmationDialog("Sign this iPhone out of your Tailscale network?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out of Tailscale", role: .destructive) { Task { await tailnet.signOut() } }
        } message: {
            Text("Away from home, Mira reaches Jellyfin again once you sign in.")
        }
    }

    /// The PC by its name in Tailscale, and when it was last connected once it isn't.
    private func pcLine(_ pc: WayPlan.Peer) -> String {
        guard !pc.online, let seen = pc.seen else { return pc.label }
        return "\(pc.label) · " + String(localized: "last seen \(seen.formatted(.relative(presentation: .named)))")
    }

    private func connectedLine(_ status: TailnetStatus) -> String {
        let name = status.device?.label ?? "mira-iphone"
        guard let network = status.tailnet, !network.isEmpty else { return String(localized: "As “\(name)”.") }
        return String(localized: "As “\(name)” on \(network).")
    }

    /// The device of the network the way to Jellyfin goes to, or the one at the server's address.
    private func jellyfinPC(_ status: TailnetStatus) -> WayPlan.Peer? {
        WayPlan.device(of: [app.way?.address, app.session?.server].compactMap { $0 }, in: status.peers ?? [])
    }
}

/// The way to Jellyfin taken now: at home, the address as it is, Tailscale's app, Built-in Tailscale, or none.
struct WayRow: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        if app.session != nil {
            LabeledContent {
                Text(verbatim: text).font(MiraFont.body).foregroundStyle(Palette.muted).multilineTextAlignment(.trailing)
            } label: {
                Text("Path to Jellyfin").font(MiraFont.body).foregroundStyle(Palette.ink)
            }
            .accessibilityElement(children: .combine)
            .accessibilityIdentifier("settings.way")
        }
    }

    private var text: String {
        switch app.way?.kind {
        case .home: String(localized: "At home (direct)")
        case .direct: String(localized: "Direct")
        case .tailscaleApp: String(localized: "Through the Tailscale app")
        case .builtIn: String(localized: "Through Built-in Tailscale")
        case nil: String(localized: "Not reachable now")
        }
    }
}

/// What Tailscale sees, said wherever Built-in Tailscale is offered.
struct TailscalePrivacy: View {
    var body: some View {
        Text("Tailscale’s servers see this device join your Tailscale network: its name, its system and its IP addresses (public and local, for direct connections). Your videos stay end-to-end encrypted, directly or through a Tailscale relay. Mira sends no logs to Tailscale.")
    }
}

/// Built-in Tailscale in a sheet: from the sign-in screen, to find Jellyfin on the Tailscale network (`found`), or from
/// Home when Jellyfin can't be reached, to turn it on.
struct TailscaleSheet: View {
    var found: ((ServerInfo) -> Void)?
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var servers: [(info: ServerInfo, device: String)] = []
    @State private var looking = false
    @State private var looked = false

    var body: some View {
        let tailnet = app.tailnet
        NavigationStack {
            List {
                Section {
                    if tailnet.enabled {
                        TailnetRows()
                    } else {
                        Button {
                            Task {
                                await tailnet.turn(true)
                                await tailnet.signIn()
                            }
                        } label: {
                            SettingsRow("Turn on Built-in Tailscale", "Then sign in to Tailscale, once, with your Tailscale account.", symbol: "point.3.connected.trianglepath.dotted")
                        }
                        .accessibilityIdentifier("tailscale.turnOn")
                    }
                } header: {
                    Text("Away from home")
                } footer: {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Reach your Jellyfin away from home without the Tailscale app: Mira joins your Tailscale network itself, only while it’s open. At home, it goes direct.")
                        TailscalePrivacy()
                    }
                }
                .listRowBackground(Palette.surface)

                if found != nil, tailnet.phase == .running {
                    Section {
                        ForEach(Array(servers.enumerated()), id: \.offset) { index, server in
                            Button {
                                found?(server.info)
                                dismiss()
                            } label: {
                                HStack(spacing: 14) {
                                    Image(systemName: "server.rack").font(.body).foregroundStyle(Palette.ink).frame(width: 26).accessibilityHidden(true)
                                    VStack(alignment: .leading, spacing: 3) {
                                        Text(verbatim: server.info.name).font(MiraFont.body).foregroundStyle(Palette.ink)
                                        Text(verbatim: "\(server.device) · \(server.info.address.host(percentEncoded: false) ?? "")").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                                    }
                                    Spacer(minLength: 8)
                                    Image(systemName: "chevron.right").font(.footnote).foregroundStyle(Palette.muted).accessibilityHidden(true)
                                }
                            }
                            .accessibilityIdentifier("tailscale.server.\(index)")
                        }
                        if looking {
                            HStack(spacing: 14) {
                                ProgressView().frame(width: 26)
                                Text("Looking for Jellyfin on your Tailscale network…").font(MiraFont.body).foregroundStyle(Palette.muted)
                            }
                        } else if looked && servers.isEmpty {
                            Text("No device of your Tailscale network answers as Jellyfin on port 8096. Check that the PC with Jellyfin is on and connected to Tailscale, or type its Tailscale address.")
                                .font(MiraFont.footnote).foregroundStyle(Palette.muted)
                            Button("Look again") { Task { await look() } }.font(MiraFont.body)
                        }
                    } header: {
                        Text("Jellyfin on your Tailscale network")
                    }
                    .listRowBackground(Palette.surface)
                }
            }
            .font(MiraFont.body)
            .scrollContentBackground(.hidden)
            .background(Palette.bg)
            .navigationTitle("Built-in Tailscale")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Close") { dismiss() } }
            }
            .task(id: tailnet.phase) {
                if found != nil, tailnet.phase == .running, servers.isEmpty { await look() }
            }
        }
    }

    private func look() async {
        guard !looking else { return }
        looking = true
        servers = await Way.shared.servers()
        looking = false
        looked = true
    }
}

/// A line of Settings: its name, what it does under it, an icon before it.
struct SettingsRow: View {
    let title: Text
    let detail: Text
    var symbol: String?

    init(_ title: LocalizedStringKey, _ detail: LocalizedStringKey, symbol: String? = nil) {
        self.title = Text(title)
        self.detail = Text(detail)
        self.symbol = symbol
    }

    init(_ title: LocalizedStringKey, verbatim detail: String, symbol: String? = nil) {
        self.title = Text(title)
        self.detail = Text(verbatim: detail)
        self.symbol = symbol
    }

    var body: some View {
        HStack(spacing: 14) {
            if let symbol {
                Image(systemName: symbol).font(.body).foregroundStyle(Palette.ink).frame(width: 26).accessibilityHidden(true)
            }
            VStack(alignment: .leading, spacing: 3) {
                title.font(MiraFont.body).foregroundStyle(Palette.ink)
                detail.font(MiraFont.footnote).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.vertical, 2)
    }
}
