using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>Réglages: the account, playback, languages and subtitles, and the version of Mira and its updates.</summary>
public sealed class SettingsPage : Page
{
    public override string Rail => "settings";
    private static readonly (string Label, string Value)[] Audio =
        [("Japonais, puis français", "jpn,ja,fre,fra,fr,eng,en"), ("Français, puis anglais", "fre,fra,fr,eng,en"), ("Anglais, puis français", "eng,en,fre,fra,fr")];
    private static readonly (string Label, string Value)[] Subtitles =
        [("Français, puis anglais", "fre,fra,fr,eng,en"), ("Anglais", "eng,en"), ("Japonais", "jpn,ja")];

    public SettingsPage(MainWindow shell) : base(shell)
    {
        var settings = shell.Settings;
        var page = new StackPanel { Spacing = 34, Margin = new Thickness(56, 44 + shell.TitleBarInset, 56, 60), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Ui.Text("Réglages", 34, FontWeight.ExtraBold));

        var connection = shell.Session?.Connection;
        // Read when « Se déconnecter » is clicked, never kept: each sign-out asks anew.
        var erase = new ToggleSwitch { IsChecked = false, OnContent = null, OffContent = null, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(erase, "Effacer aussi les données de ce compte sur ce Mac");
        page.Children.Add(Section("Compte",
            Row("Serveur Jellyfin", connection?.Server ?? "—", null),
            Row("Utilisateur", connection?.UserName ?? "—", SignOut(erase)),
            Row("Effacer aussi les données de ce compte sur ce Mac", "En te déconnectant : le cache de ta bibliothèque, les affiches et les progressions pas encore envoyées à Jellyfin. Tes réglages restent.", erase)));
        // « Ton bilan »: the month or the year, from Jellyfin's record of what was played to the end.
        page.Children.Add(Section("Ton bilan", Report()));
        // Quick Connect: shown once Jellyfin says it lets devices in by a code.
        var devices = Section("Autoriser un appareil", ApproveDevice());
        devices.IsVisible = false;
        page.Children.Add(devices);
        if (shell.Session is { } session) _ = ShowWhenEnabledAsync(session.Client, devices);

        page.Children.Add(Section("Lecture",
            Toggle("Reprendre là où tu t’es arrêté", "Sinon, chaque titre repart du début.", settings.RememberPosition, v => settings.RememberPosition = v),
            Toggle("Épisode suivant automatique", "À la fin d’un épisode, le suivant démarre.", settings.AutoNext, v => settings.AutoNext = v),
            Toggle("Passer les intros et les récaps", "Sans appuyer sur S, quand Jellyfin ou les chapitres du fichier les ont repérés. Revenir en arrière les rejoue.", settings.AutoSkip, v => settings.AutoSkip = v),
            Toggle("Décodage matériel", "Utilise la puce du Mac pour décoder la vidéo : moins de chauffe, plus d’autonomie.", settings.HardwareDecoding, v => settings.HardwareDecoding = v)));

        // « Accueil à ta main »: the libraries come from Jellyfin, for their « Nouveau dans … ».
        page.Children.Add(_home);
        DrawHome();
        if (shell.Session is { } account) _ = LoadLibrariesAsync(account.Client);

        page.Children.Add(Section("Langues",
            Choice("Audio", Audio, settings.AudioLanguage, v => settings.AudioLanguage = v),
            Choice("Sous-titres", Subtitles, settings.SubtitleLanguage, v => settings.SubtitleLanguage = v)));

        var size = new Slider { Minimum = 24, Maximum = 64, Value = settings.SubtitleSize, Width = 240, TickFrequency = 2, IsSnapToTickEnabled = true };
        var sizeLabel = new TextBlock { Text = $"{settings.SubtitleSize}", Width = 36, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted };
        size.ValueChanged += (_, e) => { settings.SubtitleSize = (int)e.NewValue; sizeLabel.Text = $"{settings.SubtitleSize}"; Save(); };
        Avalonia.Automation.AutomationProperties.SetName(size, "Taille des sous-titres");
        page.Children.Add(Section("Sous-titres", Row("Taille", "Appliquée à la prochaine lecture.", Ui.Row(12, size, sizeLabel))));

        page.Children.Add(Section("Mira",
            Row($"Version {Updates.Current}", "Mira pour Mac (Apple Silicon).", Ui.Action("Rechercher une mise à jour", "download", "", () => _ = Updates.CheckAsync(Shell, asked: true))),
            // Switched back on: GitHub is asked now rather than at the next launch, as on Windows.
            Toggle("Rechercher les nouvelles versions", "À chaque ouverture, Mira demande à GitHub s’il existe une nouvelle version. Sinon, seulement quand tu cliques sur « Rechercher une mise à jour ».",
                settings.AutoUpdate, v => { var again = v && !settings.AutoUpdate; settings.AutoUpdate = v; if (again) _ = Updates.CheckAsync(Shell); }),
            Row("Projet", "Le code, les nouveautés et les versions pour Windows et Mac.", Ui.Action("Ouvrir GitHub", "globe", "", () => Updates.Open(Updates.Project)))));
        Content = new ScrollViewer { Content = page };
    }
    private void Save() { Shell.Profile.SaveSettings(Shell.Settings); Shell.ChoicesChanged(); }
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
            periods.Children.Add(Ui.Action("Ce mois-ci", null, year ? "" : "chip on", () => { if (year) { year = false; _ = LoadAsync(); } }));
            periods.Children.Add(Ui.Action("Cette année", null, year ? "chip on" : "", () => { if (!year) { year = true; _ = LoadAsync(); } }));
        }
        async Task LoadAsync()
        {
            var mine = ++version; var period = year ? "cette année" : "ce mois-ci";
            Periods(); body.Children.Clear(); time.Text = ""; time.IsVisible = false;
            if (Shell.Session is not { } session) return;
            counts.Text = "Calcul de ton bilan…";
            try
            {
                var from = WatchReport.Start(DateTimeOffset.Now, year);
                var (played, series) = await session.Client.PlayedSinceAsync(from, session.Lifetime);
                if (mine != version) return;
                var report = WatchReport.Build(played, series, from);
                if (report.Empty) { counts.Text = $"Aucun film ni épisode terminé {period} pour l’instant."; return; }
                time.Text = WatchReport.Time(report.Minutes); time.IsVisible = true;
                counts.Text = $"devant l’écran {period} · " + string.Join(" · ", new[] { report.Films > 0 ? WatchReport.Count(report.Films, "film", "films") : null, report.Episodes > 0 ? WatchReport.Count(report.Episodes, "épisode", "épisodes") : null }.OfType<string>());
                Part("SÉRIES LES PLUS REGARDÉES", report.Series.Select(x => (x.Name, $"{WatchReport.Count(x.Episodes, "épisode", "épisodes")} · {WatchReport.Time(x.Minutes)}")));
                Part(report.Finished.Count > 1 ? "SÉRIES TERMINÉES" : "SÉRIE TERMINÉE", report.Finished.Select(x => (x, "")));
                if (report.Genres.Count > 0) Part("GENRES PRÉFÉRÉS", [(string.Join(" · ", report.Genres), "")]);
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
            new TextBlock { Text = "D’après Jellyfin : chaque film ou épisode vu jusqu’au bout compte une fois, à sa dernière lecture, sur tous tes appareils.", FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
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
            Avalonia.Automation.AutomationProperties.SetName(shown, "Afficher « " + row.Title + " »");
            shown.IsCheckedChanged += (_, _) => Apply(HomeLayout.Show(settings.HomeRows, _libraries, row.Key, shown.IsChecked == true), row.Key, "shown");
            var up = Ui.IconButton("up", "Monter « " + row.Title + " »", () => Apply(HomeLayout.Move(settings.HomeRows, _libraries, row.Key, -1), row.Key, i == 1 ? "down" : "up"), "icon", 18);
            var down = Ui.IconButton("down", "Descendre « " + row.Title + " »", () => Apply(HomeLayout.Move(settings.HomeRows, _libraries, row.Key, 1), row.Key, i == rows.Count - 2 ? "up" : "down"), "icon", 18);
            up.IsEnabled = i > 0; down.IsEnabled = i < rows.Count - 1;
            if (focus == row.Key + "shown") focused = shown; else if (focus == row.Key + "up") focused = up; else if (focus == row.Key + "down") focused = down;
            return Row(row.Title, HomeLayout.Detail(row), Ui.Row(8, shown, up, down));
        }).ToList();
        controls.Add(Row("Ordre de Mira", "Les rangées de départ, dans leur ordre.", Ui.Action("Rétablir", "refresh", "", () => { settings.HomeRows = ""; Save(); DrawHome(); })));
        _home.Content = Section("Accueil", [.. controls]);
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
        Avalonia.Automation.AutomationProperties.SetName(box, "Code de l’appareil à autoriser");
        var status = new TextBlock { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
        Button? button = null;
        async Task ApproveAsync()
        {
            if (Shell.Session?.Client is not { } client || !button!.IsEnabled) return;
            if (JellyfinClient.QuickConnectCode(box.Text) is not { } code) { status.Text = "Le code a 6 chiffres."; box.Focus(); box.SelectAll(); return; }
            button.IsEnabled = false; status.Text = "Autorisation…";
            try
            {
                var approval = await client.ApproveCodeAsync(code);
                status.Text = approval switch
                {
                    CodeApproval.Approved => "Appareil autorisé : il se connecte à ton compte.",
                    CodeApproval.Unknown => "Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.",
                    _ => "Quick Connect est désactivé sur ce serveur : active-le dans le tableau de bord de Jellyfin (Général).",
                };
                if (approval == CodeApproval.Approved) box.Text = ""; else { box.Focus(); box.SelectAll(); }
            }
            catch (Exception ex) when (Errors.Expected(ex)) { status.Text = Errors.Friendly(ex); }
            finally { button.IsEnabled = true; }
        }
        button = Ui.Action("Autoriser", null, "", () => _ = ApproveAsync());
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = ApproveAsync(); } };
        return Ui.Column(12,
            new TextBlock { Text = "Sur ton téléphone, « Se connecter avec un code » affiche 6 chiffres : saisis-les ici pour le connecter à ton compte, sans y taper ton mot de passe. N’autorise qu’un appareil que tu as sous les yeux.", FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap },
            Ui.Row(10, box, button), status);
    }
    /// <summary>« Se déconnecter »: one click, as telling Jellyfin can take a few seconds.</summary>
    private Button SignOut(ToggleSwitch erase)
    {
        Button? button = null;
        button = Ui.Action("Se déconnecter", "exit", "", () => { button!.IsEnabled = false; _ = Shell.SignOutAsync(erase.IsChecked == true); });
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
        return Ui.Column(12, new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.Bold }, list);
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
    private Control Choice(string title, (string Label, string Value)[] options, string current, Action<string> set)
    {
        var box = new ComboBox { MinWidth = 240 };
        foreach (var (label, value) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        if (options.All(x => x.Value != current)) box.Items.Add(new ComboBoxItem { Content = "Personnalisé : " + current, Tag = current });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(x => (string)x.Tag! == current);
        Avalonia.Automation.AutomationProperties.SetName(box, title);
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string value }) { set(value); Save(); } };
        return Row(title, "Les pistes choisies en premier, dans cet ordre.", box);
    }
}
