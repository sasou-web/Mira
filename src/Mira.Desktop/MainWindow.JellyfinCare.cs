using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// Jellyfin on this PC, for an administrator (« Jellyfin sur ce PC » in Réglages → Jellyfin): its backup
/// (<see cref="JellyfinBackups"/>), copied to a folder of the person's and recalled after 30 days, and its update
/// (<see cref="JellyfinReleases"/>): a backup first, then its installer, checked by Mira and again by the elevated script
/// that runs it, with one consent from Windows, never while anyone watches. Looked at three minutes after the start,
/// then every six hours; winget's catalogue on GitHub is read once a day at most.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _jellyfinCareTimer = new() { Interval = TimeSpan.FromMinutes(3) };
    private bool _jellyfinBusy;
    /// <summary>While Mira updates Jellyfin, which stops answering for a minute: not said as a Jellyfin stopped (<see cref="SayUnansweredAsync"/>).</summary>
    private bool _updatingJellyfin;
    /// <summary>What the row shows: Jellyfin's version, whether it backs itself up (10.11 and later), its newest backup, and whether Mira may update it.</summary>
    private string? _jellyfinVersion;
    private bool _jellyfinBacksUp, _jellyfinUpdatable;
    private JellyfinBackup? _newestBackup;
    private Task _jellyfinHereShowing = Task.CompletedTask;

    private void StartJellyfinCare()
    {
        _jellyfinCareTimer.Tick += async (_, _) => { _jellyfinCareTimer.Interval = TimeSpan.FromHours(6); await CareForJellyfinAsync(); };
        _jellyfinCareTimer.Start();
    }

    /// <summary>
    /// The reminder once a backup is due (<see cref="JellyfinBackups.Reminds"/>), else the notice of a newer Jellyfin,
    /// once per version; never over another notice on screen: the next look says it.
    /// </summary>
    private async Task CareForJellyfinAsync()
    {
        if (_jellyfinBusy || _demo || _client is not { } client) return;
        var ct = _connectionLifetime.Token;
        try
        {
            if (!await JellyfinOnThisPcAsync(client) || await client.IsAdministratorAsync(ct) != true) return;
            var now = DateTimeOffset.Now;
            if (await client.BackupsAsync(ct) is { } backups && ReferenceEquals(client, _client))
            {
                _newestBackup = backups.FirstOrDefault();
                if (_settings.JellyfinBackupWatchedSince is null) { _settings.JellyfinBackupWatchedSince = now; TrySaveSettings(); }
                if (JellyfinBackups.Reminds(_newestBackup?.Created, _settings.JellyfinBackupWatchedSince.Value, _settings.JellyfinBackupRemindedAt, now))
                {
                    if (NoticeShown) return;
                    _settings.JellyfinBackupRemindedAt = now; TrySaveSettings();
                    SetNotice(JellyfinBackups.Reminder(_newestBackup, now), L("Sauvegarder", "Back up"), () => _ = BackUpFromNoticeAsync(client));
                    return;
                }
            }
            if (await CheckJellyfinReleaseAsync(client, ct) is { } release && release != _settings.JellyfinReleaseSaid && !NoticeShown && ReferenceEquals(client, _client))
            {
                _settings.JellyfinReleaseSaid = release; TrySaveSettings();
                SetNotice(L($"Jellyfin {release} est disponible pour ce PC.", $"Jellyfin {release} is available for this PC."), L("Voir", "See"), OpenJellyfinHere);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
    private bool NoticeShown => Notice.Visibility == Visibility.Visible && Notice.IsHitTestVisible && NoticeText.Text.Length > 0;

    /// <summary>
    /// The newer Jellyfin to offer (« 12.3 »), from winget's catalogue read at most once a day; what was found last
    /// stays when GitHub does not answer. Null when Jellyfin is up to date.
    /// </summary>
    private async Task<string?> CheckJellyfinReleaseAsync(JellyfinClient client, CancellationToken ct)
    {
        if (JellyfinReleases.ParseVersion(await client.ServerVersionAsync(ct)) is not { } installed) return null;
        if (DateTimeOffset.Now - (_settings.JellyfinReleaseCheckedAt ?? DateTimeOffset.MinValue) >= JellyfinReleases.CheckEvery)
        {
            try
            {
                _settings.JellyfinRelease = (await JellyfinReleases.FindAsync(installed, ct: ct))?.Package.Version ?? "";
                _settings.JellyfinReleaseCheckedAt = DateTimeOffset.Now; TrySaveSettings();
            }
            // Tried again at the next look.
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        return JellyfinReleases.ParseVersion(_settings.JellyfinRelease) is { } offered && offered > installed ? _settings.JellyfinRelease : null;
    }

    /// <summary>« Voir » of the notice: the row, in Réglages → Jellyfin.</summary>
    private async void OpenJellyfinHere()
    {
        Settings_Click(this, new());
        SelectSettingsTab("care");
        await _jellyfinHereShowing;
        if (JellyfinHereRow.Visibility == Visibility.Visible && SettingsOverlay.Visibility == Visibility.Visible) JellyfinHereRow.BringIntoView();
    }

    /// <summary>« Jellyfin sur ce PC »: for an administrator of a Jellyfin on this PC; hidden otherwise.</summary>
    private async Task ShowJellyfinHereAsync()
    {
        if (_jellyfinBusy) return;
        JellyfinHereRow.Visibility = Visibility.Collapsed; JellyfinStatus.Text = "";
        if (_demo || _client is not { } client) return;
        var ct = _connectionLifetime.Token;
        try
        {
            if (!await JellyfinOnThisPcAsync(client) || await client.IsAdministratorAsync(ct) != true) return;
            _jellyfinVersion = await client.ServerVersionAsync(ct);
            var backups = await client.BackupsAsync(ct);
            _jellyfinBacksUp = backups is not null; _newestBackup = backups?.FirstOrDefault();
            // Mira updates only a Jellyfin its installer put here as a service: the installer then keeps it so.
            _jellyfinUpdatable = await Task.Run(() => JellyfinInstaller.InstalledFolder() is not null && JellyfinAutostart.ServiceState() is not (LocalJellyfin.Service.None or LocalJellyfin.Service.Unknown));
            await CheckJellyfinReleaseAsync(client, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (!ReferenceEquals(client, _client) || _jellyfinBusy) return;
        DescribeJellyfinHere();
        JellyfinHereRow.Visibility = Visibility.Visible;
    }

    private void DescribeJellyfinHere()
    {
        JellyfinHereState.Text = (_jellyfinVersion is { } version ? $"Jellyfin {version} · " : "")
            + (_jellyfinBacksUp ? JellyfinBackups.Summary(_newestBackup, DateTimeOffset.Now) : L("Sa sauvegarde intégrée arrive avec Jellyfin 10.11.", "Its built-in backup comes with Jellyfin 10.11."));
        var folder = _settings.JellyfinBackupFolder;
        JellyfinBackupActions.Visibility = _jellyfinBacksUp ? Visibility.Visible : Visibility.Collapsed;
        JellyfinBackupWhere.Text = !_jellyfinBacksUp ? L("Avant, sauvegarde son dossier de données à la main, Jellyfin arrêté.", "Until then, back up its data folder by hand, with Jellyfin stopped.")
            : folder.Length == 0 ? L("Elle reste sur ce PC, dans le dossier de Jellyfin. Choisis aussi une clé USB ou un autre disque : si ce PC tombe en panne, c’est elle qui compte.", "It stays on this PC, in Jellyfin’s folder. Also choose a USB key or another disk: if this PC breaks down, that’s the copy that counts.")
            : L($"Copiée aussi dans {folder}, qui garde les {JellyfinBackups.CopiesKept} dernières.", $"Also copied to {folder}, which keeps the last {JellyfinBackups.CopiesKept}.")
                + (SameDisk(folder, BackupsFolder) ? L(" Ce dossier est sur le même disque que Jellyfin : si ce disque lâche, la copie part avec.", " This folder is on the same disk as Jellyfin: if that disk fails, the copy goes with it.") : "");
        JellyfinBackupFolderButton.Content = folder.Length > 0 ? L("Changer de dossier…", "Change folder…") : L("Copier aussi dans…", "Also copy to…");
        JellyfinBackupNoCopyButton.Visibility = folder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var installed = JellyfinReleases.ParseVersion(_jellyfinVersion); var release = _settings.JellyfinRelease;
        if (installed is null || JellyfinReleases.ParseVersion(release) is not { } offered || offered <= installed) { JellyfinUpdatePanel.Visibility = Visibility.Collapsed; return; }
        var major = offered.Major > installed.Major; var updatable = _jellyfinUpdatable && _jellyfinBacksUp;
        JellyfinUpdateTitle.Text = major ? L($"Jellyfin {release} est disponible : une nouvelle version majeure.", $"Jellyfin {release} is available: a new major version.") : L($"Jellyfin {release} est disponible.", $"Jellyfin {release} is available.");
        JellyfinUpdateButton.Content = major ? L($"Passer à Jellyfin {release}", $"Move to Jellyfin {release}") : L("Mettre à jour Jellyfin", "Update Jellyfin");
        JellyfinUpdateButton.Visibility = updatable ? Visibility.Visible : Visibility.Collapsed;
        JellyfinUpdateText.Text = !_jellyfinBacksUp
            ? L("Mira ne met Jellyfin à jour qu’après l’avoir sauvegardé, ce que ce Jellyfin ne sait pas faire : télécharge cette version sur jellyfin.org, après avoir sauvegardé son dossier de données.", "Mira only updates Jellyfin after backing it up, which this Jellyfin can’t do: download this version from jellyfin.org, after backing up its data folder.")
            : !_jellyfinUpdatable
            ? L("Mira ne met à jour que le Jellyfin installé en service par son installateur. Télécharge cette version sur jellyfin.org.", "Mira only updates a Jellyfin its installer set up as a service. Download this version from jellyfin.org.")
            : (major ? L("Une version majeure peut changer ce dont Mira web se sert : lis ses nouveautés avant. ", "A major version can change what Mira web relies on: read what’s new first. ") : "")
                + L("Mira sauvegarde d’abord Jellyfin, puis lance son installateur officiel, vérifié par son empreinte. Windows demande ton autorisation et Jellyfin s’arrête une minute, jamais pendant que quelqu’un regarde. Ses réglages, ses comptes et tes bibliothèques restent ; revenir à l’ancienne version demande la sauvegarde.",
                    "Mira first backs Jellyfin up, then runs its official installer, checked by its fingerprint. Windows asks for your permission and Jellyfin stops for a minute, never while someone is watching. Its settings, accounts and libraries stay; going back to the old version takes the backup.");
        JellyfinUpdatePanel.Visibility = Visibility.Visible;
    }

    /// <summary>Where Jellyfin writes its backups: beside the newest one, else in its data folder.</summary>
    private string BackupsFolder => _newestBackup is { } known && Path.GetDirectoryName(known.Path) is { Length: > 0 } folder ? folder : Path.Combine(JellyfinInstaller.DataFolder(), "data", "backups");
    private static bool SameDisk(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }
    private static long? FreeSpace(string folder)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Said in the row, and as a notice when the row is not on screen (the reminder's « Sauvegarder »). <paramref name="sticky"/>: until the next message.</summary>
    private void JellyfinSays(string text, bool sticky = false)
    {
        JellyfinStatus.Text = text;
        if (SettingsOverlay.Visibility != Visibility.Visible || ServerSettings.Visibility != Visibility.Visible || JellyfinHereRow.Visibility != Visibility.Visible) SetNotice(text, null, null, sticky);
    }
    private void SetJellyfinBusy(bool busy)
    {
        _jellyfinBusy = busy;
        foreach (UIElement button in new UIElement[] { JellyfinBackupButton, JellyfinBackupFolderButton, JellyfinBackupNoCopyButton, JellyfinUpdateButton }) button.IsEnabled = !busy;
    }

    private async void JellyfinBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_jellyfinBusy || _client is not { } client) return;
        SetJellyfinBusy(true);
        try { await BackUpJellyfinAsync(client, _connectionLifetime.Token); }
        finally { SetJellyfinBusy(false); }
    }
    private async Task BackUpFromNoticeAsync(JellyfinClient client)
    {
        if (_jellyfinBusy || !ReferenceEquals(client, _client)) return;
        SetJellyfinBusy(true);
        try { await BackUpJellyfinAsync(client, _connectionLifetime.Token); }
        finally { SetJellyfinBusy(false); }
    }

    /// <summary>
    /// Jellyfin's backup, then its copy into the person's folder: the backup, or null when Jellyfin made none (said).
    /// A copy that fails leaves the backup made: it is in Jellyfin's folder.
    /// </summary>
    private async Task<JellyfinBackup?> BackUpJellyfinAsync(JellyfinClient client, CancellationToken ct)
    {
        // Jellyfin refuses below 5 GiB and only says « error 500 ».
        var backups = BackupsFolder;
        if (await Task.Run(() => FreeSpace(backups)) is { } free && free < JellyfinBackups.MinimumFree)
        {
            JellyfinSays(JellyfinBackups.NoRoom(Path.GetPathRoot(backups) ?? backups, free), sticky: true);
            return null;
        }
        JellyfinSays(L("Sauvegarde de Jellyfin… Une grande bibliothèque peut prendre quelques minutes.", "Backing up Jellyfin… A big library can take a few minutes."), sticky: true);
        JellyfinBackup? backup;
        try { backup = await client.CreateBackupAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (IsExpected(ex)) { JellyfinSays(L("Jellyfin n’a pas pu se sauvegarder : ", "Jellyfin couldn’t back itself up: ") + Friendly(ex), sticky: true); return null; }
        if (backup is null) { JellyfinSays(L("Jellyfin refuse de se sauvegarder : il y faut un compte administrateur et Jellyfin 10.11 ou plus.", "Jellyfin refuses to back itself up: that takes an administrator account and Jellyfin 10.11 or later."), sticky: true); return null; }
        if (!ReferenceEquals(client, _client)) return backup;
        _newestBackup = backup; _jellyfinBacksUp = true;
        var folder = _settings.JellyfinBackupFolder;
        var said = folder.Length == 0 ? (L($"Jellyfin est sauvegardé dans son dossier ({Path.GetDirectoryName(backup.Path)}).", $"Jellyfin is backed up in its folder ({Path.GetDirectoryName(backup.Path)})."), false)
            : await CopyBackupAsync(backup, folder);
        DescribeJellyfinHere();
        JellyfinSays(said.Item1, said.Item2);
        return backup;
    }
    /// <summary>The newest backup copied into <paramref name="folder"/>: what to say, and whether it failed.</summary>
    private async Task<(string, bool)> CopyBackupAsync(JellyfinBackup backup, string folder)
    {
        JellyfinSays(L($"Copie de la sauvegarde dans {folder}…", $"Copying the backup to {folder}…"), sticky: true);
        try
        {
            await Task.Run(() => JellyfinBackups.Copy(backup.Path, folder));
            return (L($"Jellyfin est sauvegardé, et copié dans {folder}.", $"Jellyfin is backed up, and copied to {folder}."), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var why = ex is UnauthorizedAccessException ? L("Windows en refuse l’accès.", "Windows denies access.")
                : ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) ? L("plus assez de place.", "not enough space.")
                : FromMira(ex) ? ex.Message : L("la copie a échoué.", "the copy failed.");
            return (L($"Jellyfin est sauvegardé dans son dossier ({Path.GetDirectoryName(backup.Path)}), mais pas copié dans {folder} : {why}", $"Jellyfin is backed up in its folder ({Path.GetDirectoryName(backup.Path)}), but not copied to {folder}: {why}"), true);
        }
    }

    /// <summary>A folder of the person's for the copies; the newest backup is copied there at once.</summary>
    private async void JellyfinBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_jellyfinBusy) return;
        var dialog = new OpenFolderDialog { Title = L("Dossier des copies de la sauvegarde de Jellyfin (clé USB, autre disque)", "Folder for copies of Jellyfin’s backup (USB key, another disk)") };
        if (Directory.Exists(_settings.JellyfinBackupFolder)) dialog.InitialDirectory = _settings.JellyfinBackupFolder;
        if (dialog.ShowDialog(this) != true) return;
        _settings.JellyfinBackupFolder = dialog.FolderName; TrySaveSettings();
        DescribeJellyfinHere();
        if (_newestBackup is not { } newest) { JellyfinSays(L($"Les prochaines sauvegardes seront copiées dans {dialog.FolderName}.", $"The next backups will be copied to {dialog.FolderName}.")); return; }
        SetJellyfinBusy(true);
        try { var (text, failed) = await CopyBackupAsync(newest, dialog.FolderName); JellyfinSays(text, failed); }
        finally { SetJellyfinBusy(false); }
    }
    private void JellyfinBackupNoCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_jellyfinBusy) return;
        _settings.JellyfinBackupFolder = ""; TrySaveSettings();
        DescribeJellyfinHere();
        JellyfinSays(L("Les sauvegardes restent dans le dossier de Jellyfin ; celles déjà copiées restent où elles sont.", "Backups stay in Jellyfin’s folder; those already copied stay where they are."));
    }
    private void JellyfinBackupOpen_Click(object sender, RoutedEventArgs e)
    {
        var folder = Directory.Exists(_settings.JellyfinBackupFolder) ? _settings.JellyfinBackupFolder : BackupsFolder;
        if (Directory.Exists(folder)) OpenInExplorer(folder, null);
        else JellyfinSays(L($"Le dossier {folder} n’existe pas encore : il apparaît avec la première sauvegarde.", $"The folder {folder} doesn’t exist yet: it appears with the first backup."));
    }
    private void JellyfinNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.JellyfinRelease.Length > 0) OpenWebPage(JellyfinReleases.Notes(_settings.JellyfinRelease).AbsoluteUri);
    }

    /// <summary>
    /// « Mettre à jour Jellyfin »: nobody watching, the release read again from winget's catalogue, a backup, the
    /// installer downloaded and checked, then run by the elevated script, which checks it again and puts the service back
    /// as it was. Done once Jellyfin answers with the new version; Mira web, served before, is looked for again.
    /// </summary>
    private async void JellyfinUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_jellyfinBusy || _client is not { } client) return;
        SetJellyfinBusy(true);
        var ct = _connectionLifetime.Token; string? setup = null; Task<int?>? update = null;
        try
        {
            if (await SomeoneWatchingAsync(client, ct)) return;
            var current = await client.ServerVersionAsync(ct) ?? "";
            if (JellyfinReleases.ParseVersion(current) is not { } installed) { JellyfinSays(L("Jellyfin ne dit pas sa version : Mira ne le met pas à jour.", "Jellyfin doesn’t give its version: Mira doesn’t update it."), sticky: true); return; }
            JellyfinSays(L("Recherche de la nouvelle version…", "Looking for the new version…"), sticky: true);
            JellyfinRelease? release;
            try { release = await JellyfinReleases.FindAsync(installed, ct: ct); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                JellyfinSays(ex is IOException && FromMira(ex) ? ex.Message : L("Le catalogue de winget ne répond pas : réessaie plus tard.", "winget’s catalogue isn’t answering: try again later."), sticky: true);
                return;
            }
            if (release is null)
            {
                _settings.JellyfinRelease = ""; _settings.JellyfinReleaseCheckedAt = DateTimeOffset.Now; TrySaveSettings();
                DescribeJellyfinHere(); JellyfinSays(L("Jellyfin est déjà à jour.", "Jellyfin is already up to date."));
                return;
            }
            var version = release.Package.Version;
            // No way back without it: the new version moves Jellyfin's database forward at its first start.
            if (await BackUpJellyfinAsync(client, ct) is null) { JellyfinSays(JellyfinStatus.Text + " " + L("Jellyfin n’est pas mis à jour sans sauvegarde.", "Jellyfin isn’t updated without a backup."), sticky: true); return; }
            setup = await JellyfinInstaller.DownloadAsync(_profile.DirectoryPath, release.Package, new Progress<double>(p =>
                JellyfinSays(L($"Téléchargement de Jellyfin {version}… {Math.Floor(p * 100):0} %", $"Downloading Jellyfin {version}… {Math.Floor(p * 100):0}%"), sticky: true)), ct);
            if (await SomeoneWatchingAsync(client, ct)) return;
            var before = await Task.Run(() => JellyfinAutostart.Read());
            var servedMiraWeb = await client.WebAppAvailableAsync(ct);
            var started = DateTimeOffset.Now;
            JellyfinSays(L($"Accepte l’autorisation de Windows : Jellyfin s’arrête le temps d’installer la version {version}, une minute environ.", $"Accept Windows’ permission prompt: Jellyfin stops while version {version} installs, about a minute."), sticky: true);
            _updatingJellyfin = true;
            update = JellyfinAutostart.UpdateAsync(setup, release.Package.Sha256, before);
            var asked = false;
            while (!update.IsCompleted)
            {
                await Task.WhenAny(update, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
                // Its message box « Could not start the Jellyfin Server service » waits, often behind Mira.
                if (!asked && !update.IsCompleted && RunningInstaller.BringForward(setup))
                {
                    asked = true;
                    JellyfinSays(L("L’installateur de Jellyfin affiche un message : si c’est « Could not start the Jellyfin Server service », choisis Ignorer. Mira attend ensuite que Jellyfin réponde.", "Jellyfin’s installer shows a message: if it’s “Could not start the Jellyfin Server service”, choose Ignore. Mira then waits for Jellyfin to answer."), sticky: true);
                }
            }
            var code = await update;
            if (code is null) { JellyfinSays(L($"Windows n’a pas reçu l’autorisation : Jellyfin reste en {current}.", $"Windows didn’t get permission: Jellyfin stays on {current}."), sticky: true); return; }
            if (code == JellyfinStartup.SetupMismatch) { JellyfinSays(L("Le fichier téléchargé ne correspond pas à la version vérifiée par Mira : rien n’a été installé.", "The downloaded file doesn’t match the version Mira checked: nothing was installed."), sticky: true); return; }
            if (code == JellyfinStartup.SetupNotStarted) { JellyfinSays(L($"L’installateur de Jellyfin n’a pas pu démarrer : Jellyfin reste en {current}.", $"Jellyfin’s installer couldn’t start: Jellyfin stays on {current}."), sticky: true); return; }
            JellyfinSays(L($"Démarrage de Jellyfin {version}…", $"Starting Jellyfin {version}…"), sticky: true);
            var answered = await NewJellyfinAsync(client, release.Version, ct);
            if (!ReferenceEquals(client, _client)) return;
            if (answered is null)
            {
                JellyfinSays(InstallerWaits(code) ? L("Jellyfin ne répond pas encore et son installateur attend une réponse : dans sa fenêtre « Could not start the Jellyfin Server service », choisis Ignorer.", "Jellyfin isn’t answering yet and its installer is waiting for an answer: in its “Could not start the Jellyfin Server service” window, choose Ignore.")
                    : L($"Jellyfin ne répond pas après sa mise à jour vers {version}.", $"Jellyfin isn’t answering after its update to {version}.") + LoggedCause(started), sticky: true);
                _ = RefreshAsync(quiet: true);
                return;
            }
            _settings.JellyfinRelease = ""; _settings.JellyfinReleaseCheckedAt = null; TrySaveSettings();
            _jellyfinVersion = answered;
            var said = L($"Jellyfin {answered} est installé ; la sauvegarde d’avant est gardée.", $"Jellyfin {answered} is installed; the backup from before is kept.");
            if (servedMiraWeb && !await MiraWebBackAsync(client, ct))
                said += " " + L($"Mais il ne sert plus Mira web : son extension n’a pas chargé avec cette version. Pour revenir à {current} : désinstalle Jellyfin, installe {current} depuis jellyfin.org, puis remets la sauvegarde (Tableau de bord → Sauvegardes).",
                    $"But it no longer serves Mira web: its plugin didn’t load with this version. To go back to {current}: uninstall Jellyfin, install {current} from jellyfin.org, then restore the backup (Dashboard → Backups).");
            if (before.RestartsAfterError && !(await Task.Run(() => JellyfinAutostart.Read())).RestartsAfterError)
                said += " " + L("Son redémarrage après une erreur n’a pas pu être remis : « Tout préparer », dans le guide, le refait.", "Its restart after an error couldn’t be put back: “Set everything up”, in the guide, does it again.");
            if (InstallerWaits(code)) said += " " + InstallerAsks;
            DescribeJellyfinHere();
            JellyfinSays(said, sticky: said.Length > 120);
            _ = RefreshAsync(quiet: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InstallException) { JellyfinSays(ex.Message, sticky: true); }
        catch (Exception ex) when (IsExpected(ex)) { JellyfinSays(Friendly(ex), sticky: true); }
        finally
        {
            _updatingJellyfin = false; SetJellyfinBusy(false);
            // Still running (its message box): deleted at the next update, by its download.
            if (setup is not null && !(update is { IsCompleted: true } && InstallerWaits(update.Result))) JellyfinInstaller.Forget(setup);
        }
    }
    /// <summary>The script stopped waiting for the installer (<see cref="JellyfinStartup.SetupWait"/>), or Mira for the script: its message box is still open.</summary>
    private static bool InstallerWaits(int? code) => code == JellyfinStartup.SetupStillRunning || code == -1;
    /// <summary>Someone plays something on Jellyfin, this PC included: said, and nothing is updated.</summary>
    private async Task<bool> SomeoneWatchingAsync(JellyfinClient client, CancellationToken ct)
    {
        if (await client.ViewersAsync(ct) is not [var first, ..]) return false;
        JellyfinSays(L($"Quelqu’un regarde en ce moment ({Viewers.Who(first)}) : Jellyfin s’arrêterait. Réessaie quand ce sera fini.", $"Someone is watching right now ({Viewers.Who(first)}): Jellyfin would stop. Try again once they’re done."), sticky: true);
        return true;
    }
    /// <summary>Jellyfin's version once it answers ready with <paramref name="version"/> or later, within three minutes; null otherwise.</summary>
    private static async Task<string?> NewJellyfinAsync(JellyfinClient client, Version version, CancellationToken ct)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < limit)
        {
            try
            {
                if (await client.PingAsync(ct) && await client.ServerVersionAsync(ct) is { } answer && JellyfinReleases.ParseVersion(answer) is { } now && now >= version) return answer;
            }
            // Silent while it installs and starts.
            catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }
    /// <summary>Mira web served again: its plugin loads with Jellyfin, a few seconds after it answers.</summary>
    private static async Task<bool> MiraWebBackAsync(JellyfinClient client, CancellationToken ct)
    {
        for (var tries = 0; tries < 15; tries++)
        {
            try { if (await client.WebAppAvailableAsync(ct)) return true; }
            catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }
}
