import SwiftUI

/// « Ta party » of another Jellyfin, as on Windows and Mac: the first name asked, then the host's group waited for or
/// joined, its members, and « Leave the party ». It takes the screen while this iPhone is that party's guest (Party);
/// the player opens above it when the group plays.
struct PartyView: View {
    @Environment(AppModel.self) private var app
    @FocusState private var typing: Bool

    private var party: Party { app.party }
    private var named: String { party.host.flatMap { $0.isEmpty ? nil : $0 } ?? String(localized: "whoever invited you") }
    private var inGroup: Bool { party.guest != nil && app.together.group != nil }

    var body: some View {
        @Bindable var party = app.party
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                MiraIcon(size: 64)
                    .frame(maxWidth: .infinity)
                    .padding(.bottom, 12)
                    .accessibilityHidden(true)
                Text(heading)
                    .font(MiraFont.title)
                    .foregroundStyle(Palette.ink)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityAddTraits(.isHeader)
                    .accessibilityIdentifier("party.heading")
                Text(lead)
                    .font(MiraFont.callout)
                    .foregroundStyle(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true)
                if party.guest == nil, party.stage == .name || party.stage == .opening {
                    FieldLabel("Your first name")
                    TextField("Your first name", text: $party.name)
                        .textContentType(.givenName)
                        .submitLabel(.join)
                        .focused($typing)
                        .disabled(party.stage == .opening)
                        .onSubmit { Task { await party.join() } }
                        .modifier(FieldStyle(focused: typing))
                        .accessibilityIdentifier("party.name")
                }
                if inGroup, let group = app.together.group {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(verbatim: SyncRules.groupText(group))
                            .font(MiraFont.headline)
                            .foregroundStyle(Palette.ink)
                        Text(verbatim: SyncRules.namesText(group.participants ?? []))
                            .font(MiraFont.footnote)
                            .foregroundStyle(Palette.muted)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(14)
                    .background(Palette.raised, in: .rect(cornerRadius: 12, style: .continuous))
                    .overlay { RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Palette.line, lineWidth: 1) }
                    .accessibilityElement(children: .combine)
                    .accessibilityIdentifier("party.members")
                }
                if let error = party.error {
                    Label {
                        Text(verbatim: error)
                    } icon: {
                        Image(systemName: "exclamationmark.circle.fill")
                    }
                    .font(MiraFont.footnote)
                    .foregroundStyle(Palette.danger)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("party.error")
                }
                primary
                Button {
                    Task { await party.leave() }
                } label: {
                    Text(secondary)
                        .font(MiraFont.callout)
                        .frame(maxWidth: .infinity, minHeight: 32)
                }
                .buttonStyle(.glass)
                .accessibilityIdentifier("party.leave")
            }
            .frame(maxWidth: 440)
            .padding(.horizontal, 20)
            .padding(.top, 72)
            .padding(.bottom, 32)
            .frame(maxWidth: .infinity)
        }
        .scrollDismissesKeyboard(.interactively)
        .background {
            ZStack {
                Palette.bg
                RadialGradient(colors: [Palette.raised2.opacity(0.7), .clear], center: .top, startRadius: 0, endRadius: 460)
            }
            .ignoresSafeArea()
        }
        .animation(.snappy, value: inGroup)
        .animation(.snappy, value: party.stage)
        .onAppear { if party.guest == nil, party.stage == .name { typing = party.name.isEmpty } }
    }

    private var heading: String {
        if party.stage == .ended { return String(localized: "This link’s party is over") }
        if inGroup { return String(localized: "You’re in \(named)’s party") }
        if party.guest != nil { return String(localized: "\(named) hasn’t started movie night yet") }
        if let host = party.host, !host.isEmpty { return String(localized: "Join \(host)’s party") }
        return String(localized: "Join a party")
    }

    private var lead: String {
        if party.stage == .ended { return String(localized: "This link no longer works: ask whoever invited you for a new one.") }
        if inGroup { return String(localized: "Keep Mira open: the title starts here as soon as \(named) starts it, at the same moment for everyone.") }
        if party.guest != nil { return String(localized: "Keep Mira open: you’ll join the group as soon as it opens.") }
        return String(localized: "Your first name, so the group knows who’s here. No account to create.")
    }

    private var secondary: String {
        if party.stage == .ended { return String(localized: "Close") }
        if party.guest != nil { return String(localized: "Leave the party") }
        return String(localized: "Cancel")
    }

    /// « Join the party » while the first name is asked; « Back to the group’s title » when the group plays on without
    /// this iPhone's player (reduced, or closed).
    @ViewBuilder private var primary: some View {
        if party.guest == nil, party.stage == .name || party.stage == .opening {
            Button {
                typing = false
                Task { await party.join() }
            } label: {
                ButtonLabel(busy: party.stage == .opening, title: "Join the party")
            }
            .buttonStyle(.glassProminent)
            .disabled(party.stage == .opening)
            .accessibilityIdentifier("party.join")
            .padding(.top, 6)
        } else if inGroup, app.minimizedPlayer != nil || (app.player == nil && app.together.queue != nil) {
            Button {
                if let reduced = app.minimizedPlayer {
                    reduced.expand()
                } else if let queue = app.together.queue {
                    app.playForGroup(queue.itemId, startTicks: queue.startTicks)
                }
            } label: {
                ButtonLabel(busy: false, title: "Back to the group’s title")
            }
            .buttonStyle(.glassProminent)
            .accessibilityIdentifier("party.back")
            .padding(.top, 6)
        }
    }
}
