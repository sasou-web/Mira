import Foundation
import Testing
@testable import Mira

/// Mira's opening (App/Opening.swift): when it leaves, never later than what it waits for, and the « m » it writes
/// meanwhile, starting whole as on iOS's launch screen.
@Suite("Ouverture de Mira")
struct OpeningTests {
    private let start = Date(timeIntervalSinceReferenceDate: 1_000_000)

    private func decide(_ home: OpeningPlan.Home, after seconds: TimeInterval, signedIn: Bool = true, party: Bool = false,
                        noWay: Bool = false) -> OpeningPlan.Decision {
        OpeningPlan.decide(signedIn: signedIn, party: party, home: home, noWay: noWay, startedAt: start, now: start.addingTimeInterval(seconds))
    }

    private func close(_ a: MarkStroke.Frame, _ b: MarkStroke.Frame) -> Bool {
        abs(a.from - b.from) < 1e-6 && abs(a.to - b.to) < 1e-6 && abs(a.tip - b.tip) < 1e-6 && abs(a.light - b.light) < 1e-6
    }

    @Test("S'en va dès que l'Accueil est dessiné, ou qu'il dit pourquoi il ne l'est pas")
    func leavesWhenHomeIsThere() {
        #expect(decide(.ready, after: 0.2) == .leave)
        #expect(decide(.failed, after: 0.2) == .leave)
    }

    @Test("Rien à attendre : l'écran de connexion, une party, aucun chemin vers Jellyfin")
    func leavesAtOnce() {
        #expect(decide(.waiting, after: 0, signedIn: false) == .leave)
        #expect(decide(.kept, after: 0, party: true) == .leave)
        #expect(decide(.waiting, after: 0.5, noWay: true) == .leave)
        #expect(decide(.kept, after: 0.5, noWay: true) == .leave)
    }

    @Test("Attend Jellyfin, jamais au-delà de ses limites")
    func waitsWithinItsLimits() {
        #expect(decide(.waiting, after: 1) == .wait(until: start.addingTimeInterval(OpeningPlan.withNothing)))
        #expect(decide(.waiting, after: OpeningPlan.withNothing) == .leave)
        // The Home kept from the last time: shown sooner rather than Jellyfin waited for.
        #expect(decide(.kept, after: 1) == .wait(until: start.addingTimeInterval(OpeningPlan.withKept)))
        #expect(decide(.kept, after: OpeningPlan.withKept) == .leave)
    }

    @Test("Jellyfin a répondu : les images du haut de l'écran, un instant au plus")
    func waitsForThePictures() {
        let answered = start.addingTimeInterval(2)
        #expect(decide(.answered(answered), after: 2.3) == .wait(until: answered.addingTimeInterval(OpeningPlan.pictures)))
        #expect(decide(.answered(answered), after: 2 + OpeningPlan.pictures + 0.01) == .leave)
        // Answered late: the opening's own limit stands.
        let late = start.addingTimeInterval(OpeningPlan.withNothing - 0.5)
        #expect(decide(.answered(late), after: OpeningPlan.withNothing - 0.4) == .wait(until: start.addingTimeInterval(OpeningPlan.withNothing)))
        // The way lost once Jellyfin answered: the pictures are still waited for, as long.
        #expect(decide(.answered(answered), after: 2.3, noWay: true) == .wait(until: answered.addingTimeInterval(OpeningPlan.pictures)))
    }

    @Test("Le « m » entier au départ, comme sur l'écran de lancement")
    func startsWhole() {
        let whole = MarkStroke.Frame(from: 0, to: 1, tip: 1, light: 0)
        #expect(MarkStroke.frame(0) == whole)
        #expect(MarkStroke.frame(MarkStroke.still) == whole)
    }

    @Test("Puis effacé depuis où la plume part, et réécrit, en boucle")
    func writesAgainAndAgain() {
        let whole = MarkStroke.Frame(from: 0, to: 1, tip: 1, light: 0)
        // Wound back, the pen's light brightest halfway.
        let halfway = MarkStroke.frame(MarkStroke.still + MarkStroke.unwrite / 2)
        #expect(close(halfway, MarkStroke.Frame(from: 0.5, to: 1, tip: 0.5, light: 1)))
        // Nothing drawn for a moment.
        let blank = MarkStroke.frame(MarkStroke.still + MarkStroke.unwrite + MarkStroke.blank / 2)
        #expect(blank == MarkStroke.Frame(from: 1, to: 1, tip: 1, light: 0))
        // Written again from the start, the pen at the end of what is drawn.
        let writing = MarkStroke.frame(MarkStroke.still + MarkStroke.unwrite + MarkStroke.blank + MarkStroke.write / 4)
        #expect(writing.from == 0)
        #expect(writing.to > 0 && writing.to < 0.5)
        #expect(writing.tip == writing.to)
        #expect(writing.light > 0)
        // Whole for a moment, then the same again.
        #expect(MarkStroke.frame(MarkStroke.still + MarkStroke.period - 0.1) == whole)
        #expect(close(MarkStroke.frame(MarkStroke.still + 0.3), MarkStroke.frame(MarkStroke.still + MarkStroke.period + 0.3)))
    }

    @Test("Réduire les animations : rien ne bouge, seul l'éclat varie")
    func brightnessOnly() {
        #expect(MarkStroke.brightness(0) == 1)
        let all = stride(from: MarkStroke.still, through: MarkStroke.still + 5, by: 0.05).map(MarkStroke.brightness)
        #expect(all.allSatisfy { $0 >= 0.4 - 1e-9 && $0 <= 1 + 1e-9 })
        #expect(all.contains { $0 < 0.5 })
    }

    @Test("L'accélération va de 0 à 1 sans revenir en arrière")
    func easing() {
        #expect(MarkStroke.ease(0) == 0)
        #expect(MarkStroke.ease(1) == 1)
        #expect(abs(MarkStroke.ease(0.5) - 0.5) < 1e-9)
        let values = stride(from: 0.0, through: 1.0, by: 0.01).map(MarkStroke.ease)
        #expect(zip(values, values.dropFirst()).allSatisfy { $0 <= $1 })
    }
}
