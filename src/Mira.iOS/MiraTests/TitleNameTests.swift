import Foundation
import Testing
@testable import Mira

/// The code a download tool puts before a file's name, never said (Core: TitleNames, its test « Titres : le code… »).
@Suite("Nom d’un titre")
struct TitleNameTests {
    private let code = "0c76ce5c44cd079b008af19790bb4cc3"

    @Test("Le code devant le nom part, le reste est gardé")
    func clean() {
        #expect(TitleNames.clean(code + "_The Nightmare Before Christmas") == "The Nightmare Before Christmas")
        #expect(TitleNames.clean("da39a3ee5e6b4b0d3255bfef95601890afd80709__Alien") == "Alien")
        let kept = ["L’Étrange Noël de monsieur Jack", "1917", code, code + "_", code + "_ ", "0c76ce5c44cd079b008af19790bb4cc_Trop court",
                    "0c76ce5c44cd079b008af19790bb4cc3a_Trop long", "Deadbeef_Film", "2001 : l’odyssée de l’espace"]
        #expect(kept.filter { TitleNames.clean($0) != $0 }.isEmpty)
    }

    @Test("Lu de Jellyfin, gardé dans le cache, ou donné en code")
    func item() throws {
        let data = Data(#"{"Id":"a","Name":"\#(code)_Pilot","SeriesName":"\#(code)_The Show","Type":"Episode"}"#.utf8)
        let item = try JSON.decoder.decode(Item.self, from: data)
        #expect(item.name == "Pilot" && item.seriesName == "The Show")
        let cached = try JSON.decoder.decode(Item.self, from: JSON.encoder.encode(item))
        #expect(cached.name == "Pilot" && cached.seriesName == "The Show")
        let bare = try JSON.decoder.decode(Item.self, from: Data(#"{"Id":"b"}"#.utf8))
        #expect(bare.name == nil && bare.seriesName == nil)
        var changed = bare
        changed.name = code + "_Alien"
        #expect(changed.name == "Alien")
    }
}
