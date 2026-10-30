using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Ajouter un ami » (Réglages, an administrator), as on Windows: an account of their own on this Jellyfin, which sees
/// only the libraries chosen and downloads nothing (<see cref="Friends"/>, JellyfinClient.CreateFriendAsync). Their phone
/// then signs in by a code typed here for that account, or with the password shown once (never kept).
/// </summary>
public sealed class FriendsSection
{
    private readonly MainWindow _shell;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private List<VirtualFolder> _folders = [];
    private bool _busy;

    public Control Section { get; }
    /// <summary>For the self-check: the libraries offered.</summary>
    public int Libraries => Friends.Choices(_folders).Count;
    /// <summary>For the self-check: the account made, once Mira shows how their phone signs in.</summary>
    public string? Made { get; private set; }
    /// <summary>For the self-check: « Créer son compte » for <paramref name="name"/> with the first library.</summary>
    public void CreateForCheck(string name)
    {
        if (_body.Children.OfType<TextBox>().FirstOrDefault() is not { } box || _body.Children.OfType<Button>().LastOrDefault() is not { } button) return;
        box.Text = name;
        _ = CreateAsync(box, [Friends.Choices(_folders)[0].ItemId!], button);
    }

    public FriendsSection(MainWindow shell, Func<string, Control[], Control> section)
    {
        _shell = shell;
        Section = section(L("Ajouter un ami", "Add a friend"), [Ui.Column(14, _body, _status)]);
        Section.IsVisible = false;
        _ = LoadAsync();
    }
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };

    private async Task LoadAsync()
    {
        if (_shell.Session is not { } session) return;
        try
        {
            if (await session.Client.IsAdministratorAsync(session.Lifetime) != true) return;
            var folders = await session.Client.VirtualFoldersAsync(session.Lifetime);
            if (folders is null || Friends.Choices(folders).Count == 0 || !ReferenceEquals(session, _shell.Session)) return;
            _folders = folders; ShowForm(); Section.IsVisible = true;
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    private void ShowForm()
    {
        _body.Children.Clear(); _status.Text = ""; Made = null;
        var name = new TextBox { Width = 260, MaxLength = 64, Watermark = L("Son prénom", "Their name") };
        Avalonia.Automation.AutomationProperties.SetName(name, L("Prénom de ton ami", "Your friend’s name"));
        var libraries = new StackPanel { Spacing = 2 };
        var checks = new List<(CheckBox Box, string Id)>();
        foreach (var folder in Friends.Choices(_folders))
        {
            var check = new CheckBox { Content = folder.Name };
            Avalonia.Automation.AutomationProperties.SetName(check, L($"Partager « {folder.Name} »", $"Share “{folder.Name}”"));
            checks.Add((check, folder.ItemId!)); libraries.Children.Add(check);
        }
        Button? create = null;
        create = Ui.Action(L("Créer son compte", "Create their account"), "plus", "primary", () => _ = CreateAsync(name, checks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToList(), create!));
        name.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = CreateAsync(name, checks.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToList(), create); } };
        _body.Children.Add(Note(L("Un compte à lui sur ton Jellyfin : il ne voit que les bibliothèques choisies et ne télécharge rien. Son téléphone se connecte ensuite avec un code, sans mot de passe à lui envoyer.",
            "An account of their own on your Jellyfin: they only see the libraries you choose and can’t download anything. Their phone then signs in with a code, no password to send them.")));
        _body.Children.Add(name);
        _body.Children.Add(Ui.Column(6, new TextBlock { Text = L("BIBLIOTHÈQUES PARTAGÉES", "SHARED LIBRARIES"), Classes = { "label" } }, libraries));
        _body.Children.Add(create);
    }
    private async Task CreateAsync(TextBox box, List<string> chosen, Button button)
    {
        if (_busy || _shell.Session is not { } session) return;
        var client = session.Client; var ct = session.Lifetime;
        var name = Friends.CleanName(box.Text);
        if (Friends.NameProblem(name, []) is { } wrong) { _status.Text = wrong; box.Focus(); return; }
        if (chosen.Count == 0) { _status.Text = L("Choisis au moins une bibliothèque à partager.", "Choose at least one library to share."); return; }
        _busy = true; button.IsEnabled = false; _status.Text = L("Création de son compte…", "Creating their account…");
        try
        {
            if (await client.UsersAsync(ct) is not { } users) { _status.Text = L("Seul un administrateur de Jellyfin peut ajouter un compte.", "Only a Jellyfin administrator can add an account."); return; }
            if (Friends.NameProblem(name, users.Select(x => x.Name)) is { } taken) { _status.Text = taken; box.Focus(); box.SelectAll(); return; }
            var password = Friends.NewPassword();
            var friend = await client.CreateFriendAsync(name, password, Friends.Folders(_folders, chosen), ct);
            var codes = false;
            try { codes = await client.QuickConnectEnabledAsync(ct); }
            catch (Exception ex) when (Errors.Expected(ex)) { }
            if (!ReferenceEquals(session, _shell.Session)) return;
            var shared = Friends.Choices(_folders).Where(x => chosen.Contains(x.ItemId!)).Select(x => x.Name).ToList();
            ShowDone(session, friend, password, codes, shared);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); }
        finally { _busy = false; button.IsEnabled = true; }
    }
    /// <summary>The address their phone opens: this Mac's Tailscale address when Jellyfin runs here, as the group's links.</summary>
    private static string Reachable(Session session)
    {
        var server = session.Connection.Server;
        var here = Uri.TryCreate(server, UriKind.Absolute, out var uri) && LocalNetwork.IsThisPc(uri.Host);
        return Together.InviteServer(server, here, here ? LocalNetwork.ThisPcOnTailnet() : null, here ? LocalNetwork.ThisPc() : null).TrimEnd('/');
    }
    private void ShowDone(Session session, AuthUser friend, string password, bool codes, IReadOnlyList<string> shared)
    {
        _body.Children.Clear(); _status.Text = ""; Made = friend.Name;
        var address = Reachable(session) + "/Mira";
        _body.Children.Add(new TextBlock { Text = L($"Compte de {friend.Name} créé, avec {Friends.Joined(shared)}.", $"{friend.Name}’s account is ready, with {Friends.Joined(shared)}."), FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _body.Children.Add(Note(codes
            ? L($"Sur son téléphone, {friend.Name} ouvre {address}, touche « Se connecter avec un code » et te donne les 6 chiffres : saisis-les ici, ils valent pour son compte. Aucun mot de passe à lui envoyer.",
                $"On their phone, {friend.Name} opens {address}, taps “Sign in with a code” and gives you the 6 digits: type them here, they count for their account. No password to send them.")
            : L($"{friend.Name} se connecte sur son téléphone ({address}) ou dans Mira pour Windows ou Mac avec son prénom et ce mot de passe. Envoie-le-lui : Mira ne le garde pas.",
                $"{friend.Name} signs in on their phone ({address}) or in Mira for Windows or Mac with their name and this password. Send it to them: Mira doesn’t keep it.")));
        var secret = new TextBox { Text = password, IsReadOnly = true, Width = 220, FontFamily = new FontFamily("Menlo, monospace") };
        Avalonia.Automation.AutomationProperties.SetName(secret, L($"Mot de passe de {friend.Name}", $"{friend.Name}’s password"));
        var copy = Ui.Action(L("Copier", "Copy"), null, "", () => _ = CopyAsync(password));
        var passwordRow = Ui.Row(10, secret, copy);
        if (codes)
        {
            var box = new TextBox { Width = 150, MaxLength = 7, FontSize = 16, Watermark = "000 000" };
            Avalonia.Automation.AutomationProperties.SetName(box, L($"Code du téléphone de {friend.Name}", $"Code of {friend.Name}’s phone"));
            Button? allow = null;
            allow = Ui.Action(L("Autoriser son téléphone", "Allow their phone"), null, "primary", () => _ = ApproveAsync(session, friend, box, allow!));
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = ApproveAsync(session, friend, box, allow); } };
            _body.Children.Add(Ui.Row(10, box, allow));
            passwordRow.IsVisible = false;
            Button? show = null;
            show = Ui.Action(L("Voir son mot de passe", "Show their password"), null, "quiet", () => { passwordRow.IsVisible = true; show!.IsVisible = false; _status.Text = L($"Sans code, {friend.Name} se connecte avec son prénom et ce mot de passe. Mira ne le garde pas.", $"Without a code, {friend.Name} signs in with their name and this password. Mira doesn’t keep it."); });
            show.HorizontalAlignment = HorizontalAlignment.Left;
            _body.Children.Add(show);
        }
        _body.Children.Add(passwordRow);
        var again = Ui.Action(L("Ajouter un autre ami", "Add another friend"), "plus", "quiet", ShowForm);
        again.HorizontalAlignment = HorizontalAlignment.Left;
        _body.Children.Add(again);
    }
    private async Task ApproveAsync(Session session, AuthUser friend, TextBox box, Button button)
    {
        if (!button.IsEnabled) return;
        if (JellyfinClient.QuickConnectCode(box.Text) is not { } code) { _status.Text = L("Le code a 6 chiffres.", "The code has 6 digits."); box.Focus(); box.SelectAll(); return; }
        button.IsEnabled = false; _status.Text = L("Autorisation…", "Allowing…");
        try
        {
            var approval = await session.Client.ApproveCodeAsync(code, friend.Id, session.Lifetime);
            _status.Text = approval switch
            {
                CodeApproval.Approved => L($"Téléphone autorisé : il se connecte au compte de {friend.Name}.", $"Phone allowed: it’s signing in to {friend.Name}’s account."),
                CodeApproval.Unknown => L("Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur le téléphone.", "Unknown or expired code. Check it, or ask the phone for a new one."),
                _ => L("Quick Connect est désactivé sur ce serveur : active-le dans le tableau de bord de Jellyfin (Général).", "Quick Connect is turned off on this server: turn it on in Jellyfin’s dashboard (General)."),
            };
            if (approval == CodeApproval.Approved) box.Text = ""; else { box.Focus(); box.SelectAll(); }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); }
        finally { button.IsEnabled = true; }
    }
    private async Task CopyAsync(string password)
    {
        if (TopLevel.GetTopLevel(_shell)?.Clipboard is not { } clipboard) { _status.Text = L("Copie impossible ici : sélectionne le mot de passe.", "Can’t copy here: select the password."); return; }
        await clipboard.SetTextAsync(password);
        _status.Text = L("Mot de passe copié.", "Password copied.");
    }
}
