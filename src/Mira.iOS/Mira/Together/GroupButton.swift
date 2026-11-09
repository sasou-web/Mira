import SwiftUI

/// While this iPhone is in a group of « Watch together », a button over the tabs, on every page (MiraTabBar): the
/// group's icon, « Group » and how many are in it, as the button of the rail of Mira for Windows and Mac. Touched, the
/// group's sheet (GroupSheet). A pause or a wait of the group shows as its icon.
struct GroupButton: View {
    let group: SyncGroup
    let open: () -> Void
    @Environment(AppModel.self) private var app
    @ScaledMetric(relativeTo: .footnote) private var height: CGFloat = 36

    var body: some View {
        let status = SyncRules.groupStatus(group, me: app.session?.userName)
        Button(action: open) {
            HStack(spacing: 7) {
                Image(systemName: symbol(status.alert))
                    .font(.footnote.weight(.bold))
                    .foregroundStyle(Palette.success)
                    .contentTransition(.symbolEffect(.replace))
                Text("Group").font(MiraFont.extraBold(14, .footnote)).lineLimit(1).fixedSize()
                Text(verbatim: SyncRules.countText(group))
                    .font(MiraFont.extraBold(12, .caption))
                    .monospacedDigit()
                    .foregroundStyle(Palette.onAccent)
                    .padding(.horizontal, 6)
                    .frame(minWidth: 20, minHeight: 20)
                    .background(Palette.success, in: .capsule)
                    .contentTransition(.numericText())
            }
            .foregroundStyle(Palette.ink)
            .padding(.leading, 12)
            .padding(.trailing, 8)
            .frame(height: height)
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular.interactive(), in: .capsule)
        .accessibilityLabel(Text(verbatim: SyncRules.indicatorText(group, me: app.session?.userName)))
        .accessibilityIdentifier("tab.group")
        .accessibilityShowsLargeContentViewer {
            Label { Text(verbatim: SyncRules.groupText(group)) } icon: { Image(systemName: "person.2.fill") }
        }
    }

    private func symbol(_ alert: Bool) -> String {
        guard alert else { return "person.2.fill" }
        return group.state == "Paused" ? "pause.fill" : "clock.fill"
    }
}

/// The group this iPhone is in, from the button over the tabs: who is in it and what it is doing, its title (back to
/// the player reduced over the tabs, or its page), the invitation to send, and Leave. It closes when the group ends.
struct GroupSheet: View {
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var path: [TitleRoute] = []
    @State private var detent = PresentationDetent.medium
    /// The group's title, read from Jellyfin for its name and its page.
    @State private var title: Item?
    @State private var working = false

    var body: some View {
        NavigationStack(path: $path) {
            ScrollView {
                if let group = app.together.group { content(group) }
            }
            .scrollBounceBehavior(.basedOnSize)
            .background(Palette.bg)
            .toolbar(.hidden, for: .navigationBar)
            .navigationDestination(for: TitleRoute.self) { TitleView(route: $0) }
        }
        .presentationDetents([.medium, .large], selection: $detent)
        .presentationBackground(Palette.bg)
        .onChange(of: path.isEmpty) { _, empty in if !empty { detent = .large } }
        .onChange(of: app.together.group == nil) { _, gone in if gone { dismiss() } }
        .task(id: app.session?.userId) { await app.together.loadParty() }
        .task(id: app.together.queue?.itemId) {
            guard let id = app.together.queue?.itemId, !id.isEmpty, let client = app.client else {
                title = nil
                return
            }
            if title?.id != id { title = try? await client.item(id) }
        }
    }

    private func content(_ group: SyncGroup) -> some View {
        let status = SyncRules.groupStatus(group, me: app.session?.userName)
        let names = group.participants ?? []
        return VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 14) {
                Image(systemName: "person.2.fill")
                    .font(.title3.weight(.bold))
                    .foregroundStyle(Palette.onAccent)
                    .frame(width: 46, height: 46)
                    .background(Palette.success, in: .rect(cornerRadius: 14, style: .continuous))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text(verbatim: group.groupName == nil ? String(localized: "Group") : SyncRules.groupLabel(group.groupName))
                        .font(MiraFont.section)
                        .foregroundStyle(Palette.ink)
                        .accessibilityAddTraits(.isHeader)
                    Text(verbatim: names.isEmpty ? SyncRules.people(1) : SyncRules.namesText(names))
                        .font(MiraFont.footnote)
                        .foregroundStyle(Palette.muted)
                }
                Spacer(minLength: 0)
                Button { dismiss() } label: {
                    Image(systemName: "xmark").font(.body.weight(.bold)).foregroundStyle(Palette.ink).frame(width: 44, height: 44)
                }
                .buttonStyle(.plain)
                .glassEffect(.regular.interactive(), in: .circle)
                .accessibilityLabel(Text("Close"))
                .accessibilityIdentifier("group.close")
            }
            .accessibilityElement(children: .contain)
            if status.alert {
                Label { Text(verbatim: status.text) } icon: { Image(systemName: group.state == "Paused" ? "pause.fill" : "clock.fill") }
                    .font(MiraFont.callout)
                    .foregroundStyle(Palette.ink)
                    .accessibilityIdentifier("group.status")
            }
            VStack(spacing: 10) {
                if let reduced = app.minimizedPlayer, let id = app.together.queue?.itemId, reduced.groupItemId == id {
                    Button {
                        dismiss()
                        // Once the sheet is down: the player comes up over the app, not over the sheet.
                        Task {
                            try? await Task.sleep(for: .milliseconds(400))
                            reduced.expand()
                        }
                    } label: {
                        wide("Back to the player", "play.fill")
                    }
                    .buttonStyle(.glassProminent)
                    .tint(Palette.accent)
                    .foregroundStyle(Palette.onAccent)
                    .accessibilityIdentifier("group.player")
                } else if let title {
                    NavigationLink(value: TitleRoute(item: title, source: "group-\(title.id)")) {
                        wide("View “\(SyncRules.titleName(title))”", "film.fill")
                    }
                    .buttonStyle(.glass)
                    .accessibilityIdentifier("group.title")
                }
                if let invitation = app.together.invitation(group) {
                    ShareLink(item: invitation.url, message: invitation.party ? Text("Open this link to watch with me on Mira, with no account to make.")
                                                                            : Text("Open this link where you have Mira, signed in to my Jellyfin with your own account: you’ll be in my group. Or open Mira: the group is on your Home.")) {
                        wide("Invite", "link")
                    }
                    .buttonStyle(.glass)
                    .accessibilityIdentifier("group.invite")
                }
                Button(role: .destructive) {
                    working = true
                    Task {
                        defer { working = false }
                        await app.together.leave()
                    }
                } label: {
                    wide("Leave the group", "rectangle.portrait.and.arrow.right")
                }
                .buttonStyle(.glass)
                .disabled(working)
                .accessibilityIdentifier("group.leave")
            }
        }
        .padding(.horizontal, 24)
        .padding(.top, 24)
        .padding(.bottom, 12)
    }

    private func wide(_ text: LocalizedStringKey, _ symbol: String) -> some View {
        Label(text, systemImage: symbol)
            .font(MiraFont.headline)
            .lineLimit(2)
            .frame(maxWidth: .infinity, minHeight: 36)
    }
}
