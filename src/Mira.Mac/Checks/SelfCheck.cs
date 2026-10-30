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
            // The sign-in page, seen as it opens (the mock signs in without it).
            if (mock is not null) { window.ShowLogin(); await Until(() => window.Login is not null, 5); await Task.Delay(3000); Capture(window, output, "01-login"); }
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

            // The page scrolls whatever the pointer is over: a row given a swipe down with a little sideways, and a pointer
            // left captured by a press whose release never came (a menu, the player gone from under it).
            var mouse = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            void Wheel(Control source, Point at, Vector delta) => source.RaiseEvent(new PointerWheelEventArgs(source, mouse, (Visual?)TopLevel.GetTopLevel(source) ?? window, at, 0, PointerPointProperties.None, KeyModifiers.None, delta));
            var swiped = new CardRow("Contrôle", Enumerable.Range(0, 24).Select(_ => (Control)new Button { Width = 176, Height = 40 }));
            var swipedPage = new ScrollViewer { Content = new StackPanel { Children = { swiped, new Border { Height = 1200 } } } };
            var swipeWindow = new Window { Width = 1100, Height = 300, Content = swipedPage };
            swipeWindow.Show(); await Task.Delay(400);
            Wheel(swiped.GetVisualDescendants().OfType<Button>().Last(), default, new Vector(-.2, -1));
            Check(swipedPage.Offset.Y > 0 && swiped.Offset < 1, "rangée : un glissement vers le bas fait défiler la page", $"page {swipedPage.Offset.Y:0} px, rangée {swiped.Offset:0} px");
            swipeWindow.Close();
            if (home.FindDescendantOfType<ScrollViewer>() is { } homeScroll)
            {
                homeScroll.Offset = default; await Task.Delay(200);
                mouse.Capture((Control)window.Content!);
                Wheel((Control)window.Content!, new Point(window.Bounds.Width / 2, window.Bounds.Height * .6), new Vector(0, -1));
                Check(homeScroll.Offset.Y > 0 && mouse.Captured is null, "accueil : la page défile après une capture restée sans bouton", $"page {homeScroll.Offset.Y:0} px, capture {mouse.Captured?.GetType().Name ?? "aucune"}");
                homeScroll.Offset = default;
            }

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
            // Refreshed as after the player or a title marked watched: the episodes stay, and the page with them scrolls.
            if (window.Current is DetailPage seriesPage)
            {
                await seriesPage.RefreshAsync(); await seriesPage.RefreshAsync();
                Check(seriesPage.EpisodesShown, "fiche d’une série : épisodes gardés après une actualisation");
            }
            SettingsPage.OpenAt("playback");
            window.Go("settings");
            await Task.Delay(800); Capture(window, output, "07-settings");
            // The categories, as on Windows: those of the server's account hold its sections.
            (window.Current as SettingsPage)?.Show("server");
            // « Titres à corriger », as on Windows: the mock's titles have no ids from a metadata source.
            if (mock is not null)
                Check(await Until(() => window.Current is SettingsPage { Fix.Count: > 0, Fix.Section.IsVisible: true }, 10), "réglages : titres à corriger", $"{(window.Current as SettingsPage)?.Fix.Count} titres");
            if (mock is not null)
                Check(await Until(() => window.Current is SettingsPage { Viewing.Count: > 0 }, 10), "réglages : en ce moment sur Jellyfin", $"{(window.Current as SettingsPage)?.Viewing.Count} lectures");
            // « Ajouter un ami », as on Windows: the account sees only the library chosen and downloads nothing.
            if (mock is not null && await Until(() => window.Current is SettingsPage { Friend.Libraries: 2 }, 10) && window.Current is SettingsPage withFriend)
            {
                withFriend.Friend.CreateForCheck("Marc");
                var made = await Until(() => withFriend.Friend.Made == "Marc", 10);
                var policy = mock.FriendPolicy ?? "";
                Check(made && policy.Contains("\"EnableAllFolders\":false") && policy.Contains("view-movies") && !policy.Contains("view-series") && policy.Contains("\"EnableContentDownloading\":false"),
                    "réglages : ajouter un ami", policy);
                withFriend.Friend.Section.BringIntoView(); await Task.Delay(500); Capture(window, output, "07c-settings-friend");
            }
            else if (mock is not null) Check(false, "réglages : ajouter un ami", "section absente");
            // « Sortie audio », as on Windows: the devices mpv lists, and « Tester » made and played here.
            if (window.Current is SettingsPage withAudio)
            {
                withAudio.Show("audio");
                Check(await Until(() => withAudio.AudioDevices >= 0, 15), "réglages : sortie audio", $"{withAudio.AudioDevices} sorties");
                withAudio.TestSoundForCheck();
                var heard = await Until(() => withAudio.SoundTested is not null && !File.Exists(Path.Combine(Path.GetTempPath(), "Mira-sound-test.wav")), 15);
                Check(heard && withAudio.SoundTested!.Contains(AudioOutput.TestSound(window.Settings.AudioChannels).Speakers[^1]), "réglages : tester la sortie audio", withAudio.SoundTested ?? "rien");
                withAudio.AudioOutputRow.BringIntoView(); await Task.Delay(500); Capture(window, output, "07d-settings-audio");
            }
            if (window.Current is SettingsPage { Fix.Section.IsVisible: true } withFix) { withFix.Show("server"); withFix.Viewing.Section.BringIntoView(); await Task.Delay(500); Capture(window, output, "07b-settings-fix"); }

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
                var info = player.InfoText() ?? "";
                Check(info.Split('\n').Length == 6 && info.Contains("OpenGL ·") == player.Surface is Playback.VideoView && PictureQuality.Choices.Any(x => info.Contains(PictureQuality.Label(x.Key))), $"informations de lecture ({renderer})", info.Replace('\n', ' '));
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
                if (software)
                {
                    // « ⋮ » as on Windows: its sections and their choices as chips, on the screen (a menu is a window of its own).
                    player.MoreButton.Flyout!.ShowAt(player.MoreButton);
                    await Task.Delay(600);
                    var menu = (player.MoreButton.Flyout as Flyout)?.Content as Control;
                    var chips = menu?.GetVisualDescendants().OfType<Button>().Count(x => x.Classes.Contains("menuchip")) ?? 0;
                    ScreenColour(Path.Combine(output, "08b-player-menu.png"));
                    Check(chips >= 6 && (menu is null || Unnamed(menu).Count == 0), "lecteur : menu « ⋮ » en pastilles, comme sous Windows", $"{chips} pastilles");
                    player.MoreButton.Flyout.Hide();
                    // « Chercher des sous-titres… », as on Windows: offered with an extension, its results in the menu of the tracks.
                    if (await Until(() => player.CanSearchSubtitles, 5))
                    {
                        player.TracksButton.Flyout!.ShowAt(player.TracksButton);
                        await Task.Delay(300);
                        player.SearchSubtitles();
                        var found = await Until(() => player.SubtitlesFound > 0, 5);
                        await Task.Delay(500);
                        var tracksMenu = (player.TracksButton.Flyout as Flyout)?.Content as Control;
                        ScreenColour(Path.Combine(output, "08e-subtitle-search.png"));
                        Check(found && (tracksMenu is null || Unnamed(tracksMenu).Count == 0), "lecteur : chercher des sous-titres", $"{player.SubtitlesFound} trouvés");
                        player.TracksButton.Flyout.Hide();
                    }
                    else Check(false, "lecteur : « Chercher des sous-titres… » proposé");
                    // The mini-player, as on Windows: Échap shrinks it to a corner, the rail works around it, I brings it back.
                    Key(window, Avalonia.Input.Key.Escape);
                    window.Go("Movie");
                    await Task.Delay(1200);
                    ScreenColour(Path.Combine(output, "08d-mini-player.png"));
                    Check(player.IsMini && window.Player == player && player.Bounds.Width < 400 && window.Current is BrowsePage, "mini-lecteur : la bibliothèque autour", $"{player.Bounds.Width:0} × {player.Bounds.Height:0}, page {window.Current?.GetType().Name}");
                    Key(window, Avalonia.Input.Key.I);
                    Check(await Until(() => !player.IsMini && player.Bounds.Width > 800, 3), "mini-lecteur : agrandi par I", $"{player.Bounds.Width:0}");
                }
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
                // « Épisodes » and « Épisode précédent », as on Windows: the season in a menu over the bar, P back to the one before.
                if (window.Player is { } player && await Until(() => player.PreviousButton.IsEnabled && player.EpisodesButton.IsEnabled, 15))
                {
                    player.EpisodesButton.Flyout!.ShowAt(player.EpisodesButton);
                    await Task.Delay(800);
                    var menu = (player.EpisodesButton.Flyout as Flyout)?.Content as Control;
                    var rows = menu?.GetVisualDescendants().OfType<Button>().Count(x => x.Tag is string) ?? 0;
                    ScreenColour(Path.Combine(output, "08c-player-episodes.png"));
                    Check(rows >= 2 && (menu is null || Unnamed(menu).Count == 0), "lecteur : menu « Épisodes »", $"{rows} épisodes");
                    player.EpisodesButton.Flyout.Hide();
                    Key(window, Avalonia.Input.Key.P);
                    Check(await Until(() => window.Player?.Item?.Id == episodes[0].Id, 30), "lecteur : épisode précédent (P)", window.Player?.Item?.Name ?? window.NoticeText ?? "lecteur fermé");
                }
                else Check(false, "lecteur : épisode précédent et « Épisodes » disponibles");
                await window.StopPlaybackAsync();
            }

            // —— « Garder sur ce Mac »: the file copied, listed, played from this Mac, then removed ——
            if (mock is not null)
            {
                await window.KeepOfflineAsync([movie]);
                var copied = await Until(() => window.KeptCopy(movie.Id) is not null, 30);
                var copy = window.KeptCopy(movie.Id);
                Check(copied && window.Offers("offline"), "garder sur ce Mac : le film copié", copy?.VideoPath ?? window.NoticeText ?? "");
                if (copy is not null)
                {
                    window.Go("offline");
                    Check(await Until(() => window.Current is OfflinePage { Count: 1 }, 5), "garder sur ce Mac : la page « Sur ce Mac »", $"{(window.Current as OfflinePage)?.Count} titres");
                    await Task.Delay(800); Capture(window, output, "09-offline");
                    // In software, as the « logiciel » playback above: on the runner of mac.yml, without GPU interop, the OpenGL
                    // view tried and given up here, after those of the episodes, crashed the process (segmentation fault,
                    // cause not found: no crash report there); the fall back from OpenGL is checked by the playback above.
                    Environment.SetEnvironmentVariable("MIRA_SOFTWARE_VIDEO", "1");
                    await window.PlayAsync(movie);
                    Environment.SetEnvironmentVariable("MIRA_SOFTWARE_VIDEO", null);
                    Check(await Until(() => window.Player is { IsFileLoaded: true }, 20) && window.Player?.Location == copy.VideoPath, "garder sur ce Mac : lu depuis ce Mac", window.Player?.Location ?? "lecteur fermé");
                    await window.StopPlaybackAsync();
                    await window.ForgetOfflineAsync([copy]);
                    Check(await Until(() => !File.Exists(copy.VideoPath) && !window.Offers("offline"), 10), "garder sur ce Mac : retiré");
                }
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
