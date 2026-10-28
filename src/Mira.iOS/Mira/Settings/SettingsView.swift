import SwiftUI
import UIKit

/// Settings: the server and the account, Mira web, the language, and what Mira sends where.
struct SettingsView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.openURL) private var openURL
    @State private var confirmSignOut = false
    @AppStorage("mira.quality") private var quality = "auto"

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
                    Picker("Quality", selection: $quality) {
                        Text("Automatic").tag("auto")
                        Text("Maximum").tag("max")
                        // Mira web's choices (QUALITIES of settings.js).
                        Text(verbatim: "20 Mb/s").tag("20000000")
                        Text(verbatim: "10 Mb/s").tag("10000000")
                        Text(verbatim: "6 Mb/s").tag("6000000")
                        Text(verbatim: "3 Mb/s").tag("3000000")
                        Text(verbatim: "1,5 Mb/s".replacingOccurrences(of: ",", with: Locale.current.decimalSeparator ?? ".")).tag("1500000")
                    }
                    .accessibilityIdentifier("settings.quality")
                } header: {
                    Text("Playback")
                } footer: {
                    Text("Automatic: the file as it is at home; away from home, what your connection to Jellyfin allows. Below the file’s quality, Jellyfin converts it.")
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
                    Text("Served by Jellyfin’s Mira extension: for your friends, and what Mira for iPhone doesn’t do yet.")
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
