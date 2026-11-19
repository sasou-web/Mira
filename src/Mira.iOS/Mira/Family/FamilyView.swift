import SwiftUI

/// « Le foyer » over the app (maquette A « Le hall »): « Who's watching? » on black, the profiles in tiles (a padlock for
/// a PIN, a ring for the one open on this iPhone), a profile's PIN, or the card that joins a home with its password.
struct FamilyView: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        let family = app.family
        ZStack {
            Palette.bg.ignoresSafeArea()
            switch family.stage {
            case .hall: HallPage().transition(.opacity)
            case let .pin(profile): PinPage(profile: profile).id(profile.id).transition(.opacity)
            case let .join(card): JoinPage(card: card).transition(.opacity)
            case .none: EmptyView()
            }
        }
        .animation(.easeInOut(duration: 0.2), value: family.stage)
    }
}

/// A profile's face: its Jellyfin picture when it has one (read without a session), else its initial on a tile.
struct ProfileFace: View {
    @Environment(AppModel.self) private var app
    let profile: HomeProfile
    var size: CGFloat

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
        ZStack {
            shape.fill(Palette.raised2)
            Text(verbatim: String(profile.name.prefix(1)).uppercased())
                .font(MiraFont.extraBold(size * 0.44, .largeTitle))
                .foregroundStyle(Palette.ink)
            if let url = app.family.picture(profile) {
                RemoteImage(url: url, width: size).clipShape(shape)
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

/// The small line above a page of the home: Mira's mark and « Alex's home ».
private struct HomeTag: View {
    let name: String
    var body: some View {
        HStack(spacing: 8) {
            MiraIcon(size: 22)
            Text(verbatim: HomeText.homeOf(name)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
        }
        .accessibilityElement(children: .combine)
    }
}

/// What a page says went wrong.
private struct FamilySaid: View {
    let text: String
    var body: some View {
        Label {
            Text(verbatim: text)
        } icon: {
            Image(systemName: "exclamationmark.circle.fill")
        }
        .font(MiraFont.footnote)
        .foregroundStyle(Palette.danger)
        .multilineTextAlignment(.leading)
        .fixedSize(horizontal: false, vertical: true)
        .accessibilityIdentifier("family.said")
    }
}

// MARK: « Who's watching? »

private struct HallPage: View {
    @Environment(AppModel.self) private var app
    @State private var leaving = false

    var body: some View {
        let family = app.family
        let home = family.membership
        ScrollView {
            VStack(spacing: 28) {
                Spacer(minLength: 40)
                if let home { HomeTag(name: home.name) }
                Text(verbatim: HomeText.whoIsWatching)
                    .font(MiraFont.extraBold(30, .largeTitle))
                    .foregroundStyle(Palette.ink)
                    .multilineTextAlignment(.center)
                    .accessibilityAddTraits(.isHeader)
                    .accessibilityIdentifier("family.hall")
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 120, maximum: 150), spacing: 24)], spacing: 26) {
                    ForEach(home?.profiles ?? []) { profile in tile(profile, current: profile.id == home?.current && family.isHomeSession(app.session)) }
                }
                .frame(maxWidth: 360)
                if let said = family.said {
                    VStack(spacing: 12) {
                        FamilySaid(text: said)
                        if family.retry {
                            Button {
                                Task { await family.refresh() }
                            } label: {
                                Label("Try again", systemImage: "arrow.clockwise").font(MiraFont.callout)
                            }
                            .buttonStyle(.glass)
                            .accessibilityIdentifier("family.retry")
                        }
                    }
                    .frame(maxWidth: 360)
                }
                if let busy = family.busy {
                    HStack(spacing: 10) {
                        ProgressView().tint(Palette.muted)
                        Text(verbatim: busy).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    }
                }
                VStack(spacing: 6) {
                    if app.session != nil, family.isHomeSession(app.session) {
                        Button { family.closeHall() } label: {
                            Text("Back to Mira").font(MiraFont.callout).frame(minHeight: 32)
                        }
                        .buttonStyle(.glass)
                        .accessibilityIdentifier("family.close")
                    }
                    Button { family.ownAccount() } label: {
                        Text(verbatim: HomeText.ownAccount).font(MiraFont.footnote).foregroundStyle(Palette.muted).frame(minHeight: 32)
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("family.own")
                    Button { leaving = true } label: {
                        Text(verbatim: HomeText.leave).font(MiraFont.footnote).foregroundStyle(Palette.muted).frame(minHeight: 32)
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("family.leave")
                }
                .padding(.top, 8)
                Spacer(minLength: 24)
            }
            .padding(.horizontal, 20)
            .frame(maxWidth: .infinity)
        }
        .scrollBounceBehavior(.basedOnSize)
        .disabled(family.busy != nil)
        .confirmationDialog(String(localized: "Leave this home?"), isPresented: $leaving, titleVisibility: .visible) {
            Button(HomeText.leave, role: .destructive) { Task { await family.leave() } }
                .accessibilityIdentifier("family.leave.confirm")
        } message: {
            Text(verbatim: leaveNote)
        }
        .refreshable { await family.refresh() }
    }

    private var leaveNote: String {
        guard let home = app.family.membership else { return "" }
        return home.throughTailnet ? String(localized: "To come back, you’ll need the home’s password and a new link from \(home.name).")
            : String(localized: "To come back, you’ll need the home’s password.")
    }

    private func tile(_ profile: HomeProfile, current: Bool) -> some View {
        Button {
            Task { await app.family.choose(profile) }
        } label: {
            VStack(spacing: 10) {
                ProfileFace(profile: profile, size: 112)
                    .overlay {
                        if current {
                            RoundedRectangle(cornerRadius: 112 * 0.28 + 5, style: .continuous).strokeBorder(Palette.ink, lineWidth: 3).padding(-5)
                        }
                    }
                HStack(spacing: 5) {
                    Text(verbatim: profile.name).font(current ? MiraFont.headline : MiraFont.callout).lineLimit(1)
                    if profile.pin { Image(systemName: "lock").font(.caption.weight(.semibold)).accessibilityHidden(true) }
                }
                .foregroundStyle(current ? Palette.ink : Palette.muted)
                if current {
                    Text(verbatim: HomeText.thisDevice).font(MiraFont.footnote).foregroundStyle(Palette.faint)
                }
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(Text(verbatim: profile.name))
        .accessibilityValue(Text(verbatim: [current ? HomeText.thisDevice : nil, profile.pin ? String(localized: "With a PIN") : nil].compactMap { $0 }.joined(separator: ", ")))
        .accessibilityIdentifier("family.profile.\(profile.name)")
    }
}

// MARK: A profile's PIN

private struct PinPage: View {
    @Environment(AppModel.self) private var app
    let profile: HomeProfile
    @State private var pin = ""
    @FocusState private var focused: Bool

    var body: some View {
        let family = app.family
        ScrollView {
            VStack(spacing: 22) {
                Spacer(minLength: 56)
                ProfileFace(profile: profile, size: 96)
                Text(verbatim: HomeText.pinOf(profile.name))
                    .font(MiraFont.title)
                    .foregroundStyle(Palette.ink)
                    .accessibilityAddTraits(.isHeader)
                ZStack {
                    // The digits typed, hidden: the boxes draw them.
                    TextField(HomeText.pinOf(profile.name), text: $pin)
                        .keyboardType(.numberPad)
                        .textContentType(.oneTimeCode)
                        .focused($focused)
                        .frame(width: 1, height: 1)
                        .opacity(0.02)
                        .accessibilityLabel(Text(verbatim: HomeText.pinOf(profile.name)))
                        .accessibilityIdentifier("family.pin")
                    HStack(spacing: 12) {
                        ForEach(0..<4, id: \.self) { index in
                            RoundedRectangle(cornerRadius: 12, style: .continuous)
                                .fill(Palette.raised)
                                .overlay {
                                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                                        .strokeBorder(index == pin.count && focused ? Palette.warning : Palette.line, lineWidth: index == pin.count && focused ? 1.5 : 1)
                                }
                                .overlay {
                                    if index < pin.count { Circle().fill(Palette.ink).frame(width: 12, height: 12) }
                                }
                                .frame(width: 54, height: 62)
                        }
                    }
                    .accessibilityHidden(true)
                    .contentShape(.rect)
                    .onTapGesture { focused = true }
                }
                if family.busy != nil { ProgressView().tint(Palette.muted) }
                if let said = family.said { FamilySaid(text: said).frame(maxWidth: 340) }
                Text(verbatim: HomeText.pinNote)
                    .font(MiraFont.footnote)
                    .foregroundStyle(Palette.muted)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 340)
                HStack(spacing: 16) {
                    Button { family.pinBack() } label: {
                        Label("Back", systemImage: "chevron.left").font(MiraFont.callout).frame(minHeight: 32)
                    }
                    .buttonStyle(.glass)
                    .accessibilityIdentifier("family.pin.back")
                    if let home = family.membership {
                        Text(verbatim: String(localized: "Forgot it? Ask \(home.name)’s administrator."))
                            .font(MiraFont.footnote)
                            .foregroundStyle(Palette.muted)
                    }
                }
                Spacer(minLength: 24)
            }
            .padding(.horizontal, 20)
            .frame(maxWidth: .infinity)
        }
        .scrollBounceBehavior(.basedOnSize)
        .onAppear { focused = true }
        .onChange(of: pin) { _, typed in
            let digits = String(typed.filter(\.isASCII).filter(\.isNumber).prefix(4))
            if digits != typed { pin = digits; return }
            guard digits.count == 4 else { return }
            Task {
                await family.open(profile, pin: digits)
                pin = ""
            }
        }
    }
}

// MARK: Joining the home

private struct JoinPage: View {
    @Environment(AppModel.self) private var app
    let card: Family.JoinCard
    @State private var password = ""
    @State private var reveal = false
    @FocusState private var focused: Bool

    var body: some View {
        let family = app.family
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                HStack(spacing: 12) {
                    MiraIcon(size: 34)
                    Text(verbatim: card.title).font(MiraFont.section).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
                }
                .accessibilityIdentifier("family.join")
                HStack(alignment: .top, spacing: 12) {
                    Image(systemName: card.fromLink ? "link" : "house.fill")
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Palette.success)
                        .frame(width: 22)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(verbatim: card.heading).font(MiraFont.headline).foregroundStyle(Palette.ink)
                        Text(verbatim: card.note).font(MiraFont.footnote).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 0)
                }
                .padding(14)
                .background(Palette.surface, in: .rect(cornerRadius: 16, style: .continuous))
                .overlay { RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Palette.line) }
                .accessibilityElement(children: .combine)

                VStack(alignment: .leading, spacing: 8) {
                    Text(verbatim: HomeText.password).font(MiraFont.subheadline).foregroundStyle(Palette.ink).accessibilityHidden(true)
                    HStack(spacing: 8) {
                        Group {
                            if reveal {
                                TextField(HomeText.password, text: $password)
                            } else {
                                SecureField(HomeText.password, text: $password)
                            }
                        }
                        .textContentType(.password)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .submitLabel(.go)
                        .focused($focused)
                        .onSubmit { Task { await family.join(password) } }
                        .accessibilityIdentifier("family.password")
                        Button { reveal.toggle() } label: {
                            Image(systemName: reveal ? "eye.slash" : "eye").foregroundStyle(Palette.muted)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(reveal ? Text("Hide password") : Text("Show password"))
                    }
                    .modifier(FieldStyle(focused: focused))
                }
                .disabled(!card.canJoin)

                if let said = family.said { FamilySaid(text: said) }

                HStack(spacing: 14) {
                    Button {
                        Task { await family.join(password) }
                    } label: {
                        ButtonLabel(busy: family.busy != nil, title: "Continue").frame(maxWidth: 140)
                    }
                    .buttonStyle(.glassProminent)
                    .disabled(!card.canJoin || family.busy != nil || password.isEmpty)
                    .accessibilityIdentifier("family.continue")
                    Button { family.ownAccount() } label: {
                        Text(verbatim: card.ownLabel).font(MiraFont.callout).foregroundStyle(Palette.ink).multilineTextAlignment(.leading)
                    }
                    .buttonStyle(.plain)
                    .disabled(family.busy != nil)
                    .accessibilityIdentifier("family.own")
                }
                if let busy = family.busy {
                    Text(verbatim: busy).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
                Text(verbatim: HomeText.next).font(MiraFont.footnote).foregroundStyle(Palette.faint).fixedSize(horizontal: false, vertical: true)
            }
            .padding(22)
            .background(Palette.surface.opacity(0.6), in: .rect(cornerRadius: 24, style: .continuous))
            .overlay { RoundedRectangle(cornerRadius: 24, style: .continuous).strokeBorder(Palette.line) }
            .frame(maxWidth: 440)
            .padding(.horizontal, 16)
            .padding(.top, 64)
            .padding(.bottom, 32)
            .frame(maxWidth: .infinity)
        }
        .scrollDismissesKeyboard(.interactively)
        .onAppear { if card.canJoin { focused = true } }
    }
}

// MARK: The quick switch (C): a long press on the Settings tab

/// « Switch profile »: the home's profiles, the one open here ticked, a padlock for a PIN; « Leave this home ».
struct SwitchProfileSheet: View {
    @Environment(AppModel.self) private var app
    @Environment(\.dismiss) private var dismiss
    @State private var leaving = false

    var body: some View {
        let family = app.family
        NavigationStack {
            List {
                if let home = family.membership {
                    Section {
                        ForEach(home.profiles) { profile in
                            let current = profile.id == home.current && family.isHomeSession(app.session)
                            Button {
                                guard !current else { return dismiss() }
                                dismiss()
                                Task { await family.choose(profile, fromMenu: true) }
                            } label: {
                                HStack(spacing: 14) {
                                    ProfileFace(profile: profile, size: 44)
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(verbatim: profile.name).font(MiraFont.headline).foregroundStyle(Palette.ink)
                                        if current { Text(verbatim: HomeText.thisDevice).font(MiraFont.footnote).foregroundStyle(Palette.muted) }
                                    }
                                    Spacer(minLength: 8)
                                    if current {
                                        Image(systemName: "checkmark").font(.body.weight(.semibold)).foregroundStyle(Palette.ink)
                                    } else if profile.pin {
                                        Image(systemName: "lock").foregroundStyle(Palette.muted)
                                    }
                                }
                                .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(Text(verbatim: profile.name))
                            .accessibilityValue(Text(verbatim: current ? HomeText.thisDevice : profile.pin ? String(localized: "With a PIN") : ""))
                            .accessibilityIdentifier("switch.\(profile.name)")
                        }
                    } header: {
                        Text(verbatim: HomeText.homeOf(home.name))
                    }
                    .listRowBackground(Palette.surface)
                    Section {
                        Button(role: .destructive) { leaving = true } label: {
                            Label(HomeText.leave, systemImage: "rectangle.portrait.and.arrow.right")
                        }
                        .accessibilityIdentifier("switch.leave")
                    }
                    .listRowBackground(Palette.surface)
                }
            }
            .font(MiraFont.body)
            .scrollContentBackground(.hidden)
            .background(Palette.bg)
            .navigationTitle("Switch profile")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .confirmationDialog(String(localized: "Leave this home?"), isPresented: $leaving, titleVisibility: .visible) {
                Button(HomeText.leave, role: .destructive) {
                    dismiss()
                    Task { await family.leave() }
                }
                .accessibilityIdentifier("switch.leave.confirm")
            }
            .task { await family.refresh() }
        }
        .presentationDetents([.medium, .large])
    }
}
