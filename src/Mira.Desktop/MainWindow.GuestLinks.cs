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

// « Inviter par lien » (Réglages → Jellyfin, for an administrator): a link for someone without an account here, who
// opens it from anywhere and watches with this account's group (GuestLinks, JellyfinClient.GuestLinksAsync; the
// extension makes each link's account and deletes it with the link). Links need the address Tailscale Funnel gives
// the PC of Jellyfin: on that PC, Mira turns Funnel on itself (TailscaleCli.OpenFunnel), after saying what it means.
public partial class MainWindow
{
    private bool _guestBusy;
    private Task _guestShowing = Task.CompletedTask;
    /// <summary>The page where Funnel is allowed on the tailnet, when Tailscale asked for it.</summary>
    private string? _funnelPage;
    /// <summary>The address is being changed: its form shows although one is set.</summary>
    private bool _guestAddressEditing;

    /// <summary>The row, for an administrator of a Jellyfin whose extension makes links; what was just made stays shown.</summary>
    private async Task ShowGuestLinksAsync(bool keepMade = true)
    {
        if (_guestBusy) return;
        if (_demo || _client is not { } client) { GuestRow.Visibility = Visibility.Collapsed; return; }
        GuestRules.Text = GuestLinks.Rules;
        GuestLinkList? list = null;
        try { if (await client.IsAdministratorAsync() == true) list = await client.GuestLinksAsync(_connectionLifetime.Token); }
        catch (RelayRefusedException) { list = null; }
        catch (Exception ex) when (IsExpected(ex)) { if (GuestRow.Visibility == Visibility.Visible) GuestStatus.Text = ServerError(ex); return; }
        if (!ReferenceEquals(client, _client) || _guestBusy) return;
        if (list is null) { GuestRow.Visibility = Visibility.Collapsed; return; }
        await RenderGuestLinksAsync(client, list, keepMade);
        GuestRow.Visibility = Visibility.Visible;
    }

    private async Task RenderGuestLinksAsync(JellyfinClient client, GuestLinkList list, bool keepMade = true)
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
        var made = keepMade && GuestMade.Visibility == Visibility.Visible;
        GuestForm.Visibility = !asking && !made ? Visibility.Visible : Visibility.Collapsed;
        if (asking) GuestMade.Visibility = Visibility.Collapsed;
        GuestAddressLine.Visibility = address is null || asking ? Visibility.Collapsed : Visibility.Visible;
        GuestAddressText.Text = GuestLinks.AddressLine(list) ?? "";

        GuestList.Children.Clear();
        var invites = list.Invites ?? [];
        if (invites.Count > 0)
            GuestList.Children.Add(new TextBlock { Text = Lang.Count(invites.Count, "lien en cours", "liens en cours", "link in use", "links in use"), FontSize = 12, Foreground = Palette.Get("TextTertiary"), Margin = new Thickness(0, 18, 0, 0) });
        foreach (var invite in invites)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = invite.Name, Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = GuestLinks.State(invite, DateTimeOffset.Now), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 3, 0, 0) });
            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var remove = new Button { Content = L("Retirer", "Take back"), Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
            AutomationProperties.SetName(remove, L($"Retirer le lien de {invite.Name}", $"Take back {invite.Name}’s link"));
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
            GuestList.Children.Add(row);
        }
    }

    private void GuestName_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; GuestCreate_Click(sender, e); } }
    private async void GuestCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _guestBusy) return;
        var name = Friends.CleanName(GuestNameBox.Text);
        if (name.Length > 0 && Friends.NameProblem(name, []) is { } wrong) { GuestStatus.Text = wrong; GuestNameBox.Focus(); return; }
        _guestBusy = true; GuestCreateButton.IsEnabled = false; GuestStatus.Text = L("Création du compte de l’invité…", "Creating the guest’s account…");
        var ct = _connectionLifetime.Token;
        try
        {
            var invite = await client.MakeGuestLinkAsync(name, ct);
            if (!ReferenceEquals(client, _client)) return;
            GuestStatus.Text = ""; GuestNameBox.Clear();
            if (invite.Link is { } link)
            {
                GuestMadeText.Text = L($"Envoie ce lien à {invite.Name} : il ouvre Mira et rejoint ton groupe.", $"Send this link to {invite.Name}: it opens Mira and joins your group.");
                GuestLinkBox.Text = link; GuestCopyButton.Tag = invite.Name;
                GuestForm.Visibility = Visibility.Collapsed; GuestMade.Visibility = Visibility.Visible;
                _ = Dispatcher.BeginInvoke(() => GuestCopyButton.Focus(), DispatcherPriority.Input);
            }
            _guestBusy = false;
            await ShowGuestLinksAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { GuestStatus.Text = ServerError(ex); }
        finally { _guestBusy = false; GuestCreateButton.IsEnabled = true; }
    }
    private void GuestCopy_Click(object sender, RoutedEventArgs e) => CopyGuestLink(GuestLinkBox.Text, GuestCopyButton.Tag as string ?? "");
    private void CopyGuestLink(string link, string name)
    {
        try { Clipboard.SetText(GuestLinks.Message(link)); GuestStatus.Text = L($"Lien copié : envoie-le à {name}.", $"Link copied: send it to {name}."); }
        catch (System.Runtime.InteropServices.ExternalException) { GuestStatus.Text = L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again."); }
    }
    private void GuestAgain_Click(object sender, RoutedEventArgs e)
    {
        GuestMade.Visibility = Visibility.Collapsed; GuestForm.Visibility = Visibility.Visible; GuestStatus.Text = "";
        GuestNameBox.Focus();
    }
    private async Task RemoveGuestLinkAsync(GuestLink invite)
    {
        if (_client is not { } client || _guestBusy) return;
        var question = invite.Opened
            ? L($"{invite.Name} sort du groupe tout de suite et ne pourra plus revenir avec ce lien. Son compte est supprimé de Jellyfin.", $"{invite.Name} leaves the group right away and can’t come back with this link. Their account is deleted from Jellyfin.")
            : L($"Le lien de {invite.Name} ne marchera plus. Son compte est supprimé de Jellyfin.", $"{invite.Name}’s link will stop working. Their account is deleted from Jellyfin.");
        if (MessageBox.Show(this, question, L("Retirer ce lien ?", "Take back this link?"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _guestBusy = true;
        var ct = _connectionLifetime.Token;
        try
        {
            await client.RemoveGuestLinkAsync(invite.Id, ct);
            if (GuestLinkBox.Text == invite.Link) { GuestMade.Visibility = Visibility.Collapsed; GuestLinkBox.Clear(); }
            GuestStatus.Text = L($"Lien de {invite.Name} retiré.", $"{invite.Name}’s link taken back.");
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
            if (GuestForm.Visibility == Visibility.Visible) GuestNameBox.Focus();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { GuestStatus.Text = ServerError(ex); }
        finally { _guestBusy = false; GuestAddressSave.IsEnabled = true; }
    }
    private async void GuestAddressChange_Click(object sender, RoutedEventArgs e)
    {
        _guestAddressEditing = true;
        await ShowGuestLinksAsync(keepMade: false);
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
        if (GuestForm.Visibility == Visibility.Visible) GuestNameBox.Focus();
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
