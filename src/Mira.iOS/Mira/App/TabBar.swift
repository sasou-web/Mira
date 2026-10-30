import Combine
import SwiftUI
import UIKit

/// Mira's tab bar, floating over the pages: the four tabs in one bar of glass, the open one in a light squircle with its
/// name that slides to the next with a spring while its icon bounces; Search on its own on the right, as in iOS's own
/// bar. Scrolling a page down folds it to the open tab and Search, scrolling up or back to the top unfolds it. The open
/// tab touched again goes back to its first page. Its size stops at a large text size (MainView), past which a long
/// press on a tab shows it larger, as with iOS's own bar. The player reduced (MiniPlayer) sits over the tabs, and
/// between the open tab and Search while the bar is folded.
struct MiraTabBar: View {
    @Binding var section: MainView.Section
    /// The player reduced to a bar, if there is one.
    let player: PlayerSession?
    /// The open tab, touched again.
    let reselect: (MainView.Section) -> Void
    @Environment(TabBarModel.self) private var model
    @Namespace private var lens
    /// The tabs' height, growing with the text.
    @ScaledMetric(relativeTo: .footnote) private var side: CGFloat = 46

    struct Entry: Identifiable {
        let section: MainView.Section
        let name: LocalizedStringKey
        let symbol: String
        let id: String
    }

    static let entries = [
        Entry(section: .home, name: "Home", symbol: "house.fill", id: "tab.home"),
        Entry(section: .movies, name: "Movies", symbol: "film.fill", id: "tab.movies"),
        Entry(section: .shows, name: "Shows", symbol: "tv.fill", id: "tab.shows"),
        Entry(section: .settings, name: "Settings", symbol: "gearshape.fill", id: "tab.settings"),
    ]

    /// The widest name with its icon, as the open tab shows it: the open tab keeps this width whichever it is, so the
    /// bar keeps its size and only the light squircle moves from one tab to the next.
    @State private var named: CGFloat = 0
    /// The bar's width, Search comprised.
    @State private var width: CGFloat = 0
    /// Each tab's bounces, counted when it opens (only the tab that opens bounces, not the one left).
    @State private var bounces: [MainView.Section: Int] = [:]
    private let spring = Animation.spring(response: 0.42, dampingFraction: 0.82)

    var body: some View {
        GlassEffectContainer(spacing: 12) {
            VStack(spacing: 10) {
                if let player, !model.folded {
                    reduced(player, inline: false)
                }
                HStack(spacing: 12) {
                    if let player, model.folded {
                        // Folded, the open tab's icon alone, to leave the reduced player its title.
                        tabs(named: false)
                        reduced(player, inline: true)
                    } else {
                        // The names while the widest fits, whichever tab is open; at the largest text sizes, the icons
                        // alone. Decided by the widest name, never by the open tab's: a tab change never changes it.
                        tabs(named: namesFit)
                        Spacer(minLength: 0)
                    }
                    search
                }
            }
        }
        .padding(.horizontal, 20)
        .padding(.bottom, -8)
        .background(alignment: .leading) {
            // The widest open tab, measured out of sight.
            ZStack { ForEach(Self.entries) { label($0, chosen: true, named: true) } }
                .fixedSize()
                .hidden()
                .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { named = $0 }
        }
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { height in
            // Folded, the bar may be lower (the reduced player beside the open tab): the pages keep the room they had,
            // or a page would grow and shrink at its end as it scrolls, folding and unfolding the bar.
            if !model.folded || height > model.room { model.room = height }
        }
        .onDisappear { model.room = 0 }
        .onChange(of: section) { _, now in bounces[now, default: 0] += 1 }
        .animation(spring, value: section)
        .animation(spring, value: model.folded)
        .animation(spring, value: player == nil)
        .sensoryFeedback(.selection, trigger: section)
    }

    /// Whether the four tabs fit with the widest name beside Search.
    private var namesFit: Bool {
        guard named > 0, width > 0 else { return true }
        let others = CGFloat(Self.entries.count - 1)
        let tabs = 12 + others * 4 + others * side + named
        return tabs + 12 + side + 12 <= width - 40
    }

    private func tabs(named: Bool) -> some View {
        let shown = Self.entries.filter { section == $0.section || !model.folded }
        let open = shown.firstIndex { $0.section == section }
        let wide = named ? max(self.named, side) : side
        return HStack(spacing: 4) {
            ForEach(shown) { entry in
                let chosen = section == entry.section
                Button {
                    touch(entry.section)
                } label: {
                    label(entry, chosen: chosen, named: named)
                        .frame(width: chosen ? wide : side, height: side)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text(entry.name))
                .accessibilityAddTraits(chosen ? .isSelected : [])
                .accessibilityIdentifier(entry.id)
                .accessibilityShowsLargeContentViewer { Label { Text(entry.name) } icon: { Image(systemName: entry.symbol) } }
                .transition(.opacity.combined(with: .scale(scale: 0.6)))
            }
        }
        .background(alignment: .leading) {
            // One squircle that slides under the tabs, the same spring as the tabs' widths: never one squircle fading
            // out while another fades in.
            if let open {
                RoundedRectangle(cornerRadius: side * 0.35, style: .continuous)
                    .fill(Palette.accent)
                    .frame(width: wide, height: side)
                    .offset(x: CGFloat(open) * (side + 4))
                    .transition(.opacity)
            }
        }
        .padding(6)
        .glassEffect(.regular.interactive(), in: .rect(cornerRadius: side * 0.35 + 6, style: .continuous))
    }

    private func label(_ entry: Entry, chosen: Bool, named: Bool) -> some View {
        HStack(spacing: 6) {
            Image(systemName: entry.symbol)
                .font(.body.weight(.bold))
                .symbolEffect(.bounce.up.byLayer, value: bounces[entry.section, default: 0])
            if chosen && named {
                Text(entry.name).font(MiraFont.extraBold(14, .footnote)).lineLimit(1).fixedSize()
                    .transition(.opacity.combined(with: .scale(scale: 0.85, anchor: .leading)))
            }
        }
        .padding(.horizontal, chosen && named ? 14 : 0)
        .foregroundStyle(chosen ? Palette.onAccent : Palette.ink.opacity(0.75))
    }

    /// The reduced player, in the same glass as the tabs, its height theirs.
    private func reduced(_ player: PlayerSession, inline: Bool) -> some View {
        MiniPlayer(session: player, inline: inline)
            .frame(minHeight: side + 12)
            .glassEffect(.regular.interactive(), in: .rect(cornerRadius: side * 0.35 + 6, style: .continuous))
            .glassEffectID("player", in: lens)
    }

    private var search: some View {
        let chosen = section == .search
        return Button {
            touch(.search)
        } label: {
            Image(systemName: "magnifyingglass")
                .font(.title3.weight(.bold))
                .foregroundStyle(chosen ? Palette.onAccent : Palette.ink)
                .frame(width: side, height: side)
                .background {
                    if chosen { RoundedRectangle(cornerRadius: side * 0.35, style: .continuous).fill(Palette.accent) }
                }
                .padding(6)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular.interactive(), in: .rect(cornerRadius: side * 0.35 + 6, style: .continuous))
        .accessibilityLabel(Text("Search"))
        .accessibilityAddTraits(chosen ? .isSelected : [])
        .accessibilityIdentifier("tab.search")
        .accessibilityShowsLargeContentViewer { Label("Search", systemImage: "magnifyingglass") }
    }

    private func touch(_ tab: MainView.Section) {
        if model.folded {
            model.folded = false
            if tab == section { return }
        }
        if tab == section {
            reselect(tab)
        } else {
            // Not in an animation: the pages change at once, only the bar moves (its own animation), or the new page
            // would animate from the old one's layout.
            section = tab
        }
    }
}

/// Whether the tab bar is folded, as the page scrolls, and how far it reaches above the bottom of the screen's safe
/// area (none while it is hidden).
@MainActor @Observable
final class TabBarModel {
    var folded = false
    var room: CGFloat = 0
}

extension View {
    /// Scrolling this page down folds Mira's tab bar to the open tab, scrolling up or back to the top unfolds it; the
    /// page's last lines stop above the bar.
    func foldsTabBar() -> some View { modifier(TabBarFolding()) }
}

private struct TabBarFolding: ViewModifier {
    @Environment(TabBarModel.self) private var bar: TabBarModel?

    func body(content: Content) -> some View {
        content
            .onScrollGeometryChange(for: CGFloat.self) { $0.contentOffset.y + $0.contentInsets.top } action: { old, new in
                guard let bar else { return }
                let folded = new < 24 ? false : new - old > 4 ? true : old - new > 12 ? false : bar.folded
                if folded != bar.folded { bar.folded = folded }
            }
            .safeAreaPadding(.bottom, bar.map { $0.room > 0 ? $0.room + 8 : 0 } ?? 0)
    }
}

/// Whether the keyboard is up: the tab bar steps out of its way.
struct KeyboardWatch: ViewModifier {
    @Binding var shown: Bool

    func body(content: Content) -> some View {
        content
            .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardWillShowNotification)) { _ in shown = true }
            .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardWillHideNotification)) { _ in shown = false }
    }
}
