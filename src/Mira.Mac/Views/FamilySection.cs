using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.Tailscale;
using Mira.Core.TorLink;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Réglages → Foyer, as on Windows (MainWindow.FamilySettings.cs there), for the administrator of a Jellyfin with Mira's
/// extension (/Mira/home): the home's password, the home link (away from home: its QR code, when it ends), which accounts
/// are profiles of the home and their PINs, the devices in the home, and the one-time setup of Tailscale (the policy's
/// two lines, the OAuth client, the verdict on the policy as the extension reads it). Drawn from what the extension says
/// each time it is read; hidden for anyone else, and on a Jellyfin whose extension has no home.
/// </summary>
public sealed partial class FamilySection
{
    private readonly MainWindow _shell;
    private readonly Func<string, Control[], Control> _section;
    private readonly StackPanel _body = new() { Spacing = 10 };
    private int _draw;
    private HomeLinkMade? _link;
    private Func<Task>? _makeLink;
    /// <summary>« Send the home link » pressed, as a click does (the self-check).</summary>
    public Task MakeLinkForCheck() => _makeLink?.Invoke() ?? Task.CompletedTask;
    /// <summary>A home link shown, with its QR code.</summary>
    public bool LinkShown => _link is not null;

    public Control Section => _body;
    /// <summary>The home's Tailscale and its guide, in « Loin de chez toi » with the other ways in (ogsas, 2026-11-20, maquette A).</summary>
    public Control Away => _away;
    private readonly StackPanel _away = new() { Spacing = 10, IsVisible = false };
    /// <summary>What the cards say, for the self-check: each card's title, then the words of its lines (both pages).</summary>
    public string Said => string.Join(" | ", new Control[] { _body, _away }.SelectMany(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants).OfType<TextBlock>().Select(x => x.Text).Where(x => !string.IsNullOrEmpty(x)));

    public FamilySection(MainWindow shell, Func<string, Control[], Control> section)
    {
        _shell = shell; _section = section;
        _body.IsVisible = false;
        _ = LoadAsync();
    }

    private static TextBlock Note(string text, bool problem = false, double top = 6) => new()
    {
        Text = text, FontSize = 12.5, LineHeight = 19, Foreground = problem ? Ui.Danger : Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 30, 0), IsVisible = text.Length > 0,
    };
    private static void Say(TextBlock note, string text, bool problem)
    {
        note.Text = text; note.IsVisible = text.Length > 0; note.Foreground = problem ? Ui.Danger : Ui.Muted;
        Avalonia.Automation.AutomationProperties.SetLiveSetting(note, problem ? Avalonia.Automation.AutomationLiveSetting.Assertive : Avalonia.Automation.AutomationLiveSetting.Polite);
    }
    private static string Words(Exception ex) => ex is RelayRefusedException ? ex.Message : Errors.Friendly(ex);
    private static bool Expected(Exception ex) => Errors.Expected(ex) || ex is RelayRefusedException;

    /// <summary>Shown for an administrator of a Jellyfin whose extension has the home; drawn from what it says now.</summary>
    public async Task LoadAsync()
    {
        var draw = ++_draw;
        if (_shell.Session is not { } session || _shell.InParty) { _body.IsVisible = _away.IsVisible = false; return; }
        HomeSettings? settings; HomeTailnetState? tailnet = null;
        try
        {
            if (await session.Client.IsAdministratorAsync(session.Lifetime) != true) { if (draw == _draw) _body.IsVisible = _away.IsVisible = false; return; }
            settings = await session.Client.HomeSettingsAsync(session.Lifetime);
            if (settings is not null)
                try { tailnet = await session.Client.HomeTailnetAsync(session.Lifetime); }
                catch (RelayRefusedException) { tailnet = null; }
        }
        catch (Exception ex) when (Expected(ex))
        {
            // Shown already (a change that failed to read back): said there, with « Try again »; never shown for it otherwise.
            if (draw != _draw || !_body.IsVisible) return;
            _body.Children.Clear();
            _body.Children.Add(_section(L("Foyer", "Home"), [Ui.Column(12, Note(Words(ex), problem: true, top: 0), Ui.Action(L("Réessayer", "Try again"), "refresh", "chip", () => _ = LoadAsync()))]));
            return;
        }
        if (draw != _draw || !ReferenceEquals(session, _shell.Session)) return;
        _body.Children.Clear(); _away.Children.Clear();
        _body.IsVisible = _away.IsVisible = settings is not null;
        if (settings is null) return;
        _body.Children.Add(PasswordCard(session.Client, settings));
        if (settings.Home) _body.Children.Add(LinkCard(session.Client, tailnet));
        _body.Children.Add(ProfilesCard(session.Client, settings));
        if (settings.Home) _body.Children.Add(DevicesCard(session.Client, settings));
        // The home's Tailscale (and its guide) in « Loin de chez toi »; here, where it stands and the way there.
        _away.Children.Add(_guide > 0 && tailnet is not null ? Guide(session.Client, tailnet) : TailnetCard(session.Client, tailnet));
        _shell.TellAway(settings, tailnet);
        _body.Children.Add(AwayLine());
    }

    /// <summary>« Loin de chez toi · Prêt · Ouvrir »: the home's Tailscale, set up on its own page.</summary>
    private Control AwayLine()
    {
        var away = _shell.AwayNow;
        var ready = away.Level == AwayLevel.Ready;
        var open = Button(ready ? L("Ouvrir", "Open") : L("Régler…", "Set up…"), () => _shell.OpenSettingsAt("away"), ready ? "" : "primary");
        open.VerticalAlignment = VerticalAlignment.Center; open.Margin = new Thickness(14, 0, 0, 0);
        var title = new TextBlock { Text = L("Loin de chez toi", "Away from home") + " · " + away.Word, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (away.Level == AwayLevel.Attention) title.Foreground = Ui.Warning;
        var line = new DockPanel();
        DockPanel.SetDock(open, Dock.Right); line.Children.Add(open);
        var icon = new Icon("globe", 18) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        line.Children.Add(Ui.Column(3, title, Note(AwayFromHome.Short(away.Level), top: 0)));
        return _section(null!, [line]);
    }

    private Control Card(string title, string about, params Control[] rows) =>
        _section(title, [Ui.Column(0, [new TextBlock { Text = about, FontSize = 12.5, LineHeight = 19, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -6, 30, 0) }, .. rows])]);

    private static Button Button(string label, Action act, string classes = "") => Ui.Action(label, null, classes, act);

    // —— The password ——

    private Control PasswordCard(JellyfinClient client, HomeSettings settings)
    {
        var password = new TextBox { PasswordChar = '•', Width = 260, Watermark = settings.Home ? L("Nouveau mot de passe", "New password") : L($"Au moins {settings.MinPassword} caractères", $"At least {settings.MinPassword} characters") };
        Avalonia.Automation.AutomationProperties.SetName(password, settings.Home ? L("Nouveau mot de passe du foyer", "New home password") : HomeText.Password);
        var status = Note("");
        var line = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
        line.Children.Add(password);
        line.Children.Add(Button(settings.Home ? L("Changer", "Change") : L("Activer le foyer", "Turn on the home"), () => _ = SetAsync(), settings.Home ? "" : "primary"));
        if (settings.Home) line.Children.Add(Button(L("Désactiver le foyer…", "Turn off the home…"), () => _ = OffAsync(), "quiet"));
        password.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = SetAsync(); } };
        var since = settings.Home && settings.Changed is { } changed ? Note(L($"Activé depuis le {Date(changed.ToLocalTime())}.", $"On since {Date(changed.ToLocalTime())}.")) : Note("");
        return Card(HomeText.Password, settings.Home
            ? L("Ta famille l’utilise une fois par appareil, puis choisit son profil. Le changer fait sortir tous les appareils du foyer.", "Your family types it once per device, then picks their profile. Changing it signs every device out of the home.")
            : L("Un seul mot de passe pour toute la famille : sur un appareil, il ouvre « Qui regarde ? » avec les profils que tu choisis ci-dessous. Aucun administrateur n’en fait partie.", "One password for the whole family: on a device, it opens “Who’s watching?” with the profiles you pick below. No administrator is ever one of them."),
            since, line, status);

        async Task SetAsync()
        {
            var typed = password.Text ?? "";
            if (typed.Length < settings.MinPassword) { Say(status, L($"Au moins {settings.MinPassword} caractères.", $"At least {settings.MinPassword} characters."), true); return; }
            if (settings.Home && !await _shell.ConfirmAsync(L("Changer le mot de passe du foyer ?", "Change the home password?"), L("Chaque appareil du foyer devra taper le nouveau.", "Every device in the home will need to type the new one."), L("Changer", "Change"), L("Annuler", "Cancel"))) return;
            try { await client.HomeSetPasswordAsync(typed); password.Text = ""; await LoadAsync(); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
        }
        async Task OffAsync()
        {
            if (!await _shell.ConfirmAsync(L("Désactiver le foyer ?", "Turn off the home?"), L("Tous les appareils sortent du foyer et leurs profils se ferment. Les comptes Jellyfin restent.", "Every device leaves the home and its profiles close. The Jellyfin accounts stay."), L("Désactiver", "Turn off"), L("Annuler", "Cancel"))) return;
            try { await client.HomeSetPasswordAsync(null); _link = null; await LoadAsync(); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
        }
    }

    // —— The home link ——

    private Control LinkCard(JellyfinClient client, HomeTailnetState? tailnet)
    {
        var about = L("Pour un appareil loin de chez toi : le lien emmène Mira jusqu’à ton Jellyfin par Tailscale, sans adresse ni compte Tailscale à créer. Il sert une seule fois et finit au bout de 48 heures ; le mot de passe reste demandé.",
            "For a device away from home: the link takes Mira to your Jellyfin through Tailscale, with no address to type and no Tailscale account to make. It works once and ends after 48 hours; the password is still asked.");
        if (tailnet is not { Connected: true })
        {
            var go = Button(L("Régler…", "Set up…"), () => _shell.OpenSettingsAt("away"));
            go.HorizontalAlignment = HorizontalAlignment.Left; go.Margin = new Thickness(0, 12, 0, 0);
            return Card(L("Lien du foyer", "Home link"), about, Note(L("Règle d’abord « Loin de chez toi », une seule fois.", "First set up “Away from home”, once only."), top: 12), go);
        }
        var status = Note("");
        var result = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        Button make = null!;
        make = Button(L("Envoyer le lien du foyer", "Send the home link"), () => _ = MakeAsync(), "primary");
        make.Margin = new Thickness(0, 14, 0, 0); make.HorizontalAlignment = HorizontalAlignment.Left;
        _makeLink = MakeAsync;
        if (_link is { } kept && kept.Expires > DateTimeOffset.UtcNow) Show(kept);
        return Card(L("Lien du foyer", "Home link"), about, make, status, result);

        async Task MakeAsync()
        {
            make.IsEnabled = false; Say(status, L("Création du lien…", "Making the link…"), false);
            try { Show(_link = await client.HomeLinkAsync()); Say(status, "", false); }
            catch (HomeLinkRefusedException ex) { Say(status, ex.Policy is { } verdict ? HomeText.Policy(verdict) : ex.Message, true); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
            finally { make.IsEnabled = true; }
        }
        void Show(HomeLinkMade made)
        {
            result.Children.Clear();
            var copied = Note("");
            var box = new TextBox { Text = made.Link, IsReadOnly = true, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            Avalonia.Automation.AutomationProperties.SetName(box, L("Lien du foyer", "Home link"));
            var copy = Button(L("Copier le lien", "Copy the link"), () => _ = CopyAsync());
            copy.HorizontalAlignment = HorizontalAlignment.Left;
            var text = Ui.Column(0, box, copy,
                Note(L($"Finit le {Date(made.Expires.ToLocalTime())} à {Time(made.Expires.ToLocalTime())}, ou dès qu’un appareil l’a utilisé.", $"Ends on {Date(made.Expires.ToLocalTime())} at {Time(made.Expires.ToLocalTime())}, or as soon as a device has used it."), top: 10), copied);
            text.VerticalAlignment = VerticalAlignment.Center;
            var line = new DockPanel();
            var qr = Qr(made.Link);
            DockPanel.SetDock(qr, Dock.Left); line.Children.Add(qr); line.Children.Add(text);
            result.Children.Add(line);
            async Task CopyAsync()
            {
                if (TopLevel.GetTopLevel(_shell)?.Clipboard is not { } clipboard) { Say(copied, L("Copie impossible ici : sélectionne le lien.", "Can’t copy here: select the link."), true); return; }
                await clipboard.SetTextAsync(made.Link);
                Say(copied, L("Copié : envoie-le avec le mot de passe, par deux chemins différents si tu peux.", "Copied: send it with the password, by two different ways if you can."), false);
            }
        }
    }

    /// <summary>The link's QR code, for a phone that films the screen: whole modules, dark on light, as « Mira sur ton iPhone ».</summary>
    private static Border Qr(string link)
    {
        var (size, path) = PhoneLink.Qr(link);
        var module = Math.Max(2, Math.Round(132.0 / size));
        var code = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(path), Fill = Ui.Black, Stretch = Stretch.Uniform, Width = module * size, Height = module * size };
        RenderOptions.SetEdgeMode(code, EdgeMode.Aliased);
        var frame = new Border { Background = Ui.Ink, CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top, Child = code };
        Avalonia.Automation.AutomationProperties.SetName(frame, L("QR code du lien du foyer", "Home link QR code"));
        return frame;
    }

    // —— The profiles ——

    private Control ProfilesCard(JellyfinClient client, HomeSettings settings)
    {
        var status = Note("");
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var account in settings.Profiles.Where(x => !x.Disabled).OrderBy(x => x.Admin).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)) list.Children.Add(Row(account));
        if (list.Children.Count == 0) list.Children.Add(Note(HomeText.NoProfiles));
        return Card(L("Profils", "Profiles"), L("Les comptes Jellyfin que le foyer propose. Un code à 4 chiffres garde un profil des autres ; un administrateur en a toujours un.", "The Jellyfin accounts the home offers. A 4-digit PIN keeps a profile from the others; an administrator always has one."), list, status);

        Control Row(HomeAccount account)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), Tag = account.Id };
            // An administrator's profile only with a PIN, which it keeps: turned on, the PIN is asked first.
            var offered = new ToggleSwitch { IsChecked = account.Offered, OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(offered, L($"Proposer {account.Name} dans le foyer", $"Offer {account.Name} in the home"));
            var busy = false;
            offered.IsCheckedChanged += async (_, _) =>
            {
                if (busy) return;
                if (account.Admin && !account.Pin && offered.IsChecked == true) { busy = true; offered.IsChecked = false; busy = false; AskPin(account, row, offer: true); return; }
                try { await client.HomeOfferAsync(account.Id, offered.IsChecked == true); Say(status, "", false); }
                catch (Exception ex) when (Expected(ex)) { busy = true; offered.IsChecked = !offered.IsChecked; busy = false; Say(status, Words(ex), true); }
            };
            var inHome = Ui.Row(10, Ui.Text(HomeText.InHome, 13, color: Ui.TextSecondary), offered);
            inHome.VerticalAlignment = VerticalAlignment.Center; inHome.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(inHome, Dock.Right); row.Children.Add(inHome);
            if (!account.Admin || account.Offered)
            {
                var actions = Ui.Row(6);
                if (account.Pin && !account.Admin) actions.Children.Add(Button(L("Retirer le code", "Remove PIN"), () => _ = PinAsync(account, null), "quiet"));
                actions.Children.Add(Button(account.Pin ? L("Changer le code", "Change PIN") : L("Ajouter un code", "Add a PIN"), () => AskPin(account, row)));
                actions.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            }
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = account.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis } } };
            if (account.Pin) name.Children.Add(new Icon("lock", 13) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
            var text = Ui.Column(2, name, Note(account.Admin ? HomeText.AdminNeedsPin : Reach(account), top: 0));
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            return row;
        }
        void AskPin(HomeAccount account, Control row, bool offer = false)
        {
            foreach (var other in list.Children.OfType<WrapPanel>().ToList()) list.Children.Remove(other);
            var box = new TextBox { Width = 90, MaxLength = 4, FontSize = 16, HorizontalContentAlignment = HorizontalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(box, HomeText.PinOf(account.Name));
            box.AddHandler(Avalonia.Input.InputElement.TextInputEvent, (_, e) => { if (e.Text is { } typed && !typed.All(char.IsAsciiDigit)) e.Handled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var line = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = PinAsync(account, box.Text, offer); } };
            line.Children.Add(box);
            line.Children.Add(Button(L("Enregistrer le code", "Save PIN"), () => _ = PinAsync(account, box.Text, offer), "primary"));
            line.Children.Add(Button(L("Annuler", "Cancel"), () => list.Children.Remove(line), "quiet"));
            list.Children.Insert(list.Children.IndexOf(row) + 1, line);
            box.Focus();
        }
        async Task PinAsync(HomeAccount account, string? pin, bool offer = false)
        {
            if (pin is not null && (pin.Length != 4 || !pin.All(char.IsAsciiDigit))) { Say(status, L("Un code fait 4 chiffres.", "A PIN is 4 digits."), true); return; }
            try
            {
                if (offer) await client.HomeOfferAsync(account.Id, true, pin);
                else await client.HomeSetPinAsync(account.Id, pin);
                await LoadAsync();
            }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
        }
    }

    private static string Reach(HomeAccount account) =>
        (account.AllLibraries ? L("Toute la bibliothèque", "The whole library") : L("Une partie de la bibliothèque", "Part of the library"))
        + (account.MaxRating is { Length: > 0 } rating ? L($" · jusqu’à {rating}", $" · up to {rating}") : "");

    // —— The devices ——

    private Control DevicesCard(JellyfinClient client, HomeSettings settings)
    {
        var status = Note("");
        var rows = new StackPanel();
        if (settings.Devices.Count == 0) rows.Children.Add(Note(L("Aucun appareil pour l’instant.", "No devices yet."), top: 10));
        foreach (var device in settings.Devices.OrderByDescending(x => x.Seen))
        {
            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var remove = Button(L("Retirer…", "Remove…"), () => _ = RemoveAsync(device), "quiet");
            remove.VerticalAlignment = VerticalAlignment.Center;
            Avalonia.Automation.AutomationProperties.SetName(remove, L($"Retirer {device.Name}", $"Remove {device.Name}"));
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var seen = device.Seen.ToLocalTime();
            row.Children.Add(Ui.Column(2,
                new TextBlock { Text = device.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                Note((device.ProfileName is { } profile ? L($"Profil {profile} · ", $"{profile}’s profile · ") : L("Aucun profil ouvert · ", "No profile open · "))
                    + L($"vu le {Date(seen)} à {Time(seen)}", $"seen {Date(seen)} at {Time(seen)}"), top: 0)));
            rows.Children.Add(row);
        }
        return Card(L("Appareils du foyer", "Devices in the home"), L("Chaque appareil qui a tapé le mot de passe. En retirer un ferme son profil ; il lui faudra le mot de passe pour revenir.", "Each device that typed the password. Removing one closes its profile; it will need the password to come back."), rows, status);

        async Task RemoveAsync(HomeDeviceInfo device)
        {
            if (!await _shell.ConfirmAsync(L($"Retirer {device.Name} du foyer ?", $"Remove {device.Name} from the home?"), L("Son profil se ferme. Il lui faudra le mot de passe, et loin de chez toi un nouveau lien, pour revenir.", "Its profile closes. It will need the password, and away from home a new link, to come back."), L("Retirer", "Remove"), L("Annuler", "Cancel"))) return;
            try { await client.HomeRemoveDeviceAsync(device.Id); await LoadAsync(); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
        }
    }

    private static Control Step(int number, string text)
    {
        var line = new DockPanel { Margin = new Thickness(0, 16, 30, 0) };
        var badge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Ui.LineStrong, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number.ToString(Lang.Culture), FontSize = 11.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(badge, Dock.Left); line.Children.Add(badge);
        line.Children.Add(new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return line;
    }
}
