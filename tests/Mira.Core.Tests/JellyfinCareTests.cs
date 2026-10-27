using System.Net;
using System.Text;
using System.Text.Json;
using Mira.Core;
using static TestSupport;

/// <summary>
/// Jellyfin on the PC of Jellyfin (Windows): its backup (JellyfinBackups, as Jellyfin 12.1 answers), its reminder, the
/// copies kept in the person's folder, the newer versions read in winget's catalogue (JellyfinReleases, its real
/// manifests), and the elevated script that runs the installer and puts the service back.
/// </summary>
internal static class JellyfinCareTests
{
    // GET Backup of Jellyfin 12.1 (container), as it answered.
    private const string Backups121 = """[{"ServerVersion":"12.1.0.0","BackupEngineVersion":"0.2.0","DateCreated":"2026-10-27T07:52:28.9433865+00:00","Path":"/config/data/backups/jellyfin-backup-20261027075228.zip","Options":{"Metadata":true,"Trickplay":true,"Subtitles":true,"Database":true}}]""";
    // winget-pkgs, manifests/j/Jellyfin/Server/12.3 and 12.1, as published.
    private const string Winget123 = """
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
        # Created with winmatsch

        PackageIdentifier: Jellyfin.Server
        PackageVersion: "12.3"
        InstallerLocale: en-US
        InstallerType: nullsoft
        Scope: machine
        UpgradeBehavior: install
        ProductCode: JellyfinServer
        ReleaseDate: 2026-10-23
        AppsAndFeaturesEntries:
        - DisplayName: Jellyfin Server 12.3
          ProductCode: JellyfinServer
        ElevationRequirement: elevationRequired
        InstallationMetadata:
          DefaultInstallLocation: "%ProgramFiles%\\Jellyfin\\Server"
        Installers:
        - Architecture: x64
          InstallerUrl: https://repo.jellyfin.org/files/server/windows/stable/v12.3/amd64/jellyfin_12.3_windows-x64.exe
          InstallerSha256: A3721C57E00F2C03C9ADDC325290991D6236D981927D284ACDEF1EFCA282B3B1
        ManifestType: installer
        ManifestVersion: 1.12.0
        """;
    private const string Winget121 = """
        # Created using wingetcreate 1.12.13.0
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json

        PackageIdentifier: Jellyfin.Server
        PackageVersion: "12.1"
        InstallerLocale: en-US
        InstallerType: nullsoft
        Scope: machine
        ElevationRequirement: elevationRequired
        UpgradeBehavior: install
        ProductCode: JellyfinServer
        AppsAndFeaturesEntries:
        - DisplayName: Jellyfin Server 12.1
          ProductCode: JellyfinServer
        InstallationMetadata:
          DefaultInstallLocation: '%ProgramFiles%\Jellyfin\Server'
        Installers:
        - Architecture: x64
          InstallerUrl: https://repo.jellyfin.org/files/server/windows/stable/v12.1/amd64/jellyfin_12.1_windows-x64.exe
          InstallerSha256: 28E11B817B3410C860733591FBB88B9248B8D0B31CF2793B65CE0588CFFE3DA3
        ManifestType: installer
        ManifestVersion: 1.12.0
        ReleaseDate: 2026-09-15
        """;

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Sauvegarde de Jellyfin : la liste de Jellyfin 12.1 lue, la plus récente d’abord", () =>
        {
            using var json = JsonDocument.Parse(Backups121);
            var backup = JellyfinBackups.Parse(json.RootElement).Single();
            Assert(backup.Path == "/config/data/backups/jellyfin-backup-20261027075228.zip" && backup.FileName == "jellyfin-backup-20261027075228.zip", backup.Path);
            Assert(backup.Created == new DateTimeOffset(2026, 10, 27, 7, 52, 28, TimeSpan.Zero).AddTicks(9433865) && backup.ServerVersion == "12.1.0.0", "date et version");
            Assert(backup.Metadata && backup.Subtitles && backup.Trickplay, "options");
            Assert(new JellyfinBackup(@"C:\ProgramData\Jellyfin\Server\data\backups\jellyfin-backup-20261001080000.zip", default, "", false, false, false).FileName == "jellyfin-backup-20261001080000.zip", "chemin de Windows");
            using var several = JsonDocument.Parse("""
                [{"DateCreated":"2026-09-01T10:00:00Z","Path":"/b/jellyfin-backup-20260901100000.zip"},
                 {"DateCreated":"2026-10-20T10:00:00Z","Path":"/b/jellyfin-backup-20261020100000.zip"},
                 {"DateCreated":"pas une date","Path":"/b/x.zip"},{"Path":"/b/y.zip"},{"DateCreated":"2026-10-21T10:00:00Z"},42]
                """);
            var list = JellyfinBackups.Parse(several.RootElement);
            Assert(list.Count == 2 && list[0].FileName == "jellyfin-backup-20261020100000.zip" && !list[0].Metadata, string.Join(", ", list.Select(x => x.FileName)));
            // The answer of Backup/Create: the manifest alone.
            using var one = JsonDocument.Parse(Backups121[1..^1]);
            Assert(JellyfinBackups.Parse(one.RootElement).Count == 1, "réponse de Backup/Create");
            using var empty = JsonDocument.Parse("[]");
            Assert(JellyfinBackups.Parse(empty.RootElement).Count == 0, "aucune");
            return Task.CompletedTask;
        });
        await Test("Sauvegarde de Jellyfin : rappel à 30 jours, puis toutes les semaines, jamais pour un Jellyfin tout neuf", () =>
        {
            var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
            Assert(!JellyfinBackups.Overdue(now.AddDays(-29), now.AddDays(-400), now) && JellyfinBackups.Overdue(now.AddDays(-30), now, now), "selon la dernière");
            Assert(!JellyfinBackups.Overdue(null, now.AddDays(-3), now) && JellyfinBackups.Overdue(null, now.AddDays(-31), now), "sans aucune : depuis que Mira le suit");
            Assert(JellyfinBackups.Reminds(now.AddDays(-40), now.AddDays(-90), null, now), "premier rappel");
            Assert(!JellyfinBackups.Reminds(now.AddDays(-40), now.AddDays(-90), now.AddDays(-2), now), "rappelé il y a deux jours");
            Assert(JellyfinBackups.Reminds(now.AddDays(-40), now.AddDays(-90), now.AddDays(-7), now), "une semaine plus tard");
            Assert(JellyfinBackups.Reminds(now.AddDays(-40), now.AddDays(-90), now.AddDays(3), now), "horloge revenue en arrière");
            Assert(!JellyfinBackups.Reminds(now.AddDays(-1), now.AddDays(-90), null, now), "sauvegardé hier");
            return Task.CompletedTask;
        });
        await Test("Sauvegarde de Jellyfin : ce que Mira en dit", () =>
        {
            var now = DateTimeOffset.Now;
            JellyfinBackup Made(DateTimeOffset at) => new("/b/jellyfin-backup-20261027075228.zip", at, "12.1.0", true, true, false);
            Assert(JellyfinBackups.Summary(null, now) == "Aucune sauvegarde pour l’instant.", "aucune");
            Assert(JellyfinBackups.Summary(Made(now), now).StartsWith("Dernière sauvegarde aujourd’hui à ", StringComparison.Ordinal), JellyfinBackups.Summary(Made(now), now));
            Assert(JellyfinBackups.Summary(Made(now.AddDays(-1)), now).StartsWith("Dernière sauvegarde hier à ", StringComparison.Ordinal), "hier");
            Assert(JellyfinBackups.Summary(Made(now.AddDays(-24)), now).EndsWith(", il y a 24 jours.", StringComparison.Ordinal), JellyfinBackups.Summary(Made(now.AddDays(-24)), now));
            Assert(JellyfinBackups.Reminder(null, now).StartsWith("Jellyfin n’a jamais été sauvegardé", StringComparison.Ordinal), "jamais");
            Assert(JellyfinBackups.Reminder(Made(now.AddDays(-45)), now) == "Jellyfin n’a pas été sauvegardé depuis 45 jours.", JellyfinBackups.Reminder(Made(now.AddDays(-45)), now));
            Assert(JellyfinBackups.NoRoom(@"C:\", 2L << 30).StartsWith(@"Jellyfin exige 5 Go libres sur C:\ pour se sauvegarder : il en reste 2", StringComparison.Ordinal), JellyfinBackups.NoRoom(@"C:\", 2L << 30));
            return Task.CompletedTask;
        });
        await Test("Sauvegarde de Jellyfin : copiée dans ton dossier, les 3 dernières gardées, rien d’autre effacé", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "mira-backups-" + Guid.NewGuid().ToString("N"));
            var jellyfin = Path.Combine(root, "backups"); var key = Path.Combine(root, "cle");
            Directory.CreateDirectory(jellyfin); Directory.CreateDirectory(key);
            try
            {
                foreach (var day in new[] { "20260901", "20260915", "20261001" }) File.WriteAllText(Path.Combine(key, $"jellyfin-backup-{day}080000.zip"), day);
                File.WriteAllText(Path.Combine(key, "photos.zip"), "à toi"); File.WriteAllText(Path.Combine(key, "jellyfin-backup-mien.zip"), "à toi");
                var archive = Path.Combine(jellyfin, "jellyfin-backup-20261027075228.zip"); File.WriteAllText(archive, "sauvegarde");
                var copy = JellyfinBackups.Copy(archive, key);
                Assert(copy == Path.Combine(key, "jellyfin-backup-20261027075228.zip") && File.ReadAllText(copy) == "sauvegarde", copy);
                var left = Directory.EnumerateFiles(key).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
                Assert(string.Join(" ", left) == "jellyfin-backup-20260915080000.zip jellyfin-backup-20261001080000.zip jellyfin-backup-20261027075228.zip jellyfin-backup-mien.zip photos.zip", string.Join(" ", left));
                Assert(File.Exists(archive), "l’archive de Jellyfin reste");
                // Copied again: same name, replaced.
                File.WriteAllText(archive, "sauvegarde 2");
                Assert(File.ReadAllText(JellyfinBackups.Copy(archive, key)) == "sauvegarde 2" && !Directory.EnumerateFiles(key, "*.part").Any(), "recopiée");
                var refused = false;
                try { JellyfinBackups.Copy(Path.Combine(root, "autre.zip"), key); } catch (IOException) { refused = true; }
                Assert(refused, "pas une sauvegarde de Jellyfin");
                var missing = false;
                try { JellyfinBackups.Copy(archive, Path.Combine(root, "debranchee")); } catch (DirectoryNotFoundException ex) { missing = ex.Message.Contains("débranché", StringComparison.Ordinal); }
                Assert(missing, "dossier absent");
                Assert(JellyfinBackups.Superseded(["jellyfin-backup-20261001080000.zip", "x.zip", "JELLYFIN-BACKUP-20260101000000.ZIP"], keep: 1).SequenceEqual(["JELLYFIN-BACKUP-20260101000000.ZIP"]), "plus anciennes");
            }
            finally { Directory.Delete(root, recursive: true); }
            return Task.CompletedTask;
        });
        await Test("Sauvegarde de Jellyfin : demandée à Jellyfin, sans les aperçus, refusée sans droits", async () =>
        {
            string? body = null; var status = HttpStatusCode.OK;
            using var client = new JellyfinClient(new("http://localhost:8096/", "user-1", "Admin", "secret", "device"), new Handler(async request =>
            {
                if (status != HttpStatusCode.OK) return new HttpResponseMessage(status);
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/Backup") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Backups121) };
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/Backup/Create")
                {
                    Assert(request.Headers.Authorization?.ToString().Contains("Token=\"secret\"", StringComparison.Ordinal) == true, "jeton");
                    body = await request.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Backups121[1..^1]) };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            Assert((await client.BackupsAsync())?.Single().FileName == "jellyfin-backup-20261027075228.zip", "liste");
            var made = await client.CreateBackupAsync();
            Assert(made?.Path == "/config/data/backups/jellyfin-backup-20261027075228.zip", "créée");
            using (var sent = JsonDocument.Parse(body!))
                Assert(sent.RootElement.GetProperty("Metadata").GetBoolean() && sent.RootElement.GetProperty("Subtitles").GetBoolean() && !sent.RootElement.GetProperty("Trickplay").GetBoolean(), body!);
            status = HttpStatusCode.Forbidden;
            Assert(await client.BackupsAsync() is null && await client.CreateBackupAsync() is null, "pas administrateur");
            status = HttpStatusCode.NotFound;
            Assert(await client.BackupsAsync() is null, "Jellyfin avant 10.11");
        });

        await Test("Nouvelle version de Jellyfin : versions lues et choisies, la même majeure d’abord", () =>
        {
            Assert(JellyfinReleases.ParseVersion("12.1.0") == new Version(12, 1, 0) && JellyfinReleases.ParseVersion("12.3") == new Version(12, 3, 0), "12.1.0, 12.3");
            Assert(JellyfinReleases.ParseVersion("12.1.0.0") == new Version(12, 1, 0) && JellyfinReleases.ParseVersion("v10.11.2") == new Version(10, 11, 2) && JellyfinReleases.ParseVersion(" 13 ") == new Version(13, 0, 0), "autres formes");
            Assert(JellyfinReleases.ParseVersion("12.1-beta") is null && JellyfinReleases.ParseVersion("") is null && JellyfinReleases.ParseVersion(null) is null && JellyfinReleases.ParseVersion("12..1") is null, "illisibles");
            using var listing = JsonDocument.Parse("""
                [{"name":"10.10.7","type":"dir"},{"name":"12.1","type":"dir"},{"name":"12.2","type":"dir"},{"name":"12.3","type":"dir"},
                 {"name":"README.md","type":"file"},{"name":"12.4-rc1","type":"dir"},{"name":"13.0","type":"file"}]
                """);
            var versions = JellyfinReleases.Versions(listing.RootElement);
            Assert(string.Join(" ", versions) == "10.10.7 12.1 12.2 12.3", string.Join(" ", versions));
            Assert(JellyfinReleases.Pick(versions, new(12, 1, 0)) == "12.3" && JellyfinReleases.Pick(versions, new(12, 3, 0)) is null, "même majeure");
            Assert(JellyfinReleases.Pick(versions, new(10, 10, 7)) == "12.3", "majeure suivante");
            Assert(JellyfinReleases.Pick([.. versions, "13.0", "12.10"], new(12, 3, 0)) == "12.10" && JellyfinReleases.Pick(["13.0", "13.1"], new(12, 3, 0)) == "13.1", "la majeure seulement sans mineure plus récente");
            Assert(JellyfinReleases.Versions(JsonDocument.Parse("""{"message":"API rate limit exceeded"}""").RootElement).Count == 0, "pas une liste");
            Assert(JellyfinReleases.Notes("12.3").AbsoluteUri == "https://github.com/jellyfin/jellyfin/releases/tag/v12.3", "nouveautés");
            return Task.CompletedTask;
        });
        await Test("Nouvelle version de Jellyfin : l’installateur des vrais manifestes de winget, et seulement lui", () =>
        {
            var latest = JellyfinReleases.ParseInstaller(Winget123, "12.3")!;
            Assert(latest.Version == "12.3" && latest.FileName == "jellyfin_12.3_windows-x64.exe" && latest.Sha256 == "A3721C57E00F2C03C9ADDC325290991D6236D981927D284ACDEF1EFCA282B3B1", latest.ToString());
            Assert(latest.Url.AbsoluteUri == "https://repo.jellyfin.org/files/server/windows/stable/v12.3/amd64/jellyfin_12.3_windows-x64.exe", latest.Url.AbsoluteUri);
            Assert(JellyfinReleases.ParseInstaller(Winget121.Replace("\n", "\r\n", StringComparison.Ordinal), "12.1") == JellyfinServerPackage.Current, "12.1 = la version épinglée (CRLF)");
            Assert(JellyfinReleases.ParseInstaller(Winget123, "12.2") is null, "autre version");
            Assert(JellyfinReleases.ParseInstaller(Winget123.Replace("Jellyfin.Server", "Jellyfin.Media", StringComparison.Ordinal), "12.3") is null, "autre paquet");
            foreach (var (from, to, why) in new[]
            {
                ("https://repo.jellyfin.org/", "https://repo.jellyfin.org.evil.example/", "autre hôte"),
                ("https://repo.jellyfin.org/", "http://repo.jellyfin.org/", "http"),
                ("https://repo.jellyfin.org/", "https://repo.jellyfin.org:8443/", "autre port"),
                ("https://repo.jellyfin.org/", "https://x@repo.jellyfin.org/", "nom d’utilisateur"),
                ("windows-x64.exe", "windows-x64.exe?x=1", "requête"),
                ("windows-x64.exe", "windows-x64.msi", "pas l’installateur"),
                ("A3721C57E00F2C03C9ADDC325290991D6236D981927D284ACDEF1EFCA282B3B1", "A3721C57E00F2C03", "empreinte courte"),
                ("Architecture: x64", "Architecture: arm64", "pas de x64"),
            })
                Assert(JellyfinReleases.ParseInstaller(Winget123.Replace(from, to, StringComparison.Ordinal), "12.3") is null, why);
            // An arm64 installer listed first: the x64 one is taken.
            var two = Winget123.Replace("Installers:\n", "Installers:\n- Architecture: arm64\n  InstallerUrl: https://repo.jellyfin.org/files/server/windows/stable/v12.3/arm64/jellyfin_12.3_windows-arm64.exe\n  InstallerSha256: " + new string('B', 64) + "\n", StringComparison.Ordinal);
            Assert(JellyfinReleases.ParseInstaller(two, "12.3")?.Sha256 == latest.Sha256, "x64 parmi d’autres");
            // Keys outside the Installers list never count as an installer's.
            var outside = "PackageIdentifier: Jellyfin.Server\nPackageVersion: \"12.3\"\nAppsAndFeaturesEntries:\n- Architecture: x64\n  InstallerUrl: https://repo.jellyfin.org/files/server/windows/stable/v12.3/amd64/jellyfin_12.3_windows-x64.exe\n  InstallerSha256: " + latest.Sha256 + "\n";
            Assert(JellyfinReleases.ParseInstaller(outside, "12.3") is null, "hors de Installers");
            return Task.CompletedTask;
        });
        await Test("Nouvelle version de Jellyfin : demandée au catalogue de winget sur GitHub", async () =>
        {
            var asked = new List<string>(); var manifest = Winget123;
            var handler = new Handler(request =>
            {
                asked.Add(request.RequestUri!.AbsoluteUri);
                Assert(request.Headers.UserAgent.ToString().StartsWith("Mira/", StringComparison.Ordinal), "User-Agent");
                Assert(request.Headers.Authorization is null, "rien d’autre que la requête");
                if (request.RequestUri == JellyfinReleases.Catalog)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"name":"12.1","type":"dir"},{"name":"12.3","type":"dir"}]""", Encoding.UTF8, "application/json") });
                if (request.RequestUri == JellyfinReleases.InstallerManifest("12.3")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            var found = await JellyfinReleases.FindAsync(new(12, 1, 0), handler);
            Assert(found?.Version == new Version(12, 3, 0) && found.Package.Sha256.StartsWith("A3721C57", StringComparison.Ordinal), found?.ToString() ?? "rien");
            Assert(asked.SequenceEqual([JellyfinReleases.Catalog.AbsoluteUri, "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/j/Jellyfin/Server/12.3/Jellyfin.Server.installer.yaml"]), string.Join(" ", asked));
            asked.Clear();
            Assert(await JellyfinReleases.FindAsync(new(12, 3, 0), handler) is null && asked.Count == 1, "à jour : un seul appel");
            manifest = Winget123.Replace("repo.jellyfin.org", "example.com", StringComparison.Ordinal);
            var refused = false;
            try { await JellyfinReleases.FindAsync(new(12, 1, 0), handler); } catch (IOException ex) { refused = ex.Message.Contains("12.3", StringComparison.Ordinal); }
            Assert(refused, "manifeste inattendu");
            var limited = false;
            try { await JellyfinReleases.FindAsync(new(12, 1, 0), new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))); } catch (HttpRequestException) { limited = true; }
            Assert(limited, "GitHub refuse (limite de requêtes)");
        });

        await Test("Mise à jour de Jellyfin : le service remis comme avant, l’installateur vérifié par le script élevé", () =>
        {
            var before = new JellyfinStartup.State(true, true, true, true);
            var after = JellyfinStartup.AfterUpdate(before).Select(x => $"{x.File} {x.Arguments}{(x.Optional ? " ?" : "")}").ToList();
            Assert(string.Join(" | ", after) == $"sc.exe config JellyfinServer start= delayed-auto | sc.exe failure JellyfinServer reset= 86400 actions= {JellyfinStartup.RestartActions} | sc.exe failureflag JellyfinServer 1 | sc.exe start JellyfinServer ?", string.Join(" | ", after));
            Assert(JellyfinStartup.AfterUpdate(new(true, true, false, false)).Single().Arguments == "start JellyfinServer", "rien à remettre");
            Assert(JellyfinStartup.AfterUpdate(new(true, false, true, false)).Single().Arguments == "start JellyfinServer", "démarrage manuel : rien de différé");
            var refusedName = false;
            try { JellyfinStartup.AfterUpdate(before, "Jellyfin & calc"); } catch (ArgumentException) { refusedName = true; }
            Assert(refusedName, "nom de service");

            var setup = OperatingSystem.IsWindows() ? @"C:\Users\Zoé\AppData\Local\Mira\downloads\jellyfin_12.3_windows-x64.exe" : "/home/zoé/.local/share/Mira/downloads/jellyfin_12.3_windows-x64.exe";
            var sha = "a3721c57e00f2c03c9addc325290991d6236d981927d284acdef1efca282b3b1";
            var plan = new JellyfinStartup.Plan([], [], JellyfinStartup.AfterUpdate(before), new(setup, sha));
            var script = JellyfinStartup.Script(plan);
            Assert(script.Contains($"$setup = @{{ P = (T '{Convert.ToBase64String(Encoding.UTF8.GetBytes(setup))}'); H = '{sha.ToUpperInvariant()}' }}", StringComparison.Ordinal), "chemin en base64, empreinte en majuscules");
            Assert(!script.Contains("Zoé", StringComparison.Ordinal) && !script.Contains("downloads", StringComparison.Ordinal), "aucun texte du chemin dans le code");
            Assert(script.Contains("[IO.FileShare]::Read", StringComparison.Ordinal) && script.Contains("'/S'", StringComparison.Ordinal) && script.Contains($"exit {JellyfinStartup.SetupMismatch}", StringComparison.Ordinal), "fichier tenu, vérifié, puis lancé en silencieux");
            Assert(script.IndexOf("$setup = ", StringComparison.Ordinal) < script.IndexOf("foreach ($command in $commands)", StringComparison.Ordinal), "l’installateur avant les commandes");
            Assert(JellyfinStartup.Script(new([], [], JellyfinStartup.AfterUpdate(before))).Contains("$setup = $null", StringComparison.Ordinal), "sans installateur");
            Assert(JellyfinStartup.PowerShellArguments(plan).Length <= JellyfinStartup.MaximumArguments, "longueur");
            foreach (var (path, hash, why) in new[] { ("jellyfin_12.3_windows-x64.exe", sha, "chemin relatif"), (setup.Replace(".exe", ".msi", StringComparison.Ordinal), sha, "pas un .exe"), (setup, sha[..63], "empreinte courte"), (setup, sha[..63] + "g", "empreinte illisible"), (setup, sha + "'; calc; '", "code") })
            {
                var refused = false;
                try { JellyfinStartup.Script(plan with { Setup = new(path, hash) }); } catch (ArgumentException) { refused = true; }
                Assert(refused, why);
            }
            return Task.CompletedTask;
        });
    }
}
