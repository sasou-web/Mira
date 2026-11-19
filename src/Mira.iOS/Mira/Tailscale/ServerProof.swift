import CryptoKit
import Foundation

/// The proof that Jellyfin found at another address of the home network is this account's own, as Mira for Windows and
/// Mac ask it (ServerProof.cs): Mira's extension gives each account a key (GET Mira/proof/key, with its session), and
/// signs a random nonce with it, for the address it was asked at (GET Mira/proof, no session). Only the real server can;
/// a device that passed the question on to it by another way is asked at another address. Cases shared with Windows and
/// Mac: tests/fixtures/server-proof.json.
enum ServerProof {
    /// An account's key made from the extension's secret (the extension's side, for the tests).
    static func key(secret: Data, user: String) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: Data(("mira-proof\n" + (plainId(user) ?? "")).utf8), using: SymmetricKey(data: secret)))
    }

    /// The proof for `nonce` asked at `host` (the address's host and port, as the request's Host).
    static func prove(key: Data, nonce: String, host: String) -> String {
        let text = nonce + "\n" + host.trimmingCharacters(in: .whitespaces).lowercased()
        return Data(HMAC<SHA256>.authenticationCode(for: Data(text.utf8), using: SymmetricKey(data: key))).base64EncodedString()
    }

    /// Whether `proof` is the one for this nonce and host, with the account's key (base64).
    static func holds(key: String, nonce: String, host: String, proof: String?) -> Bool {
        guard let proof, let bytes = Data(base64Encoded: key), bytes.count == 32, let given = Data(base64Encoded: proof) else { return false }
        return Data(base64Encoded: prove(key: bytes, nonce: nonce, host: host)) == given
    }

    /// A new nonce, URL-safe.
    static func newNonce() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        for index in bytes.indices { bytes[index] = UInt8.random(in: .min ... .max) }
        return Data(bytes).base64EncodedString().replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
    }

    /// A user's id as Jellyfin writes it in a session: 32 lowercase hexadecimal digits; nil when it is none.
    static func plainId(_ id: String) -> String? {
        let plain = id.lowercased().filter { $0 != "-" }
        return plain.count == 32 && plain.allSatisfy(\.isHexDigit) ? plain : nil
    }

    /// The host and port a request to `base` names (its Host header).
    static func host(of base: URL) -> String {
        let host = base.host(percentEncoded: false) ?? ""
        return base.port.map { "\(host):\($0)" } ?? host
    }
}
