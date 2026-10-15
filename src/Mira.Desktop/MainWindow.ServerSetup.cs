using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>"Installer Jellyfin sur ce PC": the server, its Films / Séries / Animes folders and its first-run wizard, then the connection.</summary>
public partial class MainWindow
{
    private bool _serverSetupBusy;
    private void ServerSetupOpen_Click(object sender, RoutedEventArgs e) => ShowServerSetup(true);
    private void ServerSetupBack_Click(object sender, RoutedEventArgs e) => ShowServerSetup(false);
    private void ShowServerSetup(bool show)
    {
        if (_serverSetupBusy) return;
        if (show)
        {
            if (SetupFolderBox.Text.Trim().Length == 0) SetupFolderBox.Text = MediaFolders.DefaultRoot;
            if (SetupUserBox.Text.Trim().Length == 0 && JellyfinSetup.IsValidUserName(Environment.UserName)) SetupUserBox.Text = Environment.UserName;
            SetupStatus.Text = "";
        }
        LoginPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ServerSetupPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Motion.Reveal(show ? ServerSetupPanel : LoginPanel, 220, 8);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (show) (SetupUserBox.Text.Length == 0 ? SetupUserBox : (UIElement)SetupPasswordBox).Focus(); else FocusLogin();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
    /// <summary>The sign-in form rather than the setup left open earlier; an installation still running keeps its panel.</summary>
    private void ShowSignInForm()
    {
        if (_serverSetupBusy) return;
        LoginPanel.Visibility = Visibility.Visible; ServerSetupPanel.Visibility = Visibility.Collapsed;
    }
    private void SetupFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Dossier des vidéos (Mira y crée Films, Séries et Animes)" };
        if (Directory.Exists(SetupFolderBox.Text.Trim())) dialog.InitialDirectory = SetupFolderBox.Text.Trim();
        if (dialog.ShowDialog(this) == true) SetupFolderBox.Text = dialog.FolderName;
    }
    private void ServerSetup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !SetupStartButton.IsEnabled) return;
        e.Handled = true; ServerSetupStart_Click(SetupStartButton, new());
    }
    private async void ServerSetupStart_Click(object sender, RoutedEventArgs e) => await RunServerSetupAsync();
    private async Task RunServerSetupAsync()
    {
        if (_serverSetupBusy) return;
        var user = SetupUserBox.Text.Trim(); var password = SetupPasswordBox.Password; var root = SetupFolderBox.Text.Trim();
        if (!JellyfinSetup.IsValidUserName(user)) { SetupStatus.Text = "Nom d’utilisateur : lettres, chiffres, espaces et - _ ' . @ +."; SetupUserBox.Focus(); return; }
        if (password.Length == 0) { SetupStatus.Text = "Choisis un mot de passe."; SetupPasswordBox.Focus(); return; }
        if (MediaFolders.Refusal(root) is { } refusal) { SetupStatus.Text = refusal; SetupFolderBox.Focus(); return; }
        SetServerSetupBusy(true);
        var server = JellyfinSetup.LocalServer; var started = DateTimeOffset.Now.AddMinutes(-1); RunningInstaller? installer = null;
        try
        {
            SetupStatus.Text = "Recherche de Jellyfin…";
            var probe = await JellyfinSetup.ProbeAsync(server);
            if (probe.Answer == ServerAnswer.Other)
                throw new InstallException("Un autre programme utilise le port 8096, celui de Jellyfin : ferme-le, puis réessaie.");
            var state = probe.State;
            if (state is null)
            {
                // An existing Jellyfin, even stopped, is never installed over: it holds someone's library.
                var existing = JellyfinInstaller.IsInstalled(); var installed = false;
                if (!existing && probe.Answer == ServerAnswer.Nothing)
                {
                    if (JellyfinSetup.PortInUse(server.Port))
                        throw new InstallException("Le port 8096, celui de Jellyfin, est déjà pris : ferme le programme qui l’utilise, puis réessaie.");
                    var version = JellyfinServerPackage.Current.Version; SetupStatus.Text = $"Téléchargement de Jellyfin {version}…";
                    installer = await JellyfinInstaller.StartAsync(_profile.DirectoryPath, new Progress<double>(p => SetupStatus.Text = p < 1
                        ? $"Téléchargement de Jellyfin {version}… {Math.Floor(p * 100):0} %"
                        : "Accepte l’autorisation de Windows."));
                    // "Could not start the Jellyfin Server service" can come while the service does start ("nssm start" gives
                    // up before it runs; seen on a real PC), and its window then waits, often behind Mira: once installed,
                    // whether Jellyfin answers is what counts, and the setup goes on without that window's answer.
                    SetupStatus.Text = "Installation de Jellyfin…"; var shown = false; var waiting = true;
                    var wait = await JellyfinSetup.AwaitInstallerAsync(installer.Exited, server, new Progress<int>(_ =>
                    {
                        // Reported on this thread, maybe after the wait: then the status says the next step.
                        if (shown || !waiting || !installer.HasWindow) return;
                        shown = true; installer.BringForward();
                        SetupStatus.Text = "L’installateur de Jellyfin affiche un message : si c’est « Could not start the Jellyfin Server service », choisis Ignorer. Mira attend ensuite que Jellyfin réponde.";
                    }));
                    waiting = false; state = wait.State;
                    if (state is null && wait.ExitCode != 0 && !JellyfinInstaller.IsInstalled())
                        throw new InstallException($"L’installateur de Jellyfin s’est arrêté sans terminer (code {wait.ExitCode})." + LoggedCause(started, orWhere: false));
                    installed = true;
                }
                if (state is null)
                {
                    SetupStatus.Text = existing && probe.Answer == ServerAnswer.Nothing ? "Jellyfin est déjà installé : attente de son démarrage…" : "Démarrage de Jellyfin…";
                    try { state = await JellyfinSetup.WaitAsync(server, existing && probe.Answer == ServerAnswer.Nothing ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(3)); }
                    catch (InstallException)
                    {
                        // A Jellyfin found installed may have failed at the PC's start, well before this attempt.
                        throw new InstallException(installed
                            ? "Jellyfin est installé mais ne démarre pas : redémarre le PC, puis réessaie." + LoggedCause(started)
                            : "Jellyfin est installé mais ne répond pas : lance « Jellyfin Server » depuis le menu Démarrer, puis réessaie." + LoggedCause(DateTimeOffset.Now.AddDays(-1)));
                    }
                }
            }
            if (state.WizardCompleted)
            {
                // Set up earlier, by its own page or another install: its account is the one to use.
                _serverSetupBusy = false; ShowServerSetup(false);
                ServerBox.Text = server.AbsoluteUri.TrimEnd('/'); UsernameBox.Text = user;
                LoginError.Text = "Jellyfin est déjà installé sur ce PC : connecte-toi avec ton compte.";
                return;
            }
            SetupStatus.Text = "Création des dossiers…";
            var folders = MediaFolders.Under(root);
            // The service reads the folders as Network Service: granted before Jellyfin is asked to add them.
            await Task.Run(() => { folders.Create(); folders.AllowJellyfinService(); });
            SetupStatus.Text = "Configuration…";
            await JellyfinSetup.ConfigureAsync(server, user, password, folders);
            SetupStatus.Text = "Connexion…";
            Connection connection;
            using (var client = new JellyfinClient(new(server.AbsoluteUri, "", user, "", _profile.DeviceId))) connection = await client.LoginAsync(user, password);
            _profile.SaveConnection(connection); SetupPasswordBox.Clear();
            _serverSetupBusy = false; ShowSignInForm();
            await ActivateConnectionAsync(connection);
            OpenGuide($"Jellyfin est prêt. Range tes vidéos dans {folders.Root}.");
            if (installer is { Running: true }) InstallerStillAsking(installer);
        }
        catch (Exception ex) when (ex is InstallException or UserInputException) { SetupFailed(ex.Message, installer); }
        catch (Exception ex) when (IsExpected(ex)) { SetupFailed(Friendly(ex), installer); }
        finally { SetServerSetupBusy(false); }
    }
    private const string InstallerAsks = "L’installateur de Jellyfin attend encore une réponse : dans sa fenêtre « Could not start the Jellyfin Server service », choisis Ignorer. Jellyfin fonctionne déjà.";
    /// <summary>
    /// The installer's message box left waiting while Jellyfin runs: brought in front of Mira to be answered, and said
    /// until it ends.
    /// </summary>
    private void InstallerStillAsking(RunningInstaller installer)
    {
        SetNotice(InstallerAsks, null, null, sticky: true);
        installer.BringForward();
        _ = HideOnceEndedAsync();
        async Task HideOnceEndedAsync()
        {
            try { await installer.Exited; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (NoticeText.Text == InstallerAsks) HideNotice();
        }
    }
    /// <summary>Why Jellyfin stopped, from its own log since <paramref name="since"/>; otherwise where that log is, or nothing.</summary>
    private static string LoggedCause(DateTimeOffset since, bool orWhere = true)
    {
        var folder = Path.Combine(JellyfinInstaller.DataFolder(), "log");
        return JellyfinLog.LastFatal(folder, since) is { } cause ? $" Jellyfin indique : « {cause} »." : orWhere ? $" Son journal est dans {folder}." : "";
    }
    /// <summary>
    /// Shown in the setup panel, and as a notice when the library was reopened during the installation; with the
    /// installer's message box when it still waits, for it holds the next attempt (one installer at a time).
    /// </summary>
    private void SetupFailed(string message, RunningInstaller? installer = null)
    {
        if (installer is { Running: true }) { message += " " + InstallerAsks; installer.BringForward(); }
        SetupStatus.Text = message;
        if (LoginOverlay.Visibility != Visibility.Visible) SetNotice("Installation de Jellyfin : " + message);
    }
    private void SetServerSetupBusy(bool busy)
    {
        _serverSetupBusy = busy;
        foreach (UIElement control in new UIElement[] { SetupFolderBox, SetupFolderBrowse, SetupUserBox, SetupPasswordBox, SetupStartButton, SetupBackButton }) control.IsEnabled = !busy;
        SetupStartText.Text = busy ? "Installation…" : "Installer";
    }
}
