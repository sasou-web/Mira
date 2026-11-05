import SwiftUI
import UIKit

/// When Mira's opening leaves (OpeningView): the screen between iOS's launch screen and the app, the launch screen's « m »
/// written again and again while Home loads. It leaves as soon as Home is drawn, can't be, or isn't what opens (the
/// sign-in, a friend's party), and never later than its limits: nothing holds it longer than what it waits for.
enum OpeningPlan {
    /// Where Home is, as Home says it (HomeView).
    enum Home: Sendable, Hashable {
        /// Nothing to show yet: Jellyfin is asked, nothing kept from the last time.
        case waiting
        /// Home kept from the last time is there, Jellyfin asked again.
        case kept
        /// Jellyfin answered (then): the pictures at the top of Home are on their way.
        case answered(Date)
        /// Home drawn: Jellyfin's answer and the pictures at the top of the screen.
        case ready
        /// Jellyfin could not be asked: Home says why.
        case failed
    }

    /// Home kept from the last time is shown after this, rather than Jellyfin waited for on.
    static let withKept: TimeInterval = 3
    /// With nothing kept, Home's own loading after this.
    static let withNothing: TimeInterval = 12
    /// Jellyfin answered: Home shown after this, even with pictures still coming.
    static let pictures: TimeInterval = 1.2
    /// A line under the mark says what Mira waits for after this.
    static let status: TimeInterval = 2

    enum Decision: Equatable {
        case leave
        /// Looked at again then, unless something changes before.
        case wait(until: Date)
    }

    /// `noWay`: Mira looked for a way to Jellyfin and none reaches it (Way's hint: no network, Tailscale off…), so
    /// nothing is coming in time.
    static func decide(signedIn: Bool, party: Bool, home: Home, noWay: Bool, startedAt: Date, now: Date) -> Decision {
        if party || !signedIn { return .leave }
        let limit: Date
        switch home {
        case .ready, .failed:
            return .leave
        case let .answered(at):
            limit = min(at.addingTimeInterval(pictures), startedAt.addingTimeInterval(withNothing))
        case .kept:
            if noWay { return .leave }
            limit = startedAt.addingTimeInterval(withKept)
        case .waiting:
            if noWay { return .leave }
            limit = startedAt.addingTimeInterval(withNothing)
        }
        return now >= limit ? .leave : .wait(until: limit)
    }
}

/// The mark while Mira opens, from the start: whole, as on the launch screen, for a moment (an opening that ends quickly
/// never sees it move); then wound back from where the pen starts, and written again, over and over, a light at the
/// pen's tip. MiraMark.pen is the line it follows.
enum MarkStroke {
    static let still: TimeInterval = 0.6
    static let unwrite: TimeInterval = 0.7
    static let blank: TimeInterval = 0.12
    static let write: TimeInterval = 1.3
    static let whole: TimeInterval = 0.5
    static var period: TimeInterval { unwrite + blank + write + whole }
    /// Leaving, the mark lights whole within this.
    static let lighting: TimeInterval = 0.15

    struct Frame: Equatable {
        /// The part of the pen's line drawn, in fractions of its length.
        var from: Double
        var to: Double
        /// Where the pen is on the line, and its light (0: none).
        var tip: Double
        var light: Double
    }

    static func frame(_ elapsed: TimeInterval) -> Frame {
        guard elapsed > still else { return Frame(from: 0, to: 1, tip: 1, light: 0) }
        var t = (elapsed - still).truncatingRemainder(dividingBy: period)
        if t < unwrite {
            let k = t / unwrite
            return Frame(from: ease(k), to: 1, tip: ease(k), light: sin(.pi * k))
        }
        t -= unwrite
        if t < blank { return Frame(from: 1, to: 1, tip: 1, light: 0) }
        t -= blank
        if t < write {
            let k = t / write
            return Frame(from: 0, to: ease(k), tip: ease(k), light: sin(.pi * k))
        }
        return Frame(from: 0, to: 1, tip: 1, light: 0)
    }

    /// With Reduce Motion: the whole mark, its brightness slowly falling and rising again, nothing moving.
    static func brightness(_ elapsed: TimeInterval) -> Double {
        guard elapsed > still else { return 1 }
        return 0.7 + 0.3 * cos(2 * .pi * (elapsed - still) / 2.4)
    }

    /// Slow, fast, slow (a cubic ease in and out).
    static func ease(_ x: Double) -> Double {
        x < 0.5 ? 4 * x * x * x : 1 - pow(-2 * x + 2, 3) / 2
    }
}

/// Mira's opening, from the app's start: on screen, leaving, then gone for good.
@MainActor @Observable
final class Opening {
    enum Phase: Equatable { case on, leaving, gone }

    private(set) var phase = Phase.on
    /// About when iOS's launch screen came, with the same mark at the same place.
    let startedAt = Date()
    private(set) var leftAt: Date?
    private(set) var home = OpeningPlan.Home.waiting

    /// Home waits to be said ready (HomeView): only while Mira opens.
    var waitsForHome: Bool { phase == .on }

    /// Where Home is, said by Home itself.
    func report(_ home: OpeningPlan.Home) {
        guard phase == .on, home != self.home else { return }
        self.home = home
    }

    func leave() {
        guard phase == .on else { return }
        leftAt = .now
        phase = .leaving
    }

    func left() {
        phase = .gone
    }
}

/// Mira's opening: the launch screen's mark, written again and again while Home loads (MarkStroke), a line under it
/// after a while saying what Mira waits for; then Home revealed: the mark lights whole and fades as it grows, the black
/// lifts, Home settles from a little closer (RootView).
struct OpeningView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var status = false

    struct Facts: Hashable {
        var signedIn: Bool
        var party: Bool
        var home: OpeningPlan.Home
        var noWay: Bool
    }

    var body: some View {
        let opening = app.opening
        let leaving = opening.phase != .on
        ZStack {
            Palette.bg
                .opacity(leaving ? 0 : 1)
                .animation(.easeInOut(duration: 0.4).delay(0.08), value: leaving)
            OpeningMark(startedAt: opening.startedAt, leftAt: opening.leftAt, still: reduceMotion)
                .frame(width: OpeningMark.size, height: OpeningMark.size)
                .overlay(alignment: .top) {
                    statusLine
                        .opacity(status ? 1 : 0)
                        .offset(y: OpeningMark.size + 6)
                }
                .scaleEffect(leaving && !reduceMotion ? 1.18 : 1)
                .animation(.easeOut(duration: 0.26), value: leaving)
                .opacity(leaving ? 0 : 1)
                .animation(.easeIn(duration: 0.26), value: leaving)
        }
        // Centred on the whole screen, as iOS centres the launch screen's mark.
        .ignoresSafeArea()
        .allowsHitTesting(!leaving)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(verbatim: "Mira"))
        .accessibilityValue(status ? statusText : Text("Loading"))
        .accessibilityIdentifier("opening")
        .task(id: facts) { await decide(facts) }
        .task {
            do { try await Task.sleep(for: .seconds(OpeningPlan.status)) } catch { return }
            withAnimation(.easeOut(duration: 0.4)) { status = true }
        }
        .task(id: leaving) {
            guard leaving else { return }
            // VoiceOver goes on to what opened; the opening goes once its fade is over.
            UIAccessibility.post(notification: .screenChanged, argument: nil)
            try? await Task.sleep(for: .seconds(0.55))
            opening.left()
        }
    }

    private var facts: Facts {
        Facts(signedIn: app.session != nil, party: app.party.shown, home: app.opening.home, noWay: app.way == nil && app.wayHint != nil)
    }

    /// Leaves when the plan says so, looked at again at its next limit.
    private func decide(_ facts: Facts) async {
        let opening = app.opening
        while !Task.isCancelled, opening.phase == .on {
            switch OpeningPlan.decide(signedIn: facts.signedIn, party: facts.party, home: facts.home, noWay: facts.noWay,
                                      startedAt: opening.startedAt, now: .now) {
            case .leave:
                opening.leave()
                return
            case let .wait(until):
                try? await Task.sleep(for: .seconds(max(0.05, until.timeIntervalSinceNow)))
            }
        }
    }

    private var statusLine: some View {
        statusText
            .font(MiraFont.subheadline)
            .foregroundStyle(Palette.muted)
            .multilineTextAlignment(.center)
            .lineLimit(2)
            .frame(width: 300)
            .fixedSize(horizontal: false, vertical: true)
    }

    /// What Mira waits for: Built-in Tailscale to connect, a way to Jellyfin, then Home's answer.
    private var statusText: Text {
        if app.way == nil, app.tailnet.phase == .starting { return Text("Connecting to Tailscale…") }
        if app.way == nil { return Text("Connecting to Jellyfin…") }
        return Text("Loading your library…")
    }
}

/// The mark of the opening, drawn each frame: the whole « m » dim, the part written bright, the pen's light; leaving, the
/// whole mark lights within MarkStroke.lighting. With Reduce Motion, the whole mark, only its brightness changing.
struct OpeningMark: View {
    /// The side of the mark's 24 × 24 box, as on the launch screen (LaunchMark, tools/ios/launch-mark.mjs).
    static let size: CGFloat = 144
    private static let box = CGRect(x: 0, y: 0, width: size, height: size)

    let startedAt: Date
    let leftAt: Date?
    let still: Bool

    var body: some View {
        TimelineView(.animation) { timeline in
            mark(at: timeline.date)
        }
        .frame(width: Self.size, height: Self.size)
        .accessibilityHidden(true)
    }

    @ViewBuilder
    private func mark(at date: Date) -> some View {
        let elapsed = date.timeIntervalSince(startedAt)
        // Leaving: 0 to 1 as the whole mark lights, from where it was when the opening began to leave.
        let left = leftAt.map { $0.timeIntervalSince(startedAt) }
        let lit = leftAt.map { min(1, max(0, date.timeIntervalSince($0) / MarkStroke.lighting)) } ?? 0
        if still {
            MiraMark().fill(Palette.accent)
                .opacity(MarkStroke.brightness(min(elapsed, left ?? elapsed)) * (1 - lit) + lit)
        } else {
            let frame = MarkStroke.frame(min(elapsed, left ?? elapsed))
            let line = MiraMark.lineWidth(in: Self.box)
            let glow = line * 1.75
            ZStack(alignment: .topLeading) {
                MiraMark().fill(Palette.accent.opacity(0.1))
                MiraPen()
                    .trim(from: frame.from, to: frame.to)
                    .stroke(Palette.accent, style: StrokeStyle(lineWidth: line, lineCap: .round, lineJoin: .round))
                    // An empty part would still leave a dot of its round caps.
                    .opacity(frame.to - frame.from > 0.001 ? 1 : 0)
                MiraMark().fill(Palette.accent).opacity(lit)
                if let tip = MiraMark.pen(in: Self.box).trimmedPath(from: 0, to: max(0.0001, frame.tip)).currentPoint {
                    Circle()
                        .fill(RadialGradient(colors: [.white.opacity(0.45), .white.opacity(0)], center: .center,
                                             startRadius: 0, endRadius: glow))
                        .frame(width: glow * 2, height: glow * 2)
                        .position(tip)
                        .opacity(frame.light * (1 - lit))
                }
            }
            .frame(width: Self.size, height: Self.size)
        }
    }
}
