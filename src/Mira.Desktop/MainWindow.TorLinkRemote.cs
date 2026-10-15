using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.TorLink;

namespace Mira.Desktop;

/// <summary>
/// « Télécharger sur le PC serveur »: on the PC of Jellyfin, Mira takes the searches and downloads that Jellyfin's
/// administrators send from Mira web, through the relay of the extension (<c>Mira/torlink</c>), and runs them with its
/// TorLink: a search by « torlnk search », a download by TorLink without a window (<see cref="TorLinkHeadless"/>),
/// placed in the library like any other. The page's TorLink hands over when nobody is at it, and takes back when it is
/// shown: never two TorLink on the same queue.
/// </summary>
public partial class MainWindow
{
    private readonly TorLinkHeadless _torlinkHeadless = new();
    private readonly DispatcherTimer _remoteTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly List<AgentSearchOutcome> _remoteSearchOutcomes = [];
    private readonly List<AgentOutcome> _remoteRequestOutcomes = [], _remoteControlOutcomes = [];
    private readonly HashSet<string> _remoteSearching = [];
    private bool _remoteVisiting, _remoteAgain;
    /// <summary>What Réglages → TorLink says of it.</summary>
    private string _remoteState = "";
    /// <summary>The page's TorLink handed its queue over (<see cref="StartHeadlessAsync"/>), shown again when TorLink without a window stops.</summary>
    private bool _torlinkHandedOff;
    /// <summary>When TorLink without a window last had nothing to download nor share.</summary>
    private DateTime? _headlessIdleSince;
    /// <summary>A TorLink outside Mira was found when one was needed: said for a minute, then looked for again.</summary>
    private DateTime _remoteElsewhereAt = DateTime.MinValue;
    /// <summary>Nobody has touched this PC for this long: TorLink's page in front is not being used.</summary>
    private static readonly TimeSpan NobodyAtScreen = TimeSpan.FromMinutes(2);

    private void StartTorLinkRemote()
    {
        _remoteTimer.Tick += async (_, _) => await VisitRelayAsync();
        _remoteTimer.Start();
    }

    /// <summary>Back to the relay at once (a search finished, an option changed).</summary>
    private void VisitRelaySoon()
    {
        if (_remoteVisiting) { _remoteAgain = true; return; }
        _remoteTimer.Stop(); _remoteTimer.Interval = TimeSpan.FromMilliseconds(300); _remoteTimer.Start();
    }

    private async Task VisitRelayAsync()
    {
        if (_remoteVisiting) return;
        _remoteVisiting = true; _remoteAgain = false;
        var next = TimeSpan.FromSeconds(30);
        try { next = await VisitRelayCoreAsync(); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            next = TimeSpan.FromSeconds(15);
        }
        finally
        {
            _remoteVisiting = false;
            if (!_closing)
            {
                _remoteTimer.Stop(); _remoteTimer.Interval = _remoteAgain ? TimeSpan.FromMilliseconds(300) : next; _remoteTimer.Start();
            }
            if (SettingsOverlay.Visibility == Visibility.Visible) DescribeTorLinkRemote();
        }
    }

    /// <summary>One visit: the report, then the work it brings back. Returns when to come back.</summary>
    private async Task<TimeSpan> VisitRelayCoreAsync()
    {
        await TidyHeadlessAsync();
        if (!_torlinkEnabled || _demo || _closing || _client is not { } client || _torlink is not { } torlink) { _remoteState = ""; return TimeSpan.FromSeconds(30); }
        if (!await JellyfinOnThisPcAsync(client))
        {
            _remoteState = "Jellyfin ne tourne pas sur ce PC : pour lancer un téléchargement sur le PC serveur, ouvre Mira web dans ton navigateur (bouton ci-dessous).";
            return TimeSpan.FromMinutes(5);
        }
        if (!_settings.TorLinkRemote) { _remoteState = ""; return TimeSpan.FromSeconds(30); }
        // TorLink off: still said to Mira web, which explains it rather than « Mira n'est pas ouvert ».
        var active = TorLinkActive();
        var installation = active ? _torlinkInstallation ??= TorLinkInstallation.Locate(_settings.TorLinkPath, _profile.DirectoryPath) : null;
        var ct = _connectionLifetime.Token;

        var problem = TorLinkRemote.Problem(installation is { HasRuntime: true }, installation is { Detected: false },
            DateTime.UtcNow - _remoteElsewhereAt < TimeSpan.FromMinutes(1), !_torlinkHeadless.Running && TorLinkInUse());
        var (searches, requests, controls) = (_remoteSearchOutcomes.ToList(), _remoteRequestOutcomes.ToList(), _remoteControlOutcomes.ToList());
        var report = new AgentReport(Environment.MachineName, problem is null, problem, await RemoteDownloadsAsync(ct), TorLinkRemote.Placed(torlink.Importer.Entries), searches, requests, controls);
        var (work, status) = await client.TorLinkExchangeAsync(report, ct);
        if (!ReferenceEquals(client, _client)) return TimeSpan.FromSeconds(5);
        if (work is null)
        {
            _remoteState = status == HttpStatusCode.Forbidden
                ? "Ton compte Jellyfin n’est pas administrateur : tes autres appareils ne peuvent pas envoyer de téléchargement ici."
                : "L’extension Mira de ton serveur est plus ancienne que ce Mira : elle se met à jour avec lui, au prochain redémarrage de Jellyfin.";
            return TimeSpan.FromMinutes(5);
        }
        // Delivered: what the relay has now is not sent again.
        _remoteSearchOutcomes.RemoveAll(searches.Contains); _remoteRequestOutcomes.RemoveAll(requests.Contains); _remoteControlOutcomes.RemoveAll(controls.Contains);
        if (work.Busy is { } other) { _remoteState = $"Un autre Mira reçoit déjà les téléchargements de tes appareils : {other}."; return TimeSpan.FromSeconds(Math.Clamp(work.Next, 10, 120)); }
        _remoteState = !active ? "Active TorLink dans la page Téléchargements : tes autres appareils pourront y lancer un téléchargement."
            : problem ?? (_torlinkHeadless.Running ? "Prêt. TorLink travaille en ce moment sans fenêtre, pour un autre appareil." : "Prêt : tes autres appareils peuvent lancer un téléchargement ici.");

        foreach (var search in work.Searches) _ = RunRemoteSearchAsync(installation, search);
        if (work.Requests.Count > 0 || work.Controls.Count > 0) { await RunRemoteDownloadsAsync(installation, work.Requests, work.Controls); _remoteAgain = true; }
        return TimeSpan.FromSeconds(Math.Clamp(work.Next, 1, 60));
    }

    /// <summary>TorLink's page is in front, and someone used this PC lately: it may be in use, it is not taken away.</summary>
    private bool TorLinkInUse() => TorLinkOverlay.Visibility == Visibility.Visible && WindowState != WindowState.Minimized && IsActive && UserIdle() < NobodyAtScreen;

    /// <summary>TorLink's downloads: live from TorLink without a window, otherwise from the queue it last saved.</summary>
    private async Task<List<RelayDownload>> RemoteDownloadsAsync(CancellationToken ct)
    {
        if (_torlinkHeadless.Client is { } serve)
        {
            try { return TorLinkRemote.ParseDownloads(await serve.DownloadsAsync(ct)); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        var state = _torlinkState; var running = _torlinkTerminal?.Running == true;
        return await Task.Run(() => state.ReadQueue() is { } queue ? TorLinkRemote.ParseQueue(queue, running) : []);
    }

    private async Task RunRemoteSearchAsync(TorLinkInstallation? installation, WorkSearch search)
    {
        if (!_remoteSearching.Add(search.Id)) return;
        try
        {
            var query = RemoteDownloads.CleanQuery(search.Query);
            var (hits, error) = query is null || !RemoteDownloads.IsCategory(search.Category) ? ([], "Recherche refusée.")
                : installation is not { HasRuntime: true, Detected: false } ? ([], "TorLink n’est pas prêt sur le PC serveur.")
                : await _torlinkHeadless.SearchAsync(installation, query, search.Category, _torlinkLifetime.Token);
            _remoteSearchOutcomes.Add(new(search.Id, error is null ? "done" : "failed", hits, error));
            VisitRelaySoon();
        }
        catch (OperationCanceledException) { }
        finally { _remoteSearching.Remove(search.Id); }
    }

    /// <summary>Downloads into TorLink without a window, started for them; controls on downloads it has under way.</summary>
    private async Task RunRemoteDownloadsAsync(TorLinkInstallation? installation, List<WorkRequest> requests, List<WorkControl> controls)
    {
        var ct = _torlinkLifetime.Token;
        if (await StartHeadlessAsync(installation) is { } refused)
        {
            _remoteRequestOutcomes.AddRange(requests.Select(x => new AgentOutcome(x.Id, "failed", refused)));
            _remoteControlOutcomes.AddRange(controls.Select(x => new AgentOutcome(x.Id, "failed", refused)));
            return;
        }
        var serve = _torlinkHeadless.Client!;
        foreach (var request in requests)
        {
            if (RemoteDownloads.InfoHash(request.Magnet) is null) { _remoteRequestOutcomes.Add(new(request.Id, "failed", "Ce n’est pas un lien magnet.")); continue; }
            try
            {
                var (outcome, error) = await serve.AddAsync(request.Magnet, ct);
                _remoteRequestOutcomes.Add(new(request.Id, outcome is null ? "failed" : "sent", outcome == "duplicate" ? "TorLink l’avait déjà." : error));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { _remoteRequestOutcomes.Add(new(request.Id, "failed", "TorLink n’a pas répondu.")); }
        }
        if (controls.Count == 0) return;
        try
        {
            // Only a download under way: a finished one is in the library, never deleted from here.
            var current = TorLinkRemote.ParseDownloads(await serve.DownloadsAsync(ct)).Select(x => x.Id).ToHashSet();
            foreach (var control in controls)
                _remoteControlOutcomes.Add(!RemoteDownloads.IsInfoHash(control.Target) || !RemoteDownloads.IsAction(control.Action) ? new(control.Id, "failed", "Demande inconnue.")
                    : !current.Contains(control.Target) ? new(control.Id, "failed", "Ce téléchargement n’est plus en cours.")
                    : await serve.ControlAsync(control.Target, control.Action, ct) ? new(control.Id, "done", null) : new(control.Id, "failed", "TorLink n’a plus ce téléchargement."));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _remoteControlOutcomes.AddRange(controls.Where(x => !_remoteControlOutcomes.Any(o => o.Id == x.Id)).Select(x => new AgentOutcome(x.Id, "failed", "TorLink n’a pas répondu.")));
        }
        _headlessIdleSince = null;
    }

    /// <summary>
    /// TorLink without a window, running and answering; null when it is, otherwise why not. The page's TorLink hands
    /// over first when nobody is at it (its queue saved, started again when the page shows).
    /// </summary>
    private async Task<string?> StartHeadlessAsync(TorLinkInstallation? installation)
    {
        if (_torlinkHeadless.Client is not null) return null;
        if (installation is not { HasRuntime: true, Detected: false }) return TorLinkRemote.Problem(installation is { HasRuntime: true }, false, false, false);
        if (TorLinkInUse()) return TorLinkRemote.Problem(true, true, false, true);
        if (_torlinkTerminal?.Running == true)
        {
            await _torlinkTerminal.StopAsync(idle: true);
            _torlinkHandedOff = true;
            if (TorLinkOverlay.Visibility == Visibility.Visible) SetTorLinkBlocker("remote");
        }
        if (await Task.Run(() => installation.RunningOutside(_torlinkHeadless.ProcessIds)) is not null)
        {
            _remoteElsewhereAt = DateTime.UtcNow;
            return TorLinkRemote.Problem(true, true, true, false);
        }
        installation = await TakePreparedTorLinkAsync(installation);
        PrepareTorLinkStart();
        if (!await _torlinkHeadless.StartAsync(installation, _torlinkLifetime.Token)) { await HeadlessStoppedAsync(); return _torlinkHeadless.Problem ?? "TorLink n’a pas pu démarrer sur le PC serveur."; }
        _headlessIdleSince = null; UpdateTorLinkState();
        _ = RefreshTorLinkAsync(installation);
        return null;
    }

    /// <summary>
    /// While TorLink runs without a window: the downloads moved into the library stop being shared (what PrepareStart
    /// does while TorLink is closed), and it stops once nothing has downloaded nor been shared for a while.
    /// </summary>
    private async Task TidyHeadlessAsync()
    {
        if (_torlinkHeadless.Client is not { } serve)
        {
            // Ended on its own (an error): its port and token go with it.
            if (_torlinkHeadless.Ended) { await _torlinkHeadless.StopAsync(); await HeadlessStoppedAsync(); }
            return;
        }
        var ct = _torlinkLifetime.Token;
        try
        {
            if (_torlink?.Importer is { } importer && importer.MovedSeeds() is { Count: > 0 } moved)
            {
                foreach (var id in moved) await serve.ControlAsync(id, "stop-seed", ct);
                importer.MarkSeedPaused(moved);
            }
            var working = TorLinkRemote.Working(await serve.DownloadsAsync(ct)) || _remoteRequestOutcomes.Count > 0;
            if (working) _headlessIdleSince = null;
            else if (_headlessIdleSince is null) _headlessIdleSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - _headlessIdleSince >= TorLinkRemote.IdleStop) { await _torlinkHeadless.StopAsync(); await HeadlessStoppedAsync(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { }
    }

    /// <summary>Downloading for another device: this PC stays awake (only idle sleep is held off).</summary>
    private bool HeadlessDownloading() => _torlinkHeadless.Running && _headlessIdleSince is null;

    /// <summary>Before the page's TorLink starts: TorLink without a window hands its queue back.</summary>
    private async Task TakeBackFromHeadlessAsync()
    {
        _torlinkHandedOff = false;
        if (!_torlinkHeadless.Running && !_torlinkHeadless.Ended) return;
        await _torlinkHeadless.StopAsync();
        _headlessIdleSince = null;
        UpdateTorLinkState();
    }

    /// <summary>
    /// TorLink without a window stopped (nothing left to do, or an error): the page's TorLink, which handed its queue over,
    /// opens again where it was shown; otherwise at the next visit of the page, as usual.
    /// </summary>
    private async Task HeadlessStoppedAsync()
    {
        _headlessIdleSince = null;
        var handedOff = _torlinkHandedOff; _torlinkHandedOff = false;
        if (_closing) return;
        if (handedOff && TorLinkOverlay.Visibility == Visibility.Visible && TorLinkActive()) await StartTorLinkAsync(restart: true);
        else { if (_torlinkBlocker == "remote") _torlinkBlocker = null; UpdateTorLinkState(); }
    }

    private void DescribeTorLinkRemote()
    {
        var elsewhere = _client is not null && !_demo && _awakeServer is { Local: false } known && known.Server == _client.Connection.Server;
        TorLinkRemoteStatus.Text = _settings.TorLinkRemote || elsewhere ? _remoteState : "";
        TorLinkRemoteStatus.Visibility = TorLinkRemoteStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TorLinkRemoteOpenButton.Visibility = elsewhere ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Mira web's « Télécharger sur le PC serveur », in the browser, from a PC that is not the server.</summary>
    private void TorLinkRemoteOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client) return;
        try { Process.Start(new ProcessStartInfo(new Uri(new Uri(client.Connection.Server), "Mira/#/telecharger").AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { SetNotice("Le navigateur n’a pas pu s’ouvrir."); }
    }

    private static TimeSpan UserIdle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
