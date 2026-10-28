import SwiftUI
import UIKit

/// Mira's palette: the tokens of Mira web (`:root` of app.css) and of Mira for Windows (Themes/Cinema.xaml), same
/// names. Dark only, as everywhere else in Mira.
enum Palette {
    static let bg = Color(hex: 0x070708)
    static let surface = Color(hex: 0x111112)
    static let raised = Color(hex: 0x1B1B1D)
    static let raised2 = Color(hex: 0x242427)
    static let line = Color(hex: 0x26262A)
    static let ink = Color(hex: 0xF4F4F5)
    static let muted = Color(hex: 0xA2A2A6)
    /// Never for text: below the contrast text needs on `bg` (as Dim on Windows).
    static let faint = Color(hex: 0x8A8A90)
    static let accent = Color(hex: 0xF5F5F7)
    static let onAccent = Color(hex: 0x0B0B0C)
    static let danger = Color(hex: 0xFF6B5F)
    static let warning = Color(hex: 0xD5B787)
}

extension Color {
    init(hex: UInt32) {
        self.init(.sRGB, red: Double(hex >> 16 & 0xFF) / 255, green: Double(hex >> 8 & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
}

/// Nunito Sans, Mira's typeface (the Latin subset of Mira web), scaled with Dynamic Type from each text style's size.
enum MiraFont {
    static func regular(_ size: CGFloat, _ style: Font.TextStyle) -> Font { .custom("NunitoSans-Regular", size: size, relativeTo: style) }
    static func semibold(_ size: CGFloat, _ style: Font.TextStyle) -> Font { .custom("NunitoSans-SemiBold", size: size, relativeTo: style) }
    static func bold(_ size: CGFloat, _ style: Font.TextStyle) -> Font { .custom("NunitoSans-Bold", size: size, relativeTo: style) }
    static func extraBold(_ size: CGFloat, _ style: Font.TextStyle) -> Font { .custom("NunitoSans-ExtraBold", size: size, relativeTo: style) }

    static let largeTitle = extraBold(34, .largeTitle)
    static let title = extraBold(28, .title)
    static let section = bold(21, .title3)
    static let headline = bold(17, .headline)
    static let body = regular(17, .body)
    static let callout = semibold(16, .callout)
    static let subheadline = semibold(15, .subheadline)
    static let footnote = semibold(13, .footnote)

    /// The navigation bars' titles in Nunito Sans too. Only their text: the bars keep the system's Liquid Glass.
    @MainActor static func applyToNavigationBars() {
        let bar = UINavigationBar.appearance()
        if let large = UIFont(name: "NunitoSans-ExtraBold", size: 34) {
            bar.largeTitleTextAttributes = [.font: UIFontMetrics(forTextStyle: .largeTitle).scaledFont(for: large)]
        }
        if let inline = UIFont(name: "NunitoSans-Bold", size: 17) {
            bar.titleTextAttributes = [.font: UIFontMetrics(forTextStyle: .headline).scaledFont(for: inline)]
        }
    }
}

/// Mira's mark, the « m » of its icon (mark.svg of Mira web), drawn in a 24 × 24 box.
struct MiraMark: Shape {
    func path(in rect: CGRect) -> Path {
        let s = min(rect.width, rect.height) / 24
        let x0 = rect.midX - 12 * s, y0 = rect.midY - 12 * s
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x0 + x * s, y: y0 + y * s) }
        var path = Path()
        path.move(to: p(6.3, 16.5))
        path.addLine(to: p(6.3, 10.6))
        path.addCurve(to: p(12, 10.6), control1: p(6.3, 6.3), control2: p(12, 6.3))
        path.addLine(to: p(12, 15.6))
        path.move(to: p(12, 10.6))
        path.addCurve(to: p(17.7, 10.6), control1: p(12, 6.3), control2: p(17.7, 6.3))
        path.addLine(to: p(17.7, 16.5))
        return path.strokedPath(StrokeStyle(lineWidth: 2.65 * s, lineCap: .round, lineJoin: .round))
    }
}

/// The app's icon as a view: the mark on the icon's dark gradient, rounded like an icon of the Home Screen.
struct MiraIcon: View {
    var size: CGFloat = 72
    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.225, style: .continuous)
            .fill(LinearGradient(colors: [Color(hex: 0x252528), Color(hex: 0x0D0D0F)], startPoint: .top, endPoint: .bottom))
            .overlay { MiraMark().fill(Palette.accent).padding(size * 0.02) }
            .overlay { RoundedRectangle(cornerRadius: size * 0.225, style: .continuous).strokeBorder(.white.opacity(0.08), lineWidth: 1) }
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// Buttons side by side, or one under the other when they do not fit (the largest text sizes, a long language name):
/// never a label cut in the middle.
struct ChipRow<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 10) { content }
            VStack(alignment: .leading, spacing: 10) { content }
        }
    }
}
