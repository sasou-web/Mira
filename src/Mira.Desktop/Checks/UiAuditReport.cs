using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Mira.Desktop;

/// <summary>
/// What --ui-audit writes in its folder: <c>report.md</c> (in French, for ogsas: each defect once, the screens where it
/// shows, a link to the capture where it is outlined), <c>findings.json</c> (the same, for the next night to compare
/// with) and <c>summary.txt</c> (one line the nightly task reads).
/// </summary>
internal static class UiAuditReport
{
    private static readonly string[] Unseen =
    [
        "Le lecteur et le mini-lecteur : l'audit ne lance aucune lecture la nuit (leurs menus et leur barre sont capturés par `--player-check` dans la CI, pas jugés ici).",
        "Le liseré au survol : seul le zoom des tuiles est mesuré, pas la couleur ni le contour réellement dessinés.",
        "Le survol des boutons : l'audit lit leurs déclencheurs et gestionnaires, il ne déplace jamais la souris.",
        "Le goût : couleurs, hiérarchie, équilibre d'un écran. C'est la revue visuelle des captures (`captures/`).",
        "Le Mac : cet audit tourne sur l'app Windows (la référence) ; l'app Mac n'est pas comparée.",
        "Les écrans qui demandent une action (connexion, ajout d'un ami, invitation, « Suivre ») : jamais ouverts, rien n'est changé sur Jellyfin.",
    ];

    public static void Write(string output, List<UiFinding> findings, List<(string Screen, string Capture, string? Annotated, int Defects)> shots, List<string> notes,
        List<(string Name, int Frames, double Ms, double Jump, string? Wanted)> transitions, string? failure, DateTimeOffset started, string version)
    {
        var grouped = findings.GroupBy(x => x.Key).Select(g => (First: g.First(), Screens: g.Select(x => x.Screen).Distinct().ToList())).ToList();
        var captureOf = shots.GroupBy(x => x.Screen).ToDictionary(x => x.Key, x => x.First().Annotated ?? x.First().Capture);
        var defects = grouped.Count(x => x.First.Severity == "défaut"); var judged = grouped.Count(x => x.First.Severity == "à juger");
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Audit de l'interface de Mira, {started:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{version} · {shots.Count} captures · {(DateTimeOffset.Now - started).TotalMinutes:0} min · fenêtre à 1440 × 900, 1024 × 720 et 1920 × 1080.");
        text.AppendLine();
        if (failure is not null) { text.AppendLine("> [!WARNING]"); text.AppendLine("> L'audit s'est arrêté avant la fin : " + failure.Split('\n')[0].Trim()); text.AppendLine(); }
        text.AppendLine(CultureInfo.InvariantCulture, $"**{defects} défaut(s) et {judged} point(s) à juger**, chacun compté une fois même s'il revient sur plusieurs écrans.");
        text.AppendLine();
        text.AppendLine("| Genre | Défauts | À juger | Infos |");
        text.AppendLine("|---|---|---|---|");
        foreach (var (kind, title, _) in UiInspector.Kinds)
        {
            var of = grouped.Where(x => x.First.Kind == kind).ToList();
            if (of.Count == 0) continue;
            text.AppendLine(CultureInfo.InvariantCulture, $"| {title} | {of.Count(x => x.First.Severity == "défaut")} | {of.Count(x => x.First.Severity == "à juger")} | {of.Count(x => x.First.Severity == "info")} |");
        }
        foreach (var (kind, title, about) in UiInspector.Kinds)
        {
            var of = grouped.Where(x => x.First.Kind == kind).OrderBy(x => x.First.Severity == "défaut" ? 0 : x.First.Severity == "à juger" ? 1 : 2).ThenByDescending(x => x.Screens.Count).ToList();
            if (of.Count == 0) continue;
            text.AppendLine();
            text.AppendLine("## " + title);
            text.AppendLine();
            text.AppendLine(about);
            text.AppendLine();
            text.AppendLine("| | Élément | Ce qui ne va pas | Écrans | Capture |");
            text.AppendLine("|---|---|---|---|---|");
            var shown = 0;
            foreach (var (first, screens) in of)
            {
                // Abbreviations are listed, not all of them.
                if (first.Severity == "info" && ++shown > 25) { text.AppendLine(CultureInfo.InvariantCulture, $"| | … et {of.Count - 25} autres | | | |"); break; }
                var capture = screens.Select(x => captureOf.GetValueOrDefault(x)).FirstOrDefault(x => x is not null);
                var where = screens.Count <= 3 ? string.Join("<br>", screens) : string.Join("<br>", screens.Take(2)) + $"<br>et {screens.Count - 2} autres";
                text.AppendLine(CultureInfo.InvariantCulture, $"| {Mark(first.Severity)} | {Cell(first.Element)} | {Cell(first.Detail)} | {Cell(where)} | {(capture is null ? "" : $"[voir]({capture})")} |");
            }
        }
        if (transitions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Transitions mesurées");
            text.AppendLine();
            text.AppendLine("Images où l'écran change, sur la durée de la transition ; « saut » = part du changement faite en une seule image (100 % = aucune animation).");
            text.AppendLine();
            text.AppendLine("| Transition | Images | Durée | Saut | |");
            text.AppendLine("|---|---|---|---|---|");
            foreach (var (name, frames, ms, jump, wanted) in transitions)
                text.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(name)} | {frames} | {ms:0} ms | {jump:P0} | {(jump > .8 && frames <= 2 ? wanted is null ? "à juger" : Cell(wanted) : "animée")} |");
        }
        if (notes.Count > 0)
        {
            text.AppendLine(); text.AppendLine("## Notes"); text.AppendLine();
            foreach (var note in notes.Distinct()) text.AppendLine("- " + note);
        }
        text.AppendLine(); text.AppendLine("## Ce que l'audit ne voit pas"); text.AppendLine();
        foreach (var line in Unseen) text.AppendLine("- " + line);
        File.WriteAllText(Path.Combine(output, "report.md"), text.ToString(), new UTF8Encoding(false));

        var json = grouped.Select(x => new
        {
            kind = x.First.Kind, severity = x.First.Severity, element = x.First.Element, detail = x.First.Detail, screens = x.Screens,
            capture = x.Screens.Select(s => captureOf.GetValueOrDefault(s)).FirstOrDefault(s => s is not null),
            bounds = x.First.Bounds.IsEmpty ? null : new[] { Math.Round(x.First.Bounds.X), Math.Round(x.First.Bounds.Y), Math.Round(x.First.Bounds.Width), Math.Round(x.First.Bounds.Height) },
        });
        File.WriteAllText(Path.Combine(output, "findings.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "summary.txt"), failure is null ? $"OK defauts={defects} a-juger={judged} captures={shots.Count}" : $"FAIL defauts={defects} a-juger={judged} captures={shots.Count}\n{failure}", new UTF8Encoding(false));
    }

    private static string Mark(string severity) => severity switch { "défaut" => "🔴", "à juger" => "🟠", _ => "⚪" };

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
