using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>Réglages: the account, playback, languages and subtitles, and the version of Mira and its updates.</summary>
public sealed partial class SettingsPage : Page
{
    public override string Rail => "settings";
    /// <summary>Again while shown: the tab a notice asked for (<see cref="OpenAt"/>), else this one back to its top, nothing filled again.</summary>
    public override void ShowAgain() { if (_lastTab != CurrentTab) Show(_lastTab); else GlideUp.Start(_tabScroll); }
    // Built with the page: the language is set once Mira opens.
    private static (string Label, string Value)[] Audio =>
        [(L("Japonais, puis français", "Japanese, then French"), "jpn,ja,fre,fra,fr,eng,en"), (L("Japonais, puis anglais", "Japanese, then English"), "jpn,ja,eng,en,fre,fra,fr"),
            (L("Français, puis anglais", "French, then English"), "fre,fra,fr,eng,en"), (L("Anglais, puis français", "English, then French"), "eng,en,fre,fra,fr")];
    private static (string Label, string Value)[] Subtitles =>
        [(L("Français, puis anglais", "French, then English"), "fre,fra,fr,eng,en"), (L("Anglais, puis français", "English, then French"), "eng,en,fre,fra,fr"), (L("Anglais", "English"), "eng,en"), (L("Japonais", "Japanese"), "jpn,ja")];

    public SettingsPage(MainWindow shell) : base(shell)
    {
        var settings = shell.Settings;
        StackPanel Tab(string key) => _tabs[key];
        foreach (var (key, _, _, _) in Tabs) _tabs[key] = new StackPanel { Spacing = 10 };

        // —— Lecture: what matters while watching; the picture and the decoding under « Réglages avancés » ——
        Tab("playback").Children.Add(Section(null,
            Choice(L("Reprendre là où tu t’es arrêté", "Resume where you left off"), ResumeChoices, ResumeShown(settings), v =>
            {
                // « Non, depuis le début » turns resuming off; any distance turns it on, that far back.
                settings.RememberPosition = v != ResumeOff;
                if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var back)) settings.ResumeBack = back;
            }, L("Quelques secondes avant, pour retrouver le fil.", "A few seconds earlier, to pick up the thread.")),
            Toggle(L("Enchaîner les épisodes", "Play episodes back to back"), L("L’épisode suivant démarre tout seul à la fin.", "The next episode starts by itself at the end."), settings.AutoNext, v => settings.AutoNext = v),
            Toggle(L("Passer les intros et les récaps", "Skip intros and recaps"), L("Tout seul, quand Jellyfin les a repérés. Sinon, touche S.", "On its own, when Jellyfin has found them. Otherwise, press S."), settings.AutoSkip, v => settings.AutoSkip = v),
            Choice(L("Qualité", "Quality"), [.. StreamQuality.Choices.Select(x => (x.Label, x.Key))], StreamQuality.Known(settings.Quality), v => settings.Quality = v,
                L("« Automatique » s’adapte à ta connexion, chez toi comme ailleurs. Aussi dans le lecteur, sous ⋮.", "“Automatic” adapts to your connection, at home or away. Also in the player, under ⋮."))));
        Tab("playback").Children.Add(Advanced(L("Réglages avancés", "Advanced settings"),
            Choice(L("Image", "Picture"), [.. PictureQuality.MacChoices.Select(x => (x.Label, x.Key))], PictureQuality.Known(settings.Picture), v => { settings.Picture = v; settings.VideoRepair = 0; },
                L("« Maximale » ajoute la mise à l’échelle la plus fine. Si ce Mac ne suit pas, Mira baisse d’un cran jusqu’à ce que tu le quittes.", "“Maximum” adds the finest scaling. If this Mac can’t keep up, Mira steps down a level until you quit it.")),
            Toggle(L("Décodage matériel", "Hardware decoding"), L("La puce du Mac lit la vidéo : plus fluide, moins de chauffe. À couper seulement si l’image saute ou reste noire.", "The Mac’s chip plays the video: smoother, cooler. Turn it off only if the picture stutters or stays black."),
                settings.HardwareDecoding, v => settings.HardwareDecoding = v)));

        // —— Audio & sous-titres: the languages, the subtitles' look; the output under « Sortie audio et ampli » ——
        Tab("audio").Children.Add(Section(null,
            Choice(L("Langue audio préférée", "Preferred audio language"), Audio, settings.AudioLanguage, v => settings.AudioLanguage = v, ""),
            Choice(L("Langue des sous-titres", "Subtitle language"), Subtitles, settings.SubtitleLanguage, v => settings.SubtitleLanguage = v,
                L("Choisies parmi les pistes du fichier. Tu peux en changer pendant la lecture.", "Picked from the file’s tracks. You can change them during playback.")),
            SubtitleStyleCard(settings)));
        // « Sortie audio », as on Windows: the device, its channels, the amplifier, and « Tester ».
        Tab("audio").Children.Add(Advanced(L("Sortie audio et ampli", "Audio output and amplifier"), AudioRows(settings)));

        // —— Apparence: Mira's language and Home's rows ——
        Tab("appearance").Children.Add(Section(null, AppLanguage(settings)));
        // « Accueil à ta main »: the libraries come from Jellyfin, for their « Nouveau dans … ».
        Tab("appearance").Children.Add(_home);
        DrawHome();
        if (shell.Session is { } account) _ = LoadLibrariesAsync(account.Client);

        // —— Compte & serveur: the account and its photo, the way to Jellyfin, who plays what ——
        var connection = shell.Session?.Connection;
        // Read when « Se déconnecter » is clicked, never kept: each sign-out asks anew.
        var erase = new ToggleSwitch { IsChecked = false, OnContent = null, OffContent = null, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(erase, L("Effacer aussi les données de ce compte sur ce Mac", "Also erase this account’s data on this Mac"));
        Tab("server").Children.Add(Section(null, Account(connection, erase),
            Row(L("En te déconnectant, effacer aussi ses données sur ce Mac", "When signing out, also erase its data on this Mac"),
                L("Le cache de ta bibliothèque, les affiches et les progressions pas encore envoyées à Jellyfin. Tes réglages restent.",
                    "Your library’s cache, the posters and any progress not yet sent to Jellyfin. Your settings stay."), erase)));
        // This account's photo, as on Windows: kept by Jellyfin, on every device.
        if (connection is not null) Tab("server").Children.Add(Section(null, PhotoRow()));
        // « Tailscale intégré »: how Mira reaches Jellyfin away from home without the Tailscale app.
        Tailnet = new TailnetSection(shell, Section);
        Tab("server").Children.Add(Tailnet.Section);
        // « En ce moment sur Jellyfin »: who plays what, as on Windows.
        Viewing = new ViewersSection(shell, Section);
        Tab("server").Children.Add(Viewing.Section);

        // —— Mira sur ce Mac: its version and updates, the titles kept here ——
        Tab("updates").Children.Add(Section(null,
            Version(),
            // Switched back on: GitHub is asked now rather than at the next launch, as on Windows.
            Toggle(L("Mettre à jour automatiquement", "Update automatically"), L("Chaque version est vérifiée, puis installée quand tu quittes Mira. Tes données restent en place.", "Each version is checked, then installed when you quit Mira. Your data stays in place."),
                settings.AutoUpdate, v => { var again = v && !settings.AutoUpdate; settings.AutoUpdate = v; if (again) _ = Updates.CheckAsync(Shell); }),
            // « Sur ce Mac »: where the titles kept to watch offline go, and all of them removed at once.
            OfflineCard(),
            Row(L("Projet", "Project"), L("Le code, les nouveautés et les versions pour Windows et Mac.", "The code, what’s new and the versions for Windows and Mac."), Ui.Action(L("Ouvrir GitHub", "Open GitHub"), "globe", "", () => Updates.Open(Updates.Project)))));

        // —— Ton bilan: the month or the year, from Jellyfin's record of what was played to the end ——
        Tab("report").Children.Add(Section(null, Report()));

        // —— Ton serveur: what only its owner sees, in Windows' order ——
        // « Ajouter un ami »: an account of their own, as on Windows, for an administrator.
        Friend = new FriendsSection(shell, Section);
        Tab("sharing").Children.Add(Friend.Section);
        // « Liens d'invités »: an administrator's links for guests without an account (shown once Jellyfin's extension answers).
        Tab("sharing").Children.Add(new GuestLinksSection(shell, Section).Section);
        // Quick Connect: shown once Jellyfin says it lets devices in by a code.
        var devices = Section(L("Autoriser un appareil", "Allow a device"), ApproveDevice());
        devices.IsVisible = false;
        Tab("sharing").Children.Add(devices);
        if (shell.Session is { } session) _ = ShowWhenEnabledAsync(session.Client, devices);
        // « Titres à corriger »: what Jellyfin did not recognize, for an administrator, as on Windows.
        Fix = new FixTitlesSection(shell, Section);
        Tab("care").Children.Add(Fix.Section);
        // « Sous-titres automatiques »: the extension's search and alignment of subtitles, for an administrator.
        Tab("care").Children.Add(new AutoSubtitlesSection(shell, Section).Section);
        Content = Layout(shell);
    }
    /// <summary>« Tailscale intégré », its node and the way to Jellyfin.</summary>
    public TailnetSection Tailnet { get; }
    public FixTitlesSection Fix { get; }
    public FriendsSection Friend { get; }
    public ViewersSection Viewing { get; }
    private void Save() { Shell.Profile.SaveSettings(Shell.Settings); Shell.ChoicesChanged(); }
    private const string ResumeOff = "off";
    /// <summary>« Reprendre là où tu t’es arrêté », one choice as on Windows: how far back, or not at all.</summary>
    private static (string Label, string Value)[] ResumeChoices =>
        [.. PlaybackSession.ResumeBackChoices.Select(x => (x > 0 ? L($"{x} s avant", $"{x} s earlier") : L("Pile au même endroit", "At the exact spot"), x.ToString(CultureInfo.InvariantCulture))),
            (L("Non, depuis le début", "No, from the start"), ResumeOff)];
    /// <summary>The resume choice the saved settings make: off, else a distance the list has (5 s otherwise), as on Windows.</summary>
    private static string ResumeShown(PlayerSettings settings) => !settings.RememberPosition ? ResumeOff
        : (PlaybackSession.ResumeBackChoices.Contains(settings.ResumeBack) ? settings.ResumeBack : 5).ToString(CultureInfo.InvariantCulture);
    /// <summary>The account's card, as Windows': who is signed in to which Jellyfin, its page in the browser, and « Se déconnecter ».</summary>
    private Control Account(Connection? connection, ToggleSwitch erase)
    {
        var name = new TextBlock { Text = connection is null ? "Jellyfin" : connection.UserName + " · Jellyfin", FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, -8) };
        if (connection is not null && Uri.TryCreate(connection.Server, UriKind.Absolute, out var server))
            buttons.Children.Add(Ui.Action(L("Ouvrir Jellyfin dans le navigateur", "Open Jellyfin in the browser"), "globe", "", () => Updates.Open(server, plainHttp: true)));
        buttons.Children.Add(SignOut(erase));
        foreach (var button in buttons.Children) button.Margin = new Thickness(0, 0, 10, 8);
        return Ui.Column(0, name, Description(connection?.Server ?? "—"), buttons);
    }
    /// <summary>This version and « Rechercher », as Windows' card; what the search finds is said by a notice.</summary>
    private Control Version()
    {
        var check = Ui.Action(L("Rechercher", "Search"), "refresh", "", () => _ = Updates.CheckAsync(Shell, asked: true));
        Avalonia.Automation.AutomationProperties.SetName(check, L("Rechercher une mise à jour", "Check for an update"));
        check.VerticalAlignment = VerticalAlignment.Top;
        var dock = new DockPanel();
        DockPanel.SetDock(check, Dock.Right); dock.Children.Add(check);
        var text = Ui.Column(0, new TextBlock { Text = $"Mira {Updates.Current}", FontSize = 18, FontWeight = FontWeight.SemiBold }, Description(L("Mira pour Mac (Apple Silicon).", "Mira for Mac (Apple silicon).")));
        text.Margin = new Thickness(0, 0, 16, 0);
        dock.Children.Add(text);
        return dock;
    }
    /// <summary>
    /// « Langue de Mira »: the Mac's language, English or French. Mira opens in it next time (its texts are written as it
    /// starts, <see cref="Lang"/>); from Mira.app, « Relancer » opens it again at once. Said in the language chosen.
    /// </summary>
    private Control AppLanguage(PlayerSettings settings)
    {
        var mac = Resolve(null, null, SystemLanguage.Preferred()) == French ? "Français" : "English";
        (string, string)[] options = [(L($"Langue du Mac ({mac})", $"Mac’s language ({mac})"), ""), ("English", English), ("Français", French)];
        return Choice(L("Langue", "Language"), options, Known(settings.Language) ?? "", v =>
        {
            settings.Language = v;
            var chosen = Resolve(v, null, SystemLanguage.Preferred());
            if (chosen == Code) return;
            string Said(string french, string english) => chosen == French ? french : english;
            var text = Said("Mira passera en français à sa prochaine ouverture.", "Mira will be in English the next time it opens.");
            // Opened again once this Mira has quit, which saves the settings and closes the session as any quit does.
            if (SystemLanguage.Bundle() is { } bundle) Shell.Notice(text, Said("Relancer", "Relaunch"), () => { if (Updates.RelaunchAfterInstall() || SystemLanguage.RelaunchAfterExit(bundle)) Shell.Close(); }, sticky: false);
            else Shell.Notice(text);
        }, L("Celle de Mira. Elle change à la prochaine ouverture de Mira.", "Mira’s own. It changes the next time Mira opens."));
    }
    private Control Report()
    {
        var time = new TextBlock { FontSize = 40, FontWeight = FontWeight.ExtraBold };
        var counts = new TextBlock { FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 4 };
        var periods = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var year = false; var version = 0;
        void Periods()
        {
            periods.Children.Clear();
            periods.Children.Add(Ui.Action(L("Ce mois-ci", "This month"), null, year ? "" : "chip on", () => { if (year) { year = false; _ = LoadAsync(); } }));
            periods.Children.Add(Ui.Action(L("Cette année", "This year"), null, year ? "chip on" : "", () => { if (!year) { year = true; _ = LoadAsync(); } }));
        }
        async Task LoadAsync()
        {
            var mine = ++version; var period = year ? L("cette année", "this year") : L("ce mois-ci", "this month");
            Periods(); body.Children.Clear(); time.Text = ""; time.IsVisible = false;
            if (Shell.Session is not { } session) return;
            counts.Text = L("Calcul de ton bilan…", "Working out your recap…");
            try
            {
                var from = WatchReport.Start(DateTimeOffset.Now, year);
                var (played, series) = await session.Client.PlayedSinceAsync(from, session.Lifetime);
                if (mine != version) return;
                var report = WatchReport.Build(played, series, from);
                if (report.Empty) { counts.Text = L($"Aucun film ni épisode terminé {period} pour l’instant.", $"No movie or episode finished {period} yet."); return; }
                time.Text = WatchReport.Time(report.Minutes); time.IsVisible = true;
                counts.Text = L($"devant l’écran {period}", $"spent watching {period}") + " · " + string.Join(" · ", new[] { report.Films > 0 ? Count(report.Films, "film", "films", "movie", "movies") : null, report.Episodes > 0 ? Count(report.Episodes, "épisode", "épisodes", "episode", "episodes") : null }.OfType<string>());
                Part(L("SÉRIES LES PLUS REGARDÉES", "MOST WATCHED SHOWS"), report.Series.Select(x => (x.Name, $"{Count(x.Episodes, "épisode", "épisodes", "episode", "episodes")} · {WatchReport.Time(x.Minutes)}")));
                Part(report.Finished.Count > 1 ? L("SÉRIES TERMINÉES", "SHOWS FINISHED") : L("SÉRIE TERMINÉE", "SHOW FINISHED"), report.Finished.Select(x => (x, "")));
                if (report.Genres.Count > 0) Part(L("GENRES PRÉFÉRÉS", "FAVORITE GENRES"), [(string.Join(" · ", report.Genres), "")]);
            }
            catch (Exception ex) when (Errors.Expected(ex)) { if (mine == version) counts.Text = Errors.Friendly(ex); }
        }
        void Part(string title, IEnumerable<(string Name, string Detail)> lines)
        {
            var rows = lines.ToList();
            if (rows.Count == 0) return;
            body.Children.Add(new TextBlock { Text = title, Classes = { "label" }, Margin = new Thickness(0, 14, 0, 4) });
            foreach (var (name, detail) in rows)
            {
                var line = new DockPanel();
                if (detail.Length > 0) { var right = new TextBlock { Text = detail, FontSize = 13.5, Foreground = Ui.Muted }; DockPanel.SetDock(right, Dock.Right); line.Children.Add(right); }
                line.Children.Add(new TextBlock { Text = name, FontSize = 14.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 16, 0) });
                body.Children.Add(line);
            }
        }
        _ = LoadAsync();
        return Ui.Column(12, periods, Ui.Column(2, time, counts), body,
            new TextBlock { Text = L("D’après Jellyfin : chaque film ou épisode vu jusqu’au bout compte une fois, à sa dernière lecture, sur tous tes appareils.", "From Jellyfin: each movie or episode watched to the end counts once, at its last playback, on all your devices."), FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
    }
    private readonly ContentControl _home = new();
    private List<LibrarySection> _libraries = [];
    private async Task LoadLibrariesAsync(JellyfinClient client)
    {
        try { _libraries = await client.LibrariesAsync(); DrawHome(); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    /// <summary>
    /// « Rangées de l’accueil », one card as on Windows: each row of the home page, two arrows to move it and whether it
    /// shows; the control pressed keeps the focus. « Revenir à l’ordre de Mira » under them.
    /// </summary>
    private void DrawHome(string? focus = null)
    {
        var settings = Shell.Settings;
        var rows = HomeLayout.Rows(settings.HomeRows, _libraries);
        Control? focused = null;
        void Apply(string value, string key, string part) { settings.HomeRows = value; Save(); DrawHome(key + part); }
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var index = i;
            var shown = new ToggleSwitch { IsChecked = row.Shown, OnContent = null, OffContent = null, MinWidth = 0, Margin = new Thickness(8, 0, 0, 0) };
            Avalonia.Automation.AutomationProperties.SetName(shown, L($"Afficher « {row.Title} »", $"Show “{row.Title}”"));
            shown.IsCheckedChanged += (_, _) => Apply(HomeLayout.Show(settings.HomeRows, _libraries, row.Key, shown.IsChecked == true), row.Key, "shown");
            var up = Ui.IconButton("up", L($"Monter « {row.Title} »", $"Move “{row.Title}” up"), () => Apply(HomeLayout.Move(settings.HomeRows, _libraries, row.Key, -1), row.Key, index == 1 ? "down" : "up"), "icon", 15);
            var down = Ui.IconButton("down", L($"Descendre « {row.Title} »", $"Move “{row.Title}” down"), () => Apply(HomeLayout.Move(settings.HomeRows, _libraries, row.Key, 1), row.Key, index == rows.Count - 2 ? "up" : "down"), "icon", 15);
            up.Width = up.Height = down.Width = down.Height = 34;
            up.IsEnabled = i > 0; down.IsEnabled = i < rows.Count - 1;
            if (focus == row.Key + "shown") focused = shown; else if (focus == row.Key + "up") focused = up; else if (focus == row.Key + "down") focused = down;
            var tools = Ui.Row(2, up, down, shown); tools.VerticalAlignment = VerticalAlignment.Center;
            var line = new DockPanel();
            DockPanel.SetDock(tools, Dock.Right); line.Children.Add(tools);
            line.Children.Add(Ui.Column(2, new TextBlock { Text = row.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, Description(HomeLayout.Detail(row), 0)));
            list.Children.Add(line);
        }
        var reset = Ui.Action(L("Revenir à l’ordre de Mira", "Back to Mira’s order"), null, "quiet", () => { settings.HomeRows = ""; Save(); DrawHome(); });
        reset.HorizontalAlignment = HorizontalAlignment.Left; reset.Margin = new Thickness(0, 10, 0, 0);
        _home.Content = Card(Ui.Column(0, CardTitle(L("Rangées de l’accueil", "Home rows")), Description(L("Choisis lesquelles s’affichent, et dans quel ordre.", "Choose which ones show, and in what order.")), list, reset));
        focused?.Focus();
    }
    private static async Task ShowWhenEnabledAsync(JellyfinClient client, Control section)
    {
        try { section.IsVisible = await client.QuickConnectEnabledAsync(); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    /// <summary>The code another device shows (Mira web on a phone), approved for this account.</summary>
    private Control ApproveDevice()
    {
        var box = new TextBox { Width = 150, MaxLength = 7, FontSize = 16, Watermark = "000 000" };
        Avalonia.Automation.AutomationProperties.SetName(box, L("Code de l’appareil à autoriser", "Code of the device to allow"));
        var status = new TextBlock { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
        Button? button = null;
        async Task ApproveAsync()
        {
            if (Shell.Session?.Client is not { } client || !button!.IsEnabled) return;
            if (JellyfinClient.QuickConnectCode(box.Text) is not { } code) { status.Text = L("Le code a 6 chiffres.", "The code has 6 digits."); box.Focus(); box.SelectAll(); return; }
            button.IsEnabled = false; status.Text = L("Autorisation…", "Allowing…");
            try
            {
                var approval = await client.ApproveCodeAsync(code);
                status.Text = approval switch
                {
                    CodeApproval.Approved => L("Appareil autorisé : il se connecte à ton compte.", "Device allowed: it’s signing in to your account."),
                    CodeApproval.Unknown => L("Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.", "Unknown or expired code. Check it, or ask the device for a new one."),
                    _ => L("Quick Connect est désactivé sur ce serveur : active-le dans le tableau de bord de Jellyfin (Général).", "Quick Connect is turned off on this server: turn it on in Jellyfin’s dashboard (General)."),
                };
                if (approval == CodeApproval.Approved) box.Text = ""; else { box.Focus(); box.SelectAll(); }
            }
            catch (Exception ex) when (Errors.Expected(ex)) { status.Text = Errors.Friendly(ex); }
            finally { button.IsEnabled = true; }
        }
        button = Ui.Action(L("Autoriser", "Allow"), null, "", () => _ = ApproveAsync());
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = ApproveAsync(); } };
        return Ui.Column(12,
            new TextBlock
            {
                Text = L("Sur ton téléphone, « Se connecter avec un code » affiche 6 chiffres : saisis-les ici pour le connecter à ton compte, sans y taper ton mot de passe. N’autorise qu’un appareil que tu as sous les yeux.",
                    "On your phone, “Sign in with a code” shows 6 digits: type them here to sign it in to your account, without typing your password on it. Only allow a device you can see in front of you."), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap
            },
            Ui.Row(10, box, button), status);
    }
    /// <summary>« Se déconnecter »: one click, as telling Jellyfin can take a few seconds.</summary>
    private Button SignOut(ToggleSwitch erase)
    {
        Button? button = null;
        button = Ui.Action(L("Se déconnecter", "Sign out"), "exit", "", () => { button!.IsEnabled = false; _ = Shell.SignOutAsync(erase.IsChecked == true); });
        return button;
    }

    /// <summary>« Titres gardés sur ce Mac », one card as on Windows: what is kept, the folder for new ones, and « Tout retirer ».</summary>
    private Control OfflineCard()
    {
        var folder = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var kept = Description("");
        var choose = Ui.Action(L("Changer", "Change"), null, "", () => _ = ChooseOfflineFolderAsync());
        Avalonia.Automation.AutomationProperties.SetName(choose, L("Choisir le dossier des titres gardés sur ce Mac", "Choose the folder for titles kept on this Mac"));
        var clear = Ui.Action(L("Tout retirer de ce Mac", "Remove all from this Mac"), "trash", "quiet", Shell.AskForgetAllOffline);
        Avalonia.Automation.AutomationProperties.SetName(clear, L("Retirer de ce Mac tous les titres gardés", "Remove every title kept on this Mac"));
        clear.HorizontalAlignment = HorizontalAlignment.Left; clear.Margin = new Thickness(0, 14, 0, 0);
        void Show()
        {
            folder.Text = Shell.OfflineFolder;
            var done = Shell.OfflineCopiesKept.Where(x => x.Complete).ToList();
            kept.Text = L("Sur la fiche d’un film ou d’une série, « Garder sur ce Mac » garde son fichier ici, pour le regarder sans connexion.", "On a movie’s or a show’s page, “Keep on this Mac” keeps its file here, to watch it offline.")
                + (done.Count > 0 ? L($" Ici : {Lang.Count(done.Count, "titre", "titres", "", "")}, {OfflineCopies.SizeText(done.Sum(x => x.Size))}.", $" Here: {Lang.Count(done.Count, "", "", "title", "titles")}, {OfflineCopies.SizeText(done.Sum(x => x.Size))}.") : "");
            kept.IsVisible = true; clear.IsVisible = Shell.OfflineCopiesKept.Count > 0;
        }
        Show();
        AttachedToVisualTree += (_, _) => Shell.OfflineUpdated += Show;
        DetachedFromVisualTree += (_, _) => Shell.OfflineUpdated -= Show;
        var where = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        choose.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(choose, Dock.Right); where.Children.Add(choose);
        where.Children.Add(Ui.Column(4, new TextBlock { Text = L("Dossier des nouveaux titres gardés", "Folder for newly kept titles"), FontSize = 12, Foreground = Ui.Faint }, folder));
        return Ui.Column(0, CardTitle(L("Titres gardés sur ce Mac", "Titles kept on this Mac")), kept, where, clear);
    }
    private async Task ChooseOfflineFolderAsync()
    {
        var start = Directory.Exists(Shell.OfflineFolder) ? await Shell.StorageProvider.TryGetFolderFromPathAsync(new Uri(Shell.OfflineFolder)) : null;
        var folders = await Shell.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = L("Dossier des titres gardés sur ce Mac", "Folder for titles kept on this Mac"), AllowMultiple = false, SuggestedStartLocation = start });
        if (folders.Count > 0 && Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders[0]) is { } path) Shell.ChooseOfflineFolder(path);
    }
    /// <summary>A setting's card, as Windows' <c>SettingRow</c>: the surface, rounded by 7, 22 by 21 inside.</summary>
    private static Border Card(Control child) => new() { Background = Ui.Brush("#111112"), CornerRadius = new CornerRadius(7), Padding = new Thickness(22, 21), Child = child };
    /// <summary>A card's title, as Windows': the window's size, semibold.</summary>
    private static TextBlock CardTitle(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    /// <summary>What a setting does, as Windows' <c>SettingDescription</c>: one quiet line under its title.</summary>
    private static TextBlock Description(string text, double top = 7) => new() { Text = text, FontSize = 12, LineHeight = 19, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 30, 0), IsVisible = text.Length > 0 };
    /// <summary>
    /// Settings, each on its card, 10 apart as on Windows. A title over a single card goes inside it, at its top (« Titres
    /// à corriger », « Ajouter un ami »); over several, it labels them.
    /// </summary>
    private static Control Section(string? title, params Control[] rows)
    {
        var list = new StackPanel { Spacing = 10 };
        if (title is not null && rows.Length == 1) { list.Children.Add(Card(Ui.Column(14, CardTitle(title), rows[0]))); return list; }
        if (title is not null) list.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(4, 14, 0, 0) });
        foreach (var row in rows) list.Children.Add(Card(row));
        return list;
    }
    private readonly List<Action<bool>> _folds = [];
    /// <summary>For the self-check: every « Réglages avancés » unfolded, for its captures.</summary>
    public void UnfoldAdvanced() { foreach (var fold in _folds) fold(true); }
    /// <summary>
    /// « Réglages avancés », as Windows' expander: folded at first, a chevron and its words; its settings on their cards
    /// under it once unfolded.
    /// </summary>
    private Control Advanced(string title, params Control[] rows)
    {
        var inside = new StackPanel { Spacing = 10, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var row in rows) inside.Children.Add(Card(row));
        var chevron = new Icon("next", 14) { VerticalAlignment = VerticalAlignment.Center };
        var header = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { chevron, new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center } } },
            Classes = { "quiet" }, Foreground = Ui.TextTertiary, Padding = new Thickness(6, 6), Margin = new Thickness(-4, 8, 0, 4), HorizontalAlignment = HorizontalAlignment.Left,
        };
        Avalonia.Automation.AutomationProperties.SetName(header, title);
        void Fold(bool open)
        {
            inside.IsVisible = open; chevron.Kind = open ? "down" : "next";
            Avalonia.Automation.AutomationProperties.SetHelpText(header, open ? L("Déplié", "Expanded") : L("Replié", "Collapsed"));
        }
        header.Click += (_, _) => Fold(!inside.IsVisible);
        Fold(false); _folds.Add(Fold);
        return Ui.Column(0, header, inside);
    }
    /// <summary>The photo, « Choisir une photo… » and « Retirer »; what Jellyfin answered said below them.</summary>
    private Control PhotoRow()
    {
        var picture = new Avalonia.Controls.Shapes.Ellipse { Width = 64, Height = 64 };
        var letter = new TextBlock { Text = Shell.Session?.Connection.UserName is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "?", FontSize = 24, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var face = new Grid { Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Top, Children = { new Border { CornerRadius = new CornerRadius(32), Background = Ui.Brush("#3D3D43") }, letter, picture } };
        var status = new TextBlock { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Button? choose = null, remove = null;
        async Task Run(Func<Task<string?>> change)
        {
            choose!.IsEnabled = remove!.IsEnabled = false; status.Text = L("Envoi à Jellyfin…", "Sending to Jellyfin…"); status.IsVisible = true;
            var said = await change();
            choose.IsEnabled = remove.IsEnabled = true; status.Text = said ?? ""; status.IsVisible = said is not null;
        }
        choose = Ui.Action(L("Choisir une photo…", "Choose a photo…"), null, "", () => _ = Run(Shell.ChoosePhotoAsync));
        remove = Ui.Action(L("Retirer", "Remove"), null, "quiet", () => _ = Run(Shell.RemovePhotoAsync));
        Avalonia.Automation.AutomationProperties.SetName(remove, L("Retirer ta photo", "Remove your photo"));
        void Show()
        {
            picture.Fill = Shell.Photo is { } photo ? new ImageBrush(photo) { Stretch = Stretch.UniformToFill } : null;
            picture.IsVisible = remove.IsVisible = Shell.Photo is not null;
        }
        Shell.PhotoChanged += Show;
        DetachedFromVisualTree += (_, _) => Shell.PhotoChanged -= Show;
        Show();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 0), Children = { choose, remove } };
        var text = Ui.Column(0, CardTitle(L("Ta photo", "Your photo")),
            Description(L("Gardée sur ton Jellyfin : elle te suit sur ton téléphone, ton PC et dans Jellyfin. Mira la recadre au carré.", "Kept on your Jellyfin: it follows you on your phone, your PC and in Jellyfin. Mira crops it square.")),
            buttons, status);
        var dock = new DockPanel();
        DockPanel.SetDock(face, Dock.Left); dock.Children.Add(face); dock.Children.Add(text);
        return dock;
    }
    private static Control Row(string title, string detail, Control? action)
    {
        var dock = new DockPanel();
        if (action is not null) { action.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(action, Dock.Right); dock.Children.Add(action); }
        var text = Ui.Column(0, CardTitle(title), Description(detail));
        text.Margin = new Thickness(0, 0, 20, 0); text.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(text);
        return dock;
    }
    private Control Toggle(string title, string detail, bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = null, OffContent = null, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(toggle, title);
        toggle.IsCheckedChanged += (_, _) => { set(toggle.IsChecked == true); Save(); };
        return Row(title, detail, toggle);
    }
    /// <summary>
    /// « Taille et style des sous-titres », as on Windows (<see cref="SubtitleStyle"/>): a preview over a bright scene,
    /// where a white subtitle is hardest to read, then each setting, saved as soon as it changes.
    /// </summary>
    private Control SubtitleStyleCard(PlayerSettings settings)
    {
        var text = new TextBlock { Text = L("Encore un épisode ?", "One more episode?"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var box = new Border { Child = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        var outline = new DropShadowEffect { Color = Colors.Black, OffsetX = 0, OffsetY = 1, BlurRadius = 4, Opacity = 1 };
        var scene = new Border
        {
            Height = 165, CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = box,
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#DCE3EA"), 0), new GradientStop(Color.Parse("#A9B4C0"), .62), new GradientStop(Color.Parse("#545B64"), 1) } },
        };
        void Preview()
        {
            var size = settings.SubtitleSize * .55; var dark = settings.SubtitleBackground;
            text.FontSize = size; text.Foreground = Ui.Brush(SubtitleStyle.Color(settings.SubtitleColor));
            text.FontWeight = settings.SubtitleBold ? FontWeight.Bold : FontWeight.SemiBold;
            text.Effect = dark ? null : outline;
            box.Background = dark ? Ui.Brush(SubtitleStyle.Box) : null;
            box.Padding = dark ? new Thickness(size * .3, size * .08, size * .3, size * .12) : default;
            box.Margin = new Thickness(16, 0, 16, 22 + (100 - SubtitleStyle.Height(settings.SubtitleHeight)) / 100.0 * 165);
        }
        var size = new Slider { Minimum = 24, Maximum = 64, Value = settings.SubtitleSize, TickFrequency = 2, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 6) };
        var sizeLabel = new TextBlock { Text = $"{settings.SubtitleSize}", FontSize = 14, Foreground = Ui.Brush("#E0E0E0"), VerticalAlignment = VerticalAlignment.Center };
        size.ValueChanged += (_, e) => { settings.SubtitleSize = (int)e.NewValue; sizeLabel.Text = $"{settings.SubtitleSize}"; Save(); Preview(); };
        Avalonia.Automation.AutomationProperties.SetName(size, L("Taille des sous-titres", "Subtitle size"));
        Preview();
        var head = new DockPanel();
        DockPanel.SetDock(sizeLabel, Dock.Right); head.Children.Add(sizeLabel); head.Children.Add(CardTitle(L("Taille et style des sous-titres", "Subtitle size and style")));
        Control Labelled(string label, Control control) => Ui.Column(8, new TextBlock { Text = label, FontSize = 14, Foreground = Ui.Brush("#E0E0E0") }, control);
        var color = Combo(L("Couleur des sous-titres", "Subtitle color"), [(L("Blanc", "White"), SubtitleStyle.White), (L("Jaune", "Yellow"), SubtitleStyle.Yellow)],
            settings.SubtitleColor == SubtitleStyle.Yellow ? SubtitleStyle.Yellow : SubtitleStyle.White, v => { settings.SubtitleColor = v; Preview(); });
        var height = Combo(L("Hauteur des sous-titres à l’écran", "Subtitle height on screen"), [.. SubtitleStyle.Heights.Select(x => (x.Label, x.Position.ToString(CultureInfo.InvariantCulture)))],
            SubtitleStyle.Height(settings.SubtitleHeight).ToString(CultureInfo.InvariantCulture), v => { settings.SubtitleHeight = int.Parse(v, CultureInfo.InvariantCulture); Preview(); });
        color.HorizontalAlignment = height.HorizontalAlignment = HorizontalAlignment.Stretch; color.MinWidth = height.MinWidth = 0;
        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*"), Margin = new Thickness(0, 18, 0, 0) };
        var left = Labelled(L("Couleur", "Color"), color); var right = Labelled(L("Hauteur à l’écran", "Height on screen"), height);
        Grid.SetColumn(right, 2); choices.Children.Add(left); choices.Children.Add(right);
        var dark = Toggle(L("Fond sombre", "Dark background"), L("Un bandeau derrière le texte, lisible même sur une scène claire.", "A band behind the text, readable even on a bright scene."), settings.SubtitleBackground, v => { settings.SubtitleBackground = v; Preview(); });
        dark.Margin = new Thickness(0, 18, 0, 0);
        var bold = Toggle(L("Texte en gras", "Bold text"), "", settings.SubtitleBold, v => { settings.SubtitleBold = v; Preview(); });
        bold.Margin = new Thickness(0, 14, 0, 0);
        return Ui.Column(0, head, size, scene, choices, dark, bold,
            Description(L("Aperçu indicatif. Les sous-titres stylés des animés (ASS) gardent leur look : seule la hauteur s’y applique.", "Approximate preview. Anime’s styled subtitles (ASS) keep their look: only the height applies to them."), 16));
    }
    /// <summary>A list to choose from, saved as soon as it changes; a value it does not have shown as « Personnalisé ».</summary>
    private ComboBox Combo(string name, (string Label, string Value)[] options, string current, Action<string> set)
    {
        var box = new ComboBox { MinWidth = 210 };
        foreach (var (label, value) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        if (options.All(x => x.Value != current)) box.Items.Add(new ComboBoxItem { Content = L("Personnalisé : ", "Custom: ") + current, Tag = current });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(x => (string)x.Tag! == current);
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string value }) { set(value); Save(); } };
        return box;
    }
    private Control Choice(string title, (string Label, string Value)[] options, string current, Action<string> set, string detail) => Row(title, detail, Combo(title, options, current, set));
}
