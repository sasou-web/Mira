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
            if (mock is not null) Check(home.Continue.Any(x => x.Id == "movie-1"), "accueil : continuer à regarder", string.Join(", ", home.Continue.Select(x => x.Name)));
            await Task.Delay(1500); Capture(window, output, "02-home");
            home.ScrollToEnd(); await Task.Delay(800); Capture(window, output, "02b-home-grid"); home.ScrollToHome();
            // « Nouveautés », as on Windows: after an update from 0.9.0, this version's points and a line for each one skipped;
            // Escape closes it and gives the keys back to the page.
            var missed = WhatsNew.Since(new Version(0, 9, 0), Updates.Current);
            window.ShowWhatsNew(missed);
            var news = await Until(() => window.CurrentSheet is { IsVisible: true }, 5);
            await Task.Delay(500); Capture(window, output, "02c-whats-new");
            window.RaiseEvent(new KeyEventArgs { Key = Avalonia.Input.Key.Escape, RoutedEvent = InputElement.KeyDownEvent, Source = window });
            Check(news && await Until(() => window.CurrentSheet is null, 3) && window.Current is HomePage, "nouveautés : montrées après une mise à jour, fermées par Échap",
                missed.Count > 0 ? string.Join(", ", missed.Select(x => x.Version)) : "aucune version");
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

            // —— A friend's party, on another Jellyfin ——
            // Its link opened here: the first name asked, then this Mac its guest (no account there), in the host's group;
            // left, this Mac's own account back. A link taken back since says the party is over.
            if (mock is not null)
            {
                var link = $"mira://party/{MockJellyfin.PartyCode}?server={Uri.EscapeDataString(MockJellyfin.FriendServer)}";
                window.OpenLink(link);
                Check(await Until(() => window.Party is { Parent: not null } party && party.Heading == Together.PartyAsk("Dafty"), 10), "party d’un ami : prénom demandé, hôte nommé", window.Party?.Heading ?? "pas de carte");
                await Task.Delay(800); Capture(window, output, "02c-party-name");
                window.Party!.FirstName = "Léa"; window.Party.ClickPrimary();
                Check(await Until(() => window.InParty && window.Session is { } guest && Together.IsGuest(guest.Connection) && guest.Connection.UserName == "Léa", 10), "party d’un ami : invité sans compte", window.Party?.Error ?? window.Session?.Connection.Server ?? "");
                Check(window.Settings.PartyName == "Léa", "party d’un ami : prénom gardé pour la suivante", window.Settings.PartyName);
                bool Called(string call) { lock (mock.FriendCalls) return mock.FriendCalls.Contains(call); }
                Check(await Until(() => Called("POST Mira/guest/SyncPlay/Join"), 25), "party d’un ami : groupe de l’hôte rejoint", string.Join(", ", mock.FriendCalls.TakeLast(6)));
                // Jellyfin's answer, which its WebSocket would bring.
                using (var joined = System.Text.Json.JsonDocument.Parse("""{"Type":"GroupJoined","Data":{"GroupId":"party-group","GroupName":"Dafty's group","Participants":["Dafty","Léa"],"State":"Idle"}}"""))
                    window.Group.Message(window.Session!, "SyncPlayGroupUpdate", joined.RootElement.Clone());
                Check(await Until(() => window.Party?.Heading == Together.PartyIn("Dafty") && window.Party.Members.Contains("Léa"), 10), "party d’un ami : dans le groupe, ses membres", $"{window.Party?.Heading} — {window.Party?.Members}");
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
                film.ShowCastForCheck(); await Task.Delay(800); Capture(window, output, "05b-film-cast");
            }
            // —— The menu bar, as a Mac app's (MainWindow.Menu.cs): its items, their keys, and each one doing what its keys do ——
            var bar = window.MenuBar();
            var command = window.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Meta;
            KeyGesture? KeysOf(string name) => bar.FirstOrDefault(x => x.Item == name).Keys;
            Check(bar.Select(x => x.Menu).Distinct().SequenceEqual([Lang.L("Édition", "Edit"), Lang.L("Présentation", "View"), Lang.L("Aller", "Go"), Lang.L("Fenêtre", "Window")])
                && Equals(KeysOf(Lang.L("Passer en mode plein écran", "Enter Full Screen")), new KeyGesture(Avalonia.Input.Key.F, KeyModifiers.Control | KeyModifiers.Meta)) && Equals(KeysOf(Lang.L("Placer dans le Dock", "Minimize")), new KeyGesture(Avalonia.Input.Key.M, command))
                && Equals(KeysOf(Lang.L("Fermer la fenêtre", "Close Window")), new KeyGesture(Avalonia.Input.Key.W, command)) && Equals(KeysOf(Lang.L("Précédent", "Back")), new KeyGesture(Avalonia.Input.Key.OemOpenBrackets, command))
                && KeysOf(Lang.L("Copier", "Copy")) is not null && KeysOf(Lang.L("Coller", "Paste")) is not null,
                "barre des menus : Édition, Présentation, Aller, Fenêtre et leurs raccourcis", string.Join(", ", bar.Select(x => x.Keys is { } k ? $"{x.Item} {k}" : x.Item)));
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
            if (mock is not null && window.Current is DetailPage still)
            {
                // A click on Favori or Vu moves nothing on the page, as on Windows: the film picked up halfway, whose « Vu »
                // took away « Depuis le début » and the progress line, measured from the click to well after Jellyfin's answer.
                var moved = new List<string>();
                foreach (var kind in new[] { "favorite", "watched", "watched", "favorite" })
                {
                    window.HideNotice(); await Task.Delay(300);
                    var before = still.TopPlaces();
                    still.ClickForCheck(kind);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 1200)
                    {
                        await Task.Delay(16);
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
            }
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
            window.Go("settings");
            await Task.Delay(800); Capture(window, output, "07-settings");
            // The categories, as on Windows: those of the server's account hold its sections (friends and guests, maintenance apart).
            (window.Current as SettingsPage)?.Show("server");
            // « Titres à corriger », as on Windows: the mock's titles have no ids from a metadata source.
            if (mock is not null)
                Check(await Until(() => window.Current is SettingsPage { Fix.Count: > 0, Fix.Section.IsVisible: true }, 10), "réglages : titres à corriger", $"{(window.Current as SettingsPage)?.Fix.Count} titres");
            if (mock is not null)
                Check(await Until(() => window.Current is SettingsPage { Viewing.Count: > 0 }, 10), "réglages : en ce moment sur Jellyfin", $"{(window.Current as SettingsPage)?.Viewing.Count} lectures");
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
                foreach (var (tab, shot) in new[] { ("server", "07e-settings-server"), ("updates", "07f-settings-updates"), ("appearance", "07g-settings-appearance"), ("audio", "07h-settings-subtitles") })
                { others.Show(tab); await Task.Delay(500); Capture(window, output, shot); }
            // Confidentialité: every connection, and the update check's switch the same as the one of « Mira sur ce Mac ».
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
                Check(info.Split('\n').Length == (player.Surface is Playback.VideoView { LastLook.Length: > 0 } ? 7 : 6) && info.Contains("OpenGL ·") == player.Surface is Playback.VideoView && PictureQuality.Choices.Any(x => info.Contains(PictureQuality.Label(x.Key))), $"informations de lecture ({renderer})", info.Replace('\n', ' '));
                var audio = engine.Get("current-ao");
                if (string.IsNullOrEmpty(audio)) Check(true, $"volume à 150 % ({renderer})", "pas de sortie audio sur cette machine : non mesurable");
                else Check((engine.Get("af") ?? "").Contains(VolumeBoost.Label) && Math.Abs(engine.Number("volume") - 100) < 0.5, $"volume à 150 % ({renderer})", $"{player.Volume}, sortie {audio}, af {engine.Get("af")}");
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
                    Key(window, Avalonia.Input.Key.I);
                    Check(await Until(() => !player.IsMini && player.Bounds.Width > 800, 3), "mini-lecteur : agrandi par I", $"{player.Bounds.Width:0}");
                }
                var pipLeft = await PipChecksAsync(window, player, engine, renderer, output, Check);
                Key(window, Avalonia.Input.Key.Space);
                Check(await Until(() => engine.Flag("pause"), 3), $"pause ({renderer})");
                await window.StopPlaybackAsync();
                Check(window.Player is null, $"retour à la bibliothèque ({renderer})");
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

    /// <summary>A key as macOS sends it: down then up to what has the keyboard focus (else the window), through every handler.</summary>
    private static void Press(MainWindow window, Key key)
    {
        foreach (var routed in new[] { InputElement.KeyDownEvent, InputElement.KeyUpEvent })
        {
            var target = window.FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive ?? window;
            target.RaiseEvent(new KeyEventArgs { Key = key, RoutedEvent = routed, Source = target });
        }
    }

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
