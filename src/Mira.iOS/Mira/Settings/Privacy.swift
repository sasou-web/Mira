import SwiftUI

/// Settings → Privacy, as Windows and Mac draw it (Privacy of Core): every connection that leaves this iPhone, then
/// those Jellyfin opens for Mira's features, each with where it goes, when, what it sends and how it stops. Built-in
/// Tailscale has its switch here, the same as in Settings. No analytics, no crash report: nothing else goes anywhere.
struct PrivacyView: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        let tailnet = app.tailnet
        List {
            Section {
                Text("Mira has no account, no ads, no analytics and no crash report sent on its own. Here is everything that goes out on the network, and how to stop it.")
                    .font(MiraFont.footnote).foregroundStyle(Palette.muted)
            }
            .listRowBackground(Palette.surface)

            Section {
                PrivacyRow(title: "Your Jellyfin server",
                           to: "The address you signed in to, at home or through Tailscale.",
                           when: "While you’re signed in: library, pictures, playback, Watch together.",
                           sends: "Your session token in a header, and in the address of the video the player reads (Apple’s player can’t send a header). What you watch and where you stopped.",
                           stop: "Sign out, in Settings.")
                Toggle(isOn: Binding(get: { tailnet.enabled }, set: { on in Task { await tailnet.turn(on) } })) {
                    PrivacyRow(title: "Built-in Tailscale",
                               to: "Tailscale’s coordination server and its relays, then your Tailscale devices (16 at most are asked whether they run Jellyfin).",
                               when: "Only once turned on, while Mira is on screen or a title still plays.",
                               sends: "Like the Tailscale app: your account, the device’s name, its system, its IP addresses and its public key. Videos stay end-to-end encrypted, no log is sent.",
                               stop: "The switch here.")
                }
                .accessibilityIdentifier("privacy.tailscale")
                PrivacyRow(title: "AltStore, installed by you",
                           to: "miracinema.app (Mira’s list), GitHub (the app) and Apple.",
                           when: "When you install Mira and at its weekly refresh.",
                           sends: "Your Apple ID, to Apple only, for the free certificate that signs the app. Mira itself never looks for updates.",
                           stop: "Nothing in Mira: it’s AltStore.")
            } header: {
                Text("From this device")
            }
            .listRowBackground(Palette.surface)

            Section {
                PrivacyRow(title: "Posters and descriptions",
                           to: "TMDb and Jellyfin’s other metadata sources.",
                           when: "When Jellyfin scans your library, and when you fix a title.",
                           sends: "Your titles’ names and years, from your server.",
                           stop: "In Jellyfin’s dashboard, Libraries.")
                PrivacyRow(title: "Automatic subtitles",
                           to: "OpenSubtitles and SubDL, through Jellyfin’s Mira extension.",
                           when: "Only once turned on, when a title arrives or when you search.",
                           sends: "The file’s fingerprint and size (OpenSubtitles), its IMDb and TMDb ids, season, episode and language. Never the file name.",
                           stop: "Turn them off in Mira for Windows or Mac, Maintenance.")
                PrivacyRow(title: "Missing episodes (TheTVDB)",
                           to: "TheTVDB, through its official Jellyfin extension.",
                           when: "Only if you installed it, at each library scan.",
                           sends: "Each series’ TheTVDB id, for its list of episodes. No name or picture from TheTVDB is kept.",
                           stop: "Uninstall it from Mira for Windows or Jellyfin’s dashboard.")
                PrivacyRow(title: "Mira extension updates",
                           to: "GitHub, where Mira is published.",
                           when: "When Jellyfin looks for updates to its extensions.",
                           sends: "What Jellyfin sends: its IP address and its program name.",
                           stop: "In Jellyfin’s dashboard, Plugins, Repositories.")
            } header: {
                Text("From your Jellyfin server")
            } footer: {
                Text("These connections leave from Jellyfin’s PC, never from this device.")
            }
            .listRowBackground(Palette.surface)

            Section {
                Link(destination: URL(string: "https://github.com/sasou-web/Mira/blob/main/SECURITY.md#ce-qui-part-sur-le-r%C3%A9seau")!) {
                    Label("Every detail (SECURITY.md)", systemImage: "doc.text")
                }
            }
            .listRowBackground(Palette.surface)
        }
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .foldsTabBar()
        .background(Palette.bg)
        .navigationTitle("Privacy")
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// One connection: its name, then where it goes, when, what it sends and how it stops, each on its line.
struct PrivacyRow: View {
    let title: LocalizedStringResource
    let to: LocalizedStringResource
    let when: LocalizedStringResource
    let sends: LocalizedStringResource
    let stop: LocalizedStringResource

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(MiraFont.headline).foregroundStyle(Palette.ink)
            Group {
                Text("**To** · \(String(localized: to))")
                Text("**When** · \(String(localized: when))")
                Text("**Sends** · \(String(localized: sends))")
                Text("**To stop it** · \(String(localized: stop))")
            }
            .font(MiraFont.footnote).foregroundStyle(Palette.muted)
            .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .combine)
    }
}
