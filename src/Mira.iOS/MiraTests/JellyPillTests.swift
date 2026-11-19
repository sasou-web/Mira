import Foundation
import Testing
@testable import Mira

/// The seasons' pill (« La gelée »): the springs of JellyPill of Core, with the cases of its test (PlayerTests.cs).
@Suite("Pastille des saisons : la gelée de Windows et du Mac")
struct JellyPillTests {
    @Test("Bord avant en tête, étirée en route, léger rebond, posée en moins d'une demi-seconde")
    func glide() {
        // From the first part (0-45) to the fourth (135-180).
        var pill = JellyPill()
        pill.put(0, 45)
        pill.go(135, 180, now: 10)
        var stretched = 0.0, past = 0.0, shortest = 45.0, leadAhead = false
        var still: Double?
        for i in 1...240 {
            let now = 10 + Double(i) / 240
            let shape = pill.at(now)
            #expect(shape.right > shape.left && shape.height > 0.7 && shape.height <= JellyPill.maxSwell, "A pill at \(i)")
            #expect(shape.right - shape.left <= 45 * (1 + JellyPill.maxStretch) + 1e-9 && shape.right - shape.left >= 45 * JellyPill.minSquash - 1e-9)
            stretched = max(stretched, shape.right - shape.left)
            shortest = min(shortest, shape.right - shape.left)
            past = max(past, shape.left - 135)
            if i == 12 { leadAhead = shape.right - 45 > (shape.left - 0) * 1.5 }
            if still == nil, !pill.moving(now) { still = now - 10 }
        }
        #expect(leadAhead, "The edge in front leads")
        #expect(stretched > 55 && stretched < 80, "Stretched along the way: \(stretched)")
        #expect(past > 1 && past < 8 && shortest < 45, "A soft rebound: \(past) past, \(shortest) at its shortest")
        let settled = still ?? 9
        #expect(settled > 0.3 && settled < 0.55, "Still within half a second: \(settled)")
        let landed = pill.at(11)
        #expect(landed.left == 135 && landed.right == 180 && landed.height == 1, "Landed exactly, at its height")
    }

    @Test("La même courbe à toute cadence, et un nouveau choix en route repart d'où elle est")
    func turn() {
        var a = JellyPill(), b = JellyPill()
        a.put(0, 45)
        a.go(135, 180, now: 0)
        b.put(0, 45)
        b.go(135, 180, now: 0)
        for i in 1...4 { _ = b.at(Double(i) / 240) }
        let at60 = a.at(1 / 60.0), at240 = b.at(4 / 240.0)
        #expect(at60.left == at240.left && at60.right == at240.right && at60.height == at240.height, "60 Hz and 240 Hz alike")
        var c = JellyPill()
        c.put(0, 45)
        c.go(270, 315, now: 0)
        let before = c.at(0.08)
        c.go(45, 90, now: 0.08)
        let after = c.at(0.08)
        #expect(abs(before.left - after.left) < 1e-9 && abs(before.right - after.right) < 1e-9, "Turned mid-way from where it was")
        #expect(c.at(0.0833).right > after.right, "Still carried on a little by its speed")
        let landed = c.at(2)
        #expect(!c.moving(2) && landed.left == 45 && landed.right == 90 && landed.height == 1, "Then lands on the new part")
    }
}
