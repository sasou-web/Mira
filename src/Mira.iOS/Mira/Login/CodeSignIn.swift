import SwiftUI

/// Jellyfin's Quick Connect, from the side that signs in: a code shown here, authorized on a device already signed in.
struct QuickConnectState: Decodable, Sendable {
    var secret: String?
    var code: String?
    var authenticated: Bool?
}

extension JellyfinClient {
    func quickConnectStart() async throws -> QuickConnectState {
        let data = try await send("POST", "QuickConnect/Initiate")
        do { return try JSON.decoder.decode(QuickConnectState.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }

    /// Whether the code was authorized; 404 when Jellyfin forgot it (expired).
    func quickConnectState(_ secret: String) async throws -> QuickConnectState {
        let data = try await send("GET", "QuickConnect/Connect", ["secret": secret], timeout: 8)
        do { return try JSON.decoder.decode(QuickConnectState.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }

    func signIn(quickConnect secret: String) async throws -> Authentication {
        let body = try JSONSerialization.data(withJSONObject: ["Secret": secret])
        let data = try await send("POST", "Users/AuthenticateWithQuickConnect", body: body)
        do { return try JSON.decoder.decode(Authentication.self, from: data) } catch { throw JellyfinError.notJellyfin }
    }
}

/// « Sign in with a code »: the code, big, until a device signed in to the account authorizes it (Mira: Settings,
/// Authorize a device; Jellyfin: the user menu, Quick Connect). No password typed on this iPhone.
struct CodeSignIn: View {
    let server: ServerInfo
    let typed: String
    var onCancel: () -> Void
    @Environment(AppModel.self) private var app
    @State private var code: String?
    @State private var message: String?
    @State private var attempt = 0

    var body: some View {
        VStack(spacing: 16) {
            Text("Sign in with a code").font(MiraFont.headline).foregroundStyle(Palette.ink)
            Group {
                if let code {
                    Text(verbatim: code.map(String.init).joined(separator: " "))
                        .font(.system(size: 40, weight: .bold, design: .monospaced))
                        .foregroundStyle(Palette.ink)
                        .textSelection(.enabled)
                        .accessibilityLabel(Text(verbatim: code.map(String.init).joined(separator: ", ")))
                        .accessibilityIdentifier("login.code")
                } else if message == nil {
                    ProgressView()
                }
            }
            .frame(minHeight: 52)
            Text("On a device already signed in to your account: in Mira, Settings, Authorize a device; in Jellyfin, your profile, Quick Connect. Enter this code there.")
                .font(MiraFont.footnote)
                .foregroundStyle(Palette.muted)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            if let message {
                Text(verbatim: message).font(MiraFont.footnote).foregroundStyle(Palette.danger).multilineTextAlignment(.center)
                Button("New code") { attempt += 1 }
                    .buttonStyle(.glass)
            }
            Button("Cancel", action: onCancel)
                .buttonStyle(.glass)
                .accessibilityIdentifier("login.code.cancel")
        }
        .padding(18)
        .frame(maxWidth: .infinity)
        .background(Palette.surface, in: .rect(cornerRadius: 16, style: .continuous))
        .overlay { RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Palette.line) }
        .task(id: attempt) { await run() }
    }

    /// A code, then a look every 3 s until it is authorized, forgotten by Jellyfin, or the card closed.
    private func run() async {
        code = nil
        message = nil
        let client = JellyfinClient(server: server.address, device: app.device)
        do {
            let started = try await client.quickConnectStart()
            guard let secret = started.secret, let shown = started.code else { throw JellyfinError.notJellyfin }
            code = shown
            while !Task.isCancelled {
                try await Task.sleep(for: .seconds(3))
                if try await client.quickConnectState(secret).authenticated == true { break }
            }
            let result = try await client.signIn(quickConnect: secret)
            app.signedIn(Session(server: server.address, serverId: result.serverId ?? server.id, serverName: server.name,
                                 userId: result.user.id, userName: result.user.name ?? "", token: result.accessToken), typed: typed)
        } catch is CancellationError {
        } catch JellyfinError.http(404) {
            code = nil
            message = String(localized: "This code expired. Ask for a new one.")
        } catch {
            code = nil
            message = Messages.text(error)
        }
    }
}
