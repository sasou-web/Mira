using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Ta party » of the settings, as on Windows: this account's one link to watch together, made anew when it went too
/// far (the old one stops, its guests go); for an administrator, the guests it let in, each taken back on its own, the
/// links of one device made before party links, and the address on the Internet the links start with: the one
/// Tailscale Funnel gives the PC of Jellyfin, typed here once (<see cref="GuestLinks"/>).
/// </summary>
public sealed class GuestLinksSection
{
    private readonly MainWindow _shell;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private bool _editing, _busy;

    public Control Section { get; }

    public GuestLinksSection(MainWindow shell, Func<string, Control[], Control> section)
    {
        _shell = shell;
        Section = section(L("Ta party", "Your party"), [Ui.Column(14, Note(L("Un seul lien pour regarder ensemble, le même à chaque fois : envoie-le à qui tu veux. Qui l’ouvre te rejoint, sans compte ni rien à régler.",
            "One link to watch together, always the same: send it to whoever you like. Whoever opens it joins you, with no account and nothing to set up.")), _body, _status)]);
        Section.IsVisible = false;
        _ = LoadAsync();
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };

    private async Task LoadAsync()
    {
        if (_shell.Session?.Client is not { } client) return;
        try
        {
            var party = await client.PartyAsync();
            var list = await client.IsAdministratorAsync() == true ? await client.GuestLinksAsync() : null;
            if (party is null && list is null) { Section.IsVisible = false; return; }
            _party = party;
            Render(list);
            Section.IsVisible = true;
        }
        catch (Exception ex) when (Errors.Expected(ex)) { if (Section.IsVisible) _status.Text = Errors.Friendly(ex); }
    }

    private PartyLink? _party;
    private GuestLinkList? _list;

    private void Render(GuestLinkList? list)
    {
        _list = list;
        _body.Children.Clear();
        if (_party is { } party) _body.Children.Add(PartyBox(party));
        if (list is null) return;
        if (list.Address is null || _editing) _body.Children.Add(AddressForm(list.Address));
        var invites = list.Invites ?? [];
        var guests = invites.Where(x => x.Party).ToList();
        _body.Children.Add(Label(guests.Count > 0 ? Lang.Count(guests.Count, "invité", "invités", "guest", "guests") : L("Invités", "Guests")));
        if (guests.Count == 0) _body.Children.Add(Note(L("Personne pour l’instant. Chaque appareil qui ouvre ton lien sans compte ici y apparaît, avec le prénom donné.", "Nobody yet. Each device that opens your link without an account here shows up, with the first name given.")));
        var single = invites.Where(x => !x.Party).ToList();
        foreach (var invite in guests.Concat(single))
        {
            if (single.Count > 0 && ReferenceEquals(invite, single[0])) _body.Children.Add(Label(L("Anciens liens", "Older links")));
            var row = new DockPanel();
            var actions = Ui.Row(8);
            if (invite.Link is { } link && !invite.Opened) actions.Children.Add(Ui.Action(L("Copier", "Copy"), "link", "", () => _ = CopyAsync(link, invite.Name)));
            var text = Ui.Column(3, new TextBlock { Text = invite.Name, FontSize = 15, FontWeight = FontWeight.SemiBold }, Note(GuestLinks.State(invite, DateTimeOffset.Now)));
            actions.Children.Add(Ui.Action(L("Retirer", "Take back"), null, "", () => ConfirmRemove(actions, text, invite)));
            Avalonia.Automation.AutomationProperties.SetName(actions.Children[^1], invite.Party ? L($"Retirer {invite.Name}", $"Remove {invite.Name}") : L($"Retirer le lien de {invite.Name}", $"Take back {invite.Name}’s link"));
            DockPanel.SetDock(actions, Dock.Right); actions.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(actions);
            text.Margin = new Thickness(0, 0, 24, 0);
            row.Children.Add(text);
            _body.Children.Add(row);
        }
        if (GuestLinks.AddressLine(list) is { } addressLine && !_editing)
        {
            var line = new DockPanel();
            var change = Ui.Action(L("Changer", "Change"), null, "quiet", () => { _editing = true; _ = LoadAsync(); });
            Avalonia.Automation.AutomationProperties.SetName(change, L("Changer l’adresse des liens", "Change the address of the links"));
            DockPanel.SetDock(change, Dock.Right); line.Children.Add(change);
            line.Children.Add(Note(addressLine));
            _body.Children.Add(line);
        }
    }

    /// <summary>The address of the links: what to run on the PC of Jellyfin, and the field for what it shows.</summary>
    private Control AddressForm(string? current)
    {
        var command = new TextBox { Text = GuestLinks.FunnelCommand(), IsReadOnly = true, FontSize = 13 };
        Avalonia.Automation.AutomationProperties.SetName(command, L("Commande de Funnel", "Funnel command"));
        var box = new TextBox { Text = current ?? "", Width = 340, FontSize = 14, Watermark = "https://pc.tailnet.ts.net" };
        Avalonia.Automation.AutomationProperties.SetName(box, L("Adresse publique de Mira", "Mira’s public address"));
        Button? save = null;
        async Task SaveAsync()
        {
            if (_shell.Session?.Client is not { } client || _busy) return;
            _busy = true; save!.IsEnabled = false; _status.Text = "";
            try
            {
                var list = await client.SetGuestAddressAsync(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
                _editing = false;
                _status.Text = list.Address is null ? "" : L("Adresse enregistrée.", "Address saved.");
                Render(list);
            }
            catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); }
            finally { _busy = false; save.IsEnabled = true; }
        }
        save = Ui.Action(L("Enregistrer", "Save"), null, "primary", () => _ = SaveAsync());
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SaveAsync(); } };
        return Ui.Column(10,
            Note(L("Pour qu’un lien marche depuis Internet, ouvre Mira au monde par Tailscale Funnel sur le PC de Jellyfin, dans un terminal :", "For a link to work from the Internet, open Mira to the world with Tailscale Funnel on the Jellyfin PC, in a terminal:")),
            command, Note(GuestLinks.FunnelPrivacy),
            Note(L("Écris ici l’adresse qu’il affiche :", "Type the address it shows here:")),
            Ui.Row(10, box, save));
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextTertiary, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>The party link, how far it reaches, « Copier » and « Faire un nouveau lien » (asked once more, in place).</summary>
    private Control PartyBox(PartyLink party)
    {
        var field = new TextBox { Text = party.Link, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeight.SemiBold };
        Avalonia.Automation.AutomationProperties.SetName(field, L("Lien de ta party", "Your party link"));
        var actions = Ui.Row(10);
        var box = Ui.Column(10, field);
        if (!string.IsNullOrWhiteSpace(party.Note))
            box.Children.Add(new TextBlock { Text = party.Note, FontSize = 13, Foreground = party.Reach is "internet" or null ? Ui.Muted : Ui.Brush("#E9DCC3"), TextWrapping = TextWrapping.Wrap });
        box.Children.Add(actions);
        void Idle()
        {
            actions.Children.Clear();
            actions.Children.Add(Ui.Action(L("Copier le lien", "Copy the link"), "link", "primary", () => _ = CopyAsync(party.Link, null)));
            actions.Children.Add(Ui.Action(L("Faire un nouveau lien", "Make a new link"), null, "quiet", Ask));
        }
        TextBlock? said = null;
        void Ask()
        {
            said = Note(L("L’ancien lien ne marchera plus, et ceux qui sont entrés sans compte sortent du groupe : leur compte est supprimé de Jellyfin. Tes amis qui ont un compte ici ne perdent rien.",
                "The old link will stop working, and those who came in without an account leave the group: their account is deleted from Jellyfin. Your friends with an account here lose nothing."));
            box.Children.Insert(box.Children.Count - 1, said);
            actions.Children.Clear();
            var confirm = Ui.Action(L("Faire un nouveau lien", "Make a new link"), "refresh", "", () => _ = RenewAsync());
            actions.Children.Add(confirm);
            actions.Children.Add(Ui.Action(L("Garder celui-ci", "Keep this one"), null, "quiet", () => { box.Children.Remove(said); Idle(); }));
            Avalonia.Threading.Dispatcher.UIThread.Post(() => confirm.Focus(), Avalonia.Threading.DispatcherPriority.Input);
        }
        Idle();
        return box;
    }

    private async Task RenewAsync()
    {
        if (_shell.Session?.Client is not { } client || _busy) return;
        _busy = true;
        try
        {
            _party = await client.PartyAsync(fresh: true);
            _status.Text = L("Nouveau lien prêt : l’ancien ne marche plus.", "New link ready: the old one no longer works.");
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); }
        finally { _busy = false; }
        await LoadAsync();
    }

    private async Task CopyAsync(string link, string? name)
    {
        if (TopLevel.GetTopLevel(_shell)?.Clipboard is not { } clipboard) { _status.Text = L("Copie impossible ici : sélectionne le lien.", "Can’t copy here: select the link."); return; }
        await clipboard.SetTextAsync(GuestLinks.Message(link));
        _status.Text = name is null ? L("Lien copié : envoie-le à qui tu veux.", "Link copied: send it to whoever you like.") : L($"Lien copié : envoie-le à {name}.", $"Link copied: send it to {name}.");
    }

    /// <summary>« Retirer » asked once more, in the row: what taking the link back does.</summary>
    private void ConfirmRemove(StackPanel actions, StackPanel text, GuestLink invite)
    {
        var kept = actions.Children.ToList();
        var said = Note(invite.Party
            ? L($"{invite.Name} sort du groupe tout de suite. Son compte est supprimé de Jellyfin ; s’il rouvre ton lien, il entre de nouveau.", $"{invite.Name} leaves the group right away. Their account is deleted from Jellyfin; if they open your link again, they come back in.")
            : invite.Opened
            ? L($"{invite.Name} sort du groupe tout de suite et ne pourra plus revenir avec ce lien. Son compte est supprimé de Jellyfin.", $"{invite.Name} leaves the group right away and can’t come back with this link. Their account is deleted from Jellyfin.")
            : L($"Le lien de {invite.Name} ne marchera plus. Son compte est supprimé de Jellyfin.", $"{invite.Name}’s link will stop working. Their account is deleted from Jellyfin."));
        text.Children.Add(said);
        actions.Children.Clear();
        var confirm = Ui.Action(invite.Party ? L($"Retirer {invite.Name}", $"Remove {invite.Name}") : L("Retirer le lien", "Take back the link"), "close", "", () => _ = RemoveAsync(invite));
        Avalonia.Automation.AutomationProperties.SetName(confirm, invite.Party ? L($"Retirer {invite.Name}", $"Remove {invite.Name}") : L($"Retirer le lien de {invite.Name}", $"Take back {invite.Name}’s link"));
        actions.Children.Add(confirm);
        actions.Children.Add(Ui.Action(L("Garder", "Keep"), null, "quiet", () => { text.Children.Remove(said); actions.Children.Clear(); actions.Children.AddRange(kept); }));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => confirm.Focus(), Avalonia.Threading.DispatcherPriority.Input);
    }

    private async Task RemoveAsync(GuestLink invite)
    {
        if (_shell.Session?.Client is not { } client || _busy) return;
        _busy = true;
        try
        {
            await client.RemoveGuestLinkAsync(invite.Id);
            _status.Text = invite.Party ? L($"{invite.Name} retiré.", $"{invite.Name} removed.") : L($"Lien de {invite.Name} retiré.", $"{invite.Name}’s link taken back.");
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); }
        finally { _busy = false; }
        await LoadAsync();
    }
}
