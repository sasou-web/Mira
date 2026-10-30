using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Desktop.Services;
using static TestSupport;

/// <summary>Signed updates, the mpv engine download, what's new, window placement and Start menu. Free of WPF: also run outside Windows.</summary>
internal static class UpdateTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
        await Test("Fenêtre : taille normale sur un grand écran, réduite et jamais agrandie sur un petit", () =>
        {
            ScreenFit.Fit? Open(double width, double height) => ScreenFit.Window(new(1480, 930), new(960, 600), new(width, height));
            Assert(Open(2560, 1392) is { Size: { Width: 1480, Height: 930 }, Minimum: { Width: 960, Height: 600 } }, "A large screen keeps the default size");
            Assert(Open(1920, 1032) is { Size: { Width: 1480, Height: 928 } }, "1080p at 100 %: 930 px do not fit in 90 % of 1032");
            Assert(Open(1536, 826) is { Size: { Width: 1382, Height: 743 } }, "1080p at 125 %: 90 % of the screen");
            Assert(Open(1280, 688) is { Size: { Width: 1152, Height: 619 } }, "1080p at 150 %: 90 % of the screen, above the minimum height");
            Assert(Open(900, 560) is { Size: { Width: 900, Height: 560 }, Minimum: { Width: 900, Height: 560 } }, "A screen smaller than the minimum lowers the minimum to the screen");
            Assert(Open(0, 0) is null, "An unknown screen changes nothing");
            // The size remembered from the last session: kept when it fits, never larger than the screen nor below the minimum.
            Assert(ScreenFit.Window(new(1700, 1000), new(960, 600), new(1920, 1032)) is { Size: { Width: 1700, Height: 928 } }, "A remembered size taller than the screen");
            Assert(ScreenFit.Window(new(500, 300), new(960, 600), new(1920, 1032)) is { Size: { Width: 960, Height: 600 } }, "A remembered size below the minimum");
            return Task.CompletedTask;
        });
        await Test("Fenêtre : rouverte à sa place si son écran est là, sinon centrée sur l’écran de la souris, à l’échelle de cet écran", () =>
        {
            ScreenFit.Screen Screen(double left, double top, double width, double height, double scale = 1) => new(new(left, top, width, height), scale);
            // A 1080p primary screen at 100 % (taskbar below), and a smaller one on its right.
            var primary = Screen(0, 0, 1920, 1032); var small = Screen(1920, 0, 1366, 728);
            ScreenFit.Placement? Place((double, double)? remembered, (double, double)? pointer, params ScreenFit.Screen[] screens) =>
                ScreenFit.Place(new(1480, 930), new(960, 600), screens, remembered, pointer);
            // No corner remembered: the screen under the pointer, sized for it (90 % of 1366 × 728), centred there.
            Assert(Place(null, (2500, 300), primary, small) is { Remembered: false, Fit.Size: { Width: 1229, Height: 655 }, Window: { Left: 1988, Top: 36, Width: 1229, Height: 655 } } p1 && p1.Screen == small,
                "Opened on the smaller screen of the pointer: sized for that screen, not for the primary one");
            Assert(Place(null, null, primary, small) is { Window: { Left: 220, Top: 52, Width: 1480, Height: 928 } } p2 && p2.Screen == primary, "No pointer: centred on the primary screen");
            Assert(Place(null, (-5000, -5000), primary, small)?.Screen == primary, "A pointer outside every screen: the primary screen");
            // A remembered corner on a screen still there: kept, the size fitted to that screen.
            Assert(Place((2000, 40), (100, 100), primary, small) is { Remembered: true, Window: { Left: 2000, Top: 40 } } p3 && p3.Screen == small, "Back where it was on the second screen");
            // That screen unplugged since: centred on the screen of the pointer instead of off every screen.
            Assert(Place((2000, 40), (100, 100), primary) is { Remembered: false, Window: { Left: 220, Top: 52 } }, "Its screen unplugged: back on a screen present");
            // Partly beyond the edge: moved inside the work area, never over the taskbar.
            Assert(Place((1700, 900), null, primary) is { Remembered: true, Window: { Left: 440, Top: 104, Width: 1480, Height: 928 } }, "A corner too close to the edge is moved in");
            // Only a sliver of the title bar would show (above the top of the screen): not reachable, so centred.
            Assert(Place((100, -900), null, primary) is { Remembered: false }, "A title bar out of reach is not reopened there");
            Assert(Place((1900, 100), null, primary) is { Remembered: false }, "Twenty pixels of title bar are not enough to grab it");
            // Screens left of or above the primary one have negative coordinates.
            var left = Screen(-2560, -200, 2560, 1400);
            Assert(Place((-2400, -100), null, primary, left) is { Remembered: true, Window: { Left: -2400, Top: -100 } } p4 && p4.Screen == left, "A screen left of the primary one");
            // 150 %: the size in device-independent pixels fits 1707 × 928, and the window measures 1.5 times that in pixels.
            var scaled = Screen(1920, 0, 2560, 1392, 1.5);
            Assert(Place(null, (3000, 500), primary, scaled) is { Fit.Size: { Width: 1480, Height: 835 }, Window: { Width: 2220, Height: 1252 } } p5 && p5.Screen == scaled,
                "At 150 %: fitted in device-independent pixels, placed in device pixels");
            // The corner is checked against each screen at its own scale.
            Assert(Place((1950, 50), null, primary, scaled) is { Remembered: true } p6 && p6.Screen == scaled, "Remembered on the 150 % screen");
            // Nothing usable: Windows' own placement.
            Assert(Place(null, null) is null && Place(null, null, Screen(0, 0, 0, 0)) is null && Place(null, null, Screen(0, 0, 1920, 1032, double.NaN)) is null, "No usable screen");
            Assert(Place((double.NaN, 10), null, primary) is { Remembered: false }, "A broken saved corner is ignored");
            // A screen smaller than the minimum size: the window keeps its top-left corner on it.
            Assert(Place(null, null, Screen(0, 0, 800, 560)) is { Window: { Left: 0, Top: 0, Width: 800, Height: 560 } }, "Tiny screen");
            return Task.CompletedTask;
        });
        await Test("Menu Démarrer : la copie installée le garde, une autre copie ne le remplit que s’il manque ou ne mène nulle part", () =>
        {
            var home = Path.Combine(Path.GetTempPath(), "mira-shell-" + Guid.NewGuid().ToString("N"));
            var temporary = Path.Combine(home, "Temp"); var installed = Path.Combine(home, "Programs", "Mira", "Mira.exe");
            var portable = Path.Combine(home, "Documents", "Mira", "Mira.exe"); var lookalike = Path.Combine(home, "Temporary", "Mira", "Mira.exe");
            var present = new HashSet<string> { installed, portable };
            bool Should(string executable, bool isInstalled, bool exists, string? target) => ShellRegistration.ShouldRegister(executable, isInstalled, temporary, exists, target, present.Contains);
            Assert(Should(installed, true, true, portable), "The installed copy takes the shortcut back from a portable one");
            Assert(Should(installed, true, false, null), "The installed copy writes a missing shortcut");
            Assert(!Should(portable, false, true, installed), "A portable copy leaves the shortcut of the installed one");
            Assert(!Should(lookalike, false, true, portable), "Nor does another copy take one that still leads somewhere");
            Assert(Should(portable, false, false, null), "No shortcut yet: a portable copy adds one");
            Assert(Should(portable, false, true, Path.Combine(home, "Deleted", "Mira.exe")), "A shortcut to a deleted copy is replaced");
            Assert(Should(portable, false, true, null), "An unreadable shortcut is replaced");
            Assert(!Should(Path.Combine(temporary, "Temp1_Mira.zip", "Mira", "Mira.exe"), false, false, null), "A zip opened in place, under the temporary folder: never");
            Assert(!Should(Path.Combine(temporary, "Mira.exe"), true, false, null), "Never from the temporary folder, whatever it says");
            Assert(Should(lookalike, false, false, null), "Temporary is not Temp: a folder whose name only starts the same is not the temporary folder");
            return Task.CompletedTask;
        });
        await Test("Moteur mpv : téléchargé, vérifié et extrait dans le profil ; altéré ou incomplet, jamais installé", async () =>
        {
            // A small archive shaped like mpv's Windows builds: libmpv-2.dll and its headers.
            var archive = Convert.FromBase64String("N3q8ryccAAQSc70DwgAAAAAAAAAiAAAAAAAAABwWbsgBADQvKiBlbi10ZXRlICovCmZhdXggbW90ZXVyIG1wdiBwb3VyIGxlcyB0ZXN0cyBkZSBNaXJhCgAAAIEzB64P0YkKnKCQoHdexUAYjoQJNqTpTtdkwHH/AlkW0BAA/Bkz01eGX3Anuuait2gpZTXZf9jAC7J3/dXvw+tz35fkNTgXZ9k+YJs57syaM+RclR0pc/nBp46uaTmsVW1bYGi/WuClG7sn7CLBB8/0KodMIp0c0SrMXAIplkuFPaS65AAAABcGOQEJgIkABwsBAAEjAwEBBV0AEAAADIDWCgF5nvznAAA=");
            MpvPackage Package(string? libraryHash = null) => new("test-1", new("https://downloads.example.test/mpv/mpv-dev-test.7z"), archive.Length,
                "9ec0036370600d637114441debcdd6ba663a3b295264dff5240c28557c7f934d", libraryHash ?? "7306c02a18fb46f612ba938a15f9e7b8c0313c1f1e1eb67ead4565729a7403c2");
            var requests = 0; var served = archive; var status = HttpStatusCode.OK;
            var handler = new Handler(_ => { Interlocked.Increment(ref requests); return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(served) }); });
            var profile = Path.Combine(testRoot, "engine");
            var path = await MpvInstaller.InstallAsync(profile, Package(), handler);
            Assert(path == MpvInstaller.LibraryPath(profile) && File.ReadAllText(path) == "faux moteur mpv pour les tests de Mira\n" && MpvInstaller.IsInstalled(profile, Package()), "Engine not installed");
            var kept = Directory.GetFiles(MpvInstaller.Folder(profile)).Select(Path.GetFileName).Order().ToArray();
            Assert(kept.SequenceEqual(new[] { "libmpv-2.dll", "SOURCE.txt", "version.txt" }.Order()), "Unexpected files: " + string.Join(", ", kept));
            await MpvInstaller.InstallAsync(profile, Package(), handler);
            Assert(requests == 1 && !MpvInstaller.IsInstalled(profile, Package() with { Version = "test-2" }), "Installed engine downloaded again, or another version taken for it");
            async Task Refused(string folder, MpvPackage package, string expected)
            {
                var target = Path.Combine(testRoot, folder);
                try { await MpvInstaller.InstallAsync(target, package, handler); throw new Exception("Installed: " + folder); }
                catch (InstallException ex) { Assert(ex.Message.Contains(expected), $"{folder}: {ex.Message}"); }
                Assert(!MpvInstaller.IsInstalled(target, package) && !Directory.EnumerateFiles(MpvInstaller.Folder(target)).Any(), folder + " left files behind");
            }
            served = archive.ToArray(); served[^8] ^= 0xFF;
            await Refused("engine-altered", Package(), "ne correspond pas à la version vérifiée");
            served = [.. archive, 0, 0, 0];
            await Refused("engine-longer", Package(), "n’est pas celui attendu");
            served = archive;
            await Refused("engine-library", Package(new string('0', 64)), "extrait ne correspond pas");
            status = HttpStatusCode.NotFound;
            await Refused("engine-missing", Package(), "404");
            var current = MpvPackage.Current;
            Assert(current.Url.Scheme == "https" && current.ArchiveName.EndsWith(".7z") && current.ArchiveSha256.Length == 64 && current.LibrarySha256.Length == 64 && current.Size > 1_000_000, "Pinned engine");
        });
        await Test("Nouveautés : bienvenue sur un profil neuf, points forts après une mise à jour, notes de version courtes, comme Mira web (tests/fixtures/whats-new.json)", () =>
        {
            var all = WhatsNew.All;
            Assert(all.Count >= 2 && all.Select(x => x.Number).SequenceEqual(all.Select(x => x.Number).OrderDescending()), "Versions not newest first");
            foreach (var release in all)
                Assert(release.Summary.Length is > 10 and <= 100 && release.Items.Count is >= 2 and <= 5 && release.Items.All(x => x.Icon.Length > 0 && x.Title.Length is > 3 and <= 50 && x.Text.Length is > 10 and <= 200),
                    "Highlights of " + release.Version + " are not short");
            // Each version written in English too (its "en" in WhatsNew.json), as short, never the French left in place.
            foreach (var release in all)
                Assert(release.EnglishSummary.Length is > 10 and <= 100 && release.EnglishSummary != release.FrenchSummary
                    && release.Items.All(x => x.EnglishTitle.Length is > 3 and <= 50 && x.EnglishText.Length is > 10 and <= 200 && x.EnglishTitle != x.FrenchTitle && x.EnglishText != x.FrenchText)
                    && !System.Text.RegularExpressions.Regex.IsMatch(release.EnglishSummary + string.Concat(release.Items.Select(x => x.EnglishTitle + x.EnglishText)), "[éèêàùçôîœ«»]"),
                    "English highlights of " + release.Version + " are missing, French or not short");
            // The version being built has its main points: the screen after the update and the release page need them.
            var project = Path.Combine("src", "Mira.Desktop", "Mira.Desktop.csproj");
            if (File.Exists(project) && System.Text.RegularExpressions.Regex.Match(File.ReadAllText(project), "<Version>([^<]+)</Version>") is { Success: true } version)
                Assert(all.Any(x => x.Version == version.Groups[1].Value), $"WhatsNew.json has no entry for {version.Groups[1].Value}");
            var entries = WhatsNew.Parse("""[{"version":"0.5.4","summary":"Quatre","items":[]},{"version":"0.5.6","summary":"Six","items":[]},{"version":"0.5.5","summary":"Cinq","items":[]}]""");
            Assert(entries.Select(x => x.Version).SequenceEqual(["0.5.6", "0.5.5", "0.5.4"]), "Parsed order");
            Assert(WhatsNew.Since(new Version(0, 5, 3), new Version(0, 5, 5), entries).Select(x => x.Version).SequenceEqual(["0.5.5", "0.5.4"]), "Versions skipped by an update");
            Assert(WhatsNew.Since(null, new Version(0, 5, 5), entries).Select(x => x.Version).SequenceEqual(["0.5.5"]), "Unknown previous version");
            var decisions = new[]
            {
                WhatsNew.Decide("", new Version(0, 5, 5), knownProfile: false, entries), WhatsNew.Decide("", new Version(0, 5, 5), knownProfile: true, entries),
                WhatsNew.Decide("0.5.4", new Version(0, 5, 5), true, entries), WhatsNew.Decide("0.5.5", new Version(0, 5, 5), true, entries),
                WhatsNew.Decide("0.5.6", new Version(0, 5, 5), true, entries), WhatsNew.Decide("0.5.6", new Version(0, 5, 7), true, entries),
                WhatsNew.Decide("pas une version", new Version(0, 5, 5), false, entries),
            };
            Assert(decisions.SequenceEqual([StartupScreen.Welcome, StartupScreen.WhatsNew, StartupScreen.WhatsNew, StartupScreen.None, StartupScreen.None, StartupScreen.None, StartupScreen.Welcome]),
                "Startup screens: " + string.Join(", ", decisions));
            // The versions shown after an update, here and by Mira web (js/whats-new.js), with the points Mira web shows.
            using var shared = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository() ?? throw new Exception("Repository not found"), "tests", "fixtures", "whats-new.json")));
            var sharedEntries = WhatsNew.Parse(shared.RootElement.GetProperty("entries").GetRawText());
            static string Names(JsonElement list) => string.Join(" ", list.EnumerateArray().Select(x => x.GetString()));
            foreach (var row in shared.RootElement.GetProperty("cases").EnumerateArray())
            {
                var seen = row.GetProperty("seen").GetString()!; var current = ReleaseFeed.ParseVersion(row.GetProperty("current").GetString()!)!;
                var shown = WhatsNew.Since(ReleaseFeed.ParseVersion(seen), current, sharedEntries);
                Assert(string.Join(" ", shown.Select(x => x.Version)) == Names(row.GetProperty("since")), $"Since {seen} up to {current}: " + string.Join(" ", shown.Select(x => x.Version)));
                Assert(string.Join(" ", shown.Where(x => x.Items.Any(i => i.Web)).Select(x => x.Version)) == Names(row.GetProperty("phone")), $"Mira web, since {seen} up to {current}");
            }
            var notes = WhatsNew.ReleaseNotes(all[0]); var unsigned = WhatsNew.ReleaseNotes(all[0], signed: false);
            Assert(notes.StartsWith("**Mira** is a cinema-style Jellyfin client") && notes.Contains("\n\n" + all[0].EnglishSummary + "\n\n") && all[0].Items.All(x => notes.Contains($"**{x.EnglishTitle}**")) && notes.Contains($"Mira-{all[0].Version}-win-x64-setup.exe") && notes.Contains($"Mira-{all[0].Version}-mac-arm64.dmg") && notes.Contains("/docs/INSTALLATION-WEB.md")
                && notes.Contains($"/blob/v{all[0].Version}/CHANGELOG.md") && notes.Contains("**Code signing policy**") && notes.Contains("certificate by [SignPath Foundation]") && notes.Contains("#code-signing-policy") && notes.Length < 2000, "Release notes (" + notes.Length + "):\n" + notes);
            Assert(unsigned.Contains("**Code signing policy**") && unsigned.Contains("are not signed") && !unsigned.Contains("certificate by") && unsigned.Contains("#code-signing-policy"), "Release notes, unsigned:\n" + unsigned);
            // The banner on top, from the tag (tools/banner, docs/releases), the rest unchanged.
            var banner = WhatsNew.ReleaseNotes(all[0], signed: false, banner: true);
            var image = $"![Mira {all[0].Version}](https://raw.githubusercontent.com/sasou-web/Mira/v{all[0].Version}/docs/releases/{all[0].Version}.jpg)\n\n";
            Assert(banner == image + unsigned, "Release notes with the banner:\n" + banner);
            // Mira in English: the screen after an update speaks English, the release page is always English (Mira.Release runs in English, and so does the page).
            Lang.Use(Lang.English);
            var point = all[0].Items[0];
            Assert(all[0].Summary == all[0].EnglishSummary && point.Title == point.EnglishTitle && point.Text == point.EnglishText && WhatsNew.ReleaseNotes(all[0]) == notes, "What’s new in English");
            Lang.Use(Lang.French);
            Assert(WhatsNew.ReleaseNotes(all[0]) == notes, "Release notes do not depend on Mira’s language");
            Lang.Use(Lang.French);
            Assert(WhatsNew.Parse("""[{"version":"0.5.4","summary":"Quatre","items":[{"icon":"check","title":"Titre","text":"Texte"}]}]""")[0] is { EnglishSummary: "Quatre", Items: [{ EnglishTitle: "Titre", EnglishText: "Texte" }] }, "Without English, the French");
            return Task.CompletedTask;
        });
        await Test("Mises à jour : manifeste signé accepté ; modifié, mal formé ou d’une autre clé, refusé", () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifest = new UpdateManifest("Mira", new Version(0, 5, 3), [new("zip", "Mira-0.5.3-win-x64.zip", 10, new string('a', 64))]).ToJson();
            var signature = UpdateSignature.Sign(manifest, key); string[] trusted = [UpdateSignature.PublicKey(key)];
            Assert(UpdateSignature.Verify(manifest, signature, trusted), "Valid signature refused");
            var altered = manifest.ToArray(); altered[^3] ^= 1;
            Assert(!UpdateSignature.Verify(altered, signature, trusted), "Altered manifest accepted");
            Assert(!UpdateSignature.Verify(manifest, UpdateSignature.Sign(manifest, other), trusted) && !UpdateSignature.Verify(manifest, signature, ["garbage", ""]), "Another or an invalid key accepted");
            Assert(!UpdateSignature.Verify(manifest, signature[..63], trusted) && UpdateSignature.Decode("pas du base64") is null && UpdateSignature.Decode(UpdateSignature.Encode(signature)) is { Length: 64 }, "Signature encoding");
            Assert(UpdateKeys.Trusted.Count > 0 && UpdateKeys.Trusted.All(k => { using var e = ECDsa.Create(); e.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _); return e.KeySize == 256; }), "Built-in key missing or not P-256");
            var parsed = UpdateManifest.Parse(manifest);
            Assert(parsed.Version == new Version(0, 5, 3) && parsed.Files.Single() is { Kind: "zip", Name: "Mira-0.5.3-win-x64.zip", Size: 10 }, "Manifest round trip");
            string Json(string files, string product = "Mira", string version = "0.5.3") => $$"""{"product":"{{product}}","version":"{{version}}","files":[{{files}}]}""";
            var good = $$"""{"kind":"zip","name":"Mira-0.5.3-win-x64.zip","size":10,"sha256":"{{new string('a', 64)}}"}""";
            foreach (var bad in new[] { Json(good, product: "Autre"), Json(good, version: "0.5"), Json(""), Json(good.Replace("Mira-0.5.3-win-x64.zip", "..\\\\evil.exe")), Json(good.Replace("\"size\":10", "\"size\":0")),
                Json(good.Replace(new string('a', 64), "abc")), Json(good + "," + good), Json(good.Replace("\"zip\"", "\"script\"")), "{" })
            {
                var rejected = false; try { UpdateManifest.Parse(Encoding.UTF8.GetBytes(bad)); } catch (UpdateException) { rejected = true; }
                Assert(rejected, "Invalid manifest accepted: " + bad);
            }
            return Task.CompletedTask;
        });
        await Test("Mises à jour Mac : l’image disque signée est proposée depuis « others », vérifiée au téléchargement", async () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] zip = Encoding.UTF8.GetBytes("archive"), dmg = Encoding.UTF8.GetBytes("image disque du Mac");
            string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var dmgName = UpdateManifest.OtherName("mac", new Version(0, 8, 8));
            var manifest = new UpdateManifest("Mira", new Version(0, 8, 8), [new("zip", "Mira-0.8.8-win-x64.zip", zip.Length, Sha(zip))]) { Others = [new("mac", dmgName, dmg.Length, Sha(dmg))] }.ToJson();
            var files = new Dictionary<string, byte[]> { ["mira-update.json"] = manifest, ["mira-update.json.sig"] = Encoding.ASCII.GetBytes(UpdateSignature.Encode(UpdateSignature.Sign(manifest, key))), [dmgName] = dmg };
            var release = new
            {
                tag_name = "v0.8.8", draft = false, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.8.8",
                assets = files.Select(f => new { name = f.Key, size = f.Value.Length, browser_download_url = $"https://github.com/sasou-web/Mira/releases/download/v0.8.8/{f.Key}" }).ToArray()
            };
            using var handler = new Handler(request =>
            {
                var url = request.RequestUri!;
                if (url.Host == "api.github.com") return Task.FromResult(JsonResponse(new[] { release }));
                return Task.FromResult(files.TryGetValue(Path.GetFileName(url.AbsolutePath), out var bytes) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var client = new UpdateClient(new Version(0, 8, 7), [UpdateSignature.PublicKey(key)], handler: handler);
            var offer = await client.CheckAsync("mac");
            Assert(offer is { File.Kind: "mac" } && offer.File.Name == dmgName, "The Mac disk image of the signed manifest is not offered");
            var folder = Path.Combine(testRoot, "update-mac");
            Assert(File.ReadAllBytes(await client.DownloadAsync(offer!, folder)).SequenceEqual(dmg), "The disk image is not downloaded");
            File.Delete(Path.Combine(folder, dmgName)); files[dmgName] = Encoding.UTF8.GetBytes("image disque du Maq");
            var refused = false;
            try { await client.DownloadAsync(offer!, folder); } catch (UpdateException) { refused = true; }
            Assert(refused && !File.Exists(Path.Combine(folder, dmgName)), "A disk image that does not match the signature was kept");
        });
        await Test("Mises à jour Mac : le script remplace Mira.app après sa fermeture, et laisse l’ancienne quand la nouvelle manque", async () =>
        {
            if (OperatingSystem.IsWindows()) return;
            var root = Path.Combine(testRoot, "mac-install", "Applications"); Directory.CreateDirectory(root);
            var app = Path.Combine(root, "Mira.app");
            void Bundle(string path, string version) { Directory.CreateDirectory(Path.Combine(path, "Contents", "MacOS")); File.WriteAllText(Path.Combine(path, "Contents", "version"), version); }
            Bundle(app, "0.8.7"); Bundle(MacInstall.Staging(app), "0.8.8");
            Assert(MacInstall.Staging(app) == Path.Combine(root, ".Mira.app.update") && MacInstall.Previous(app) == Path.Combine(root, ".Mira.app.old"), "Staging beside the bundle");
            Assert(MacInstall.Problem(app) == MacInstallProblem.None, "A writable bundle can't be replaced");
            Assert(MacInstall.Problem("/private/var/folders/x/AppTranslocation/1234/d/Mira.app") == MacInstallProblem.Translocated, "A translocated copy");
            // Mira still running: the script waits for it.
            using var mira = Process.Start(new ProcessStartInfo("/bin/sleep", "1") { UseShellExecute = false })!;
            async Task Swap(int process, string opener)
            {
                var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                foreach (var argument in MacInstall.Arguments(process, app, Path.Combine(root, MacInstall.ResultName), new Version(0, 8, 8), opener)) start.ArgumentList.Add(argument);
                using var shell = Process.Start(start)!;
                await shell.WaitForExitAsync();
            }
            var watch = Stopwatch.StartNew();
            await Swap(mira.Id, "/bin/true");
            Assert(watch.Elapsed > TimeSpan.FromMilliseconds(500) && mira.HasExited, "The swap did not wait for Mira to quit");
            Assert(File.ReadAllText(Path.Combine(app, "Contents", "version")) == "0.8.8" && !Directory.Exists(MacInstall.Staging(app)) && !Directory.Exists(MacInstall.Previous(app)), "The new bundle is not in place");
            Assert(MacInstall.ReadResult(root) is { Installed: true, Version: { Major: 0, Minor: 8, Build: 8 } } && MacInstall.ReadResult(root) is null, "The result is not read, once");
            // Nothing staged: the bundle stays as it is.
            await Swap(mira.Id, "");
            Assert(File.ReadAllText(Path.Combine(app, "Contents", "version")) == "0.8.8" && MacInstall.ReadResult(root) is { Installed: false, Step: "missing" }, "A missing new version was not reported, or broke the bundle");
            // A folder this account can't write to (not for root, who writes anywhere).
            if (Environment.UserName != "root")
            {
                var locked = Path.Combine(testRoot, "mac-install", "Locked"); Bundle(Path.Combine(locked, "Mira.app"), "0.8.7");
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try { Assert(MacInstall.Problem(Path.Combine(locked, "Mira.app")) == MacInstallProblem.ReadOnly, "A read-only folder can be replaced"); }
                finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            }
        });
        await Test("Mises à jour : la version signée la plus récente est proposée, jamais une antérieure ; téléchargement vérifié", async () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var files = new Dictionary<string, byte[]>();
            byte[] zip = Encoding.UTF8.GetBytes("nouvelle archive"), setup = Encoding.UTF8.GetBytes("nouvel installateur");
            string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var manifest = new UpdateManifest("Mira", new Version(0, 5, 3), [new("zip", "Mira-0.5.3-win-x64.zip", zip.Length, Sha(zip)), new("installer", "Mira-0.5.3-win-x64-setup.exe", setup.Length, Sha(setup))]).ToJson();
            files["/0.5.3/mira-update.json"] = manifest; files["/0.5.3/mira-update.json.sig"] = Encoding.ASCII.GetBytes(UpdateSignature.Encode(UpdateSignature.Sign(manifest, key)));
            files["/0.5.3/Mira-0.5.3-win-x64.zip"] = zip; files["/0.5.3/Mira-0.5.3-win-x64-setup.exe"] = setup;
            object Asset(string version, string name) => new { name, size = files[$"/{version}/{name}"].Length, browser_download_url = $"https://github.com/sasou-web/Mira/releases/download/v{version}/{name}" };
            var releases = new object[]
            {
                new { tag_name = "v0.6.0", draft = true, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.6.0", assets = new[] { Asset("0.5.3", "mira-update.json") } },
                new { tag_name = "v0.5.4", draft = false, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.5.4", assets = Array.Empty<object>() },
                new { tag_name = "nightly", draft = false, html_url = "https://github.com/sasou-web/Mira/releases/tag/nightly", assets = Array.Empty<object>() },
                new { tag_name = "v0.5.3", draft = false, prerelease = true, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.5.3",
                    assets = new[] { Asset("0.5.3", "mira-update.json"), Asset("0.5.3", "mira-update.json.sig"), Asset("0.5.3", "Mira-0.5.3-win-x64.zip"), Asset("0.5.3", "Mira-0.5.3-win-x64-setup.exe") } },
                new { tag_name = "v0.5.1", draft = false, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.5.1", assets = Array.Empty<object>() }
            };
            var requested = new List<Uri>();
            using var handler = new Handler(request =>
            {
                var url = request.RequestUri!; requested.Add(url);
                if (url.Host == "api.github.com") return Task.FromResult(JsonResponse(releases));
                var path = url.AbsolutePath.Replace("/sasou-web/Mira/releases/download/v", "/");
                return Task.FromResult(files.TryGetValue(path, out var bytes) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch (UpdateException) { return true; } }
            using var client = new UpdateClient(new Version(0, 5, 2), [UpdateSignature.PublicKey(key)], handler: handler);
            var offer = await client.CheckAsync("zip");
            Assert(offer is { Version: { Major: 0, Minor: 5, Build: 3 }, File.Name: "Mira-0.5.3-win-x64.zip" } && offer.Page?.AbsoluteUri == "https://github.com/sasou-web/Mira/releases/tag/v0.5.3", "Newest signed release not offered");
            Assert((await client.CheckAsync("installer"))?.File.Name == "Mira-0.5.3-win-x64-setup.exe" && await Fails(() => client.CheckAsync("portable")), "File for the installation kind");
            Assert(requested.All(u => u.Host is "api.github.com" or "github.com"), "Another host was contacted");
            using (var same = new UpdateClient(new Version(0, 5, 3), [UpdateSignature.PublicKey(key)], handler: handler)) Assert(await same.CheckAsync("zip") is null, "The running version is offered again");
            using (var newer = new UpdateClient(new Version(0, 6, 1), [UpdateSignature.PublicKey(key)], handler: handler)) Assert(await newer.CheckAsync("zip") is null, "An older version is offered");
            using (var stranger = new UpdateClient(new Version(0, 5, 2), [UpdateSignature.PublicKey(other)], handler: handler)) Assert(await Fails(() => stranger.CheckAsync("zip")), "A manifest signed by an unknown key is accepted");
            var folder = Path.Combine(testRoot, "update-download");
            var path = await client.DownloadAsync(offer!, folder);
            Assert(File.ReadAllBytes(path).SequenceEqual(zip) && !File.Exists(path + ".partial") && UpdateClient.Matches(path, offer!.File), "Download not kept or not verified");
            Assert(await client.DownloadAsync(offer!, folder) == path, "A verified download is fetched again");
            File.Delete(path); files["/0.5.3/Mira-0.5.3-win-x64.zip"] = Encoding.UTF8.GetBytes("nouvelle archivf");
            Assert(await Fails(() => client.DownloadAsync(offer!, folder)) && !Directory.EnumerateFiles(folder).Any(), "A download that does not match the signature was kept");
            Assert(await Fails(() => client.DownloadAsync(offer! with { Url = new Uri("https://example.com/Mira.zip") }, folder)) && requested.All(u => u.Host != "example.com"), "A download outside GitHub was attempted");
            var feedRefused = false; try { new UpdateClient(new Version(0, 5, 2), [], new Uri("https://example.com/releases")).Dispose(); } catch (ArgumentException) { feedRefused = true; }
            Assert(feedRefused, "A feed outside GitHub was accepted");
        });
        await Test("Mises à jour : chaque redirection vérifiée, reprise d’un téléchargement interrompu, réponse inattendue refusée", async () =>
        {
            var payload = Encoding.UTF8.GetBytes(new string('m', 200_000));
            var file = new UpdateFile("zip", "Mira-0.5.3-win-x64.zip", payload.Length, Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
            var requested = new List<Uri>(); var honourRange = true; var redirect = "https://release-assets.githubusercontent.com/github-production-release-asset/1/a";
            using var handler = new Handler(request =>
            {
                var url = request.RequestUri!; requested.Add(url);
                if (url.Host == "github.com") { var moved = new HttpResponseMessage(HttpStatusCode.Found); moved.Headers.Location = new Uri(redirect); return Task.FromResult(moved); }
                if (request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } from && honourRange)
                {
                    var rest = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[(int)from..]) };
                    rest.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
                    return Task.FromResult(rest);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            });
            async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch (UpdateException) { return true; } }
            using var client = new UpdateClient(new Version(0, 5, 2), [], handler: handler);
            var offer = new UpdateOffer(new Version(0, 5, 3), "v0.5.3", null, file, new Uri("https://github.com/sasou-web/Mira/releases/download/v0.5.3/Mira-0.5.3-win-x64.zip"));
            var folder = Path.Combine(testRoot, "update-resume"); Directory.CreateDirectory(folder);
            // Interrupted after 80 000 bytes: the rest is asked for with Range and the hash covers the whole file.
            File.WriteAllBytes(Path.Combine(folder, file.Name + ".partial"), payload[..80_000]);
            var path = await client.DownloadAsync(offer, folder);
            Assert(File.ReadAllBytes(path).SequenceEqual(payload) && requested.Any(u => u.Host == "release-assets.githubusercontent.com"), "Download not resumed through GitHub's file server");
            File.Delete(path); File.WriteAllBytes(Path.Combine(folder, file.Name + ".partial"), payload[..80_000]); honourRange = false;
            Assert(File.ReadAllBytes(await client.DownloadAsync(offer, folder)).SequenceEqual(payload), "A server ignoring Range was not downloaded again from the start");
            File.Delete(Path.Combine(folder, file.Name)); requested.Clear(); redirect = "https://evil.example/Mira.zip";
            Assert(await Fails(() => client.DownloadAsync(offer, folder)) && requested.All(u => u.Host != "evil.example"), "A redirect outside GitHub was followed");
            redirect = "http://release-assets.githubusercontent.com/x";
            Assert(await Fails(() => client.DownloadAsync(offer, folder)) && requested.All(u => u.Scheme == "https"), "A redirect to plain HTTP was followed");
            foreach (var bad in new[] { "[1, \"x\", null]", "[{\"tag_name\":\"v0.5.3\",\"assets\":[1,{\"name\":\"a\",\"size\":\"big\",\"browser_download_url\":\"https://github.com/a\"}]}]", "{\"message\":\"Not Found\"}" })
            {
                var parsed = false; try { var releases = ReleaseFeed.Parse(bad); parsed = releases.All(r => r.Assets.All(a => a.Size == -1)); } catch (UpdateException) { parsed = true; }
                Assert(parsed, "Unexpected JSON not handled: " + bad);
            }
            var manifestRefused = false; try { UpdateManifest.Parse(Encoding.UTF8.GetBytes("{\"product\":\"Mira\",\"version\":\"0.5.3\",\"files\":[1,2]}")); } catch (UpdateException) { manifestRefused = true; }
            Assert(manifestRefused, "A manifest with unexpected entries was not refused cleanly");
        });
        await Test("Mises à jour : seules les pages de release de Mira s’ouvrent dans le navigateur (Windows et Mac)", () =>
        {
            foreach (var good in new[] { "https://github.com/sasou-web/Mira/releases/tag/v0.7.3", "https://github.com/sasou-web/Mira/releases/latest" })
                Assert(ReleaseFeed.ReleasePage(good)?.AbsoluteUri == good, "Mira's release page refused: " + good);
            foreach (var bad in new[] { "http://github.com/sasou-web/Mira/releases/tag/v0.7.3", "https://github.com.evil.example/sasou-web/Mira/releases/tag/v0.7.3",
                "https://evil.example/sasou-web/Mira/releases/", "https://github.com/someone/Mira/releases/tag/v0.7.3", "https://github.com/sasou-web/Mira/issues/1",
                "https://github.com/sasou-web/Mira/releases/../../other/x", "https://github.com:8443/sasou-web/Mira/releases/tag/v1.0.0",
                "https://user@github.com/sasou-web/Mira/releases/tag/v1.0.0", "file:///sasou-web/Mira/releases/x", "/sasou-web/Mira/releases/x",
                "javascript:alert(1)", "-a Calculator", "", null })
                Assert(ReleaseFeed.ReleasePage(bad) is null, "Another page accepted: " + bad);
            Assert(ReleaseFeed.ReleasePage((Uri?)null) is null && ReleaseFeed.ReleasePage(new Uri("relative/releases/", UriKind.Relative)) is null, "No page or a relative one accepted");
            return Task.CompletedTask;
        });
        await Test("Mises à jour : le manifeste signe aussi le Mac et Mira web, et les copies jusqu’à 0.7.4 le lisent comme avant", () =>
        {
            var sha = new string('b', 64); var version = new Version(0, 7, 5);
            var manifest = new UpdateManifest("Mira", version, [new("installer", "Mira-0.7.5-win-x64-setup.exe", 30, sha), new("zip", "Mira-0.7.5-win-x64.zip", 10, sha)])
            { Others = [new("mac", "Mira-0.7.5-mac-arm64.dmg", 20, sha), new("jellyfin", "mira-jellyfin-0.7.5.zip", 5, sha)] };
            var json = manifest.ToJson(); var parsed = UpdateManifest.Parse(json);
            Assert(parsed.Version == version && parsed.Files.Select(f => f.Kind).SequenceEqual(["installer", "zip"])
                && parsed.Others.Select(f => (f.Kind, f.Name, f.Size, f.Sha256)).SequenceEqual([("mac", "Mira-0.7.5-mac-arm64.dmg", 20L, sha), ("jellyfin", "mira-jellyfin-0.7.5.zip", 5L, sha)]), "Round trip with the other platforms");
            Assert(parsed.ToJson().SequenceEqual(json), "Written differently after a round trip");
            // Installed copies up to 0.7.4 read "files" alone, and refuse there any kind they do not know: they still update.
            var old = Parse074(json);
            Assert(old.Version == version && old.Files.Select(f => f.Kind).SequenceEqual(["installer", "zip"]), "Mira 0.7.4 does not read the new manifest");
            var windows = new UpdateManifest("Mira", version, manifest.Files).ToJson();
            Assert(!Encoding.UTF8.GetString(windows).Contains("others") && UpdateManifest.Parse(windows).Others.Count == 0 && Parse074(windows).Files.Count == 2, "A Windows-only manifest changed");
            var refusedBy074 = false; try { Parse074(new UpdateManifest("Mira", version, [.. manifest.Files, manifest.Others[0]]).ToJson()); } catch (UpdateException) { refusedBy074 = true; }
            Assert(refusedBy074 && UpdateManifest.OtherKinds.All(k => !UpdateManifest.Kinds.Contains(k)), "A Mac file among the Windows ones would stop older copies from updating");
            // A kind added later is skipped in "others". A known one that cannot be used is skipped too for Windows updates,
            // and refuses the manifest when Mira web or the release tooling read it (strict).
            string Json(string files, string others) => $$"""{"product":"Mira","version":"0.7.5","files":[{{files}}],"others":{{others}}}""";
            var zip = $$"""{"kind":"zip","name":"Mira-0.7.5-win-x64.zip","size":10,"sha256":"{{sha}}"}""";
            var mac = $$"""{"kind":"mac","name":"Mira-0.7.5-mac-arm64.dmg","size":20,"sha256":"{{sha}}"}""";
            foreach (var strict in new[] { false, true })
                Assert(UpdateManifest.Parse(Encoding.UTF8.GetBytes(Json(zip, $$"""[{"kind":"linux","name":"../x","size":"?"},{{mac}}]""")), strict).Others.Single().Kind == "mac", "A kind added later was not skipped");
            bool Refused(string json, bool strict) { try { UpdateManifest.Parse(Encoding.UTF8.GetBytes(json), strict); return false; } catch (UpdateException) { return true; } }
            foreach (var bad in new[] { Json(zip, "{}"), Json(zip, "[1]"), Json(zip, $"[{mac},{mac}]"), Json(zip, $"[{mac.Replace("\"size\":20", "\"size\":0")}]"), Json(zip, $"[{mac.Replace(sha, "abc")}]"),
                Json(zip, $"[{mac.Replace("Mira-0.7.5-mac-arm64.dmg", "../evil.dmg")}]"), Json(zip, $"[{mac.Replace("\"size\":20", "\"size\":2147483648")}]") })
            {
                Assert(Refused(bad, strict: true), "Invalid manifest accepted by the strict reading: " + bad);
                var lenient = UpdateManifest.Parse(Encoding.UTF8.GetBytes(bad));
                Assert(lenient.Files.Single().Kind == "zip" && lenient.Others.Count <= 1 && lenient.Others.All(f => f.Name == "Mira-0.7.5-mac-arm64.dmg" && f.Size == 20), "Windows updates stopped by another platform's entry: " + bad);
            }
            // "files" stays strict for both: it is what Windows installs.
            foreach (var bad in new[] { Json($"{zip},{mac}", "[]"), Json($"{zip},{zip}", "[]"), Json(zip.Replace(sha, "abc"), "[]") })
                Assert(Refused(bad, strict: false) && Refused(bad, strict: true), "Invalid Windows file accepted: " + bad);
            // The release tooling's manifest: the Windows files of one folder, both other platforms from another, or nothing.
            var packages = Path.Combine(testRoot, "manifest-packages"); var others = Path.Combine(testRoot, "manifest-others");
            Directory.CreateDirectory(packages); Directory.CreateDirectory(others);
            File.WriteAllText(Path.Combine(packages, "Mira-0.7.5-win-x64.zip"), "zip"); File.WriteAllText(Path.Combine(others, "Mira-0.7.5-mac-arm64.dmg"), "dmg");
            var created = false; try { UpdateManifest.Create(version, packages, others); created = true; } catch (UpdateException) { }
            Assert(!created && UpdateManifest.Create(version, packages).Others.Count == 0, "A manifest was made without Mira web's plugin");
            File.WriteAllText(Path.Combine(others, "mira-jellyfin-0.7.5.zip"), "plugin");
            var full = UpdateManifest.Create(version, packages, others);
            Assert(full.Files.Single() is { Kind: "zip", Size: 3 } && full.Others.Select(f => f.Name).SequenceEqual(["Mira-0.7.5-mac-arm64.dmg", "mira-jellyfin-0.7.5.zip"])
                && full.Others[1].Sha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("plugin"))).ToLowerInvariant(), "Manifest of the release folders");
            return Task.CompletedTask;
        });
        await Test("Mises à jour : les copies 0.7.5 se mettent à jour même si une version suivante change les fichiers Mac ou Mira web", () =>
        {
            // What 0.7.5 copies will read (Parse075, frozen), given manifests a later release could publish.
            var sha = new string('c', 64);
            string Entry(string kind, string name, long size) => $$"""{"kind":"{{kind}}","name":"{{name}}","size":{{size}},"sha256":"{{sha}}"}""";
            string Json(string others) => $$"""{"product":"Mira","version":"0.7.9","files":[{{Entry("installer", "Mira-0.7.9-win-x64-setup.exe", 30)}},{{Entry("zip", "Mira-0.7.9-win-x64.zip", 10)}}],"others":{{others}}}""";
            var mac = Entry("mac", "Mira-0.7.9-mac-arm64.dmg", 20);
            foreach (var (name, json) in new[]
            {
                ("two Mac disk images", Json($"[{mac},{Entry("mac", "Mira-0.7.9-mac-x64.dmg", 21)}]")),
                ("a Mac disk image over 1 GiB", Json($"[{Entry("mac", "Mira-0.7.9-mac-arm64.dmg", 3L << 30)},{Entry("jellyfin", "mira-jellyfin-0.7.9.zip", 5)}]")),
                ("a name this version refuses", Json($"[{Entry("jellyfin", "mira jellyfin.zip", 5)}]")),
                ("another kind", Json($"[{Entry("linux", "Mira-0.7.9.AppImage", 40)},{mac}]")),
                ("others no longer a list", Json("{\"mac\":1}")),
            })
            {
                foreach (var parse in new Func<byte[], UpdateManifest>[] { Parse075, bytes => UpdateManifest.Parse(bytes) })
                {
                    var manifest = parse(Encoding.UTF8.GetBytes(json));
                    Assert(manifest.Version == new Version(0, 7, 9) && manifest.Files.Select(f => f.Kind).SequenceEqual(["installer", "zip"]), "Windows copies would stop updating: " + name);
                }
            }
            // Same reading as the current one on this version's own manifest.
            var own = new UpdateManifest("Mira", new Version(0, 7, 5), [new("zip", "Mira-0.7.5-win-x64.zip", 10, sha)]) { Others = [new("mac", "Mira-0.7.5-mac-arm64.dmg", 20, sha), new("jellyfin", "mira-jellyfin-0.7.5.zip", 5, sha)] }.ToJson();
            Assert(Parse075(own).Others.SequenceEqual(UpdateManifest.Parse(own).Others) && Parse075(own).Files.SequenceEqual(UpdateManifest.Parse(own).Files), "The frozen reading differs from the current one");
            return Task.CompletedTask;
        });
        await Test("Mises à jour : le .dmg et Mira web vérifiés avant de signer (SHA-256 publiée, manifeste de Jellyfin et MD5)", () =>
        {
            var version = new Version(0, 7, 5); var folder = Path.Combine(testRoot, "release-others"); Directory.CreateDirectory(folder);
            var dmg = Path.Combine(folder, "Mira-0.7.5-mac-arm64.dmg"); var plugin = Encoding.UTF8.GetBytes("extension Mira 0.7.5");
            File.WriteAllText(dmg, "image disque");
            string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            File.WriteAllText(dmg + ".sha256", $"{Sha(dmg)}  Mira-0.7.5-mac-arm64.dmg\n");
            File.WriteAllBytes(Path.Combine(folder, "mira-jellyfin-0.7.5.zip"), plugin);
            var source = "https://github.com/sasou-web/Mira/releases/download/v0.7.5/mira-jellyfin-0.7.5.zip"; var md5 = Convert.ToHexString(MD5.HashData(plugin)).ToLowerInvariant();
            void Manifest(string text) => File.WriteAllText(Path.Combine(folder, "jellyfin-manifest.json"), text);
            bool Refused(Action check) { try { check(); return false; } catch (UpdateException) { return true; } }
            Manifest(JellyfinRepository("0.7.5.0", source, md5));
            ReleaseChecks.CheckOthers(version, folder);
            // shasum's binary mode, and a Windows line ending, are the same file.
            File.WriteAllText(dmg + ".sha256", $"{Sha(dmg).ToUpperInvariant()} *Mira-0.7.5-mac-arm64.dmg\r\n"); ReleaseChecks.CheckOthers(version, folder);
            File.WriteAllText(dmg + ".sha256", $"{Sha(dmg)}  Mira-0.7.4-mac-arm64.dmg\n"); Assert(Refused(() => ReleaseChecks.CheckOthers(version, folder)), "A SHA-256 for another file accepted");
            File.WriteAllText(dmg + ".sha256", $"{new string('0', 64)}  Mira-0.7.5-mac-arm64.dmg\n"); Assert(Refused(() => ReleaseChecks.CheckOthers(version, folder)), "A wrong SHA-256 accepted");
            File.WriteAllText(dmg + ".sha256", $"{Sha(dmg)}  Mira-0.7.5-mac-arm64.dmg\n");
            var guid = MiraWeb.PluginId.ToString();
            foreach (var (name, json) in new[]
            {
                ("other plugin", JellyfinRepository("0.7.5.0", source, md5, guid: Guid.NewGuid().ToString())), ("other name", JellyfinRepository("0.7.5.0", source, md5, name: "Autre")),
                ("other version", JellyfinRepository("0.7.4.0", source, md5)), ("latest release", JellyfinRepository("0.7.5.0", "https://github.com/sasou-web/Mira/releases/latest/download/mira-jellyfin-0.7.5.zip", md5)),
                ("other host", JellyfinRepository("0.7.5.0", source.Replace("github.com", "evil.example"), md5)), ("wrong MD5", JellyfinRepository("0.7.5.0", source, new string('a', 32))),
                ("two versions", JellyfinRepository("0.7.5.0", source, md5, versions: 2)), ("two plugins", $"[{JellyfinRepository("0.7.5.0", source, md5)[1..^1]},{JellyfinRepository("0.7.5.0", source, md5)[1..^1]}]"),
                ("not JSON", "<html>"), ("an object", "{}"), ("no versions", $$"""[{"guid":"{{guid}}","name":"Mira"}]"""),
            })
            {
                Manifest(json);
                Assert(Refused(() => ReleaseChecks.CheckOthers(version, folder)), "Jellyfin manifest accepted: " + name);
            }
            Manifest(JellyfinRepository("0.7.5.0", source, md5.ToUpperInvariant())); ReleaseChecks.CheckOthers(version, folder);
            File.Delete(Path.Combine(folder, "mira-jellyfin-0.7.5.zip")); Assert(Refused(() => ReleaseChecks.CheckOthers(version, folder)), "A missing plugin accepted");
            return Task.CompletedTask;
        });
        await Test("Mises à jour : copie en deux temps, dossier intact si la préparation échoue, résultat annoncé une fois", () =>
        {
            var root = Path.Combine(testRoot, "update-two-phase"); var app = Path.Combine(root, "app"); var incoming = Path.Combine(root, "incoming");
            Directory.CreateDirectory(Path.Combine(app, "data", "updates")); Directory.CreateDirectory(incoming);
            File.WriteAllText(Path.Combine(app, "Mira.dll"), "ancienne"); File.WriteAllText(Path.Combine(app, "b.dll"), "ancienne b");
            File.WriteAllText(Path.Combine(incoming, "Mira.dll"), "nouvelle"); File.WriteAllText(Path.Combine(incoming, "b.dll"), "nouvelle b");
            // The new file cannot even be prepared: nothing of the installed copy has moved.
            using (new FileStream(Path.Combine(app, "b.dll" + UpdateApplier.NewSuffix), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var failed = false; try { UpdateApplier.CopyFolder(incoming, app, Path.Combine(app, "data", "updates", "rollback-a"), attempts: 2); } catch (IOException) { failed = true; }
                Assert(failed && File.ReadAllText(Path.Combine(app, "Mira.dll")) == "ancienne" && File.ReadAllText(Path.Combine(app, "b.dll")) == "ancienne b" && !File.Exists(Path.Combine(app, "Mira.dll" + UpdateApplier.NewSuffix)),
                    "A failed preparation touched the installed copy");
            }
            UpdateApplier.CopyFolder(incoming, app, Path.Combine(app, "data", "updates", "rollback-b"));
            Assert(File.ReadAllText(Path.Combine(app, "b.dll")) == "nouvelle b" && !Directory.EnumerateFiles(app, "*" + UpdateApplier.NewSuffix, SearchOption.AllDirectories).Any(), "Two-phase copy incomplete");
            // Result of the last installation, reported once whatever the updater of this run.
            var pending = Path.Combine(app, "data", "updates", "pending.json");
            File.WriteAllText(pending, $$"""{"Version":"{{JellyfinClient.AppVersion}}","From":"0.0.1","Attempts":1,"Reported":false}""");
            Assert(Updater.TakeResult(Path.Combine(app, "data")) is { Installed: true } && !File.Exists(pending), "An installed version was not reported, or its record stayed");
            File.WriteAllText(pending, """{"Version":"99.0.0","From":"0.0.1","Attempts":1,"Reported":false}""");
            Assert(Updater.TakeResult(Path.Combine(app, "data")) is { Installed: false } && Updater.TakeResult(Path.Combine(app, "data")) is null && File.Exists(pending), "A failed installation was not reported exactly once");
            return Task.CompletedTask;
        });
        await Test("Mises à jour : l’outil d’installation refuse ce qui n’est pas une copie de Mira, sans rien toucher, et rend son verrou", () =>
        {
            var root = Path.Combine(testRoot, "apply-refused"); var profile = Path.Combine(root, "data"); Directory.CreateDirectory(root);
            // Arguments that name no Mira copy to replace: refused before anything is read or locked.
            Assert(UpdateApplier.Run(["--apply-update", "zip", "--target", root]) == 2, "An unknown kind was accepted");
            Assert(UpdateApplier.Run(["--apply-update", "folder", "--target", "Mira"]) == 2, "A relative target was accepted");
            Assert(UpdateApplier.Run(["--apply-update", "portable"]) == 2, "No target, yet accepted");
            Assert(!Directory.Exists(profile), "A refused command line wrote in the profile");
            // A file, then a folder whose Mira.exe is not Mira: nothing replaced, no rollback, no relaunch, the reason logged.
            var notes = Path.Combine(root, "notes.txt"); File.WriteAllText(notes, "pas Mira");
            Assert(UpdateApplier.Run(["--apply-update", "portable", "--target", notes, "--data", profile, "--relaunch"]) == 1 && File.ReadAllText(notes) == "pas Mira", "Another file was replaced");
            var exe = Path.Combine(root, "Mira.exe"); File.WriteAllText(exe, "pas un exécutable");
            Assert(UpdateApplier.Run(["--apply-update", "folder", "--target", root, "--data", profile, "--relaunch"]) == 1 && File.ReadAllText(exe) == "pas un exécutable", "A folder without Mira was updated");
            Assert(!UpdateApplier.IsMira(exe) && !Directory.EnumerateDirectories(Path.Combine(profile, "updates"), "rollback-*").Any() && !File.Exists(exe + UpdateApplier.NewSuffix), "Files of the refused folder were touched");
            var log = File.ReadAllLines(Path.Combine(profile, "updates", "update.log"));
            Assert(log.Length == 2 && log.All(x => x.EndsWith("installation impossible : La cible n’est pas une copie de Mira.")), "Log: " + string.Join(" | ", log));
            // The update lock is released: Mira, waiting for it before its start, is not held back.
            Assert(!UpdateLock.Busy(profile, installer: false), "The update lock is still held");
            return Task.CompletedTask;
        });
        await Test("Mises à jour : au démarrage, rien n’est remis en place sous un Mira du même profil ouvert, ni sans échange interrompu", () =>
        {
            var profile = Path.Combine(testRoot, "recover-startup"); var instance = @"Local\Mira-Test-" + Guid.NewGuid().ToString("N");
            Assert(!UpdateApplier.RecoverAtStartup(profile, [], instance), "Nothing interrupted, yet a restart");
            // A swap of this very folder cut short, while a Mira of this profile is open: left for later, untouched.
            var journal = Path.Combine(profile, "updates", "rollback-20260101000000", UpdateApplier.InProgressMarker);
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            // No file named in it: even if this check regressed, nothing of the test's own folder would be put back.
            File.WriteAllText(journal, JsonSerializer.Serialize(new UpdateApplier.SwapJournal(AppContext.BaseDirectory, [], [])));
            Assert(UpdateApplier.Interrupted(Path.Combine(profile, "updates"), AppContext.BaseDirectory).Count == 1, "The journal was not read");
            using (var running = new Mutex(true, instance))
                Assert(!UpdateApplier.RecoverAtStartup(profile, [], instance) && File.Exists(journal), "Files restored under a running Mira");
            return Task.CompletedTask;
        });
    }
}
