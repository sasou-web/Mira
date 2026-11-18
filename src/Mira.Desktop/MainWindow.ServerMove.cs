using Mira.Core;

namespace Mira.Desktop;

/// <summary>
/// Jellyfin silent at its address on the home network: looked for at another one, by its id (<see cref="ServerMove"/>),
/// before « Hors connexion » is said; found, the account moves there without asking for its password again, its cache,
/// artwork and copies kept. The id of a session signed in before Mira kept it is read once Jellyfin answers.
/// </summary>
public partial class MainWindow
{
    private DateTimeOffset _moveSearched = DateTimeOffset.MinValue;
    private bool _moveSearching;

    /// <summary>
    /// True when Jellyfin was found at another address and this account opened there (what failed is then not said).
    /// Not while something plays (a title kept on this PC): looked for at the next refresh that fails instead.
    /// </summary>
    private async Task<bool> FindMovedServerAsync(JellyfinClient client)
    {
        if (_demo || InParty || _playing || _moveSearching || !ServerMove.Movable(client.Connection) || DateTimeOffset.UtcNow - _moveSearched < ServerMove.Every) return false;
        _moveSearching = true; _moveSearched = DateTimeOffset.UtcNow;
        try
        {
            var lifetime = _connectionLifetime.Token;
            var moved = await ServerMove.FindAsync(client.Connection, ct: lifetime);
            if (moved is null || !ReferenceEquals(client, _client) || _playing || InParty) return false;
            var before = client.Connection.Server;
            _profile.SaveConnection(moved);
            await ActivateConnectionAsync(moved);
            AddNotice(ServerMove.Said(before, moved.Server));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
        finally { _moveSearching = false; }
    }

    /// <summary>A refresh found Jellyfin silent: at another address, it opens there; otherwise said once, as it stops answering.</summary>
    private async Task UnansweredAsync(JellyfinClient client, Exception error, bool wasOffline)
    {
        if (await FindMovedServerAsync(client) || wasOffline || !ReferenceEquals(client, _client)) return;
        await SayUnansweredAsync(client, error, Friendly(error) + L(" Les informations déjà chargées restent disponibles.", " What’s already loaded stays available."));
    }

    /// <summary>The server's id, read once it answers and kept with the session (signed in before Mira kept it).</summary>
    private async Task LearnServerIdAsync(JellyfinClient client)
    {
        if (_demo || InParty || client.Connection.ServerId is { Length: > 0 } || ServerMove.HomeAddress(client.Connection.Server) is null) return;
        try
        {
            if (await client.LearnServerIdAsync(_connectionLifetime.Token) is null || !ReferenceEquals(client, _client)) return;
            // Only over the same session: another account signed in meanwhile keeps its own.
            if (_profile.LoadConnection() is { } saved && saved.Server == client.Connection.Server && saved.UserId == client.Connection.UserId && saved.Token == client.Connection.Token)
                _profile.SaveConnection(saved with { ServerId = client.Connection.ServerId });
        }
        catch (Exception ex) when (IsExpected(ex) || ex is OperationCanceledException) { }
    }
}
