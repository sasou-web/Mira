using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.Tailscale;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// Jellyfin silent at its address on the home network: looked for at another one, by its id (<see cref="ServerMove"/>),
/// before « Hors connexion » is said; found, the account moves there without asking for its password again, its cache,
/// artwork and copies kept. Away from home, through Tailscale (the app's or « Tailscale intégré »), as the iPhone does,
/// back to the home address as soon as it answers again (each change of network, and once at opening). The id of a
/// session signed in before Mira kept it is read once Jellyfin answers.
/// </summary>
public partial class MainWindow
{
    private DateTimeOffset _moveSearched = DateTimeOffset.MinValue;
    private bool _moveSearching;
    /// <summary>Searches in a row that found nothing (<see cref="ServerMove.After"/>); none once Jellyfin answers.</summary>
    private int _moveFruitless;

    /// <summary>
    /// True when Jellyfin was found at another address and this account opened there (what failed is then not said).
    /// Not while something plays (a title kept on this PC): looked for at the next refresh that fails instead.
    /// </summary>
    private async Task<bool> FindMovedServerAsync(JellyfinClient client)
    {
        if (_demo || InParty || _playing || _moveSearching || !ServerMove.Searchable(client.Connection) || DateTimeOffset.UtcNow - _moveSearched < ServerMove.After(_moveFruitless)) return false;
        _moveSearching = true; _moveSearched = DateTimeOffset.UtcNow;
        try
        {
            var lifetime = _connectionLifetime.Token;
            var tailnet = await TailnetDevicesAsync();
            var moved = await ServerMove.FindAsync(client.Connection, tailnet: tailnet, ct: lifetime);
            if (moved is null) _moveFruitless++;
            if (moved is null || !ReferenceEquals(client, _client) || _playing || InParty) return false;
            await OpenMovedAsync(client.Connection, moved);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
        finally { _moveSearching = false; }
    }

    /// <summary>
    /// Through Tailscale for now: back at the home address once it answers (full quality). Each change of network, and
    /// once as the account opens; never while something plays.
    /// </summary>
    private async Task ComeHomeAsync()
    {
        if (_demo || InParty || _playing || _moveSearching || _client is not { Connection.Home: not null } client) return;
        _moveSearching = true;
        try
        {
            var back = await ServerMove.HomeAgainAsync(client.Connection, ct: _connectionLifetime.Token);
            if (back is null || !ReferenceEquals(client, _client) || _playing || InParty) return;
            await OpenMovedAsync(client.Connection, back);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex)) { }
        finally { _moveSearching = false; }
    }
    /// <summary>The account opened at the address found, kept for the next time; said when it is something the screen does not show.</summary>
    private async Task OpenMovedAsync(Connection before, Connection moved)
    {
        _moveFruitless = 0;
        _profile.SaveConnection(moved);
        await ActivateConnectionAsync(moved);
        // Through Tailscale: the quality may be lower. Back home: nothing to say, it simply works as before.
        if (moved.Home is not null && before.Home is null) AddNotice(ServerMove.SaidAway);
        else if (moved.Home is null && before.Home is null) AddNotice(ServerMove.Said(before.Server, moved.Server));
    }
    /// <summary>The devices of the tailnet that may run Jellyfin: the Tailscale app's when it is connected here, otherwise « Tailscale intégré »'s.</summary>
    private async Task<IReadOnlyList<string>> TailnetDevicesAsync()
    {
        try
        {
            var (app, devices) = await Task.Run(() => LocalNetwork.ThisPcOnTailnet() is null ? (false, (IReadOnlyList<string>)[]) : (true, ServerSearch.TailnetDevices(TailscaleCli.Status())));
            if (!app && _tailnet?.Node is { Helper: TailnetHelper.Running } node) devices = ServerSearch.NodeDevices(node.Status);
            return devices;
        }
        catch (Exception ex) when (IsExpected(ex) || ex is SocketException or NetworkInformationException) { return []; }
    }

    /// <summary>
    /// The network changed (another Wi-Fi, a cable, back home), as the Mac does: « Tailscale intégré » looks at it afresh,
    /// Jellyfin is asked again at once when it was silent, and an account through Tailscale goes back to its home address
    /// when it answers. A burst of changes counts once, 2 s after the last.
    /// </summary>
    private readonly DispatcherTimer _networkSettled = new() { Interval = TimeSpan.FromSeconds(2) };
    private void WatchNetwork()
    {
        _networkSettled.Tick += (_, _) =>
        {
            _networkSettled.Stop();
            if (_closing) return;
            _ = RebindTailnetAsync();
            _moveSearched = DateTimeOffset.MinValue; _moveFruitless = 0;
            if (_client?.Connection.Home is not null) _ = ComeHomeAsync();
            else if (_client is not null && _offlineSince is not null) _ = RefreshAsync(quiet: true);
        };
        try { NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(() => { _networkSettled.Stop(); _networkSettled.Start(); }); }
        catch (PlatformNotSupportedException) { }
    }

    /// <summary>A refresh found Jellyfin silent: at another address, it opens there; otherwise said once, as it stops answering.</summary>
    private async Task UnansweredAsync(JellyfinClient client, Exception error, bool wasOffline)
    {
        if (await FindMovedServerAsync(client) || wasOffline || !ReferenceEquals(client, _client)) return;
        await SayUnansweredAsync(client, error, Friendly(error) + L(" Les informations déjà chargées restent disponibles.", " What’s already loaded stays available."));
    }

    /// <summary>
    /// The server's id and this account's proof key (<see cref="ServerProof"/>), read once it answers and kept with the
    /// session (signed in before Mira kept them, or before the extension gave a key).
    /// </summary>
    private async Task LearnServerIdAsync(JellyfinClient client)
    {
        if (_demo || InParty || client.Connection is { ServerId.Length: > 0, ServerProof.Length: > 0 } || ServerMove.HomeAddress(client.Connection.Server) is null) return;
        try
        {
            if (await client.LearnServerIdAsync(_connectionLifetime.Token) is null || !ReferenceEquals(client, _client)) return;
            var key = await client.LearnServerProofAsync(_connectionLifetime.Token);
            if (!ReferenceEquals(client, _client)) return;
            // Only over the same session: another account signed in meanwhile keeps its own.
            if (_profile.LoadConnection() is { } saved && saved.Server == client.Connection.Server && saved.UserId == client.Connection.UserId && saved.Token == client.Connection.Token)
                _profile.SaveConnection(saved with { ServerId = client.Connection.ServerId, ServerProof = key ?? saved.ServerProof });
        }
        catch (Exception ex) when (IsExpected(ex) || ex is OperationCanceledException) { }
    }
}
