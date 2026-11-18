using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

// Jellyfin silent at its address on the home network, as on Windows (MainWindow.ServerMove.cs of Mira.Desktop): looked
// for at another one by its id (ServerMove) before « Hors connexion » is said; found, the account moves there without
// asking for its password again, its cache, artwork and copies kept. The id of a session signed in before Mira kept it
// is read once Jellyfin answers.
public sealed partial class MainWindow
{
    private DateTimeOffset _moveSearched = DateTimeOffset.MinValue;
    private bool _moveSearching;
    /// <summary>The session whose server id was asked (once each).</summary>
    private Session? _idAsked;

    /// <summary>
    /// True when Jellyfin was found at another address and this account opened there (what failed is then not said).
    /// Not while something plays (a title kept on this Mac), nor during the checks (their Jellyfin is a stand-in).
    /// </summary>
    private async Task<bool> FindMovedServerAsync(Session session)
    {
        if (InParty || Player is not null || IsCheckRun || _handler is not null || _moveSearching || !ServerMove.Movable(session.Connection)
            || DateTimeOffset.UtcNow - _moveSearched < ServerMove.Every) return false;
        _moveSearching = true; _moveSearched = DateTimeOffset.UtcNow;
        try
        {
            var moved = await ServerMove.FindAsync(session.Connection, ct: session.Lifetime);
            if (moved is null || !ReferenceEquals(session, Session) || Player is not null || InParty) return false;
            var before = session.Connection.Server;
            await Task.Run(() => Profile.SaveConnection(moved));
            await ActivateAsync(moved);
            AddNotice(ServerMove.Said(before, moved.Server));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (Errors.Expected(ex)) { return false; }
        finally { _moveSearching = false; }
    }

    /// <summary>The server's id, read once it answers and kept with the session (signed in before Mira kept it).</summary>
    private async Task LearnServerIdAsync(Session session)
    {
        if (ReferenceEquals(_idAsked, session) || InParty || _handler is not null || ServerMove.HomeAddress(session.Connection.Server) is null) return;
        _idAsked = session;
        try
        {
            if (await session.Client.LearnServerIdAsync(session.Lifetime) is not { } id || !ReferenceEquals(session, Session)) return;
            var connection = session.Connection;
            // Only over the same session: another account signed in meanwhile keeps its own.
            await Task.Run(() =>
            {
                if (Profile.LoadConnection() is { } saved && saved.Server == connection.Server && saved.UserId == connection.UserId && saved.Token == connection.Token)
                    Profile.SaveConnection(saved with { ServerId = id });
            });
        }
        catch (Exception ex) when (Errors.Expected(ex) || ex is OperationCanceledException) { }
    }
}
