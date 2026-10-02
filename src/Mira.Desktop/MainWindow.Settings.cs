using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Mira.Desktop.Playback;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private async Task CloseSettingsAnimatedAsync() { AutoSaveSettings(); await Motion.HideAsync(SettingsOverlay); UpdateNavigation(); UpdateHeroClock(); }
    /// <summary>Leaving the settings keeps what was changed, instead of silently dropping it.</summary>
    private void AutoSaveSettings()
    {
        if (SettingsOverlay.Visibility != Visibility.Visible || _settingsBusy || !SettingsChanged()) return;
        SaveSettings_Click(this, new()); SetNotice("Réglages enregistrés.");
    }
    private bool SettingsChanged() =>
        _settings.HardwareDecoding != (HardwareCheck.IsChecked == true) || _settings.AutoNext != (AutoNextCheck.IsChecked == true) || _settings.RememberPosition != (RememberCheck.IsChecked == true)
        || _settings.ShowProgress != (ShowProgressCheck.IsChecked == true) || _settings.ReduceMotion != (ReduceMotionCheck.IsChecked == true) || _settings.HeroAutoPlay != (HeroAutoPlayCheck.IsChecked == true)
        || _settings.PosterDensity != Choice(DensityChoice) || _settings.AudioLanguage != AudioLanguageBox.Text.Trim() || _settings.SubtitleLanguage != SubtitleLanguageBox.Text.Trim()
        || _settings.SubtitleSize != (int)SubtitleSizeSlider.Value || (_settings.MpvPath != MpvPathBox.Text.Trim() && MpvEngine.FindLibrary(_settings.MpvPath) != MpvPathBox.Text.Trim())
        || _settings.TorLinkAutoImport != (TorLinkAutoImportCheck.IsChecked == true) || _settings.TorLinkKeepShared != (TorLinkKeepSharedCheck.IsChecked == true)
        || _settings.TorLinkPath != TorLinkPathBox.Text.Trim() || _settings.TorLinkMoviesFolder != TorLinkMoviesBox.Text.Trim()
        || _settings.TorLinkSeriesFolder != TorLinkSeriesBox.Text.Trim() || _settings.TorLinkAnimeFolder != TorLinkAnimeBox.Text.Trim()
        || _settings.AutoUpdate != (AutoUpdateCheck.IsChecked == true);
    private bool _settingsBusy;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ClosePreview(); ++_detailVersion; _returnToDetail = null;
        _settingsBusy = true;
        HardwareCheck.IsChecked = _settings.HardwareDecoding; AutoNextCheck.IsChecked = _settings.AutoNext; RememberCheck.IsChecked = _settings.RememberPosition;
        ShowProgressCheck.IsChecked = _settings.ShowProgress; ReduceMotionCheck.IsChecked = _settings.ReduceMotion;
        HeroAutoPlayCheck.IsChecked = _settings.HeroAutoPlay;
        AudioLanguageBox.Text = _settings.AudioLanguage; SubtitleLanguageBox.Text = _settings.SubtitleLanguage;
        SelectChoice(AudioLanguageChoice, _settings.AudioLanguage); SelectChoice(SubtitleLanguageChoice, _settings.SubtitleLanguage); SelectChoice(DensityChoice, _settings.PosterDensity);
        SubtitleSizeSlider.Value = _settings.SubtitleSize; MpvPathBox.Text = MpvEngine.FindLibrary(_settings.MpvPath) ?? _settings.MpvPath;
        ShowEngineState();
        SubtitlePreviewImage.Source = HeroImage.Source;
        TorLinkAutoImportCheck.IsChecked = _settings.TorLinkAutoImport; TorLinkKeepSharedCheck.IsChecked = _settings.TorLinkKeepShared;
        TorLinkPathBox.Text = _settings.TorLinkPath; TorLinkMoviesBox.Text = _settings.TorLinkMoviesFolder;
        TorLinkSeriesBox.Text = _settings.TorLinkSeriesFolder; TorLinkAnimeBox.Text = _settings.TorLinkAnimeFolder;
        TorLinkSettingsNav.Visibility = _torlinkEnabled ? Visibility.Visible : Visibility.Collapsed;
        DescribeTorLinkSettings();
        AutoUpdateCheck.IsChecked = _settings.AutoUpdate; ShowUpdateState();
        Motion.Reveal(SettingsOverlay, 260, 0); Motion.Reveal(SettingsContent, 260, 10); _ = Motion.HideAsync(DetailOverlay); _ = Motion.HideAsync(GuideOverlay); UpdateHeroClock();
        SettingsSaveStatus.Text = "Les changements s’appliquent après enregistrement.";
        _settingsBusy = false; LanguageChoice_Changed(this, null!);
        SelectSettingsTab("playback"); UpdateNavigation();
        SettingsSaveStatus.Text = "Enregistré automatiquement en quittant les réglages.";
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
        SettingsSectionTitle.Text = tab switch { "subtitles" => "Audio & sous-titres", "appearance" => "Apparence", "server" => "Jellyfin & synchronisation", "torlink" => "TorLink", "updates" => "Mises à jour", _ => "Lecture" };
        SettingsSectionDescription.Text = tab switch { "subtitles" => "Les bonnes langues, dès le premier épisode.", "appearance" => "Ajuste le confort de navigation à tes habitudes.", "server" => "Ta bibliothèque et ta progression, toujours à portée de main.", "torlink" => "Tes téléchargements rangés dans Jellyfin, sans rien déplacer à la main.", "updates" => "Les nouvelles versions de Mira, installées sans y penser.", _ => "Le confort de lecture, sans avoir à y penser." };
        foreach (var button in SettingsNavigation.Children.OfType<Button>()) { var active = button.Tag?.ToString() == tab; button.Background = active ? Brush("#F4F4F5") : Brushes.Transparent; button.Foreground = active ? Brush("#161618") : Brush("#D0D0D5"); }
        SettingsScroll.ScrollToTop();
        var body = tab switch { "subtitles" => SubtitleSettings, "appearance" => AppearanceSettings, "server" => ServerSettings, "torlink" => TorLinkSettings, "updates" => UpdateSettings, _ => PlaybackSettings };
        SmoothScroll.Jump(SettingsScroll); Motion.Reveal(body, 230, 7);
    }
    private void LanguageChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settingsBusy || CustomLanguages is null) return;
        if (Choice(AudioLanguageChoice) is { Length: > 0 } audio && audio != "custom") AudioLanguageBox.Text = audio;
        if (Choice(SubtitleLanguageChoice) is { Length: > 0 } subs && subs != "custom") SubtitleLanguageBox.Text = subs;
        CustomLanguages.Visibility = Choice(AudioLanguageChoice) == "custom" || Choice(SubtitleLanguageChoice) == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SubtitleSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (SubtitlePreview is null) return; SubtitlePreview.FontSize = e.NewValue * .65; SubtitleSizeLabel.Text = $"{e.NewValue:0}"; }
    private void BrowseMpv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Sélectionner le moteur mpv 64 bits", Filter = "Bibliothèque mpv|mpv-2.dll;libmpv-2.dll;mpv-1.dll|Bibliothèques DLL|*.dll" };
        if (dialog.ShowDialog(this) != true) return;
        MpvPathBox.Text = dialog.FileName;
        EngineStatus.Text = "Moteur sélectionné · il sera utilisé à la prochaine lecture après enregistrement.";
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.HardwareDecoding = HardwareCheck.IsChecked == true; _settings.AutoNext = AutoNextCheck.IsChecked == true; _settings.RememberPosition = RememberCheck.IsChecked == true;
        _settings.ShowProgress = ShowProgressCheck.IsChecked == true; _settings.ReduceMotion = ReduceMotionCheck.IsChecked == true; _settings.PosterDensity = Choice(DensityChoice);
        _settings.HeroAutoPlay = HeroAutoPlayCheck.IsChecked == true; Motion.Reduced = _settings.ReduceMotion;
        _settings.AudioLanguage = AudioLanguageBox.Text.Trim(); _settings.SubtitleLanguage = SubtitleLanguageBox.Text.Trim();
        _settings.SubtitleSize = (int)SubtitleSizeSlider.Value; _settings.MpvPath = MpvPathBox.Text.Trim();
        // Switching automatic placement back on starts from now: downloads finished meanwhile stay listed, to import by hand.
        var automaticAgain = !_settings.TorLinkAutoImport && TorLinkAutoImportCheck.IsChecked == true;
        _settings.TorLinkAutoImport = TorLinkAutoImportCheck.IsChecked == true; _settings.TorLinkKeepShared = TorLinkKeepSharedCheck.IsChecked == true;
        _settings.TorLinkPath = TorLinkPathBox.Text.Trim(); _settings.TorLinkMoviesFolder = TorLinkMoviesBox.Text.Trim();
        _settings.TorLinkSeriesFolder = TorLinkSeriesBox.Text.Trim(); _settings.TorLinkAnimeFolder = TorLinkAnimeBox.Text.Trim();
        // Switched back on: look for a new version now rather than at the next scheduled check.
        var updatesAgain = !_settings.AutoUpdate && AutoUpdateCheck.IsChecked == true;
        _settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        _profile.SaveSettings(_settings);
        if (updatesAgain) _ = _updater?.CheckAsync();
        if (automaticAgain) _torlinkImporter?.RestartBaseline();
        if (_torlinkEnabled) ApplyTorLinkLibraries();
        _mpv?.Set("hwdec", _settings.HardwareDecoding ? "auto" : "no"); _mpv?.Set("sub-font-size", _settings.SubtitleSize.ToString(CultureInfo.InvariantCulture));
        RenderLibrary(); UpdateHeroClock(); SettingsSaveStatus.Text = "Préférences enregistrées.";
    }
    /// <summary>What Mira found: the TorLink installation and the library folders read from Jellyfin.</summary>
    private void DescribeTorLinkSettings()
    {
        var installation = TorLink.TorLinkInstallation.Locate(TorLinkPathBox.Text, _profile.DirectoryPath);
        TorLinkPathStatus.Text = installation is null
            ? "Pas installé : active TorLink dans la page Téléchargements."
            : installation.Managed ? $"TorLink trouvé : version {installation.Version}, installée par Mira."
            : $"TorLink trouvé : {installation.Root}" + (installation.HasRuntime ? "." : " · Node.js 22 ou plus récent est nécessaire.")
              + (TorLinkPathBox.Text.Trim().Length == 0 ? " Laisse vide pour le retrouver automatiquement." : "");
        string Folder(string? path) => path ?? "non disponible";
        var detected = _torlinkDetected;
        TorLinkFoldersStatus.Text = _demo
            ? "En démonstration, Jellyfin n’est pas consulté : seuls les dossiers choisis ici reçoivent les téléchargements."
            : detected.IsEmpty
            ? "Laisse vide pour utiliser les dossiers de tes bibliothèques Jellyfin. Ils n’ont pas pu être lus : il faut un compte administrateur et un serveur sur ce PC. Choisis-les ici sinon."
            : $"Laisse vide pour utiliser les dossiers de Jellyfin : Films → {Folder(detected.Movies)} · Séries → {Folder(detected.Series)} · Animes → {Folder(detected.Anime ?? detected.Series)}.";
    }
    private void BrowseTorLink_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseTorLinkFolder() is not { } root) return;
        TorLinkPathBox.Text = root; DescribeTorLinkSettings();
    }
    private void BrowseTorLinkFolder_Click(object sender, RoutedEventArgs e)
    {
        var target = ((Button)sender).Tag?.ToString() switch { "movies" => TorLinkMoviesBox, "series" => TorLinkSeriesBox, _ => TorLinkAnimeBox };
        var dialog = new OpenFolderDialog { Title = "Dossier de la bibliothèque Jellyfin" };
        if (Directory.Exists(target.Text.Trim())) dialog.InitialDirectory = target.Text.Trim();
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }
    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    { await RefreshAsync(quiet: true); if (_demo) SettingsSaveStatus.Text = "La démonstration utilise des données fictives."; else SettingsSaveStatus.Text = "Actualisation demandée."; }
}
