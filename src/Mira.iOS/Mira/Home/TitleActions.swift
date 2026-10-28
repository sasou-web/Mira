import SwiftUI

/// Favorite and Watched on a title's page: two buttons that say the title's state and change it at once (Jellyfin is
/// told, then every page reads it again).
struct TitleActions: View {
    @Binding var item: Item
    @Environment(AppModel.self) private var app

    var body: some View {
        let favorite = item.userData?.isFavorite == true
        let played = item.userData?.played == true
        HStack(spacing: 10) {
            Button {
                update(favorite: !favorite)
            } label: {
                Label(favorite ? "In favorites" : "Favorite", systemImage: favorite ? "heart.fill" : "heart").font(MiraFont.callout)
            }
            .buttonStyle(.glass)
            .accessibilityIdentifier("title.favorite")
            .accessibilityAddTraits(favorite ? .isSelected : [])
            Button {
                update(played: !played)
            } label: {
                Label(played ? "Watched" : "Mark as watched", systemImage: played ? "checkmark.circle.fill" : "checkmark.circle").font(MiraFont.callout)
            }
            .buttonStyle(.glass)
            .accessibilityIdentifier("title.watched")
            .accessibilityAddTraits(played ? .isSelected : [])
        }
        .sensoryFeedback(.selection, trigger: item.userData)
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
        Task { await app.set(target, played: played, favorite: favorite) }
    }
}

extension View {
    /// The long press of a card or an episode, as Mira web's: mark it watched or not, add it to favorites or not.
    func titleMenu(_ item: Item) -> some View { modifier(TitleMenu(item: item)) }
}

private struct TitleMenu: ViewModifier {
    let item: Item
    @Environment(AppModel.self) private var app

    func body(content: Content) -> some View {
        content.contextMenu {
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
