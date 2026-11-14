using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Shapes;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The sections of Réglages and « L’essentiel », their front page (ogsas, 2026-11-14, maquette A: a friend could not
/// find where Mira updates): Mira's version and its update, the account, and six settings people change most, each one
/// bound to the same control in its own section, so that there is one value shown in two places. A version ready to
/// install puts a dot on « Mises à jour » and on the rail's gear.
/// </summary>
public partial class MainWindow
{
    /// <summary>Each section: its tag, its page, its title and what it holds, in the order of the list.</summary>
    private (string Tag, StackPanel Panel, string Title, string About)[] SettingsTabs =>
    [
        ("home", EssentialsSettings, L("L’essentiel", "Essentials"), L("Mira en un coup d’œil, et les réglages qu’on change le plus.", "Mira at a glance, and the settings people change most.")),
        ("playback", PlaybackSettings, L("Lecture", "Playback"), L("Ce qui compte pendant que tu regardes.", "What matters while you watch.")),
        ("subtitles", SubtitleSettings, L("Audio & sous-titres", "Audio & subtitles"), L("Les bonnes langues dès le premier épisode, et des sous-titres lisibles.", "The right languages from the very first episode, and readable subtitles.")),
        ("library", LibrarySettings, L("Accueil & bibliothèque", "Home & library"), L("Ce que montrent l’accueil et les pages : rangées, affiches, spoilers, épisodes manquants, titres gardés sur ce PC.", "What Home and the pages show: rows, posters, spoilers, missing episodes, titles kept on this PC.")),
        ("general", GeneralSettings, L("Général", "General"), L("La langue de Mira, le mode canapé, les animations et ce que Mira te signale.", "Mira’s language, couch mode, animations and what Mira tells you about.")),
        ("server", ServerSettings, L("Compte & serveur", "Account & server"), L("Ton compte, ton serveur Jellyfin, et le joindre loin de chez toi.", "Your account, your Jellyfin server, and reaching it away from home.")),
        ("privacy", PrivacySettings, L("Confidentialité", "Privacy"), L("Tout ce qui part sur le réseau, et comment l’arrêter.", "Everything that goes out on the network, and how to stop it.")),
        ("updates", UpdateSettings, L("Mises à jour", "Updates"), L("La version de Mira, ses mises à jour et son moteur de lecture.", "Mira’s version, its updates and its playback engine.")),
        ("report", ReportSettings, L("Ton bilan", "Your recap"), L("Ce que tu as regardé, sur tous tes appareils.", "What you’ve watched, on all your devices.")),
        ("sharing", SharingSettings, L("Amis & invités", "Friends & guests"), L("Un compte pour un ami, un lien pour un invité, un appareil à autoriser.", "An account for a friend, a link for a guest, a device to let in.")),
        ("care", CareSettings, L("Soin de la bibliothèque", "Library care"), L("Ce qui garde ta bibliothèque en forme : titres à corriger, sous-titres, épisodes manquants, collections, aperçus.", "What keeps your library in shape: titles to fix, subtitles, missing episodes, collections, previews.")),
        ("jellyfin", JellyfinSettings, L("Serveur Jellyfin", "Jellyfin server"), L("Qui regarde quoi, et la machine de Jellyfin : sa version, sa sauvegarde, sa carte graphique.", "Who is watching what, and Jellyfin’s machine: its version, its backup, its graphics card.")),
        ("torlink", TorLinkSettings, L("Téléchargements", "Downloads"), L("Où vont tes téléchargements, et comment ils rejoignent Jellyfin.", "Where your downloads go, and how they join Jellyfin.")),
    ];

    private bool _essentialsBound;
    /// <summary>
    /// The shortcuts of « L’essentiel » tied once to their settings, after the lists are filled: a change on either side is
    /// the other's at once, and saved as any change (<see cref="WatchSettingChanges"/>).
    /// </summary>
    private void MirrorEssentials()
    {
        if (_essentialsBound || QualityChoice.Items.Count == 0 || AudioLanguageChoice.Items.Count == 0) return;
        _essentialsBound = true;
        Copy(QualityChoice, EssentialsQuality); Copy(AudioLanguageChoice, EssentialsAudio); Copy(SubtitleLanguageChoice, EssentialsSubtitles);
        Bind(EssentialsQuality, QualityChoice); Bind(EssentialsAudio, AudioLanguageChoice); Bind(EssentialsSubtitles, SubtitleLanguageChoice); Bind(EssentialsLanguage, MiraLanguageChoice);
        EssentialsAutoNext.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = AutoNextCheck, Mode = BindingMode.TwoWay });
        EssentialsAutoSkip.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = AutoSkipCheck, Mode = BindingMode.TwoWay });
        static void Copy(ComboBox from, ComboBox to)
        {
            foreach (var item in from.Items.OfType<ComboBoxItem>()) to.Items.Add(new ComboBoxItem { Content = item.Content, Tag = item.Tag, ToolTip = item.ToolTip });
        }
        static void Bind(ComboBox shortcut, ComboBox setting) =>
            shortcut.SetBinding(Selector.SelectedIndexProperty, new Binding(nameof(Selector.SelectedIndex)) { Source = setting, Mode = BindingMode.TwoWay });
    }

    /// <summary>The account card and the subtitles' look, read again each time « L’essentiel » shows.</summary>
    private void ShowEssentials()
    {
        ShowEssentialsUpdate();
        ShowEssentialsAccount();
        var colour = (SubtitleColorChoice.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        EssentialsSubtitleStyle.Text = L("Taille", "Size") + " " + ((int)SubtitleSizeSlider.Value).ToString(Lang.Culture) + (colour.Length > 0 ? " · " + colour.ToLower(Lang.Culture) : "");
    }

    /// <summary>
    /// The account card: who, on which Jellyfin, and whether it answers. Also told again by <see cref="SyncChanged"/>
    /// while « L’essentiel » shows: Jellyfin answering again (a refresh behind the window) changes the line at once.
    /// </summary>
    private void ShowEssentialsAccount()
    {
        EssentialsLetter.Text = AvatarLetter.Text;
        var offline = _client is not null && _sync is { } sync && SyncStatus.Offline(sync.LastSynced, _offlineSince);
        if (_demo)
        {
            EssentialsName.Text = L("Mode démonstration", "Demo mode");
            EssentialsServerState.Text = L("Données fictives · rien n’est envoyé", "Made-up data · nothing is sent");
            EssentialsServerDot.Fill = Palette.Get("Faint");
        }
        else if (_client is { } client)
        {
            EssentialsName.Text = client.Connection.UserName + (InParty ? " · " + L("invité", "guest") : "");
            var host = Uri.TryCreate(client.Connection.Server, UriKind.Absolute, out var server) ? server.Host : client.Connection.Server;
            EssentialsServerState.Text = host + " · " + (offline ? L("ne répond pas", "not answering") : L("connecté", "connected"));
            EssentialsServerDot.Fill = Palette.Get(offline ? "Warning" : "Success");
        }
        else
        {
            EssentialsName.Text = L("Pas connecté", "Not signed in");
            EssentialsServerState.Text = L("Aucun serveur Jellyfin", "No Jellyfin server");
            EssentialsServerDot.Fill = Palette.Get("Faint");
        }
        EssentialsRecap.Visibility = _client is null || _demo || InParty ? Visibility.Collapsed : Visibility.Visible;
        // « Synchroniser maintenant » left: the sync is automatic. « Réessayer » only while Jellyfin isn't answering.
        SyncRetryButton.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The card of Mira: its version and where its update stands, said in a few words; its buttons those of « Mises à jour ».</summary>
    private void ShowEssentialsUpdate()
    {
        EssentialsVersion.Text = $"Mira {JellyfinClient.AppVersion}";
        var updater = _updater;
        var ready = updater?.State == UpdateState.Ready;
        var version = updater?.Offer?.Version.ToString(3);
        EssentialsUpdateState.Text = updater?.State switch
        {
            null => L("Mises à jour coupées pour cette session", "Updates off for this session"),
            UpdateState.Disabled => L("Copie de développement", "Development copy"),
            UpdateState.Checking => L("Recherche d’une nouvelle version…", "Looking for a new version…"),
            UpdateState.Downloading => L($"Téléchargement de Mira {version}… {Math.Floor(updater.Progress * 100):0} %", $"Downloading Mira {version}… {Math.Floor(updater.Progress * 100):0}%"),
            UpdateState.Ready => L($"Mira {version} est prête", $"Mira {version} is ready"),
            UpdateState.UpToDate => L("À jour", "Up to date") + (updater.LastCheck is { } time ? L($" · vérifiée à {time:HH:mm}", $" · checked at {Time(time)}") : ""),
            UpdateState.Failed => L("La recherche n’a pas abouti", "The check didn’t go through"),
            _ => _settings.AutoUpdate ? L("Elle se cherche toute seule", "Checked by itself") : L("Recherche automatique coupée", "Automatic check off"),
        };
        EssentialsUpdateDot.Fill = Palette.Get(ready ? "Text" : updater?.State switch { UpdateState.UpToDate => "Success", UpdateState.Failed => "Warning", _ => "Faint" });
        EssentialsRestart.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        EssentialsRestart.IsEnabled = RestartUpdateButton.IsEnabled;
        EssentialsCheck.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        EssentialsCheck.IsEnabled = CheckUpdatesButton.IsEnabled;
        ShowUpdateDots();
    }

    /// <summary>A version ready to install: a dot on « Mises à jour » and on the rail's gear, said to a screen reader too.</summary>
    private void ShowUpdateDots()
    {
        var ready = _updater?.State == UpdateState.Ready;
        SettingsNavDot.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(SettingsNav, ready ? L("Une nouvelle version de Mira est prête.", "A new version of Mira is ready.") : "");
        if (!_settingsNavDrawn || UpdateSettingsNav.Content is not DockPanel row) return;
        var dot = row.Children.OfType<Ellipse>().FirstOrDefault();
        if (dot is null)
        {
            // The button's own ink: white on the quiet list, dark on the lit one.
            dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            dot.SetBinding(Shape.FillProperty, new Binding(nameof(Button.Foreground)) { Source = UpdateSettingsNav });
            DockPanel.SetDock(dot, Dock.Right); row.Children.Insert(1, dot);
        }
        dot.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(UpdateSettingsNav, ready ? L("Une nouvelle version est prête.", "A new version is ready.") : "");
    }

    /// <summary>A button of « L’essentiel » (and of « Mises à jour ») that opens a section.</summary>
    private void EssentialsGo_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string tab) return;
        SelectSettingsTab(tab);
        _ = Dispatcher.BeginInvoke(() => SettingsPage.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>« Serveur Jellyfin » for an administrator: who watches what is read only while it shows, so it is listed for them alone.</summary>
    private bool _jellyfinAdmin;
    private async Task ShowJellyfinServerAsync()
    {
        if (_demo || InParty || _client is not { } client) { _jellyfinAdmin = false; ShowSettingsGroups(); return; }
        bool admin;
        try { admin = await IsAdministratorAsync(client); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (!ReferenceEquals(client, _client)) return;
        _jellyfinAdmin = admin; ShowSettingsGroups();
    }
}
