using Mira.Core;
using Mira.Core.Tailscale;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

// Jellyfin silent at its address on the home network, as on Windows (MainWindow.ServerMove.cs of Mira.Desktop): looked
// for at another one by its id (ServerMove) before « Hors connexion » is said; found, the account moves there without
// asking for its password again, its cache, artwork and copies kept. Away from home, through Tailscale (the app's or
// « Tailscale intégré »), back to the home address as soon as it answers (each change of network, and at opening). The
// id of a session signed in before Mira kept it is read once Jellyfin answers.
public sealed partial class MainWindow
{
    private DateTimeOffset _moveSearched = DateTimeOffset.MinValue;
    private bool _moveSearching;
    /// <summary>Searches in a row that found nothing (<see cref="ServerMove.After"/>); none once Jellyfin answers.</summary>
    private int _moveFruitless;
    /// <summary>The session whose server id was asked (once each).</summary>
    private Session? _idAsked;

    /// <summary>
    /// True when Jellyfin was found at another address and this account opened there (what failed is then not said).
    /// Not while something plays (a title kept on this Mac), nor during the checks (their Jellyfin is a stand-in).
    /// </summary>
    private async Task<bool> FindMovedServerAsync(Session session)
    {
        if (InParty || Player is not null || IsCheckRun || _handler is not null || _moveSearching || !ServerMove.Searchable(session.Connection)
            || DateTimeOffset.UtcNow - _moveSearched < ServerMove.After(_moveFruitless)) return false;
        _moveSearching = true; _moveSearched = DateTimeOffset.UtcNow;
        try
        {
            var tailnet = await TailnetDevicesAsync();
            var moved = await ServerMove.FindAsync(session.Connection, tailnet: tailnet, ct: session.Lifetime);
            if (moved is null) _moveFruitless++;
            if (moved is null || !ReferenceEquals(session, Session) || Player is not null || InParty) return false;
            await OpenMovedAsync(session.Connection, moved);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (Errors.Expected(ex)) { return false; }
        finally { _moveSearching = false; }
    }

    /// <summary>Through Tailscale for now: back at the home address once it answers (full quality). Never while something plays.</summary>
    internal async Task ComeHomeAsync()
    {
        if (InParty || Player is not null || IsCheckRun || _handler is not null || _moveSearching || Session is not { Connection.Home: not null } session) return;
        _moveSearching = true;
        try
        {
            var back = await ServerMove.HomeAgainAsync(session.Connection, ct: session.Lifetime);
            if (back is null || !ReferenceEquals(session, Session) || Player is not null || InParty) return;
            await OpenMovedAsync(session.Connection, back);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        finally { _moveSearching = false; }
    }
    /// <summary>The account opened at the address found, kept for the next time; said when it is something the screen does not show.</summary>
    private async Task OpenMovedAsync(Connection before, Connection moved)
    {
        _moveFruitless = 0;
        await Task.Run(() => Profile.SaveConnection(moved));
        await ActivateAsync(moved);
        // Through Tailscale: the quality may be lower. Back home: nothing to say, it simply works as before.
        if (moved.Home is not null && before.Home is null) AddNotice(ServerMove.SaidAway);
        else if (moved.Home is null && before.Home is null) AddNotice(ServerMove.Said(before.Server, moved.Server));
    }
    /// <summary>The network changed (BuiltInTailscale hears it): searched again at once, and home again when it answers.</summary>
    internal void NetworkChanged()
    {
        _moveSearched = DateTimeOffset.MinValue; _moveFruitless = 0;
        if (Session?.Connection.Home is not null) _ = ComeHomeAsync();
    }
    /// <summary>The devices of the tailnet that may run Jellyfin: the Tailscale app's when it is connected here, otherwise « Tailscale intégré »'s.</summary>
    private async Task<IReadOnlyList<string>> TailnetDevicesAsync()
    {
        try
        {
            var (app, devices) = await Task.Run(() => (LocalNetwork.ThisPcOnTailnet() is not null, ServerSearch.TailnetDevices(TailscaleCli.Status())));
            if (!app && Tailnet.Current?.Node is { Helper: TailnetHelper.Running } node) devices = ServerSearch.NodeDevices(node.Status);
            return devices;
        }
        catch (Exception ex) when (Errors.Expected(ex) || ex is System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException) { return []; }
    }

    /// <summary>
    /// The server's id and this account's proof key (<see cref="ServerProof"/>), read once it answers and kept with the
    /// session (signed in before Mira kept them, or before the extension gave a key).
    /// </summary>
    private async Task LearnServerIdAsync(Session session)
    {
        if (ReferenceEquals(_idAsked, session) || InParty || _handler is not null || ServerMove.HomeAddress(session.Connection.Server) is null) return;
        _idAsked = session;
        try
        {
            if (await session.Client.LearnServerIdAsync(session.Lifetime) is not { } id || !ReferenceEquals(session, Session)) return;
            var key = await session.Client.LearnServerProofAsync(session.Lifetime);
            if (!ReferenceEquals(session, Session)) return;
            var connection = session.Connection;
            // Only over the same session: another account signed in meanwhile keeps its own.
            await Task.Run(() =>
            {
                if (Profile.LoadConnection() is { } saved && saved.Server == connection.Server && saved.UserId == connection.UserId && saved.Token == connection.Token)
                    Profile.SaveConnection(saved with { ServerId = id, ServerProof = key ?? saved.ServerProof });
            });
        }
        catch (Exception ex) when (Errors.Expected(ex) || ex is OperationCanceledException) { }
    }
}
