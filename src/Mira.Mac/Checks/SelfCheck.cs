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
using Mira.Mac.Services;
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
        // « apart »: everything but quitting; « only »: signed in, then straight to quitting (mac.yml runs both).
        var quit = Environment.GetEnvironmentVariable("MIRA_CHECK_QUIT");
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
            // The player's own safety with this libmpv: https:// checked before the token goes, yt-dlp never run, and
            // the subtitles a film starts with (--track-media: the films of mac.yml).
            var engineSettings = new PlayerSettings { MpvPath = window.Settings.MpvPath, AudioLanguage = "jpn,ja,eng,en,fre,fra,fr", SubtitleLanguage = "eng,en,fre,fra,fr" };
            foreach (var line in await Mira.Checks.PlayerSafety.HttpsAsync(network => Playback.MpvPlayer.Probe(engineSettings, network)))
                Check(line.Passed, (line.Info ? "(info) " : "") + "lecteur : " + line.Message);
            if (Arg("--track-media") is { Length: > 0 } tracks)
                foreach (var line in await Mira.Checks.PlayerSafety.TracksAsync(languages => Playback.MpvPlayer.Probe(new PlayerSettings { MpvPath = window.Settings.MpvPath, AudioLanguage = languages.Audio, SubtitleLanguage = languages.Subtitles }), tracks))
                    Check(line.Passed, "lecteur : " + line.Message);
            // The account's photo from an iPhone's HEIC (--photos: tools/mac/check).
            if (Arg("--photos") is { Length: > 0 } photos) PhotoCheck.Run(Path.GetFullPath(photos), Check);
            window.Settings.AutoNext = true; window.Settings.RememberPosition = true; window.Settings.Volume = 150;
            // The trackpad read as a Mac's out of the box (natural scrolling, two fingers between pages), whatever this machine says.
            MacTrackpad.Override(natural: true, swipes: true);
            // Two-finger gestures as macOS sends them: scroll events 16 ms apart, a pause between gestures.
            var gestureClock = 1_000_000L;
            var trackpad = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            void Swipe(Avalonia.Interactivity.Interactive target, Point at, double x, double y, int events)
            {
                gestureClock += 1000;
                for (var i = 0; i < events; i++)
                    target.RaiseEvent(new PointerWheelEventArgs(target, trackpad, window, at, (ulong)(gestureClock += 16), PointerPointProperties.None, KeyModifiers.None, new Vector(x, y)));
            }

            // —— Sign-in ——
            // The sign-in page, seen as it opens (the mock signs in without it).
            if (mock is not null)
            {
                window.ShowLogin(); await Until(() => window.Login is not null, 5); await Task.Delay(3000); Capture(window, output, "01-login");
                // As on Windows: the eye shows the password, a line says Caps Lock is on while typing it.
                var login = window.Login!;
                MacKeyboard.Override(true); login.FocusPassword("motdepasse"); await Task.Delay(200);
                login.PasswordEye.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(400); Capture(window, output, "01b-login-password");
                var eyeName = Avalonia.Automation.AutomationProperties.GetName(login.PasswordEye);
                Check(login.PasswordShown && login.CapsShown && eyeName == Lang.L("Masquer le mot de passe", "Hide password"), "connexion : l’œil montre le mot de passe, Verr. Maj est dit", $"montré {login.PasswordShown}, Verr. Maj {login.CapsShown}, « {eyeName} »");
                MacKeyboard.Override(null);
            }
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
            if (mock is not null && quit == "only")
            {
                await PrepareQuitAsync(home.Continue.First(x => x.Id == "movie-1"));
                exit = results.All(x => x.Ok) ? 0 : 1;
                Finish();
                return;
            }
            if (mock is not null) Check(home.Continue.Any(x => x.Id == "movie-1"), "accueil : continuer à regarder", string.Join(", ", home.Continue.Select(x => x.Name)));
            await Task.Delay(1500); Capture(window, output, "02-home");
            home.ScrollToEnd(); await Task.Delay(800); Capture(window, output, "02b-home-grid"); home.ScrollToHome();
            // « Ajoutés récemment » goes on with « Afficher plus de titres »: Home refreshed (come back to, the player
            // closed, a change on the server) keeps every title shown, as Windows' grid keeps its pages. It was asked for
            // its first 30 only: a title come or a mark changed among them drew it again with those 30, under the reader.
            if (mock is not null)
            {
                mock.Fillers = 100; await window.RefreshPagesAsync();
                int Shown() => home.GetVisualDescendants().OfType<Mira.Mac.Views.CardGrid>().FirstOrDefault()?.Children.Count ?? 0;
                Button? More() => home.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.IsEffectivelyVisible
                    && Avalonia.Automation.AutomationProperties.GetName(x) == Lang.L("Afficher plus de titres", "Show more titles"));
                await Until(() => Shown() >= 30 && More() is not null, 10);
                for (var i = 0; i < 2 && More() is { } more; i++)
                {
                    var before = Shown();
                    more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    await Until(() => Shown() > before && More() is { IsEnabled: true }, 10);
                }
                var expanded = Shown();
                mock.LatestAdded = true; await window.RefreshPagesAsync();
                await Until(() => home.Latest.FirstOrDefault()?.Id == "filler-0", 10); await Task.Delay(300);
                var kept = Shown();
                Check(expanded >= 90 && kept >= expanded && home.Latest.FirstOrDefault()?.Id == "filler-0", "accueil : « Ajoutés récemment » garde tous ses titres à l’actualisation",
                    $"{expanded} montrés, {kept} après un titre arrivé en tête");
                mock.Fillers = 0; mock.LatestAdded = false; await window.RefreshPagesAsync();
                await Until(() => home.Latest.All(x => !x.Id.StartsWith("filler")), 10);
            }
            // « Nouveautés », as on Windows: the last versions, the newest marked;
            // Escape closes it and gives the keys back to the page.
            var missed = WhatsNew.Recent(Updates.Current);
            window.ShowWhatsNew(missed);
            var news = await Until(() => window.CurrentSheet is { IsVisible: true }, 5);
            await Task.Delay(500); Capture(window, output, "02c-whats-new");
            window.RaiseEvent(new KeyEventArgs { Key = Avalonia.Input.Key.Escape, RoutedEvent = InputElement.KeyDownEvent, Source = window });
            Check(news && await Until(() => window.CurrentSheet is null, 3) && window.Current is HomePage, "nouveautés : montrées après une mise à jour, fermées par Échap",
                missed.Count > 0 ? string.Join(", ", missed.Select(x => x.Version)) : "aucune version");
            // « Retirer de Continuer à regarder », as on Windows: out of the row, its resume point taken back on Jellyfin too.
            if (mock is not null && home.Continue.FirstOrDefault(x => x.Id == "movie-1") is { } resumed)
            {
                var group = ContinueWatching.Group(resumed);
                await window.HideFromResumeAsync(resumed);
                var gone = await Until(() => window.Current is HomePage { Continue: var shownRow } && shownRow.All(x => x.Id != "movie-1"), 10);
                bool told; lock (mock.Calls) told = mock.Calls.Contains("POST UserItems/movie-1/UserData");
                Check(gone && told, "accueil : retirer de continuer à regarder", $"retiré {gone}, Jellyfin prévenu {told}");
                window.Session.Store.HideFromResume(group, null); await window.RefreshPagesAsync();
                await Until(() => window.Current is HomePage { Continue: var back } && back.Any(x => x.Id == "movie-1"), 10);
                // « Annuler » pressed at once, while a Jellyfin far away still gives the row again: the resume point is
                // back on Jellyfin, never put to 0 by the removal after the undo.
                var ticks = resumed.UserData.PlaybackPositionTicks;
                mock.SlowResume = TimeSpan.FromSeconds(1.5); lock (mock.Positions) mock.Positions.Clear();
                var removal = window.HideFromResumeAsync(resumed);
                var undo = await Until(() => window.NoticeAction == Lang.L("Annuler", "Undo"), 5);
                if (undo) window.PressNotice();
                await removal;
                await Until(() => { lock (mock.Positions) return mock.Positions.Count >= 2; }, 10);
                await Task.Delay(2500);
                mock.SlowResume = TimeSpan.Zero;
                List<(string Id, long Ticks)> positions; lock (mock.Positions) positions = [.. mock.Positions];
                var again = await Until(() => window.Current is HomePage { Continue: var row } && row.Any(x => x.Id == "movie-1"), 10);
                Check(undo && positions.Count > 0 && positions[^1] == ("movie-1", ticks) && again, "accueil : « Annuler » tout de suite rend le point de reprise sur Jellyfin",
                    $"bouton {undo}, Jellyfin : {string.Join(" puis ", positions.Select(x => x.Ticks))} (avant : {ticks}), de retour dans la rangée {again}");
            }
            // « Animer l’accueil » off: the banner stays on its title; on again, it turns.
            if (mock is not null)
            {
                window.Settings.HeroAutoPlay = false; window.Go("search"); window.Go("home");
                var bannerStill = await Until(() => window.Current is HomePage { Turning: false }, 3);
                window.Settings.HeroAutoPlay = true; window.Go("search"); window.Go("home");
                Check(bannerStill && await Until(() => window.Current is HomePage { Turning: true } || MacMotion.Reduced, 3), "accueil : animer l’accueil coupé, puis remis");
                // Its clock as on Windows: counted while it turns, held where it is once the page is scrolled past it, going on when it comes back.
                if (window.Current is HomePage { Turning: true } turning)
                {
                    await Task.Delay(600);
                    var counted = turning.HeroElapsed;
                    turning.ScrollForCheck(10_000);
                    var held = await Until(() => !turning.Turning, 2);
                    var heldAt = turning.HeroElapsed; await Task.Delay(500);
                    var kept = turning.HeroElapsed;
                    turning.ScrollForCheck(0);
                    var turnsAgain = await Until(() => turning.Turning, 2);
                    Check(counted > 0 && held && kept == heldAt && turnsAgain && turning.HeroBars is var (bars, titles) && bars == titles && titles > 1,
                        "accueil : bandeau tenu une fois la page défilée au-delà, son temps gardé, une barre par titre",
                        $"compté {counted:0.00} s, tenu {held} ({heldAt:0.00} → {kept:0.00} s), reparti {turnsAgain}, {turning.HeroBars.Bars} barres pour {turning.HeroBars.Titles} titres");
                }
            }
            // Jellyfin no longer answering, as on Windows: said once, « Hors connexion » with « Réessayer » until it answers,
            // the list it last gave shown on a page opened meanwhile, then « Jellyfin répond de nouveau. ».
            if (mock is not null)
            {
                var listedPage = new BrowsePage(window, BrowseKind.Movies);
                await window.NavigateAsync(listedPage);
                var listedCount = await Until(() => listedPage.Items.Count > 0, 10) ? listedPage.Items.Count : 0;
                window.Go("home"); await Until(() => window.Current is HomePage, 3);
                window.HideNotice();
                mock.Unreachable = true;
                await window.RefreshPagesAsync(fromServer: true);
                var saidFirst = window.NoticeText ?? ""; var badgeFirst = window.OfflineBadgeText;
                window.HideNotice();
                var keptPage = new BrowsePage(window, BrowseKind.Movies);
                await window.NavigateAsync(keptPage);
                await Until(() => keptPage.Items.Count > 0, 5);
                var saidAgain = window.NoticeText; var keptCount = keptPage.Items.Count; var badgeLater = window.OfflineBadgeText;
                await Task.Delay(400); Capture(window, output, "03d-offline");
                await window.RetryForCheck();
                var stillSaid = window.NoticeText ?? "";
                window.HideNotice();
                mock.Unreachable = false;
                await window.RetryForCheck();
                var backSaid = window.NoticeText ?? ""; var badgeGone = window.OfflineBadgeText is null;
                var offlineWords = Lang.L("Hors connexion · cache local", "Offline · local cache");
                Check(listedCount > 0 && saidFirst.EndsWith(Lang.L(" Les informations déjà chargées restent disponibles.", " What’s already loaded stays available.")) && badgeFirst == offlineWords
                    && saidAgain is null && keptCount == listedCount && badgeLater == offlineWords && stillSaid.StartsWith(Lang.L("Jellyfin ne répond toujours pas", "Jellyfin still isn’t answering"))
                    && backSaid == Lang.L("Jellyfin répond de nouveau.", "Jellyfin is answering again.") && badgeGone,
                    "Jellyfin injoignable : dit une fois, « Hors connexion » jusqu’à sa réponse, la liste gardée montrée",
                    $"{listedCount} films · « {saidFirst} » · pastille « {badgeFirst} » · ensuite « {saidAgain} », {keptCount} films, « {badgeLater} » · Réessayer « {stillSaid} » · de retour « {backSaid} », pastille partie {badgeGone}");
                window.HideNotice(); window.Go("home"); await Until(() => window.Current is HomePage, 3);
            }
            // A row's arrows only when it goes past the window, and a press glides about three posters along, not a page.
            Check(home.GetVisualDescendants().OfType<CardRow>().All(x => x.GetVisualDescendants().OfType<ScrollViewer>().First() is var s && x.ArrowsShown == s.Extent.Width > s.Viewport.Width + 1), "accueil : flèches des rangées seulement si elles débordent");
            var row = new CardRow("Contrôle", Enumerable.Range(0, 24).Select(_ => (Control)new Border { Width = 176, Height = 40 }));
            var rowWindow = new Window { Width = 1100, Height = 200, Content = row };
            rowWindow.Show();
            await Task.Delay(400);
            var shown = row.ArrowsShown;
            // Where the row is at each frame, as drawn: a glide shows steps on the way, a jump none. Read by frames, not by
            // the clock: a wait of 50 ms once lasted the whole glide on a slow runner.
            var rowFrames = new List<double>(); var watching = true;
            void Watch(TimeSpan _) { rowFrames.Add(row.Offset); if (watching) rowWindow.RequestAnimationFrame(Watch); }
            rowWindow.RequestAnimationFrame(Watch);
            row.Glide(1);
            await Until(() => Math.Abs(row.Offset - CardRow.Step) < 1, 3);
            await Task.Delay(100); watching = false;
            var midway = rowFrames.Where(x => x > .5 && x < CardRow.Step - 1).Distinct().ToList();
            Check(shown && midway.Count >= 2 && Math.Abs(row.Offset - CardRow.Step) < 1, "rangée : défilement fluide d’environ trois affiches",
                $"flèches {shown}, {midway.Count} images en chemin ({string.Join(", ", midway.Take(8).Select(x => x.ToString("0")))} px), {row.Offset:0} px à la fin");
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
                Check(await Until(() => homeScroll.Offset.Y > 0, 2) && mouse.Captured is null, "accueil : la page défile après une capture restée sans bouton", $"page {homeScroll.Offset.Y:0} px, capture {mouse.Captured?.GetType().Name ?? "aucune"}");
                homeScroll.Offset = default; await Task.Delay(100);
                // A wheel with notches eases the page (Glide, the Windows wheel's curve), by Avalonia's step: three notches
                // in a row, 150 px, reached still, never at once. With motion, whatever the runner's setting.
                MacMotion.Override(false);
                var point = new Point(window.Bounds.Width / 2, window.Bounds.Height * .6);
                var maximum = homeScroll.Extent.Height - homeScroll.Viewport.Height;
                var steps = new List<double>();
                for (var notch = 0; notch < 3; notch++)
                {
                    if (window.InputHitTest(point) is Control under) Wheel(under, point, new Vector(0, -1));
                    steps.Add(homeScroll.Offset.Y); await Task.Delay(40);
                }
                Check(await Until(() => !Glide.Moving(homeScroll), 2) && Math.Abs(homeScroll.Offset.Y - Math.Min(150, maximum)) < .5 && steps[0] < 1 && steps[2] < Math.Min(150, maximum),
                    "accueil : la molette à crans glisse la page", $"{string.Join(", ", steps.Select(x => x.ToString("0")))} px aux crans, {homeScroll.Offset.Y:0} px à la fin (sur {maximum:0})");
                MacMotion.Override(null);
                homeScroll.Offset = default;
            }

            // —— Notifications of the Mac, as Windows': « Nouvel épisode » of a series followed, once; the first look only marks ——
            if (mock is not null)
            {
                window.Settings.EpisodeNotices = true; window.Settings.EpisodesSeenFor = "";
                var before = MacNotifications.Shown.Count;
                await window.LookForNewEpisodesAsync();
                Check(MacNotifications.Shown.Count == before && window.Settings.EpisodesSeenFor.Length > 0, "notifications : le premier regard ne fait que poser la marque", window.Settings.EpisodesSeenFor);
                mock.NewEpisodeAt = DateTimeOffset.UtcNow;
                await window.LookForNewEpisodesAsync();
                var told = MacNotifications.Shown.Skip(before).ToList();
                Check(told is [{ Title: "Petite série", Group: "episodes" } one] && one.Text == Lang.L("Nouvel épisode : S1 É4 · Le retour", "New episode: S1 E4 · Le retour"),
                    "notifications : un nouvel épisode d’une série suivie", string.Join(" | ", told.Select(x => $"{x.Title} : {x.Text} ({x.Group})")));
                await window.LookForNewEpisodesAsync();
                Check(MacNotifications.Shown.Count == before + 1, "notifications : jamais deux fois le même épisode", $"{MacNotifications.Shown.Count - before}");
                Check(!MacNotifications.Allowed, "notifications : rien de macOS pendant le contrôle");
                mock.NewEpisodeAt = null;
            }

            // —— A friend's party, on another Jellyfin ——
            // Its link opened here: the first name asked, then this Mac its guest (no account there), in the host's group;
            // left, this Mac's own account back. A link taken back since says the party is over.
            if (mock is not null)
            {
                var link = $"mira://party/{MockJellyfin.PartyCode}?server={Uri.EscapeDataString(MockJellyfin.FriendServer)}";
                window.OpenLink(link);
                Check(await Until(() => window.Party is { Parent: not null } party && party.Heading == Together.PartyAsk("Sam"), 10), "party d’un ami : prénom demandé, hôte nommé", window.Party?.Heading ?? "pas de carte");
                await Task.Delay(800); Capture(window, output, "02c-party-name");
                window.Party!.FirstName = "Léa"; window.Party.ClickPrimary();
                Check(await Until(() => window.InParty && window.Session is { } guest && Together.IsGuest(guest.Connection) && guest.Connection.UserName == "Léa", 10), "party d’un ami : invité sans compte", window.Party?.Error ?? window.Session?.Connection.Server ?? "");
                Check(window.Settings.PartyName == "Léa", "party d’un ami : prénom gardé pour la suivante", window.Settings.PartyName);
                bool Called(string call) { lock (mock.FriendCalls) return mock.FriendCalls.Contains(call); }
                Check(await Until(() => Called("POST Mira/guest/SyncPlay/Join"), 25), "party d’un ami : groupe de l’hôte rejoint", string.Join(", ", mock.FriendCalls.TakeLast(6)));
                // Jellyfin's answer, which its WebSocket would bring.
                using (var joined = System.Text.Json.JsonDocument.Parse("""{"Type":"GroupJoined","Data":{"GroupId":"party-group","GroupName":"Sam's group","Participants":["Sam","Léa"],"State":"Idle"}}"""))
                    window.Group.Message(window.Session!, "SyncPlayGroupUpdate", joined.RootElement.Clone());
                Check(await Until(() => window.Party?.Heading == Together.PartyIn("Sam") && window.Party.Members.Contains("Léa"), 10), "party d’un ami : dans le groupe, ses membres", $"{window.Party?.Heading} — {window.Party?.Members}");
                Check(window.Current is null, "party d’un ami : rien de la bibliothèque de ce Mac", window.Current?.GetType().Name ?? "");
                await Task.Delay(600); Capture(window, output, "02d-party-in");
                await window.LeavePartyAsync();
                Check(await Until(() => !window.InParty && window.Session?.Connection.Server == MockJellyfin.Server && window.Current is HomePage, 15), "party d’un ami : quitter rend le compte de ce Mac", window.Session?.Connection.Server ?? "aucun compte");
                Check(Called("POST Mira/guest/SyncPlay/Leave") && Called("POST Mira/guest/Sessions/Logout"), "party d’un ami : groupe quitté, session fermée chez l’ami", string.Join(", ", mock.FriendCalls.TakeLast(4)));
                mock.PartyGone = true;
                window.OpenLink(link);
                Check(await Until(() => window.Party is { Parent: not null } party && party.Heading == Together.PartyOver, 10), "party d’un ami : lien retiré, la party est finie", window.Party?.Heading ?? "pas de carte");
                window.Party!.ClickSecondary();
                Check(await Until(() => window.Party is not { Parent: not null } && window.Current is HomePage, 5), "party d’un ami : « Fermer » rend la bibliothèque");
                if (window.Session is null) throw new InvalidOperationException("pas de session après la party");
            }

            // —— Films, search ——
            // As on Windows, while Jellyfin answers: the cards' shapes, then Jellyfin's soft placeholder until each poster.
            if (mock is not null) mock.Slow = TimeSpan.FromSeconds(2.5);
            window.Go("Movie");
            if (mock is not null)
            {
                var shaped = await Until(() => window.Current is BrowsePage { SkeletonCards: > 0 }, 5);
                var shapes = (window.Current as BrowsePage)?.SkeletonCards ?? 0;
                await Task.Delay(400); Capture(window, output, "03-films-loading");
                Check(shaped, "films : la forme des cartes pendant le chargement, comme sous Windows", $"{shapes} cartes");
                var blurred = await Until(() => window.Current is BrowsePage { Items.Count: > 0, SkeletonCards: 0 } page && Cards.Blurred(page) > 0, 10);
                await Task.Delay(300); Capture(window, output, "03-films-blur");
                Check(blurred, "films : l’aperçu flou de Jellyfin avant chaque affiche", $"{(window.Current is BrowsePage page ? Cards.Blurred(page) : 0)} aperçus");
                mock.Slow = default;
            }
            Check(await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15), "films : grille", $"{(window.Current as BrowsePage)?.Items.Count} films");
            var movie = ((BrowsePage)window.Current!).Items.FirstOrDefault(x => x.Id == "movie-1") ?? ((BrowsePage)window.Current!).Items.First();
            await Task.Delay(1000); Capture(window, output, "03-films");
            if (window.Current is BrowsePage films) { films.ToggleFilters(); await Task.Delay(500); Capture(window, output, "03b-films-filtres"); films.ToggleFilters(); }
            // Left and come back to, as on Windows (PageMemory): the same page, scrolled where it was, not filled again from the start.
            if (window.Current is BrowsePage left && left.Content is ScrollViewer leftScroll)
            {
                leftScroll.Offset = new Vector(0, Math.Max(0, Math.Min(300, leftScroll.Extent.Height - leftScroll.Viewport.Height))); await Task.Delay(200);
                var before = leftScroll.Offset.Y;
                window.Go("home"); await Task.Delay(400);
                var away = window.Current is HomePage;
                window.Go("Movie"); await Task.Delay(800);
                Check(away && window.Current == left && Math.Abs(leftScroll.Offset.Y - before) < 2, "films : la page revient comme laissée", $"{before:0} px, puis {leftScroll.Offset.Y:0} px, même page {window.Current == left}");
                leftScroll.Offset = default;
            }
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
            // Édition of the menu bar presses the field's own keys again: select all, cut, then undo, as the keys do.
            var box = ((BrowsePage)window.Current!).SearchBox;
            box.Focus(); await Task.Delay(200);
            window.ChooseInMenu(Lang.L("Tout sélectionner", "Select All"));
            var all = box.SelectionStart == 0 && box.SelectionEnd == (box.Text?.Length ?? -1);
            window.ChooseInMenu(Lang.L("Couper", "Cut"));
            var cut = await Until(() => string.IsNullOrEmpty(box.Text), 3);
            window.ChooseInMenu(Lang.L("Annuler", "Undo"));
            Check(all && cut && await Until(() => box.Text == "zqxwv", 3), "barre des menus : Tout sélectionner, Couper et Annuler dans un champ", $"sélection {all}, coupé {cut}, « {box.Text} »");
            // As on Windows: × with some text, and Échap first clears the search, staying on its page.
            var searchPage = (BrowsePage)window.Current!;
            var crossShown = searchPage.SearchClear?.IsVisible == true;
            box.Focus(); Press(window, Avalonia.Input.Key.Escape);
            Check(crossShown && string.IsNullOrEmpty(box.Text) && searchPage.SearchClear?.IsVisible == false && window.Current == searchPage && searchPage.SearchBox.Bounds.Width <= 761,
                "recherche : × pour l’effacer, Échap l’efface d’abord, 760 px au plus", $"× {crossShown}, « {box.Text} », page {window.Current?.GetType().Name}, × ensuite {searchPage.SearchClear?.IsVisible}, {searchPage.SearchBox.Bounds.Width:0.0} px");
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
            // As on Windows: the picture fills the window, Lecture and « Regarder ensemble » keep their words, the others are
            // the same rounded squares on or off (« Favori », « Vu »: their glyph alone changes); the cast with their photos under the picture.
            if (window.Current is DetailPage film)
            {
                var buttons = film.ActionButtons().ToList();
                Check(film.HeroHeight >= film.Bounds.Height - 1 && buttons.Count >= 3 && buttons.Count(x => x.Words) == 1,
                    "fiche d’un film : image de toute la fenêtre, boutons ronds", $"{film.HeroHeight:0}/{film.Bounds.Height:0} px · " + string.Join(", ", buttons.Select(x => (x.Words ? "« " + x.Name + " »" : x.Name) + (x.On ? " (actif)" : ""))));
                Check(film.CastShown == 2, "fiche d’un film : distribution en photos", $"{film.CastShown} personnes");
                // As on Windows, the rail keeps lit the page the title was opened from, never nothing.
                Check(window.LitRail is { Length: > 0 } and not "settings", "fiche d’un film : le rail garde allumée la page d’où elle vient", window.LitRail ?? "rien");
                film.ShowCastForCheck(); await Task.Delay(800); Capture(window, output, "05b-film-cast");
            }
            // —— The menu bar, as a Mac app's (MainWindow.Menu.cs): its items, their keys, and each one doing what its keys do ——
            var bar = window.MenuBar();
            var command = window.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Meta;
            KeyGesture? KeysOf(string name) => bar.FirstOrDefault(x => x.Item == name).Keys;
            Check(bar.Select(x => x.Menu).Distinct().SequenceEqual([Lang.L("Édition", "Edit"), Lang.L("Présentation", "View"), Lang.L("Aller", "Go"), Lang.L("Fenêtre", "Window"), Lang.L("Aide", "Help")])
                && Equals(KeysOf(Lang.L("Passer en mode plein écran", "Enter Full Screen")), new KeyGesture(Avalonia.Input.Key.F, KeyModifiers.Control | KeyModifiers.Meta)) && Equals(KeysOf(Lang.L("Placer dans le Dock", "Minimize")), new KeyGesture(Avalonia.Input.Key.M, command))
                && Equals(KeysOf(Lang.L("Fermer la fenêtre", "Close Window")), new KeyGesture(Avalonia.Input.Key.W, command)) && Equals(KeysOf(Lang.L("Précédent", "Back")), new KeyGesture(Avalonia.Input.Key.OemOpenBrackets, command))
                && KeysOf(Lang.L("Copier", "Copy")) is not null && KeysOf(Lang.L("Coller", "Paste")) is not null,
                "barre des menus : Édition, Présentation, Aller, Fenêtre, Aide et leurs raccourcis", string.Join(", ", bar.Select(x => x.Keys is { } k ? $"{x.Item} {k}" : x.Item)));
            Check(Equals(KeysOf(Lang.L("Accueil", "Home")), new KeyGesture(Avalonia.Input.Key.D1, command)) && Equals(KeysOf(Lang.L("Films", "Movies")), new KeyGesture(Avalonia.Input.Key.D2, command))
                && Equals(KeysOf(Lang.L("Séries", "Shows")), new KeyGesture(Avalonia.Input.Key.D3, command)) && Equals(KeysOf(Lang.L("Toute la bibliothèque", "Whole library")), new KeyGesture(Avalonia.Input.Key.D4, command))
                && Equals(KeysOf(Lang.L("Téléchargements", "Downloads")), new KeyGesture(Avalonia.Input.Key.D5, command)),
                "barre des menus : ⌘1 à ⌘5 comme Ctrl+1 à Ctrl+5 sous Windows");
            window.ChooseInMenu(Lang.L("Précédent", "Back"));
            var backed = await Until(() => window.Current is not DetailPage, 3);
            window.ChooseInMenu(Lang.L("Suivant", "Forward"));
            Check(backed && await Until(() => window.Current is DetailPage, 3), "barre des menus : Précédent et Suivant comme ⌘[ et ⌘]");
            window.ChooseInMenu(Lang.L("Passer en mode plein écran", "Enter Full Screen"));
            var full = await Until(() => window.WindowState == WindowState.FullScreen, 3);
            await Task.Delay(1000);
            full = full && window.ChooseInMenu(Lang.L("Quitter le mode plein écran", "Exit Full Screen"));
            Check(full && await Until(() => window.WindowState != WindowState.FullScreen, 3), "barre des menus : plein écran (⌃⌘F) et retour", window.WindowState.ToString());
            await Task.Delay(800);
            // Aide (MainWindow.Shortcuts.cs): the shortcuts in Aide's window (⌘/), the guide and the report beside them.
            Check(Equals(KeysOf(Lang.L("Raccourcis clavier", "Keyboard Shortcuts")), new KeyGesture(Avalonia.Input.Key.OemQuestion, command))
                && KeysOf(Lang.L("Guide de Mira pour Mac", "Mira for Mac Guide")) is null && bar.Any(x => x.Item == Lang.L("Exporter un rapport…", "Export a Report…"))
                && MainWindow.Guide.Host == "wiki.miracinema.app", "barre des menus : Aide (guide, raccourcis ⌘/, rapport)", MainWindow.Guide.ToString());
            window.ChooseInMenu(Lang.L("Raccourcis clavier", "Keyboard Shortcuts"));
            var lines = MainWindow.Shortcuts.Sum(x => x.Lines.Count);
            Check(await Until(() => window.Current is SettingsPage { HelpTopic: "keys" }, 3), "Aide : raccourcis clavier dans la fenêtre de l’aide", $"{lines} raccourcis");
            await Task.Delay(500); Capture(window, output, "05h-shortcuts");
            // Its search finds a shortcut by what it does, without regard to accents; Échap clears it, then closes the window,
            // back to the title's page under it.
            if (window.Current is SettingsPage help)
            {
                help.SearchFor(Lang.L("ecran", "screen"));
                var found = help.Search.Length > 0 && help.KeysShown is > 0 and < 20;
                await Task.Delay(300); Capture(window, output, "05i-help-search");
                var cleared = window.Back() && help.Search.Length == 0 && help.KeysShown == lines;
                window.Back();
                Check(found && cleared && await Until(() => window.Current is DetailPage, 3), "Aide : recherche sans accents, Échap l’efface puis ferme la fenêtre", $"{help.KeysShown} raccourcis, page {window.Current?.GetType().Name}");
            }
            // ⌘W as on a Mac (MainWindow.MacWindow.cs): the window goes to the Dock, Mira keeps running, the Dock icon brings it
            // back; its place remembered for the next opening. Called directly: on Linux closing still quits.
            var state = window.WindowState;
            await window.CloseToDockAsync();
            var docked = window.InDock && !window.IsVisible && window.Settings.WindowX is not null;
            window.Reopen();
            Check(docked && await Until(() => window.IsVisible && !window.InDock, 3) && window.Current is DetailPage, "⌘W : fenêtre dans le Dock, rouverte au clic, place gardée",
                $"x {window.Settings.WindowX}, y {window.Settings.WindowY}, {state}");
            await Task.Delay(800);
            if (mock is not null && window.Current is DetailPage still)
            {
                // A click on Favori or Vu moves nothing on the page, as on Windows: the film picked up halfway, whose « Vu »
                // took away « Depuis le début » and the progress line, measured from the click to well after Jellyfin's answer.
                var moved = new List<string>();
                foreach (var kind in new[] { "favorite", "watched", "watched", "favorite" })
                {
                    window.HideNotice(); await Task.Delay(300);
                    // Places as drawn: laid out first. Jellyfin's answer draws the page's texts again (RefreshAsync), and
                    // a look taken between that and the next layout pass, which comes before any frame, saw new texts at
                    // 0 × 0 that never show (a slow runner landed there three times).
                    still.UpdateLayout();
                    var before = still.TopPlaces();
                    still.ClickForCheck(kind);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 1200)
                    {
                        await Task.Delay(16);
                        still.UpdateLayout();
                        foreach (var (part, now) in still.TopPlaces())
                            if (!before.TryGetValue(part, out var was)) moved.Add($"{kind}: {part} appeared");
                            else if (new[] { now.X - was.X, now.Y - was.Y, now.Width - was.Width, now.Height - was.Height }.Max(Math.Abs) > .5)
                                moved.Add($"{kind}: {part} {was.X:0},{was.Y:0} {was.Width:0}×{was.Height:0} → {now.X:0},{now.Y:0} {now.Width:0}×{now.Height:0} ({clock.ElapsedMilliseconds} ms)");
                    }
                    if (window.NoticeText is { Length: > 0 } notice) moved.Add($"{kind}: a notice « {notice} »");
                }
                Check(moved.Count == 0, "fiche d’un film : un clic sur Favori ou Vu ne bouge rien, sans avis", string.Join(" ; ", moved.Distinct().Take(6)));
            }
            // A favourite: « Favori » solid, the same square as off, no words.
            if (mock is not null)
            {
                window.Open(new MediaItem { Id = "movie-2", Name = "Brume basse", Type = "Movie" });
                var lit = await Until(() => window.Current is DetailPage { Item.UserData.IsFavorite: true } page && page.ActionButtons().Any(x => x.On && !x.Words), 15);
                Check(lit, "fiche d’un favori : « Favori » plein, même carré, sans mots", string.Join(", ", (window.Current as DetailPage)?.ActionButtons().Where(x => x.On).Select(x => x.Name) ?? []));
                await Task.Delay(800); Capture(window, output, "05c-film-favorite");
                // —— Notices, as on Windows: one with a button waits under the next one, and comes back once it is closed ——
                window.Notice("Avis avec un bouton", "Agir", () => { });
                window.Notice("Avis suivant");
                var next = window.NoticeText;
                window.HideNotice();
                Check(next == "Avis suivant" && window.NoticeText == "Avis avec un bouton" && window.NoticeAction == "Agir", "avis : celui qui a un bouton attend sous le suivant, puis revient",
                    $"« {next} », puis « {window.NoticeText} » · {window.NoticeAction}");
                window.AddNotice("Une ligne de plus.");
                Check(window.NoticeText == "Avis avec un bouton\nUne ligne de plus." && window.NoticeAction == "Agir", "avis : une ligne ajoutée garde le bouton", window.NoticeText ?? "");
                window.HideNotice(); await Task.Delay(300);

                // —— In the window, as on Windows: « Ajouter à une collection », « Identifier… », « Inviter quelqu’un » ——
                var collecting = window.CollectAsync(movie);
                var sheetShown = await Until(() => window.CurrentSheet is { IsVisible: true } && window.FocusManager?.GetFocusedElement() is TextBox, 5);
                await Task.Delay(800); Capture(window, output, "05d-collection");
                Check(sheetShown && TabStaysIn(window), "collections : une feuille dans la fenêtre, le nom prêt à taper, Tab gardé dedans");
                if (window.FocusManager?.GetFocusedElement() is TextBox name)
                {
                    name.Text = "Soirées";
                    name.RaiseEvent(new KeyEventArgs { Key = Avalonia.Input.Key.Enter, RoutedEvent = InputElement.KeyDownEvent, Source = name });
                }
                var made = await Until(() => collecting.IsCompleted, 10);
                Check(made && mock.CollectionMade == "Soirées" && window.CurrentSheet is null && window.NoticeText?.Contains("Soirées") == true, "collections : une nouvelle, faite par Entrée, dite par un avis",
                    $"{mock.CollectionMade} · {window.NoticeText}");
                window.HideNotice();
                var identify = new IdentifySheet(window.Session!, movie);
                var identifying = identify.ShowAsync(window);
                var proposed = await Until(() => identify.Proposals == 2, 10);
                await Task.Delay(600); Capture(window, output, "05e-identify");
                Check(proposed && TabStaysIn(window), "identifier : une feuille, les propositions de Jellyfin, Tab gardé dedans", $"{identify.Proposals} propositions");
                window.RaiseEvent(new KeyEventArgs { Key = Avalonia.Input.Key.Escape, RoutedEvent = InputElement.KeyDownEvent, Source = window });
                Check(await Until(() => identifying.IsCompleted && window.CurrentSheet is null, 3), "identifier : Échap la ferme");
                var inviting = InviteSheet.ShowAsync(window, "Envoie ce lien : qui l’ouvre te rejoint.", "http://jellyfin.local:8096/Mira/party/AbCdEfGhIjKlMnOpQrStUv", Together.PartySteps(null), warnLast: true);
                await Until(() => window.CurrentSheet is { IsVisible: true }, 5);
                await Task.Delay(600); Capture(window, output, "05f-invite");
                var copyFocused = window.FocusManager?.GetFocusedElement() is Button { Classes: var classes } && classes.Contains("primary");
                var inviteTab = TabStaysIn(window);
                window.RaiseEvent(new KeyEventArgs { Key = Avalonia.Input.Key.Escape, RoutedEvent = InputElement.KeyDownEvent, Source = window });
                Check(copyFocused && inviteTab && await Until(() => inviting.IsCompleted && window.CurrentSheet is null, 3), "inviter : une feuille, « Copier le lien » prêt, Tab gardé dedans, Échap la ferme", $"focus sur « Copier le lien » {copyFocused}, Tab {inviteTab}");
                // A question on Mira's card, as Windows asks before following a series: the focus on the safe answer, Échap = no.
                var asking = window.ConfirmAsync(Lang.L("Suivre « Série » ?", "Follow “Series”?"), Lang.L("Le PC serveur cherchera ses nouveaux épisodes.", "The server PC will look for its new episodes."), Lang.L("Suivre la série", "Follow the series"), Lang.L("Annuler", "Cancel"));
                await Until(() => window.CurrentSheet is { IsVisible: true }, 5);
                await Task.Delay(400); Capture(window, output, "05j-confirm");
                var safeFocus = window.FocusManager?.GetFocusedElement() is Button { Classes: var safe } && safe.Contains("quiet");
                window.RaiseEvent(new KeyEventArgs { Key = Avalonia.Input.Key.Escape, RoutedEvent = InputElement.KeyDownEvent, Source = window });
                var refused = await Until(() => asking.IsCompleted, 3) && !asking.Result && window.CurrentSheet is null;
                var accepting = window.ConfirmAsync("?", "", Lang.L("Oui", "Yes"), Lang.L("Non", "No"));
                await Until(() => window.CurrentSheet is { IsVisible: true }, 5);
                var accepted = window.AnswerConfirm(true) && await Until(() => accepting.IsCompleted, 3) && accepting.Result && window.CurrentSheet is null && !window.Asking;
                Check(safeFocus && refused && accepted, "question sur une carte de Mira : focus sur la réponse sûre, Échap = non, le bouton = oui", $"focus {safeFocus}, Échap {refused}, oui {accepted}");
            }
            window.Go("Series");
            await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15);
            var series = ((BrowsePage)window.Current!).Items.OrderByDescending(x => x.Name.Contains("Longue", StringComparison.OrdinalIgnoreCase)).First();
            window.Open(series);
            Check(await Until(() => window.Current is DetailPage { Episodes.Count: > 0 }, 20), "fiche d’une série : épisodes", $"{series.Name} : {(window.Current as DetailPage)?.Episodes.Count} épisodes");
            if (window.Current is DetailPage { Episodes.Count: > 100 } longSeries)
                Check(longSeries.SeasonLabels.Any(x => x.Contains("1–100")) && longSeries.SeasonLabels.Count() > 1, "longue saison par tranches", string.Join(" | ", longSeries.SeasonLabels));
            // TheTVDB's episodes « à leur place »: the seasons the library has none of at the end of the list, none above the episodes.
            if (window.Current is DetailPage { Episodes.Count: > 100 } shelfSeries)
                Check(await Until(() => shelfSeries.SeasonLabels.Contains(Lang.L("Saison 2 · absente", "Season 2 · not in library")) && shelfSeries.SeasonLabels.Last() == Lang.L("Saison 3 · à venir", "Season 3 · coming"), 10),
                    "épisodes manquants et à venir : saisons absentes dans la liste", string.Join(" | ", shelfSeries.SeasonLabels));
            if (window.Current is DetailPage { Episodes.Count: > 100 } shelfPage && shelfPage.SeasonLabels.FirstOrDefault(x => x.EndsWith("–130", StringComparison.Ordinal)) is { } lastSlice)
            {
                // The end of season 1: two missing at their place, the next one hollow, those after it; then season 2, a card.
                Check(shelfPage.ShowSeasonForCheck(lastSlice), "épisodes manquants et à venir : fin de saison");
                await Task.Delay(900); Capture(window, output, "06c-series-shelf");
                Check(shelfPage.ShowSeasonForCheck(Lang.L("Saison 2 · absente", "Season 2 · not in library")), "épisodes manquants et à venir : saison absente");
                await Task.Delay(900); Capture(window, output, "06d-series-absent-season");
                // « Télécharger la saison 2 » (an administrator, the relay's agent places it): the menu searches on the PC of
                // Jellyfin; the stand-in's results are another title's, the menu says nothing was found and offers another name.
                Check(await Until(() => shelfPage.MissingOffered, 10) && shelfPage.OpenMissingForCheck(), "télécharger les manquants : bouton de la saison absente");
                Check(await Until(() => shelfPage.MissingMenuShown == "nothing", 15), "télécharger les manquants : menu, rien trouvé", shelfPage.MissingMenuShown ?? "fermé");
                await Task.Delay(500); Capture(window, output, "06f-missing-menu");
                shelfPage.CloseMissingForCheck();
                shelfPage.ShowSeasonForCheck(shelfPage.SeasonLabels.First());
                Check(shelfPage.PillsShown && shelfPage.TilesShown > 0, "épisodes en grille, saisons en pastilles", $"{shelfPage.TilesShown} tuiles");
                shelfPage.ShowEpisodesForCheck(); await Task.Delay(900); Capture(window, output, "06e-series-grid");
            }
            await Task.Delay(1200); Capture(window, output, "06-series");
            // Refreshed as after the player or a title marked watched: the episodes stay, and the page with them scrolls.
            if (window.Current is DetailPage seriesPage)
            {
                await seriesPage.RefreshAsync(); await seriesPage.RefreshAsync();
                Check(seriesPage.EpisodesShown, "fiche d’une série : épisodes gardés après une actualisation");
            }
            // Two fingers to the right: the page follows them, the one before (the series grid) under it; let go short of
            // the way, it comes back; past it, the page before; to the left: this page again.
            const string swipeCheck = "trackpad : la page suit deux doigts, revient lâchée trop tôt ; vers la droite = Retour, vers la gauche = Avant";
            // With motion, whatever this machine says (the Mac runner leaves it out): the page and the posters glide.
            MacMotion.Override(reduced: false);
            var swipeAt = new Point(window.Bounds.Width / 2, 140);
            if (window.InputHitTest(swipeAt) is Avalonia.Interactivity.Interactive swipeTarget && window.Current is DetailPage swipedFrom)
            {
                Swipe(swipeTarget, swipeAt, .15, 0, 8);
                var followed = window.Sliding;
                var cameBack = await Until(() => !window.Sliding, 3) && window.Current == swipedFrom;
                Swipe(swipeTarget, swipeAt, .15, 0, 30);
                // Under the fingers, before they leave: the title page on top, the series grid coming under it.
                Capture(window, output, "06b-swipe-back");
                var wentBack = await Until(() => window.Current is BrowsePage && !window.Sliding, 3);
                if (window.InputHitTest(swipeAt) is Avalonia.Interactivity.Interactive back) Swipe(back, swipeAt, -.15, 0, 30);
                var forward = await Until(() => window.Current == swipedFrom && !window.Sliding, 3);
                Check(followed && cameBack && wentBack && forward, swipeCheck, $"suit {followed}, revenue {cameBack}, retour {wentBack}, avant {forward} ({window.Current?.GetType().Name})");
            }
            else Check(false, swipeCheck, "rien sous le pointeur");
            // A pinch (or ⌘+) makes the grid's posters larger: fewer of them on a line.
            window.Go("Movie");
            if (await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15))
            {
                await Task.Delay(500);
                var grid = window.Current!.GetVisualDescendants().OfType<Mira.Mac.Views.CardGrid>().First();
                var first = grid.Children[0];
                var poster = MainWindow.Glided(first);
                var narrow = first.Bounds.Width;
                window.ZoomGrids(Trackpad.ZoomMax);
                // The poster glides from its old size to its new one, frame by frame, then rests there; shown as it is set
                // (a button's own transition would show it as none), the button itself left to its theme (its press).
                var glided = poster.RenderTransform is Avalonia.Media.MatrixTransform { Value.IsIdentity: false } && first.RenderTransform is not Avalonia.Media.MatrixTransform;
                var settled = await Until(() => poster.RenderTransform is null, 3);
                var frames = MainWindow.GlideFrames;
                var wide = first.Bounds.Width;
                window.ZoomGrids(1); await Until(() => poster.RenderTransform is null, 3);
                Check(wide > narrow * 1.3 && Math.Abs(first.Bounds.Width - narrow) < 1 && glided && settled && frames > 1, "trackpad : pincer agrandit les affiches de la grille, qui glissent à leur place", $"{narrow:0} → {wide:0} px, puis {first.Bounds.Width:0} ; glisse {glided}, posée {settled} en {frames} images");
                // With macOS's « Réduire les animations »: the posters at their new size at once, nothing gliding.
                MacMotion.Override(reduced: true);
                window.ZoomGrids(Trackpad.ZoomMax);
                var unmoved = poster.RenderTransform is null;
                var placed = first.Bounds.Width;
                window.ZoomGrids(1);
                Check(unmoved && Math.Abs(placed - wide) < 1 && Math.Abs(first.Bounds.Width - narrow) < 1, "trackpad : pincer sans animations (Réduire les animations) : les affiches à leur taille d’un coup", $"{narrow:0} → {placed:0} px, puis {first.Bounds.Width:0} ; immobile {unmoved}");
            }
            MacMotion.Override(null);
            SettingsPage.OpenAt("playback");
            var litBefore = window.LitRail;
            window.Go("settings");
            var litAtOnce = window.LitRail;
            await Task.Delay(800); Capture(window, output, "07-settings");
            // A window over the page, not a page: the rail keeps the page under it lit, from the first frame on.
            Check(litBefore is not null and not "settings" && litAtOnce == litBefore && window.LitRail == litBefore, "réglages : le rail garde la page dessous allumée", $"{litBefore} → {litAtOnce} → {window.LitRail}");
            if (window.Current is SettingsPage sheetPage)
            {
                var drawn = sheetPage.Drawn;
                sheetPage.ClickTab("playback"); sheetPage.ClickTab("playback");
                Check(sheetPage.CurrentTab == "playback" && sheetPage.Drawn == drawn, "réglages : l’onglet ouvert recliqué ne refait rien", $"{sheetPage.CurrentTab}, {sheetPage.Drawn - drawn} dessins de plus");
                // The rail's avatar and « ? », as on Windows: the account (« Compte et serveur ») and Aide, in the same window.
                window.ClickRailForCheck("account");
                var account = sheetPage.CurrentTab;
                window.ClickRailForCheck("help");
                var topic = sheetPage.HelpTopic;
                sheetPage.Show("playback");
                Check(account == "server" && topic == "keys" && window.Current == sheetPage && window.LitRail == litBefore, "rail : l’avatar ouvre le compte, « ? » l’aide, comme sous Windows", $"{account}, {topic}, {window.LitRail}");
            }
            // « L’essentiel », the front page (maquette A, 2026-11-14): its shortcuts are the switches of their sections, and
            // « mettre à jour » finds « Mises à jour » while « L’essentiel » itself is never a result.
            if (window.Current is SettingsPage essentials)
            {
                essentials.Show("home"); await Task.Delay(500); Capture(window, output, "07a-settings-essentials");
                var before = window.Settings.AutoNext;
                essentials.EssentialsAutoNext.IsChecked = !before;
                var followed = essentials.AutoNextSwitch.IsChecked == !before && window.Settings.AutoNext == !before;
                essentials.AutoNextSwitch.IsChecked = before;
                var back = essentials.EssentialsAutoNext.IsChecked == before && window.Settings.AutoNext == before;
                Check(followed && back, "réglages : un raccourci de « L’essentiel » est l’interrupteur de sa section", $"suivi {followed}, retour {back}");
                essentials.SearchFor(Lang.L("mettre à jour", "update"));
                var updates = essentials.Found.GetValueOrDefault("updates");
                var front = essentials.Found.GetValueOrDefault("home");
                await Task.Delay(400); Capture(window, output, "07l-settings-search-update");
                essentials.SearchFor("");
                Check(updates > 0 && front == 0 && essentials.CurrentTab == "home", "réglages : « mettre à jour » trouve « Mises à jour »", $"mises à jour {updates}, essentiel {front}, section {essentials.CurrentTab}");
                essentials.Show("playback");
            }
            // Réglages in a window over the page (MainWindow.Sheet.cs): its search finds settings in every section at once,
            // « sous titres » finding « Sous-titres », then gives back the section open before it.
            if (window.Current is SettingsPage searched)
            {
                searched.SearchFor(Lang.L("sous titres", "subtitle"));
                var count = searched.Found.Values.Sum();
                var audio = searched.Found.GetValueOrDefault("audio");
                var sections = string.Join(", ", searched.Found.Where(x => x.Value > 0).Select(x => $"{x.Key} {x.Value}"));
                await Task.Delay(400); Capture(window, output, "07k-settings-search");
                searched.SearchFor("");
                Check(count > 0 && audio > 0 && searched.CurrentTab == "playback" && window.CanGo(-1) == false, "réglages : une fenêtre, la recherche dans toutes les sections",
                    $"{sections} ; section {searched.CurrentTab}");
            }
            // The categories, as on Windows: those of the server's account hold its sections (who watches what in « Serveur Jellyfin »).
            (window.Current as SettingsPage)?.Show("jellyfin");
            // « Titres à corriger », as on Windows: the mock's titles have no ids from a metadata source.
            if (mock is not null)
                Check(await Until(() => window.Current is SettingsPage { Fix.Count: > 0, Fix.Section.IsVisible: true }, 10), "réglages : titres à corriger", $"{(window.Current as SettingsPage)?.Fix.Count} titres");
            if (mock is not null)
                Check(await Until(() => window.Current is SettingsPage { Viewing.Count: > 0 }, 10), "réglages : en ce moment sur Jellyfin", $"{(window.Current as SettingsPage)?.Viewing.Count} lectures");
            // « Extension Mira sur Jellyfin »: offered to an administrator of a Jellyfin without it, then installed from the
            // release Mira checked (here the mock's), Jellyfin restarted only once nobody watches, and gone once served.
            if (mock is not null && window.Current is SettingsPage withExtension)
            {
                var hidden = await Until(() => !withExtension.Extension.Section.IsVisible, 3);
                mock.NoExtension = true; mock.Watching = true;
                withExtension.Show("playback"); withExtension.Show("jellyfin");
                var offered = await Until(() => withExtension.Extension.Section.IsVisible, 10);
                if (hidden && offered)
                {
                    ExtensionSection.Verified = () => Task.FromResult(MiraWeb.RepositoryFor(Updates.Current)); ExtensionSection.Wait = TimeSpan.FromMilliseconds(300);
                    withExtension.Extension.Section.BringIntoView(); await Task.Delay(400); Capture(window, output, "07j-settings-extension");
                    withExtension.Extension.InstallForCheck();
                    var waited = await Until(() => mock.PluginInstalled && withExtension.Extension.Status.Contains(Lang.L("dès que personne ne regarde", "as soon as nobody is watching")), 10);
                    bool early; lock (mock.Calls) early = mock.Calls.Contains("POST System/Restart");
                    mock.Watching = false;
                    var restarted = await Until(() => { lock (mock.Calls) return mock.Calls.Contains("POST System/Restart"); }, 10);
                    mock.NoExtension = false;
                    var done = await Until(() => !withExtension.Extension.Section.IsVisible, 15);
                    Check(waited && !early && restarted && done, "réglages : installer l’extension Mira depuis le Mac", $"installée {mock.PluginInstalled}, attendue {waited}, redémarrage avant {early}, après {restarted}, cachée {done} : {withExtension.Extension.Status}");
                }
                else Check(false, "réglages : installer l’extension Mira depuis le Mac", $"cachée avec l’extension {hidden}, proposée sans {offered}");
                mock.NoExtension = false; mock.Watching = true; ExtensionSection.Verified = null; ExtensionSection.Wait = TimeSpan.FromMinutes(1);
            }
            // « Ajouter un ami », as on Windows: the account sees only the library chosen and downloads nothing.
            if (mock is not null && await Until(() => window.Current is SettingsPage { Friend.Libraries: 2 }, 10) && window.Current is SettingsPage withFriend)
            {
                withFriend.Show("sharing"); withFriend.Friend.CreateForCheck("Marc");
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
                withAudio.Show("audio"); withAudio.UnfoldAdvanced();
                Check(await Until(() => withAudio.AudioDevices >= 0, 15), "réglages : sortie audio", $"{withAudio.AudioDevices} sorties");
                withAudio.TestSoundForCheck();
                var heard = await Until(() => withAudio.SoundTested is not null && !File.Exists(Path.Combine(Path.GetTempPath(), "Mira-sound-test.wav")), 15);
                Check(heard && withAudio.SoundTested!.Contains(AudioOutput.TestSound(window.Settings.AudioChannels).Speakers[^1]), "réglages : tester la sortie audio", withAudio.SoundTested ?? "rien");
                withAudio.AudioOutputRow.BringIntoView(); await Task.Delay(500); Capture(window, output, "07d-settings-audio");
            }
            if (window.Current is SettingsPage { Fix.Section.IsVisible: true } withFix) { withFix.Show("care"); withFix.Fix.Section.BringIntoView(); await Task.Delay(500); Capture(window, output, "07b-settings-fix"); }
            // The other sections, as on Windows: the account and the way to Jellyfin, this Mac's version and kept titles, Home's rows.
            if (window.Current is SettingsPage others)
                foreach (var (tab, shot) in new[] { ("server", "07e-settings-server"), ("updates", "07f-settings-updates"), ("library", "07g-settings-library"), ("general", "07m-settings-general"), ("audio", "07h-settings-subtitles") })
                { others.Show(tab); await Task.Delay(500); Capture(window, output, shot); }
            // Confidentialité: every connection, and the update check's switch the same as the one of « Mises à jour ».
            if (window.Current is SettingsPage privacy)
            {
                privacy.Show("privacy"); await Task.Delay(500); Capture(window, output, "07i-settings-privacy");
                var updates = window.Settings.AutoUpdate;
                if (privacy.PrivacyUpdatesSwitch is { } privacySwitch) privacySwitch.IsChecked = !updates;
                var followed = window.Settings.AutoUpdate == !updates;
                if (privacy.PrivacyUpdatesSwitch is { } back) back.IsChecked = updates;
                Check(privacy.PrivacyUpdatesSwitch is not null && followed && window.Settings.AutoUpdate == updates, "confidentialité : l’interrupteur des mises à jour est celui de « Mettre à jour automatiquement »");
            }

            // —— Téléchargements: sent to the PC of Jellyfin ——
            if (mock is not null)
            {
                Check(await Until(() => window.Offers("downloads"), 5), "téléchargements : proposés à un administrateur");
                // Most Jellyfin servers have no Mira extension: the page could not work there, so it is not offered.
                mock.NoExtension = true; await window.AskRightsAsync();
                var withoutExtension = window.Offers("downloads");
                mock.NoExtension = false; await window.AskRightsAsync();
                Check(!withoutExtension && window.Offers("downloads"), "téléchargements : pas proposés sans l’extension de Mira sur Jellyfin, de nouveau avec elle");
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
                    // Closed into the Dock, the page asks Jellyfin nothing more (it asked every 3 s): nobody looks. Back from the
                    // Dock, it asks again, and the page is read again.
                    int Asked() { lock (mock.Calls) return mock.Calls.Count; }
                    List<string> AskedSince(int from) { lock (mock.Calls) return mock.Calls.Skip(from).ToList(); }
                    var polled = await Until(() => AskedSince(0).Count(x => x == "GET Mira/torlink") > 0, 4);
                    await window.CloseToDockAsync();
                    await Task.Delay(300);
                    var before = Asked();
                    await Task.Delay(7500);
                    var whileDocked = AskedSince(before);
                    var asleep = !downloads.IsShowing && window.Away;
                    var after = Asked();
                    window.Reopen();
                    var awake = await Until(() => AskedSince(after).Contains("GET Mira/torlink"), 6) && downloads.IsShowing;
                    Check(polled && asleep && whileDocked.Count == 0 && awake, "Dock : rien demandé à Jellyfin pendant 7,5 s, de nouveau au retour",
                        whileDocked.Count == 0 ? $"au retour : {string.Join(", ", AskedSince(after).Distinct())}" : string.Join(", ", whileDocked));
                }
            }

            // ⌘1 to ⌘5 as Ctrl+1 to Ctrl+5 on Windows: on a Mac the Go menu takes them, here the window reads them.
            Press(window, Avalonia.Input.Key.D2, command);
            var byKeys = await Until(() => window.Current is BrowsePage { Rail: "Movie" } && window.LitRail == "Movie", 5);
            Press(window, Avalonia.Input.Key.D1, command);
            Check(byKeys && await Until(() => window.Current is HomePage && window.LitRail == "home", 5), "⌘2 puis ⌘1 : Films puis Accueil", window.Current?.GetType().Name ?? "rien");

            // —— « Mode canapé »: the window full screen, the pages larger, the arrows anywhere, a controller as those keys ——
            window.Go("home");
            await Until(() => window.Current is HomePage { Latest.Count: > 0 }, 10);
            window.SetCouchMode(true);
            await Task.Delay(1200);
            var couchZoom = Couch.Zoom(window.ClientSize.Width, window.ClientSize.Height);
            Check(window.InCouchMode && window.Classes.Contains("couch") && window.CouchZoom == couchZoom && (couchZoom > 1 || window.ClientSize.Width <= 1280 || window.ClientSize.Height <= 720), "mode canapé : pages agrandies (écran du runner de 1024 × 768 : 1)", $"zoom {window.CouchZoom:0.00} pour {window.ClientSize.Width:0} × {window.ClientSize.Height:0}");
            Control? Focused() => window.FocusManager?.GetFocusedElement() as Control;
            Rect? Where(Control? control) => control?.TransformToVisual(window) is { } m ? new Rect(control.Bounds.Size).TransformToAABB(m) : null;
            window.FocusFirst();
            var start = Focused();
            await Task.Delay(200);
            Capture(window, output, "10-couch-home");
            Check(window.CouchRing is { } ring && Where(start) is { } at && ring.Contains(at) && ring.Width < at.Width + 40, "mode canapé : l’anneau entoure le contrôle qui a le focus", $"{window.CouchRing} autour de {Where(start)}");
            Press(window, Avalonia.Input.Key.Down); await Task.Delay(300);
            var below = Focused();
            Check(start is not null && below is not null && below != start && Where(below)?.Top > Where(start)?.Top, "mode canapé : ↓ va au contrôle d’en dessous", $"{start?.GetType().Name} → {below?.GetType().Name}");
            var reachedRail = false;
            for (var i = 0; i < 12 && !reachedRail; i++) { Press(window, Avalonia.Input.Key.Left); await Task.Delay(120); reachedRail = Focused()?.Classes.Contains("rail") == true; }
            var railAt = Focused();
            // Up the rail, or down from its top: the arrows stay in it.
            Press(window, Avalonia.Input.Key.Up); await Task.Delay(150);
            if (Focused() == railAt) { Press(window, Avalonia.Input.Key.Down); await Task.Delay(150); }
            Check(reachedRail && Focused()?.Classes.Contains("rail") == true && Focused() != railAt, "mode canapé : ← mène au rail, ↑ ou ↓ le parcourt", $"{Avalonia.Automation.AutomationProperties.GetName(railAt!)} → {(Focused() is { } f ? Avalonia.Automation.AutomationProperties.GetName(f) : "rien")}");
            Press(window, Avalonia.Input.Key.Right); await Task.Delay(300);
            Check(Focused() is { } inPage && !inPage.Classes.Contains("rail") && window.Current?.IsVisualAncestorOf(inPage) == true, "mode canapé : → revient dans la page", Focused()?.GetType().Name ?? "rien");
            window.Go("Movie");
            await Until(() => window.Current is BrowsePage { Items.Count: > 1 }, 10); await Task.Delay(600);
            var posters = window.Current!.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("card") && x.IsEffectivelyVisible).ToList();
            var firstPoster = posters.FirstOrDefault();
            firstPoster?.Focus(NavigationMethod.Directional);
            Press(window, Avalonia.Input.Key.Right); await Task.Delay(300);
            var secondPoster = Focused();
            await Task.Delay(300); Capture(window, output, "10b-couch-films");
            Check(firstPoster is not null && secondPoster != firstPoster && Where(secondPoster)?.Left > Where(firstPoster)?.Left && Math.Abs((Where(secondPoster)?.Top ?? 0) - (Where(firstPoster)?.Top ?? 1)) < 2, "mode canapé : → passe à l’affiche voisine", $"{posters.Count} affiches");
            Press(window, Avalonia.Input.Key.Enter);
            Check(await Until(() => window.Current is DetailPage, 10), "mode canapé : Entrée ouvre la fiche", window.Current?.GetType().Name ?? "");
            await Task.Delay(800); Capture(window, output, "10c-couch-film");
            Press(window, Avalonia.Input.Key.Back);
            Check(await Until(() => window.Current is BrowsePage, 5), "mode canapé : Retour arrière revient", window.Current?.GetType().Name ?? "");
            // A controller (MacGamepad stood in for): offered while couch mode is off, then its cross and its A and B.
            MacGamepad.Override(new PadState(Mira.Core.Pad.None));
            var padSeen = await Until(() => window.PadConnected, 4);
            firstPoster?.Focus(NavigationMethod.Directional);
            async Task PadPress(Mira.Core.Pad button) { MacGamepad.Override(new PadState(button)); await Task.Delay(120); MacGamepad.Override(new PadState(Mira.Core.Pad.None)); await Task.Delay(120); }
            await PadPress(Mira.Core.Pad.Right);
            var padMoved = Focused() != firstPoster;
            await PadPress(Mira.Core.Pad.A);
            var padOpened = await Until(() => window.Current is DetailPage, 10);
            await PadPress(Mira.Core.Pad.B);
            Check(padSeen && padMoved && padOpened && await Until(() => window.Current is BrowsePage, 5), "mode canapé : la manette se déplace, ouvre (A) et revient (B)", $"vue {padSeen}, déplacée {padMoved}, fiche {padOpened}, revenue {window.Current is BrowsePage}");
            MacGamepad.StopOverride();
            window.SetCouchMode(false);
            await Task.Delay(800);
            Check(!window.InCouchMode && window.CouchZoom == 1 && !window.Classes.Contains("couch"), "mode canapé : coupé, la fenêtre comme avant", $"zoom {window.CouchZoom:0.00}");

            // —— Space over the button that started the film ——
            // Lecture clicked keeps the focus under the player, and Avalonia presses a focused button when Space is
            // released: the film started over instead of pausing (0.9.4, on a real Mac). The keys go where macOS sends
            // them, to the focus, through the window's handlers.
            window.Open(movie);
            if (await Until(() => window.Current is DetailPage { PlayButton.IsEffectivelyVisible: true }, 15) && window.Current is DetailPage detail && detail.PlayButton is { } lecture)
            {
                // As a click leaves it (the page may still be arriving: the focus is taken once it can be).
                var focused = await Until(() => lecture.Focus(), 5);
                lecture.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                var started = await Until(() => window.Player is { IsFileLoaded: true, Engine: not null }, 40);
                var first = window.Player;
                var moved = !detail.IsKeyboardFocusWithin;
                Press(window, Avalonia.Input.Key.Space);
                await Task.Delay(1500);
                Check(started && window.Player == first && first?.Engine?.Flag("pause") == true && focused && moved, "lecteur : Espace met en pause, le clavier n’est plus sur la page dessous",
                    $"lancé {started}, même lecteur {window.Player == first}, pause {first?.Engine?.Flag("pause")}, focus quitté {moved} (pris {focused})");
                // The focus brought back onto Lecture (Tab may reach the page under the player): Space plays on, never starts over.
                lecture.Focus();
                Press(window, Avalonia.Input.Key.Space);
                await Task.Delay(1500);
                Check(window.Player == first && first?.Engine?.Flag("pause") == false, "lecteur : Espace relâché sur Lecture ne relance pas le film",
                    $"même lecteur {window.Player == first}, pause {first?.Engine?.Flag("pause")}, focus sur Lecture {lecture.IsFocused}");
                await window.StopPlaybackAsync();
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
                Check(info.Split('\n').Length == (player.Surface is Playback.VideoView { LastLook.Length: > 0 } ? 9 : 8) && info.Contains(Lang.L("\nSortie  Par défaut du Mac", "\nOutput  Mac default")) && info.Contains("OpenGL ·") == player.Surface is Playback.VideoView && PictureQuality.Choices.Any(x => info.Contains(PictureQuality.Label(x.Key))), $"informations de lecture ({renderer})", info.Replace('\n', ' '));
                var audio = engine.Get("current-ao");
                if (string.IsNullOrEmpty(audio)) Check(true, $"volume à 150 % ({renderer})", "pas de sortie audio sur cette machine : non mesurable");
                else Check((engine.Get("af") ?? "").Contains(VolumeBoost.Label) && Math.Abs(engine.Number("volume") - 100) < 0.5, $"volume à 150 % ({renderer})", $"{player.Volume}, sortie {audio}, af {engine.Get("af")}");
                // « Son de nuit », as on Windows: the compressor at the head of mpv's chain, before the boost, and gone again.
                player.ChooseNightSound(true);
                var chain = engine.Get("af") ?? "";
                if (string.IsNullOrEmpty(audio)) Check(true, $"son de nuit ({renderer})", "pas de sortie audio sur cette machine : non mesurable");
                else Check(engine.NightSoundOn && chain.IndexOf(NightSound.Label, StringComparison.Ordinal) is >= 0 and var night && night < chain.IndexOf(VolumeBoost.Label, StringComparison.Ordinal) && window.Settings.NightSound, $"son de nuit avant l’amplification ({renderer})", chain);
                player.ChooseNightSound(false);
                Check(!engine.NightSoundOn && !(engine.Get("af") ?? "").Contains(NightSound.Label, StringComparison.Ordinal) && !window.Settings.NightSound, $"son de nuit coupé ({renderer})", engine.Get("af") ?? "");
                // « Décalage de l’audio », as on Windows: 0.1 s at a time, to mpv, kept for the next title until Mira quits.
                player.ShiftAudio(0.1); player.ShiftAudio(0.1);
                var delayed = engine.Number("audio-delay");
                player.ShiftAudio(0);
                Check(Math.Abs(delayed - .2) < .001 && Math.Abs(engine.Number("audio-delay")) < .001 && window.AudioDelay == 0, $"décalage de l’audio ({renderer})", $"{delayed:0.0} s, puis {engine.Number("audio-delay"):0.0} s");
                // A click on the picture, as macOS hands it in: to what Avalonia hits there, pausing after the double-click
                // wait; a second one plays on. mpv's OpenGL view is no target of its own (0.9.7: the click went to the page under it).
                var middle = player.TranslatePoint(new Point(player.Bounds.Width / 2, player.Bounds.Height / 2), window) ?? default;
                var hit = window.InputHitTest(middle) as Avalonia.Interactivity.Interactive;
                var wasPaused = engine.Flag("pause");
                Click(window, middle);
                var clickPaused = await Until(() => engine.Flag("pause") != wasPaused, 3);
                await Task.Delay(300);
                Click(window, middle);
                Check(clickPaused && await Until(() => engine.Flag("pause") == wasPaused, 3), $"lecteur : un clic sur l’image met en pause, un autre reprend ({renderer})",
                    $"touché {hit?.GetType().Name ?? "rien"}, pause {wasPaused} → {!wasPaused} {(clickPaused ? "oui" : "non")}, puis {engine.Flag("pause")}");
                // The progress bar answers on its whole height, not only on its 4 px line: near its top and bottom edges,
                // and in the gap between two chapters (0.9.7, on a real Mac).
                player.ShowControlsForCheck();
                await Task.Delay(300);
                var progress = player.ProgressBar;
                var unhit = new[] { (.3, 2.0), (.5, progress.Bounds.Height / 2), (.7, progress.Bounds.Height - 2) }
                    .Select(p => progress.TranslatePoint(new Point(progress.Bounds.Width * p.Item1, p.Item2), window) is { } at && window.InputHitTest(at) is Visual v && (v == progress || progress.IsVisualAncestorOf(v)) ? null : $"{p.Item1:P0}/{p.Item2:0}")
                    .OfType<string>().ToList();
                Check(progress.Bounds.Height >= 24 && unhit.Count == 0, $"lecteur : la barre répond sur toute sa hauteur ({renderer})", $"{progress.Bounds.Height:0} px de haut, ratés : {(unhit.Count == 0 ? "aucun" : string.Join(", ", unhit))}");
                var before = player.Position;
                Key(window, Avalonia.Input.Key.Right);
                Check(await Until(() => player.Position >= before + 8, 6), $"avance de 10 s ({renderer})", $"{before:0.0} → {player.Position:0.0}");
                Check(player.SeekPillText == "+10 s", $"lecteur : « +10 s » au milieu de l’image ({renderer})", player.SeekPillText ?? "rien");
                if (!software)
                {
                    // The keys of Windows: ⇧← a minute back (to the start of this short film), 5 the middle of the title,
                    // Space held (the Mac repeating it) a single pause.
                    Key(window, Avalonia.Input.Key.Left, KeyModifiers.Shift);
                    Check(await Until(() => player.Position < 1.5, 6) && player.SeekPillText == "−60 s", "lecteur : ⇧← recule d’une minute", $"{player.Position:0.0}, « {player.SeekPillText} »");
                    Key(window, Avalonia.Input.Key.D5);
                    Check(await Until(() => Math.Abs(player.Position - player.Duration / 2) < 2, 6) && player.SeekPillText == "50 %", "lecteur : 5 va à la moitié du titre", $"{player.Position:0.0} sur {player.Duration:0.0}, « {player.SeekPillText} »");
                    Key(window, Avalonia.Input.Key.Space, presses: 4);
                    await Task.Delay(500);
                    var paused = engine.Flag("pause");
                    Key(window, Avalonia.Input.Key.Space);
                    Check(paused && await Until(() => !engine.Flag("pause"), 3), "lecteur : Espace tenu ne met en pause qu’une fois");
                    // The Mac's media controls (F8, AirPods, Control Center), as the player's own keys: on a Mac through the
                    // native method macOS calls, and « Now Playing » read back from macOS.
                    var told = await Until(() => MacNowPlaying.Clock.Told is { Ready: true, Paused: false } now && now.Title == movie.DisplayTitle && now.Duration > 0, 3);
                    Check(told, "touches média : « Now Playing » dit le titre qui joue", MacNowPlaying.Clock.Told?.ToString() ?? "rien");
                    if (OperatingSystem.IsMacOS()) Check(MacNowPlaying.Listening && MacNowPlaying.ToldTitle() == movie.DisplayTitle, "touches média : macOS tient le titre", $"à l’écoute {MacNowPlaying.Listening}, « {MacNowPlaying.ToldTitle()} »");
                    Check(await Until(() => MacNowPlaying.ArtworkFor == movie.DisplayTitle, 8), "touches média : l’affiche du titre à côté", MacNowPlaying.ArtworkFor ?? "aucune");
                    MacNowPlaying.Press(MediaKey.TogglePause);
                    Check(await Until(() => engine.Flag("pause"), 3) && await Until(() => MacNowPlaying.Clock.Told is { Paused: true }, 3), "touches média : F8 met en pause, « Now Playing » le dit");
                    MacNowPlaying.Press(MediaKey.Pause);
                    await Task.Delay(400);
                    var stillPaused = engine.Flag("pause");
                    MacNowPlaying.Press(MediaKey.Play);
                    Check(stillPaused && await Until(() => !engine.Flag("pause"), 3), "touches média : Pause ne relance pas, Lecture reprend");
                    var heard = player.Position;
                    MacNowPlaying.Press(MediaKey.Back);
                    Check(await Until(() => player.Position < heard - 7, 6), "touches média : F7 recule de 10 s", $"{heard:0.0} → {player.Position:0.0}");
                    MacNowPlaying.Press(MediaKey.Seek, player.Duration / 4);
                    Check(await Until(() => Math.Abs(player.Position - player.Duration / 4) < 2, 6), "touches média : la barre de « Now Playing » déplace le film", $"{player.Position:0.0} sur {player.Duration:0.0}");
                    MacNowPlaying.Press(MediaKey.Next);
                    await Task.Delay(500);
                    Check(window.Player == player && player.Item?.Id == movie.Id, "touches média : F9 sans épisode suivant ne fait rien", player.Item?.Name ?? "");
                    // Jellyfin's remote control (MainWindow.Remote.cs), as the WebSocket hands its commands in: another
                    // device pauses, moves, turns down and mutes this Mac, and says something on its screen.
                    var session = window.Session!;
                    window.Remote(session, new RemoteCommand(RemoteAction.Pause));
                    Check(await Until(() => engine.Flag("pause"), 3), "télécommande : Pause d’un autre appareil met en pause");
                    window.Remote(session, new RemoteCommand(RemoteAction.Unpause));
                    Check(await Until(() => !engine.Flag("pause"), 3), "télécommande : Lecture reprend");
                    window.Remote(session, new RemoteCommand(RemoteAction.Seek) { Position = player.Duration / 2 });
                    Check(await Until(() => Math.Abs(player.Position - player.Duration / 2) < 2, 6), "télécommande : la position demandée", $"{player.Position:0.0} sur {player.Duration:0.0}");
                    var level = player.Sound.Level;
                    window.Remote(session, new RemoteCommand(RemoteAction.SetVolume) { Volume = 35 });
                    window.Remote(session, new RemoteCommand(RemoteAction.ToggleMute));
                    Check(player.Sound is { Level: 35, Muted: true }, "télécommande : volume à 35 %, puis muet", player.Sound.ToString());
                    window.Remote(session, new RemoteCommand(RemoteAction.Unmute));
                    player.PutBackSound(level);
                    Check(!player.Sound.Muted, "télécommande : le son revient", player.Sound.ToString());
                    window.Remote(session, new RemoteCommand(RemoteAction.Message) { Header = "Mira", Text = "Check" });
                    Check(await Until(() => player.PillText == "Mira · Check", 2), "télécommande : le message d’un autre appareil en haut du lecteur", player.PillText ?? "rien");
                    window.Remote(session, new RemoteCommand(RemoteAction.Next));
                    await Task.Delay(400);
                    Check(window.Player == player && player.Item?.Id == movie.Id, "télécommande : « suivant » sans épisode suivant ne fait rien", player.Item?.Name ?? "");
                    // Two fingers sideways over the picture: about 6 s on, the bar at once, the film when the fingers rest.
                    var from = player.Position;
                    Swipe(player, new Point(player.Bounds.Width / 2, player.Bounds.Height / 2), .15, 0, 20);
                    var aimed = player.ScrubTarget;
                    Check(aimed > from + 4 && aimed < from + 8 && await Until(() => player.Position >= aimed - 1.5, 6), "trackpad : glisser sur l’image avance dans le film", $"{from:0.0} → {aimed:0.0}, lu {player.Position:0.0}");
                }
                var unnamed = Unnamed(window);
                Check(unnamed.Count == 0, $"VoiceOver : tout est nommé (lecteur, {renderer})", string.Join(", ", unnamed.Take(8)));
                await Task.Delay(1500);
                var colour = ScreenColour(Path.Combine(output, $"08-player-{(software ? "software" : "opengl")}.png"));
                Check(colour is null || colour > 0.2, $"image visible à l’écran ({renderer})", colour is null ? "capture d’écran indisponible" : $"{colour:P0} de pixels colorés au centre");
                if (!software)
                {
                    // A grey picture (a friend's Mac, 0.8.7), made here by mpv's contrast: the player takes its steps back
                    // until it draws without OpenGL, the video still playing.
                    if (player.Surface is not Playback.VideoView) Check(true, "image grise : réparée", "pas de rendu OpenGL ici : non mesurable");
                    // Without sound, the video let go during the move would end the file: a Mac always has a sound output.
                    else if (string.IsNullOrEmpty(engine.Get("current-ao"))) Check(true, "image grise : réparée", "pas de sortie audio sur cette machine : non mesurable");
                    else
                    {
                        // « Réparer » of ⋮, for a grey screen no look sees: one step at once, said in « Informations ».
                        player.MoreButton.Flyout!.ShowAt(player.MoreButton);
                        await Task.Delay(600);
                        var repairButton = ((player.MoreButton.Flyout as Flyout)?.Content as Control)?.GetVisualDescendants().OfType<Button>()
                            .FirstOrDefault(x => Avalonia.Automation.AutomationProperties.GetName(x)?.StartsWith(Lang.L("Essayer la réparation 1", "Try repair 1")) == true);
                        repairButton?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(300);
                        var asked = player.InfoText() ?? "";
                        Check(repairButton is not null && window.Settings.VideoRepair == 1 && asked.Contains(Lang.L("Réparé", "Repaired")) && asked.Contains(Lang.L("Image lue", "Frame read")),
                            "image grise : « Réparer » à la main", $"bouton {(repairButton is null ? "absent" : "trouvé")}, étape {window.Settings.VideoRepair} — {asked.Replace('\n', ' ')}");
                        player.MoreButton.Flyout.Hide();
                        // Bars beside the picture, as on a screen wider than the film (a MacBook's 2940 × 1640 around a 16:9
                        // film): black there, grey in the middle, it is still the grey picture.
                        engine.Set("loop-file", "inf"); engine.Set("video-aspect-override", "4:3"); engine.Set("contrast", "-100"); engine.Set("brightness", "50");
                        var repaired = await Until(() => player.Surface is Playback.SoftwareVideoView, 45);
                        var frames = player.Surface?.Frames ?? 0;
                        engine.Set("contrast", "0"); engine.Set("brightness", "0"); engine.Set("video-aspect-override", "-1");
                        var drawing = repaired && await Until(() => player.Surface?.Frames > frames + 12, 10);
                        Check(repaired && drawing && window.Settings.VideoRepair == 3 && !(player.InfoText() ?? "").Contains("OpenGL ·"),
                            "image grise : réparée", $"étape {window.Settings.VideoRepair}, {player.Surface?.GetType().Name}, {player.Surface?.Frames} images");
                        engine.Set("loop-file", "no"); window.Settings.VideoRepair = 0;
                    }
                }
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
                    // Left alone, it tucks itself away against the right edge, a sliver showing; the pointer on it brings it out.
                    var pointerOn = false; player.DockForCheck(() => pointerOn);
                    double Shown() => (player.Parent as Visual)?.Bounds.Width - player.TranslatePoint(new Point(0, 0), (Visual)player.Parent!)?.X ?? 0;
                    var tucked = await Until(() => player.IsMiniTucked && Math.Abs(Shown() - MiniDock.Peek) < 1, 4);
                    ScreenColour(Path.Combine(output, "08f-mini-tucked.png"));
                    Check(tucked && player.IsMini && ReferenceEquals(player.Engine, engine), "mini-lecteur : rangé tout seul contre le bord droit, un bout de la vidéo qui dépasse", $"{Shown():0.#} visibles");
                    pointerOn = true;
                    Check(await Until(() => !player.IsMiniTucked && Math.Abs(Shown() - MiniDock.Width - MiniDock.Gap) < 1, 3), "mini-lecteur : sorti au survol", $"{Shown():0.#} visibles");
                    pointerOn = false;
                    Check(await Until(() => player.IsMiniTucked && Math.Abs(Shown() - MiniDock.Peek) < 1, 3), "mini-lecteur : rangé de nouveau quand le pointeur part", $"{Shown():0.#} visibles");
                    player.DockForCheck(null);
                    Key(window, Avalonia.Input.Key.I);
                    Check(await Until(() => !player.IsMini && player.Bounds.Width > 800, 3), "mini-lecteur : agrandi par I", $"{player.Bounds.Width:0}");
                }
                var pipLeft = await PipChecksAsync(window, player, engine, renderer, output, Check);
                Key(window, Avalonia.Input.Key.Space);
                Check(await Until(() => engine.Flag("pause"), 3), $"pause ({renderer})");
                await window.StopPlaybackAsync();
                Check(window.Player is null, $"retour à la bibliothèque ({renderer})");
                Check(MacNowPlaying.Clock.Told is null && (!OperatingSystem.IsMacOS() || MacNowPlaying.ToldTitle() is null), $"touches média : « Now Playing » vidé à la fermeture ({renderer})", MacNowPlaying.Clock.Told?.Title ?? "");
                if (pipLeft is not null) Check(!pipLeft.IsVisible && player.Pip is null, $"image dans l’image : partie avec la lecture ({renderer})");
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

            // —— A start overtaken before its picture: stopped, or another title asked for, while the view waits for OpenGL ——
            await OvertakenStartsAsync(window, movie, Check);

            // —— « Lecture impossible », as on Windows: its screen, another try in the same player, Échap back to the library ——
            if (mock is not null)
            {
                mock.RefusePlayback = true;
                await window.PlayAsync(movie, fromStart: true);
                var failing = window.Player;
                var failureShown = await Until(() => failing?.FailureShown == true, 15);
                await Task.Delay(500); Capture(window, output, "08h-player-failure");
                var unnamedFailure = Unnamed(window);
                Check(failureShown && unnamedFailure.Count == 0, "lecture impossible : son écran, comme sous Windows", (failing?.FailureText ?? window.NoticeText ?? "lecteur fermé") + (unnamedFailure.Count > 0 ? " — sans nom : " + string.Join(", ", unnamedFailure.Take(4)) : ""));
                mock.RefusePlayback = false;
                failing?.RetryForCheck();
                Check(await Until(() => failing is { FailureShown: false, IsFileLoaded: true } && window.Player == failing, 30), "lecture impossible : « Réessayer » relance le titre", failing?.FailureText ?? window.NoticeText ?? "");
                await window.StopPlaybackAsync();
                mock.RefusePlayback = true;
                await window.PlayAsync(movie, fromStart: true);
                var refused = window.Player;
                var refusedShown = await Until(() => refused?.FailureShown == true, 15);
                Key(window, Avalonia.Input.Key.Escape);
                Check(refusedShown && await Until(() => window.Player is null, 5), "lecture impossible : Échap revient à la bibliothèque", window.Player is null ? window.Current?.GetType().Name ?? "" : "lecteur encore là");
                mock.RefusePlayback = false;
                await window.StopPlaybackAsync();
            }

            // —— Over the network, as on Windows (MainWindow.Power.cs, WatchStall): the Mac asleep, said by macOS or only by its
            // clock, then a connection that stops sending without closing (Jellyfin's PC gone): opened again where it was, then
            // « Lecture impossible » when it stops again at once ——
            if (mock is not null)
            {
                using var streams = StreamServer.TryStart(mock);
                if (streams is null) Check(false, "réseau : le Jellyfin du contrôle sert ses vidéos", $"{StreamServer.Prefix} déjà pris");
                else
                {
                    mock.StreamOverHttp = true;
                    int Opened() { lock (mock.Calls) return mock.Calls.Count(x => x.EndsWith("/PlaybackInfo", StringComparison.Ordinal)); }
                    // What mpv is doing, said when a check fails (the Mac runner differs from Linux).
                    string Mpv() { var m = window.Player?.Engine; return m is null ? "pas de mpv" : string.Join(" ", new[] { "pause", "paused-for-cache", "core-idle", "seeking", "eof-reached", "demuxer-cache-time", "cache-buffering-state", "time-pos" }.Select(x => $"{x}={m.Get(x)}")) + $" ; flux {streams.Streams} : {streams.Log()}"; }
                    await window.PlayAsync(movie, fromStart: true);
                    var streamed = window.Player;
                    var playing = await Until(() => streamed is { IsFileLoaded: true, Position: > 3 }, 30);
                    Check(playing && streamed!.Location?.StartsWith(StreamServer.Prefix, StringComparison.Ordinal) == true, "réseau : le film arrive par une connexion", streamed?.Location ?? window.NoticeText ?? "lecteur fermé");
                    if (playing && streamed?.Engine is not null)
                    {
                        // macOS says it sleeps, then wakes: paused where it was, opened again, still paused.
                        var opened = Opened(); var was = streamed.Position;
                        streamed.SleepForCheck();
                        var pausedBefore = streamed.Engine?.Flag("pause") == true;
                        streams.Hang();
                        await Task.Delay(1500);
                        streamed.WakeForCheck();
                        var back = await Until(() => Opened() > opened && streamed is { IsFileLoaded: true, ReopeningForCheck: false } && streamed.Engine?.Flag("pause") == true, 20);
                        Check(pausedBefore && back && Math.Abs(streamed.Position - was) < 2, "veille du Mac : en pause avant, rouvert au même endroit au réveil, toujours en pause",
                            $"pause avant {pausedBefore}, {Opened() - opened} réouverture, {was:0.0} → {streamed.Position:0.0}, chargé {streamed.IsFileLoaded}, pause {streamed.Engine?.Flag("pause")}");
                        // Asleep without a word from macOS: its clock alone says so.
                        streamed.Engine?.Set("pause", "no");
                        var played = await Until(() => streamed.Position > was + 2, 10); var afterSleep = played ? "" : " — après la veille, pas reparti : " + Mpv();
                        opened = Opened(); was = streamed.Position;
                        streams.Hang();
                        streamed.ClockJumpForCheck(40);
                        back = await Until(() => Opened() > opened && streamed is { IsFileLoaded: true, ReopeningForCheck: false } && streamed.Engine?.Flag("pause") == true, 20);
                        Check(back && Math.Abs(streamed.Position - was) < 2, "veille vue par l’horloge seule : rouvert au même endroit, en pause", $"{Opened() - opened} réouverture, {was:0.0} → {streamed.Position:0.0}{afterSleep}");
                        // The connection stops sending: mpv waits, Mira opens it again where it was after 15 s.
                        streamed.Engine?.Set("pause", "no");
                        var playedOn = await Until(() => streamed.Position > was + 3, 10); var afterClock = playedOn ? "" : " — pas reparti : " + Mpv();
                        opened = Opened();
                        streams.Hang();
                        var stalledAt = 0.0;
                        var waited = await Until(() => { if (streamed.Engine?.Flag("paused-for-cache") == true && stalledAt == 0) stalledAt = streamed.Position; return stalledAt > 0; }, 20);
                        var reopened = await Until(() => Opened() > opened, 30) && await Until(() => streamed is { IsFileLoaded: true, ReopeningForCheck: false } && streamed.Position > stalledAt + 1, 20);
                        Check(waited && reopened && window.Player == streamed && !streamed.FailureShown, "flux bloqué : rouvert là où il était, la lecture continue",
                            $"attente {waited} à {stalledAt:0.0}, {Opened() - opened} réouverture, lu {streamed.Position:0.0}{(streamed.FailureShown ? ", " + streamed.FailureText : "")}{afterClock}{(waited && reopened ? "" : " — " + Mpv())}");
                        // Stuck again before it played on: Jellyfin answers, the video does not come, « Lecture impossible ».
                        await Until(() => streamed.Position > stalledAt + 3, 10);
                        streams.Hang();
                        var failed = await Until(() => streamed.FailureShown, 45);
                        Check(failed && streamed.FailureText == StreamRecovery.StuckText, "flux bloqué de nouveau : « Lecture impossible »", streamed.FailureText ?? window.NoticeText ?? (window.Player is null ? "lecteur fermé" : $"lu {streamed.Position:0.0} — {Mpv()}"));
                    }
                    mock.StreamOverHttp = false;
                    await window.StopPlaybackAsync();
                }
            }

            // —— « Bande-annonce »: on the page when the title has one, and « Bande-annonce avant le film » ——
            if (mock is not null)
            {
                var kept = await window.Session.Client.ItemAsync("movie-3");
                var web = await window.Session.Client.ItemAsync("movie-2");
                foreach (var titled in new[] { kept, web })
                {
                    window.Open(titled);
                    var tip = Trailers.Tip(titled);
                    Check(await Until(() => window.Current is DetailPage page && page.Item.Id == titled.Id && page.ActionButtons().Any(x => x.Name == tip), 15),
                        $"fiche : « Bande-annonce » dit où il mène ({titled.Name})", string.Join(", ", (window.Current as DetailPage)?.ActionButtons().Select(x => x.Name) ?? []));
                    if (titled == kept) { await Task.Delay(600); Capture(window, output, "05g-film-trailer"); }
                }
                window.Open(movie);
                Check(await Until(() => window.Current is DetailPage page && page.Item.Id == movie.Id && page.ActionButtons().Any(), 15)
                    && !(window.Current as DetailPage)!.ActionButtons().Any(x => x.Name.Contains(Trailers.Label, StringComparison.OrdinalIgnoreCase)), "fiche sans bande-annonce : pas de bouton");
                window.Settings.TrailerFirst = true;
                // « Depuis le début » on a film left at 12 s: its trailer, then the film from its first second, never where it was left.
                string FilmStart() => window.Player is { } film ? $"{film.Item?.Name} à {film.Position:0.0} s" : window.NoticeText ?? "lecteur fermé";
                kept.UserData.PlaybackPositionTicks = 12 * TimeSpan.TicksPerSecond;
                await window.PlayAsync(kept, fromStart: true);
                var trailerPlayer = window.Player;
                Check(await Until(() => trailerPlayer is { IsFileLoaded: true, Item.Id: "trailer-3" } && trailerPlayer.SkipPillText == Trailers.SkipLabel, 30),
                    "bande-annonce avant le film : elle passe d’abord, « Passer la bande-annonce » au-dessus", $"{trailerPlayer?.Item?.Name}, « {trailerPlayer?.SkipPillText} »");
                trailerPlayer?.SkipForCheck();
                Check(await Until(() => window.Player is { IsFileLoaded: true, Item.Id: "movie-3" }, 30) && window.Player?.Position < 6, "bande-annonce avant le film : la pastille mène au film, depuis le début", FilmStart());
                await window.StopPlaybackAsync();
                kept.UserData.PlaybackPositionTicks = 12 * TimeSpan.TicksPerSecond;
                await window.PlayAsync(kept, fromStart: true);
                Check(await Until(() => window.Player is { Item.Id: "trailer-3" }, 30) && await Until(() => window.Player is { IsFileLoaded: true, Item.Id: "movie-3" }, 60) && window.Player?.Position < 6,
                    "bande-annonce avant le film : le film suit la fin de la bande-annonce, depuis le début", FilmStart());
                await window.StopPlaybackAsync();
                kept.UserData.PlaybackPositionTicks = 0;
                // A film without one starts at once, its own.
                await window.PlayAsync(movie);
                Check(await Until(() => window.Player is { IsFileLoaded: true }, 30) && window.Player?.Item?.Id == movie.Id && window.Player.SkipPillText != Trailers.SkipLabel, "bande-annonce avant le film : un film sans bande-annonce part tout de suite", window.Player?.Item?.Name ?? "");
                await window.StopPlaybackAsync();
                window.Settings.TrailerFirst = false;
            }

            // —— A film in several files (« cd1 », « cd2 »): its parts one after the other, « Lecture » back in the one left ——
            if (mock is not null)
            {
                var parted = await window.Session.Client.ItemAsync("parts-1");
                var labels = new MovieParts(parted, [new MediaItem { Id = "parts-1-b", Type = "Video" }]);
                int Started(string id) { lock (mock.Reports) return mock.Reports.Count(x => x.Kind == "start" && x.Body.TryGetProperty("ItemId", out var played) && played.GetString() == id); }
                await window.PlayAsync(parted, fromStart: true);
                Check(await Until(() => window.Player is { IsFileLoaded: true, Item.Id: "parts-1" } player && player.HeadingText.Contains(labels.Label("parts-1"), StringComparison.Ordinal), 30),
                    "film en plusieurs fichiers : la première partie d'abord", window.Player?.HeadingText ?? window.NoticeText ?? "lecteur fermé");
                Check(await Until(() => window.Player is { IsFileLoaded: true, Item.Id: "parts-1-b" } player && player.HeadingText.StartsWith("Deux bobines", StringComparison.Ordinal) && player.HeadingText.Contains(labels.Label("parts-1-b"), StringComparison.Ordinal), 60),
                    "film en plusieurs fichiers : la deuxième partie suit, sous le titre du film", window.Player?.HeadingText ?? window.NoticeText ?? "lecteur fermé");
                Check(await Until(() => Started("parts-1-b") > 0, 15) && Started("parts-1") > 0, "film en plusieurs fichiers : chaque partie dite à Jellyfin sous son propre id", $"{Started("parts-1")} et {Started("parts-1-b")} départs");
                Check(await Until(() => window.Player is null, 60), "film en plusieurs fichiers : le lecteur se ferme après la dernière partie", window.Player?.Item?.Id ?? "");
                // Watched to the end of its first part (Jellyfin marks the title watched), the second left at 3 s.
                mock.PartsWatched = true; mock.PartTwoTicks = 3 * TimeSpan.TicksPerSecond;
                await window.PlayAsync(await window.Session.Client.ItemAsync("parts-1"));
                Check(await Until(() => window.Player is { IsFileLoaded: true, Item.Id: "parts-1-b" }, 30) && window.Player!.Position >= 2,
                    "film en plusieurs fichiers : « Lecture » reprend dans la partie laissée en cours", $"{window.Player?.Item?.Id} à {window.Player?.Position:0.0} s");
                await window.StopPlaybackAsync();
                mock.PartsWatched = false; mock.PartTwoTicks = 0;
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
                    // The OpenGL view first, as everywhere: on the runner of mac.yml, without GPU interop, the process crashed
                    // here until the engine of the episode stopped above before its picture was no longer handed to the
                    // software view (OvertakenStartsAsync).
                    await window.PlayAsync(movie);
                    Check(await Until(() => window.Player is { IsFileLoaded: true }, 20) && window.Player?.Location == copy.VideoPath, "garder sur ce Mac : lu depuis ce Mac", window.Player?.Location ?? "lecteur fermé");
                    await window.StopPlaybackAsync();
                    await window.ForgetOfflineAsync([copy]);
                    Check(await Until(() => !File.Exists(copy.VideoPath) && !window.Offers("offline"), 10), "garder sur ce Mac : retiré");
                }
            }

            // —— Session ended on Jellyfin (signed out from its dashboard, password changed) ——
            if (mock is not null)
            {
                window.Go("home"); await Until(() => window.Current is HomePage, 10);
                mock.Expired = true;
                await window.RefreshPagesAsync();
                var ended = await Until(() => window.Login is not null && window.Session is null, 10);
                Check(ended && window.Login!.Server == MockJellyfin.Server && window.Login.User == "Contrôle" && window.Login.Error is { Length: > 0 },
                    "session finie sur Jellyfin : retour à la connexion, serveur et nom remplis", window.Login?.Error ?? window.NoticeText ?? "rien");
                mock.Expired = false;
                await window.ActivateAsync(new Connection(MockJellyfin.Server, MockJellyfin.UserId, "Contrôle", "check-token", window.Profile.DeviceId), mock);
                await Until(() => window.Current is HomePage, 10);
            }

            // —— « Lire sur… » from another device while the window is in the Dock: back as from the Dock icon ——
            if (mock is not null)
            {
                // Refused by Jellyfin: the player's own screen, no film (enough to see the window come back).
                await window.CloseToDockAsync();
                mock.RefusePlayback = true;
                window.Remote(window.Session!, new RemoteCommand(RemoteAction.Play) { ItemId = "movie-1", Start = 0 });
                var asked = await Until(() => window.Player is { FailureShown: true }, 15);
                Check(asked && window.IsVisible && !window.InDock && !window.Away, "télécommande : « Lire sur… » ramène la fenêtre du Dock",
                    $"lecteur {asked}, visible {window.IsVisible}, Dock {window.InDock}");
                mock.RefusePlayback = false;
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

            // —— macOS quitting Mira: shutting down, restarting, logging out (asked last, below: it ends this process) ——
            // On the runner of mac.yml, apart in a Mira of its own (MIRA_CHECK_QUIT): one more film this late in the run
            // crashed the process there (segmentation fault, no report from macOS), as an OpenGL view once did.
            if (mock is not null && quit != "apart") await PrepareQuitAsync(movie);
            exit = results.All(x => x.Ok) ? 0 : 1;
        }
        catch (Exception ex)
        {
            Check(false, "contrôle interrompu", ex.GetType().Name + " : " + ex.Message);
        }
        // A film playing in « Image dans l’image », whose small window refuses a close of its own: both are asked.
        async Task PrepareQuitAsync(MediaItem movie)
        {
            // Each step said as it starts: a crash of the process here leaves only this log.
            static void Step(string what) { Console.WriteLine($"…  quitter à la demande de macOS : {what}"); Console.Out.Flush(); }
            Step("reconnexion");
            if (window.Session is null) await window.ActivateAsync(new Connection(MockJellyfin.Server, MockJellyfin.UserId, "Contrôle", "check-token", window.Profile.DeviceId), mock);
            Environment.SetEnvironmentVariable("MIRA_SOFTWARE_VIDEO", "1");
            Step("lecture");
            await window.PlayAsync(movie, fromStart: true);
            Environment.SetEnvironmentVariable("MIRA_SOFTWARE_VIDEO", null);
            var quitting = window.Player;
            Step("film ouvert");
            Check(await Until(() => quitting is { IsFileLoaded: true, Position: > 2 }, 30), "quitter à la demande de macOS : un film en cours", $"{quitting?.Position:0.0} s");
            Step("image dans l’image");
            if (quitting is not null) await quitting.EnterPipAsync();
            Check(await Until(() => quitting?.Pip is { IsVisible: true }, 10), "quitter à la demande de macOS : dans l’image dans l’image");
        }
        void Write()
        {
            File.WriteAllLines(Path.Combine(output, "results.txt"), results.Select(x => $"{(x.Ok ? "PASS" : "FAIL")}  {x.Name}{(x.Detail.Length > 0 ? " — " + x.Detail : "")}")
                .Prepend($"Mira {Services.Updates.Current} — {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"));
            Console.WriteLine($"{results.Count(x => x.Ok)}/{results.Count} contrôles réussis.");
            Console.Out.Flush();
        }
        void Finish()
        {
            Write();
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
            // Asked only when everything before passed: a failure is read in results.txt as it stands.
            if (exit == 0 && quit != "apart" && QuitAsSystem(window, desktop, mock, Check, Write, () => results.All(x => x.Ok))) return;
            desktop.Shutdown(results.All(x => x.Ok) ? exit : 1);
        }
        Finish();
    }

    /// <summary>
    /// Last: macOS asks Mira to quit, as it does to shut down, restart or log out (<see cref="SystemQuit"/>). Mira must
    /// accept at once, its windows closed (the small one of « Image dans l’image » included), the film paused, where it
    /// was queued for Jellyfin, the settings saved (the volume changed just before). Read as Avalonia ends: on a Mac,
    /// AppKit ends the process right after, before anything else runs. True when the quit went through.
    /// </summary>
    private static bool QuitAsSystem(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop, MockJellyfin? mock, Action<bool, string, string> check, Action write, Func<bool> passed)
    {
        const string Name = "quitter à la demande de macOS";
        var player = window.Player; var engine = player?.Engine;
        engine?.SetLevel(77);
        var settings = Path.Combine(window.Profile.DirectoryPath, "settings.json");
        var reports = 0; if (mock is not null) lock (mock.Reports) reports = mock.Reports.Count;
        var ended = false;
        desktop.Exit += (_, e) =>
        {
            if (ended) return;
            ended = true;
            check(window.QuitAtOnce, $"{Name} : accepté tout de suite (éteindre, redémarrer, fermer la session)", "");
            check(desktop.Windows.Count == 0, $"{Name} : fenêtres fermées, image dans l’image comprise", string.Join(", ", desktop.Windows.Select(x => x.GetType().Name)));
            if (engine is not null) check(engine.Flag("pause"), $"{Name} : le film mis en pause", "");
            if (player is not null && mock is not null && window.Session is { } session)
            {
                bool sent; lock (mock.Reports) sent = mock.Reports.Skip(reports).Any(x => x.Kind == "stop");
                check(session.Sync.PendingCount > 0 || sent, $"{Name} : sa position gardée pour Jellyfin", $"{session.Sync.PendingCount} en attente, envoyé : {sent}");
            }
            string? volume = null;
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(settings));
                volume = json.RootElement.EnumerateObject().FirstOrDefault(x => x.Name.Equals("Volume", StringComparison.OrdinalIgnoreCase)).Value.ToString();
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { volume = ex.Message; }
            check(engine is null || volume == "77", $"{Name} : réglages enregistrés", $"volume {volume}");
            write();
            e.ApplicationExitCode = passed() ? 0 : 1;
        };
        Console.WriteLine($"…  {Name} : [NSApp terminate:]"); Console.Out.Flush();
        if (!SystemQuit.Ask(desktop)) return true;
        check(false, $"{Name} : refusé, macOS arrêterait l’extinction ou le redémarrage", "");
        write();
        return false;
    }

    /// <summary>
    /// A start overtaken while mpv's view waits for its first OpenGL frame (on the runner of mac.yml, without GPU interop,
    /// four seconds before the software view): stopped there, or another title asked for in the same player. The engine
    /// let go meanwhile was once handed to the software view, which gave mpv no handle: the process crashed a film or
    /// two later (SIGSEGV in mpv_render_context_create), and the start overtaken closed the player that played the
    /// newer title, its sound going on with no player to stop it.
    /// </summary>
    private static async Task OvertakenStartsAsync(MainWindow window, MediaItem movie, Action<bool, string, string> check)
    {
        var opening = window.PlayAsync(movie, fromStart: true);
        var first = await Until(() => window.Player is { Engine: not null }, 15) ? window.Player : null;
        await window.StopPlaybackAsync();
        await opening;
        // Past the four seconds the view waits for OpenGL.
        await Task.Delay(5500);
        check(first is not null && window.Player is null && first.Surface is null && first.Engine is null, "lecteur : arrêté avant sa première image",
            $"lecteur {window.Player is not null}, vue {first?.Surface?.GetType().Name ?? "aucune"}, moteur {first?.Engine is not null}");

        opening = window.PlayAsync(movie, fromStart: true);
        var player = await Until(() => window.Player is { Engine: not null }, 15) ? window.Player : null;
        var engine = player?.Engine;
        // From its first second: resumed where the checks before left the film (the slower the runner, the later), it
        // reached the end of its file during the wait below (when it lasted 30 s), and the player rightly closed with it.
        var newer = player?.PlayAgainForCheck(movie with { UserData = movie.UserData with { PlaybackPositionTicks = 0 } });
        await opening;
        if (newer is not null) await newer;
        var playing = await Until(() => window.Player == player && player is { IsFileLoaded: true, Surface.Frames: > 6 } && player.Engine != engine, 30);
        await Task.Delay(5500);
        check(player is not null && playing && window.Player == player && player.IsFileLoaded && !player.IsStopped,
            "lecteur : un autre titre demandé avant la première image du premier, le lecteur le garde",
            $"lecteur {(window.Player == player ? "gardé" : "fermé")}, chargé {player?.IsFileLoaded}, {player?.Surface?.GetType().Name} {player?.Surface?.Frames} images, à {player?.Position:0.0} s");
        await window.StopPlaybackAsync();

        // Closed while the next title opens (the one playing still letting go): nothing plays on behind the library.
        await window.PlayAsync(movie, fromStart: true);
        var closed = await Until(() => window.Player is { IsFileLoaded: true }, 20) ? window.Player : null;
        var following = closed?.PlayAgainForCheck(movie);
        await window.StopPlaybackAsync();
        if (following is not null) await following;
        await Task.Delay(3000);
        check(closed is not null && window.Player is null && closed.Engine is null && !closed.IsFileLoaded,
            "lecteur : fermé pendant que le titre suivant s’ouvre, il reste fermé", $"lecteur {window.Player is not null}, moteur {closed?.Engine is not null}, chargé {closed?.IsFileLoaded}");
    }

    /// <summary>
    /// « Image dans l’image », as on Windows: the video moved into a window of its own above the others, the same engine
    /// playing on and drawing there, the subtitles raised for its size, the library back in Mira's window; then back in
    /// Mira, the whole player drawing again with mpv's own subtitle size; then into it again from the whole player, left
    /// open for the stop that follows. The small window, still open, for the stop to check.
    /// </summary>
    private static async Task<PipWindow?> PipChecksAsync(MainWindow window, PlayerPage player, Playback.MpvPlayer engine, string renderer, string output, Action<bool, string, string> check)
    {
        var subtitles = Path.Combine(output, "pip-subtitles.srt");
        await File.WriteAllTextAsync(subtitles, "1\r\n00:00:00,000 --> 00:00:59,000\r\nMIRA PIP SUBTITLES\r\n");
        engine.TryCommand("sub-add", subtitles, "select");
        await player.EnterPipAsync();
        var pip = player.Pip;
        var surface = player.Surface;
        check(pip is { IsVisible: true, Topmost: true } && surface?.View.Parent == pip.VideoHost && ReferenceEquals(player.Engine, engine) && player.IsFileLoaded && player.IsMini && !player.IsVisible && !window.Watching,
            $"image dans l’image : sa fenêtre au-dessus des autres, la même lecture ({renderer})", $"{surface?.GetType().Name}, fenêtre {pip?.Bounds.Width:0} × {pip?.Bounds.Height:0}{(window.NoticeText is { Length: > 0 } said ? " — " + said : "")}");
        if (pip is null || surface is null) return null;
        var frames = surface.Frames; var position = player.Position;
        check(await Until(() => player.Surface?.Frames > frames + 12 && player.Position > position + 1, 10), $"image dans l’image : la vidéo y est dessinée et avance ({renderer})", $"{player.Surface?.Frames - frames} images, {position:0.0} → {player.Position:0.0}");
        var expected = PictureInPicture.SubtitleScale(window.Settings.SubtitleSize, pip.VideoHeight);
        check(expected > 1 && Math.Abs(engine.Number("sub-scale") - expected) < .01 && engine.Get("sub-visibility") == "yes" && !ConvertedTracks.Off(engine.Get("sid")),
            $"image dans l’image : sous-titres agrandis pour sa taille ({renderer})", $"{pip.VideoHeight:0} de haut, sub-scale {engine.Get("sub-scale")}");
        pip.ShowControls(); await Task.Delay(300);
        var name = renderer == "OpenGL" ? "opengl" : "software";
        ScreenColour(Path.Combine(output, $"08f-pip-{name}.png"));
        var unnamed = Unnamed(pip);
        check(unnamed.Count == 0, $"VoiceOver : tout est nommé (image dans l’image, {renderer})", string.Join(", ", unnamed.Take(8)));
        // While the controls show, the subtitles above the time and the subtitles' button (sub-pos, sub-margin-y at 0); at
        // their place once they go. mpv's own margin, which changed with its versions (22, then 34).
        var usual = (int)engine.Number("option-info/sub-margin-y/default-value", 22); var own = SubtitleStyle.Height(window.Settings.SubtitleHeight);
        double Margin() => engine.Number("sub-margin-y", -1); double Position() => engine.Number("sub-pos", -1);
        var lifted = PictureInPicture.SubtitleLift(pip.Fit.Lift, pip.VideoHeight, own);
        check(lifted is { } at && Math.Abs(Position() - at) < .5 && Margin() == 0, $"image dans l’image : sous-titres au-dessus des commandes tant qu’elles se voient ({renderer})", $"sub-pos {Position()}, attendu {lifted}, sub-margin-y {Margin()}");
        // The smallest window: lighter controls, each row in it, the subtitles still above them.
        if (pip.Placement() is { } placed && PipWindow.ScreensOf(pip) is { Count: > 0 } screens && PictureInPicture.ScreenOf(placed, screens) is { } screen)
        {
            var narrowest = PictureInPicture.Widths(screen, pip.Aspect).Minimum;
            pip.Place(placed with { Width = narrowest, Height = Math.Round(narrowest / pip.Aspect) }, screen);
            await Until(() => pip.Fit.Small, 3); pip.ShowControls(); await Task.Delay(400);
            ScreenColour(Path.Combine(output, $"08g-pip-small-{name}.png"));
            var small = PictureInPicture.SubtitleLift(pip.Fit.Lift, pip.VideoHeight, own);
            check(pip.Fit is { Small: true, Jumps: true } && small is { } there && Math.Abs(Position() - there) < .5 && Margin() == 0, $"image dans l’image : commandes allégées dans la plus petite fenêtre, sous-titres au-dessus ({renderer})",
                $"{pip.Bounds.Width:0} × {pip.Bounds.Height:0}, {pip.Fit}, sub-pos {Position()}, attendu {small}");
            pip.Place(placed, screen);
            await Until(() => !pip.Fit.Small, 3);
        }
        pip.HideControlsNow(); await Task.Delay(200);
        check(Math.Abs(Margin() - usual) < .5 && Math.Abs(Position() - own) < .5, $"image dans l’image : sous-titres à leur place une fois les commandes parties ({renderer})", $"sub-margin-y {Margin()}, sub-pos {Position()}");

        await player.ReturnFromPipAsync();
        frames = player.Surface?.Frames ?? 0;
        check(player.Pip is null && !pip.IsVisible && player.IsVisible && !player.IsMini && player.Surface?.View.Parent is { } host && host != pip.VideoHost && ReferenceEquals(player.Engine, engine)
            && Math.Abs(engine.Number("sub-scale") - 1) < .001 && Math.Abs(engine.Number("sub-margin-y") - engine.Number("option-info/sub-margin-y/default-value", 22)) < .5
            && Math.Abs(engine.Number("sub-pos") - SubtitleStyle.Height(window.Settings.SubtitleHeight)) < .5 && await Until(() => player.Surface?.Frames > frames + 12, 10),
            $"image dans l’image : de retour dans Mira, le lecteur entier ({renderer})", $"{player.Surface?.GetType().Name}, sub-scale {engine.Get("sub-scale")}");
        engine.Set("sid", "no");

        await player.EnterPipAsync();
        check(player.Pip is { IsVisible: true } && !player.IsVisible && window.Player == player, $"image dans l’image : ouverte depuis le lecteur entier ({renderer})", "");
        return player.Pip;
    }

    private static async Task<bool> Until(Func<bool> condition, double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }
    /// <summary>A key pressed <paramref name="presses"/> times before its release: more than once is a key held, the Mac repeating it.</summary>
    private static void Key(MainWindow window, Key key, KeyModifiers modifiers = KeyModifiers.None, int presses = 1)
    {
        var player = window.Player;
        for (var i = 0; i < presses; i++) player?.HandleKey(new KeyEventArgs { Key = key, KeyModifiers = modifiers, RoutedEvent = InputElement.KeyDownEvent });
        player?.HandleKeyUp(new KeyEventArgs { Key = key, KeyModifiers = modifiers, RoutedEvent = InputElement.KeyUpEvent });
    }

    /// <summary>A left click as macOS sends it: pressed and released on what Avalonia hits at <paramref name="at"/> (window coordinates).</summary>
    private static void Click(MainWindow window, Point at)
    {
        if (window.InputHitTest(at) is not Avalonia.Interactivity.Interactive target) return;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var time = (ulong)Environment.TickCount64;
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, at, time, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, window, at, time + 80, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
    }

    /// <summary>A key as macOS sends it: down then up to what has the keyboard focus (else the window), through every handler.</summary>
    private static void Press(MainWindow window, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        foreach (var routed in new[] { InputElement.KeyDownEvent, InputElement.KeyUpEvent })
        {
            var target = window.FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive ?? window;
            target.RaiseEvent(new KeyEventArgs { Key = key, KeyModifiers = modifiers, RoutedEvent = routed, Source = target });
        }
    }

    /// <summary>The window as Avalonia draws it (without mpv's picture, which only reaches the screen).</summary>
    internal static void CaptureWindow(Window window, string output, string name)
    {
        var scaling = window.RenderScaling;
        var size = new PixelSize((int)(window.Bounds.Width * scaling), (int)(window.Bounds.Height * scaling));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        // Avalonia 11.3's RenderTargetBitmap draws the child of a border with a BoxShadow at twice its place and size in a
        // window at ×2 (the screen draws it right: the Filter panel, seen under Xvfb): such shadows are left out of a capture.
        var shadowed = window.GetVisualDescendants().OfType<Border>().Where(x => x.BoxShadow.Count > 0 && x.Child is not null).Select(x => (Border: x, x.BoxShadow)).ToList();
        foreach (var (border, _) in shadowed) border.BoxShadow = default;
        try { bitmap.Render(window); }
        finally { foreach (var (border, shadow) in shadowed) border.BoxShadow = shadow; }
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
            unnamed.Add($"{control.GetType().Name}{(text is not null ? $" « {text} »" : icon is not null ? $" ({icon})" : $" dans {string.Join(" < ", control.GetVisualAncestors().Take(3).Select(x => x.GetType().Name))}")}");
        }
        return unnamed;
    }
    /// <summary>A sheet over the window: Tab from its focused control, 40 times, never leaves it (the page under it is out of reach).</summary>
    private static bool TabStaysIn(MainWindow window)
    {
        if (window.CurrentSheet is not { } sheet || window.FocusManager?.GetFocusedElement() is not IInputElement current) return false;
        for (var i = 0; i < 40; i++)
        {
            if (KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next) is not { } next) return false;
            if (next is not Visual visual || !sheet.IsVisualAncestorOf(visual)) return false;
            current = next;
        }
        return true;
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
