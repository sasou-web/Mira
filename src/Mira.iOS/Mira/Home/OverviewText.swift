import SwiftUI

/// A title's summary as plain text: Jellyfin passes on the HTML of some metadata providers (AniList's « <br> »), which
/// a Text would show as is. As overview.js of Mira web and OverviewText of Mira.Core (cases shared in
/// tests/fixtures/overview.json).
enum OverviewText {
    private static let named: [String: String] = [
        "amp": "&", "lt": "<", "gt": ">", "quot": "\"", "apos": "'", "nbsp": "\u{00A0}", "hellip": "…", "mdash": "—",
        "ndash": "–", "laquo": "«", "raquo": "»", "lsquo": "‘", "rsquo": "’", "ldquo": "“", "rdquo": "”", "bull": "•",
        "middot": "·", "copy": "©", "reg": "®", "trade": "™", "deg": "°", "times": "×", "euro": "€", "shy": "",
        "agrave": "à", "aacute": "á", "acirc": "â", "auml": "ä", "ccedil": "ç", "egrave": "è", "eacute": "é",
        "ecirc": "ê", "euml": "ë", "icirc": "î", "iuml": "ï", "ocirc": "ô", "ouml": "ö", "ugrave": "ù", "ucirc": "û",
        "uuml": "ü", "oelig": "œ", "Agrave": "À", "Eacute": "É", "Egrave": "È", "Ecirc": "Ê", "Ccedil": "Ç", "OElig": "Œ",
    ]

    /// <br> = a new line, a paragraph (or two <br>) = an empty line, other tags taken out, entities read, spaces and
    /// empty lines collapsed. flat: on one line (a summary cut at two lines).
    static func plain(_ text: String?, flat: Bool = false) -> String {
        guard let text, !text.isEmpty else { return "" }
        // « \r\n » is one Character in Swift: replaced whole, then a lone « \r ».
        var s = text.replacing("\r\n", with: "\n").replacing("\r", with: "\n")
        s = s.replacing(#/<br\s*/?>/#.ignoresCase(), with: "\n")
        s = s.replacing(#/</?(?:p|div|ul|ol|li|h[1-6]|blockquote)\b[^>]*>/#.ignoresCase(), with: "\n\n")
        s = s.replacing(#/</?[a-zA-Z][^>]*>/#, with: "")
        s = decode(s).split(separator: "\n", omittingEmptySubsequences: false)
            .map { line in line.replacing(#/[ \t]+/#, with: " ").trimmingCharacters(in: CharacterSet(charactersIn: " ")) }
            .joined(separator: "\n")
        s = s.replacing(#/\n{3,}/#, with: "\n\n").trimmingCharacters(in: CharacterSet(charactersIn: "\n"))
        return flat ? s.replacing(#/\n+/#, with: " ") : s
    }

    private static func decode(_ text: String) -> String {
        text.replacing(#/&(#[0-9]+|#[xX][0-9a-fA-F]+|[a-zA-Z]+);/#) { match in
            let name = match.output.1
            if name.hasPrefix("#") {
                let hex = name.dropFirst().first.map { $0 == "x" || $0 == "X" } ?? false
                let code = hex ? UInt32(name.dropFirst(2), radix: 16) : UInt32(name.dropFirst())
                if let code, code > 0, let scalar = Unicode.Scalar(code) { return String(Character(scalar)) }
                return String(match.output.0)
            }
            return named[String(name)] ?? String(match.output.0)
        }
    }
}

/// A summary cut at a few lines, with « More » once it really is cut (as Mira web): the episodes of a series come
/// right under it. Cut, its paragraphs run on, so that its lines are full; open, they are back. A tap on the text opens
/// it too.
struct ClampedText: View {
    let text: String
    var lines = 4
    @State private var open = false
    @State private var whole: CGFloat = 0
    @State private var shown: CGFloat = 0

    private var cut: Bool { !open && whole > shown + 1 }
    private var runOn: String {
        text.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.joined(separator: " ")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(verbatim: open ? text : runOn)
                .lineLimit(open ? nil : lines)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(alignment: .topLeading) {
                    // The text whole, unseen: its height tells whether the lines shown cut it.
                    Text(verbatim: open ? text : runOn)
                        .lineLimit(nil)
                        .fixedSize(horizontal: false, vertical: true)
                        .hidden()
                        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { whole = $0 }
                        .accessibilityHidden(true)
                }
                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { shown = $0 }
                .contentShape(.rect)
                .onTapGesture { if cut { withAnimation(.easeOut(duration: 0.2)) { open = true } } }
            if cut {
                Button {
                    withAnimation(.easeOut(duration: 0.2)) { open = true }
                } label: {
                    Text("More").font(MiraFont.extraBold(15, .subheadline)).foregroundStyle(Palette.ink)
                        .padding(.vertical, 6)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityHint(Text("Shows the whole summary"))
                .accessibilityIdentifier("overview.more")
            }
        }
        .onChange(of: text) { open = false }
    }
}
