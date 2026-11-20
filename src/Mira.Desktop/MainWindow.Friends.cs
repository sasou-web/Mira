using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.TorLink;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « Ajouter un ami » (Réglages → Jellyfin, for an administrator): an account of their own on this Jellyfin, which sees
// only the libraries chosen and downloads nothing (Friends, JellyfinClient.CreateFriendAsync). Their phone then signs
// in by a code allowed right in the guide shown once it is made (where they watch from, the address to send: FriendReach),
// or with the password shown there once.
public partial class MainWindow
{
    private List<VirtualFolder> _friendFolders = [];
    private bool _friendBusy;
    private Task _friendShowing = Task.CompletedTask, _approveShowing = Task.CompletedTask;
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
        FriendDone.Visibility = Visibility.Collapsed; FriendForm.Visibility = Visibility.Visible; FriendSteps.Children.Clear(); FriendStatus.Text = "";
        _friendGuide?.Watch?.Stop(); _friendGuide = null; _friendCodeBox = null;
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
            ShowFriendDone(client, friend, codes, shared);
            // « Allow a device » below lists it too, for a phone later.
            if (codes) await ShowApproveDeviceAsync();
        }
        // Only Mira's own cancellation (another account, closing): HttpClient's 12 s limit is one too, and must be said.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { FriendStatus.Text = Friendly(ex); }
        finally { _friendBusy = false; FriendCreateButton.IsEnabled = true; }
    }
    /// <summary>
    /// The friend's guide, as it stands: Quick Connect on the server, the libraries named, the away choice, who shares this
    /// PC. Réglages shows it for the account just made; Aide (<see cref="Help"/>) at any time, for « ton ami ».
    /// </summary>
    private sealed class FriendGuide
    {
        public required string Name { get; init; }
        public required StackPanel Steps { get; init; }
        public IReadOnlyList<string> Shared { get; init; } = [];
        public bool Codes { get; init; }
        /// <summary>Aide's guide: no account made here, so its creation and « Allow a device » are where Réglages has them.</summary>
        public bool Help { get; init; }
        /// <summary>Aide's guide: this account may make one (Réglages → Amis et invités).</summary>
        public bool Admin { get; set; }
        public string? Home { get; init; }
        public string? Away { get; init; }
        /// <summary>Jellyfin runs on this PC: the guide speaks of « this PC », and Mira reads its Tailscale shares.</summary>
        public bool Here { get; init; }
        public bool Elsewhere { get; set; } = true;
        public IReadOnlyList<string>? Sharees { get; set; }
        public bool LetIn { get; set; }
        public bool WatchesShares => Elsewhere && Here && Away is not null;
        public DispatcherTimer? Watch { get; set; }
        public bool Reading { get; set; }
        /// <summary>Réglages' guide of an account just made: its id, for a friend's link.</summary>
        public string? UserId { get; init; }
        /// <summary>The home is on Tailscale: « Somewhere else » is one link (maquette A « Le lien »), not Tailscale's sharing.</summary>
        public bool Links { get; set; }
        public Mira.Core.Family.FriendLinkMade? Link { get; set; }
    }
    private FriendGuide? _friendGuide, _helpFriendGuide;
    private TextBox? _friendCodeBox;

    private void ShowFriendDone(JellyfinClient client, AuthUser friend, bool codes, IReadOnlyList<string> shared)
    {
        FriendForm.Visibility = Visibility.Collapsed; FriendDone.Visibility = Visibility.Visible;
        var server = client.Connection.Server;
        var here = Uri.TryCreate(server, UriKind.Absolute, out var uri) && LocalNetwork.IsThisPc(uri.Host);
        _friendGuide?.Watch?.Stop();
        _friendGuide = new FriendGuide
        {
            Name = friend.Name, Steps = FriendSteps, Shared = shared, Codes = codes, Here = here, UserId = friend.Id,
            Home = FriendReach.HomeAddress(server, here, here ? LocalNetwork.ThisPc() : null),
            Away = FriendReach.AwayAddress(server, here, here ? LocalNetwork.ThisPcOnTailnet() : null),
        };
        FriendWhereTitle.Text = FriendReach.Question(friend.Name);
        // Somewhere else first: the case a home address cannot serve (ogsas's friend, 2026-11-18).
        FriendElsewhere.IsChecked = true;
        FriendWhere.Selected = 1;
        RenderFriendGuide();
        _ = ShowFriendPartyAsync(client);
        _ = OfferFriendLinkAsync(client, _friendGuide);
        _ = Dispatcher.BeginInvoke(() => FriendElsewhere.Focus(), DispatcherPriority.Input);
    }
    private void FriendWhere_Checked(object sender, RoutedEventArgs e)
    {
        var help = ReferenceEquals(sender, HelpFriendAtHome) || ReferenceEquals(sender, HelpFriendElsewhere);
        if ((help ? _helpFriendGuide : _friendGuide) is not { } guide) return;
        var elsewhere = ReferenceEquals(sender, help ? HelpFriendElsewhere : FriendElsewhere);
        if (guide.Elsewhere == elsewhere && guide.Steps.Children.Count > 0) return;
        guide.Elsewhere = elsewhere;
        // « Copied. » spoke of the other choice's address.
        (help ? HelpFriendStatus : FriendStatus).Text = "";
        RenderFriendGuide(guide);
    }
    private void RenderFriendGuide() { if (_friendGuide is { } guide) RenderFriendGuide(guide); }
    /// <summary>
    /// The steps of the choice made, the first one left to do marked; « Mira checks by itself » while it waits for the
    /// share. Aide's guide marks none: its account and its device are done in Réglages, which its buttons open.
    /// </summary>
    private void RenderFriendGuide(FriendGuide guide)
    {
        var made = _friendMade;
        if (!guide.Help && made is null) return;
        var code = guide.Help ? "" : _friendCodeBox?.Text ?? "";
        guide.Steps.Children.Clear();
        var steps = new List<(string Title, string Text, FrameworkElement? Action, FrameworkElement? Note, bool Done)>
        {
            guide.Help
                ? (FriendReach.Account, FriendReach.AccountLine(guide.Admin), guide.Admin ? FriendButton(FriendReach.CreateAccount, "plus", (_, _) => _ = OpenFriendSettingsAsync(device: false), primary: false) : null, null, false)
                : (FriendReach.Created, FriendReach.CreatedLine(guide.Name, guide.Shared), null, null, true),
        };
        if (guide.Elsewhere && guide.Links && !guide.Help)
        {
            // One link, from anywhere (maquette A « Le lien »): no Tailscale to open, no address, no code.
            FrameworkElement action = guide.Link is { } made2 ? FriendCopyField(made2.Link, FriendText.CopyLink)
                : FriendButton(FriendText.Invite(guide.Name), "link", (_, _) => _ = MakeFriendLinkAsync(guide), primary: true);
            steps.Add((FriendText.SendTo(guide.Name), FriendText.SendLine, action, guide.Link is { } shown ? FriendLinkQr(shown.Link) : null, guide.Link is not null));
            steps.Add((L($"Laisse entrer son appareil", "Let their device in"), L($"Quand {guide.Name} ouvre le lien, « Laisser entrer » apparaît sous Amis, juste au-dessus. Rien ne s’ouvre sans toi.", $"When {guide.Name} opens the link, “Let in” shows under Friends, just above. Nothing opens without you."), null, null, false));
            var first = steps.FindIndex(x => !x.Done);
            for (var i = 0; i < steps.Count; i++)
            {
                if (i > 0) guide.Steps.Children.Add(new Border { Height = 1, Background = Palette.Get("Line") });
                guide.Steps.Children.Add(FriendStep(i + 1, steps[i].Done, i == first, steps[i].Title, steps[i].Text, steps[i].Action, steps[i].Note));
            }
            guide.Watch?.Stop();
            return;
        }
        if (guide.Elsewhere)
        {
            var shared = guide.Sharees is { Count: > 0 };
            FrameworkElement? note = !guide.WatchesShares || guide.Sharees is null ? null
                : FriendNote(shared ? "check" : "clock", shared ? FriendReach.SharedWith(guide.Sharees!) : FriendReach.NotShared, shared ? "Success" : "Warning");
            var open = FriendButton(FriendReach.OpenTailscale, "external", (_, _) => OpenWebPage(TailscaleNode.Machines), primary: !shared);
            var text = guide.Here && guide.Away is null ? FriendReach.NotOnTailnet : guide.Here ? FriendReach.ShareLine(guide.Name) : FriendReach.ShareElsewhere(guide.Name);
            steps.Add((FriendReach.Share(guide.Name), text, open, note, shared));
            steps.Add((FriendReach.SendAway(guide.Name), guide.Away is null ? FriendReach.AwayUnknown : FriendReach.AwayLine(guide.Name),
                guide.Away is { } away ? FriendCopyField(away, L("Copier l’adresse", "Copy the address")) : null, null, false));
        }
        else steps.Add((FriendReach.GiveHome(guide.Name), guide.Home is null ? FriendReach.HomeUnknown : FriendReach.HomeLine,
            guide.Home is { } home ? FriendCopyField(home, L("Copier l’adresse", "Copy the address")) : null, null, false));
        if (guide.Help)
            steps.Add((FriendReach.LetIn, FriendReach.LetInHelp, guide.Admin ? FriendButton(FriendReach.AllowDevice, "phone", (_, _) => _ = OpenFriendSettingsAsync(device: true), primary: false) : null, null, false));
        else
        {
            _friendCodeBox = null;
            steps.Add((FriendReach.LetIn, guide.Codes ? FriendReach.LetInCode(guide.Name) : FriendReach.LetInPassword(guide.Name),
                guide.LetIn ? null : guide.Codes ? FriendCodeField(code) : FriendCopyField(made!.Value.Password, L("Copier son mot de passe", "Copy their password")), null, guide.LetIn));
        }
        var now = guide.Help ? -1 : steps.FindIndex(x => !x.Done);
        for (var i = 0; i < steps.Count; i++)
        {
            if (i > 0) guide.Steps.Children.Add(new Border { Height = 1, Background = Palette.Get("Line") });
            guide.Steps.Children.Add(FriendStep(i + 1, steps[i].Done, i == now, steps[i].Title, steps[i].Text, steps[i].Action, steps[i].Note));
        }
        WatchFriendShare(guide);
    }
    private FrameworkElement FriendStep(int number, bool done, bool current, string title, string text, FrameworkElement? action, FrameworkElement? note)
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lit = current && !done;
        var badge = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
            Background = done ? Palette.Get("Text") : lit ? Brushes.Transparent : Palette.Get("Raised2"),
            BorderBrush = lit ? Palette.Get("Text") : Brushes.Transparent, BorderThickness = new Thickness(lit ? 2 : 0),
            Child = done ? new Icon { Kind = "check", Width = 14, Height = 14, Foreground = Palette.Get("OnAccent") }
                : new TextBlock { Text = number.ToString(Lang.Culture), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Palette.Get(lit ? "Text" : "Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetName(badge, done ? L($"Étape {number}, faite", $"Step {number}, done") : L($"Étape {number}", $"Step {number}"));
        grid.Children.Add(badge);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        words.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Get(done || lit ? "Text" : "TextBody") });
        words.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 4, 0, 0) });
        if (note is not null) words.Children.Add(note);
        Grid.SetColumn(words, 1); grid.Children.Add(words);
        if (action is not null) { action.Margin = new Thickness(16, 0, 0, 0); action.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(action, 2); grid.Children.Add(action); }
        return grid;
    }
    private static StackPanel FriendNote(string icon, string text, string ink)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        line.Children.Add(new Icon { Kind = icon, Width = 14, Height = 14, Foreground = Palette.Get(ink), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Palette.Get(ink), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
        return line;
    }
    private Button FriendButton(string text, string icon, RoutedEventHandler click, bool primary)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Kind = icon, Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0) });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content };
        if (!primary) button.Style = (Style)FindResource("Quiet");
        AutomationProperties.SetName(button, text);
        button.Click += click;
        return button;
    }
    /// <summary>An address or a password to send, and « Copy » beside it.</summary>
    private DockPanel FriendCopyField(string value, string copyName)
    {
        var line = new DockPanel();
        var copy = FriendButton(L("Copier", "Copy"), "link", (_, _) => CopyFriendText(value), primary: false);
        AutomationProperties.SetName(copy, copyName);
        copy.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(copy, Dock.Right); line.Children.Add(copy);
        var box = new TextBox { Text = value, IsReadOnly = true, Width = 230, FontWeight = FontWeights.SemiBold, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, copyName);
        line.Children.Add(box);
        return line;
    }
    /// <summary>The 6 digits the friend reads out, allowed for their account right here.</summary>
    private DockPanel FriendCodeField(string code)
    {
        var line = new DockPanel();
        var allow = new Button { Content = L("Autoriser", "Allow"), Margin = new Thickness(8, 0, 0, 0) };
        allow.Click += FriendAllow_Click;
        DockPanel.SetDock(allow, Dock.Right); line.Children.Add(allow);
        var box = new TextBox { Text = code, Width = 120, MaxLength = 7, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, L("Les 6 chiffres de son appareil", "The 6 digits of their device"));
        box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; FriendAllow_Click(s, e); } };
        line.Children.Add(box);
        _friendCodeBox = box;
        return line;
    }
    private async void FriendAllow_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _friendMade is not { } made || _friendGuide is not { } guide || _friendCodeBox is not { IsEnabled: true } box) return;
        if (JellyfinClient.QuickConnectCode(box.Text) is not { } code) { FriendStatus.Text = L("Le code a 6 chiffres.", "The code has 6 digits."); box.Focus(); box.SelectAll(); return; }
        box.IsEnabled = false; FriendStatus.Text = L("Autorisation…", "Allowing…");
        try
        {
            var approval = await client.ApproveCodeAsync(code, made.User.Id);
            FriendStatus.Text = approval switch
            {
                CodeApproval.Approved => L($"Appareil autorisé : il se connecte au compte de {guide.Name}.", $"Device allowed: it’s signing in to {guide.Name}’s account."),
                CodeApproval.Unknown => L("Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.", "Unknown or expired code. Check it, or ask for a new one on the device."),
                _ => L("Quick Connect est désactivé sur ce serveur : active-le dans le tableau de bord de Jellyfin (Général).", "Quick Connect is off on this server: turn it on in Jellyfin’s Dashboard (General)."),
            };
            box.IsEnabled = true;
            if (approval == CodeApproval.Approved) { guide.LetIn = true; RenderFriendGuide(); }
            else { box.Focus(); box.SelectAll(); }
        }
        catch (Exception ex) when (IsExpected(ex)) { box.IsEnabled = true; FriendStatus.Text = Friendly(ex); }
    }
    private void CopyFriendText(string text)
    {
        // Said under the guide it was copied from: Réglages' or Aide's.
        var status = GuideFriend.IsVisible ? HelpFriendStatus : FriendStatus;
        try { Clipboard.SetText(text); status.Text = L("Copié.", "Copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { status.Text = L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again."); }
    }
    private bool Current(FriendGuide guide) => ReferenceEquals(guide, _friendGuide) || ReferenceEquals(guide, _helpFriendGuide);
    /// <summary>
    /// « Mira checks by itself »: while the away steps show for a Jellyfin on this PC, who this PC is shared with on
    /// Tailscale, read every 10 s from its command line (FriendReach.Sharees), until the guide or its window goes.
    /// </summary>
    private void WatchFriendShare(FriendGuide guide)
    {
        if (!guide.WatchesShares || !Current(guide)) { guide.Watch?.Stop(); return; }
        if (guide.Watch is null)
        {
            var watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            watch.Tick += (_, _) =>
            {
                if (!Current(guide) || !guide.Steps.IsVisible) { watch.Stop(); return; }
                _ = ReadFriendSharesAsync(guide);
            };
            guide.Watch = watch;
        }
        if (!guide.Watch.IsEnabled) guide.Watch.Start();
        if (guide.Sharees is null) _ = ReadFriendSharesAsync(guide);
    }
    private async Task ReadFriendSharesAsync(FriendGuide guide)
    {
        if (guide.Reading) return;
        guide.Reading = true;
        try
        {
            var sharees = await Task.Run(() => FriendReach.Sharees(TailscaleCli.Status()));
            if (!Current(guide) || sharees is null || guide.Sharees is { } before && before.SequenceEqual(sharees)) return;
            guide.Sharees = sharees;
            if (guide.WatchesShares) RenderFriendGuide(guide);
        }
        finally { guide.Reading = false; }
    }
    /// <summary>
    /// Aide → « Inviter un ami » (ogsas, 2026-11-18: « toujours à portée de main »): the same guide, for a friend whose
    /// account is made or to make, drawn again each time the subject opens (the account or the server may have changed).
    /// </summary>
    private void ShowHelpFriend()
    {
        _helpFriendGuide?.Watch?.Stop();
        HelpFriendStatus.Text = "";
        var server = _demo ? null : _client?.Connection.Server;
        var here = Uri.TryCreate(server, UriKind.Absolute, out var uri) && LocalNetwork.IsThisPc(uri.Host);
        var guide = new FriendGuide
        {
            Name = FriendReach.AFriend, Steps = HelpFriendSteps, Help = true, Here = here,
            Home = server is null ? null : FriendReach.HomeAddress(server, here, here ? LocalNetwork.ThisPc() : null),
            Away = server is null ? null : FriendReach.AwayAddress(server, here, here ? LocalNetwork.ThisPcOnTailnet() : null),
        };
        _helpFriendGuide = guide;
        HelpFriendTitle.Text = FriendReach.Capital(FriendReach.Question(FriendReach.AFriend));
        HelpFriendElsewhere.IsChecked = true;
        HelpFriendWhere.Selected = 1;
        RenderFriendGuide(guide);
        if (!_demo && _client is { } client) _ = HelpFriendAdminAsync(client, guide);
    }
    /// <summary>« Créer son compte » and « Autoriser un appareil » for an administrator, who has them in Réglages.</summary>
    private async Task HelpFriendAdminAsync(JellyfinClient client, FriendGuide guide)
    {
        bool? admin = null;
        try { admin = await client.IsAdministratorAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { }
        if (admin != true || !ReferenceEquals(guide, _helpFriendGuide) || !ReferenceEquals(client, _client)) return;
        guide.Admin = true;
        RenderFriendGuide(guide);
    }
    /// <summary>From Aide's guide: Réglages → Amis et invités, on « Ajouter un ami » or « Autoriser un appareil ».</summary>
    private async Task OpenFriendSettingsAsync(bool device)
    {
        Settings_Click(this, new());
        SelectSettingsTab("sharing");
        await _friendShowing;
        if (SettingsOverlay.Visibility != Visibility.Visible) return;
        if (device)
        {
            await _approveShowing;
            if (SettingsOverlay.Visibility != Visibility.Visible || ApproveDeviceRow.Visibility != Visibility.Visible) return;
            ApproveDeviceRow.BringIntoView();
            _ = Dispatcher.BeginInvoke(() => ApproveCodeBox.Focus(), DispatcherPriority.Input);
        }
        else
        {
            if (FriendRow.Visibility != Visibility.Visible) return;
            FriendRow.BringIntoView();
            if (FriendForm.Visibility == Visibility.Visible) _ = Dispatcher.BeginInvoke(() => FriendNameBox.Focus(), DispatcherPriority.Input);
        }
    }
    /// <summary>« Only for tonight? »: this account's party link, when its Jellyfin makes one.</summary>
    private async Task ShowFriendPartyAsync(JellyfinClient client)
    {
        FriendPartyLine.Visibility = Visibility.Collapsed;
        PartyLink? party = null;
        try { party = await client.PartyAsync(ct: _connectionLifetime.Token); }
        catch (Exception ex) when (ex is RelayRefusedException || IsExpected(ex)) { }
        if (party is null || !ReferenceEquals(client, _client) || FriendDone.Visibility != Visibility.Visible) return;
        FriendPartyText.Text = FriendReach.Party;
        FriendPartyButton.Content = FriendReach.CopyParty;
        FriendPartyButton.Tag = party.Link;
        FriendPartyLine.Visibility = Visibility.Visible;
    }
    private void FriendPartyCopy_Click(object sender, RoutedEventArgs e)
    { if (FriendPartyButton.Tag is string link) CopyFriendText(link); }
    private void FriendAgain_Click(object sender, RoutedEventArgs e)
    {
        _friendMade = null; FriendStatus.Text = "";
        ShowFriendForm();
        FriendNameBox.Focus();
    }
    /// <summary>« Ajouter un ami » of the invitation: the row in Réglages → Jellyfin, its name to type.</summary>
    private async void InviteAddFriend_Click(object sender, RoutedEventArgs e)
    {
        await CloseInviteAsync();
        Settings_Click(this, new());
        SelectSettingsTab("sharing");
        await _friendShowing;
        if (FriendRow.Visibility != Visibility.Visible || SettingsOverlay.Visibility != Visibility.Visible) return;
        FriendRow.BringIntoView();
        if (FriendForm.Visibility == Visibility.Visible) FriendNameBox.Focus();
    }
    /// <summary>The invitation's « Ajouter un ami » and « Lien sans compte », for an administrator only.</summary>
    private async Task ShowInviteAddFriendAsync(JellyfinClient client)
    {
        InviteAddFriend.Visibility = Visibility.Collapsed; InviteGuestLink.Visibility = Visibility.Collapsed;
        bool? admin = null;
        try { admin = await client.IsAdministratorAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { }
        if (admin != true || !ReferenceEquals(client, _client) || InviteOverlay.Visibility != Visibility.Visible) return;
        InviteGuestLink.Content = L("Lien sans compte", "Link without an account");
        InviteGuestLink.ToolTip = L("Un lien pour quelqu’un sans compte ni Tailscale, dans les réglages de Mira", "A link for someone with no account and no Tailscale, in Mira’s settings");
        InviteAddFriend.Visibility = InviteGuestLink.Visibility = Visibility.Visible;
    }
}
