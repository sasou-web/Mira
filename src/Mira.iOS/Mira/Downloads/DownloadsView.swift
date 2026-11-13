import SwiftUI

/// « Télécharger sur le PC serveur », from Settings → Administration: Mira for Windows on the PC of Jellyfin searches
/// with TorLink and downloads there, then files the title in the library; this screen asks it through the relay of
/// Mira's extension (as Mira for Windows and Mac do away from that PC). What the PC has under way is read again every
/// 3 seconds while the screen shows and Mira is in front.
struct DownloadsView: View {
    @Environment(AppModel.self) private var app
    @Environment(\.scenePhase) private var phase
    @State private var state: RemoteLines.State?
    @State private var failure: String?
    @State private var query = ""
    @State private var looking: Looking?
    @State private var searching: Task<Void, Never>?
    @State private var results: Results?
    @State private var searchProblem: String?
    @State private var sent: Set<String> = []
    @State private var asking: Ask?
    @State private var cancelling: RemoteLines.Download?
    @State private var placedOpen = false
    @State private var unfolded: Set<String> = []
    @State private var magnetProblem: String?
    @FocusState private var typing: Bool

    private static let every: Duration = .seconds(3)
    private static let placedShown = 3

    struct Looking: Equatable { var id: String?; var query: String; var started: Date }
    struct Results: Equatable { var search: String; var query: String; var count: Int; var groups: [Releases.Group] }

    /// What « Download » asks once more before it goes: a result of the search, or a magnet link pasted.
    enum Ask: Identifiable {
        case hit(search: String, Releases.Hit)
        case magnet(String, name: String?)
        var id: String {
            switch self {
            case let .hit(search, hit): "\(search)/\(hit.id)"
            case let .magnet(link, _): link
            }
        }
    }

    private var agent: RemoteLines.Agent? { state?.agent }
    private var line: (text: String, ready: Bool) { RemoteLines.agentLine(agent) }

    var body: some View {
        List {
            if let state {
                content(state)
            } else if let failure {
                Section {
                    Text(verbatim: failure).foregroundStyle(Palette.muted)
                    Button("Try again") { Task { await load() } }
                }
                .listRowBackground(Palette.surface)
            } else {
                Section {
                    HStack(spacing: 10) {
                        ProgressView()
                        Text("Checking the server PC…").foregroundStyle(Palette.muted)
                    }
                }
                .listRowBackground(Palette.surface)
            }
        }
        .font(MiraFont.body)
        .scrollContentBackground(.hidden)
        .scrollDismissesKeyboard(.interactively)
        .foldsTabBar()
        .background(Palette.bg)
        .navigationTitle("Download")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await load() }
        .task(id: phase == .active) {
            guard phase == .active else { return }
            while !Task.isCancelled {
                await load()
                try? await Task.sleep(for: Self.every)
            }
        }
        .onDisappear { searching?.cancel() }
        .confirmationDialog("Download to the server PC?", isPresented: Binding(get: { asking != nil }, set: { if !$0 { asking = nil } }), titleVisibility: .visible, presenting: asking) { ask in
            Button("Download") { Task { await send(ask) } }
        } message: { ask in
            Text(verbatim: confirmation(ask))
        }
        .confirmationDialog("Cancel download", isPresented: Binding(get: { cancelling != nil }, set: { if !$0 { cancelling = nil } }), titleVisibility: .visible, presenting: cancelling) { download in
            Button("Cancel download", role: .destructive) { Task { await control(download, "delete") } }
        } message: { _ in
            Text("TorLink stops it and deletes what it has already downloaded on the server PC.")
        }
    }

    // MARK: Sections

    @ViewBuilder
    private func content(_ state: RemoteLines.State) -> some View {
        Section {
            if agent == nil {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Open Mira on the server PC").font(MiraFont.headline).foregroundStyle(Palette.ink)
                    Text("It searches and downloads with TorLink, then adds the title to your library. Open it on the PC that runs Jellyfin, signed in with an administrator account: this screen comes alive as soon as it’s there, even minimized.")
                        .font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
                .padding(.vertical, 4)
            } else {
                Label {
                    Text(verbatim: line.text).foregroundStyle(Palette.ink)
                } icon: {
                    Image(systemName: line.ready ? "checkmark.circle" : "clock").foregroundStyle(line.ready ? Palette.ink : Palette.warning)
                }
                .accessibilityIdentifier("downloads.agent")
            }
        }
        .listRowBackground(Palette.surface)

        if agent != nil { searchSection }

        let rows = self.rows(state)
        if !rows.isEmpty {
            Section("On the server PC") {
                ForEach(rows) { row in rowView(row) }
            }
            .listRowBackground(Palette.surface)
        }

        let placed = state.placed ?? []
        if !placed.isEmpty {
            Section("Added to your library") {
                ForEach(placedOpen ? placed : Array(placed.prefix(Self.placedShown))) { title in
                    Label {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(verbatim: title.title).foregroundStyle(Palette.ink)
                            Text(verbatim: RemoteLines.placedLine(title.status)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                        }
                    } icon: {
                        Image(systemName: title.ok == true ? "checkmark" : "exclamationmark.triangle").foregroundStyle(title.ok == true ? Palette.ink : Palette.warning)
                    }
                    .accessibilityElement(children: .combine)
                }
                if placed.count > Self.placedShown {
                    Button(placedOpen ? String(localized: "See less") : String(localized: "See all \(placed.count)")) { placedOpen.toggle() }
                }
            }
            .listRowBackground(Palette.surface)
        }

        if agent != nil { resultSections }

        if agent != nil {
            Section {
            } footer: {
                Text("TorLink downloads on the PC that runs Jellyfin, without you having to touch it, then Mira adds the title to your library. Mira must be open there, even minimized. Only Jellyfin administrator accounts see this screen.")
            }
        }
    }

    private var searchSection: some View {
        Section {
            HStack(spacing: 10) {
                Image(systemName: "magnifyingglass").foregroundStyle(Palette.muted).accessibilityHidden(true)
                TextField("Movie, show, anime…", text: $query)
                    .focused($typing)
                    .submitLabel(.search)
                    .autocorrectionDisabled()
                    .onSubmit { start() }
                    .accessibilityLabel(Text("What do you want to download?"))
                    .accessibilityIdentifier("downloads.query")
                Button("Search") { start() }
                    .buttonStyle(.borderedProminent)
                    .tint(Palette.accent)
                    .foregroundStyle(Palette.onAccent)
                    .disabled(!line.ready || looking != nil || RemoteLines.cleanQuery(query) == nil)
                    .accessibilityIdentifier("downloads.search")
            }
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Paste a magnet link").foregroundStyle(Palette.ink)
                    Spacer(minLength: 8)
                    // The system's paste button: no « Allow Paste » question, the person chose to paste.
                    PasteButton(payloadType: String.self) { texts in
                        Task { @MainActor in paste(texts.first ?? "") }
                    }
                    .labelStyle(.iconOnly)
                    .buttonBorderShape(.capsule)
                }
                if let magnetProblem {
                    Text(verbatim: magnetProblem).font(MiraFont.footnote).foregroundStyle(Palette.danger)
                } else {
                    Text("Paste a link from a site you know: TorLink downloads it on the server PC, then Mira adds it to your library.")
                        .font(MiraFont.footnote).foregroundStyle(Palette.muted)
                }
            }
            .padding(.vertical, 2)
        }
        .listRowBackground(Palette.surface)
    }

    @ViewBuilder
    private var resultSections: some View {
        if let looking {
            Section {
                HStack(spacing: 10) {
                    ProgressView()
                    Text("Searching movies, shows and anime for “\(looking.query)” on the server PC…").foregroundStyle(Palette.muted)
                }
                .accessibilityElement(children: .combine)
            }
            .listRowBackground(Palette.surface)
        } else if let searchProblem {
            Section {
                Text(verbatim: searchProblem).foregroundStyle(Palette.muted).accessibilityIdentifier("downloads.problem")
            }
            .listRowBackground(Palette.surface)
        }
        if let results {
            if results.groups.isEmpty {
                if looking == nil {
                    Section {
                        Text("No results for “\(results.query)” in movies, shows or anime. Try another title.").foregroundStyle(Palette.muted)
                    }
                    .listRowBackground(Palette.surface)
                }
            } else {
                ForEach(results.groups) { group in
                    Section {
                        release(results.search, group.best, best: !Releases.dead(group.best))
                        if !group.others.isEmpty {
                            let open = unfolded.contains(group.key)
                            Button {
                                if open { unfolded.remove(group.key) } else { unfolded.insert(group.key) }
                            } label: {
                                HStack {
                                    Text(open ? String(localized: "Hide other versions") : String(localized: "\(group.others.count) other versions"))
                                    Spacer()
                                    Image(systemName: open ? "chevron.up" : "chevron.down").font(.footnote).accessibilityHidden(true)
                                }
                            }
                            .accessibilityValue(open ? Text("Shown") : Text("Hidden"))
                            if open {
                                ForEach(group.others, id: \.hit.id) { entry in release(results.search, entry, best: false) }
                            }
                        }
                    } header: {
                        heading(group)
                    }
                    .listRowBackground(Palette.surface)
                }
            }
        }
    }

    private func heading(_ group: Releases.Group) -> some View {
        let kind = RemoteLines.kindLabel(group.category)
        return VStack(alignment: .leading, spacing: 2) {
            Text(verbatim: group.title).font(MiraFont.headline).foregroundStyle(Palette.ink).textCase(nil)
            let under = [group.label, kind].filter { !$0.isEmpty }.joined(separator: " · ")
            if !under.isEmpty { Text(verbatim: under).font(MiraFont.footnote).foregroundStyle(Palette.muted).textCase(nil) }
        }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isHeader)
    }

    private func release(_ search: String, _ entry: Releases.Entry, best: Bool) -> some View {
        let key = "\(search)/\(entry.hit.id)"
        let done = sent.contains(key)
        let quality = Releases.qualityLine(entry.release)
        let caution = Releases.warning(entry)
        return Button {
            asking = .hit(search: search, entry.hit)
        } label: {
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 8) {
                        Text(verbatim: quality.isEmpty ? String(localized: "Quality not stated") : quality).font(MiraFont.callout).foregroundStyle(Palette.ink)
                        if best {
                            Text("Best pick").font(MiraFont.footnote).foregroundStyle(Palette.onAccent)
                                .padding(.horizontal, 7).padding(.vertical, 2)
                                .background(Palette.accent, in: .capsule)
                        }
                    }
                    Text(verbatim: RemoteLines.hitLine(entry.hit)).font(MiraFont.footnote).foregroundStyle(Palette.muted)
                    Text(verbatim: entry.hit.name).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(2)
                    if !caution.isEmpty {
                        Text(verbatim: caution).font(MiraFont.footnote).foregroundStyle(Palette.warning)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                if done {
                    Text("Sent").font(MiraFont.footnote).foregroundStyle(Palette.muted)
                } else {
                    Image(systemName: "arrow.down.circle").font(.title3).foregroundStyle(Palette.ink).accessibilityHidden(true)
                }
            }
            .padding(.vertical, 4)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .disabled(done)
        .accessibilityElement(children: .combine)
        .accessibilityHint(done ? Text("Sent") : Text("Download"))
        .accessibilityIdentifier("downloads.hit.\(entry.hit.id)")
    }

    // MARK: Under way on the PC

    private enum Row: Identifiable {
        case request(RemoteLines.Request)
        case download(RemoteLines.Download, pending: String?)
        var id: String {
            switch self {
            case let .request(request): "r:\(request.id)"
            case let .download(download, _): "d:\(download.id)"
            }
        }
    }

    private func rows(_ state: RemoteLines.State) -> [Row] {
        let downloads = state.downloads ?? []
        let inTorLink = Set(downloads.map(\.id))
        // A request TorLink has now shows as its download; one sent and not listed yet, as sent.
        let asked = (state.requests ?? []).filter { !($0.state == "sent" && inTorLink.contains($0.infoHash ?? "")) }
        let controls = state.controls ?? []
        return asked.map { Row.request($0) } + downloads.map { download in
            .download(download, pending: controls.first { $0.target == download.id }.map { Self.asked($0.action) })
        }
    }

    private static func asked(_ action: String) -> String {
        switch action {
        case "pause": String(localized: "Asking the server PC to pause…")
        case "resume": String(localized: "Asking the server PC to resume…")
        default: String(localized: "Asking the server PC to cancel…")
        }
    }

    @ViewBuilder
    private func rowView(_ row: Row) -> some View {
        switch row {
        case let .request(request):
            let name = Releases.readable(request.name)
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: request.state == "failed" ? "exclamationmark.triangle" : "clock")
                    .foregroundStyle(request.state == "failed" ? Palette.warning : Palette.muted).accessibilityHidden(true)
                texts(name, RemoteLines.requestLine(request))
                if request.state != "claimed" {
                    Button {
                        Task { await cancel(request) }
                    } label: {
                        Image(systemName: "xmark").font(.body.weight(.semibold))
                    }
                    .buttonStyle(.borderless)
                    .accessibilityLabel(request.state == "waiting" ? Text("Cancel “\(name.title)”") : Text("Remove “\(name.title)”"))
                }
            }
            .padding(.vertical, 4)
        case let .download(download, pending):
            let name = Releases.readable(download.name)
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: "arrow.down.circle").foregroundStyle(Palette.muted).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 6) {
                    texts(name, pending ?? RemoteLines.downloadLine(download))
                    ProgressView(value: Double(RemoteLines.percent(download.progress)), total: 100).tint(Palette.ink).accessibilityHidden(true)
                }
                if RemoteLines.controllable(download) {
                    let paused = download.status == "paused" || download.status == "stopped"
                    Menu {
                        if download.status != "failed" {
                            if paused {
                                Button("Resume", systemImage: "play") { Task { await control(download, "resume") } }
                            } else {
                                Button("Pause", systemImage: "pause") { Task { await control(download, "pause") } }
                            }
                        }
                        Button("Cancel download", systemImage: "xmark", role: .destructive) { cancelling = download }
                    } label: {
                        Image(systemName: "ellipsis").font(.body.weight(.semibold)).frame(width: 32, height: 32)
                    }
                    .accessibilityLabel(Text("Actions for “\(name.title)”"))
                }
            }
            .padding(.vertical, 4)
            .accessibilityIdentifier("downloads.download.\(download.id)")
        }
    }

    private func texts(_ name: (title: String, full: String?), _ line: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(verbatim: name.title).foregroundStyle(Palette.ink)
            if let full = name.full {
                Text(verbatim: full).font(MiraFont.footnote).foregroundStyle(Palette.muted).lineLimit(1).accessibilityHidden(true)
            }
            Text(verbatim: line).font(MiraFont.footnote).foregroundStyle(Palette.muted)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    // MARK: Work

    private func explain(_ error: Error) -> String {
        switch error as? JellyfinError {
        case .forbidden: String(localized: "Only Jellyfin administrator accounts can download to the server PC.")
        case .http(404): String(localized: "The Mira extension on your server doesn’t know this screen yet. Update Mira on the server PC, then restart Jellyfin.")
        default: Messages.text(error)
        }
    }

    private func say(_ text: String) { app.together.notice = text }

    private func load() async {
        guard let client = app.client else { return }
        do {
            state = try await client.torLink()
            failure = nil
        } catch is CancellationError {
        } catch {
            // A screen already filled keeps what it shows (a hiccup on Tailscale); an empty one says why.
            if state == nil || (error as? JellyfinError).map({ $0 == .forbidden || $0 == .http(404) }) == true {
                state = nil
                failure = explain(error)
            }
        }
    }

    private func start() {
        guard let text = RemoteLines.cleanQuery(query) else { typing = true; return }
        guard line.ready else { searchProblem = line.text; return }
        guard let client = app.client else { return }
        typing = false
        searching?.cancel()
        let started = Date()
        looking = Looking(id: nil, query: text, started: started)
        searchProblem = nil
        searching = Task {
            do {
                let id = try await client.torLinkSearch(text)
                guard !Task.isCancelled else { return }
                looking?.id = id
                while !Task.isCancelled {
                    try await Task.sleep(for: .seconds(1))
                    do {
                        let found = try await client.torLinkSearchState(id)
                        if found.state == "done" {
                            let hits = found.results ?? []
                            results = Results(search: id, query: text, count: hits.count, groups: Releases.group(hits))
                            unfolded = []
                            looking = nil
                            return
                        }
                        if found.state == "failed" {
                            finish(found.error.flatMap { $0.isEmpty ? nil : $0 } ?? String(localized: "The search didn’t go through."))
                            return
                        }
                    } catch JellyfinError.http(404) {
                        // Forgotten (Jellyfin restarted meanwhile, or kept past its time): it will not come back.
                        finish(String(localized: "This search was lost. Try it again."))
                        return
                    } catch is CancellationError {
                        return
                    } catch {}
                    if Date().timeIntervalSince(started) > 90 {
                        finish(String(localized: "The server PC didn’t answer in time. Check that Mira is open there, then search again."))
                        return
                    }
                }
            } catch is CancellationError {
            } catch {
                finish(explain(error))
            }
        }
    }

    private func finish(_ problem: String) {
        looking = nil
        results = nil
        searchProblem = problem
    }

    private func paste(_ text: String) {
        let link = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard RemoteLines.infoHash(link) != nil else {
            magnetProblem = String(localized: "That’s not a magnet link (“magnet:?xt=urn:btih:…”).")
            return
        }
        magnetProblem = nil
        asking = .magnet(link, name: RemoteLines.displayName(link))
    }

    private func confirmation(_ ask: Ask) -> String {
        let details: String
        switch ask {
        case let .hit(_, hit):
            let made = [Releases.qualityLine(Releases.parse(hit.name)), RemoteLines.hitLine(hit)].filter { !$0.isEmpty }.joined(separator: " · ")
            details = [hit.name, made].filter { !$0.isEmpty }.joined(separator: "\n")
        case let .magnet(link, name):
            details = name ?? RemoteLines.infoHash(link) ?? link
        }
        if let name = agent?.name {
            return String(localized: "\(details). TorLink downloads it on \(name), then Mira adds it to your library. You can cancel it here until it’s done.")
        }
        return String(localized: "\(details). TorLink downloads it on the server PC, then Mira adds it to your library. You can cancel it here until it’s done.")
    }

    private func send(_ ask: Ask) async {
        guard let client = app.client else { return }
        do {
            switch ask {
            case let .hit(search, hit):
                try await client.torLinkRequest(search: search, hit: hit)
                sent.insert("\(search)/\(hit.id)")
            case let .magnet(link, _):
                try await client.torLinkRequest(magnet: link)
            }
            // Marked « Sent » and at the top of the list: a notice only when the server PC will do it later.
            if !line.ready { say(String(localized: "Request saved: the server PC will handle it as soon as Mira is open there.")) }
        } catch {
            say(explain(error))
        }
        await load()
    }

    private func cancel(_ request: RemoteLines.Request) async {
        guard let client = app.client else { return }
        do {
            // Gone from the list, it needs no notice.
            try await client.torLinkCancel(request.id)
        } catch JellyfinError.http(409) {
            say(String(localized: "The server PC has already picked it up. Cancel it in its list as soon as it shows up there."))
        } catch JellyfinError.http(404) {
            say(String(localized: "This request is gone."))
        } catch {
            say(explain(error))
        }
        await load()
    }

    private func control(_ download: RemoteLines.Download, _ action: String) async {
        guard let client = app.client else { return }
        do {
            try await client.torLinkControl(download.id, action)
            say(Self.asked(action))
        } catch {
            say(explain(error))
        }
        await load()
    }
}
