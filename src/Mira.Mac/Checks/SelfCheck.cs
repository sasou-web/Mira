using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Mac.Views;

namespace Mira.Mac.Checks;

/// <summary>
/// Mira --self-check &lt;folder&gt;: walks every screen and plays videos in both renderers, then writes results.txt and
/// a capture of each screen to the folder. Exit code 1 when a check fails.
/// With --mock --media &lt;video&gt; --short &lt;clip&gt;: a stand-in Jellyfin (GitHub's Macs have no server); otherwise the
/// Jellyfin given by MIRA_CHECK_SERVER, MIRA_CHECK_USER and MIRA_CHECK_PASSWORD, signed in through the real form.
/// </summary>
public static class SelfCheck
{
    private sealed record Result(bool Ok, string Name, string Detail);

    public static async Task RunAsync(MainWindow window)
    {
        var args = window.Args;
        string Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        var output = Path.GetFullPath(Arg("--self-check") is { Length: > 0 } folder && !folder.StartsWith("--") ? folder : "mira-check");
        Directory.CreateDirectory(output);
        var results = new List<Result>();
        void Check(bool ok, string name, string detail = "") { results.Add(new(ok, name, detail)); Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}"); }
        // Each screen captured is also read as VoiceOver would: no button, field or list it can reach without a name.
        void Capture(Window shown, string folder, string name)
        {
            CaptureWindow(shown, folder, name);
            var unnamed = Unnamed(shown);
            Check(unnamed.Count == 0, $"VoiceOver : tout est nommé ({name})", string.Join(", ", unnamed.Take(8)));
            // And by the keyboard: Tab goes from control to control, never to one hidden or without a size.
            var (stops, faults) = TabWalk(shown);
            Check(stops > 0 && faults.Count == 0, $"Clavier : Tab parcourt l’écran ({name})", faults.Count > 0 ? string.Join(", ", faults.Take(8)) : $"{stops} arrêts");
        }
        var mock = args.Contains("--mock") ? new MockJellyfin(Path.GetFullPath(Arg("--media")), Path.GetFullPath(Arg("--short"))) : null;
        var exit = 1;
        try
        {
            Check(Playback.MpvPlayer.FindLibrary(window.Settings.MpvPath) is not null, "libmpv trouvé", Playback.MpvPlayer.FindLibrary(window.Settings.MpvPath) ?? "introuvable");
            window.Settings.AutoNext = true; window.Settings.RememberPosition = true; window.Settings.Volume = 150;

            // —— Sign-in ——
            if (mock is not null) await window.ActivateAsync(new Connection(MockJellyfin.Server, MockJellyfin.UserId, "Contrôle", "check-token", window.Profile.DeviceId), mock);
            else
            {
                window.ShowLogin();
                await Until(() => window.Login is not null, 5);
                Capture(window, output, "01-login");
                await window.Login!.SignInAsync(Environment.GetEnvironmentVariable("MIRA_CHECK_SERVER") ?? "", Environment.GetEnvironmentVariable("MIRA_CHECK_USER") ?? "", Environment.GetEnvironmentVariable("MIRA_CHECK_PASSWORD") ?? "");
                Check(window.Session is not null, "connexion par le formulaire", window.Login?.Error ?? window.Session?.Connection.Server ?? "");
            }
            if (window.Session is null) throw new InvalidOperationException("pas de session");

            // —— Home ——
            Check(await Until(() => window.Current is HomePage { Latest.Count: > 0 }, 20), "accueil : ajouts récents", $"{(window.Current as HomePage)?.Latest.Count} titres");
            var home = (HomePage)window.Current!;
            if (mock is not null) Check(home.Continue.Any(x => x.Id == "movie-1"), "accueil : continuer à regarder", string.Join(", ", home.Continue.Select(x => x.Name)));
            await Task.Delay(1500); Capture(window, output, "02-home");
            home.ScrollToEnd(); await Task.Delay(800); Capture(window, output, "02b-home-grid"); home.ScrollToHome();
            // A row's arrows only when it goes past the window, and a press glides about three posters along, not a page.
            Check(home.GetVisualDescendants().OfType<CardRow>().All(x => x.GetVisualDescendants().OfType<ScrollViewer>().First() is var s && x.ArrowsShown == s.Extent.Width > s.Viewport.Width + 1), "accueil : flèches des rangées seulement si elles débordent");
            var row = new CardRow("Contrôle", Enumerable.Range(0, 24).Select(_ => (Control)new Border { Width = 176, Height = 40 }));
            var rowWindow = new Window { Width = 1100, Height = 200, Content = row };
            rowWindow.Show();
            await Task.Delay(400);
            var shown = row.ArrowsShown;
            row.Glide(1);
            await Task.Delay(50); var midway = row.Offset;
            await Task.Delay(1000);
            Check(shown && midway > 0 && midway < CardRow.Step - 1 && Math.Abs(row.Offset - CardRow.Step) < 1, "rangée : défilement fluide d’environ trois affiches", $"flèches {shown}, {midway:0} px à 50 ms, {row.Offset:0} px à la fin");
            rowWindow.Close();

            // —— Films, search ——
            window.Go("Movie");
            Check(await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15), "films : grille", $"{(window.Current as BrowsePage)?.Items.Count} films");
            var movie = ((BrowsePage)window.Current!).Items.FirstOrDefault(x => x.Id == "movie-1") ?? ((BrowsePage)window.Current!).Items.First();
            await Task.Delay(1000); Capture(window, output, "03-films");
            if (window.Current is BrowsePage films) { films.ToggleFilters(); await Task.Delay(500); Capture(window, output, "03b-films-filtres"); films.ToggleFilters(); }
            window.Go("search");
            await Until(() => window.Current is BrowsePage, 5);
            var word = movie.Name.Split(' ').OrderByDescending(x => x.Length).First();
            ((BrowsePage)window.Current!).Search(word);
            Check(await Until(() => window.Current is BrowsePage search && search.Items.Any(x => x.Id == movie.Id), 15), "recherche", $"« {word} »");
            await Task.Delay(800); Capture(window, output, "04-search");
            // ⌘A in a field: the text stays readable on its selection.
            ((BrowsePage)window.Current!).SelectSearch(); await Task.Delay(300); Capture(window, output, "04b-search-selected");
            ((BrowsePage)window.Current!).Search("zqxwv");
            Check(await Until(() => window.Current is BrowsePage { Empty.IsVisible: true, Items.Count: 0 } nothing && nothing.Empty.Title.Contains("zqxwv"), 15), "recherche sans résultat : l’état vide le dit",
                $"{(window.Current as BrowsePage)?.Empty.Title} · {(window.Current as BrowsePage)?.Empty.ActionLabel}");
            await Task.Delay(500); Capture(window, output, "04-search-empty");
            if (mock is not null)
            {
                ((BrowsePage)window.Current!).Search("Jeanne");
                Check(await Until(() => window.Current is BrowsePage { People.Count: > 0 } found && found.People[0].Id == "actor-1", 15), "recherche : une personne",
                    string.Join(", ", (window.Current as BrowsePage)?.People.Select(x => x.Name) ?? []));
                await Task.Delay(800); Capture(window, output, "04-search-people");
            }

            // —— Title pages ——
            window.Open(movie);
            Check(await Until(() => window.Current is DetailPage { Item.People.Count: > 0 } or DetailPage { Item.Overview: not null }, 15), "fiche d’un film", movie.Name);
            await Task.Delay(1200); Capture(window, output, "05-film");
            window.Go("Series");
            await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15);
            var series = ((BrowsePage)window.Current!).Items.OrderByDescending(x => x.Name.Contains("Longue", StringComparison.OrdinalIgnoreCase)).First();
            window.Open(series);
            Check(await Until(() => window.Current is DetailPage { Episodes.Count: > 0 }, 20), "fiche d’une série : épisodes", $"{series.Name} : {(window.Current as DetailPage)?.Episodes.Count} épisodes");
            if (window.Current is DetailPage { Episodes.Count: > 100 } longSeries)
                Check(longSeries.SeasonLabels.Any(x => x.Contains("1–100")) && longSeries.SeasonLabels.Count() > 1, "longue saison par tranches", string.Join(" | ", longSeries.SeasonLabels));
            await Task.Delay(1200); Capture(window, output, "06-series");
            window.Go("settings");
            await Task.Delay(800); Capture(window, output, "07-settings");

            // —— Téléchargements: sent to the PC of Jellyfin ——
            if (mock is not null)
            {
                Check(await Until(() => window.Offers("downloads"), 5), "téléchargements : proposés à un administrateur");
                window.Go("downloads");
                Check(await Until(() => window.Current is DownloadsPage { Ready: true, QueueCount: > 1 }, 10), "téléchargements : le PC serveur et sa file", $"{(window.Current as DownloadsPage)?.QueueCount} lignes");
                if (window.Current is DownloadsPage downloads)
                {
                    await downloads.SearchAsync("Brume basse");
                    Check(downloads.ResultGroups == 1, "téléchargements : versions regroupées par titre", $"{downloads.ResultGroups} groupes");
                    await downloads.SendBestAsync();
                    bool sent; lock (mock.Requests) sent = mock.Requests.Contains("Brume.Basse.2024.MULTI.1080p.BluRay.x264-NOTAG");
                    Check(sent && await Until(() => downloads.QueueCount > 2, 6), "téléchargements : la meilleure version envoyée", string.Join(", ", mock.Requests));
                    await Task.Delay(800); Capture(window, output, "07-downloads");
                }
            }

            // —— Playback, OpenGL then software ——
            foreach (var software in new[] { false, true })
            {
                var renderer = software ? "logiciel" : "OpenGL";
                Environment.SetEnvironmentVariable("MIRA_SOFTWARE_VIDEO", software ? "1" : null);
                await window.PlayAsync(movie, fromStart: true);
                var player = window.Player;
                var started = await Until(() => player is { IsFileLoaded: true, Surface.Frames: > 12 }, 40);
                Check(started, $"lecture ({renderer}) : images", $"{player?.Surface?.GetType().Name}, {player?.Surface?.Frames} images{(player?.Surface is Playback.VideoView { Renderer.Length: > 0 } gl ? ", " + gl.Renderer : "")}{(player?.VideoFallback is { } why ? " — repli : " + why : "")}");
                if (player?.Engine is not { } engine) continue;
                var audio = engine.Get("current-ao");
                if (string.IsNullOrEmpty(audio)) Check(true, $"volume à 150 % ({renderer})", "pas de sortie audio sur cette machine : non mesurable");
                else Check((engine.Get("af") ?? "").Contains(VolumeBoost.Label) && Math.Abs(engine.Number("volume") - 100) < 0.5, $"volume à 150 % ({renderer})", $"{player.Volume}, sortie {audio}, af {engine.Get("af")}");
                var before = player.Position;
                Key(window, Avalonia.Input.Key.Right);
                Check(await Until(() => player.Position >= before + 8, 6), $"avance de 10 s ({renderer})", $"{before:0.0} → {player.Position:0.0}");
                var unnamed = Unnamed(window);
                Check(unnamed.Count == 0, $"VoiceOver : tout est nommé (lecteur, {renderer})", string.Join(", ", unnamed.Take(8)));
                await Task.Delay(1500);
                var colour = ScreenColour(Path.Combine(output, $"08-player-{(software ? "software" : "opengl")}.png"));
                Check(colour is null || colour > 0.2, $"image visible à l’écran ({renderer})", colour is null ? "capture d’écran indisponible" : $"{colour:P0} de pixels colorés au centre");
                Key(window, Avalonia.Input.Key.Space);
                Check(await Until(() => engine.Flag("pause"), 3), $"pause ({renderer})");
                await window.StopPlaybackAsync();
                Check(window.Player is null, $"retour à la bibliothèque ({renderer})");
            }
            Environment.SetEnvironmentVariable("MIRA_SOFTWARE_VIDEO", null);
            if (mock is not null)
            {
                await window.Session.Sync.FlushAsync();
                List<string> kinds; lock (mock.Reports) kinds = mock.Reports.Select(x => x.Kind).ToList();
                var stop = mock.Reports.LastOrDefault(x => x.Kind == "stop").Body;
                Check(kinds.Contains("start") && kinds.Contains("stop") && stop.ValueKind != System.Text.Json.JsonValueKind.Undefined && stop.GetProperty("PositionTicks").GetInt64() > 0 && stop.GetProperty("VolumeLevel").GetInt32() == 100,
                    "rapports envoyés à Jellyfin", $"{string.Join(", ", kinds.Distinct())} ; volume signalé {(stop.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "?" : stop.GetProperty("VolumeLevel").ToString())}");
            }

            // —— Next episode at the end of one ——
            var episodes = (await window.Session.Client.EpisodesAsync(series.Id)).Items;
            if (episodes.Count >= 2 && (mock is not null || episodes[0].RunTimeTicks < TimeSpan.FromMinutes(3).Ticks))
            {
                await window.PlayAsync(episodes[0], fromStart: true);
                var next = episodes[1].Id;
                Check(await Until(() => window.Player?.Item?.Id == next, 60), "épisode suivant automatique", $"{episodes[0].Name} → {window.Player?.Item?.Name ?? window.NoticeText ?? "lecteur fermé"}");
                await window.StopPlaybackAsync();
            }

            // —— Sign-out ——
            if (mock is not null)
            {
                await window.SignOutAsync();
                Check(window.Login is not null && window.Session is null, "déconnexion");
                bool told; lock (mock.Calls) told = mock.Calls.Contains("POST Sessions/Logout");
                Check(told, "session fermée sur Jellyfin aussi");
            }

            // —— Keychain ——
            // On an item of its own, from a worker thread given 20 s: a locked keychain would ask a question nobody answers
            // here. Refused or unavailable, Mira keeps the token in its file: only a call that misbehaves fails.
            if (OperatingSystem.IsMacOS())
            {
                var keychain = new Services.Keychain(); var account = Path.Combine(window.Profile.DirectoryPath, "self-check");
                var token = "contrôle " + Guid.NewGuid().ToString("N");
                var probe = Task.Run(() =>
                {
                    if (!keychain.Store(account, token)) return $"écriture refusée (OSStatus {keychain.Status})";
                    if (keychain.Read(account) != token) return $"relu autre chose (OSStatus {keychain.Status})";
                    return keychain.Delete(account) && keychain.Read(account) is null ? null : $"pas effacé (OSStatus {keychain.Status})";
                });
                if (await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(20))) != probe) Check(true, "trousseau", "pas de réponse en 20 s (trousseau verrouillé ?) : non mesurable");
                else if (probe.Result is not null && keychain.Status is Services.Keychain.InteractionNotAllowed or Services.Keychain.NoSuchKeychain or Services.Keychain.NotAvailable
                    or Services.Keychain.AuthFailed or Services.Keychain.UserCanceled) Check(true, "trousseau", probe.Result + " : indisponible ici, non mesurable");
                else Check(probe.Result is null, "trousseau : écrire, relire, effacer", probe.Result ?? "");
            }

            // —— Built-in Tailscale: its node in Mira.app ——
            // Where tools/mac/package.sh puts it, free of any quarantine; then a copy marked as macOS marks a download, which
            // Mira must take for one Gatekeeper holds, until the mark says the person let it open.
            if (OperatingSystem.IsMacOS())
            {
                var (helper, missing) = Services.TailnetExecutable.Locate();
                Check(helper is not null && Services.TailnetExecutable.Blocked(helper) is null, "Tailscale intégré : mira-tailscale dans Mira.app", helper ?? missing ?? "");
                if (helper is not null)
                {
                    var copy = Path.Combine(Path.GetTempPath(), $"mira-tailscale-{Guid.NewGuid():N}");
                    File.Copy(helper, copy);
                    try
                    {
                        string? Marked(string flags)
                        {
                            using var xattr = Process.Start("/usr/bin/xattr", ["-w", "com.apple.quarantine", $"{flags};{DateTimeOffset.UtcNow.ToUnixTimeSeconds():x};Safari;", copy])!;
                            xattr.WaitForExit();
                            return Services.TailnetExecutable.Blocked(copy);
                        }
                        string? held = Marked("0083"), allowed = Marked("00c3");
                        Check(held?.Contains(Services.TailnetExecutable.Command) == true && allowed is null, "Tailscale intégré : blocage de Gatekeeper reconnu", held ?? "marque de quarantaine pas lue");
                    }
                    finally { File.Delete(copy); }
                }
            }
            exit = results.All(x => x.Ok) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Check(false, "contrôle interrompu", ex.GetType().Name + " : " + ex.Message);
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "results.txt"), results.Select(x => $"{(x.Ok ? "PASS" : "FAIL")}  {x.Name}{(x.Detail.Length > 0 ? " — " + x.Detail : "")}")
            .Prepend($"Mira {Services.Updates.Current} — {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"));
        Console.WriteLine($"{results.Count(x => x.Ok)}/{results.Count} contrôles réussis.");
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exit);
    }

    private static async Task<bool> Until(Func<bool> condition, double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }
    private static void Key(MainWindow window, Key key) => window.Player?.HandleKey(new KeyEventArgs { Key = key, RoutedEvent = InputElement.KeyDownEvent });

    /// <summary>The window as Avalonia draws it (without mpv's picture, which only reaches the screen).</summary>
    internal static void CaptureWindow(Window window, string output, string name)
    {
        var scaling = window.RenderScaling;
        var size = new PixelSize((int)(window.Bounds.Width * scaling), (int)(window.Bounds.Height * scaling));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(output, name + ".png"));
    }
    /// <summary>
    /// The controls shown that VoiceOver reaches but could only call « bouton » or « champ de texte »: their automation
    /// peer has no name, or only the name of their content's type (a button holding an icon and a label, without
    /// AutomationProperties.Name, is read « Avalonia.Controls.StackPanel »; a tooltip is not read). The parts of a
    /// control's template are its own.
    /// </summary>
    public static List<string> Unnamed(Visual root)
    {
        var unnamed = new List<string>();
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (control is not (Button or TextBox or ComboBox or Slider) || control.TemplatedParent is not null || !control.IsEffectivelyVisible) continue;
            var name = ControlAutomationPeer.CreatePeerForElement(control).GetName();
            if (!string.IsNullOrWhiteSpace(name) && !(control is ContentControl { Content: { } content and not string } && name == content.ToString())) continue;
            var text = control.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            var icon = control.GetVisualDescendants().OfType<Icon>().Select(x => x.Kind).FirstOrDefault();
            unnamed.Add($"{control.GetType().Name}{(text is not null ? $" « {text} »" : icon is not null ? $" ({icon})" : "")}");
        }
        return unnamed;
    }
    /// <summary>
    /// Tab from the window's start, as macOS's « Navigation au clavier » does, until it comes back to a control already
    /// reached (200 at most): how many stops, and those that would be hidden (not shown, disabled, without a size).
    /// </summary>
    public static (int Stops, List<string> Faults) TabWalk(Window root)
    {
        var seen = new HashSet<IInputElement>(); var faults = new List<string>();
        IInputElement current = root;
        for (var i = 0; i < 200; i++)
        {
            if (KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next) is not { } next || !seen.Add(next)) break;
            current = next;
            if (next is not Control control) continue;
            if (!control.IsEffectivelyVisible || !control.IsEffectivelyEnabled || control.Bounds.Width < 1 || control.Bounds.Height < 1)
            {
                var label = ControlAutomationPeer.CreatePeerForElement(control).GetName();
                faults.Add($"{control.GetType().Name}{(string.IsNullOrWhiteSpace(label) ? "" : $" « {label} »")} caché");
            }
        }
        return (seen.Count, faults);
    }
    /// <summary>
    /// The screen as macOS (screencapture) or X11 (import) shows it, mpv's picture included: the share of colourful
    /// pixels in its centre, where the test pattern plays. Null when the screen cannot be captured here.
    /// </summary>
    public static double? ScreenColour(string file, bool centre = true)
    {
        var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("screencapture", ["-x", file]) : new ProcessStartInfo("import", ["-window", "root", "PNG32:" + file]);
        try { using var process = Process.Start(start); process?.WaitForExit(15000); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        if (!File.Exists(file)) return null;
        // Decoded into 32-bit pixels whatever the capture's format (an all-black capture is saved as grey levels).
        using var stream = File.OpenRead(file);
        using var bitmap = WriteableBitmap.Decode(stream);
        using var buffer = bitmap.Lock();
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        // The centre of the screen, where the window plays; or, for the probe's window in the corner, its middle.
        var area = centre ? new PixelRect(width * 35 / 100, height * 35 / 100, width * 30 / 100, height * 30 / 100) : new PixelRect(100, 100, Math.Min(600, width - 100), Math.Min(300, height - 100));
        var row = new byte[buffer.RowBytes];
        var colourful = 0; var total = 0;
        // Grey levels only (an all-black capture): nothing colourful by definition.
        var size = buffer.Format.BitsPerPixel / 8;
        if (size < 3) return 0;
        for (var y = area.Y; y < area.Bottom; y++)
        {
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, row.Length);
            for (var x = area.X; x < area.Right; x++)
            {
                int a = row[x * size], b = row[x * size + 1], c = row[x * size + 2];
                if (Math.Max(a, Math.Max(b, c)) - Math.Min(a, Math.Min(b, c)) > 70) colourful++;
                total++;
            }
        }
        return total == 0 ? 0 : colourful / (double)total;
    }
}
