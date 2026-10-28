import SwiftUI

/// « Watch together » on a title's page: make a group or join one open on Jellyfin; in one, who is in it, what it is
/// doing, the invitation to send, and Leave.
struct TogetherSection: View {
    let item: Item
    @Environment(AppModel.self) private var app
    @State private var working = false
    @State private var failure: String?

    var body: some View {
        let together = app.together
        VStack(alignment: .leading, spacing: 12) {
            Text("Watch together").font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
            if let group = together.group {
                let status = SyncRules.groupStatus(group, me: app.session?.userName)
                HStack(alignment: .top, spacing: 12) {
                    Image(systemName: "person.2.fill").foregroundStyle(Palette.ink).accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(verbatim: SyncRules.groupText(group)).font(MiraFont.headline).foregroundStyle(Palette.ink)
                        Text(verbatim: status.text).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    }
                }
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("together.group")
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 10) { invite(group); leave }
                    VStack(alignment: .leading, spacing: 10) { invite(group); leave }
                }
            } else {
                Text("Play a title at the same moment as friends, each on their own screen; pause and seeks go to everyone.")
                    .font(MiraFont.footnote)
                    .foregroundStyle(Palette.muted)
                    .fixedSize(horizontal: false, vertical: true)
                Button {
                    run { try await together.create() }
                } label: {
                    Label("Create a group", systemImage: "person.2.badge.plus").font(MiraFont.callout)
                }
                .buttonStyle(.glass)
                .disabled(working)
                .accessibilityIdentifier("together.create")
                ForEach(together.open) { group in
                    Button {
                        run { try await together.join(group.groupId) }
                    } label: {
                        Label(String(localized: "Join \(SyncRules.groupText(group))"), systemImage: "person.2.fill")
                            .font(MiraFont.callout)
                            .lineLimit(2)
                    }
                    .buttonStyle(.glass)
                    .disabled(working)
                }
            }
            if working { ProgressView().accessibilityLabel(Text("Loading")) }
            if let failure {
                Text(verbatim: failure).font(MiraFont.footnote).foregroundStyle(Palette.danger).fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: 420, alignment: .leading)
        .task(id: app.playback) { await together.refresh() }
    }

    @ViewBuilder private func invite(_ group: SyncGroup) -> some View {
        if let server = app.session?.server {
            let link = SyncRules.inviteLink(server: server, groupId: group.groupId, host: SyncRules.groupHost(group.groupName) ?? app.session?.userName)
            if let url = URL(string: link) {
                ShareLink(item: url, message: Text("Open this link and sign in with your own Jellyfin account: you’ll be in my group on Mira.")) {
                    Label("Invite", systemImage: "link").font(MiraFont.callout)
                }
                .buttonStyle(.glass)
                .accessibilityIdentifier("together.invite")
            }
        }
    }

    private var leave: some View {
        Button(role: .destructive) {
            run { await app.together.leave() }
        } label: {
            Label("Leave the group", systemImage: "rectangle.portrait.and.arrow.right").font(MiraFont.callout)
        }
        .buttonStyle(.glass)
        .disabled(working)
        .accessibilityIdentifier("together.leave")
    }

    private func run(_ work: @escaping @MainActor () async throws -> Void) {
        working = true
        failure = nil
        Task {
            defer { working = false }
            do { try await work() } catch JellyfinError.unauthorized { app.sessionEnded() } catch { failure = Messages.text(error) }
        }
    }
}
