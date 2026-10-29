import AVFoundation
import SwiftUI

/// The player reduced (a sweep down, or its chevron), as YouTube does: the title goes on in a bar over the tabs (the
/// tab view's bottom accessory, in Liquid Glass), its picture live; a tap brings the full player back, × stops it.
struct MiniPlayer: View {
    let session: PlayerSession
    @Environment(\.tabViewBottomAccessoryPlacement) private var placement

    private var model: PlayerModel { session.model }

    var body: some View {
        HStack(spacing: 10) {
            MiniPicture(player: session.player)
                .frame(width: placement == .inline ? 44 : 64, height: placement == .inline ? 25 : 36)
                .clipShape(.rect(cornerRadius: 6))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 0) {
                Text(verbatim: model.title)
                    .font(MiraFont.subheadline)
                    .lineLimit(1)
                if placement != .inline, let subtitle = model.subtitle {
                    Text(verbatim: subtitle)
                        .font(MiraFont.footnote)
                        .foregroundStyle(Palette.muted)
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Button {
                model.actions?.togglePlay()
            } label: {
                Image(systemName: model.playing ? "pause.fill" : "play.fill")
                    .font(.system(size: 18, weight: .semibold))
                    .frame(width: 36, height: 36)
                    .contentTransition(.symbolEffect(.replace))
            }
            .buttonStyle(.plain)
            .accessibilityLabel(model.playing ? Text("Pause") : Text("Play"))
            .accessibilityIdentifier("mini.play")
            Button {
                model.actions?.close()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 15, weight: .semibold))
                    .frame(width: 32, height: 36)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("Close the player"))
            .accessibilityIdentifier("mini.close")
        }
        .padding(.leading, 6)
        .padding(.trailing, 8)
        .overlay(alignment: .bottom) {
            // How far the title is, a line under it.
            GeometryReader { box in
                Capsule()
                    .fill(Palette.ink.opacity(0.85))
                    .frame(width: box.size.width * (model.duration > 0 ? min(1, model.time / model.duration) : 0), height: 2)
            }
            .frame(height: 2)
            .padding(.horizontal, 14)
            .accessibilityHidden(true)
        }
        .contentShape(.rect)
        .onTapGesture { session.expand() }
        .accessibilityElement(children: .contain)
        .accessibilityAction(named: Text("Open the player")) { session.expand() }
        .accessibilityIdentifier("mini")
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

extension View {
    /// The reduced player over the tabs, while there is one (iOS 26.1: the accessory shown and hidden).
    @ViewBuilder
    func miniPlayer(_ session: PlayerSession?) -> some View {
        if #available(iOS 26.1, *) {
            tabViewBottomAccessory(isEnabled: session != nil) {
                if let session { MiniPlayer(session: session) }
            }
        } else {
            self
        }
    }
}
