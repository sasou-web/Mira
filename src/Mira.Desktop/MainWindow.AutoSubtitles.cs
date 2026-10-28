using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « Sous-titres automatiques » (Réglages → Jellyfin, for an administrator): the extension searches OpenSubtitles, and
// SubDL when set, for each title that arrives, keeps only what fits the file, aligns it on the video, and keeps a
// journal (Mira.Jellyfin Subtitles/, JellyfinClient.AutoSubtitlesAsync). Here as in Mira web (views/auto-subtitles.js):
// turned on or off, the languages, what is kept, the sources and their accounts (never shown back), a try of the
// sources, the titles already there, the journal. Secrets typed here go to the extension only.
public partial class MainWindow
{
    private bool _autoSubtitlesBusy;
    private AutoSubtitleState? _autoSubtitles;
    private string? _autoSubtitlesTried;
    private const int AutoSubtitleJournalShown = 8;

    /// <summary>The row, for an administrator of a Jellyfin whose extension has « Sous-titres automatiques ».</summary>
    private async Task ShowAutoSubtitlesAsync()
    {
        if (_demo || _client is not { } client) { AutoSubtitlesRow.Visibility = Visibility.Collapsed; return; }
        if (_autoSubtitlesBusy) return;
        _autoSubtitlesBusy = true;
        var ct = _connectionLifetime.Token;
        try
        {
            if (!await IsAdministratorAsync(client)) { AutoSubtitlesRow.Visibility = Visibility.Collapsed; return; }
            var state = await client.AutoSubtitlesAsync(ct);
            if (!ReferenceEquals(client, _client)) return;
            _autoSubtitles = state;
            RenderAutoSubtitles();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (RelayRefusedException) { AutoSubtitlesRow.Visibility = Visibility.Collapsed; }
        // Jellyfin out of reach: the rest of the tab says so already; this comes back with it.
        catch (Exception ex) when (IsExpected(ex)) { }
        finally { _autoSubtitlesBusy = false; }
    }

    /// <summary>Sends what changed; the row shows what the extension kept.</summary>
    private async Task ChangeAutoSubtitlesAsync(JsonObject changes, string? said = null)
    {
        if (_client is not { } client || _autoSubtitles is not { } state) return;
        try
        {
            var settings = await client.SetAutoSubtitlesAsync(changes, _connectionLifetime.Token);
            if (!ReferenceEquals(client, _client)) return;
            _autoSubtitles = state with { Settings = settings };
            RenderAutoSubtitles();
            if (said is not null) AutoSubtitlesStatus.Text = said;
        }
        catch (Exception ex) when (IsExpected(ex)) { AutoSubtitlesStatus.Text = ServerError(ex); }
    }

    private TextBlock Description(string text, double top = 7) =>
        new() { Text = text, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, top, 30, 0) };
    private TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 12, Foreground = Palette.Get("TextTertiary"), Margin = new Thickness(0, 20, 0, 4) };

    /// <summary>A switch with its label and what it does, as the other rows of Réglages.</summary>
    private DockPanel AutoSwitch(string label, string description, bool on, Func<bool, JsonObject> change)
    {
        var box = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(box, label);
        box.Click += async (_, _) => await ChangeAutoSubtitlesAsync(change(box.IsChecked == true));
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Description(description, 3));
        var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(box); row.Children.Add(text);
        return row;
    }

    private void RenderAutoSubtitles()
    {
        if (_autoSubtitles is not { Settings: { } s } state) { AutoSubtitlesRow.Visibility = Visibility.Collapsed; return; }
        AutoSubtitlesRow.Visibility = Visibility.Visible;
        AutoSubtitlesWaiting.Text = AutoSubtitles.Waiting(state);
        var panel = AutoSubtitlesPanel;
        panel.Children.Clear();
        var languages = s.Languages ?? [];

        panel.Children.Add(AutoSwitch(L("Chercher tout seul", "Search automatically"),
            L("Quand un titre arrive, puis de temps en temps pendant un mois s’il n’y a rien d’assez sûr. Jamais pendant une lecture.", "When a title arrives, then now and then for a month if nothing is reliable enough. Never while something plays."),
            s.Enabled, on =>
            {
                var change = new JsonObject { ["enabled"] = on };
                if (on && languages.Count == 0) change["languages"] = new JsonArray(Lang.IsFrench ? "fre" : "eng");
                return change;
            }));
        panel.Children.Add(Description(AutoSubtitles.Privacy));

        panel.Children.Add(Heading(L("Langues", "Languages")));
        var chips = new WrapPanel();
        foreach (var code in languages)
        {
            var name = AutoSubtitles.LanguageName(code);
            var chip = new Button { Content = name + "  ×", Style = (Style)FindResource("Quiet"), Margin = new Thickness(0, 4, 8, 4) };
            AutomationProperties.SetName(chip, L($"Retirer {name}", $"Remove {name}"));
            chip.Click += async (_, _) => await ChangeAutoSubtitlesAsync(new JsonObject { ["languages"] = new JsonArray([.. languages.Where(x => x != code).Select(x => (JsonNode)x)]) });
            chips.Children.Add(chip);
        }
        if (languages.Count < AutoSubtitles.MaxLanguages)
        {
            var add = new ComboBox { Width = 200, Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetName(add, L("Ajouter une langue", "Add a language"));
            add.Items.Add(new ComboBoxItem { Content = L("Ajouter une langue…", "Add a language…"), Tag = "", IsSelected = true });
            foreach (var code in AutoSubtitles.Languages.Where(x => !languages.Contains(x))) add.Items.Add(new ComboBoxItem { Content = AutoSubtitles.LanguageName(code), Tag = code });
            add.PreviewMouseLeftButtonDown += Combo_PreviewClick;
            add.SelectionChanged += async (_, _) =>
            {
                if (Choice(add) is { Length: > 0 } code) await ChangeAutoSubtitlesAsync(new JsonObject { ["languages"] = new JsonArray([.. languages.Append(code).Select(x => (JsonNode)x)]) });
            };
            chips.Children.Add(add);
        }
        panel.Children.Add(chips);
        panel.Children.Add(Description(L("Une recherche par langue et par titre. Ce qui a déjà des sous-titres de cette langue, dans le fichier ou à côté, est laissé tel quel.", "One search per language and title. Titles that already have subtitles in that language, in the file or next to it, are left alone.")));

        panel.Children.Add(Heading(L("Ce que Mira garde", "What Mira keeps")));
        var hearing = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(hearing, L("Pour sourds et malentendants", "SDH"));
        foreach (var (choice, label) in new[] { ("any", L("Pour sourds : indifférent", "SDH: either")), ("prefer", L("Pour sourds : de préférence", "SDH: preferred")), ("avoid", L("Pour sourds : les éviter", "SDH: avoid them")) })
            hearing.Items.Add(new ComboBoxItem { Content = label, Tag = choice, IsSelected = choice == (s.Hearing ?? "any") });
        hearing.PreviewMouseLeftButtonDown += Combo_PreviewClick;
        hearing.SelectionChanged += async (_, _) => { if (Choice(hearing) is { Length: > 0 } picked && picked != s.Hearing) await ChangeAutoSubtitlesAsync(new JsonObject { ["hearing"] = picked }); };
        panel.Children.Add(hearing);
        panel.Children.Add(AutoSwitch(L("Accepter les traductions automatiques", "Accept machine translations"), L("Seulement s’il n’y a rien d’autre.", "Only when there is nothing else."),
            s.MachineTranslated, on => new JsonObject { ["machineTranslated"] = on }));
        panel.Children.Add(AutoSwitch(L("Recaler sur la vidéo", "Sync to the video"),
            L("Sur une piste de sous-titres du fichier, sinon sur les voix : le PC serveur écoute le film (une minute ou deux, en priorité basse).", "On a subtitle track of the file, else on the voices: the server PC listens to the film (a minute or two, at low priority)."),
            s.Sync, on => new JsonObject { ["sync"] = on }));

        panel.Children.Add(Heading(L("Sources", "Sources")));
        var os = s.OpenSubtitles;
        panel.Children.Add(AutoSwitch("OpenSubtitles", os?.Account is { } account
                ? L($"Compte {account}.", $"Account {account}.")
                : L("Sans compte : quelques téléchargements par jour. Un compte gratuit en donne plus.", "Without an account: a few downloads a day. A free account gives more."),
            os?.Enabled == true, on => new JsonObject { ["openSubtitles"] = new JsonObject { ["enabled"] = on } }));
        var user = new TextBox { Width = 200, Text = os?.Account ?? "", Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(user, L("Nom d’utilisateur OpenSubtitles", "OpenSubtitles username"));
        var password = new PasswordBox { Width = 200, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(password, L("Mot de passe OpenSubtitles", "OpenSubtitles password"));
        var saveAccount = new Button { Content = L("Enregistrer le compte", "Save the account") };
        saveAccount.Click += async (_, _) =>
        {
            var change = new JsonObject { ["username"] = user.Text.Trim() };
            if (password.Password.Length > 0 || user.Text.Trim().Length == 0) change["password"] = password.Password;
            await ChangeAutoSubtitlesAsync(new JsonObject { ["openSubtitles"] = change }, L("Compte enregistré.", "Account saved."));
        };
        var accountLine = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        accountLine.Children.Add(user); accountLine.Children.Add(password); accountLine.Children.Add(saveAccount);
        panel.Children.Add(accountLine);
        panel.Children.Add(Description(L("Le mot de passe reste sur le PC serveur, jamais renvoyé à un appareil. Laisse le nom vide pour effacer le compte.", "The password stays on the server PC and is never sent back to a device. Leave the name empty to clear the account.")));

        var sd = s.SubDl;
        if (sd?.Ready == true)
            panel.Children.Add(AutoSwitch("SubDL", L("Clé d’API enregistrée.", "API key saved."), sd.Enabled, on => new JsonObject { ["subDl"] = new JsonObject { ["enabled"] = on } }));
        else
            panel.Children.Add(new TextBlock { Text = "SubDL", Foreground = Palette.Get("TextBody"), Margin = new Thickness(0, 14, 0, 0) });
        var key = new PasswordBox { Width = 300, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(key, L("Clé d’API SubDL", "SubDL API key"));
        var saveKey = new Button { Content = sd?.Ready == true ? L("Changer la clé", "Change the key") : L("Enregistrer la clé", "Save the key") };
        saveKey.Click += async (_, _) =>
        {
            if (key.Password.Trim().Length == 0) return;
            await ChangeAutoSubtitlesAsync(new JsonObject { ["subDl"] = new JsonObject { ["apiKey"] = key.Password, ["enabled"] = true } }, L("Clé enregistrée.", "Key saved."));
        };
        var keyLine = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        keyLine.Children.Add(key); keyLine.Children.Add(saveKey);
        if (sd?.Ready == true)
        {
            var clear = new Button { Content = L("Effacer la clé", "Clear the key"), Style = (Style)FindResource("Quiet"), Margin = new Thickness(10, 0, 0, 0) };
            clear.Click += async (_, _) => await ChangeAutoSubtitlesAsync(new JsonObject { ["subDl"] = new JsonObject { ["apiKey"] = "", ["enabled"] = false } });
            keyLine.Children.Add(clear);
        }
        panel.Children.Add(keyLine);
        panel.Children.Add(Description(L("Une clé gratuite : crée un compte sur subdl.com, puis copie celle de ton profil. Elle reste sur le PC serveur.", "A free key: create an account on subdl.com, then copy the one from your profile. It stays on the server PC.")));

        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        var test = new Button { Content = L("Essayer les sources", "Test the sources"), Margin = new Thickness(0, 0, 10, 8) };
        test.Click += async (_, _) => await TryAutoSubtitleSourcesAsync(test);
        var library = new Button { Content = L("Chercher aussi pour ce qui est déjà là", "Also search for what is already there"), Style = (Style)FindResource("Quiet"), Margin = new Thickness(0, 0, 0, 8), IsEnabled = s.Enabled && languages.Count > 0 };
        library.Click += async (_, _) => await QueueAutoSubtitleLibraryAsync(library);
        actions.Children.Add(test); actions.Children.Add(library);
        panel.Children.Add(actions);
        if (_autoSubtitlesTried is { } tried) panel.Children.Add(Description(tried, 0));
        panel.Children.Add(Description(L("« Chercher aussi pour ce qui est déjà là » : chaque film et épisode sans sous-titres de tes langues passe une fois, quelques-uns par minute, au rythme des quotas.", "“Also search for what is already there”: each film and episode without subtitles in your languages goes through once, a few a minute, as the quotas allow.")));

        panel.Children.Add(Heading(L("Journal", "Log")));
        var journal = state.Journal ?? [];
        if (journal.Count == 0) panel.Children.Add(Description(L("Rien encore : ce que Mira trouve, recale ou écarte s’inscrit ici.", "Nothing yet: what Mira finds, syncs or sets aside shows here."), 3));
        foreach (var entry in journal.Take(AutoSubtitleJournalShown))
        {
            var line = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            line.Children.Add(new TextBlock { Text = entry.Name is { Length: > 0 } name ? name : L("Titre", "Title"), Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap });
            var when = entry.When.ToLocalTime();
            var at = when.Date == DateTime.Today ? Lang.Time(when) : Lang.Day(when);
            line.Children.Add(Description(string.Join(" · ", new[] { entry.Language is { } language ? AutoSubtitles.LanguageName(language) : null, AutoSubtitles.Line(entry), at }.Where(x => !string.IsNullOrEmpty(x))), 3));
            panel.Children.Add(line);
        }
    }

    private async Task TryAutoSubtitleSourcesAsync(Button button)
    {
        if (_client is not { } client) return;
        button.IsEnabled = false;
        AutoSubtitlesStatus.Text = L("Essai en cours…", "Testing…");
        try { _autoSubtitlesTried = AutoSubtitles.Tried(await client.TestAutoSubtitlesAsync(_connectionLifetime.Token)); }
        catch (Exception ex) when (IsExpected(ex)) { _autoSubtitlesTried = ServerError(ex); }
        AutoSubtitlesStatus.Text = "";
        if (ReferenceEquals(client, _client)) RenderAutoSubtitles();
    }

    private async Task QueueAutoSubtitleLibraryAsync(Button button)
    {
        if (_client is not { } client || _autoSubtitles is not { } state) return;
        button.IsEnabled = false;
        try
        {
            var (added, waiting) = await client.QueueAutoSubtitlesAsync(_connectionLifetime.Token);
            if (!ReferenceEquals(client, _client)) return;
            _autoSubtitles = state with { Waiting = waiting };
            RenderAutoSubtitles();
            AutoSubtitlesStatus.Text = added > 0
                ? Lang.Count(added, "recherche ajoutée.", "recherches ajoutées.", "search added.", "searches added.")
                : L("Rien à ajouter : tout est déjà en attente ou fait.", "Nothing to add: everything is already waiting or done.");
        }
        catch (Exception ex) when (IsExpected(ex)) { AutoSubtitlesStatus.Text = ServerError(ex); button.IsEnabled = true; }
    }
}
