import SwiftUI

/// A friend's invite over the app (maquette A « Le lien »), on black like « Le foyer »: « You're invited to watch », the
/// private line, what the friend sees of this iPhone, « Start watching »; then « Almost there » while the friend lets
/// it in; or why the invite is over.
struct FriendView: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        let friend = app.friend
        ZStack {
            Palette.bg.ignoresSafeArea()
            ScrollView {
                VStack(spacing: 22) {
                    Spacer(minLength: 56)
                    MiraIcon(size: 64)
                    Text(verbatim: heading(friend.stage))
                        .font(MiraFont.extraBold(28, .largeTitle))
                        .foregroundStyle(Palette.ink)
                        .multilineTextAlignment(.center)
                        .accessibilityAddTraits(.isHeader)
                        .accessibilityIdentifier("friend.heading")
                    Text(verbatim: line(friend.stage))
                        .font(MiraFont.callout)
                        .foregroundStyle(Palette.muted)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                    if friend.stage == .waiting {
                        ProgressView().tint(Palette.muted).accessibilityIdentifier("friend.waiting")
                    }
                    if friend.stage == .invited {
                        // Said before anything: what the friend sees of this iPhone.
                        HStack(alignment: .top, spacing: 12) {
                            Image(systemName: "eye")
                                .font(.body.weight(.semibold))
                                .foregroundStyle(Palette.muted)
                                .frame(width: 22)
                                .accessibilityHidden(true)
                            Text(verbatim: FriendText.visible)
                                .font(MiraFont.footnote)
                                .foregroundStyle(Palette.muted)
                                .fixedSize(horizontal: false, vertical: true)
                            Spacer(minLength: 0)
                        }
                        .padding(14)
                        .background(Palette.surface, in: .rect(cornerRadius: 16, style: .continuous))
                        .overlay { RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Palette.line) }
                        .accessibilityElement(children: .combine)
                        .accessibilityIdentifier("friend.visible")
                    }
                    if let said = friend.said {
                        Label {
                            Text(verbatim: said)
                        } icon: {
                            Image(systemName: "exclamationmark.circle.fill")
                        }
                        .font(MiraFont.footnote)
                        .foregroundStyle(Palette.danger)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("friend.said")
                    }
                    if let busy = friend.busy {
                        Text(verbatim: busy).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    }
                    VStack(spacing: 10) {
                        if friend.stage == .invited {
                            Button {
                                Task { await friend.begin() }
                            } label: {
                                ButtonLabel(busy: friend.busy != nil, title: "Start watching")
                            }
                            .buttonStyle(.glassProminent)
                            .disabled(friend.busy != nil)
                            .accessibilityIdentifier("friend.start")
                        }
                        Button { friend.close() } label: {
                            Text(verbatim: friend.stage == .invited ? FriendText.notNow : String(localized: "Close"))
                                .font(MiraFont.callout)
                                .frame(maxWidth: .infinity, minHeight: 32)
                        }
                        .buttonStyle(.glass)
                        .disabled(friend.busy != nil)
                        .accessibilityIdentifier("friend.close")
                        if friend.stage != .waiting, app.session == nil {
                            Button { friend.close() } label: {
                                Text(verbatim: FriendText.haveAddress).font(MiraFont.footnote).foregroundStyle(Palette.muted).frame(minHeight: 32)
                            }
                            .buttonStyle(.plain)
                            .disabled(friend.busy != nil)
                            .accessibilityIdentifier("friend.address")
                        }
                    }
                    .frame(maxWidth: 320)
                    Spacer(minLength: 24)
                }
                .frame(maxWidth: 440)
                .padding(.horizontal, 20)
                .frame(maxWidth: .infinity)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .animation(.easeInOut(duration: 0.2), value: friend.stage)
    }

    private func heading(_ stage: FriendInvite.Stage) -> String {
        switch stage {
        case .waiting: FriendText.almostThere
        case let .ended(heading, _): heading
        default: FriendText.invited
        }
    }

    private func line(_ stage: FriendInvite.Stage) -> String {
        switch stage {
        case .waiting: FriendText.waitingForHost
        case let .ended(_, line): line
        default: FriendText.privateLine
        }
    }
}
