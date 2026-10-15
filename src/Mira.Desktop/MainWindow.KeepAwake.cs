using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mira.Core;

namespace Mira.Desktop;

/// <summary>
/// This PC kept awake while another device plays from the Jellyfin on it (<see cref="ServerSleep.KeepAwake"/>), as long
/// as Mira runs, even minimized: read every minute from Jellyfin's sessions. Only idle sleep is held off: closing the
/// lid, or sleep asked for, still sleeps.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _awakeTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _keptAwake, _readingPlayback;
    /// <summary>The server last judged, and whether it runs on this PC.</summary>
    private (string Server, bool Local)? _awakeServer;

    private void StartKeepingAwake()
    {
        _awakeTimer.Tick += async (_, _) => await ReadRemotePlaybackAsync();
        _awakeTimer.Start();
    }

    private async Task ReadRemotePlaybackAsync()
    {
        if (_readingPlayback) return;
        _readingPlayback = true;
        var awake = false;
        try
        {
            if (!_demo && _client is { } client && Uri.TryCreate(client.Connection.Server, UriKind.Absolute, out var uri))
            {
                if (_awakeServer?.Server != client.Connection.Server)
                    _awakeServer = (client.Connection.Server, uri.IsLoopback || await Task.Run(() => LocalNetwork.IsThisPc(uri.Host)));
                if (_awakeServer.Value.Local) awake = ServerSleep.KeepAwake(await client.ViewersAsync());
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { }
        finally { _readingPlayback = false; }
        KeepAwake(awake);
    }

    /// <summary>
    /// From the interface's thread, which lives as long as Mira: Windows ties the request to the thread that made it,
    /// and drops it when Mira ends.
    /// </summary>
    private void KeepAwake(bool awake)
    {
        if (awake == _keptAwake) return;
        if (SetThreadExecutionState(awake ? EsContinuous | EsSystemRequired : EsContinuous) != 0) _keptAwake = awake;
    }

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
