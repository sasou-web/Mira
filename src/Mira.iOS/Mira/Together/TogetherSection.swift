import SwiftUI

/// « Watch together » on a title's page while this iPhone is in a group: who is in it, what it is doing, the invitation
/// to send, and Leave.
struct TogetherSection: View {
    let item: Item
    @Environment(AppModel.self) private var app
    @State private var working = false
    @State private var failure: String?

    var body: some View {
        let together = app.together
        if let group = together.group {
            VStack(alignment: .leading, spacing: 12) {
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
                if working { ProgressView().accessibilityLabel(Text("Loading")) }
                if let failure {
                    Text(verbatim: failure).font(MiraFont.footnote).foregroundStyle(Palette.danger).fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: 420, alignment: .leading)
            .padding(16)
            .background(Palette.surface, in: .rect(cornerRadius: 20, style: .continuous))
            .task(id: app.playback) { await together.refresh() }
        }
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

/// « Watch together » opened from « … » of a title's page, as tall as what it holds: what it is, Create a group, the
/// groups open on Jellyfin. In a group, it closes: the page shows the group.
struct TogetherSheet: View {
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var working = false
    @State private var failure: String?
    @State private var height: CGFloat = 300

    var body: some View {
        let together = app.together
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 14) {
                Image(systemName: "person.2.fill")
                    .font(.title3.weight(.bold))
                    .foregroundStyle(Palette.onAccent)
                    .frame(width: 46, height: 46)
                    .background(Palette.accent, in: .rect(cornerRadius: 14, style: .continuous))
                    .accessibilityHidden(true)
                Text("Watch together").font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
                Spacer(minLength: 0)
                Button { dismiss() } label: {
                    Image(systemName: "xmark").font(.body.weight(.bold)).foregroundStyle(Palette.ink).frame(width: 44, height: 44)
                }
                .buttonStyle(.plain)
                .glassEffect(.regular.interactive(), in: .circle)
                .accessibilityLabel(Text("Close"))
            }
            Text("Play a title at the same moment as friends, each on their own screen; pause and seeks go to everyone.")
                .font(MiraFont.body)
                .foregroundStyle(Palette.muted)
                .fixedSize(horizontal: false, vertical: true)
            Button {
                run { try await together.create() }
            } label: {
                Label("Create a group", systemImage: "person.2.badge.plus")
                    .font(MiraFont.headline)
                    .foregroundStyle(Palette.onAccent)
                    .frame(maxWidth: .infinity, minHeight: 36)
            }
            .buttonStyle(.glassProminent)
            .disabled(working)
            .accessibilityIdentifier("together.create")
            ForEach(together.open) { group in
                Button {
                    run { try await together.join(group.groupId) }
                } label: {
                    Label(String(localized: "Join \(SyncRules.groupText(group))"), systemImage: "person.2.fill")
                        .font(MiraFont.headline)
                        .lineLimit(2)
                        .frame(maxWidth: .infinity, minHeight: 36)
                }
                .buttonStyle(.glass)
                .disabled(working)
            }
            if working { ProgressView().frame(maxWidth: .infinity).accessibilityLabel(Text("Loading")) }
            if let failure {
                Text(verbatim: failure).font(MiraFont.footnote).foregroundStyle(Palette.danger).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.horizontal, 24)
        .padding(.top, 24)
        .padding(.bottom, 12)
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { height = $0 }
        .presentationDetents([.height(height)])
        .task { await together.refresh() }
        .onChange(of: together.group?.groupId) { _, id in if id != nil { dismiss() } }
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

/// A group open on Jellyfin, while this iPhone is in none: one line on a title's page to join it.
struct TogetherOpen: View {
    @Environment(AppModel.self) private var app
    @State private var working = false

    var body: some View {
        if let group = app.together.open.first {
            Button {
                working = true
                Task {
                    defer { working = false }
                    do { try await app.together.join(group.groupId) } catch JellyfinError.unauthorized { app.sessionEnded() } catch {
                        app.actionMessage = Messages.text(error)
                    }
                }
            } label: {
                Label(String(localized: "Join \(SyncRules.groupText(group))"), systemImage: "person.2.fill")
                    .font(MiraFont.callout)
                    .lineLimit(2)
            }
            .buttonStyle(.glass)
            .disabled(working)
            .accessibilityIdentifier("together.joinOpen")
        }
    }
}
