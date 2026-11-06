using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Playback;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// Réglages → Mira sur ce PC → « Exporter un rapport » (DiagnosticReport of Core): a text file saved where the person
/// chooses, to send by hand when something goes wrong. Nothing leaves this PC by itself.
/// </summary>
public partial class MainWindow
{
    private async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now;
        var dialog = new SaveFileDialog
        {
            Title = L("Enregistrer le rapport de Mira", "Save Mira’s report"),
            FileName = DiagnosticReport.FileName(now),
            Filter = L("Texte|*.txt", "Text|*.txt"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != true) return;
        ExportReportButton.IsEnabled = false;
        try
        {
            var report = await DiagnosticTextAsync(now);
            await File.WriteAllTextAsync(dialog.FileName, report);
            SetNotice(L("Rapport enregistré. Joins-le à ton message : rien n’est parti tout seul.", "Report saved. Attach it to your message: nothing was sent on its own."), L("Afficher", "Show"), () => ShowInExplorer(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetNotice(L("Le rapport n’a pas pu être enregistré ici. Choisis un autre dossier.", "The report couldn’t be saved there. Choose another folder."));
        }
        finally { ExportReportButton.IsEnabled = true; }
    }

    private static void ShowInExplorer(string path)
    {
        try { using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The report: Jellyfin's version asked with a short wait (an unanswering server must not hold the export).</summary>
    private async Task<string> DiagnosticTextAsync(DateTimeOffset now)
    {
        string? jellyfin = null;
        if (_client is { } client)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { jellyfin = await client.ServerVersionAsync(wait.Token) ?? "no answer"; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException) { jellyfin = "no answer (" + ex.GetType().Name + ")"; }
        }
        var engine = MpvEngine.FindLibrary(_settings.MpvPath) is not { } found ? "none"
            : string.Equals(found, MpvInstaller.LibraryPath(AppFiles.ProfileDirectory), StringComparison.OrdinalIgnoreCase) ? "installed by Mira " + MpvPackage.Current.Version : "found on this PC";
        (string, string?)[] facts =
        [
            ("Signed in", _client is null ? "no" : "yes"),
            ("Quality", _settings.Quality),
            ("Picture", _settings.Picture),
            ("Audio output", _settings.AudioDevice == AudioOutput.Default ? "default" : "chosen"),
            ("Audio channels", _settings.AudioChannels),
            ("Passthrough", _settings.AudioPassthrough),
            ("mpv engine", engine),
            ("Built-in Tailscale", _settings.Tailnet ? _tailnet?.State ?? "on, not running" : "off"),
            ("Updates", _settings.AutoUpdate ? "automatic" : "by hand"),
        ];
        return DiagnosticReport.Build("Mira for Windows", _client?.Connection.Server, jellyfin, facts, RecentPlaybacks.All,
            DiagnosticReport.ReadErrors(AppFiles.ProfileDirectory), now, [_client?.Connection.UserName, .. DiagnosticReport.ComputerNames()]);
    }
}
