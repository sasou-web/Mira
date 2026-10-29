import Combine
import SwiftUI
import UIKit

/// Mira's tab bar, floating over the pages: the four tabs in one bar of glass, the open one in a light squircle with its
/// name that slides to the next with a spring while its icon bounces; Search on its own on the right, as in iOS's own
/// bar. Scrolling a page down folds it to the open tab and Search, scrolling up or back to the top unfolds it. The open
/// tab touched again goes back to its first page.
struct MiraTabBar: View {
    @Binding var section: MainView.Section
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

    var body: some View {
        GlassEffectContainer(spacing: 12) {
            HStack(spacing: 12) {
                // The open tab's name while it fits; at the largest text sizes, the icons alone.
                ViewThatFits(in: .horizontal) {
                    tabs(named: true)
                    tabs(named: false)
                }
                Spacer(minLength: 0)
                search
            }
        }
        .padding(.horizontal, 20)
        .padding(.bottom, -8)
        .animation(.spring(response: 0.38, dampingFraction: 0.8), value: model.folded)
        .sensoryFeedback(.selection, trigger: section)
    }

    private func tabs(named: Bool) -> some View {
        HStack(spacing: 4) {
            ForEach(Self.entries) { entry in
                let chosen = section == entry.section
                if chosen || !model.folded {
                    Button {
                        touch(entry.section)
                    } label: {
                        HStack(spacing: 7) {
                            Image(systemName: entry.symbol)
                                .font(.body.weight(.bold))
                                .symbolEffect(.bounce.up.byLayer, value: chosen)
                            if chosen && named {
                                Text(entry.name).font(MiraFont.extraBold(14, .footnote)).lineLimit(1).fixedSize()
                            }
                        }
                        .foregroundStyle(chosen ? Palette.onAccent : Palette.ink.opacity(0.75))
                        .padding(.horizontal, chosen && named ? 16 : 0)
                        .frame(minWidth: side, minHeight: side)
                        .background {
                            if chosen {
                                RoundedRectangle(cornerRadius: side * 0.35, style: .continuous)
                                    .fill(Palette.accent)
                                    .matchedGeometryEffect(id: "lens", in: lens)
                            }
                        }
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(Text(entry.name))
                    .accessibilityAddTraits(chosen ? .isSelected : [])
                    .accessibilityIdentifier(entry.id)
                    .transition(.opacity.combined(with: .scale(scale: 0.6)))
                }
            }
        }
        .padding(6)
        .glassEffect(.regular.interactive(), in: .rect(cornerRadius: side * 0.35 + 6, style: .continuous))
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
    }

    private func touch(_ tab: MainView.Section) {
        if model.folded {
            model.folded = false
            if tab == section { return }
        }
        if tab == section {
            reselect(tab)
        } else {
            withAnimation(.spring(response: 0.38, dampingFraction: 0.8)) { section = tab }
        }
    }
}

/// Whether the tab bar is folded, as the page scrolls.
@MainActor @Observable
final class TabBarModel {
    var folded = false
}

extension View {
    /// Scrolling this page down folds Mira's tab bar to the open tab, scrolling up or back to the top unfolds it.
    func foldsTabBar() -> some View { modifier(TabBarFolding()) }
}

private struct TabBarFolding: ViewModifier {
    @Environment(TabBarModel.self) private var bar: TabBarModel?

    func body(content: Content) -> some View {
        content.onScrollGeometryChange(for: CGFloat.self) { $0.contentOffset.y + $0.contentInsets.top } action: { old, new in
            guard let bar else { return }
            let folded = new < 24 ? false : new - old > 4 ? true : old - new > 12 ? false : bar.folded
            if folded != bar.folded { bar.folded = folded }
        }
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
