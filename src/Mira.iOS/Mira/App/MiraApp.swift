import SwiftUI
import Synchronization
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
        // The player wherever it is: under a menu or AirPlay's list it still says how the screen turns.
        var player = PlayerController.current.flatMap { $0.presentingViewController != nil ? $0 : nil }
        var next = window?.rootViewController
        while let controller = next {
            if let found = controller as? PlayerController { player = found }
            next = controller.presentedViewController
        }
        guard let player, !player.isBeingDismissed else { return .portrait }
        return player.supportedInterfaceOrientations
    }
}

/// Signed in: the tabs; otherwise the sign-in screen. Built-in Tailscale follows Mira on and off the screen.
struct RootView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.scenePhase) private var phase
    @Environment(\.openURL) private var openURL

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
        // A party link (mira://party/…): into the host's group, or its page in Safari as a guest.
        .onOpenURL { url in
            Task { if let web = await app.together.openParty(url) { openURL(web) } }
        }
        .onChange(of: phase) {
            switch phase {
            case .active: app.foreground()
            case .background: app.background()
            default: break
            }
        }
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
    /// The tab whose page has come in: a new tab's page fades in, quickly, instead of replacing the other at once.
    @State private var entered = Section.home

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
                MiraTabBar(section: $section, player: app.minimizedPlayer) { restarts[$0, default: 0] += 1 }
                    // Like iOS's own bar, it stops growing with the text, past which a long press shows it larger.
                    .dynamicTypeSize(...DynamicTypeSize.xLarge)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.easeOut(duration: 0.2), value: keyboard)
        .modifier(KeyboardWatch(shown: $keyboard))
        .environment(bar)
        .onChange(of: section) { _, now in
            bar.folded = false
            withAnimation(.easeOut(duration: 0.2)) { entered = now }
        }
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
            .opacity(entered == tab || section != tab ? 1 : 0)
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
            case .timeout:
                if let hint = Way.hint.withLock({ $0 }) { return text(hint) }
                return String(localized: "The server isn’t answering. Check the address and that Jellyfin is running.")
            case .notJellyfin: return String(localized: "This address answers, but it isn’t a Jellyfin server. Check the port (8096 by default).")
            case .certificate:
                return String(localized: "This server’s HTTPS certificate isn’t recognized. If this server is at your home, type its full address with http://, for example http://192.168.1.20:8096.")
            case let .tooOld(version):
                return String(localized: "Mira needs Jellyfin 10.9 or later; this server runs \(version). Update Jellyfin to continue.")
            case let .http(status):
                return String(localized: "Jellyfin answered with an error (\(status)). Try again in a moment.")
            case .unreachable:
                if let hint = Way.hint.withLock({ $0 }) { return text(hint) }
                return String(localized: "Can’t reach Jellyfin. At home, check the Wi-Fi; away from home, turn on Tailscale.")
            case let .tailscale(code):
                return tailscale(code)
            }
        default:
            if let hint = Way.hint.withLock({ $0 }) { return text(hint) }
            return String(localized: "Can’t reach Jellyfin. At home, check the Wi-Fi; away from home, turn on Tailscale.")
        }
    }

    /// Why no way reaches Jellyfin, from what Mira knows of the network and of Built-in Tailscale (Way).
    static func text(_ hint: WayPlan.Hint) -> String {
        switch hint {
        case .noNetwork:
            String(localized: "This iPhone isn’t connected to the Internet.")
        case .tailscaleOff:
            String(localized: "Can’t reach Jellyfin. Away from home, turn on Built-in Tailscale in Settings, or the Tailscale app.")
        case .signedOut:
            String(localized: "Built-in Tailscale is signed out: sign in again in Settings.")
        case .expired:
            String(localized: "This iPhone’s sign-in to Tailscale has expired: sign in again in Settings.")
        case .approval:
            String(localized: "Built-in Tailscale is waiting for this iPhone to be approved in Tailscale’s admin console.")
        case .starting:
            String(localized: "Built-in Tailscale is connecting. Try again in a moment.")
        case .offline:
            String(localized: "Built-in Tailscale can’t reach Tailscale. Check this iPhone’s connection.")
        case let .serverOffline(name):
            String(localized: "The PC with Jellyfin (\(name)) is offline on Tailscale: check that it’s on and connected to Tailscale.")
        case .serverNotFound:
            String(localized: "No device of your Tailscale network answers as this Jellyfin. Check that the PC with Jellyfin is on and connected to Tailscale.")
        }
    }

    /// A request Built-in Tailscale's relay could not take to Jellyfin, by its reason (X-Mira-Tailscale).
    static func tailscale(_ code: String) -> String {
        switch code {
        case "needs-login": text(.signedOut)
        case "expired": text(.expired)
        case "machine-auth": text(.approval)
        case "starting", "stopped": text(.starting)
        case "offline": text(.offline)
        case "peer-offline":
            String(localized: "The PC with Jellyfin is offline on Tailscale: check that it’s on and connected to Tailscale.")
        case "closed":
            String(localized: "The PC with Jellyfin answers on Tailscale, but Jellyfin doesn’t: check that Jellyfin is running.")
        case "refused":
            String(localized: "Built-in Tailscale only reaches the devices of your Tailscale network.")
        case "certificate":
            String(localized: "This server’s HTTPS certificate isn’t recognized. If this server is at your home, type its full address with http://, for example http://192.168.1.20:8096.")
        default:
            String(localized: "The PC with Jellyfin doesn’t answer on Tailscale. Check that it’s on, then try again.")
        }
    }
}

/// A notice (« Watch together », a device authorized…) said for a few seconds over the top of the app: someone came or went, the group ended.
struct NoticeToast: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        let notice = app.together.notice
        ZStack {
            if let notice {
                // A card as on Mira web and the computers: its icon in a tile, the text beside it.
                HStack(spacing: 12) {
                    Image(systemName: "info")
                        .font(.system(size: 17, weight: .bold))
                        .foregroundStyle(Palette.ink)
                        .frame(width: 40, height: 40)
                        .background(.white.opacity(0.1), in: .rect(cornerRadius: 12))
                        .accessibilityHidden(true)
                    Text(verbatim: notice)
                        .font(MiraFont.headline)
                        .foregroundStyle(Palette.ink)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(12)
                .frame(minHeight: 64)
                .accessibilityElement(children: .combine)
                .glassEffect(.regular, in: .rect(cornerRadius: 20))
                .padding(.horizontal, 16)
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
