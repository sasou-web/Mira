using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// The graphics card Jellyfin converts with, tried again once Jellyfin or a card's driver has changed
/// (<see cref="GpuRecheck"/>): two minutes after the start, then every half hour while Mira runs, for an administrator
/// of a Jellyfin on this PC (Mira reads this PC's drivers), never while anyone watches. Two failed tries put Jellyfin
/// back on the processor, and Mira says so.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _gpuRecheckTimer = new() { Interval = GpuRecheck.FirstLook };
    private bool _gpuRechecking;

    private void StartGpuRecheck()
    {
        _gpuRecheckTimer.Tick += async (_, _) => { _gpuRecheckTimer.Interval = GpuRecheck.Retry; await RecheckGpuAsync(); };
        _gpuRecheckTimer.Start();
    }

    private async Task RecheckGpuAsync()
    {
        if (_gpuRechecking || _gpuBusy || _demo || _client is not { } client) return;
        _gpuRechecking = true;
        var ct = _connectionLifetime.Token;
        try
        {
            var server = new Uri(client.Connection.Server);
            if (!server.IsLoopback && !await Task.Run(() => LocalNetwork.IsThisPc(server.Host))) return;
            // Read first: an account that is not an administrator, or a Jellyfin on the processor, has nothing to try.
            if (await client.HardwareAccelerationAsync(ct) is not { } kind || kind == GpuEncoding.None) return;
            var fingerprint = GpuRecheck.Fingerprint(await client.ServerVersionAsync(ct), await Task.Run(GraphicsCards.Drivers));
            // Nothing changed since the last try that converted: Jellyfin's sessions are not even read.
            if (fingerprint is null || fingerprint == _settings.GpuCheckedFor) return;
            if (GpuRecheck.Decide(kind, fingerprint, _settings.GpuCheckedFor, watching: (await client.ViewersAsync(ct)).Count > 0) != GpuRecheck.Step.Try) return;
            // Clicked meanwhile (« Utiliser la carte graphique » tries on its own, « Revenir au processeur »): theirs.
            if (_gpuBusy) return;
            _gpuBusy = true;
            bool? converted;
            try { converted = await client.ConversionWorksAsync(TimeSpan.FromSeconds(30), ct: ct); }
            finally { _gpuBusy = false; }
            if (!ReferenceEquals(client, _client)) return;
            var (verdict, failures) = GpuRecheck.After(converted, _settings.GpuFailures);
            _settings.GpuFailures = failures;
            if (verdict == GpuRecheck.Verdict.Kept) _settings.GpuCheckedFor = fingerprint!;
            else if (verdict == GpuRecheck.Verdict.Processor && await client.SetHardwareAccelerationAsync(GpuEncoding.None, ct))
            {
                _settings.GpuCheckedFor = "";
                AddNotice(GpuRecheck.Notice(kind));
                if (SettingsOverlay.Visibility == System.Windows.Visibility.Visible) _ = ShowGpuAsync();
            }
            TrySaveSettings();
        }
        // Only Mira's own cancellation (another account, closing); anything else is tried again at the next look.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { }
        finally { _gpuRechecking = false; }
    }

    /// <summary>A card just kept by « Utiliser la carte graphique » holds for this Jellyfin and these drivers: not tried again.</summary>
    private async Task RememberGpuCheckedAsync(JellyfinClient client)
    {
        try
        {
            if (GpuRecheck.Fingerprint(await client.ServerVersionAsync(), await Task.Run(GraphicsCards.Drivers)) is { } fingerprint)
            {
                _settings.GpuCheckedFor = fingerprint; _settings.GpuFailures = 0; TrySaveSettings();
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
}
