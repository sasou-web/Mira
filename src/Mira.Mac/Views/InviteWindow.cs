using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Inviter quelqu'un » in a group of « Regarder ensemble »: your party link, or the link that opens Mira web on the group (#/ensemble/…),
/// to send to a friend, with what the friend needs (an account of this Jellyfin, Tailscale away from home). The same
/// words as Windows and Mira web (<see cref="Together.InviteSteps"/>).
/// </summary>
public sealed class InviteWindow : Window
{
    public InviteWindow(string lead, string link, IReadOnlyList<string> list, Action<string> notice)
    {
        Title = L("Inviter quelqu’un", "Invite someone"); Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var field = new TextBox { Text = link, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeight.SemiBold };
        Avalonia.Automation.AutomationProperties.SetName(field, L("Lien d’invitation", "Invite link"));
        var steps = new StackPanel { Spacing = 10 };
        for (var i = 0; i < list.Count; i++) steps.Children.Add(Ui.Text($"{i + 1}. {list[i]}", 13.5, color: Ui.Muted));
        foreach (var step in steps.Children.OfType<TextBlock>()) { step.TextWrapping = TextWrapping.Wrap; step.LineHeight = 20; }
        var copy = Ui.Action(L("Copier le lien", "Copy the link"), "link", "primary", async () =>
        {
            if (Clipboard is not { } clipboard) { notice(L("Copie impossible ici : sélectionne le lien.", "Can’t copy here: select the link.")); return; }
            await clipboard.SetTextAsync(link);
            notice(L("Lien copié : envoie-le à ton ami.", "Link copied: send it to your friend."));
            Close();
        });
        var close = Ui.Action(L("Fermer", "Close"), null, "quiet", Close);
        Content = new Border
        {
            Padding = new Thickness(32, 28),
            Child = Ui.Column(16,
                Ui.Text(L("Inviter quelqu’un", "Invite someone"), 26, FontWeight.Bold),
                new TextBlock { Text = lead, FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap },
                field, steps,
                new DockPanel { Children = { close, copy } }),
        };
        DockPanel.SetDock(copy, Dock.Right); copy.HorizontalAlignment = HorizontalAlignment.Right; close.HorizontalAlignment = HorizontalAlignment.Left;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Opened += (_, _) => copy.Focus();
    }
}
