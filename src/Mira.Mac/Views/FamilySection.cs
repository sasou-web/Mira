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
public sealed class FamilySection
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
    /// <summary>What the cards say, for the self-check: each card's title, then the words of its lines.</summary>
    public string Said => string.Join(" | ", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_body).OfType<TextBlock>().Select(x => x.Text).Where(x => !string.IsNullOrEmpty(x)));

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
        if (_shell.Session is not { } session || _shell.InParty) { _body.IsVisible = false; return; }
        HomeSettings? settings; HomeTailnetState? tailnet = null;
        try
        {
            if (await session.Client.IsAdministratorAsync(session.Lifetime) != true) { if (draw == _draw) _body.IsVisible = false; return; }
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
        _body.Children.Clear();
        _body.IsVisible = settings is not null;
        if (settings is null) return;
        _body.Children.Add(PasswordCard(session.Client, settings));
        if (settings.Home) _body.Children.Add(LinkCard(session.Client, tailnet));
        _body.Children.Add(ProfilesCard(session.Client, settings));
        if (settings.Home) _body.Children.Add(DevicesCard(session.Client, settings));
        _body.Children.Add(TailnetCard(session.Client, tailnet));
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
            : L("Un seul mot de passe pour toute la famille : sur un appareil, il ouvre « Qui regarde ? » avec les profils que tu choisis ci-dessous. Aucun administrateur n’en fait partie.", "One password for the whole family: on a device, it opens « Who’s watching? » with the profiles you pick below. No administrator is ever one of them."),
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
            return Card(L("Lien du foyer", "Home link"), about, Note(L("Termine d’abord « Loin de chez toi » plus bas : une seule fois.", "First finish « Away from home » below: it’s once only."), top: 12));
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
        return Card(L("Profils", "Profiles"), L("Les comptes Jellyfin que le foyer propose. Un code à 4 chiffres garde un profil des autres ; les administrateurs n’en font jamais partie.", "The Jellyfin accounts the home offers. A 4-digit PIN keeps a profile from the others; administrators are never part of it."), list, status);

        Control Row(HomeAccount account)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), Tag = account.Id };
            var offered = new ToggleSwitch { IsChecked = account.Offered && !account.Admin, IsEnabled = !account.Admin, OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Avalonia.Automation.AutomationProperties.SetName(offered, L($"Proposer {account.Name} dans le foyer", $"Offer {account.Name} in the home"));
            var busy = false;
            offered.IsCheckedChanged += async (_, _) =>
            {
                if (busy) return;
                try { await client.HomeOfferAsync(account.Id, offered.IsChecked == true); Say(status, "", false); }
                catch (Exception ex) when (Expected(ex)) { busy = true; offered.IsChecked = !offered.IsChecked; busy = false; Say(status, Words(ex), true); }
            };
            DockPanel.SetDock(offered, Dock.Right); row.Children.Add(offered);
            if (!account.Admin)
            {
                var actions = Ui.Row(6);
                if (account.Pin) actions.Children.Add(Button(L("Retirer le code", "Remove PIN"), () => _ = PinAsync(account, null), "quiet"));
                actions.Children.Add(Button(account.Pin ? L("Changer le code", "Change PIN") : L("Ajouter un code", "Add a PIN"), () => AskPin(account, row), "quiet"));
                actions.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            }
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = account.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis } } };
            if (account.Pin) name.Children.Add(new Icon("lock", 13) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
            var text = Ui.Column(2, name, Note(account.Admin ? L("Administrateur : jamais dans le foyer.", "Administrator: never in the home.") : Reach(account), top: 0));
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            return row;
        }
        void AskPin(HomeAccount account, Control row)
        {
            foreach (var other in list.Children.OfType<WrapPanel>().ToList()) list.Children.Remove(other);
            var box = new TextBox { Width = 90, MaxLength = 4, FontSize = 16, HorizontalContentAlignment = HorizontalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(box, HomeText.PinOf(account.Name));
            box.AddHandler(Avalonia.Input.InputElement.TextInputEvent, (_, e) => { if (e.Text is { } typed && !typed.All(char.IsAsciiDigit)) e.Handled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var line = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; _ = PinAsync(account, box.Text); } };
            line.Children.Add(box);
            line.Children.Add(Button(L("Enregistrer le code", "Save PIN"), () => _ = PinAsync(account, box.Text), "primary"));
            line.Children.Add(Button(L("Annuler", "Cancel"), () => list.Children.Remove(line), "quiet"));
            list.Children.Insert(list.Children.IndexOf(row) + 1, line);
            box.Focus();
        }
        async Task PinAsync(HomeAccount account, string? pin)
        {
            if (pin is not null && (pin.Length != 4 || !pin.All(char.IsAsciiDigit))) { Say(status, L("Un code fait 4 chiffres.", "A PIN is 4 digits."), true); return; }
            try { await client.HomeSetPinAsync(account.Id, pin); await LoadAsync(); }
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

    // —— Away from home: Tailscale, once ——

    private Control TailnetCard(JellyfinClient client, HomeTailnetState? state)
    {
        var title = L("Loin de chez toi", "Away from home");
        var about = L("Une seule fois : Mira crée lui-même les clés des liens du foyer sur ton réseau Tailscale. Les appareils du foyer n’y voient que Jellyfin.", "Once only: Mira makes the home links’ keys on your Tailscale network itself. The home’s devices see only Jellyfin there.");
        var status = Note("");
        if (state is null) return Card(title, about, Note(L("Le serveur ne dit rien de Tailscale : mets à jour l’extension Mira.", "The server says nothing about Tailscale: update the Mira extension."), problem: true, top: 12));
        if (state.Connected)
        {
            var ok = state.Policy == "ok";
            var lines = new List<Control> { Note(HomeText.Policy(state.Policy), problem: !ok, top: 12) };
            foreach (var rule in state.Open)
                lines.Add(Note(L($"Règle {rule.Number} de « {rule.Section} » : {string.Join(", ", rule.Sources)} va plus loin que Jellyfin.", $"Rule {rule.Number} of “{rule.Section}”: {string.Join(", ", rule.Sources)} goes further than Jellyfin."), problem: true));
            if (state.Server is { } server) lines.Add(Note(L($"Jellyfin joint à {server}:{state.Port}.", $"Jellyfin reached at {server}:{state.Port}.")));
            if (state.Devices.Count > 0) lines.Add(Note(Count(state.Devices.Count, "appareil du foyer sur ton réseau", "appareils du foyer sur ton réseau", "home device on your network", "home devices on your network")));
            var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
            buttons.Children.Add(Button(L("Vérifier à nouveau", "Check again"), () => _ = LoadAsync()));
            if (!ok) buttons.Children.Add(Button(TailnetText.OpenPolicy, () => Updates.Open(new Uri(TailscaleNode.Policy)), "quiet"));
            buttons.Children.Add(Button(L("Déconnecter…", "Disconnect…"), () => _ = DisconnectAsync(), "quiet"));
            lines.Add(buttons); lines.Add(status);
            return Card(title, about, [.. lines]);
        }
        var steps = new List<Control> { Step(1, L("Ajoute ces lignes à la politique de ton réseau (dans « tagOwners » et « grants »).", "Add these lines to your network’s policy (in “tagOwners” and “grants”).")) };
        if (state.Setup is { } setup)
        {
            var policy = setup.TagOwners + "\n\n" + setup.Grant;
            var code = new TextBox { Text = policy, IsReadOnly = true, FontFamily = new FontFamily("Menlo, monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Margin = new Thickness(32, 8, 0, 8) };
            Avalonia.Automation.AutomationProperties.SetName(code, L("Lignes de la politique", "Policy lines"));
            steps.Add(code);
            var copy = new WrapPanel { Margin = new Thickness(32, 0, 0, 0), ItemSpacing = 10, LineSpacing = 8 };
            copy.Children.Add(Button(L("Copier", "Copy"), () => _ = CopyAsync(policy)));
            copy.Children.Add(Button(TailnetText.OpenPolicy, () => Updates.Open(new Uri(TailscaleNode.Policy)), "quiet"));
            steps.Add(copy);
        }
        var scopes = state.Setup is null ? "auth_keys, devices:core, policy_file:read" : string.Join(", ", state.Setup.Scopes);
        var tag = state.Setup?.Tag ?? "tag:mira-home";
        steps.Add(Step(2, L($"Crée un client OAuth avec les droits {scopes}, et le tag {tag}.", $"Create an OAuth client with the scopes {scopes}, and the tag {tag}.")));
        var trust = Button(L("Ouvrir les identifiants de Tailscale", "Open Tailscale’s credentials"), () => Updates.Open(new Uri(Credentials)), "quiet");
        trust.Margin = new Thickness(32, 6, 0, 0); trust.HorizontalAlignment = HorizontalAlignment.Left;
        steps.Add(trust);
        steps.Add(Step(3, L("Colle son identifiant et son secret : Mira les garde sur le serveur, jamais sur ce Mac.", "Paste its ID and secret: Mira keeps them on the server, never on this Mac.")));
        var id = new TextBox { Width = 260 };
        Avalonia.Automation.AutomationProperties.SetName(id, L("Identifiant du client", "Client ID"));
        var secret = new TextBox { Width = 260, PasswordChar = '•' };
        Avalonia.Automation.AutomationProperties.SetName(secret, L("Secret du client", "Client secret"));
        var connect = Button(L("Connecter", "Connect"), () => _ = ConnectAsync(), "primary");
        connect.VerticalAlignment = VerticalAlignment.Bottom;
        var fields = new WrapPanel { Margin = new Thickness(32, 10, 0, 0), ItemSpacing = 10, LineSpacing = 8, Children = { Labeled(L("Identifiant", "Client ID"), id), Labeled(L("Secret", "Secret"), secret), connect } };
        steps.Add(fields); steps.Add(status);
        return Card(title, about, [.. steps]);

        static Control Labeled(string label, Control field) => Ui.Column(4, new TextBlock { Text = label, FontSize = 12, Foreground = Ui.Muted, Margin = new Thickness(2, 0, 0, 0) }, field);
        async Task CopyAsync(string text)
        {
            if (TopLevel.GetTopLevel(_shell)?.Clipboard is not { } clipboard) { Say(status, L("Copie impossible ici : sélectionne les lignes.", "Can’t copy here: select the lines."), true); return; }
            await clipboard.SetTextAsync(text);
            Say(status, L("Copié.", "Copied."), false);
        }
        async Task ConnectAsync()
        {
            if ((id.Text ?? "").Trim().Length == 0 || (secret.Text ?? "").Length == 0) { Say(status, L("Colle l’identifiant et le secret.", "Paste the ID and the secret."), true); return; }
            Say(status, L("Connexion à Tailscale…", "Connecting to Tailscale…"), false);
            try { await client.HomeConnectTailnetAsync(id.Text!.Trim(), secret.Text); secret.Text = ""; await LoadAsync(); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
        }
        async Task DisconnectAsync()
        {
            if (!await _shell.ConfirmAsync(L("Déconnecter Tailscale du foyer ?", "Disconnect Tailscale from the home?"), L("Plus aucun lien du foyer. Les appareils déjà loin de chez toi gardent leur accès tant que tu ne les retires pas.", "No more home links. Devices already away from home keep their access until you remove them."), L("Déconnecter", "Disconnect"), L("Annuler", "Cancel"))) return;
            try { await client.HomeConnectTailnetAsync(null, null); _link = null; await LoadAsync(); }
            catch (Exception ex) when (Expected(ex)) { Say(status, Words(ex), true); }
        }
    }

    private const string Credentials = "https://login.tailscale.com/admin/settings/trust-credentials";

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
