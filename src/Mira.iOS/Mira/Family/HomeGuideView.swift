import SwiftUI
import UIKit

/// The guide « Away from home », in a sheet over Settings → Home (Windows and Mac show it in place of the section): one
/// step per screen, Tailscale's page one tap away with a drawing of what to tap, the secret pasted by the system's Paste
/// button (no clipboard prompt), then Mira's check and the rules put right on a tap.
struct HomeGuideView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.openURL) private var openURL
    @Environment(\.dismiss) private var dismiss
    @State var step: Int
    @State private var secret = ""
    @State private var clientId = ""
    @State private var needsId = false
    @State private var refused: HomeAdminRefused?
    @State private var state: HomeTailnetState?
    @State private var working = false
    @State private var said: String?
    @State private var copied = false
    @State private var showLines = false
    /// Called when the guide is done or closed: Settings → Home reads the home again.
    var done: () -> Void

    init(step: Int, done: @escaping () -> Void) {
        _step = State(initialValue: step)
        self.done = done
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    bars
                    let page = current
                    Text(verbatim: page.title).font(MiraFont.title).foregroundStyle(Palette.ink).accessibilityAddTraits(.isHeader)
                    Text(verbatim: page.why).font(MiraFont.body).foregroundStyle(Palette.muted)
                    ForEach(Array(page.todo.enumerated()), id: \.offset) { index, line in
                        HStack(alignment: .firstTextBaseline, spacing: 10) {
                            Text(verbatim: "\(index + 1)").font(MiraFont.footnote).foregroundStyle(Palette.ink)
                                .frame(width: 22, height: 22).background(Circle().fill(Palette.raised2))
                            Text(verbatim: line).font(MiraFont.body).foregroundStyle(Palette.ink)
                        }
                    }
                    if step == 1 {
                        Button(copied ? HomeGuide.copied : HomeGuide.copy) { UIPasteboard.general.string = HomeGuide.tagName; copied = true }
                            .buttonStyle(.bordered)
                    }
                    if let open = page.open, let label = page.openLabel {
                        Button(label) { openURL(open) }.buttonStyle(.borderedProminent).tint(Palette.accent).foregroundStyle(Palette.onAccent)
                    }
                    if step <= 3 { sketch }
                    if step == 3 { secretStep }
                    if step == 4 { checkStep }
                    if let foot = page.foot { Text(verbatim: foot).font(MiraFont.footnote).foregroundStyle(Palette.muted) }
                    if let said { Text(verbatim: said).font(MiraFont.footnote).foregroundStyle(Palette.danger).accessibilityIdentifier("home.guide.said") }
                    HStack {
                        Button(step == 1 ? String(localized: "Not now") : String(localized: "Back")) { back() }.buttonStyle(.bordered)
                        Spacer()
                        if step < 4 || state?.policy == "ok" || !(state?.fix ?? []).isEmpty {
                            Button(page.next) { Task { await forward() } }
                                .buttonStyle(.borderedProminent).tint(Palette.accent).foregroundStyle(Palette.onAccent)
                                .disabled(working || (step == 3 && secret.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty))
                                .accessibilityIdentifier("home.guide.next")
                        }
                    }
                    .padding(.top, 8)
                }
                .padding(20)
            }
            .background(Palette.bg)
            .navigationTitle(Text(verbatim: HomeGuide.header(step)))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Close") { finish() } }
            }
            .disabled(working)
            .task(id: step) { if step == 4 { await read() } }
        }
    }

    private var current: HomeGuide.Step {
        switch step {
        case 1: HomeGuide.tag
        case 2: HomeGuide.key
        case 3: HomeGuide.secret
        default: HomeGuide.rules(state?.fix?.count ?? 0)
        }
    }

    private var bars: some View {
        HStack(spacing: 6) {
            ForEach(1...HomeGuide.steps, id: \.self) { index in
                Capsule().fill(index <= step ? Palette.accent : Palette.raised2).frame(height: 3)
            }
        }
        .accessibilityHidden(true)
    }

    // MARK: Step 3

    @ViewBuilder private var secretStep: some View {
        if let refused, let trouble = HomeGuide.trouble(refused.policy, missing: refused.missing) {
            callout(trouble.title, trouble.text, danger: true) {
                ForEach(trouble.remedies, id: \.self) { remedy in Button(HomeGuide.label(remedy)) { act(remedy) } }
            }
        }
        // The system's Paste button: the clipboard is read only when it is tapped, without iOS asking.
        PasteButton(payloadType: String.self) { texts in
            guard let text = texts.first?.trimmingCharacters(in: .whitespacesAndNewlines) else { return }
            Task { @MainActor in
                secret = text
                if HomeGuide.looksLikeSecret(text) { await forward() }
            }
        }
        .tint(Palette.accent)
        SecureField(HomeGuide.secretBox, text: $secret)
            .textInputAutocapitalization(.never).autocorrectionDisabled()
            .padding(10).background(RoundedRectangle(cornerRadius: 10).fill(Palette.surface))
            .accessibilityIdentifier("home.guide.secret")
        if needsId {
            Text(verbatim: HomeGuide.idNeeded).font(MiraFont.footnote).foregroundStyle(Palette.muted)
            TextField(HomeGuide.idBox, text: $clientId)
                .textInputAutocapitalization(.never).autocorrectionDisabled()
                .padding(10).background(RoundedRectangle(cornerRadius: 10).fill(Palette.surface))
        }
    }

    // MARK: Step 4

    @ViewBuilder private var checkStep: some View {
        if let state {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(Array(HomeGuide.checks(state).enumerated()), id: \.offset) { _, line in
                    let (mark, text) = line
                    Label {
                        Text(verbatim: text).font(MiraFont.body).foregroundStyle(Palette.ink)
                    } icon: {
                        Image(systemName: mark == .ok ? "checkmark.circle.fill" : mark == .warn ? "exclamationmark.circle.fill" : "xmark.circle.fill")
                            .foregroundStyle(mark == .ok ? Palette.success : mark == .warn ? Palette.warning : Palette.danger)
                    }
                }
            }
            if let fix = state.fix, !fix.isEmpty {
                callout(HomeGuide.makeChanges(fix.count), "", danger: false) {
                    ForEach(fix, id: \.self) { change in Text(verbatim: "•  " + HomeGuide.change(change)).font(MiraFont.footnote).foregroundStyle(Palette.muted) }
                    Button(showLines ? HomeGuide.hideLines : HomeGuide.seeLines) { showLines.toggle() }
                    if showLines { lines(state) }
                }
            } else if state.policy != "ok", let trouble = HomeGuide.trouble(state.policy, missing: state.missing ?? [], broad: state.broad == true) {
                callout(trouble.title, trouble.text, danger: true) {
                    ForEach(trouble.remedies, id: \.self) { remedy in Button(HomeGuide.label(remedy)) { act(remedy) } }
                }
            }
        } else {
            ProgressView(HomeGuide.checking).tint(Palette.muted)
        }
    }

    private func lines(_ state: HomeTailnetState) -> some View {
        Text(verbatim: (state.lines ?? []).joined(separator: "\n"))
            .font(.system(size: 12, design: .monospaced)).foregroundStyle(Palette.ink).textSelection(.enabled)
            .padding(10).frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 8).fill(Palette.bg))
    }

    private func callout<More: View>(_ title: String, _ text: String, danger: Bool, @ViewBuilder _ more: () -> More) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(verbatim: title).font(MiraFont.headline).foregroundStyle(danger ? Palette.danger : Palette.ink)
            if !text.isEmpty { Text(verbatim: text).font(MiraFont.footnote).foregroundStyle(Palette.muted) }
            more()
        }
        .padding(14).frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 14).fill(Palette.surface))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(danger ? Palette.danger.opacity(0.6) : Palette.line))
    }

    // MARK: The drawing

    private var sketch: some View {
        let (address, lines) = HomeGuide.drawing(step)
        return VStack(alignment: .leading, spacing: 8) {
            Text(verbatim: address).font(.system(size: 11)).foregroundStyle(Palette.muted).lineLimit(1).truncationMode(.middle)
                .padding(.horizontal, 10).padding(.vertical, 4).background(RoundedRectangle(cornerRadius: 6).fill(Palette.raised2))
            ForEach(lines) { line in sketchLine(line).opacity(line.dim ? 0.45 : 1) }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 14).fill(Palette.surface))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Palette.line))
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Text("Drawing of Tailscale’s page"))
    }

    @ViewBuilder private func sketchLine(_ line: HomeGuide.Sketch) -> some View {
        HStack(spacing: 8) {
            switch line.kind {
            case .heading:
                Text(verbatim: line.label).font(MiraFont.footnote).foregroundStyle(Palette.ink)
            case .button:
                Text(verbatim: line.label).font(MiraFont.footnote).foregroundStyle(Palette.onAccent)
                    .padding(.horizontal, 10).padding(.vertical, 4).background(RoundedRectangle(cornerRadius: 6).fill(Palette.ink))
            case .field:
                VStack(alignment: .leading, spacing: 3) {
                    Text(verbatim: line.label).font(.system(size: 11)).foregroundStyle(Palette.muted)
                    Text(verbatim: line.value ?? "").font(.system(size: 12, design: .monospaced)).foregroundStyle(Palette.ink)
                        .padding(.horizontal, 8).padding(.vertical, 3).frame(maxWidth: .infinity, alignment: .leading)
                        .overlay(RoundedRectangle(cornerRadius: 5).stroke(Palette.raised2))
                }
            case .choice:
                Image(systemName: line.on ? "largecircle.fill.circle" : "circle").foregroundStyle(line.on ? Palette.accent : Palette.faint)
                Text(verbatim: line.label).font(MiraFont.footnote).foregroundStyle(Palette.ink).strikethrough(line.wrong)
                if let value = line.value { Text(verbatim: value).font(.system(size: 12)).foregroundStyle(line.wrong ? Palette.danger : Palette.muted) }
            case .scope:
                Text(verbatim: line.label).font(MiraFont.footnote).foregroundStyle(Palette.ink)
                if let tag = line.tag {
                    Text(verbatim: tag).font(.system(size: 11, design: .monospaced)).foregroundStyle(Palette.ink)
                        .padding(.horizontal, 5).padding(.vertical, 1).background(RoundedRectangle(cornerRadius: 4).fill(Palette.raised2))
                }
                Spacer(minLength: 4)
                Image(systemName: line.on ? "checkmark.square.fill" : "square").foregroundStyle(line.on ? Palette.accent : Palette.faint)
                Text(verbatim: "R").font(.system(size: 11)).foregroundStyle(Palette.muted)
                Image(systemName: line.on ? "checkmark.square.fill" : "square").foregroundStyle(line.on ? Palette.accent : Palette.faint)
                Text(verbatim: "W").font(.system(size: 11)).foregroundStyle(Palette.muted)
            }
            if line.kind != .scope { Spacer(minLength: 4) }
            if line.marker > 0 {
                Text(verbatim: "\(line.marker)").font(.system(size: 11, weight: .bold)).foregroundStyle(Palette.onAccent)
                    .frame(width: 20, height: 20).background(Circle().fill(Palette.accent))
            }
        }
    }

    // MARK: Moving

    private func back() {
        said = nil
        if step == 1 { finish(); return }
        go(step == 4 ? 2 : step - 1)
    }

    private func go(_ next: Int) {
        if next != 3 { refused = nil; needsId = false }
        said = nil
        step = next
    }

    private func finish() {
        done()
        dismiss()
    }

    private func act(_ remedy: HomeGuide.Remedy) {
        switch remedy {
        case .redoKey, .setUpAgain: go(2)
        case .openCredentials: openURL(HomeGuide.credentialsPage)
        case .openPolicy: openURL(HomeGuide.policyPage)
        case .seeLines: showLines.toggle()
        case .checkAgain: Task { await read() }
        case .fix: Task { await forward() }
        }
    }

    private func forward() async {
        guard let client = app.client else { return }
        switch step {
        case 3:
            let given = secret.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !given.isEmpty else { return }
            if needsId && clientId.trimmingCharacters(in: .whitespaces).isEmpty { said = HomeGuide.idNeeded; return }
            working = true
            defer { working = false }
            do {
                try await client.homeConnectTailnet(id: needsId ? clientId.trimmingCharacters(in: .whitespaces) : nil, secret: given)
                secret = ""
                if UIPasteboard.general.hasStrings, UIPasteboard.general.string?.trimmingCharacters(in: .whitespacesAndNewlines) == given { UIPasteboard.general.string = "" }
                go(4)
            } catch let error as HomeAdminRefused {
                secret = ""
                if error.needsId && !needsId { needsId = true; said = HomeGuide.idNeeded; return }
                if HomeGuide.trouble(error.policy, missing: error.missing) != nil { refused = error; said = nil; return }
                said = error.message
            } catch {
                said = Messages.text(error)
            }
        case 4:
            guard let fix = state?.fix, !fix.isEmpty else { finish(); return }
            working = true
            defer { working = false }
            do {
                try await client.homeFixPolicy()
                finish()
            } catch let error as HomeAdminRefused {
                said = HomeGuide.trouble(error.policy, missing: error.missing).map { $0.title + ". " + $0.text } ?? error.message
            } catch {
                said = Messages.text(error)
            }
        default:
            go(step + 1)
        }
    }

    private func read() async {
        guard let client = app.client else { return }
        do { state = try await client.homeTailnet() } catch { said = (error as? HomeAdminRefused)?.message ?? Messages.text(error) }
    }
}
