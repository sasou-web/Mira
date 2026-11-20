using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.Tailscale;
using Mira.Core.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Réglages → Foyer, for the administrator of a Jellyfin with Mira's extension (/Mira/home): the home's password, the
/// home link (away from home: its QR code, when it ends), which accounts are profiles of the home and their PINs, the
/// devices in the home, and the one-time setup of Tailscale (the tag, the OAuth client, the verdict on the policy as the
/// extension reads it, and its fix in one click). Drawn from what the extension says each time the section opens.
/// </summary>
public partial class MainWindow
{
    /// <summary>The client whose extension was asked whether it has a home: the section listed for its administrator alone.</summary>
    private JellyfinClient? _familyProbed;
    private int _familyDraw;
    private HomeLinkMade? _familyLink;

    /// <summary>Réglages opened: « Foyer » listed when this account administers a Jellyfin whose extension has the home.</summary>
    private async Task ProbeFamilySettingsAsync()
    {
        if (_demo || InParty || _client is not { } client) { FamilySettingsNav.Visibility = Visibility.Collapsed; ShowSettingsGroups(); return; }
        if (ReferenceEquals(client, _familyProbed)) return;
        _familyProbed = client;
        bool shown;
        try { shown = await IsAdministratorAsync(client) && await client.HomeSettingsAsync(_connectionLifetime.Token) is not null; }
        catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { shown = false; _familyProbed = null; }
        if (!ReferenceEquals(client, _client)) return;
        FamilySettingsNav.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ShowSettingsGroups();
    }

    private async Task ShowFamilySettingsAsync()
    {
        var draw = ++_familyDraw;
        if (_client is not { } client) return;
        if (FamilySettings.Children.Count == 0) FamilySettings.Children.Add(FamilyNote(L("Lecture du foyer…", "Reading the home…")));
        HomeSettings? settings; HomeTailnetState? tailnet = null;
        try
        {
            settings = await client.HomeSettingsAsync(_connectionLifetime.Token);
            if (settings is not null)
                try { tailnet = await client.HomeTailnetAsync(_connectionLifetime.Token); }
                catch (RelayRefusedException) { tailnet = null; }
        }
        catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException)
        {
            if (draw != _familyDraw) return;
            FamilySettings.Children.Clear();
            FamilySettings.Children.Add(FamilyNote(ex is RelayRefusedException ? ex.Message : Friendly(ex), problem: true));
            FamilySettings.Children.Add(FamilyButton(L("Réessayer", "Try again"), () => _ = ShowFamilySettingsAsync(), quiet: true));
            return;
        }
        if (draw != _familyDraw || !ReferenceEquals(client, _client)) return;
        FamilySettings.Children.Clear();
        if (settings is null) { FamilySettings.Children.Add(FamilyNote(HomeText.NoExtension, problem: true)); return; }
        if (_familyGuide > 0 && tailnet is not null) { FamilySettings.Children.Add(FamilyGuide(client, tailnet)); return; }
        FamilySettings.Children.Add(FamilyPasswordRow(client, settings));
        if (settings.Home) FamilySettings.Children.Add(FamilyLinkRow(client, tailnet));
        FamilySettings.Children.Add(FamilyProfilesRow(client, settings));
        if (settings.Home) FamilySettings.Children.Add(FamilyDevicesRow(client, settings));
        FamilySettings.Children.Add(FamilyTailnetRow(client, tailnet));
    }

    // ---- The password ----

    private Border FamilyPasswordRow(JellyfinClient client, HomeSettings settings)
    {
        var body = FamilyCard(L("Mot de passe du foyer", "Home password"), settings.Home
            ? L("Ta famille l’utilise une fois par appareil, puis choisit son profil. Le changer fait sortir tous les appareils du foyer.", "Your family types it once per device, then picks their profile. Changing it signs every device out of the home.")
            : L("Un seul mot de passe pour toute la famille : sur un appareil, il ouvre « Qui regarde ? » avec les profils que tu choisis ci-dessous. Aucun administrateur n’en fait partie.", "One password for the whole family: on a device, it opens « Who’s watching? » with the profiles you pick below. No administrator is ever one of them."));
        if (settings.Home && settings.Changed is { } changed) body.Children.Add(FamilyNote(L($"Activé depuis le {Date(changed.ToLocalTime())}.", $"On since {Date(changed.ToLocalTime())}.")));
        var password = new PasswordBox { Width = 260, Margin = new Thickness(0, 0, 10, 8) };
        AutomationProperties.SetName(password, settings.Home ? L("Nouveau mot de passe du foyer", "New home password") : L("Mot de passe du foyer", "Home password"));
        var status = FamilyNote("");
        var line = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        line.Children.Add(password);
        line.Children.Add(FamilyButton(settings.Home ? L("Changer", "Change") : L("Activer le foyer", "Turn on the home"), () => _ = SetAsync(), primary: !settings.Home));
        if (settings.Home) line.Children.Add(FamilyButton(L("Désactiver le foyer…", "Turn off the home…"), () => _ = OffAsync(), quiet: true));
        body.Children.Add(line); body.Children.Add(status);
        return FamilyRow(body);

        async Task SetAsync()
        {
            if (password.Password.Length < settings.MinPassword) { FamilySay(status, L($"Au moins {settings.MinPassword} caractères.", $"At least {settings.MinPassword} characters."), true); return; }
            if (settings.Home && !await ConfirmAsync(L("Changer le mot de passe du foyer ?", "Change the home password?"), L("Chaque appareil du foyer devra taper le nouveau.", "Every device in the home will need to type the new one."), L("Changer", "Change"), L("Annuler", "Cancel"))) return;
            try { await client.HomeSetPasswordAsync(password.Password, _connectionLifetime.Token); password.Clear(); await ShowFamilySettingsAsync(); }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, ex is RelayRefusedException ? ex.Message : Friendly(ex), true); }
        }
        async Task OffAsync()
        {
            if (!await ConfirmAsync(L("Désactiver le foyer ?", "Turn off the home?"), L("Tous les appareils sortent du foyer et leurs profils se ferment. Les comptes Jellyfin restent.", "Every device leaves the home and its profiles close. The Jellyfin accounts stay."), L("Désactiver", "Turn off"), L("Annuler", "Cancel"))) return;
            try { await client.HomeSetPasswordAsync(null, _connectionLifetime.Token); _familyLink = null; await ShowFamilySettingsAsync(); }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, ex is RelayRefusedException ? ex.Message : Friendly(ex), true); }
        }
    }

    // ---- The home link ----

    private Border FamilyLinkRow(JellyfinClient client, HomeTailnetState? tailnet)
    {
        var body = FamilyCard(L("Lien du foyer", "Home link"), L("Pour un appareil loin de chez toi : le lien emmène Mira jusqu’à ton Jellyfin par Tailscale, sans adresse ni compte Tailscale à créer. Il sert une seule fois et finit au bout de 48 heures ; le mot de passe reste demandé.", "For a device away from home: the link takes Mira to your Jellyfin through Tailscale, with no address to type and no Tailscale account to make. It works once and ends after 48 hours; the password is still asked."));
        var status = FamilyNote("");
        var result = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (tailnet is not { Connected: true })
        {
            body.Children.Add(FamilyNote(L("Termine d’abord « Loin de chez toi » plus bas : une seule fois.", "First finish « Away from home » below: it’s once only."), margin: new Thickness(0, 12, 0, 0)));
            return FamilyRow(body);
        }
        Button make = null!;
        make = FamilyButton(L("Envoyer le lien du foyer", "Send the home link"), () => _ = MakeAsync(), primary: true);
        make.Margin = new Thickness(0, 14, 0, 0); make.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(make); body.Children.Add(status); body.Children.Add(result);
        if (_familyLink is { } kept && kept.Expires > DateTimeOffset.UtcNow) Show(kept);
        return FamilyRow(body);

        async Task MakeAsync()
        {
            make.IsEnabled = false; FamilySay(status, L("Création du lien…", "Making the link…"), false);
            try { Show(_familyLink = await client.HomeLinkAsync(_connectionLifetime.Token)); FamilySay(status, "", false); }
            catch (HomeLinkRefusedException ex) { FamilySay(status, ex.Policy is { } verdict ? HomeText.Policy(verdict) : ex.Message, true); }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, ex is RelayRefusedException ? ex.Message : Friendly(ex), true); }
            finally { make.IsEnabled = true; }
        }
        void Show(HomeLinkMade made)
        {
            result.Children.Clear();
            var line = new DockPanel();
            var qr = FamilyQr(made.Link);
            DockPanel.SetDock(qr, Dock.Left); line.Children.Add(qr);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { Text = made.Link, IsReadOnly = true, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(box, L("Lien du foyer", "Home link"));
            text.Children.Add(box);
            var copied = FamilyNote("");
            var buttons = new WrapPanel();
            buttons.Children.Add(FamilyButton(L("Copier le lien", "Copy the link"), () =>
            {
                try { Clipboard.SetText(made.Link); FamilySay(copied, L("Copié : envoie-le avec le mot de passe, par deux chemins différents si tu peux.", "Copied: send it with the password, by two different ways if you can."), false); }
                catch (System.Runtime.InteropServices.ExternalException) { FamilySay(copied, L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again."), true); }
            }));
            text.Children.Add(buttons);
            text.Children.Add(FamilyNote(L($"Finit le {Date(made.Expires.ToLocalTime())} à {Time(made.Expires.ToLocalTime())}, ou dès qu’un appareil l’a utilisé.", $"Ends on {Date(made.Expires.ToLocalTime())} at {Time(made.Expires.ToLocalTime())}, or as soon as a device has used it."), margin: new Thickness(0, 10, 0, 0)));
            text.Children.Add(copied);
            line.Children.Add(text);
            result.Children.Add(line);
        }
    }

    /// <summary>The link's QR code, for a phone that films the screen; whole modules, as the phone's own (<see cref="ShowPhoneQr"/>).</summary>
    private static Squircle FamilyQr(string link)
    {
        var (size, path) = PhoneLink.Qr(link);
        var module = Math.Max(2, Math.Round(132.0 / size));
        var geometry = Geometry.Parse(path); geometry.Freeze();
        var code = new Path { Data = geometry, Width = module * size, Height = module * size, Stretch = Stretch.Fill, Fill = Palette.Get("OnAccent"), SnapsToDevicePixels = true };
        RenderOptions.SetEdgeMode(code, EdgeMode.Aliased);
        var frame = new Squircle { Background = Palette.Get("Text"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top, Child = code };
        AutomationProperties.SetName(frame, L("QR code du lien du foyer", "Home link QR code"));
        return frame;
    }

    // ---- The profiles ----

    private Border FamilyProfilesRow(JellyfinClient client, HomeSettings settings)
    {
        var body = FamilyCard(L("Profils", "Profiles"), L("Les comptes Jellyfin que le foyer propose. Un code à 4 chiffres garde un profil des autres ; les administrateurs n’en font jamais partie.", "The Jellyfin accounts the home offers. A 4-digit PIN keeps a profile from the others; administrators are never part of it."));
        var status = FamilyNote("");
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var account in settings.Profiles.Where(x => !x.Disabled).OrderBy(x => x.Admin).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)) list.Children.Add(Row(account));
        if (list.Children.Count == 0) list.Children.Add(FamilyNote(HomeText.NoProfiles));
        body.Children.Add(list); body.Children.Add(status);
        return FamilyRow(body);

        FrameworkElement Row(HomeAccount account)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
            var offered = new CheckBox { IsChecked = account.Offered && !account.Admin, IsEnabled = !account.Admin, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            AutomationProperties.SetName(offered, L($"Proposer {account.Name} dans le foyer", $"Offer {account.Name} in the home"));
            offered.Click += async (_, _) =>
            {
                try { await client.HomeOfferAsync(account.Id, offered.IsChecked == true, _connectionLifetime.Token); }
                catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { offered.IsChecked = !offered.IsChecked; FamilySay(status, ex is RelayRefusedException ? ex.Message : Friendly(ex), true); }
            };
            DockPanel.SetDock(offered, Dock.Right); row.Children.Add(offered);
            if (!account.Admin)
            {
                var pin = FamilyButton(account.Pin ? L("Changer le code", "Change PIN") : L("Ajouter un code", "Add a PIN"), () => AskProfilePin(account), quiet: true);
                pin.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(pin, Dock.Right); row.Children.Add(pin);
                if (account.Pin)
                {
                    var remove = FamilyButton(L("Retirer le code", "Remove PIN"), () => _ = PinAsync(account, null), quiet: true);
                    remove.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                }
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new TextBlock { Text = account.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (account.Pin) name.Children.Add(new Icon { Kind = "lock", Width = 13, Height = 13, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get("TextSecondary") });
            text.Children.Add(name);
            text.Children.Add(new TextBlock { Style = (Style)FindResource("SettingDescription"), Text = account.Admin ? L("Administrateur : jamais dans le foyer.", "Administrator: never in the home.") : AccountReach(account) });
            row.Children.Add(text);
            return row;
        }
        void AskProfilePin(HomeAccount account)
        {
            var box = new TextBox { Width = 90, MaxLength = 4, FontSize = 16, Margin = new Thickness(0, 0, 10, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(box, L($"Code de {account.Name}", $"{account.Name}’s PIN"));
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
            var line = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            line.Children.Add(box);
            line.Children.Add(FamilyButton(L("Enregistrer le code", "Save PIN"), () => _ = PinAsync(account, box.Text), primary: true));
            line.Children.Add(FamilyButton(L("Annuler", "Cancel"), () => list.Children.Remove(line), quiet: true));
            var at = list.Children.IndexOf(list.Children.OfType<DockPanel>().FirstOrDefault(x => x.Children.OfType<StackPanel>().Any(t => t.Children.OfType<StackPanel>().Any(n => n.Children.OfType<TextBlock>().Any(b => b.Text == account.Name)))));
            foreach (var other in list.Children.OfType<WrapPanel>().ToList()) list.Children.Remove(other);
            list.Children.Insert(at < 0 ? list.Children.Count : at + 1, line);
            box.Focus();
        }
        async Task PinAsync(HomeAccount account, string? pin)
        {
            if (pin is not null && (pin.Length != 4 || !pin.All(char.IsAsciiDigit))) { FamilySay(status, L("Un code fait 4 chiffres.", "A PIN is 4 digits."), true); return; }
            try { await client.HomeSetPinAsync(account.Id, pin, _connectionLifetime.Token); await ShowFamilySettingsAsync(); }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, ex is RelayRefusedException ? ex.Message : Friendly(ex), true); }
        }
    }

    private static string AccountReach(HomeAccount account) =>
        (account.AllLibraries ? L("Toute la bibliothèque", "The whole library") : L("Une partie de la bibliothèque", "Part of the library"))
        + (account.MaxRating is { Length: > 0 } rating ? L($" · jusqu’à {rating}", $" · up to {rating}") : "");

    // ---- The devices ----

    private Border FamilyDevicesRow(JellyfinClient client, HomeSettings settings)
    {
        var body = FamilyCard(L("Appareils du foyer", "Devices in the home"), L("Chaque appareil qui a tapé le mot de passe. En retirer un ferme son profil ; il lui faudra le mot de passe pour revenir.", "Each device that typed the password. Removing one closes its profile; it will need the password to come back."));
        var status = FamilyNote("");
        if (settings.Devices.Count == 0) body.Children.Add(FamilyNote(L("Aucun appareil pour l’instant.", "No devices yet."), margin: new Thickness(0, 10, 0, 0)));
        foreach (var device in settings.Devices.OrderByDescending(x => x.Seen))
        {
            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var remove = FamilyButton(L("Retirer…", "Remove…"), () => _ = RemoveAsync(device), quiet: true);
            remove.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(remove, L($"Retirer {device.Name}", $"Remove {device.Name}"));
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = device.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var seen = device.Seen.ToLocalTime();
            text.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("SettingDescription"),
                Text = (device.ProfileName is { } profile ? L($"Profil {profile} · ", $"{profile}’s profile · ") : L("Aucun profil ouvert · ", "No profile open · "))
                    + L($"vu le {Date(seen)} à {Time(seen)}", $"seen {Date(seen)} at {Time(seen)}"),
            });
            row.Children.Add(text);
            body.Children.Add(row);
        }
        body.Children.Add(status);
        return FamilyRow(body);

        async Task RemoveAsync(HomeDeviceInfo device)
        {
            if (!await ConfirmAsync(L($"Retirer {device.Name} du foyer ?", $"Remove {device.Name} from the home?"), L("Son profil se ferme. Il lui faudra le mot de passe, et loin de chez toi un nouveau lien, pour revenir.", "Its profile closes. It will need the password, and away from home a new link, to come back."), L("Retirer", "Remove"), L("Annuler", "Cancel"))) return;
            try { await client.HomeRemoveDeviceAsync(device.Id, _connectionLifetime.Token); await ShowFamilySettingsAsync(); }
            catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException) { FamilySay(status, ex is RelayRefusedException ? ex.Message : Friendly(ex), true); }
        }
    }

    // ---- Pieces ----

    private StackPanel FamilyCard(string title, string about)
    {
        var body = new StackPanel();
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = about, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 4, 30, 0) });
        return body;
    }

    private Border FamilyRow(StackPanel body) => new() { Style = (Style)FindResource("SettingRow"), Child = body, Margin = new Thickness(0, 0, 0, 12) };

    private TextBlock FamilyNote(string text, bool problem = false, Thickness? margin = null)
    {
        var note = new TextBlock { Text = text, Style = (Style)FindResource("SettingDescription"), Margin = margin ?? new Thickness(0, 6, 30, 0), Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
        if (problem) note.Foreground = Palette.Get("Danger");
        return note;
    }

    private void FamilySay(TextBlock note, string text, bool problem)
    {
        note.Text = text;
        note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (problem) note.Foreground = Palette.Get("Danger"); else note.ClearValue(TextBlock.ForegroundProperty);
        if (problem && text.Length > 0) AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Assertive);
    }

    private Button FamilyButton(string label, Action act, bool primary = false, bool quiet = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 10, 8) };
        if (primary) button.Style = (Style)FindResource("Primary"); else if (quiet) button.Style = (Style)FindResource("Quiet");
        button.Click += (_, _) => act();
        return button;
    }

    private DockPanel FamilyStep(int number, string text)
    {
        var line = new DockPanel { Margin = new Thickness(0, 16, 30, 0) };
        var badge = new Squircle { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Palette.Get("LineStrong"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = number.ToString(Lang.Culture), FontSize = 11.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        DockPanel.SetDock(badge, Dock.Left); line.Children.Add(badge);
        line.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return line;
    }
}
