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
/// « Liens d'invités » of the settings, for an administrator of a Jellyfin whose extension makes them: a link for
/// someone without an account here, who opens it from anywhere and watches with this account's group. The address the
/// links start with is the one Tailscale Funnel gives the PC of Jellyfin, typed here once (the same words as Windows
/// and Mira web: <see cref="GuestLinks"/>).
/// </summary>
public sealed class GuestLinksSection
{
    private readonly MainWindow _shell;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private Control? _made;
    private string? _madeLink;
    private bool _editing, _busy;

    public Control Section { get; }

    public GuestLinksSection(MainWindow shell, Func<string, Control[], Control> section)
    {
        _shell = shell;
        Section = section(L("Liens d’invités", "Guest links"), [Ui.Column(14, Note(GuestLinks.Rules), _body, _status)]);
        Section.IsVisible = false;
        _ = LoadAsync();
    }

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };

    private async Task LoadAsync()
    {
        if (_shell.Session?.Client is not { } client) return;
        try
        {
            if (await client.IsAdministratorAsync() != true) return;
            Render(await client.GuestLinksAsync());
            Section.IsVisible = true;
        }
        catch (Exception ex) when (Errors.Expected(ex)) { if (Section.IsVisible) _status.Text = Errors.Friendly(ex); }
    }

    private void Render(GuestLinkList list)
    {
        _body.Children.Clear();
        if (list.Address is null || _editing) _body.Children.Add(AddressForm(list.Address));
        else _body.Children.Add(_made ?? NewLinkForm());
        foreach (var invite in list.Invites ?? [])
        {
            var row = new DockPanel();
            var actions = Ui.Row(8);
            if (invite.Link is { } link && !invite.Opened) actions.Children.Add(Ui.Action(L("Copier", "Copy"), "link", "", () => _ = CopyAsync(link, invite.Name)));
            var text = Ui.Column(3, new TextBlock { Text = invite.Name, FontSize = 15, FontWeight = FontWeight.SemiBold }, Note(GuestLinks.State(invite, DateTimeOffset.Now)));
            actions.Children.Add(Ui.Action(L("Retirer", "Take back"), null, "", () => ConfirmRemove(actions, text, invite)));
            Avalonia.Automation.AutomationProperties.SetName(actions.Children[^1], L($"Retirer le lien de {invite.Name}", $"Take back {invite.Name}’s link"));
            DockPanel.SetDock(actions, Dock.Right); actions.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(actions);
            text.Margin = new Thickness(0, 0, 24, 0);
            row.Children.Add(text);
            _body.Children.Add(row);
        }
        if (list.Address is { } address && !_editing)
        {
            var line = new DockPanel();
            var change = Ui.Action(L("Changer", "Change"), null, "quiet", () => { _editing = true; _ = LoadAsync(); });
            Avalonia.Automation.AutomationProperties.SetName(change, L("Changer l’adresse des liens", "Change the address of the links"));
            DockPanel.SetDock(change, Dock.Right); line.Children.Add(change);
            line.Children.Add(Note(L($"Les liens commencent par {address}.", $"Links start with {address}.")));
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

    /// <summary>A new link: the guest's name (what the group shows of them), then the link to send.</summary>
    private Control NewLinkForm()
    {
        var box = new TextBox { Width = 260, MaxLength = 40, FontSize = 14, Watermark = L("Son prénom", "Their first name") };
        Avalonia.Automation.AutomationProperties.SetName(box, L("Prénom de ton invité", "Your guest’s first name"));
        Button? create = null;
        async Task CreateAsync()
        {
            if (_shell.Session?.Client is not { } client || _busy) return;
            var name = Friends.CleanName(box.Text);
            if (name.Length > 0 && Friends.NameProblem(name, []) is { } wrong) { _status.Text = wrong; box.Focus(); return; }
            _busy = true; create!.IsEnabled = false; _status.Text = L("Création du compte de l’invité…", "Creating the guest’s account…");
            try
            {
                var invite = await client.MakeGuestLinkAsync(name);
                _status.Text = "";
                if (invite.Link is { } link) { _made = Made(invite, link); _madeLink = link; }
                _busy = false;
                await LoadAsync();
            }
            catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); }
            finally { _busy = false; create.IsEnabled = true; }
        }
        create = Ui.Action(L("Créer le lien", "Create the link"), "link", "primary", () => _ = CreateAsync());
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = CreateAsync(); } };
        return Ui.Row(10, box, create);
    }

    private Control Made(GuestLink invite, string link)
    {
        var field = new TextBox { Text = link, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeight.SemiBold };
        Avalonia.Automation.AutomationProperties.SetName(field, L("Lien de l’invité", "Guest’s link"));
        return Ui.Column(10,
            new TextBlock { Text = L($"Envoie ce lien à {invite.Name} : il ouvre Mira et rejoint ton groupe.", $"Send this link to {invite.Name}: it opens Mira and joins your group."), FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            field,
            Ui.Row(10,
                Ui.Action(L("Copier le lien", "Copy the link"), "link", "primary", () => _ = CopyAsync(link, invite.Name)),
                Ui.Action(L("Un autre lien", "Another link"), null, "quiet", () => { _made = null; _ = LoadAsync(); })));
    }

    private async Task CopyAsync(string link, string name)
    {
        if (TopLevel.GetTopLevel(_shell)?.Clipboard is not { } clipboard) { _status.Text = L("Copie impossible ici : sélectionne le lien.", "Can’t copy here: select the link."); return; }
        await clipboard.SetTextAsync(GuestLinks.Message(link));
        _status.Text = L($"Lien copié : envoie-le à {name}.", $"Link copied: send it to {name}.");
    }

    /// <summary>« Retirer » asked once more, in the row: what taking the link back does.</summary>
    private void ConfirmRemove(StackPanel actions, StackPanel text, GuestLink invite)
    {
        var kept = actions.Children.ToList();
        var said = Note(invite.Opened
            ? L($"{invite.Name} sort du groupe tout de suite et ne pourra plus revenir avec ce lien. Son compte est supprimé de Jellyfin.", $"{invite.Name} leaves the group right away and can’t come back with this link. Their account is deleted from Jellyfin.")
            : L($"Le lien de {invite.Name} ne marchera plus. Son compte est supprimé de Jellyfin.", $"{invite.Name}’s link will stop working. Their account is deleted from Jellyfin."));
        text.Children.Add(said);
        actions.Children.Clear();
        var confirm = Ui.Action(L("Retirer le lien", "Take back the link"), "close", "", () => _ = RemoveAsync(invite));
        Avalonia.Automation.AutomationProperties.SetName(confirm, L($"Retirer le lien de {invite.Name}", $"Take back {invite.Name}’s link"));
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
            _status.Text = L($"Lien de {invite.Name} retiré.", $"{invite.Name}’s link taken back.");
            if (invite.Link is not null && invite.Link == _madeLink) { _made = null; _madeLink = null; }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); }
        finally { _busy = false; }
        await LoadAsync();
    }
}
