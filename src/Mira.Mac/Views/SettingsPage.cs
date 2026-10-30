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
public sealed class SettingsPage : Page
{
    public override string Rail => "settings";
    // Built with the page: the language is set once Mira opens.
    private static (string Label, string Value)[] Audio =>
        [(L("Japonais, puis français", "Japanese, then French"), "jpn,ja,fre,fra,fr,eng,en"), (L("Japonais, puis anglais", "Japanese, then English"), "jpn,ja,eng,en,fre,fra,fr"),
            (L("Français, puis anglais", "French, then English"), "fre,fra,fr,eng,en"), (L("Anglais, puis français", "English, then French"), "eng,en,fre,fra,fr")];
    private static (string Label, string Value)[] Subtitles =>
        [(L("Français, puis anglais", "French, then English"), "fre,fra,fr,eng,en"), (L("Anglais, puis français", "English, then French"), "eng,en,fre,fra,fr"), (L("Anglais", "English"), "eng,en"), (L("Japonais", "Japanese"), "jpn,ja")];

    public SettingsPage(MainWindow shell) : base(shell)
    {
        var settings = shell.Settings;
        var page = new StackPanel { Spacing = 34, Margin = new Thickness(56, 44 + shell.TitleBarInset, 56, 60), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Ui.Heading(L("Réglages", "Settings"), 32, 1, FontWeight.SemiBold));

        var connection = shell.Session?.Connection;
        // Read when « Se déconnecter » is clicked, never kept: each sign-out asks anew.
        var erase = new ToggleSwitch { IsChecked = false, OnContent = null, OffContent = null, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(erase, L("Effacer aussi les données de ce compte sur ce Mac", "Also erase this account’s data on this Mac"));
        page.Children.Add(Section(L("Compte", "Account"),
            Row(L("Serveur Jellyfin", "Jellyfin server"), connection?.Server ?? "—", null),
            Row(L("Utilisateur", "User"), connection?.UserName ?? "—", SignOut(erase)),
            Row(L("Effacer aussi les données de ce compte sur ce Mac", "Also erase this account’s data on this Mac"),
                L("En te déconnectant : le cache de ta bibliothèque, les affiches et les progressions pas encore envoyées à Jellyfin. Tes réglages restent.",
                    "When you sign out: your library’s cache, the posters and any progress not yet sent to Jellyfin. Your settings stay."), erase)));
        // « Ton bilan »: the month or the year, from Jellyfin's record of what was played to the end.
        page.Children.Add(Section(L("Ton bilan", "Your recap"), Report()));
        // Quick Connect: shown once Jellyfin says it lets devices in by a code.
        var devices = Section(L("Autoriser un appareil", "Allow a device"), ApproveDevice());
        devices.IsVisible = false;
        page.Children.Add(devices);
        if (shell.Session is { } session) _ = ShowWhenEnabledAsync(session.Client, devices);
        // « Liens d'invités »: an administrator's links for guests without an account (shown once Jellyfin's extension answers).
        page.Children.Add(new GuestLinksSection(shell, Section).Section);
        // « Sous-titres automatiques »: the extension's search and alignment of subtitles, for an administrator.
        page.Children.Add(new AutoSubtitlesSection(shell, Section).Section);

        page.Children.Add(Section(L("Lecture", "Playback"),
            Toggle(L("Reprendre là où tu t’es arrêté", "Resume where you left off"), L("Sinon, chaque titre repart du début.", "Otherwise, every title starts from the beginning."), settings.RememberPosition, v => settings.RememberPosition = v),
            Choice(L("Reprendre un peu avant", "Resume a little earlier"), PlaybackSession.ResumeBackChoices.Select(x => (x > 0 ? L($"{x} s avant", $"{x} s earlier") : L("Non", "No"), x.ToString(CultureInfo.InvariantCulture))).ToArray(),
                settings.ResumeBack.ToString(CultureInfo.InvariantCulture), v => settings.ResumeBack = int.Parse(v, CultureInfo.InvariantCulture),
                L("Pour retrouver le fil plutôt que de tomber au milieu d’une phrase. Un titre vu moins de 30 s reprend pile là où tu t’es arrêté.",
                    "To pick up the thread rather than land in the middle of a sentence. A title watched for less than 30 s resumes exactly where you left off.")),
            Choice(L("Qualité", "Quality"), [.. StreamQuality.Choices.Select(x => (x.Label, x.Key))], StreamQuality.Known(settings.Quality), v => settings.Quality = v,
                L("« Automatique » lit le fichier tel quel quand Jellyfin est sur ce Mac ou à la maison, et ailleurs quand ta connexion le tient (mesurée une fois par heure) ; sinon Jellyfin le convertit à ce qu’elle laisse passer. Un débit fixe fait convertir tout fichier plus lourd. Aussi dans le lecteur.",
                    "“Automatic” plays the file as it is when Jellyfin is on this Mac or at home, and elsewhere when your connection can handle it (measured once an hour); otherwise Jellyfin converts it to what the connection lets through. A fixed bitrate has any heavier file converted. Also in the player.")),
            Toggle(L("Épisode suivant automatique", "Play the next episode automatically"), L("À la fin d’un épisode, le suivant démarre.", "When an episode ends, the next one starts."), settings.AutoNext, v => settings.AutoNext = v),
            Toggle(L("Passer les intros et les récaps", "Skip intros and recaps"), L("Sans appuyer sur S, quand Jellyfin ou les chapitres du fichier les ont repérés. Revenir en arrière les rejoue.",
                "Without pressing S, when Jellyfin or the file’s chapters found them. Going back plays them again."), settings.AutoSkip, v => settings.AutoSkip = v),
            Toggle(L("Décodage matériel", "Hardware decoding"), L("Utilise la puce du Mac pour décoder la vidéo : moins de chauffe, plus d’autonomie.", "Uses the Mac’s chip to decode the video: less heat, longer battery life."), settings.HardwareDecoding, v => settings.HardwareDecoding = v)));

        // « Accueil à ta main »: the libraries come from Jellyfin, for their « Nouveau dans … ».
        page.Children.Add(_home);
        DrawHome();
        if (shell.Session is { } account) _ = LoadLibrariesAsync(account.Client);

        page.Children.Add(Section(L("Langues", "Languages"),
            AppLanguage(settings),
            Choice(L("Audio", "Audio"), Audio, settings.AudioLanguage, v => settings.AudioLanguage = v),
            Choice(L("Sous-titres", "Subtitles"), Subtitles, settings.SubtitleLanguage, v => settings.SubtitleLanguage = v)));

        page.Children.Add(Section(L("Sous-titres", "Subtitles"), SubtitleRows(settings)));

        page.Children.Add(Section("Mira",
            Row($"Version {Updates.Current}", L("Mira pour Mac (Apple Silicon).", "Mira for Mac (Apple silicon)."), Ui.Action(L("Rechercher une mise à jour", "Check for updates"), "download", "", () => _ = Updates.CheckAsync(Shell, asked: true))),
            // Switched back on: GitHub is asked now rather than at the next launch, as on Windows.
            Toggle(L("Rechercher les nouvelles versions", "Look for new versions"), L("À chaque ouverture, Mira demande à GitHub s’il existe une nouvelle version. Sinon, seulement quand tu cliques sur « Rechercher une mise à jour ».",
                    "Each time it opens, Mira asks GitHub whether there’s a new version. Otherwise, only when you click “Check for updates”."),
                settings.AutoUpdate, v => { var again = v && !settings.AutoUpdate; settings.AutoUpdate = v; if (again) _ = Updates.CheckAsync(Shell); }),
            Row(L("Projet", "Project"), L("Le code, les nouveautés et les versions pour Windows et Mac.", "The code, what’s new and the versions for Windows and Mac."), Ui.Action(L("Ouvrir GitHub", "Open GitHub"), "globe", "", () => Updates.Open(Updates.Project)))));
        Content = new ScrollViewer { Content = page };
    }
    private void Save() { Shell.Profile.SaveSettings(Shell.Settings); Shell.ChoicesChanged(); }
    /// <summary>
    /// « Langue de Mira »: the Mac's language, English or French. Mira opens in it next time (its texts are written as it
    /// starts, <see cref="Lang"/>); from Mira.app, « Relancer » opens it again at once. Said in the language chosen.
    /// </summary>
    private Control AppLanguage(PlayerSettings settings)
    {
        var mac = Resolve(null, null, SystemLanguage.Preferred()) == French ? "Français" : "English";
        (string, string)[] options = [(L($"Langue du Mac ({mac})", $"Mac’s language ({mac})"), ""), ("English", English), ("Français", French)];
        return Choice(L("Langue de Mira", "Mira’s language"), options, Known(settings.Language) ?? "", v =>
        {
            settings.Language = v;
            var chosen = Resolve(v, null, SystemLanguage.Preferred());
            if (chosen == Code) return;
            string Said(string french, string english) => chosen == French ? french : english;
            var text = Said("Mira passera en français à sa prochaine ouverture.", "Mira will be in English the next time it opens.");
            // Opened again once this Mira has quit, which saves the settings and closes the session as any quit does.
            if (SystemLanguage.Bundle() is { } bundle) Shell.Notice(text, Said("Relancer", "Relaunch"), () => { if (SystemLanguage.RelaunchAfterExit(bundle)) Shell.Close(); });
            else Shell.Notice(text);
        }, L("Appliquée à la prochaine ouverture de Mira.", "Applies the next time Mira opens."));
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
    /// <summary>Each row of the home page: shown or not, and two arrows to move it; the arrow pressed keeps the focus.</summary>
    private void DrawHome(string? focus = null)
    {
        var settings = Shell.Settings;
        var rows = HomeLayout.Rows(settings.HomeRows, _libraries);
        Control? focused = null;
        void Apply(string value, string key, string part) { settings.HomeRows = value; Save(); DrawHome(key + part); }
        var controls = rows.Select((row, i) =>
        {
            var shown = new ToggleSwitch { IsChecked = row.Shown, OnContent = null, OffContent = null, MinWidth = 0 };
            Avalonia.Automation.AutomationProperties.SetName(shown, L($"Afficher « {row.Title} »", $"Show “{row.Title}”"));
            shown.IsCheckedChanged += (_, _) => Apply(HomeLayout.Show(settings.HomeRows, _libraries, row.Key, shown.IsChecked == true), row.Key, "shown");
            var up = Ui.IconButton("up", L($"Monter « {row.Title} »", $"Move “{row.Title}” up"), () => Apply(HomeLayout.Move(settings.HomeRows, _libraries, row.Key, -1), row.Key, i == 1 ? "down" : "up"), "icon", 18);
            var down = Ui.IconButton("down", L($"Descendre « {row.Title} »", $"Move “{row.Title}” down"), () => Apply(HomeLayout.Move(settings.HomeRows, _libraries, row.Key, 1), row.Key, i == rows.Count - 2 ? "up" : "down"), "icon", 18);
            up.IsEnabled = i > 0; down.IsEnabled = i < rows.Count - 1;
            if (focus == row.Key + "shown") focused = shown; else if (focus == row.Key + "up") focused = up; else if (focus == row.Key + "down") focused = down;
            return Row(row.Title, HomeLayout.Detail(row), Ui.Row(8, shown, up, down));
        }).ToList();
        controls.Add(Row(L("Ordre de Mira", "Mira’s order"), L("Les rangées de départ, dans leur ordre.", "The original rows, in their order."), Ui.Action(L("Rétablir", "Restore"), "refresh", "", () => { settings.HomeRows = ""; Save(); DrawHome(); })));
        _home.Content = Section(L("Accueil", "Home"), [.. controls]);
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

    private static Control Section(string title, params Control[] rows)
    {
        var list = new StackPanel { Spacing = 1 };
        for (var i = 0; i < rows.Length; i++)
        {
            var radius = new CornerRadius(i == 0 ? 12 : 0, i == 0 ? 12 : 0, i == rows.Length - 1 ? 12 : 0, i == rows.Length - 1 ? 12 : 0);
            list.Children.Add(new Border { Background = Ui.Brush("#111113"), CornerRadius = radius, Padding = new Thickness(22, 16), Child = rows[i] });
        }
        return Ui.Column(12, Ui.Heading(title, 19, 2), list);
    }
    private static Control Row(string title, string detail, Control? action)
    {
        var dock = new DockPanel();
        if (action is not null) { action.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(action, Dock.Right); dock.Children.Add(action); }
        var text = Ui.Column(3, new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = detail, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        text.Margin = new Thickness(0, 0, 24, 0);
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
    private Control[] SubtitleRows(PlayerSettings settings)
    {
        var text = new TextBlock { Text = L("Encore un épisode ?", "One more episode?"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var box = new Border { Child = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        var outline = new DropShadowEffect { Color = Colors.Black, OffsetX = 0, OffsetY = 1, BlurRadius = 4, Opacity = 1 };
        var scene = new Border
        {
            Height = 150, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = box,
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
            box.Margin = new Thickness(16, 0, 16, 22 + (100 - SubtitleStyle.Height(settings.SubtitleHeight)) / 100.0 * 150);
        }
        var size = new Slider { Minimum = 24, Maximum = 64, Value = settings.SubtitleSize, Width = 240, TickFrequency = 2, IsSnapToTickEnabled = true };
        var sizeLabel = new TextBlock { Text = $"{settings.SubtitleSize}", Width = 36, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted };
        size.ValueChanged += (_, e) => { settings.SubtitleSize = (int)e.NewValue; sizeLabel.Text = $"{settings.SubtitleSize}"; Save(); Preview(); };
        Avalonia.Automation.AutomationProperties.SetName(size, L("Taille des sous-titres", "Subtitle size"));
        Preview();
        var note = new TextBlock
        {
            Text = L("Aperçu indicatif, appliqué à la prochaine lecture. Taille, couleur, fond et gras valent pour les sous-titres simples (SRT, WebVTT…) : les sous-titres stylés des animés (ASS) gardent leur mise en forme, seule la hauteur remonte leurs dialogues.",
                "A preview, applied at the next playback. Size, color, background and bold apply to plain subtitles (SRT, WebVTT…): the styled subtitles of anime (ASS) keep their look, only the height moves their dialogue up."),
            FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap,
        };
        return [
            Ui.Column(12, scene, note),
            Row(L("Taille", "Size"), L("La taille du texte à l’écran.", "The size of the text on screen."), Ui.Row(12, size, sizeLabel)),
            Toggle(L("Fond sombre", "Dark background"), L("Un bandeau derrière le texte, lisible même sur une scène claire.", "A band behind the text, readable even over a bright scene."), settings.SubtitleBackground, v => { settings.SubtitleBackground = v; Preview(); }),
            Choice(L("Couleur", "Color"), [(L("Blanc", "White"), SubtitleStyle.White), (L("Jaune", "Yellow"), SubtitleStyle.Yellow)], settings.SubtitleColor == SubtitleStyle.Yellow ? SubtitleStyle.Yellow : SubtitleStyle.White,
                v => { settings.SubtitleColor = v; Preview(); }, L("Le jaune se détache mieux des scènes blanches.", "Yellow stands out better against white scenes.")),
            Toggle(L("Texte en gras", "Bold text"), L("Plus épais, plus facile à lire de loin.", "Thicker, easier to read from a distance."), settings.SubtitleBold, v => { settings.SubtitleBold = v; Preview(); }),
            Choice(L("Hauteur à l’écran", "Height on screen"), [.. SubtitleStyle.Heights.Select(x => (x.Label, x.Position.ToString(CultureInfo.InvariantCulture)))], SubtitleStyle.Height(settings.SubtitleHeight).ToString(CultureInfo.InvariantCulture),
                v => { settings.SubtitleHeight = int.Parse(v, CultureInfo.InvariantCulture); Preview(); }, L("Plus haut, ils évitent un texte incrusté en bas de l’image.", "Higher up, they stay clear of text burned into the bottom of the picture."))];
    }
    private Control Choice(string title, (string Label, string Value)[] options, string current, Action<string> set, string? detail = null)
    {
        var box = new ComboBox { MinWidth = 240 };
        foreach (var (label, value) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        if (options.All(x => x.Value != current)) box.Items.Add(new ComboBoxItem { Content = L("Personnalisé : ", "Custom: ") + current, Tag = current });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(x => (string)x.Tag! == current);
        Avalonia.Automation.AutomationProperties.SetName(box, title);
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string value }) { set(value); Save(); } };
        return Row(title, detail ?? L("Les pistes choisies en premier, dans cet ordre.", "The tracks chosen first, in this order."), box);
    }
}
