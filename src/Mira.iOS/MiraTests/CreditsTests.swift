import Foundation
import Testing
@testable import Mira

/// A title's cast and crew as its page lists them, and the line under a person's name.
@Suite("Distribution d’un titre")
struct CreditsTests {
    private func item(_ people: [Person]) -> Item {
        var item = Item(id: "film")
        item.people = people
        return item
    }

    @Test("Réalisation d’abord, puis les acteurs, chacun une fois")
    func order() {
        let credits = Credits.of(item([
            Person(id: "1", name: "Derek de Lint", role: "Thom", type: "Actor"),
            Person(id: "2", name: "Ian Hubert", type: "Director"),
            Person(id: "1", name: " derek de lint ", role: "Old Thom", type: "Actor"),
            Person(id: "3", name: "Vanja Rukavina", role: "  ", type: "Actor"),
            Person(id: "4", name: "Ton Roosendaal", type: "Producer"),
            Person(id: "5", name: "Jody Bhe", type: "GuestStar"),
            Person(id: nil, name: "Sans page", type: "Actor"),
            Person(id: "6", name: "  ", type: "Actor"),
        ]))
        #expect(credits.map(\.id) == ["2", "1", "3", "5"])
        #expect(credits.map(\.role) == [String(localized: "Director"), "Thom", nil, String(localized: "Guest star")])
        #expect(credits[1].name == "Derek de Lint")
    }

    @Test("Trois réalisateurs et douze acteurs au plus")
    func limits() {
        let directors = (0..<5).map { Person(id: "d\($0)", name: "Réalisateur \($0)", type: "director") }
        let actors = (0..<20).map { Person(id: "a\($0)", name: "Acteur \($0)", type: "Actor") }
        let credits = Credits.of(item(directors + actors))
        #expect(credits.count == 15)
        #expect(credits.prefix(3).map(\.id) == ["d0", "d1", "d2"])
        #expect(credits.last?.id == "a11")
        #expect(Credits.of(Item(id: "vide")).isEmpty)
    }

    @Test("La ligne sous le nom")
    func life() {
        let en = Locale(identifier: "en_US")
        var person = Item(id: "p")
        #expect(Credits.life(person, locale: en) == "")
        person.productionLocations = ["", "The Hague, Netherlands"]
        #expect(Credits.life(person, locale: en) == "The Hague, Netherlands")
        person.premiereDate = "1950-07-15T00:00:00.0000000Z"
        #expect(Credits.life(person, locale: en).hasSuffix("July 15, 1950 · The Hague, Netherlands"))
        person.endDate = "2024-01-02T00:00:00.0000000Z"
        #expect(Credits.life(person, locale: en) == "July 15, 1950 – January 2, 2024 · The Hague, Netherlands")
        #expect(Credits.day("0001-01-01T00:00:00.0000000Z", locale: en) == nil)
        #expect(Credits.day("n/a", locale: en) == nil)
        #expect(Credits.day("1950-07-15", locale: Locale(identifier: "fr_FR")) == "15 juillet 1950")
    }
}
