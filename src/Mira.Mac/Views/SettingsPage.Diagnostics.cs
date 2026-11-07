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
        export = Ui.Action(L("Exporter…", "Export…"), "download", "", () => _ = Shell.ExportReportAsync(export!));
        Avalonia.Automation.AutomationProperties.SetName(export, L("Exporter un rapport", "Export a report"));
        return Row(L("Rapport pour signaler un problème", "Report for a problem"),
            L("Un fichier texte à joindre à ton message : versions, sorte de réseau, dernières lectures et erreurs de Mira. Ni mot de passe, ni adresse, ni nom de fichier. Rien ne part tout seul.", "A text file to attach to your message: versions, kind of network, Mira’s last playbacks and errors. No password, address nor file name. Nothing is sent on its own."),
            export);
    }
}
