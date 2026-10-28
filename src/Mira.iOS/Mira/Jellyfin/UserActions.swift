import Foundation

extension JellyfinClient {
    /// Marks a title (a film, an episode, a whole season or series) watched or not, as Mira web does; Jellyfin answers
    /// with the title's new state.
    @discardableResult
    func setPlayed(_ itemId: String, _ played: Bool) async throws -> UserData? {
        let data = try await send(played ? "POST" : "DELETE", "UserPlayedItems/\(itemId)", ["userId": userId])
        return try? JSON.decoder.decode(UserData.self, from: data)
    }

    /// Adds a title to the account's favorites or takes it out.
    @discardableResult
    func setFavorite(_ itemId: String, _ favorite: Bool) async throws -> UserData? {
        let data = try await send(favorite ? "POST" : "DELETE", "UserFavoriteItems/\(itemId)", ["userId": userId])
        return try? JSON.decoder.decode(UserData.self, from: data)
    }
}
