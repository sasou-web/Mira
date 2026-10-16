using System.IO;
using System.Net;
using System.Net.Http;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// A Jellyfin on this PC that stops answering, told as such rather than as a far server: stopped, with « Démarrer
/// Jellyfin » (Windows' consent for its service, none for its program), starting, or stopping after each start, with
/// its log (<see cref="LocalJellyfin"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>Starts in a row that Jellyfin did not answer after; back to 0 once it answers.</summary>
    private int _jellyfinFailedStarts;
    private bool _startingJellyfin;
    /// <summary>The notice about Jellyfin on this PC, replaced (not added to) once it answers again.</summary>
    private string? _localJellyfinNotice;

    /// <summary>No answer at all, or Jellyfin's own « loading » (503): what a stopped or starting Jellyfin gives.</summary>
    private static bool Unanswered(Exception ex) => ex is OperationCanceledException or HttpRequestException { StatusCode: null or HttpStatusCode.ServiceUnavailable };

    /// <summary>What a Jellyfin at <paramref name="server"/> that does not answer is: Unknown for another computer's.</summary>
    private Task<(LocalJellyfin.Verdict Verdict, bool Service)> ReadLocalJellyfinAsync(string server)
    {
        var failed = _jellyfinFailedStarts;
        return Task.Run(() =>
        {
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || !uri.IsLoopback && !LocalNetwork.IsThisPc(uri.Host)) return (LocalJellyfin.Verdict.Unknown, false);
            var service = JellyfinAutostart.ServiceState();
            var none = service == LocalJellyfin.Service.None;
            return (LocalJellyfin.Judge(service, none && JellyfinAutostart.ProgramRunning(), !none || JellyfinAutostart.Program() is not null, failed), !none);
        });
    }

    /// <summary>
    /// Said as Jellyfin stops answering (<paramref name="error"/>), or still does not: for one on this PC, what it is
    /// and its button; otherwise <paramref name="otherwise"/>, Mira's usual message.
    /// </summary>
    private async Task SayUnansweredAsync(JellyfinClient client, Exception? error, string otherwise)
    {
        var (verdict, service) = error is null || Unanswered(error) ? await ReadLocalJellyfinAsync(client.Connection.Server) : (LocalJellyfin.Verdict.Unknown, false);
        if (!ReferenceEquals(client, _client)) return;
        var cause = verdict == LocalJellyfin.Verdict.Failing ? await Task.Run(() => JellyfinLog.LastFatal(JellyfinLogFolder, DateTimeOffset.Now.AddDays(-1))) : null;
        if (LocalJellyfin.Says(verdict, service, cause) is not { } said) { SetNotice(otherwise); return; }
        Action? act = said.Action == LocalJellyfin.Start ? () => _ = StartJellyfinAsync(client) : said.Action == LocalJellyfin.OpenLog ? OpenJellyfinLog : null;
        _localJellyfinNotice = said.Text;
        SetNotice(said.Text, said.Action, act);
    }

    private static string JellyfinLogFolder => Path.Combine(JellyfinInstaller.DataFolder(), "log");
    /// <summary>Its log folder: once read, the next notice offers to start it again.</summary>
    private void OpenJellyfinLog()
    {
        _jellyfinFailedStarts = 0;
        if (Directory.Exists(JellyfinLogFolder)) OpenInExplorer(JellyfinLogFolder, null);
        else SetNotice($"Le journal de Jellyfin n’est pas dans {JellyfinLogFolder}.");
    }

    /// <summary>
    /// « Démarrer Jellyfin »: its service through Windows' consent, or its program; then Jellyfin has three minutes to
    /// answer, as after its installation. Stopped again meanwhile, or silent, it counts as a failed start.
    /// </summary>
    private async Task StartJellyfinAsync(JellyfinClient client)
    {
        if (_startingJellyfin) return;
        _startingJellyfin = true;
        try
        {
            var service = await Task.Run(() => JellyfinAutostart.ServiceState()) is not LocalJellyfin.Service.None;
            if (service) SetNotice("Accepte l’autorisation de Windows, pour Windows PowerShell…", null, null, sticky: true);
            var started = service ? await JellyfinAutostart.StartServiceAsync() : await Task.Run(JellyfinAutostart.StartProgram) ? AutostartResult.Done : AutostartResult.Failed;
            if (!ReferenceEquals(client, _client)) return;
            if (started == AutostartResult.Refused)
            {
                _localJellyfinNotice = null;
                SetNotice("Windows n’a pas reçu l’autorisation : Jellyfin reste arrêté.", LocalJellyfin.Start, () => _ = StartJellyfinAsync(client));
                DescribeGuideAgain();
                return;
            }
            if (started == AutostartResult.Done)
            {
                _localJellyfinNotice = LocalJellyfin.Says(LocalJellyfin.Verdict.Starting, service)!.Value.Text;
                SetNotice(_localJellyfinNotice, null, null, sticky: true);
                DescribeGuideAgain();
                var since = DateTime.UtcNow;
                while (DateTime.UtcNow - since < TimeSpan.FromMinutes(3))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    if (!ReferenceEquals(client, _client)) return;
                    try { if (await client.PingAsync()) { _jellyfinFailedStarts = 0; await RefreshAsync(); if (NoticeText.Text == _localJellyfinNotice) SetNotice(JellyfinBack); return; } }
                    catch (Exception ex) when (IsExpected(ex)) { }
                    // Stopped again (an error at its start): no need to wait the three minutes.
                    if (DateTime.UtcNow - since > TimeSpan.FromSeconds(10)
                        && await Task.Run(() => service ? JellyfinAutostart.ServiceState() is LocalJellyfin.Service.Stopped : !JellyfinAutostart.ProgramRunning())) break;
                }
            }
            if (!ReferenceEquals(client, _client)) return;
            _jellyfinFailedStarts++;
            await SayUnansweredAsync(client, null, "Jellyfin ne répond toujours pas. Mira réessaie tout seul et te le dira dès qu’il répond.");
            DescribeGuideAgain();
        }
        finally { _startingJellyfin = false; }
    }
    /// <summary>The phone's card, when open, says it too: starting, still stopped (its button back), or its log.</summary>
    private void DescribeGuideAgain()
    {
        if (GuideOverlay.Visibility == System.Windows.Visibility.Visible && !_preparing) _ = DescribePhoneAsync();
    }
}
