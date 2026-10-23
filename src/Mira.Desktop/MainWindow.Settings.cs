using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Playback;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private async Task CloseSettingsAnimatedAsync() { AutoSaveSettings(); await Motion.HideAsync(SettingsOverlay); UpdateNavigation(); UpdateHeroClock(); }
    /// <summary>Leaving the settings keeps what was changed, instead of silently dropping it.</summary>
    private void AutoSaveSettings()
    {
        if (SettingsOverlay.Visibility != Visibility.Visible || _settingsBusy || !SettingsChanged()) return;
        var language = _settings.Language; var saved = ApplySettings();
        // A new language says so itself (ApplySettings), with its button.
        if (_settings.Language == language || !saved) SetNotice(saved ? L("Réglages enregistrés.", "Settings saved.") : SettingsNotSaved);
    }
    /// <summary>Mira's language as the settings show it: the one chosen, else the one Mira speaks (Windows' or the installer's).</summary>
    private string ShownLanguage => Lang.Known(_settings.Language) ?? Lang.Code;
    /// <summary>Said in the language chosen, which Mira reads only when it starts (App.UseLanguage).</summary>
    private void SayLanguage(string language) => SetNotice(language == Lang.French ? "Mira sera en français à sa prochaine ouverture." : "Mira will be in English the next time it opens.",
        language == Lang.French ? "Relancer maintenant" : "Restart now", RestartMira, sticky: false);
    private bool _restartAtClose;
    private void RestartMira() { _restartAtClose = true; Close(); }
    /// <summary>
    /// Writes the settings. A file that cannot be written (full disk, held by another program) is no reason to stop what
    /// Mira was doing: the settings stay applied for this session, and closing Mira writes them again.
    /// </summary>
    private bool TrySaveSettings()
    {
        try { _profile.SaveSettings(_settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Append(_profile.DirectoryPath, ex); return false; }
    }
    private static string SettingsNotSaved => L("Réglages appliqués, mais pas enregistrés : le disque est plein ou le fichier des réglages est utilisé par un autre programme. Mira réessaiera en se fermant.",
        "Settings applied but not saved: the disk is full or the settings file is in use by another program. Mira will try again when it closes.");
    private bool SettingsChanged() =>
        _settings.HardwareDecoding != (HardwareCheck.IsChecked == true) || _settings.AutoNext != (AutoNextCheck.IsChecked == true) || _settings.AutoSkip != (AutoSkipCheck.IsChecked == true) || _settings.RememberPosition != (RememberCheck.IsChecked == true) || _settings.ResumeBack.ToString(CultureInfo.InvariantCulture) != Choice(ResumeBackChoice)
        || _settings.ShowProgress != (ShowProgressCheck.IsChecked == true) || _settings.ReduceMotion != (ReduceMotionCheck.IsChecked == true) || _settings.HeroAutoPlay != (HeroAutoPlayCheck.IsChecked == true)
        || _settings.PosterDensity != Choice(DensityChoice) || _settings.AudioLanguage != AudioLanguageBox.Text.Trim() || _settings.SubtitleLanguage != SubtitleLanguageBox.Text.Trim()
        || _settings.SubtitleSize != (int)SubtitleSizeSlider.Value || _settings.SubtitleBackground != (SubtitleBackgroundCheck.IsChecked == true) || _settings.SubtitleBold != (SubtitleBoldCheck.IsChecked == true)
        || _settings.SubtitleColor != Choice(SubtitleColorChoice) || SubtitleStyle.Height(_settings.SubtitleHeight) != SubtitleHeight() || (_settings.MpvPath != MpvPathBox.Text.Trim() && MpvEngine.FindLibrary(_settings.MpvPath) != MpvPathBox.Text.Trim())
        || _settings.TorLinkAutoImport != (TorLinkAutoImportCheck.IsChecked == true) || _settings.TorLinkKeepShared != (TorLinkKeepSharedCheck.IsChecked == true)
        || _settings.TorLinkRemote != (TorLinkRemoteCheck.IsChecked == true)
        || _settings.TorLinkPath != TorLinkPathBox.Text.Trim() || _settings.TorLinkMoviesFolder != TorLinkMoviesBox.Text.Trim()
        || _settings.TorLinkSeriesFolder != TorLinkSeriesBox.Text.Trim() || _settings.TorLinkAnimeFolder != TorLinkAnimeBox.Text.Trim()
        || _settings.AutoUpdate != (AutoUpdateCheck.IsChecked == true) || _settings.EpisodeNotices != (EpisodeNoticesCheck.IsChecked == true)
        || Choice(MiraLanguageChoice) is { Length: > 0 } language && language != ShownLanguage;
    private bool _settingsBusy;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // The gear clicked again while the page is open refills it from the saved settings: what was changed is kept first.
        AutoSaveSettings();
        ClosePreview(); ++_detailVersion; _returnToDetail = null;
        _settingsBusy = true;
        HardwareCheck.IsChecked = _settings.HardwareDecoding; AutoNextCheck.IsChecked = _settings.AutoNext; AutoSkipCheck.IsChecked = _settings.AutoSkip; RememberCheck.IsChecked = _settings.RememberPosition;
        SelectChoice(ResumeBackChoice, _settings.ResumeBack.ToString(CultureInfo.InvariantCulture));
        ShowProgressCheck.IsChecked = _settings.ShowProgress; ReduceMotionCheck.IsChecked = _settings.ReduceMotion;
        HeroAutoPlayCheck.IsChecked = _settings.HeroAutoPlay; EpisodeNoticesCheck.IsChecked = _settings.EpisodeNotices; ShowHomeRowsSetting(); SelectChoice(MiraLanguageChoice, ShownLanguage);
        AudioLanguageBox.Text = _settings.AudioLanguage; SubtitleLanguageBox.Text = _settings.SubtitleLanguage;
        SelectChoice(AudioLanguageChoice, _settings.AudioLanguage); SelectChoice(SubtitleLanguageChoice, _settings.SubtitleLanguage); SelectChoice(DensityChoice, _settings.PosterDensity);
        SubtitleSizeSlider.Value = _settings.SubtitleSize; ShowSubtitleStyle(); MpvPathBox.Text = MpvEngine.FindLibrary(_settings.MpvPath) ?? _settings.MpvPath;
        ShowEngineState();
        SubtitlePreviewImage.Source = HeroImage.Source;
        TorLinkAutoImportCheck.IsChecked = _settings.TorLinkAutoImport; TorLinkKeepSharedCheck.IsChecked = _settings.TorLinkKeepShared;
        TorLinkRemoteCheck.IsChecked = _settings.TorLinkRemote; DescribeTorLinkRemote();
        TorLinkPathBox.Text = _settings.TorLinkPath; TorLinkMoviesBox.Text = _settings.TorLinkMoviesFolder;
        TorLinkSeriesBox.Text = _settings.TorLinkSeriesFolder; TorLinkAnimeBox.Text = _settings.TorLinkAnimeFolder;
        TorLinkSettingsNav.Visibility = _torlinkEnabled ? Visibility.Visible : Visibility.Collapsed;
        DescribeTorLinkSettings(); _friendShowing = ShowFriendAsync(); _guestShowing = ShowGuestLinksAsync(); _ = ShowApproveDeviceAsync(); _ = ShowSagasAsync(); _ = ShowTrickplaySettingAsync(); _ = ShowGpuAsync(); ShowOfflineSettings();
        AutoUpdateCheck.IsChecked = _settings.AutoUpdate; ShowUpdateState();
        Motion.Reveal(SettingsOverlay, 260, 0); Motion.Reveal(SettingsContent, 260, 10); _ = Motion.HideAsync(DetailOverlay); _ = Motion.HideAsync(GuideOverlay); UpdateHeroClock();
        SettingsSaveStatus.Text = L("Les changements s’appliquent après enregistrement.", "Changes apply once they’re saved.");
        _settingsBusy = false; LanguageChoice_Changed(this, null!);
        SelectSettingsTab("playback"); UpdateNavigation();
        SettingsSaveStatus.Text = L("Enregistré automatiquement en quittant les réglages.", "Saved automatically when you leave Settings.");
        _ = Dispatcher.BeginInvoke(() => PlaybackSettingsNav.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
    private static void SelectChoice(ComboBox box, string value) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == value) ?? box.Items.OfType<ComboBoxItem>().Last();
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => SelectSettingsTab((string)((Button)sender).Tag);
    private void SelectSettingsTab(string tab)
    {
        PlaybackSettings.Visibility = tab == "playback" ? Visibility.Visible : Visibility.Collapsed;
        SubtitleSettings.Visibility = tab == "subtitles" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSettings.Visibility = tab == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        ServerSettings.Visibility = tab == "server" ? Visibility.Visible : Visibility.Collapsed;
        TorLinkSettings.Visibility = tab == "torlink" ? Visibility.Visible : Visibility.Collapsed;
        UpdateSettings.Visibility = tab == "updates" ? Visibility.Visible : Visibility.Collapsed;
        ReportSettings.Visibility = tab == "report" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSectionTitle.Text = tab switch { "subtitles" => L("Audio & sous-titres", "Audio & subtitles"), "appearance" => L("Apparence", "Appearance"), "server" => L("Jellyfin & synchronisation", "Jellyfin & sync"), "torlink" => "TorLink", "updates" => L("Mises à jour", "Updates"), "report" => L("Ton bilan", "Your recap"), _ => L("Lecture", "Playback") };
        SettingsSectionDescription.Text = tab switch
        {
            "subtitles" => L("Les bonnes langues, dès le premier épisode.", "The right languages, from the very first episode."),
            "appearance" => L("Ajuste le confort de navigation à tes habitudes.", "Make browsing work the way you like."),
            "server" => L("Ta bibliothèque et ta progression, toujours à portée de main.", "Your library and your progress, always at hand."),
            "torlink" => L("Tes téléchargements rangés dans Jellyfin, sans rien déplacer à la main.", "Your downloads filed in Jellyfin, without moving anything by hand."),
            "updates" => L("Les nouvelles versions de Mira, installées sans y penser.", "New versions of Mira, installed without a thought."),
            "report" => L("Ce que tu as regardé, sur tous tes appareils.", "What you’ve watched, on all your devices."),
            _ => L("Le confort de lecture, sans avoir à y penser.", "Comfortable playback, without having to think about it.")
        };
        foreach (var button in SettingsNavigation.Children.OfType<Button>()) { var active = button.Tag?.ToString() == tab; button.Background = active ? Palette.Get("Text") : Brushes.Transparent; button.Foreground = active ? Palette.Get("OnAccent") : Palette.Get("TextBody"); }
        SettingsScroll.ScrollToTop();
        var body = tab switch { "subtitles" => SubtitleSettings, "appearance" => AppearanceSettings, "server" => ServerSettings, "torlink" => TorLinkSettings, "updates" => UpdateSettings, "report" => ReportSettings, _ => PlaybackSettings };
        SmoothScroll.Jump(SettingsScroll); Motion.Reveal(body, 230, 7);
        WatchViewers(tab == "server");
        if (tab == "server") _ = ShowFailuresAsync();
        if (tab == "report") _ = ShowReportAsync();
    }
    private void LanguageChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settingsBusy || CustomLanguages is null) return;
        if (Choice(AudioLanguageChoice) is { Length: > 0 } audio && audio != "custom") AudioLanguageBox.Text = audio;
        if (Choice(SubtitleLanguageChoice) is { Length: > 0 } subs && subs != "custom") SubtitleLanguageBox.Text = subs;
        CustomLanguages.Visibility = Choice(AudioLanguageChoice) == "custom" || Choice(SubtitleLanguageChoice) == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SubtitleSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (SubtitleSizeLabel is null) return; SubtitleSizeLabel.Text = $"{e.NewValue:0}"; PreviewSubtitles(); }
    private void SubtitleStyle_Changed(object sender, RoutedEventArgs e) => PreviewSubtitles();
    /// <summary>The saved style in the page: the heights come from Core, with the Mac's.</summary>
    private void ShowSubtitleStyle()
    {
        if (SubtitleHeightChoice.Items.Count == 0)
            foreach (var (position, label) in SubtitleStyle.Heights) SubtitleHeightChoice.Items.Add(new ComboBoxItem { Content = label, Tag = position.ToString(CultureInfo.InvariantCulture) });
        SubtitleBackgroundCheck.IsChecked = _settings.SubtitleBackground; SubtitleBoldCheck.IsChecked = _settings.SubtitleBold;
        SelectChoice(SubtitleColorChoice, _settings.SubtitleColor == SubtitleStyle.Yellow ? SubtitleStyle.Yellow : SubtitleStyle.White);
        SubtitleHeightChoice.SelectedItem = SubtitleHeightChoice.Items.OfType<ComboBoxItem>().First(x => (string)x.Tag == SubtitleStyle.Height(_settings.SubtitleHeight).ToString(CultureInfo.InvariantCulture));
        PreviewSubtitles();
    }
    private int SubtitleHeight() => int.TryParse(Choice(SubtitleHeightChoice), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ? height : 100;
    private System.Windows.Media.Effects.Effect? _subtitleOutline;
    /// <summary>
    /// The preview as mpv draws a plain text subtitle: its colour, bold or not, an outline or the dark box (mpv's margin,
    /// at the preview's scale), and its height (mpv's sub-pos, a share of the picture's height).
    /// </summary>
    private void PreviewSubtitles()
    {
        if (SubtitlePreview is null || SubtitlePreviewBox is null || SubtitleBackgroundCheck is null || SubtitleBoldCheck is null || SubtitleColorChoice is null || SubtitleHeightChoice is null) return;
        _subtitleOutline ??= SubtitlePreview.Effect;
        var size = SubtitleSizeSlider.Value * .65; var box = SubtitleBackgroundCheck.IsChecked == true;
        SubtitlePreview.FontSize = size;
        SubtitlePreview.Foreground = Brush(SubtitleStyle.Color(Choice(SubtitleColorChoice)));
        SubtitlePreview.FontWeight = SubtitleBoldCheck.IsChecked == true ? FontWeights.Bold : FontWeights.SemiBold;
        SubtitlePreview.Effect = box ? null : _subtitleOutline;
        SubtitlePreviewBox.Background = box ? Brush(SubtitleStyle.Box) : Brushes.Transparent;
        SubtitlePreviewBox.Padding = box ? new Thickness(size * .3, size * .08, size * .3, size * .12) : new Thickness(0);
        SubtitlePreviewBox.Margin = new Thickness(20, 0, 20, 27 + (100 - SubtitleHeight()) / 100.0 * 165);
    }
    private void BrowseMpv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L("Sélectionner le moteur mpv 64 bits", "Select the 64-bit mpv engine"), Filter = L("Bibliothèque mpv|mpv-2.dll;libmpv-2.dll;mpv-1.dll|Bibliothèques DLL|*.dll", "mpv library|mpv-2.dll;libmpv-2.dll;mpv-1.dll|DLL libraries|*.dll") };
        if (dialog.ShowDialog(this) != true) return;
        MpvPathBox.Text = dialog.FileName;
        EngineStatus.Text = L("Moteur sélectionné · il sera utilisé à la prochaine lecture après enregistrement.", "Engine selected · it will be used from the next playback, once saved.");
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e) => ApplySettings();
    /// <summary>What the page shows becomes Mira's settings; false when they could not be written.</summary>
    private bool ApplySettings()
    {
        _settings.HardwareDecoding = HardwareCheck.IsChecked == true; _settings.AutoNext = AutoNextCheck.IsChecked == true; _settings.AutoSkip = AutoSkipCheck.IsChecked == true; _settings.RememberPosition = RememberCheck.IsChecked == true;
        _settings.ResumeBack = int.TryParse(Choice(ResumeBackChoice), NumberStyles.Integer, CultureInfo.InvariantCulture, out var back) ? back : 5;
        _settings.ShowProgress = ShowProgressCheck.IsChecked == true; _settings.ReduceMotion = ReduceMotionCheck.IsChecked == true; _settings.PosterDensity = Choice(DensityChoice);
        _settings.HeroAutoPlay = HeroAutoPlayCheck.IsChecked == true; Motion.Reduced = _settings.ReduceMotion;
        // Turned on again: from what Jellyfin has now, not every episode that came while it was off.
        if (!_settings.EpisodeNotices && EpisodeNoticesCheck.IsChecked == true) _settings.EpisodesSeenFor = "";
        _settings.EpisodeNotices = EpisodeNoticesCheck.IsChecked == true; if (_settings.EpisodeNotices) EpisodesMayHaveArrived();
        _settings.AudioLanguage = AudioLanguageBox.Text.Trim(); _settings.SubtitleLanguage = SubtitleLanguageBox.Text.Trim();
        _settings.SubtitleSize = (int)SubtitleSizeSlider.Value; _settings.SubtitleBackground = SubtitleBackgroundCheck.IsChecked == true; _settings.SubtitleBold = SubtitleBoldCheck.IsChecked == true;
        _settings.SubtitleColor = Choice(SubtitleColorChoice) is { Length: > 0 } colour ? colour : SubtitleStyle.White; _settings.SubtitleHeight = SubtitleHeight(); _settings.MpvPath = MpvPathBox.Text.Trim();
        // Switching automatic placement back on starts from now: downloads finished meanwhile stay listed, to import by hand.
        var automaticAgain = !_settings.TorLinkAutoImport && TorLinkAutoImportCheck.IsChecked == true;
        _settings.TorLinkAutoImport = TorLinkAutoImportCheck.IsChecked == true; _settings.TorLinkKeepShared = TorLinkKeepSharedCheck.IsChecked == true;
        // Changed: said in Réglages and to the relay at once, not at the next visit.
        var remoteChanged = _settings.TorLinkRemote != (TorLinkRemoteCheck.IsChecked == true);
        _settings.TorLinkRemote = TorLinkRemoteCheck.IsChecked == true;
        _settings.TorLinkPath = TorLinkPathBox.Text.Trim(); _settings.TorLinkMoviesFolder = TorLinkMoviesBox.Text.Trim();
        _settings.TorLinkSeriesFolder = TorLinkSeriesBox.Text.Trim(); _settings.TorLinkAnimeFolder = TorLinkAnimeBox.Text.Trim();
        // Switched back on: look for a new version now rather than at the next scheduled check.
        var updatesAgain = !_settings.AutoUpdate && AutoUpdateCheck.IsChecked == true;
        _settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        var language = Choice(MiraLanguageChoice) is { Length: > 0 } picked && picked != ShownLanguage ? picked : null;
        if (language is not null) _settings.Language = language;
        var saved = TrySaveSettings();
        if (language is not null && saved) SayLanguage(language);
        ChoicesChanged();
        if (updatesAgain) _ = _updater?.CheckAsync();
        if (automaticAgain) _torlinkImporter?.RestartBaseline();
        if (_torlinkEnabled) ApplyTorLinkLibraries();
        if (remoteChanged) VisitRelaySoon();
        _mpv?.Set("hwdec", _settings.HardwareDecoding ? "auto" : "no"); _mpv?.StyleSubtitles(_settings);
        RenderLibrary(); UpdateHeroClock(); SettingsSaveStatus.Text = saved ? L("Préférences enregistrées.", "Preferences saved.") : SettingsNotSaved;
        return saved;
    }
    /// <summary>What Mira found: the TorLink installation and the library folders read from Jellyfin.</summary>
    private void DescribeTorLinkSettings()
    {
        var installation = TorLink.TorLinkInstallation.Locate(TorLinkPathBox.Text, _profile.DirectoryPath);
        TorLinkPathStatus.Text = installation is null
            ? L("Pas installé : active TorLink dans la page Téléchargements.", "Not installed: turn TorLink on in the Downloads page.")
            : installation.Managed ? L($"TorLink trouvé : version {installation.Version}, installée par Mira.", $"TorLink found: version {installation.Version}, installed by Mira.")
            : L($"TorLink trouvé : {installation.Root}", $"TorLink found: {installation.Root}") + (installation.HasRuntime ? "." : L(" · Node.js 22 ou plus récent est nécessaire.", " · Node.js 22 or later is required."))
              + (installation.Detected ? L(" Mira te demandera de confirmer que c’est le tien avant de le lancer.", " Mira will ask you to confirm it’s yours before starting it.") : "")
              + (TorLinkPathBox.Text.Trim().Length == 0 ? L(" Laisse vide pour le retrouver automatiquement.", " Leave it empty to find it automatically.") : "");
        string Folder(string? path) => path ?? L("non disponible", "not available");
        var detected = _torlink?.Detected ?? new MediaLibraries(null, null, null);
        TorLinkFoldersStatus.Text = _demo
            ? L("En démonstration, Jellyfin n’est pas consulté : seuls les dossiers choisis ici reçoivent les téléchargements.", "In the demo, Jellyfin isn’t asked: only the folders chosen here receive downloads.")
            : detected.IsEmpty
            ? L("Laisse vide pour utiliser les dossiers de tes bibliothèques Jellyfin. Ils n’ont pas pu être lus : il faut un compte administrateur et un serveur sur ce PC. Choisis-les ici sinon.", "Leave them empty to use the folders of your Jellyfin libraries. They couldn’t be read: that takes an administrator account and a server on this PC. Otherwise, choose them here.")
            : L($"Laisse vide pour utiliser les dossiers de Jellyfin : Films → {Folder(detected.Movies)} · Séries → {Folder(detected.Series)} · Animes → {Folder(detected.Anime ?? detected.Series)}.",
                $"Leave them empty to use Jellyfin’s folders: Movies → {Folder(detected.Movies)} · Shows → {Folder(detected.Series)} · Anime → {Folder(detected.Anime ?? detected.Series)}.");
    }
    private void BrowseTorLink_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseTorLinkFolder() is not { } root) return;
        TorLinkPathBox.Text = root; DescribeTorLinkSettings();
    }
    private void BrowseTorLinkFolder_Click(object sender, RoutedEventArgs e)
    {
        var target = ((Button)sender).Tag?.ToString() switch { "movies" => TorLinkMoviesBox, "series" => TorLinkSeriesBox, _ => TorLinkAnimeBox };
        var dialog = new OpenFolderDialog { Title = L("Dossier de la bibliothèque Jellyfin", "Jellyfin library folder") };
        if (Directory.Exists(target.Text.Trim())) dialog.InitialDirectory = target.Text.Trim();
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }
    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    { await RefreshAsync(quiet: true); if (_demo) SettingsSaveStatus.Text = L("La démonstration utilise des données fictives.", "The demo uses made-up data."); else SettingsSaveStatus.Text = L("Actualisation demandée.", "Refresh requested."); }
    /// <summary>
    /// « Autoriser un appareil »: shown once Jellyfin says it lets devices in by a code (Quick Connect). An
    /// administrator with other accounts on Jellyfin chooses which one the device joins (<paramref name="select"/>).
    /// </summary>
    private async Task ShowApproveDeviceAsync(string? select = null)
    {
        ApproveDeviceRow.Visibility = Visibility.Collapsed; ApproveCodeBox.Clear(); ApproveDeviceStatus.Text = "";
        if (_demo || _client is not { } client) return;
        bool enabled;
        try { enabled = await client.QuickConnectEnabledAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (!enabled || client != _client) return;
        List<AuthUser>? users = null;
        try { if (await client.IsAdministratorAsync() == true) users = await client.UsersAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { }
        if (client != _client) return;
        ShowApproveAccounts(client, users ?? [], select);
        ApproveDeviceRow.Visibility = Visibility.Visible;
    }
    private static string ApproveOwn => L("Sur ton téléphone, « Se connecter avec un code » affiche 6 chiffres : saisis-les ici pour le connecter à ton compte, sans y taper ton mot de passe. N’autorise qu’un appareil que tu as sous les yeux.",
        "On your phone, “Sign in with a code” shows 6 digits: enter them here to sign it in to your account, without typing your password on it. Only allow a device you can see in front of you.");
    private static string ApproveAny => L("Sur un téléphone, « Se connecter avec un code » affiche 6 chiffres : choisis le compte à y ouvrir, le tien ou celui d’un ami, puis saisis-les ici. Aucun mot de passe à taper. N’autorise que les chiffres d’un appareil que tu connais.",
        "On a phone, “Sign in with a code” shows 6 digits: choose the account to open on it, yours or a friend’s, then enter them here. No password to type. Only allow the digits of a device you know.");
    private void ShowApproveAccounts(JellyfinClient client, IReadOnlyList<AuthUser> users, string? select)
    {
        var others = users.Where(x => !string.Equals(x.Id, client.Connection.UserId, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ApproveAccount.Items.Clear();
        ApproveAccount.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApproveDeviceText.Text = others.Count > 0 ? ApproveAny : ApproveOwn;
        if (others.Count == 0) return;
        ApproveAccount.Items.Add(new ComboBoxItem { Content = L("Ton compte", "Your account"), Tag = client.Connection.UserId });
        foreach (var user in others) ApproveAccount.Items.Add(new ComboBoxItem { Content = user.Name, Tag = user.Id });
        SelectApproveAccount(select);
    }
    private void SelectApproveAccount(string? id) => ApproveAccount.SelectedItem =
        ApproveAccount.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag as string, id, StringComparison.OrdinalIgnoreCase)) ?? ApproveAccount.Items.OfType<ComboBoxItem>().FirstOrDefault();
    private bool _sagasBusy;
    /// <summary>
    /// « Sagas de films »: for an administrator, whether Jellyfin gathers films into sagas (the saga row of a film page),
    /// with a button while it does not. Hidden when Mira can neither tell nor change it.
    /// </summary>
    private async Task ShowSagasAsync()
    {
        if (_sagasBusy) return;
        SagasRow.Visibility = Visibility.Collapsed; SagasStatus.Text = "";
        if (_demo || _client is not { } client) return;
        bool? sagas;
        try { sagas = await client.SagasAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (client != _client || sagas is null || _sagasBusy) return;
        SagasButton.Visibility = sagas == true ? Visibility.Collapsed : Visibility.Visible;
        SagasStatus.Text = sagas == true ? SagasOn : "";
        SagasRow.Visibility = Visibility.Visible;
    }
    private static string SagasOn => L("Activé : Jellyfin range tes films en sagas à chaque analyse de la bibliothèque. Pour l’arrêter : Tableau de bord de Jellyfin → Médiathèques → Films → « Ajouter automatiquement à la collection ».",
        "On: Jellyfin groups your movies into collections at each library scan. To stop it: Jellyfin’s Dashboard → Libraries → Movies → “Automatically add to collection”.");
    /// <summary>Turns the option on in every film library, then has Jellyfin scan: the sagas exist at the end of the scan.</summary>
    private async void Sagas_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _sagasBusy) return;
        _sagasBusy = true; SagasButton.IsEnabled = false; SagasStatus.Text = L("Réglage de Jellyfin…", "Changing Jellyfin’s settings…");
        var ct = _connectionLifetime.Token;
        try
        {
            if (!await client.EnableSagasAsync(ct)) { SagasStatus.Text = L("Seul un administrateur de Jellyfin peut changer ce réglage.", "Only a Jellyfin administrator can change this setting."); return; }
            var progress = new Progress<double?>(p => SagasStatus.Text = p is { } share ? L($"Jellyfin range tes films… {share * 100:0} %", $"Jellyfin is grouping your movies… {share * 100:0}%") : L("Jellyfin range tes films…", "Jellyfin is grouping your movies…"));
            await client.ScanAndWaitAsync(progress, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), ct);
            var count = await client.SagaCountAsync(ct);
            SagasButton.Visibility = Visibility.Collapsed;
            SagasStatus.Text = count > 0 ? L($"{count} saga{(count > 1 ? "s" : "")} dans ta bibliothèque : la fiche de leurs films les montre. Jellyfin range les suivantes à chaque analyse.",
                    $"{count} {(count == 1 ? "collection" : "collections")} in your library: the pages of their movies show them. Jellyfin groups the next ones at each scan.")
                : L("Activé. Aucune saga pour l’instant : Jellyfin en crée une dès que deux films d’une même saga sont dans ta bibliothèque.", "On. No collections yet: Jellyfin makes one as soon as two movies of the same collection are in your library.");
        }
        // Only Mira's own cancellation (another account, closing): HttpClient's 12 s limit is one too, and must be said.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SagasStatus.Text = Friendly(ex); }
        finally { _sagasBusy = false; SagasButton.IsEnabled = true; }
    }
    private bool _trickplayBusy;
    /// <summary>
    /// « Aperçus de la barre de lecture »: for an administrator, whether Jellyfin makes the trickplay pictures, how far its
    /// task is, and a button while it does not. Hidden when Mira can neither tell nor change it.
    /// </summary>
    private async Task ShowTrickplaySettingAsync()
    {
        if (_trickplayBusy) return;
        TrickplayRow.Visibility = Visibility.Collapsed; TrickplayStatus.Text = "";
        if (_demo || _client is not { } client) return;
        bool? enabled; double? progress = null;
        try { enabled = await client.TrickplayEnabledAsync(); if (enabled == true) progress = await client.TrickplayProgressAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (client != _client || enabled is null || _trickplayBusy) return;
        TrickplayButton.Visibility = enabled == true ? Visibility.Collapsed : Visibility.Visible;
        TrickplayStatus.Text = enabled != true ? "" : progress is { } share ? TrickplayRunning(share) : TrickplayOn;
        TrickplayRow.Visibility = Visibility.Visible;
    }
    private static string TrickplayRunning(double share) => L($"Jellyfin prépare les aperçus… {share * 100:0} %. Ceux des titres déjà faits apparaissent dès maintenant.", $"Jellyfin is making the seek previews… {share * 100:0}%. Titles already done have theirs now.");
    private static string TrickplayOn => L("Activé : Jellyfin prépare les aperçus des nouveaux titres chaque nuit. Pour l’arrêter : Tableau de bord de Jellyfin → Médiathèques → la bibliothèque → images trickplay.",
        "On: Jellyfin makes the seek previews of new titles every night. To stop it: Jellyfin’s Dashboard → Libraries → the library → trickplay image extraction.");
    /// <summary>Turns the option on in the film and series libraries and starts Jellyfin's task, which goes on alone.</summary>
    private async void Trickplay_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _trickplayBusy) return;
        _trickplayBusy = true; TrickplayButton.IsEnabled = false; TrickplayStatus.Text = L("Réglage de Jellyfin…", "Changing Jellyfin’s settings…");
        var ct = _connectionLifetime.Token;
        try
        {
            if (!await client.EnableTrickplayAsync(ct)) { TrickplayStatus.Text = L("Seul un administrateur de Jellyfin peut changer ce réglage.", "Only a Jellyfin administrator can change this setting."); return; }
            TrickplayButton.Visibility = Visibility.Collapsed;
            TrickplayStatus.Text = await client.TrickplayProgressAsync(ct) is { } share ? TrickplayRunning(share) : TrickplayOn;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { TrickplayStatus.Text = Friendly(ex); }
        finally { _trickplayBusy = false; TrickplayButton.IsEnabled = true; }
    }
    private bool _gpuBusy;
    private IReadOnlyList<GpuEncoding.Card> _gpuCards = [];
    /// <summary>
    /// « Conversions par la carte graphique »: for an administrator of a Jellyfin on this PC (Mira reads this PC's cards,
    /// not the server's), what Jellyfin converts with, and a button for a card it could use. Hidden otherwise.
    /// </summary>
    private async Task ShowGpuAsync()
    {
        if (_gpuBusy) return;
        GpuRow.Visibility = Visibility.Collapsed; GpuStatus.Text = "";
        if (_demo || _client is not { } client) return;
        string? kind;
        try
        {
            var server = new Uri(client.Connection.Server);
            if (!server.IsLoopback && !await Task.Run(() => LocalNetwork.IsThisPc(server.Host))) return;
            _gpuCards = GpuEncoding.Candidates(await Task.Run(GraphicsCards.Names));
            kind = await client.HardwareAccelerationAsync();
        }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (client != _client || kind is null || _gpuBusy) return;
        var on = kind != GpuEncoding.None;
        if (!on && _gpuCards.Count == 0) return;
        ShowGpuState(on ? L($"Activé : Jellyfin convertit avec la carte graphique ({GpuEncoding.Maker(kind)}).", $"On: Jellyfin converts with the graphics card ({GpuEncoding.Maker(kind)}).") : "", on);
        GpuRow.Visibility = Visibility.Visible;
    }
    private void ShowGpuState(string status, bool on)
    {
        GpuStatus.Text = status;
        GpuButton.Visibility = on || _gpuCards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        GpuOffButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>Tries each card of this PC on a title, keeps the first that converts it, or puts Jellyfin back as it was.</summary>
    private async void Gpu_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _gpuBusy || _gpuCards.Count == 0) return;
        _gpuBusy = true; GpuButton.IsEnabled = false; GpuStatus.Text = L("Essai de conversion sur la carte graphique…", "Trying a conversion on the graphics card…");
        var ct = _connectionLifetime.Token;
        try
        {
            var (outcome, card) = await GpuEncoding.EnableAsync(client, _gpuCards, TimeSpan.FromSeconds(30), ct);
            switch (outcome)
            {
                case GpuOutcome.Done: ShowGpuState(L($"Activé : ta {card!.Name} convertit les vidéos pour ton téléphone.", $"On: your {card!.Name} converts videos for your phone."), true); _ = RememberGpuCheckedAsync(client); break;
                case GpuOutcome.Refused: GpuStatus.Text = L("Seul un administrateur de Jellyfin peut changer ce réglage.", "Only a Jellyfin administrator can change this setting."); break;
                case GpuOutcome.NothingToTry: GpuStatus.Text = L("Ajoute d’abord un film ou un épisode à ta bibliothèque : Mira essaie la carte sur l’un d’eux.", "Add a movie or an episode to your library first: Mira tries the card on one of them."); break;
                default: GpuStatus.Text = L("La carte graphique n’a pas réussi l’essai : Jellyfin continue avec le processeur. Mettre à jour son pilote peut suffire, puis réessaie.", "The graphics card failed the test: Jellyfin keeps converting with the processor. Updating its driver may be enough; then try again."); break;
            }
        }
        // Only Mira's own cancellation (another account, closing): HttpClient's 12 s limit is one too, and must be said.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { GpuStatus.Text = Friendly(ex); }
        finally { _gpuBusy = false; GpuButton.IsEnabled = true; }
    }
    private async void GpuOff_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _gpuBusy) return;
        _gpuBusy = true; GpuOffButton.IsEnabled = false;
        var ct = _connectionLifetime.Token;
        try
        {
            if (await client.SetHardwareAccelerationAsync(GpuEncoding.None, ct)) ShowGpuState(L("Jellyfin convertit de nouveau avec le processeur.", "Jellyfin converts with the processor again."), false);
            else GpuStatus.Text = L("Seul un administrateur de Jellyfin peut changer ce réglage.", "Only a Jellyfin administrator can change this setting.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { GpuStatus.Text = Friendly(ex); }
        finally { _gpuBusy = false; GpuOffButton.IsEnabled = true; }
    }
    private void ApproveCode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; ApproveDevice_Click(sender, e); } }
    /// <summary>The code another device shows (Mira web on a phone), approved for this account or the one chosen.</summary>
    private async void ApproveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || !ApproveDeviceButton.IsEnabled) return;
        if (JellyfinClient.QuickConnectCode(ApproveCodeBox.Text) is not { } code) { ApproveDeviceStatus.Text = L("Le code a 6 chiffres.", "The code has 6 digits."); ApproveCodeBox.Focus(); ApproveCodeBox.SelectAll(); return; }
        (string? Id, string? Name) account = ApproveAccount.Visibility == Visibility.Visible && ApproveAccount.SelectedItem is ComboBoxItem { Tag: string id } chosen
            && !string.Equals(id, client.Connection.UserId, StringComparison.OrdinalIgnoreCase) ? (id, chosen.Content as string) : (null, null);
        ApproveDeviceButton.IsEnabled = false; ApproveDeviceStatus.Text = L("Autorisation…", "Allowing…");
        try
        {
            var approval = await client.ApproveCodeAsync(code, account.Id);
            ApproveDeviceStatus.Text = approval switch
            {
                CodeApproval.Approved => account.Id is null ? L("Appareil autorisé : il se connecte à ton compte.", "Device allowed: it’s signing in to your account.")
                    : L($"Appareil autorisé : il se connecte au compte de {account.Name}.", $"Device allowed: it’s signing in to {account.Name}’s account."),
                CodeApproval.Unknown => L("Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.", "Unknown or expired code. Check it, or ask for a new one on the device."),
                _ => L("Quick Connect est désactivé sur ce serveur : active-le dans le tableau de bord de Jellyfin (Général).", "Quick Connect is off on this server: turn it on in Jellyfin’s Dashboard (General)."),
            };
            if (approval == CodeApproval.Approved) ApproveCodeBox.Clear(); else { ApproveCodeBox.Focus(); ApproveCodeBox.SelectAll(); }
        }
        catch (Exception ex) when (IsExpected(ex)) { ApproveDeviceStatus.Text = Friendly(ex); }
        finally { ApproveDeviceButton.IsEnabled = true; }
    }
}
