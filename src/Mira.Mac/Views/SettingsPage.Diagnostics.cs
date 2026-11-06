using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Réglages → Mira sur ce Mac → « Exporter un rapport » (DiagnosticReport of Core), as on Windows: a text file saved
/// where the person chooses, to send by hand. Nothing leaves this Mac by itself.
/// </summary>
public sealed partial class SettingsPage
{
    private Control ReportRow()
    {
        Button? export = null;
        export = Ui.Action(L("Exporter…", "Export…"), "download", "", () => _ = ExportReportAsync(export!));
        Avalonia.Automation.AutomationProperties.SetName(export, L("Exporter un rapport", "Export a report"));
        return Row(L("Rapport pour signaler un problème", "Report for a problem"),
            L("Un fichier texte à joindre à ton message : versions, sorte de réseau, dernières lectures et erreurs de Mira. Ni mot de passe, ni adresse, ni nom de fichier. Rien ne part tout seul.", "A text file to attach to your message: versions, kind of network, Mira’s last playbacks and errors. No password, address nor file name. Nothing is sent on its own."),
            export);
    }

    private async Task ExportReportAsync(Button export)
    {
        var now = DateTimeOffset.Now;
        var file = await Shell.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L("Enregistrer le rapport de Mira", "Save Mira’s report"),
            SuggestedFileName = DiagnosticReport.FileName(now),
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType(L("Texte", "Text")) { Patterns = ["*.txt"], MimeTypes = ["text/plain"] }],
        });
        if (file is null) return;
        export.IsEnabled = false;
        try
        {
            var report = await DiagnosticTextAsync(now);
            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new StreamWriter(stream)) await writer.WriteAsync(report);
            var path = file.TryGetLocalPath();
            Shell.Notice(L("Rapport enregistré. Joins-le à ton message : rien n’est parti tout seul.", "Report saved. Attach it to your message: nothing was sent on its own."),
                path is null ? null : L("Afficher", "Show"), path is null ? null : () => Reveal(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Shell.Notice(L("Le rapport n’a pas pu être enregistré ici. Choisis un autre dossier.", "The report couldn’t be saved there. Choose another folder."));
        }
        finally { export.IsEnabled = true; }
        file.Dispose();
    }

    private static void Reveal(string path)
    {
        try { using var _ = Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-R", path }, UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The report: Jellyfin's version asked with a short wait (an unanswering server must not hold the export).</summary>
    private async Task<string> DiagnosticTextAsync(DateTimeOffset now)
    {
        var client = Shell.Session?.Client;
        string? jellyfin = null;
        if (client is not null)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { jellyfin = await client.ServerVersionAsync(wait.Token) ?? "no answer"; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException) { jellyfin = "no answer (" + ex.GetType().Name + ")"; }
        }
        var settings = Shell.Settings;
        (string, string?)[] facts =
        [
            ("Signed in", client is null ? "no" : "yes"),
            ("Quality", settings.Quality),
            ("Picture", settings.Picture),
            ("Picture repair", settings.VideoRepair.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Built-in Tailscale", settings.Tailnet ? Shell.Tailnet.Current?.State ?? "on, not running" : "off"),
            ("Updates", settings.AutoUpdate ? "automatic" : "by hand"),
        ];
        return DiagnosticReport.Build("Mira for Mac", client?.Connection.Server, jellyfin, facts, RecentPlaybacks.All,
            DiagnosticReport.ReadErrors(Shell.Profile.DirectoryPath), now, [client?.Connection.UserName, .. DiagnosticReport.ComputerNames()]);
    }
}
