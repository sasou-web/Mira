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
        SetNotice(ApplySettings() ? "Réglages enregistrés." : SettingsNotSaved);
    }
    /// <summary>
    /// Writes the settings. A file that cannot be written (full disk, held by another program) is no reason to stop what
    /// Mira was doing: the settings stay applied for this session, and closing Mira writes them again.
    /// </summary>
    private bool TrySaveSettings()
    {
        try { _profile.SaveSettings(_settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Append(_profile.DirectoryPath, ex); return false; }
    }
    private const string SettingsNotSaved = "Réglages appliqués, mais pas enregistrés : le disque est plein ou le fichier des réglages est utilisé par un autre programme. Mira réessaiera en se fermant.";
    private bool SettingsChanged() =>
        _settings.HardwareDecoding != (HardwareCheck.IsChecked == true) || _settings.AutoNext != (AutoNextCheck.IsChecked == true) || _settings.AutoSkip != (AutoSkipCheck.IsChecked == true) || _settings.RememberPosition != (RememberCheck.IsChecked == true)
        || _settings.ShowProgress != (ShowProgressCheck.IsChecked == true) || _settings.ReduceMotion != (ReduceMotionCheck.IsChecked == true) || _settings.HeroAutoPlay != (HeroAutoPlayCheck.IsChecked == true)
        || _settings.PosterDensity != Choice(DensityChoice) || _settings.AudioLanguage != AudioLanguageBox.Text.Trim() || _settings.SubtitleLanguage != SubtitleLanguageBox.Text.Trim()
        || _settings.SubtitleSize != (int)SubtitleSizeSlider.Value || (_settings.MpvPath != MpvPathBox.Text.Trim() && MpvEngine.FindLibrary(_settings.MpvPath) != MpvPathBox.Text.Trim())
        || _settings.TorLinkAutoImport != (TorLinkAutoImportCheck.IsChecked == true) || _settings.TorLinkKeepShared != (TorLinkKeepSharedCheck.IsChecked == true)
        || _settings.TorLinkPath != TorLinkPathBox.Text.Trim() || _settings.TorLinkMoviesFolder != TorLinkMoviesBox.Text.Trim()
        || _settings.TorLinkSeriesFolder != TorLinkSeriesBox.Text.Trim() || _settings.TorLinkAnimeFolder != TorLinkAnimeBox.Text.Trim()
        || _settings.AutoUpdate != (AutoUpdateCheck.IsChecked == true) || _settings.EpisodeNotices != (EpisodeNoticesCheck.IsChecked == true);
    private bool _settingsBusy;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // The gear clicked again while the page is open refills it from the saved settings: what was changed is kept first.
        AutoSaveSettings();
        ClosePreview(); ++_detailVersion; _returnToDetail = null;
        _settingsBusy = true;
        HardwareCheck.IsChecked = _settings.HardwareDecoding; AutoNextCheck.IsChecked = _settings.AutoNext; AutoSkipCheck.IsChecked = _settings.AutoSkip; RememberCheck.IsChecked = _settings.RememberPosition;
        ShowProgressCheck.IsChecked = _settings.ShowProgress; ReduceMotionCheck.IsChecked = _settings.ReduceMotion;
        HeroAutoPlayCheck.IsChecked = _settings.HeroAutoPlay; EpisodeNoticesCheck.IsChecked = _settings.EpisodeNotices; ShowHomeRowsSetting();
        AudioLanguageBox.Text = _settings.AudioLanguage; SubtitleLanguageBox.Text = _settings.SubtitleLanguage;
        SelectChoice(AudioLanguageChoice, _settings.AudioLanguage); SelectChoice(SubtitleLanguageChoice, _settings.SubtitleLanguage); SelectChoice(DensityChoice, _settings.PosterDensity);
        SubtitleSizeSlider.Value = _settings.SubtitleSize; MpvPathBox.Text = MpvEngine.FindLibrary(_settings.MpvPath) ?? _settings.MpvPath;
        ShowEngineState();
        SubtitlePreviewImage.Source = HeroImage.Source;
        TorLinkAutoImportCheck.IsChecked = _settings.TorLinkAutoImport; TorLinkKeepSharedCheck.IsChecked = _settings.TorLinkKeepShared;
        TorLinkPathBox.Text = _settings.TorLinkPath; TorLinkMoviesBox.Text = _settings.TorLinkMoviesFolder;
        TorLinkSeriesBox.Text = _settings.TorLinkSeriesFolder; TorLinkAnimeBox.Text = _settings.TorLinkAnimeFolder;
        TorLinkSettingsNav.Visibility = _torlinkEnabled ? Visibility.Visible : Visibility.Collapsed;
        DescribeTorLinkSettings(); _friendShowing = ShowFriendAsync(); _ = ShowApproveDeviceAsync(); _ = ShowSagasAsync(); _ = ShowTrickplaySettingAsync(); _ = ShowGpuAsync(); ShowOfflineSettings();
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
        ReportSettings.Visibility = tab == "report" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSectionTitle.Text = tab switch { "subtitles" => "Audio & sous-titres", "appearance" => "Apparence", "server" => "Jellyfin & synchronisation", "torlink" => "TorLink", "updates" => "Mises à jour", "report" => "Ton bilan", _ => "Lecture" };
        SettingsSectionDescription.Text = tab switch { "subtitles" => "Les bonnes langues, dès le premier épisode.", "appearance" => "Ajuste le confort de navigation à tes habitudes.", "server" => "Ta bibliothèque et ta progression, toujours à portée de main.", "torlink" => "Tes téléchargements rangés dans Jellyfin, sans rien déplacer à la main.", "updates" => "Les nouvelles versions de Mira, installées sans y penser.", "report" => "Ce que tu as regardé, sur tous tes appareils.", _ => "Le confort de lecture, sans avoir à y penser." };
        foreach (var button in SettingsNavigation.Children.OfType<Button>()) { var active = button.Tag?.ToString() == tab; button.Background = active ? Palette.Get("Text") : Brushes.Transparent; button.Foreground = active ? Palette.Get("OnAccent") : Palette.Get("TextBody"); }
        SettingsScroll.ScrollToTop();
        var body = tab switch { "subtitles" => SubtitleSettings, "appearance" => AppearanceSettings, "server" => ServerSettings, "torlink" => TorLinkSettings, "updates" => UpdateSettings, "report" => ReportSettings, _ => PlaybackSettings };
        SmoothScroll.Jump(SettingsScroll); Motion.Reveal(body, 230, 7);
        WatchViewers(tab == "server");
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
    { if (SubtitlePreview is null) return; SubtitlePreview.FontSize = e.NewValue * .65; SubtitleSizeLabel.Text = $"{e.NewValue:0}"; }
    private void BrowseMpv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Sélectionner le moteur mpv 64 bits", Filter = "Bibliothèque mpv|mpv-2.dll;libmpv-2.dll;mpv-1.dll|Bibliothèques DLL|*.dll" };
        if (dialog.ShowDialog(this) != true) return;
        MpvPathBox.Text = dialog.FileName;
        EngineStatus.Text = "Moteur sélectionné · il sera utilisé à la prochaine lecture après enregistrement.";
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e) => ApplySettings();
    /// <summary>What the page shows becomes Mira's settings; false when they could not be written.</summary>
    private bool ApplySettings()
    {
        _settings.HardwareDecoding = HardwareCheck.IsChecked == true; _settings.AutoNext = AutoNextCheck.IsChecked == true; _settings.AutoSkip = AutoSkipCheck.IsChecked == true; _settings.RememberPosition = RememberCheck.IsChecked == true;
        _settings.ShowProgress = ShowProgressCheck.IsChecked == true; _settings.ReduceMotion = ReduceMotionCheck.IsChecked == true; _settings.PosterDensity = Choice(DensityChoice);
        _settings.HeroAutoPlay = HeroAutoPlayCheck.IsChecked == true; Motion.Reduced = _settings.ReduceMotion;
        // Turned on again: from what Jellyfin has now, not every episode that came while it was off.
        if (!_settings.EpisodeNotices && EpisodeNoticesCheck.IsChecked == true) _settings.EpisodesSeenFor = "";
        _settings.EpisodeNotices = EpisodeNoticesCheck.IsChecked == true; if (_settings.EpisodeNotices) EpisodesMayHaveArrived();
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
        var saved = TrySaveSettings();
        if (updatesAgain) _ = _updater?.CheckAsync();
        if (automaticAgain) _torlinkImporter?.RestartBaseline();
        if (_torlinkEnabled) ApplyTorLinkLibraries();
        _mpv?.Set("hwdec", _settings.HardwareDecoding ? "auto" : "no"); _mpv?.Set("sub-font-size", _settings.SubtitleSize.ToString(CultureInfo.InvariantCulture));
        RenderLibrary(); UpdateHeroClock(); SettingsSaveStatus.Text = saved ? "Préférences enregistrées." : SettingsNotSaved;
        return saved;
    }
    /// <summary>What Mira found: the TorLink installation and the library folders read from Jellyfin.</summary>
    private void DescribeTorLinkSettings()
    {
        var installation = TorLink.TorLinkInstallation.Locate(TorLinkPathBox.Text, _profile.DirectoryPath);
        TorLinkPathStatus.Text = installation is null
            ? "Pas installé : active TorLink dans la page Téléchargements."
            : installation.Managed ? $"TorLink trouvé : version {installation.Version}, installée par Mira."
            : $"TorLink trouvé : {installation.Root}" + (installation.HasRuntime ? "." : " · Node.js 22 ou plus récent est nécessaire.")
              + (installation.Detected ? " Mira te demandera de confirmer que c’est le tien avant de le lancer." : "")
              + (TorLinkPathBox.Text.Trim().Length == 0 ? " Laisse vide pour le retrouver automatiquement." : "");
        string Folder(string? path) => path ?? "non disponible";
        var detected = _torlink?.Detected ?? new MediaLibraries(null, null, null);
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
    private const string ApproveOwn = "Sur ton téléphone, « Se connecter avec un code » affiche 6 chiffres : saisis-les ici pour le connecter à ton compte, sans y taper ton mot de passe. N’autorise qu’un appareil que tu as sous les yeux.";
    private const string ApproveAny = "Sur un téléphone, « Se connecter avec un code » affiche 6 chiffres : choisis le compte à y ouvrir, le tien ou celui d’un ami, puis saisis-les ici. Aucun mot de passe à taper. N’autorise que les chiffres d’un appareil que tu connais.";
    private void ShowApproveAccounts(JellyfinClient client, IReadOnlyList<AuthUser> users, string? select)
    {
        var others = users.Where(x => !string.Equals(x.Id, client.Connection.UserId, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ApproveAccount.Items.Clear();
        ApproveAccount.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApproveDeviceText.Text = others.Count > 0 ? ApproveAny : ApproveOwn;
        if (others.Count == 0) return;
        ApproveAccount.Items.Add(new ComboBoxItem { Content = "Ton compte", Tag = client.Connection.UserId });
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
    private const string SagasOn = "Activé : Jellyfin range tes films en sagas à chaque analyse de la bibliothèque. Pour l’arrêter : Tableau de bord de Jellyfin → Médiathèques → Films → « Ajouter automatiquement à la collection ».";
    /// <summary>Turns the option on in every film library, then has Jellyfin scan: the sagas exist at the end of the scan.</summary>
    private async void Sagas_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _sagasBusy) return;
        _sagasBusy = true; SagasButton.IsEnabled = false; SagasStatus.Text = "Réglage de Jellyfin…";
        var ct = _connectionLifetime.Token;
        try
        {
            if (!await client.EnableSagasAsync(ct)) { SagasStatus.Text = "Seul un administrateur de Jellyfin peut changer ce réglage."; return; }
            var progress = new Progress<double?>(p => SagasStatus.Text = p is { } share ? $"Jellyfin range tes films… {share * 100:0} %" : "Jellyfin range tes films…");
            await client.ScanAndWaitAsync(progress, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), ct);
            var count = await client.SagaCountAsync(ct);
            SagasButton.Visibility = Visibility.Collapsed;
            SagasStatus.Text = count > 0 ? $"{count} saga{(count > 1 ? "s" : "")} dans ta bibliothèque : la fiche de leurs films les montre. Jellyfin range les suivantes à chaque analyse."
                : "Activé. Aucune saga pour l’instant : Jellyfin en crée une dès que deux films d’une même saga sont dans ta bibliothèque.";
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
    private static string TrickplayRunning(double share) => $"Jellyfin prépare les aperçus… {share * 100:0} %. Ceux des titres déjà faits apparaissent dès maintenant.";
    private const string TrickplayOn = "Activé : Jellyfin prépare les aperçus des nouveaux titres chaque nuit. Pour l’arrêter : Tableau de bord de Jellyfin → Médiathèques → la bibliothèque → images trickplay.";
    /// <summary>Turns the option on in the film and series libraries and starts Jellyfin's task, which goes on alone.</summary>
    private async void Trickplay_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _trickplayBusy) return;
        _trickplayBusy = true; TrickplayButton.IsEnabled = false; TrickplayStatus.Text = "Réglage de Jellyfin…";
        var ct = _connectionLifetime.Token;
        try
        {
            if (!await client.EnableTrickplayAsync(ct)) { TrickplayStatus.Text = "Seul un administrateur de Jellyfin peut changer ce réglage."; return; }
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
        ShowGpuState(on ? $"Activé : Jellyfin convertit avec la carte graphique ({GpuEncoding.Maker(kind)})." : "", on);
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
        _gpuBusy = true; GpuButton.IsEnabled = false; GpuStatus.Text = "Essai de conversion sur la carte graphique…";
        var ct = _connectionLifetime.Token;
        try
        {
            var (outcome, card) = await GpuEncoding.EnableAsync(client, _gpuCards, TimeSpan.FromSeconds(30), ct);
            switch (outcome)
            {
                case GpuOutcome.Done: ShowGpuState($"Activé : ta {card!.Name} convertit les vidéos pour ton téléphone.", true); break;
                case GpuOutcome.Refused: GpuStatus.Text = "Seul un administrateur de Jellyfin peut changer ce réglage."; break;
                case GpuOutcome.NothingToTry: GpuStatus.Text = "Ajoute d’abord un film ou un épisode à ta bibliothèque : Mira essaie la carte sur l’un d’eux."; break;
                default: GpuStatus.Text = "La carte graphique n’a pas réussi l’essai : Jellyfin continue avec le processeur. Mettre à jour son pilote peut suffire, puis réessaie."; break;
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
            if (await client.SetHardwareAccelerationAsync(GpuEncoding.None, ct)) ShowGpuState("Jellyfin convertit de nouveau avec le processeur.", false);
            else GpuStatus.Text = "Seul un administrateur de Jellyfin peut changer ce réglage.";
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
        if (JellyfinClient.QuickConnectCode(ApproveCodeBox.Text) is not { } code) { ApproveDeviceStatus.Text = "Le code a 6 chiffres."; ApproveCodeBox.Focus(); ApproveCodeBox.SelectAll(); return; }
        (string? Id, string? Name) account = ApproveAccount.Visibility == Visibility.Visible && ApproveAccount.SelectedItem is ComboBoxItem { Tag: string id } chosen
            && !string.Equals(id, client.Connection.UserId, StringComparison.OrdinalIgnoreCase) ? (id, chosen.Content as string) : (null, null);
        ApproveDeviceButton.IsEnabled = false; ApproveDeviceStatus.Text = "Autorisation…";
        try
        {
            var approval = await client.ApproveCodeAsync(code, account.Id);
            ApproveDeviceStatus.Text = approval switch
            {
                CodeApproval.Approved => account.Id is null ? "Appareil autorisé : il se connecte à ton compte." : $"Appareil autorisé : il se connecte au compte de {account.Name}.",
                CodeApproval.Unknown => "Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.",
                _ => "Quick Connect est désactivé sur ce serveur : active-le dans le tableau de bord de Jellyfin (Général).",
            };
            if (approval == CodeApproval.Approved) ApproveCodeBox.Clear(); else { ApproveCodeBox.Focus(); ApproveCodeBox.SelectAll(); }
        }
        catch (Exception ex) when (IsExpected(ex)) { ApproveDeviceStatus.Text = Friendly(ex); }
        finally { ApproveDeviceButton.IsEnabled = true; }
    }
}
