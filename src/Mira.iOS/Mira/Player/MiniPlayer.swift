import AVFoundation
import SwiftUI

/// The player reduced (a sweep down, or its chevron), as YouTube does: the title goes on in a bar over the tabs
/// (MiraTabBar, which gives it its glass), its picture live, how far it is in a ring around play; a tap brings the full
/// player back, × stops it. Beside a folded tab bar (inline), only the picture, the title and play.
struct MiniPlayer: View {
    let session: PlayerSession
    let inline: Bool

    private var model: PlayerModel { session.model }

    var body: some View {
        HStack(spacing: 12) {
            let height: CGFloat = inline ? 24 : 34
            MiniPicture(player: session.player)
                .frame(width: height * 16 / 9, height: height)
                .background(.black)
                .clipShape(.rect(cornerRadius: inline ? 5 : 7))
                .overlay { RoundedRectangle(cornerRadius: inline ? 5 : 7).strokeBorder(.white.opacity(0.14), lineWidth: 0.5) }
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text(verbatim: model.title)
                    .font(MiraFont.subheadline)
                    .lineLimit(1)
                if !inline, let detail {
                    Text(verbatim: detail)
                        .font(MiraFont.footnote)
                        .foregroundStyle(Palette.muted)
                        .monospacedDigit()
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Button {
                model.actions?.togglePlay()
            } label: {
                ZStack {
                    Circle().stroke(.white.opacity(0.18), lineWidth: 2.5)
                    Circle()
                        .trim(from: 0, to: progress)
                        .stroke(Palette.ink, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                    Image(systemName: model.playing ? "pause.fill" : "play.fill")
                        .font(.system(size: 12, weight: .bold))
                        .offset(x: model.playing ? 0 : 1)
                        .contentTransition(.symbolEffect(.replace))
                }
                .frame(width: 30, height: 30)
                .frame(width: 44, height: 44)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(model.playing ? Text("Pause") : Text("Play"))
            .accessibilityIdentifier("mini.play")
            if !inline {
                Button {
                    model.actions?.close()
                } label: {
                    Image(systemName: "xmark")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(Palette.muted)
                        .frame(width: 36, height: 44)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("Close the player"))
                .accessibilityIdentifier("mini.close")
            }
        }
        .padding(.leading, inline ? 8 : 10)
        .padding(.trailing, 4)
        .foregroundStyle(Palette.ink)
        .contentShape(.rect)
        .onTapGesture { session.expand() }
        .accessibilityElement(children: .contain)
        .accessibilityAction(named: Text("Open the player")) { session.expand() }
        .accessibilityIdentifier("mini")
    }

    private var progress: Double { model.duration > 0 ? min(1, max(0, model.time / model.duration)) : 0 }

    /// « 2010 · 26 min left », « S1 · E3 · 12 min left »: what it is, and how long it still has.
    private var detail: String? {
        var parts = [model.subtitle].compactMap { $0 }
        if model.duration > 0, model.time < model.duration {
            parts.append(String(localized: "\(ItemText.duration(seconds: Int64(model.duration - model.time))) left"))
        }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

/// The title's picture, live, in the bar: a second layer on the same player.
private struct MiniPicture: UIViewRepresentable {
    let player: AVPlayer

    func makeUIView(context: Context) -> PlayerVideoView {
        let view = PlayerVideoView(player: player)
        view.playerLayer.videoGravity = .resizeAspectFill
        return view
    }

    func updateUIView(_ uiView: PlayerVideoView, context: Context) {
        if uiView.playerLayer.player !== player { uiView.playerLayer.player = player }
    }

    static func dismantleUIView(_ uiView: PlayerVideoView, coordinator: ()) {
        uiView.playerLayer.player = nil
    }
}
