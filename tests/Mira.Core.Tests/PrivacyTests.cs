using System.IO;
using Mira.Core;
using static TestSupport;

/// <summary>Réglages → Confidentialité (Privacy of Core, Windows and Mac).</summary>
internal static class PrivacyTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Confidentialité : chaque connexion dite en français et en anglais, avec sa façon de l’arrêter", () =>
        {
            var was = Lang.Code;
            try
            {
                foreach (var app in new[] { PrivacyApp.Windows, PrivacyApp.Mac })
                {
                    Lang.Use(Lang.French); var french = Privacy.Connections(app);
                    Lang.Use(Lang.English); var english = Privacy.Connections(app);
                    Assert(french.Count == english.Count && french.Select(x => x.Key).SequenceEqual(english.Select(x => x.Key)), $"{app} : mêmes lignes dans les deux langues");
                    Assert(french.Select(x => x.Key).Distinct().Count() == french.Count, $"{app} : une ligne par connexion");
                    // This device's first, then the server's, as the page draws them.
                    Assert(french.SkipWhile(x => !x.FromServer).All(x => x.FromServer), $"{app} : celles du serveur après celles de l’appareil");
                    foreach (var (fr, en) in french.Zip(english))
                    {
                        foreach (var (f, e) in new[] { (fr.Title, en.Title), (fr.To, en.To), (fr.When, en.When), (fr.Sends, en.Sends), (fr.Stop, en.Stop) })
                            Assert(f.Length > 0 && e.Length > 0 && f != e, $"{app} {fr.Key} : « {f} » / « {e} »");
                        Assert(fr.Hosts.Length > 0, $"{app} {fr.Key} : nommée dans SECURITY.md");
                    }
                }
                var windows = Privacy.Connections(PrivacyApp.Windows).Select(x => x.Key).ToList();
                var mac = Privacy.Connections(PrivacyApp.Mac).Select(x => x.Key).ToList();
                Assert(windows.Contains("updates") && mac.Contains("updates") && windows.Contains("tailscale") && mac.Contains("tailscale"), "mises à jour et Tailscale intégré partout");
                Assert(windows.Contains("jellyfin-releases") && windows.Contains("torrents") && !mac.Contains("jellyfin-releases") && !mac.Contains("torrents"), "Jellyfin, TorLink et qBittorrent : Windows seulement");
                Assert(Privacy.Connections(PrivacyApp.Mac).Single(x => x.Key == "updates").Control == PrivacyControl.Updates, "mises à jour : un interrupteur");
            }
            finally { Lang.Use(was); }
            return Task.CompletedTask;
        });

        await Test("Confidentialité : chaque connexion de la page est dans SECURITY.md et sa traduction (« What goes out on the network »)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            foreach (var (file, heading, anchor, language) in new[]
            {
                ("SECURITY.md", "## What goes out on the network", "#what-goes-out-on-the-network", Lang.English),
                (Path.Combine("docs", "SECURITY.fr.md"), "## Ce qui part sur le réseau", "#ce-qui-part-sur-le-réseau", Lang.French),
            })
            {
                var security = File.ReadAllText(Path.Combine(repository, file));
                var start = security.IndexOf(heading, StringComparison.Ordinal);
                Assert(start >= 0, $"{file} : section « {heading[3..]} »");
                var end = security.IndexOf("\n## ", start + 4, StringComparison.Ordinal);
                var table = security[start..(end < 0 ? security.Length : end)];
                var was = Lang.Code;
                try { Lang.Use(language); Assert(Privacy.Document.EndsWith(anchor, StringComparison.Ordinal), $"{file} : lien vers la section"); }
                finally { Lang.Use(was); }
                foreach (var connection in Privacy.Connections(PrivacyApp.Windows).Concat(Privacy.Connections(PrivacyApp.Mac)))
                    foreach (var host in connection.Hosts) Assert(table.Contains(host, StringComparison.Ordinal), $"{connection.Key} : « {host} » absent de {file}");
            }
            // The French heading's anchor stays in the English file: the apps published before it link there.
            Assert(File.ReadAllText(Path.Combine(repository, "SECURITY.md")).Contains("<a id=\"ce-qui-part-sur-le-réseau\"></a>", StringComparison.Ordinal), "ancre des anciennes versions");
            return Task.CompletedTask;
        });
    }
}
