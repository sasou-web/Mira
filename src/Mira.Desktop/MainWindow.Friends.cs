using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mira.Core;

namespace Mira.Desktop;

// « Ajouter un ami » (Réglages → Jellyfin, for an administrator): an account of their own on this Jellyfin, which sees
// only the libraries chosen and downloads nothing (Friends, JellyfinClient.CreateFriendAsync). Their phone then signs
// in by a code that « Autoriser un appareil » approves for that account, or with the password shown here once.
public partial class MainWindow
{
    private List<VirtualFolder> _friendFolders = [];
    private bool _friendBusy;
    private Task _friendShowing = Task.CompletedTask;
    /// <summary>The friend just added, with their password: in memory only, until another is added or the account changes.</summary>
    private (JellyfinClient Client, AuthUser User, string Password)? _friendMade;

    /// <summary>The row, for an administrator of a Jellyfin with a library to share; what was just made stays shown.</summary>
    private async Task ShowFriendAsync()
    {
        if (_friendBusy) return;
        FriendRow.Visibility = Visibility.Collapsed; FriendStatus.Text = "";
        if (_demo || _client is not { } client) return;
        if (_friendMade is { } made && !ReferenceEquals(made.Client, client)) _friendMade = null;
        List<VirtualFolder>? folders = null;
        try { if (await client.IsAdministratorAsync() == true) folders = await client.VirtualFoldersAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (!ReferenceEquals(client, _client) || folders is null || _friendBusy || Friends.Choices(folders).Count == 0) return;
        _friendFolders = folders;
        if (_friendMade is null) ShowFriendForm();
        FriendRow.Visibility = Visibility.Visible;
    }
    private void ShowFriendForm()
    {
        FriendDone.Visibility = Visibility.Collapsed; FriendForm.Visibility = Visibility.Visible; FriendPasswordBox.Clear();
        FriendLibraries.Children.Clear();
        foreach (var folder in Friends.Choices(_friendFolders))
        {
            var check = new CheckBox { Tag = folder.ItemId };
            AutomationProperties.SetName(check, L($"Partager « {folder.Name} »", $"Share “{folder.Name}”"));
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(check, Dock.Right); line.Children.Add(check);
            line.Children.Add(new TextBlock { Text = folder.Name, VerticalAlignment = VerticalAlignment.Center });
            FriendLibraries.Children.Add(line);
        }
    }
    private List<string> ChosenLibraries() =>
        [.. FriendLibraries.Children.OfType<DockPanel>().SelectMany(x => x.Children.OfType<CheckBox>()).Where(x => x.IsChecked == true).Select(x => (string)x.Tag)];
    private void FriendName_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; FriendCreate_Click(sender, e); } }
    private async void FriendCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _friendBusy) return;
        var name = Friends.CleanName(FriendNameBox.Text);
        if (Friends.NameProblem(name, []) is { } wrong) { FriendStatus.Text = wrong; FriendNameBox.Focus(); return; }
        var chosen = ChosenLibraries();
        if (chosen.Count == 0) { FriendStatus.Text = L("Choisis au moins une bibliothèque à partager.", "Choose at least one library to share."); return; }
        _friendBusy = true; FriendCreateButton.IsEnabled = false; FriendStatus.Text = L("Création de son compte…", "Creating their account…");
        var ct = _connectionLifetime.Token;
        try
        {
            if (await client.UsersAsync(ct) is not { } users) { FriendStatus.Text = L("Seul un administrateur de Jellyfin peut ajouter un compte.", "Only a Jellyfin administrator can add an account."); return; }
            if (Friends.NameProblem(name, users.Select(x => x.Name)) is { } taken) { FriendStatus.Text = taken; FriendNameBox.Focus(); FriendNameBox.SelectAll(); return; }
            var password = Friends.NewPassword();
            var friend = await client.CreateFriendAsync(name, password, Friends.Folders(_friendFolders, chosen), ct);
            var codes = false;
            try { codes = await client.QuickConnectEnabledAsync(ct); }
            catch (Exception ex) when (IsExpected(ex)) { }
            if (!ReferenceEquals(client, _client)) return;
            _friendMade = (client, friend, password);
            FriendNameBox.Clear(); FriendStatus.Text = "";
            var shared = Friends.Choices(_friendFolders).Where(x => chosen.Contains(x.ItemId!)).Select(x => x.Name).ToList();
            ShowFriendDone(client, friend, password, codes, shared);
            // Its phone is approved for its account below: the list of accounts now has it, chosen.
            if (codes) await ShowApproveDeviceAsync(friend.Id);
        }
        // Only Mira's own cancellation (another account, closing): HttpClient's 12 s limit is one too, and must be said.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { FriendStatus.Text = Friendly(ex); }
        finally { _friendBusy = false; FriendCreateButton.IsEnabled = true; }
    }
    private void ShowFriendDone(JellyfinClient client, AuthUser friend, string password, bool codes, IReadOnlyList<string> shared)
    {
        FriendForm.Visibility = Visibility.Collapsed; FriendDone.Visibility = Visibility.Visible;
        FriendDoneTitle.Text = L($"Compte de {friend.Name} créé, avec {Friends.Joined(shared)}.", $"{friend.Name}’s account is ready, with {Friends.Joined(shared)}.");
        var address = ReachableServer(client) + "/Mira";
        FriendDoneText.Text = codes
            ? L($"Sur son téléphone, {friend.Name} ouvre {address}, touche « Se connecter avec un code » et te donne les 6 chiffres : « Autoriser son téléphone » les saisit pour son compte. Aucun mot de passe à lui envoyer.",
                $"On their phone, {friend.Name} opens {address}, taps “Sign in with a code” and gives you the 6 digits: “Allow their phone” enters them for their account. No password to send them.")
            : L($"{friend.Name} se connecte sur son téléphone ({address}) ou dans Mira pour Windows ou Mac avec son prénom et ce mot de passe. Envoie-le-lui : Mira ne le garde pas.",
                $"{friend.Name} signs in on their phone ({address}) or in Mira for Windows or Mac with their name and this password. Send it to them: Mira doesn’t keep it.");
        FriendPasswordBox.Text = password;
        FriendPasswordPanel.Visibility = codes ? Visibility.Collapsed : Visibility.Visible;
        FriendPasswordButton.Visibility = codes ? Visibility.Visible : Visibility.Collapsed;
        FriendApproveButton.Visibility = codes ? Visibility.Visible : Visibility.Collapsed;
        _ = Dispatcher.BeginInvoke(() => { if (codes) FriendApproveButton.Focus(); else FriendPasswordBox.Focus(); }, DispatcherPriority.Input);
    }
    /// <summary>« Autoriser son téléphone »: the row below, on the friend's account, ready for the 6 digits.</summary>
    private void FriendApprove_Click(object sender, RoutedEventArgs e)
    {
        if (_friendMade is not { } made || ApproveDeviceRow.Visibility != Visibility.Visible) return;
        SelectApproveAccount(made.User.Id);
        ApproveDeviceRow.BringIntoView();
        ApproveDeviceStatus.Text = L($"Saisis les 6 chiffres que {made.User.Name} voit sur son téléphone.", $"Enter the 6 digits {made.User.Name} sees on their phone.");
        ApproveCodeBox.Focus();
    }
    private void FriendPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_friendMade is not { } made) return;
        FriendPasswordPanel.Visibility = Visibility.Visible; FriendPasswordButton.Visibility = Visibility.Collapsed;
        FriendStatus.Text = L($"Sans code, {made.User.Name} se connecte avec son prénom et ce mot de passe, sur son téléphone ou dans Mira pour Windows ou Mac. Mira ne le garde pas.", $"Without a code, {made.User.Name} signs in with their name and this password, on their phone or in Mira for Windows or Mac. Mira doesn’t keep it.");
        FriendPasswordBox.Focus(); FriendPasswordBox.SelectAll();
    }
    private void FriendPasswordCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(FriendPasswordBox.Text); FriendStatus.Text = L("Mot de passe copié.", "Password copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { FriendStatus.Text = L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again."); }
    }
    private void FriendAgain_Click(object sender, RoutedEventArgs e)
    {
        _friendMade = null; FriendStatus.Text = "";
        ShowFriendForm();
        FriendNameBox.Focus();
    }
    private void FriendTailscale_Click(object sender, RoutedEventArgs e) => OpenWebPage("https://login.tailscale.com/admin/machines");
    /// <summary>« Ajouter un ami » of the invitation: the row in Réglages → Jellyfin, its name to type.</summary>
    private async void InviteAddFriend_Click(object sender, RoutedEventArgs e)
    {
        await CloseInviteAsync();
        Settings_Click(this, new());
        SelectSettingsTab("server");
        await _friendShowing;
        if (FriendRow.Visibility != Visibility.Visible || SettingsOverlay.Visibility != Visibility.Visible) return;
        FriendRow.BringIntoView();
        if (FriendForm.Visibility == Visibility.Visible) FriendNameBox.Focus();
    }
    /// <summary>The invitation's « Ajouter un ami », for an administrator only.</summary>
    private async Task ShowInviteAddFriendAsync(JellyfinClient client)
    {
        InviteAddFriend.Visibility = Visibility.Collapsed;
        bool? admin = null;
        try { admin = await client.IsAdministratorAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { }
        if (admin == true && ReferenceEquals(client, _client) && InviteOverlay.Visibility == Visibility.Visible) InviteAddFriend.Visibility = Visibility.Visible;
    }
}
