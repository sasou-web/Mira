import SwiftUI

/// Signing in, in two steps as on Windows and Mac: the server first (found from what was typed, checked as Jellyfin
/// before any password leaves), then the account.
struct LoginView: View {
    @Environment(AppModel.self) private var app
    @State private var address = ""
    @State private var server: ServerInfo?
    @State private var username = ""
    @State private var password = ""
    @State private var busy = false
    @State private var message: String?
    /// The warning about a password sent in clear over the Internet was shown: a second tap sends it.
    @State private var plainWarned = false
    /// The server lets a device sign in with a code (Quick Connect), and the code is shown.
    @State private var quickConnect = false
    @State private var withCode = false
    @FocusState private var focus: Field?

    enum Field: Hashable { case address, username, password }

    var body: some View {
        ScrollView {
            VStack(spacing: 32) {
                header
                if let server {
                    accountForm(server).transition(.move(edge: .trailing).combined(with: .opacity))
                } else {
                    serverForm.transition(.move(edge: .leading).combined(with: .opacity))
                }
            }
            .frame(maxWidth: 440)
            .padding(.horizontal, 20)
            .padding(.top, 56)
            .padding(.bottom, 32)
            .frame(maxWidth: .infinity)
            .animation(.snappy, value: server)
        }
        .scrollDismissesKeyboard(.interactively)
        .background {
            ZStack {
                Palette.bg
                RadialGradient(colors: [Palette.raised2.opacity(0.7), .clear], center: .top, startRadius: 0, endRadius: 460)
            }
            .ignoresSafeArea()
        }
        .onAppear {
            if address.isEmpty { address = app.lastServer }
            message = app.notice
            focus = address.isEmpty ? .address : nil
        }
    }

    private var header: some View {
        VStack(spacing: 14) {
            MiraIcon(size: 76)
            Text(verbatim: "Mira")
                .font(MiraFont.extraBold(40, .largeTitle))
                .foregroundStyle(Palette.ink)
            Text(server.map { String(localized: "Sign in to \($0.name)") } ?? String(localized: "Your Jellyfin, like at the cinema."))
                .font(MiraFont.callout)
                .foregroundStyle(Palette.muted)
                .multilineTextAlignment(.center)
        }
        .accessibilityElement(children: .combine)
    }

    // MARK: Server

    private var serverForm: some View {
        VStack(alignment: .leading, spacing: 14) {
            FieldLabel("Server address")
            TextField("Server address", text: $address, prompt: Text(verbatim: "192.168.1.20"))
                .textContentType(.URL)
                .keyboardType(.URL)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.continue)
                .focused($focus, equals: .address)
                .onSubmit { Task { await findServer() } }
                .modifier(FieldStyle(focused: focus == .address))
                .accessibilityIdentifier("login.address")
            Text("At home, the address of the PC with Jellyfin. Away from home, its Tailscale address, with Tailscale on.")
                .font(MiraFont.footnote)
                .foregroundStyle(Palette.muted)
                .fixedSize(horizontal: false, vertical: true)
            messageView
            Button {
                Task { await findServer() }
            } label: {
                ButtonLabel(busy: busy, title: "Continue")
            }
            .buttonStyle(.glassProminent)
            .disabled(busy || address.trimmingCharacters(in: .whitespaces).isEmpty)
            .accessibilityIdentifier("login.continue")
            .padding(.top, 6)
        }
    }

    private func findServer() async {
        guard !busy else { return }
        busy = true
        message = nil
        defer { busy = false }
        do {
            let found = try await JellyfinClient.discover(address)
            plainWarned = false
            withCode = false
            server = found
            focus = username.isEmpty ? .username : .password
            quickConnect = await JellyfinClient(server: found.address, device: app.device).quickConnectEnabled()
        } catch is CancellationError {
        } catch {
            message = Messages.text(error)
        }
    }

    // MARK: Account

    private func accountForm(_ server: ServerInfo) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                Image(systemName: "server.rack")
                    .font(.title3)
                    .foregroundStyle(Palette.muted)
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(server.name).font(MiraFont.headline).foregroundStyle(Palette.ink)
                    Text(verbatim: server.address.absoluteString).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1).truncationMode(.middle)
                }
                Spacer(minLength: 8)
                Button("Change") {
                    withCode = false
                    quickConnect = false
                    self.server = nil
                    password = ""
                    message = nil
                    focus = .address
                }
                .buttonStyle(.glass)
                .accessibilityIdentifier("login.change")
            }
            .padding(14)
            .background(Palette.surface, in: .rect(cornerRadius: 16, style: .continuous))
            .overlay { RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Palette.line) }
            .padding(.bottom, 6)

            FieldLabel("Username")
            TextField("Username", text: $username)
                .textContentType(.username)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.next)
                .focused($focus, equals: .username)
                .onSubmit { focus = .password }
                .modifier(FieldStyle(focused: focus == .username))
                .accessibilityIdentifier("login.username")
            FieldLabel("Password")
            SecureField("Password", text: $password)
                .textContentType(.password)
                .submitLabel(.go)
                .focused($focus, equals: .password)
                .onSubmit { Task { await signIn(server) } }
                .modifier(FieldStyle(focused: focus == .password))
                .accessibilityIdentifier("login.password")
            if plainWarned {
                Label {
                    Text("This address goes over the Internet with http://: nothing is encrypted, and anyone along the way could read your password. Use its https:// address or Tailscale instead, or tap “Sign in anyway”.")
                } icon: {
                    Image(systemName: "exclamationmark.triangle.fill")
                }
                .font(MiraFont.footnote)
                .foregroundStyle(Palette.warning)
                .fixedSize(horizontal: false, vertical: true)
            }
            messageView
            Button {
                Task { await signIn(server) }
            } label: {
                ButtonLabel(busy: busy, title: plainWarned ? "Sign in anyway" : "Sign in")
            }
            .buttonStyle(.glassProminent)
            .disabled(busy || username.trimmingCharacters(in: .whitespaces).isEmpty)
            .accessibilityIdentifier("login.signin")
            .padding(.top, 6)
            if withCode {
                CodeSignIn(server: server, typed: address) { withCode = false }
                    .padding(.top, 6)
                    .transition(.opacity)
            } else if quickConnect {
                Button {
                    focus = nil
                    withCode = true
                } label: {
                    Label("Sign in with a code", systemImage: "number.square")
                        .font(MiraFont.callout)
                        .frame(maxWidth: .infinity, minHeight: 32)
                }
                .buttonStyle(.glass)
                .accessibilityIdentifier("login.withCode")
            }
        }
        .animation(.snappy, value: withCode)
    }

    private func signIn(_ server: ServerInfo) async {
        guard !busy, !username.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        if ServerAddress.exposesPassword(server.address), !plainWarned {
            plainWarned = true
            return
        }
        busy = true
        message = nil
        defer { busy = false }
        let client = JellyfinClient(server: server.address, device: app.device)
        do {
            let result = try await client.signIn(username: username.trimmingCharacters(in: .whitespaces), password: password)
            app.signedIn(Session(server: server.address, serverId: result.serverId ?? server.id, serverName: server.name,
                                 userId: result.user.id, userName: result.user.name ?? username, token: result.accessToken), typed: address)
        } catch is CancellationError {
        } catch {
            message = Messages.text(error)
            if (error as? JellyfinError) == .unauthorized {
                password = ""
                focus = .password
            }
        }
    }

    @ViewBuilder private var messageView: some View {
        if let message {
            Label {
                Text(message)
            } icon: {
                Image(systemName: "exclamationmark.circle.fill")
            }
            .font(MiraFont.footnote)
            .foregroundStyle(Palette.danger)
            .fixedSize(horizontal: false, vertical: true)
            .accessibilityIdentifier("login.message")
        }
    }
}

/// A field's name above it.
struct FieldLabel: View {
    let title: LocalizedStringKey
    init(_ title: LocalizedStringKey) { self.title = title }
    var body: some View {
        Text(title).font(MiraFont.subheadline).foregroundStyle(Palette.ink).accessibilityHidden(true)
    }
}

/// Mira's text fields: raised, rounded, outlined brighter while typing.
struct FieldStyle: ViewModifier {
    var focused: Bool
    func body(content: Content) -> some View {
        content
            .font(MiraFont.body)
            .foregroundStyle(Palette.ink)
            .padding(.horizontal, 14)
            .frame(minHeight: 50)
            .background(Palette.raised, in: .rect(cornerRadius: 12, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(focused ? Palette.muted : Palette.line, lineWidth: focused ? 1.5 : 1)
            }
    }
}

/// A wide button's label, or a spinner while its action runs.
struct ButtonLabel: View {
    var busy: Bool
    var title: LocalizedStringKey
    var body: some View {
        ZStack {
            Text(title).font(MiraFont.headline).foregroundStyle(Palette.onAccent).opacity(busy ? 0 : 1)
            if busy { ProgressView().tint(Palette.onAccent) }
        }
        .frame(maxWidth: .infinity, minHeight: 32)
    }
}
