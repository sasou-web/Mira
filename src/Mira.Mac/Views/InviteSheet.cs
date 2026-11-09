using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Inviter quelqu'un » in a group of « Regarder ensemble », a sheet over the window as on Windows: your party link, or
/// without Mira's extension the group's mira:// link, to send to a friend, with what the friend needs (an account of this
/// Jellyfin, Tailscale away from home). The same words as Windows (<see cref="Together.InviteSteps"/>).
/// </summary>
public static class InviteSheet
{
    private static readonly IBrush Warning = Ui.Brush("#D5B787");

    /// <param name="warnLast">The last step said in the warning colour: the link reaches only this Jellyfin's home or tailnet.</param>
    public static Task ShowAsync(MainWindow shell, string lead, string link, IReadOnlyList<string> list, bool warnLast = false)
    {
        var sheet = new Sheet(L("REGARDER ENSEMBLE", "WATCH TOGETHER"), "users", L("Inviter quelqu’un", "Invite someone"), lead);
        var field = new TextBox { Text = link, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeight.SemiBold, Padding = new Thickness(12, 10), Margin = new Thickness(0, 18, 0, 0) };
        AutomationProperties.SetName(field, L("Lien d’invitation", "Invite link"));
        var steps = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        for (var i = 0; i < list.Count; i++)
            steps.Children.Add(new TextBlock { Text = $"{i + 1}. {list[i]}", FontSize = 13, LineHeight = 19, Foreground = warnLast && i == list.Count - 1 ? Warning : Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        sheet.Body.Children.Add(field); sheet.Body.Children.Add(steps);
        // As on Windows, the sheet stays: the link may be sent twice, and « Fermer » or Escape closes it.
        var copy = Ui.Action(L("Copier le lien", "Copy link"), "link", "primary", async () =>
        {
            if (shell.Clipboard is not { } clipboard) { shell.Notice(L("Copie impossible ici : sélectionne le lien.", "Can’t copy here: select the link.")); return; }
            await clipboard.SetTextAsync(link);
            shell.Notice(L("Lien copié : envoie-le à ton ami.", "Link copied: send it to your friend."), tone: NoticeTone.Done);
        });
        var close = Ui.Action(L("Fermer", "Close"), null, "quiet", () => shell.CloseSheet(sheet));
        sheet.Buttons(copy, close);
        sheet.Body.Children[^1].Margin = new Thickness(0, 8, 0, 0);
        var done = new TaskCompletionSource();
        sheet.Closed += () => done.TrySetResult();
        shell.ShowSheet(sheet);
        return done.Task;
    }
}
