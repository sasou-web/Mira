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
/// then signs in by a code typed in the guide shown once it is made (where they watch from, the address to send), or with
/// the password shown there (never kept).
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
        _body.Children.Clear(); _status.Text = ""; Made = null; _guide = null;
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
    /// <summary>
    /// After « Create their account », as on Windows (ogsas, maquette B « Le guide », 2026-11-18): where the friend watches
    /// from, at your home or somewhere else (the jelly switch of Quality), then the steps with the right address to copy
    /// (<see cref="FriendReach"/>): somewhere else, this Mac shared on Tailscale first, which Mira checks by itself.
    /// </summary>
    private void ShowDone(Session session, AuthUser friend, string password, bool codes, IReadOnlyList<string> shared)
    {
        _body.Children.Clear(); _status.Text = ""; Made = friend.Name;
        var server = session.Connection.Server;
        var here = Uri.TryCreate(server, UriKind.Absolute, out var uri) && LocalNetwork.IsThisPc(uri.Host);
        var home = FriendReach.HomeAddress(server, here, here ? LocalNetwork.ThisPc() : null);
        var away = FriendReach.AwayAddress(server, here, here ? LocalNetwork.ThisPcOnTailnet() : null);
        var guide = new Guide(friend, password, codes, shared, home, away, here);
        _guide = guide;
        var steps = new StackPanel();
        var card = new Border { Background = Ui.Inset, BorderBrush = Ui.MenuLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(18, 16, 18, 8) };
        var track = new ChoiceTrack(0, "friends.where");
        var chips = new List<Button>();
        foreach (var (elsewhere, label, icon) in new[] { (false, FriendReach.AtHome, "home"), (true, FriendReach.Elsewhere, "globe") })
        {
            var index = track.Count;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new Icon(icon, 15) { VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Button { Content = content, Classes = { "menuchip" }, MinWidth = 150, Padding = new Thickness(18, 6) };
            Avalonia.Automation.AutomationProperties.SetName(chip, label);
            chip.Click += (_, _) =>
            {
                track.Select(index);
                if (guide.Elsewhere == elsewhere) return;
                guide.Elsewhere = elsewhere; Render(guide, steps);
            };
            chips.Add(chip); track.Add(chip, elsewhere == guide.Elsewhere);
        }
        _where = chips;
        var switcher = new Border { Background = Ui.Inset, BorderBrush = Ui.Rule, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 8), Child = track };
        card.Child = Ui.Column(0, Ui.Heading(FriendReach.Question(friend.Name), 15, 3), switcher, steps);
        _body.Children.Add(card);
        _party.IsVisible = false;
        _body.Children.Add(_party);
        var again = Ui.Action(L("Ajouter un autre ami", "Add another friend"), "plus", "quiet", ShowForm);
        again.HorizontalAlignment = HorizontalAlignment.Left;
        _body.Children.Add(again);
        Render(guide, steps);
        _ = PartyAsync(session);
        if (guide.WatchesShares) _ = WatchSharesAsync(guide, steps);
    }

    private sealed class Guide(AuthUser friend, string password, bool codes, IReadOnlyList<string> shared, string? home, string? away, bool here)
    {
        public AuthUser Friend { get; } = friend;
        public string Password { get; } = password;
        public bool Codes { get; } = codes;
        public IReadOnlyList<string> Shared { get; } = shared;
        public string? Home { get; } = home;
        public string? Away { get; } = away;
        public bool Here { get; } = here;
        public bool Elsewhere { get; set; } = true;
        public IReadOnlyList<string>? Sharees { get; set; }
        public bool LetIn { get; set; }
        public string Code { get; set; } = "";
        public bool WatchesShares => Here && Away is not null;
    }
    private Guide? _guide;
    private List<Button> _where = [];
    /// <summary>For the self-check: the guide's steps shown, and where the friend watches from.</summary>
    public string? Where => _guide is null ? null : _guide.Elsewhere ? "elsewhere" : "home";
    /// <summary>For the self-check: a click on « At your home » or « Somewhere else ».</summary>
    public void WhereForCheck(bool elsewhere)
    { if (_where.Count == 2) _where[elsewhere ? 1 : 0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); }
    private readonly DockPanel _party = new() { Margin = new Thickness(2, 0, 0, 0) };

    private void Render(Guide guide, StackPanel panel)
    {
        if (!ReferenceEquals(guide, _guide)) return;
        panel.Children.Clear();
        var name = guide.Friend.Name;
        var steps = new List<(string Title, string Text, Control? Action, Control? Note, bool Done)> { (FriendReach.Created, FriendReach.CreatedLine(name, guide.Shared), null, null, true) };
        if (guide.Elsewhere)
        {
            var shared = guide.Sharees is { Count: > 0 };
            Control? note = !guide.WatchesShares || guide.Sharees is null ? null
                : Note(shared ? "check" : "clock", shared ? FriendReach.SharedWith(guide.Sharees!) : FriendReach.NotShared, shared ? Ui.Success : Ui.Warning);
            var open = Ui.Action(FriendReach.OpenTailscale, "external", shared ? "quiet" : "primary", BuiltInTailscale.OpenConsole);
            var text = guide.Here && guide.Away is null ? FriendReach.NotOnTailnet : guide.Here ? FriendReach.ShareLine(name) : FriendReach.ShareElsewhere(name);
            steps.Add((FriendReach.Share(name), text, open, note, shared));
            steps.Add((FriendReach.SendAway(name), guide.Away is null ? FriendReach.AwayUnknown : FriendReach.AwayLine(name), guide.Away is { } away ? CopyField(away, L("Copier l’adresse", "Copy the address")) : null, null, false));
        }
        else steps.Add((FriendReach.GiveHome(name), guide.Home is null ? FriendReach.HomeUnknown : FriendReach.HomeLine, guide.Home is { } home ? CopyField(home, L("Copier l’adresse", "Copy the address")) : null, null, false));
        steps.Add((FriendReach.LetIn, guide.Codes ? FriendReach.LetInCode(name) : FriendReach.LetInPassword(name),
            guide.LetIn ? null : guide.Codes ? CodeField(guide, panel) : CopyField(guide.Password, L("Copier son mot de passe", "Copy their password")), null, guide.LetIn));
        var now = steps.FindIndex(x => !x.Done);
        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0) panel.Children.Add(new Border { Height = 1, Background = Ui.MenuLine });
            panel.Children.Add(Step(i + 1, steps[i].Done, i == now, steps[i].Title, steps[i].Text, steps[i].Action, steps[i].Note));
        }
    }
    private static Grid Step(int number, bool done, bool current, string title, string text, Control? action, Control? note)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 12) };
        var lit = current && !done;
        var badge = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
            Background = done ? Ui.Ink : lit ? Brushes.Transparent : Ui.Raised2, BorderBrush = lit ? Ui.Ink : Brushes.Transparent, BorderThickness = new Thickness(lit ? 2 : 0),
            Child = done ? new Icon("check", 14) { Foreground = Ui.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new TextBlock { Text = number.ToString(Lang.Culture), FontSize = 12, FontWeight = FontWeight.Bold, Foreground = lit ? Ui.Ink : Ui.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Avalonia.Automation.AutomationProperties.SetName(badge, done ? L($"Étape {number}, faite", $"Step {number}, done") : L($"Étape {number}", $"Step {number}"));
        grid.Children.Add(badge);
        var words = Ui.Column(4, new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = done || lit ? Ui.Ink : Ui.TextBody }, Note(text));
        if (note is not null) words.Children.Add(note);
        Grid.SetColumn(words, 1); grid.Children.Add(words);
        if (action is not null) { action.Margin = new Thickness(16, 0, 0, 0); action.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(action, 2); grid.Children.Add(action); }
        return grid;
    }
    private static StackPanel Note(string icon, string text, IBrush ink)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        line.Children.Add(new Icon(icon, 14) { Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = ink, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        Avalonia.Automation.AutomationProperties.SetLiveSetting(line, Avalonia.Automation.AutomationLiveSetting.Polite);
        return line;
    }
    private StackPanel CopyField(string value, string name)
    {
        var box = new TextBox { Text = value, IsReadOnly = true, Width = 230, FontWeight = FontWeight.SemiBold };
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        var copy = Ui.Action(L("Copier", "Copy"), "link", "", () => _ = CopyAsync(value));
        Avalonia.Automation.AutomationProperties.SetName(copy, name);
        return Ui.Row(8, box, copy);
    }
    private StackPanel CodeField(Guide guide, StackPanel panel)
    {
        var box = new TextBox { Text = guide.Code, Width = 120, MaxLength = 7, FontSize = 16, Watermark = "000 000" };
        Avalonia.Automation.AutomationProperties.SetName(box, L($"Code de l’appareil de {guide.Friend.Name}", $"Code of {guide.Friend.Name}’s device"));
        box.TextChanged += (_, _) => guide.Code = box.Text ?? "";
        Button? allow = null;
        allow = Ui.Action(L("Autoriser", "Allow"), null, "primary", () => _ = ApproveAsync(guide, panel, box, allow!));
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = ApproveAsync(guide, panel, box, allow); } };
        return Ui.Row(8, box, allow);
    }
    private async Task ApproveAsync(Guide guide, StackPanel panel, TextBox box, Button button)
    {
        if (!button.IsEnabled || _shell.Session is not { } session) return;
        if (JellyfinClient.QuickConnectCode(box.Text) is not { } code) { _status.Text = L("Le code a 6 chiffres.", "The code has 6 digits."); box.Focus(); box.SelectAll(); return; }
        button.IsEnabled = false; _status.Text = L("Autorisation…", "Allowing…");
        try
        {
            var approval = await session.Client.ApproveCodeAsync(code, guide.Friend.Id, session.Lifetime);
            _status.Text = approval switch
            {
                CodeApproval.Approved => L($"Appareil autorisé : il se connecte au compte de {guide.Friend.Name}.", $"Device allowed: it’s signing in to {guide.Friend.Name}’s account."),
                CodeApproval.Unknown => L("Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.", "Unknown or expired code. Check it, or ask for a new one on the device."),
                _ => L("Quick Connect est désactivé sur ce serveur : active-le dans le tableau de bord de Jellyfin (Général).", "Quick Connect is turned off on this server: turn it on in Jellyfin’s dashboard (General)."),
            };
            button.IsEnabled = true;
            if (approval == CodeApproval.Approved) { guide.LetIn = true; guide.Code = ""; Render(guide, panel); }
            else { box.Focus(); box.SelectAll(); }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { button.IsEnabled = true; _status.Text = Errors.Friendly(ex); }
    }
    /// <summary>« Mira checks by itself »: who this Mac is shared with on Tailscale, every 10 s while this guide is on screen.</summary>
    private async Task WatchSharesAsync(Guide guide, StackPanel panel)
    {
        // Read only while the section is on screen; given up after 30 min (the share is a minute's job).
        var until = DateTime.UtcNow.AddMinutes(30);
        while (ReferenceEquals(guide, _guide) && DateTime.UtcNow < until)
        {
            if (!Section.IsEffectivelyVisible) { await Task.Delay(TimeSpan.FromSeconds(10)); continue; }
            var sharees = await Task.Run(() => FriendReach.Sharees(TailscaleCli.Status()));
            if (!ReferenceEquals(guide, _guide)) return;
            if (sharees is not null && (guide.Sharees is null || !guide.Sharees.SequenceEqual(sharees)))
            {
                guide.Sharees = sharees;
                if (guide.Elsewhere) Render(guide, panel);
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }
    /// <summary>« Only for tonight? »: this account's party link, when its Jellyfin makes one.</summary>
    private async Task PartyAsync(Session session)
    {
        PartyLink? party = null;
        try { party = await session.Client.PartyAsync(ct: session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        if (party is null || !ReferenceEquals(session, _shell.Session) || _guide is null) return;
        _party.Children.Clear();
        var copy = Ui.Action(FriendReach.CopyParty, "link", "quiet", () => _ = CopyAsync(party.Link));
        DockPanel.SetDock(copy, Dock.Right); copy.Margin = new Thickness(12, 0, 0, 0); copy.VerticalAlignment = VerticalAlignment.Center;
        var icon = new Icon("users", 15) { Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 9, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        _party.Children.Add(copy); _party.Children.Add(icon);
        _party.Children.Add(new TextBlock { Text = FriendReach.Party, FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        _party.IsVisible = true;
    }
    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(_shell)?.Clipboard is not { } clipboard) { _status.Text = L("Copie impossible ici : sélectionne le texte.", "Can’t copy here: select the text."); return; }
        await clipboard.SetTextAsync(text);
        _status.Text = L("Copié.", "Copied.");
    }
}
