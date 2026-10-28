import SwiftUI

@main
struct MiraApp: App {
    @State private var app = AppModel()

    init() { MiraFont.applyToNavigationBars() }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(app)
                .preferredColorScheme(.dark)
                .tint(Palette.accent)
        }
    }
}

/// Signed in: the tabs; otherwise the sign-in screen.
struct RootView: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        ZStack {
            Palette.bg.ignoresSafeArea()
            if app.session != nil {
                MainView().transition(.opacity)
            } else {
                LoginView().transition(.opacity)
            }
        }
        .animation(.easeInOut(duration: 0.25), value: app.session?.userId)
    }
}

struct MainView: View {
    enum Section: Hashable { case home, settings }
    @State private var section = Section.home

    var body: some View {
        TabView(selection: $section) {
            Tab("Home", systemImage: "house.fill", value: Section.home) {
                NavigationStack { HomeView() }
            }
            Tab("Settings", systemImage: "gearshape.fill", value: Section.settings) {
                NavigationStack { SettingsView() }
            }
        }
        .tabBarMinimizeBehavior(.onScrollDown)
    }
}

/// What to tell someone when Jellyfin cannot be reached or refuses, in Mira's words (as ServerAddress.cs).
enum Messages {
    static func text(_ error: Error) -> String {
        switch error {
        case let problem as ServerAddress.Problem:
            return problem == .empty ? String(localized: "Enter your Jellyfin server’s address, for example 192.168.1.20.")
                : String(localized: "Enter an http:// or https:// address, without credentials in it.")
        case let error as JellyfinError:
            switch error {
            case .unauthorized: return String(localized: "Wrong username or password.")
            case .forbidden: return String(localized: "Jellyfin doesn’t allow this account to do that.")
            case .timeout: return String(localized: "The server isn’t answering. Check the address and that Jellyfin is running.")
            case .notJellyfin: return String(localized: "This address answers, but it isn’t a Jellyfin server. Check the port (8096 by default).")
            case .certificate:
                return String(localized: "This server’s HTTPS certificate isn’t recognized. If this server is at your home, type its full address with http://, for example http://192.168.1.20:8096.")
            case let .tooOld(version):
                return String(localized: "Mira needs Jellyfin 10.9 or later; this server runs \(version). Update Jellyfin to continue.")
            case let .http(status):
                return String(localized: "Jellyfin answered with an error (\(status)). Try again in a moment.")
            case .unreachable:
                return String(localized: "Can’t reach Jellyfin. At home, check the Wi-Fi; away from home, turn on Tailscale.")
            }
        default:
            return String(localized: "Can’t reach Jellyfin. At home, check the Wi-Fi; away from home, turn on Tailscale.")
        }
    }
}
