import SwiftUI

/// Favorite and Watched on a title's page: two round buttons of glass beside Play, filled when set, that change the title
/// at once (Jellyfin is told, then every page reads it again).
struct TitleActions: View {
    @Binding var item: Item
    @Environment(AppModel.self) private var app
    /// Touched here: the only change that is felt (not a page read again after the player closed).
    @State private var touched = 0
    @ScaledMetric(relativeTo: .body) private var side: CGFloat = 48

    var body: some View {
        let favorite = item.userData?.isFavorite == true
        let played = item.userData?.played == true
        HStack(spacing: 8) {
            Button {
                update(favorite: !favorite)
            } label: {
                Image(systemName: favorite ? "heart.fill" : "heart")
                    .font(.body.weight(.bold))
                    .foregroundStyle(Palette.ink)
                    .contentTransition(.symbolEffect(.replace))
                    .frame(width: side, height: side)
                    .contentShape(.circle)
            }
            .buttonStyle(.plain)
            .glassEffect(.regular.interactive(), in: .circle)
            .accessibilityLabel(Text(favorite ? "In favorites" : "Favorite"))
            .accessibilityIdentifier("title.favorite")
            .accessibilityAddTraits(favorite ? .isSelected : [])
            Button {
                update(played: !played)
            } label: {
                Image(systemName: played ? "checkmark.circle.fill" : "checkmark")
                    .font(played ? .title3.weight(.bold) : .body.weight(.bold))
                    .foregroundStyle(Palette.ink)
                    .contentTransition(.symbolEffect(.replace))
                    .frame(width: side, height: side)
                    .contentShape(.circle)
            }
            .buttonStyle(.plain)
            .glassEffect(.regular.interactive(), in: .circle)
            .accessibilityLabel(Text(played ? "Watched" : "Mark as watched"))
            .accessibilityIdentifier("title.watched")
            .accessibilityAddTraits(played ? .isSelected : [])
        }
        .sensoryFeedback(.selection, trigger: touched)
    }

    private func update(played: Bool? = nil, favorite: Bool? = nil) {
        var data = item.userData ?? UserData()
        if let played {
            data.played = played
            if played { data.playbackPositionTicks = 0; data.playedPercentage = nil }
        }
        if let favorite { data.isFavorite = favorite }
        let target = item
        item.userData = data
        touched += 1
        Task { await app.set(target, played: played, favorite: favorite) }
    }
}

extension View {
    /// The long press of a card or an episode, as Mira web's: mark it watched or not, add it to favorites or not.
    func titleMenu(_ item: Item, inContinueWatching: Bool = false) -> some View { modifier(TitleMenu(item: item, inContinueWatching: inContinueWatching)) }
}

private struct TitleMenu: ViewModifier {
    let item: Item
    let inContinueWatching: Bool
    @Environment(AppModel.self) private var app

    func body(content: Content) -> some View {
        content.contextMenu {
            if inContinueWatching {
                // Until it is played again, on every device (its resume point goes too), as on Mira web.
                Button {
                    Task { await app.hideFromContinueWatching(item) }
                } label: {
                    Label("Remove from Continue watching", systemImage: "minus.circle")
                }
            }
            let played = item.userData?.played == true
            let favorite = item.userData?.isFavorite == true
            Button {
                Task { await app.set(item, played: !played) }
            } label: {
                Label(played ? "Mark as unwatched" : "Mark as watched", systemImage: played ? "eye.slash" : "checkmark.circle")
            }
            if !item.isEpisode {
                Button {
                    Task { await app.set(item, favorite: !favorite) }
                } label: {
                    Label(favorite ? "Remove from favorites" : "Add to favorites", systemImage: favorite ? "heart.slash" : "heart")
                }
            }
        }
    }
}

/// The sleep timer in « … » of a title's page: off, 15 min, 30 min, 1 h, or the end of the episode for an episode; its
/// label says what is set.
struct SleepPicker: View {
    let episode: Bool
    @Environment(AppModel.self) private var app

    var body: some View {
        let status = app.sleep.status(.now)
        Menu {
            Picker("Sleep timer", selection: Binding(get: { app.sleep.chosen ?? -1 }, set: { app.sleep.choose($0 < 0 ? nil : $0, now: .now) })) {
                Text("Timer off").tag(-1)
                ForEach(SleepTimer.minutes, id: \.self) { minutes in Text(verbatim: SleepTimer.duration(minutes)).tag(minutes) }
                if episode { Text("End of the episode").tag(0) }
            }
        } label: {
            Label(SleepStatus.label(status), systemImage: status == nil ? "moon.zzz" : "moon.zzz.fill")
        }
        .accessibilityIdentifier("title.sleep")
    }
}

/// The sleep timer once set, on the line of the languages: what it will do and the time left; a touch turns it off.
struct SleepStatus: View {
    @Environment(AppModel.self) private var app

    var body: some View {
        TimelineView(.periodic(from: .now, by: 20)) { context in
            if let status = app.sleep.status(context.date) {
                Button {
                    app.sleep.choose(nil, now: .now)
                } label: {
                    Label(Self.label(status), systemImage: "moon.zzz.fill").font(MiraFont.footnote).lineLimit(1)
                }
                .buttonStyle(.glass)
                .accessibilityLabel(Text("Sleep timer"))
                .accessibilityValue(Text(Self.label(status)))
                .accessibilityHint(Text("Turns the timer off"))
                .accessibilityIdentifier("title.sleepStatus")
            }
        }
    }

    static func label(_ status: SleepTimer.Status?) -> String {
        switch status {
        case nil: String(localized: "Sleep timer")
        case .endOfEpisode: String(localized: "Stop at the end of the episode")
        case let .pause(left): String(localized: "Pause in \(left)")
        }
    }
}
