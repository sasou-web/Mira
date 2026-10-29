import SwiftUI
import UIKit

@main
struct MiraApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var delegate
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

/// On iPhone, Mira's pages stay upright and only the player turns, sideways for a film unless turned back (its
/// button): coming back from a film watched sideways, the pages are never laid out sideways first, then upright
/// again. iPad turns everywhere.
final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(_ application: UIApplication, supportedInterfaceOrientationsFor window: UIWindow?) -> UIInterfaceOrientationMask {
        guard UIDevice.current.userInterfaceIdiom == .phone else { return .all }
        var top = window?.rootViewController
        while let next = top?.presentedViewController { top = next }
        guard let player = top as? PlayerController, !player.isBeingDismissed else { return .portrait }
        return player.supportedInterfaceOrientations
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

/// The tabs, each keeping its pages, under Mira's own tab bar (MiraTabBar): iOS's bar stays hidden.
struct MainView: View {
    enum Section: Hashable { case home, movies, shows, search, settings }
    @Environment(AppModel.self) private var app
    @Environment(\.scenePhase) private var phase
    @State private var section = Section.home
    @State private var bar = TabBarModel()
    /// A tab touched again while open: its pages start over from the first.
    @State private var restarts: [Section: Int] = [:]
    @State private var keyboard = false

    var body: some View {
        TabView(selection: $section) {
            Tab("Home", systemImage: "house.fill", value: Section.home) { stack(.home) { HomeView() } }
            Tab("Movies", systemImage: "film.fill", value: Section.movies) { stack(.movies) { LibraryView(kind: .movies) } }
            Tab("Shows", systemImage: "tv.fill", value: Section.shows) { stack(.shows) { LibraryView(kind: .shows) } }
            Tab("Settings", systemImage: "gearshape.fill", value: Section.settings) { stack(.settings) { SettingsView() } }
            Tab("Search", systemImage: "magnifyingglass", value: Section.search) { stack(.search) { SearchView() } }
        }
        // Over the tabs, not a safe-area inset: TabView doesn't pass an inset on to the tabs' pages, which make room
        // for the bar themselves (foldsTabBar).
        .overlay(alignment: .bottom) {
            if !keyboard {
                MiraTabBar(section: $section) { restarts[$0, default: 0] += 1 }
                    // Like iOS's own bar, it stops growing with the text, past which a long press shows it larger.
                    .dynamicTypeSize(...DynamicTypeSize.xLarge)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.easeOut(duration: 0.2), value: keyboard)
        .modifier(KeyboardWatch(shown: $keyboard))
        .environment(bar)
        .onChange(of: section) { bar.folded = false }
        .miniPlayer(app.minimizedPlayer)
        .overlay(alignment: .top) { NoticeToast() }
        .overlay(alignment: .top) { NewEpisodesBanner() }
        .onChange(of: phase) {
            if phase == .active {
                app.together.foreground()
                app.syncChoices(lookAgain: true)
                Task { await app.lookForNewEpisodes(comingBack: true) }
            }
        }
        // Signed in or opened: what changed on the other devices comes, what changed here goes.
        .task(id: app.session?.userId) {
            app.syncChoices()
            await app.lookForNewEpisodes()
        }
        .alert("Can’t play this title", isPresented: Binding(get: { app.playerMessage != nil }, set: { if !$0 { app.playerMessage = nil } })) {
            if let retry = app.retryItem {
                Button("Try again") {
                    app.retryItem = nil
                    app.play(retry)
                }
            }
            Button("OK", role: .cancel) { app.retryItem = nil }
        } message: {
            Text(verbatim: app.playerMessage ?? "")
        }
        .alert("Jellyfin refused", isPresented: Binding(get: { app.actionMessage != nil }, set: { if !$0 { app.actionMessage = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(verbatim: app.actionMessage ?? "")
        }
    }

    /// A tab's pages, iOS's tab bar hidden on each.
    private func stack(_ tab: Section, @ViewBuilder _ root: () -> some View) -> some View {
        NavigationStack { root() }
            .toolbar(.hidden, for: .tabBar)
            .id(restarts[tab, default: 0])
    }
}

/// What to tell someone when Jellyfin cannot be reached or refuses, in Mira's words (as ServerAddress.cs).
enum Messages {
    static func text(_ error: Error) -> String {
        switch error {
        case let problem as ServerAddress.Problem:
            return problem == .empty ? String(localized: "Enter your Jellyfin server’s address, for example 192.168.1.20.")
                : String(localized: "Enter an http:// or https:// address, without credentials in it.")
        case let error as TogetherError:
            return error.errorDescription ?? ""
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

/// A line of « Watch together » said for a few seconds over the top of the app: someone came or went, the group ended.
struct NoticeToast: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        let notice = app.together.notice
        ZStack {
            if let notice {
                Text(verbatim: notice)
                    .font(MiraFont.callout)
                    .foregroundStyle(Palette.ink)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 18)
                    .padding(.vertical, 12)
                    .glassEffect(.regular, in: .capsule)
                    .padding(.horizontal, 24)
                    .padding(.top, 6)
                    .transition(.move(edge: .top).combined(with: .opacity))
                    .accessibilityIdentifier("notice")
                    .onTapGesture { app.together.notice = nil }
                    .task(id: notice) {
                        AccessibilityNotification.Announcement(notice).post()
                        try? await Task.sleep(for: .seconds(4))
                        if app.together.notice == notice { app.together.notice = nil }
                    }
            }
        }
        .animation(.spring(duration: 0.35), value: notice)
    }
}
