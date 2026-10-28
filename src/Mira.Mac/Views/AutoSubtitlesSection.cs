using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Sous-titres automatiques » of the settings, for an administrator of a Jellyfin whose extension has them: the
/// extension searches OpenSubtitles, and SubDL when set, for each title that arrives, keeps only what fits the file,
/// aligns it on the video and keeps a journal. The same words as Windows and Mira web (<see cref="AutoSubtitles"/>).
/// Secrets typed here go to the extension only, never shown back.
/// </summary>
public sealed class AutoSubtitlesSection
{
    private const int JournalShown = 8;
    private readonly MainWindow _shell;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _waiting = Note("");
    private readonly TextBlock _status = Note("");
    private AutoSubtitleState? _state;
    private string? _tried;
    private bool _busy;

    public Control Section { get; }

    public AutoSubtitlesSection(MainWindow shell, Func<string, Control[], Control> section)
    {
        _shell = shell;
        Section = section(L("Sous-titres automatiques", "Automatic subtitles"), [Ui.Column(14, _waiting, _body, _status)]);
        Section.IsVisible = false;
        _ = LoadAsync();
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(0, 8, 0, 0) };

    private async Task LoadAsync()
    {
        if (_shell.Session?.Client is not { } client) return;
        try
        {
            if (await client.IsAdministratorAsync() != true) return;
            _state = await client.AutoSubtitlesAsync();
            if (_state is null) return;
            Render();
            Section.IsVisible = true;
        }
        catch (Exception ex) when (Errors.Expected(ex)) { if (Section.IsVisible) _status.Text = Errors.Friendly(ex); }
    }

    /// <summary>Sends what changed; the section shows what the extension kept.</summary>
    private async Task ChangeAsync(JsonObject changes, string? said = null)
    {
        if (_shell.Session?.Client is not { } client || _state is not { } state || _busy) return;
        _busy = true;
        try
        {
            _state = state with { Settings = await client.SetAutoSubtitlesAsync(changes) };
            Render();
            _status.Text = said ?? "";
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); }
        finally { _busy = false; }
    }

    private Control Switch(string title, string detail, bool on, Func<bool, JsonObject> change)
    {
        var toggle = new ToggleSwitch { IsChecked = on, OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Top };
        Avalonia.Automation.AutomationProperties.SetName(toggle, title);
        toggle.IsCheckedChanged += (_, _) => _ = ChangeAsync(change(toggle.IsChecked == true));
        var row = new DockPanel();
        DockPanel.SetDock(toggle, Dock.Right); row.Children.Add(toggle);
        var text = Ui.Column(3, new TextBlock { Text = title, FontSize = 15, TextWrapping = TextWrapping.Wrap }, Note(detail));
        text.Margin = new Thickness(0, 0, 24, 0);
        row.Children.Add(text);
        return row;
    }

    private void Render()
    {
        if (_state is not { Settings: { } s } state) return;
        _waiting.Text = AutoSubtitles.Waiting(state);
        _body.Children.Clear();
        var languages = s.Languages ?? [];

        _body.Children.Add(Switch(L("Chercher tout seul", "Search automatically"),
            L("Quand un titre arrive, puis de temps en temps pendant un mois s’il n’y a rien d’assez sûr. Jamais pendant une lecture.", "When a title arrives, then now and then for a month if nothing is reliable enough. Never while something plays."),
            s.Enabled, on =>
            {
                var change = new JsonObject { ["enabled"] = on };
                if (on && languages.Count == 0) change["languages"] = new JsonArray(IsFrench ? "fre" : "eng");
                return change;
            }));
        _body.Children.Add(Note(AutoSubtitles.Privacy));

        _body.Children.Add(Label(L("Langues", "Languages")));
        var chips = new WrapPanel();
        foreach (var code in languages)
        {
            var name = AutoSubtitles.LanguageName(code);
            var chip = Ui.Action(name + "  ×", null, "quiet", () => _ = ChangeAsync(new JsonObject { ["languages"] = new JsonArray([.. languages.Where(x => x != code).Select(x => (JsonNode)x)]) }));
            Avalonia.Automation.AutomationProperties.SetName(chip, L($"Retirer {name}", $"Remove {name}"));
            chip.Margin = new Thickness(0, 0, 8, 8);
            chips.Children.Add(chip);
        }
        if (languages.Count < AutoSubtitles.MaxLanguages)
        {
            var add = new ComboBox { MinWidth = 220, PlaceholderText = L("Ajouter une langue…", "Add a language…") };
            Avalonia.Automation.AutomationProperties.SetName(add, L("Ajouter une langue", "Add a language"));
            foreach (var code in AutoSubtitles.Languages.Where(x => !languages.Contains(x))) add.Items.Add(new ComboBoxItem { Content = AutoSubtitles.LanguageName(code), Tag = code });
            add.SelectionChanged += (_, _) =>
            {
                if (add.SelectedItem is ComboBoxItem { Tag: string code }) _ = ChangeAsync(new JsonObject { ["languages"] = new JsonArray([.. languages.Append(code).Select(x => (JsonNode)x)]) });
            };
            chips.Children.Add(add);
        }
        _body.Children.Add(chips);
        _body.Children.Add(Note(L("Une recherche par langue et par titre. Ce qui a déjà des sous-titres de cette langue, dans le fichier ou à côté, est laissé tel quel.", "One search per language and title. Titles that already have subtitles in that language, in the file or next to it, are left alone.")));

        _body.Children.Add(Label(L("Ce que Mira garde", "What Mira keeps")));
        var hearing = new ComboBox { MinWidth = 260 };
        Avalonia.Automation.AutomationProperties.SetName(hearing, L("Pour sourds et malentendants", "SDH"));
        foreach (var (choice, label) in new[] { ("any", L("Pour sourds : indifférent", "SDH: either")), ("prefer", L("Pour sourds : de préférence", "SDH: preferred")), ("avoid", L("Pour sourds : les éviter", "SDH: avoid them")) })
            hearing.Items.Add(new ComboBoxItem { Content = label, Tag = choice });
        hearing.SelectedItem = hearing.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag! == (s.Hearing ?? "any"));
        hearing.SelectionChanged += (_, _) => { if (hearing.SelectedItem is ComboBoxItem { Tag: string picked } && picked != s.Hearing) _ = ChangeAsync(new JsonObject { ["hearing"] = picked }); };
        _body.Children.Add(hearing);
        _body.Children.Add(Switch(L("Accepter les traductions automatiques", "Accept machine translations"), L("Seulement s’il n’y a rien d’autre.", "Only when there is nothing else."),
            s.MachineTranslated, on => new JsonObject { ["machineTranslated"] = on }));
        _body.Children.Add(Switch(L("Recaler sur la vidéo", "Sync to the video"),
            L("Sur une piste de sous-titres du fichier, sinon sur les voix : le PC serveur écoute le film (une minute ou deux, en priorité basse).", "On a subtitle track of the file, else on the voices: the server PC listens to the film (a minute or two, at low priority)."),
            s.Sync, on => new JsonObject { ["sync"] = on }));

        _body.Children.Add(Label(L("Sources", "Sources")));
        var os = s.OpenSubtitles;
        _body.Children.Add(Switch("OpenSubtitles", os?.Account is { } account
                ? L($"Compte {account}.", $"Account {account}.")
                : L("Sans compte : quelques téléchargements par jour. Un compte gratuit en donne plus.", "Without an account: a few downloads a day. A free account gives more."),
            os?.Enabled == true, on => new JsonObject { ["openSubtitles"] = new JsonObject { ["enabled"] = on } }));
        var user = new TextBox { Width = 200, FontSize = 14, Text = os?.Account ?? "", Watermark = L("Nom d’utilisateur", "Username") };
        Avalonia.Automation.AutomationProperties.SetName(user, L("Nom d’utilisateur OpenSubtitles", "OpenSubtitles username"));
        var password = new TextBox { Width = 200, FontSize = 14, PasswordChar = '•', Watermark = L("Mot de passe", "Password") };
        Avalonia.Automation.AutomationProperties.SetName(password, L("Mot de passe OpenSubtitles", "OpenSubtitles password"));
        var saveAccount = Ui.Action(L("Enregistrer le compte", "Save the account"), null, "", () =>
        {
            var name = (user.Text ?? "").Trim();
            var change = new JsonObject { ["username"] = name };
            if (!string.IsNullOrEmpty(password.Text) || name.Length == 0) change["password"] = password.Text ?? "";
            _ = ChangeAsync(new JsonObject { ["openSubtitles"] = change }, L("Compte enregistré.", "Account saved."));
        });
        _body.Children.Add(Ui.Row(10, user, password, saveAccount));
        _body.Children.Add(Note(L("Le mot de passe reste sur le PC serveur, jamais renvoyé à un appareil. Laisse le nom vide pour effacer le compte.", "The password stays on the server PC and is never sent back to a device. Leave the name empty to clear the account.")));

        var sd = s.SubDl;
        if (sd?.Ready == true) _body.Children.Add(Switch("SubDL", L("Clé d’API enregistrée.", "API key saved."), sd.Enabled, on => new JsonObject { ["subDl"] = new JsonObject { ["enabled"] = on } }));
        else _body.Children.Add(new TextBlock { Text = "SubDL", FontSize = 15 });
        var key = new TextBox { Width = 300, FontSize = 14, PasswordChar = '•', Watermark = L("Clé d’API", "API key") };
        Avalonia.Automation.AutomationProperties.SetName(key, L("Clé d’API SubDL", "SubDL API key"));
        var keyRow = Ui.Row(10, key, Ui.Action(sd?.Ready == true ? L("Changer la clé", "Change the key") : L("Enregistrer la clé", "Save the key"), null, "", () =>
        {
            if (string.IsNullOrWhiteSpace(key.Text)) return;
            _ = ChangeAsync(new JsonObject { ["subDl"] = new JsonObject { ["apiKey"] = key.Text, ["enabled"] = true } }, L("Clé enregistrée.", "Key saved."));
        }));
        if (sd?.Ready == true) keyRow.Children.Add(Ui.Action(L("Effacer la clé", "Clear the key"), null, "quiet", () => _ = ChangeAsync(new JsonObject { ["subDl"] = new JsonObject { ["apiKey"] = "", ["enabled"] = false } })));
        _body.Children.Add(keyRow);
        _body.Children.Add(Note(L("Une clé gratuite : crée un compte sur subdl.com, puis copie celle de ton profil. Elle reste sur le PC serveur.", "A free key: create an account on subdl.com, then copy the one from your profile. It stays on the server PC.")));

        Button? test = null, library = null;
        test = Ui.Action(L("Essayer les sources", "Test the sources"), "refresh", "", () => _ = TryAsync(test!));
        library = Ui.Action(L("Chercher aussi pour ce qui est déjà là", "Also search for what is already there"), null, "quiet", () => _ = QueueAsync(library!));
        library.IsEnabled = s.Enabled && languages.Count > 0;
        _body.Children.Add(Ui.Row(10, test, library));
        if (_tried is { } tried) _body.Children.Add(Note(tried));
        _body.Children.Add(Note(L("« Chercher aussi pour ce qui est déjà là » : chaque film et épisode sans sous-titres de tes langues passe une fois, quelques-uns par minute, au rythme des quotas.", "“Also search for what is already there”: each film and episode without subtitles in your languages goes through once, a few a minute, as the quotas allow.")));

        _body.Children.Add(Label(L("Journal", "Log")));
        var journal = state.Journal ?? [];
        if (journal.Count == 0) _body.Children.Add(Note(L("Rien encore : ce que Mira trouve, recale ou écarte s’inscrit ici.", "Nothing yet: what Mira finds, syncs or sets aside shows here.")));
        foreach (var entry in journal.Take(JournalShown))
        {
            var when = entry.When.ToLocalTime();
            var at = when.Date == DateTime.Today ? Time(when) : Day(when);
            _body.Children.Add(Ui.Column(3,
                new TextBlock { Text = entry.Name is { Length: > 0 } name ? name : L("Titre", "Title"), FontSize = 15, TextWrapping = TextWrapping.Wrap },
                Note(string.Join(" · ", new[] { entry.Language is { } language ? AutoSubtitles.LanguageName(language) : null, AutoSubtitles.Line(entry), at }.Where(x => !string.IsNullOrEmpty(x))))));
        }
    }

    private async Task TryAsync(Button button)
    {
        if (_shell.Session?.Client is not { } client) return;
        button.IsEnabled = false;
        _status.Text = L("Essai en cours…", "Testing…");
        try { _tried = AutoSubtitles.Tried(await client.TestAutoSubtitlesAsync()); }
        catch (Exception ex) when (Errors.Expected(ex)) { _tried = Errors.Friendly(ex); }
        _status.Text = "";
        Render();
    }

    private async Task QueueAsync(Button button)
    {
        if (_shell.Session?.Client is not { } client || _state is not { } state) return;
        button.IsEnabled = false;
        try
        {
            var (added, waiting) = await client.QueueAutoSubtitlesAsync();
            _state = state with { Waiting = waiting };
            Render();
            _status.Text = added > 0
                ? Lang.Count(added, "recherche ajoutée.", "recherches ajoutées.", "search added.", "searches added.")
                : L("Rien à ajouter : tout est déjà en attente ou fait.", "Nothing to add: everything is already waiting or done.");
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); button.IsEnabled = true; }
    }
}
