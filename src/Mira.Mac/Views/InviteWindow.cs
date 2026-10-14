using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;

namespace Mira.Mac.Views;

/// <summary>
/// « Inviter quelqu'un » in a group of « Regarder ensemble »: the link that opens Mira web on the group (#/ensemble/…),
/// to send to a friend, with what the friend needs (an account of this Jellyfin, Tailscale away from home). The same
/// words as Windows and Mira web (<see cref="Together.InviteSteps"/>).
/// </summary>
public sealed class InviteWindow : Window
{
    public InviteWindow(Together.Group group, string link, Action<string> notice)
    {
        Title = "Inviter quelqu’un"; Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var field = new TextBox { Text = link, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeight.SemiBold };
        Avalonia.Automation.AutomationProperties.SetName(field, "Lien d’invitation");
        var steps = new StackPanel { Spacing = 10 };
        var list = Together.InviteSteps(link);
        for (var i = 0; i < list.Count; i++) steps.Children.Add(Ui.Text($"{i + 1}. {list[i]}", 13.5, color: Ui.Muted));
        foreach (var step in steps.Children.OfType<TextBlock>()) { step.TextWrapping = TextWrapping.Wrap; step.LineHeight = 20; }
        var copy = Ui.Action("Copier le lien", "link", "primary", async () =>
        {
            if (Clipboard is not { } clipboard) { notice("Copie impossible ici : sélectionne le lien."); return; }
            await clipboard.SetTextAsync(link);
            notice("Lien copié : envoie-le à ton ami.");
            Close();
        });
        var close = Ui.Action("Fermer", null, "quiet", Close);
        Content = new Border
        {
            Padding = new Thickness(32, 28),
            Child = Ui.Column(16,
                Ui.Text("Inviter quelqu’un", 26, FontWeight.Bold),
                new TextBlock { Text = $"Envoie ce lien à ton ami pour qu’il rejoigne « {group.GroupName} ».", FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap },
                field, steps,
                new DockPanel { Children = { close, copy } }),
        };
        DockPanel.SetDock(copy, Dock.Right); copy.HorizontalAlignment = HorizontalAlignment.Right; close.HorizontalAlignment = HorizontalAlignment.Left;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Opened += (_, _) => copy.Focus();
    }
}
