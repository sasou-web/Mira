import Foundation

/// A title's name without what a download tool put before its file's name, as Core's TitleNames: Jellyfin names a film
/// after its file when it stands with others in a folder, and some tools save it as
/// « 0c76ce5c44cd079b008af19790bb4cc3_The Nightmare Before Christmas 1993.mkv » (a friend of ogsas, 2026-11-19).
/// 32 or 40 hexadecimal digits and « _ » before the name are never part of a title.
enum TitleNames {
    static func clean(_ name: String) -> String {
        for length in [40, 32] {
            let digits = name.prefix(length)
            guard digits.count == length, digits.allSatisfy(\.isHexDigit) else { continue }
            let rest = name.dropFirst(length)
            guard rest.first == "_" else { continue }
            let title = rest.drop { $0 == "_" }
            guard let first = title.first, !first.isWhitespace else { return name }
            return String(title)
        }
        return name
    }
}

/// `Item.name` and `Item.seriesName`, cleaned as Jellyfin's answer is read (TitleNames), and when set.
@propertyWrapper
struct TitleName: Codable, Sendable, Hashable {
    private var value: String?
    var wrappedValue: String? {
        get { value }
        set { value = newValue.map(TitleNames.clean) }
    }
    init(wrappedValue: String?) { value = wrappedValue.map(TitleNames.clean) }
    init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        self.init(wrappedValue: container.decodeNil() ? nil : try container.decode(String.self))
    }
    func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(value)
    }
}

extension KeyedDecodingContainer {
    /// A name missing from Jellyfin's answer is nil, as for the other optional fields.
    func decode(_ type: TitleName.Type, forKey key: Key) throws -> TitleName {
        try decodeIfPresent(type, forKey: key) ?? TitleName(wrappedValue: nil)
    }
}
