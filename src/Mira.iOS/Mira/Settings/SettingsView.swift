import SwiftUI
import UIKit

/// Settings: the server and the account, Mira web, the language, and what Mira sends where.
struct SettingsView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.openURL) private var openURL
    @State private var confirmSignOut = false

    var body: some View {
        List {
            if let session = app.session {
                Section("Jellyfin") {
                    LabeledContent("Server", value: session.serverName)
                    LabeledContent("Address") {
                        Text(verbatim: session.server.absoluteString).textSelection(.enabled).lineLimit(1).truncationMode(.middle)
                    }
                    LabeledContent("Account", value: session.userName)
                    Button("Sign out", role: .destructive) { confirmSignOut = true }
                        .accessibilityIdentifier("settings.signout")
                }
                .listRowBackground(Palette.surface)

                Section {
                    if let url = ServerAddress.miraWeb(session.server) {
                        Button {
                            openURL(url)
                        } label: {
                            Label("Open Mira web", systemImage: "safari")
                        }
                    }
                } header: {
                    Text(verbatim: "Mira web")
                } footer: {
                    Text("Served by Jellyfin’s Mira extension. Until Mira for iPhone has its own player, it plays your titles.")
                }
                .listRowBackground(Palette.surface)
            }

            Section {
                Button {
                    if let url = URL(string: UIApplication.openSettingsURLString) { openURL(url) }
                } label: {
                    Label("Language", systemImage: "globe")
                }
            } footer: {
                Text("Mira follows your iPhone’s language. To choose another one for Mira only: Settings, Apps, Mira, Language.")
            }
            .listRowBackground(Palette.surface)

            Section {
                LabeledContent("Version", value: app.device.version)
                Link(destination: URL(string: "https://github.com/sasou-web/Mira")!) {
                    Label("Source code", systemImage: "chevron.left.forwardslash.chevron.right")
                }
            } header: {
                Text("About")
            } footer: {
                Text("Mira talks only to your Jellyfin server: no account of ours, no analytics, no ads.")
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
    }
}
