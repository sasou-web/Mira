using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « Ta party » (Réglages → Amis et invités): this account's one link to watch together (JellyfinClient.PartyAsync),
// made anew when it went too far (the old one stops, its guests go); for an administrator, the guests it let in, each
// taken back on its own (GuestLinks, JellyfinClient.GuestLinksAsync; the extension makes each guest's account and
// deletes it with them), the links of one device made before party links, and the address on the Internet the links
// start with: the one Tailscale Funnel gives the PC of Jellyfin, which Mira turns on itself there
// (TailscaleCli.OpenFunnel), after saying what it means.
public partial class MainWindow
{
    private bool _guestBusy;
    private Task _guestShowing = Task.CompletedTask;
    /// <summary>The page where Funnel is allowed on the tailnet, when Tailscale asked for it.</summary>
    private string? _funnelPage;
    /// <summary>The address is being changed: its form shows although one is set.</summary>
    private bool _guestAddressEditing;

    /// <summary>The row, for an account whose Jellyfin's extension makes party links (an administrator also sees its guests).</summary>
    private async Task ShowGuestLinksAsync()
    {
        if (_guestBusy) return;
        if (_demo || _client is not { } client) { GuestRow.Visibility = Visibility.Collapsed; return; }
        GuestRules.Text = L("Un seul lien pour regarder ensemble, le même à chaque fois : envoie-le à qui tu veux. Qui l’ouvre te rejoint, sans compte ni rien à régler.",
            "One link to watch together, always the same: send it to whoever you like. Whoever opens it joins you, with no account and nothing to set up.");
        PartyLink? party = null;
        GuestLinkList? list = null;
        try
        {
            party = await client.PartyAsync(ct: _connectionLifetime.Token);
            if (await client.IsAdministratorAsync() == true) list = await client.GuestLinksAsync(_connectionLifetime.Token);
        }
        catch (RelayRefusedException) { list = null; }
        catch (Exception ex) when (IsExpected(ex)) { if (GuestRow.Visibility == Visibility.Visible) GuestStatus.Text = ServerError(ex); return; }
        if (!ReferenceEquals(client, _client) || _guestBusy) return;
        if (party is null && list is null) { GuestRow.Visibility = Visibility.Collapsed; return; }
        ShowParty(party);
        if (list is null) { HideGuestAdmin(); GuestList.Children.Clear(); }
        else await RenderGuestLinksAsync(client, list);
        GuestRow.Visibility = Visibility.Visible;
    }

    private void ShowParty(PartyLink? party)
    {
        PartyPanel.Visibility = party is null ? Visibility.Collapsed : Visibility.Visible;
        if (party is null) return;
        PartyLinkBox.Text = party.Link;
        PartyReach.Text = party.Note ?? "";
        PartyReach.Foreground = Palette.Get(party.Reach is "internet" or null ? "Muted" : "Warning");
        PartyReach.Visibility = string.IsNullOrWhiteSpace(party.Note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HideGuestAdmin()
    {
        GuestAddressPanel.Visibility = GuestFunnelPanel.Visibility = GuestAddressLine.Visibility = Visibility.Collapsed;
    }

    private async Task RenderGuestLinksAsync(JellyfinClient client, GuestLinkList list)
    {
        var address = list.Address;
        var asking = address is null || _guestAddressEditing;
        GuestAddressPanel.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
        GuestFunnelPanel.Visibility = Visibility.Collapsed;
        var port = Uri.TryCreate(client.Connection.Server, UriKind.Absolute, out var server) && server.Scheme == Uri.UriSchemeHttp ? server.Port : 8096;
        GuestCommandBox.Text = GuestLinks.FunnelCommand(port);
        if (asking)
        {
            if (address is not null && GuestAddressBox.Text.Length == 0) GuestAddressBox.Text = address;
            // On the PC of Jellyfin with Tailscale, Mira opens Funnel itself; the command stays for another way.
            if (await JellyfinOnThisPcAsync(client) && ReferenceEquals(client, _client))
            {
                GuestFunnelText.Text = GuestLinks.FunnelPrivacy;
                GuestEnableButton.Visibility = _funnelPage is null ? Visibility.Collapsed : Visibility.Visible;
                GuestFunnelPanel.Visibility = Visibility.Visible;
            }
        }
        GuestAddressLine.Visibility = address is null || asking ? Visibility.Collapsed : Visibility.Visible;
        GuestAddressText.Text = GuestLinks.AddressLine(list) ?? "";

        GuestList.Children.Clear();
        var invites = list.Invites ?? [];
        var guests = invites.Where(x => x.Party).ToList();
        var single = invites.Where(x => !x.Party).ToList();
        GuestList.Children.Add(ListLabel(guests.Count > 0 ? Lang.Count(guests.Count, "invité", "invités", "guest", "guests") : L("Invités", "Guests")));
        if (guests.Count == 0)
            GuestList.Children.Add(new TextBlock { Text = L("Personne pour l’instant. Chaque appareil qui ouvre ton lien sans compte ici y apparaît, avec le prénom donné.", "Nobody yet. Each device that opens your link without an account here shows up, with the first name given."), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 6, 0, 0) });
        foreach (var invite in guests) GuestList.Children.Add(GuestLine(invite));
        if (single.Count > 0) GuestList.Children.Add(ListLabel(L("Anciens liens", "Older links")));
        foreach (var invite in single) GuestList.Children.Add(GuestLine(invite));
    }

    private TextBlock ListLabel(string text) => new() { Text = text, FontSize = 12, Foreground = Palette.Get("TextTertiary"), Margin = new Thickness(0, 18, 0, 0) };

    /// <summary>A guest of the party, or an older link of one device: who, where it is, « Retirer » (and « Copier » for a link not opened yet).</summary>
    private DockPanel GuestLine(GuestLink invite)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = invite.Name, Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = GuestLinks.State(invite, DateTimeOffset.Now), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 3, 0, 0) });
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var remove = new Button { Content = L("Retirer", "Take back"), Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
        AutomationProperties.SetName(remove, invite.Party ? L($"Retirer {invite.Name}", $"Remove {invite.Name}") : L($"Retirer le lien de {invite.Name}", $"Take back {invite.Name}’s link"));
        remove.Click += async (_, _) => await RemoveGuestLinkAsync(invite);
        DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
        if (invite.Link is { } link && !invite.Opened)
        {
            var copy = new Button { Content = L("Copier", "Copy"), Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
            AutomationProperties.SetName(copy, L($"Copier le lien de {invite.Name}", $"Copy {invite.Name}’s link"));
            copy.Click += (_, _) => CopyGuestLink(link, invite.Name);
            DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy);
        }
        row.Children.Add(text);
        return row;
    }

    private void PartyCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(GuestLinks.Message(PartyLinkBox.Text)); GuestStatus.Text = L("Lien copié : envoie-le à qui tu veux.", "Link copied: send it to whoever you like."); }
        catch (System.Runtime.InteropServices.ExternalException) { GuestStatus.Text = L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again."); }
    }

    /// <summary>« Faire un nouveau lien »: the old one stops, the guests it let in leave the group and lose their account.</summary>
    private async void PartyNew_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _guestBusy) return;
        if (!await ConfirmAsync(L("Faire un nouveau lien ?", "Make a new link?"),
            L("L’ancien lien ne marchera plus, et ceux qui sont entrés sans compte sortent du groupe : leur compte est supprimé de Jellyfin. Tes amis qui ont un compte ici ne perdent rien.",
                "The old link will stop working, and those who came in without an account leave the group: their account is deleted from Jellyfin. Your friends with an account here lose nothing."),
            L("Faire un nouveau lien", "Make a new link"), L("Garder celui-ci", "Keep this one"))) return;
        _guestBusy = true; PartyNewButton.IsEnabled = false;
        var ct = _connectionLifetime.Token;
        try
        {
            var party = await client.PartyAsync(fresh: true, ct);
            if (!ReferenceEquals(client, _client)) return;
            ShowParty(party);
            GuestStatus.Text = L("Nouveau lien prêt : l’ancien ne marche plus.", "New link ready: the old one no longer works.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (IsExpected(ex)) { GuestStatus.Text = ServerError(ex); }
        finally { _guestBusy = false; PartyNewButton.IsEnabled = true; }
        await ShowGuestLinksAsync();
    }

    private void CopyGuestLink(string link, string name)
    {
        try { Clipboard.SetText(GuestLinks.Message(link)); GuestStatus.Text = L($"Lien copié : envoie-le à {name}.", $"Link copied: send it to {name}."); }
        catch (System.Runtime.InteropServices.ExternalException) { GuestStatus.Text = L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again."); }
    }
    private async Task RemoveGuestLinkAsync(GuestLink invite)
    {
        if (_client is not { } client || _guestBusy) return;
        var question = invite.Party
            ? L($"{invite.Name} sort du groupe tout de suite. Son compte est supprimé de Jellyfin ; s’il rouvre ton lien, il entre de nouveau.", $"{invite.Name} leaves the group right away. Their account is deleted from Jellyfin; if they open your link again, they come back in.")
            : invite.Opened
            ? L($"{invite.Name} sort du groupe tout de suite et ne pourra plus revenir avec ce lien. Son compte est supprimé de Jellyfin.", $"{invite.Name} leaves the group right away and can’t come back with this link. Their account is deleted from Jellyfin.")
            : L($"Le lien de {invite.Name} ne marchera plus. Son compte est supprimé de Jellyfin.", $"{invite.Name}’s link will stop working. Their account is deleted from Jellyfin.");
        if (!await ConfirmAsync(invite.Party ? L($"Retirer {invite.Name} ?", $"Remove {invite.Name}?") : L("Retirer ce lien ?", "Take back this link?"), question, L("Retirer", "Take back"), L("Garder", "Keep"))) return;
        _guestBusy = true;
        var ct = _connectionLifetime.Token;
        try
        {
            await client.RemoveGuestLinkAsync(invite.Id, ct);
            GuestStatus.Text = invite.Party ? L($"{invite.Name} retiré.", $"{invite.Name} removed.") : L($"Lien de {invite.Name} retiré.", $"{invite.Name}’s link taken back.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (IsExpected(ex)) { GuestStatus.Text = ServerError(ex); }
        finally { _guestBusy = false; }
        await ShowGuestLinksAsync();
    }

    private void GuestCommandCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(GuestCommandBox.Text); GuestStatus.Text = L("Commande copiée.", "Command copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { GuestStatus.Text = L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again."); }
    }
    private void GuestAddress_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; GuestAddressSave_Click(sender, e); } }
    private async void GuestAddressSave_Click(object sender, RoutedEventArgs e) => await SaveGuestAddressAsync(GuestAddressBox.Text.Trim());
    private async Task SaveGuestAddressAsync(string? address)
    {
        if (_client is not { } client || _guestBusy) return;
        _guestBusy = true; GuestAddressSave.IsEnabled = false; GuestStatus.Text = "";
        var ct = _connectionLifetime.Token;
        try
        {
            var list = await client.SetGuestAddressAsync(string.IsNullOrEmpty(address) ? null : address, ct);
            if (!ReferenceEquals(client, _client)) return;
            _guestAddressEditing = false; GuestAddressBox.Clear();
            GuestStatus.Text = list.Address is null ? "" : L("Adresse enregistrée.", "Address saved.");
            await RenderGuestLinksAsync(client, list);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { GuestStatus.Text = ServerError(ex); }
        finally { _guestBusy = false; GuestAddressSave.IsEnabled = true; }
    }
    private async void GuestAddressChange_Click(object sender, RoutedEventArgs e)
    {
        _guestAddressEditing = true;
        await ShowGuestLinksAsync();
        GuestAddressBox.Focus(); GuestAddressBox.SelectAll();
    }

    /// <summary>
    /// « Ouvrir Mira à Internet »: Funnel turned on for /Mira by Tailscale's command line, in the person's rights, then
    /// its address given to the extension. Tailscale may first want Funnel allowed on the tailnet: its page is offered.
    /// </summary>
    private async void GuestFunnel_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _guestBusy) return;
        _guestBusy = true; GuestFunnelButton.IsEnabled = false; GuestStatus.Text = L("Ouverture de Funnel…", "Opening Funnel…");
        try
        {
            var port = Uri.TryCreate(client.Connection.Server, UriKind.Absolute, out var server) && server.Scheme == Uri.UriSchemeHttp ? server.Port : 8096;
            var (serves, address) = await Task.Run(TailscaleCli.Funnel);
            string? page = null;
            if (!serves) (serves, address, page) = await Task.Run(() => TailscaleCli.OpenFunnel(port));
            if (!ReferenceEquals(client, _client)) return;
            _funnelPage = page;
            GuestEnableButton.Visibility = page is null ? Visibility.Collapsed : Visibility.Visible;
            if (serves && address is not null)
            {
                _guestBusy = false;
                await SaveGuestAddressAsync(address);
                return;
            }
            GuestStatus.Text = page is not null
                ? L("Tailscale demande d’abord d’autoriser Funnel : touche « Autoriser Funnel sur Tailscale », accepte sur sa page, puis « Ouvrir Mira à Internet » de nouveau.",
                    "Tailscale first asks to allow Funnel: press “Allow Funnel on Tailscale”, accept on its page, then “Open Mira to the Internet” again.")
                : TailscaleCli.Read().Readable
                    ? L("Tailscale n’a pas ouvert Funnel. Lance la commande ci-dessous dans PowerShell pour voir ce qu’il répond.", "Tailscale didn’t open Funnel. Run the command below in PowerShell to see what it says.")
                    : L("Tailscale ne répond pas sur ce PC : ouvre-le et connecte-toi, puis réessaie.", "Tailscale isn’t answering on this PC: open it and sign in, then try again.");
        }
        finally { _guestBusy = false; GuestFunnelButton.IsEnabled = true; }
    }
    private void GuestEnable_Click(object sender, RoutedEventArgs e) { if (_funnelPage is { } page) OpenWebPage(page); }

    /// <summary>« Sans compte ni Tailscale ? » of the invitation: the row in Réglages → Jellyfin.</summary>
    private async void InviteGuestLink_Click(object sender, RoutedEventArgs e)
    {
        await CloseInviteAsync();
        Settings_Click(this, new());
        SelectSettingsTab("sharing");
        await _guestShowing;
        if (GuestRow.Visibility != Visibility.Visible || SettingsOverlay.Visibility != Visibility.Visible) return;
        GuestRow.BringIntoView();
        if (GuestFunnelPanel.Visibility == Visibility.Visible) GuestFunnelButton.Focus();
        else if (GuestAddressPanel.Visibility == Visibility.Visible) GuestAddressBox.Focus();
    }

    /// <summary>Tailscale's page to allow Funnel on the tailnet, offered once per session when it asks for it.</summary>
    private bool _funnelAsked;
    /// <summary>
    /// Party links that work everywhere, with nothing to set up: on the PC of Jellyfin, an administrator's account,
    /// Funnel opened for /Mira by itself (at sign-in and when a party is shared) and its address given to the
    /// extension. When Tailscale first wants Funnel allowed on the tailnet (its admin's one-time consent, which no
    /// program may give), its page is offered once, then Funnel opens as soon as it is allowed. True once it serves Mira.
    /// </summary>
    private async Task<bool> KeepFunnelOpenAsync(JellyfinClient client, CancellationToken ct)
    {
        try
        {
            if (!await JellyfinOnThisPcAsync(client) || await client.IsAdministratorAsync(ct) != true) return false;
            var list = await client.GuestLinksAsync(ct);
            var port = Uri.TryCreate(client.Connection.Server, UriKind.Absolute, out var server) && server.Scheme == Uri.UriSchemeHttp ? server.Port : 8096;
            var (serves, address) = await Task.Run(TailscaleCli.Funnel, ct);
            string? page = null;
            if (!serves && TailscaleCli.Read().Readable) (serves, address, page) = await Task.Run(() => TailscaleCli.OpenFunnel(port), ct);
            if (!ReferenceEquals(client, _client)) return false;
            if (serves && address is not null)
            {
                if (!string.Equals(list.Address?.TrimEnd('/'), address.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) await client.SetGuestAddressAsync(address, ct);
                return true;
            }
            if (page is null || _funnelAsked) return false;
            _funnelAsked = true;
            SetNotice(L("Pour que tes liens de party marchent partout, Tailscale demande d’autoriser Funnel une fois sur ton compte.", "For your party links to work everywhere, Tailscale asks you to allow Funnel once on your account."),
                L("Autoriser", "Allow"), () => { OpenWebPage(page); _ = AwaitFunnelAsync(client, port, ct); });
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }
    /// <summary>Funnel allowed on Tailscale's page: opened here as soon as it is (every 5 s, 10 minutes at most).</summary>
    private async Task AwaitFunnelAsync(JellyfinClient client, int port, CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < 120 && ReferenceEquals(client, _client); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var (serves, address, _) = await Task.Run(() => TailscaleCli.OpenFunnel(port), ct);
                if (!serves || address is null) continue;
                await client.SetGuestAddressAsync(address, ct);
                SetDone(L("Funnel est ouvert : tes liens de party marchent partout.", "Funnel is open: your party links work everywhere."));
                if (InviteOverlay.Visibility == Visibility.Visible) await ShowPartyAsync(client, opened: true);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
}
