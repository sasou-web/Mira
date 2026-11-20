import Foundation

/// The short link of « Inviter un ami » and of « Le foyer » (JoinLink of Core, made by the extension):
/// https://miracinema.app/join#<data>, or mira://join#<data> that the page hands to Mira. Its data is base64url of
/// [kind][IPv4 on the tailnet][port][key length][key without « tskey-auth- »][path]: no address or key readable in the
/// link, nothing else in it. Anything else is not one (nil).
struct JoinLink: Sendable, Hashable {
    enum Kind: UInt8, Sendable { case home = 1, friend = 2 }

    let kind: Kind
    /// The server on the tailnet: http://100.x.y.z:port[/path]/
    let address: URL
    let key: String

    static let page = "https://miracinema.app/join#"
    static let app = "mira://join#"
    private static let keyPrefix = "tskey-auth-"
    /// The kind's high bit: the key kept whole (it did not start with keyPrefix).
    private static let wholeKey: UInt8 = 0x80

    /// The address's host: what the node alone reaches.
    var host: String { address.host(percentEncoded: false) ?? "" }

    /// Whether a text is such a link at all (its page or Mira's), read or not.
    static func isJoinLink(_ text: String?) -> Bool {
        guard let text = text?.lowercased() else { return false }
        return text.hasPrefix(app) || text.hasPrefix("mira://join/#") || text.hasPrefix(page)
    }

    static func read(_ text: String?) -> JoinLink? {
        guard let text, isJoinLink(text), text.count <= 1200, let hash = text.firstIndex(of: "#") else { return nil }
        let data = text[text.index(after: hash)...].trimmingCharacters(in: .whitespacesAndNewlines)
        guard !data.isEmpty, data.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || "-_=".contains($0)) }) else { return nil }
        var base64 = data.replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard let decoded = Data(base64Encoded: base64) else { return nil }
        let bytes = [UInt8](decoded)
        guard bytes.count >= 9, let kind = Kind(rawValue: bytes[0] & ~wholeKey) else { return nil }
        let length = Int(bytes[7])
        guard length > 0, bytes.count >= 8 + length else { return nil }
        let port = Int(bytes[5]) << 8 | Int(bytes[6])
        guard port > 0 else { return nil }
        let body = bytes[8..<(8 + length)]
        guard body.allSatisfy({ ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x41 && $0 <= 0x5a) || ($0 >= 0x61 && $0 <= 0x7a) || $0 == 0x2d || $0 == 0x5f }) else { return nil }
        guard let path = String(bytes: bytes[(8 + length)...], encoding: .utf8) else { return nil }
        if !path.isEmpty {
            guard path.hasPrefix("/"), !path.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) || "?#@\\ ".unicodeScalars.contains($0) }) else { return nil }
        }
        // Only a device of a tailnet: the node reaches that one host, nothing a link could aim elsewhere.
        let host = "\(bytes[1]).\(bytes[2]).\(bytes[3]).\(bytes[4])"
        guard WayPlan.isTailnetHost(host) else { return nil }
        let secret = String(decoding: body, as: UTF8.self)
        let key = bytes[0] & wholeKey != 0 ? secret : keyPrefix + secret
        guard key.hasPrefix("tskey-") else { return nil }
        var root = "http://\(host):\(port)\(path)"
        while root.hasSuffix("/") { root.removeLast() }
        guard let address = URL(string: root + "/") else { return nil }
        return JoinLink(kind: kind, address: address, key: key)
    }
}
