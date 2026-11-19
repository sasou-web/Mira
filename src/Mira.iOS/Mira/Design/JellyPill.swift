import Foundation

/// The pill of a row of choices going from one to another as a jelly (« La gelée », ogsas 2026-11-18): each edge is a
/// damped spring, the edge in front stiffer than the one behind, so the pill stretches along the way, the back catching
/// up with a soft rebound (a few points past its place, then still); squashed a little while stretched. Springs solved
/// exactly, at any time: the same curve at 60 Hz or 120 Hz, and a new choice mid-way starts from where the pill is and
/// how fast each edge moves. JellyPill of Core (Windows and Mac), the same springs and limits, ported with its test.
/// Times in seconds, places in points.
struct JellyPill: Sendable {
    /// The edge in front: quick, barely past its place.
    static let leadHz = 3.8, leadDamping = 0.82
    /// The edge behind: slower, a little past its place before it settles (the rebound).
    static let trailHz = 2.7, trailDamping = 0.72
    /// The pill never longer than its resting width and this share of it, nor shorter than this share of it.
    static let maxStretch = 0.6, minSquash = 0.8
    /// How the height answers the width: (rest ÷ width) to this power, at most `maxSwell` taller.
    static let squash = 0.3, maxSwell = 1.06
    /// Close enough and slow enough to be still, in points and points per second.
    private static let near = 0.2, slow = 4.0

    private var left = Edge.still(0), right = Edge.still(0)
    /// The resting widths of where the pill comes from and where it goes.
    private var restFrom = 0.0, restTo = 0.0

    var targetLeft: Double { left.target }
    var targetRight: Double { right.target }

    /// The pill put at once (no motion, first layout, a new width).
    mutating func put(_ left: Double, _ right: Double) {
        self.left = .still(left)
        self.right = .still(right)
        restFrom = max(0, right - left)
        restTo = restFrom
    }

    /// The pill sent to [left, right] at `now`, from where it is and as fast as it moves.
    mutating func go(_ left: Double, _ right: Double, now: Double) {
        let shown = at(now)
        let lv = self.left.at(now).speed, rv = self.right.at(now).speed
        // Going right: the right edge leads. Going left: the left one.
        let rightwards = left + right > shown.left + shown.right
        restFrom = restTo
        restTo = max(0, right - left)
        self.left = Edge(target: left, start: shown.left, speed: lv, since: now,
                         hz: rightwards ? Self.trailHz : Self.leadHz, damping: rightwards ? Self.trailDamping : Self.leadDamping)
        self.right = Edge(target: right, start: shown.right, speed: rv, since: now,
                          hz: rightwards ? Self.leadHz : Self.trailHz, damping: rightwards ? Self.leadDamping : Self.trailDamping)
    }

    /// Whether the pill still moves at `now`.
    func moving(_ now: Double) -> Bool { !left.settled(now) || !right.settled(now) }

    /// The pill at `now`: its edges, and its height as a share of its resting height.
    func at(_ now: Double) -> (left: Double, right: Double, height: Double) {
        guard moving(now) else { return (left.target, right.target, 1) }
        var l = left.at(now).at, r = right.at(now).at
        let longest = max(restFrom, restTo), shortest = min(restFrom, restTo)
        // Held within its stretch: the edge behind follows the one in front, as a band at its length would.
        let going = right.target + left.target - (left.start + right.start)
        var width = r - l
        let most = longest * (1 + Self.maxStretch), least = shortest * Self.minSquash
        if width > most {
            if going >= 0 { l = r - most } else { r = l + most }
            width = most
        } else if width < least, least > 0 {
            let middle = (l + r) / 2
            l = middle - least / 2
            r = middle + least / 2
            width = least
        }
        var height = 1.0
        if width > longest, width > 0 { height = pow(longest / width, Self.squash) }
        else if width < shortest, width > 0 { height = min(Self.maxSwell, pow(shortest / width, Self.squash)) }
        return (l, r, height)
    }

    /// One edge: a damped spring towards `target`, solved exactly from (`start`, its speed then) at `since`.
    private struct Edge: Sendable {
        let target: Double
        let start: Double
        let speed: Double
        let since: Double
        let hz: Double
        let damping: Double

        static func still(_ at: Double) -> Edge { Edge(target: at, start: at, speed: 0, since: 0, hz: JellyPill.leadHz, damping: JellyPill.leadDamping) }

        func at(_ now: Double) -> (at: Double, speed: Double) {
            let y0 = start - target
            if y0 == 0 && speed == 0 { return (target, 0) }
            let t = max(0, now - since)
            let w = 2 * Double.pi * hz, a = damping * w, wd = w * (1 - damping * damping).squareRoot()
            let b = (speed + a * y0) / wd
            let e = exp(-a * t), c = cos(wd * t), s = sin(wd * t)
            return (target + e * (y0 * c + b * s), e * ((wd * b - a * y0) * c + (-a * b - wd * y0) * s))
        }

        func settled(_ now: Double) -> Bool {
            let now = self.at(now)
            return abs(now.at - target) < JellyPill.near && abs(now.speed) < JellyPill.slow
        }
    }
}
