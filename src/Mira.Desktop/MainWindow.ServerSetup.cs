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
        var dialog = new OpenFolderDialog { Title = "Dossier de tes vidéos : Mira y crée Films, Séries et Animes" };
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
        if (!JellyfinSetup.IsValidUserName(user)) { SetupStatus.Text = "Le nom d’utilisateur peut contenir des lettres, des chiffres, des espaces et - _ ' . @ +."; SetupUserBox.Focus(); return; }
        if (password.Length == 0) { SetupStatus.Text = "Choisis un mot de passe : il protège ta bibliothèque sur le réseau."; SetupPasswordBox.Focus(); return; }
        if (MediaFolders.Refusal(root) is { } refusal) { SetupStatus.Text = refusal; SetupFolderBox.Focus(); return; }
        SetServerSetupBusy(true);
        var server = JellyfinSetup.LocalServer;
        try
        {
            SetupStatus.Text = "Recherche de Jellyfin sur ce PC…";
            var state = await JellyfinSetup.StateAsync(server);
            if (state is null && JellyfinInstaller.InstalledFolder() is null)
            {
                var version = JellyfinServerPackage.Current.Version; SetupStatus.Text = $"Téléchargement de Jellyfin {version}…";
                await JellyfinInstaller.InstallAsync(_profile.DirectoryPath, new Progress<double>(p => SetupStatus.Text = p < 1
                    ? $"Téléchargement de Jellyfin {version}… {Math.Floor(p * 100):0} %"
                    : "Installation de Jellyfin : accepte la demande d’autorisation de Windows…"));
            }
            if (state is null)
            {
                SetupStatus.Text = "Démarrage de Jellyfin… (la première fois peut prendre une minute)";
                state = await JellyfinSetup.WaitAsync(server, TimeSpan.FromMinutes(3));
            }
            if (state.WizardCompleted)
            {
                // Set up earlier, by its own page or another install: its account is the one to use.
                _serverSetupBusy = false; ShowServerSetup(false);
                ServerBox.Text = server.AbsoluteUri.TrimEnd('/'); UsernameBox.Text = user;
                LoginError.Text = "Jellyfin est déjà installé et configuré sur ce PC : connecte-toi avec ton compte Jellyfin.";
                return;
            }
            SetupStatus.Text = "Création des dossiers Films, Séries et Animes…";
            var folders = MediaFolders.Under(root);
            // The service reads the folders as Network Service: granted before Jellyfin is asked to add them.
            await Task.Run(() => { folders.Create(); folders.AllowJellyfinService(); });
            SetupStatus.Text = "Configuration de Jellyfin en français…";
            await JellyfinSetup.ConfigureAsync(server, user, password, folders);
            SetupStatus.Text = "Connexion…";
            Connection connection;
            using (var client = new JellyfinClient(new(server.AbsoluteUri, "", user, "", _profile.DeviceId))) connection = await client.LoginAsync(user, password);
            _profile.SaveConnection(connection); SetupPasswordBox.Clear();
            _serverSetupBusy = false; ShowSignInForm();
            await ActivateConnectionAsync(connection);
            SetNotice($"Jellyfin est prêt. Range tes vidéos dans {folders.Root} : Films, Séries ou Animes.");
        }
        catch (Exception ex) when (ex is InstallException or ArgumentException) { SetupFailed(ex.Message); }
        catch (Exception ex) when (IsExpected(ex)) { SetupFailed(Friendly(ex)); }
        finally { SetServerSetupBusy(false); }
    }
    /// <summary>Shown in the setup panel, and as a notice when the library was reopened during the installation.</summary>
    private void SetupFailed(string message)
    {
        SetupStatus.Text = message;
        if (LoginOverlay.Visibility != Visibility.Visible) SetNotice("Installation de Jellyfin : " + message);
    }
    private void SetServerSetupBusy(bool busy)
    {
        _serverSetupBusy = busy;
        foreach (UIElement control in new UIElement[] { SetupFolderBox, SetupFolderBrowse, SetupUserBox, SetupPasswordBox, SetupStartButton, SetupBackButton }) control.IsEnabled = !busy;
        SetupStartText.Text = busy ? "Installation en cours…" : "Installer et configurer";
    }
}
