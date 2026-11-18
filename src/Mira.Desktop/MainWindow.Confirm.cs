using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private TaskCompletionSource<bool>? _confirm;
    private IInputElement? _confirmReturn;

    /// <summary>
    /// A question put on Mira's own card, over the page, in place of a Windows message box: true on the first button,
    /// false on the second, Esc or Back. Asking again while one is open refuses the new one.
    /// </summary>
    private Task<bool> ConfirmAsync(string title, string note, string yes, string no)
    {
        if (_confirm is not null) return Task.FromResult(false);
        ConfirmCard.Width = 480; ConfirmNote.Visibility = Visibility.Visible; ConfirmDetails.Visibility = Visibility.Collapsed;
        ConfirmYes.Style = (Style)FindResource("Primary"); ConfirmButtons.HorizontalAlignment = HorizontalAlignment.Left;
        PlaceConfirmButtons(yesFirst: true);
        return Ask(title, note, yes, no);
    }

    /// <summary>
    /// « Mettre à la Corbeille » asked on the same card, wider: what goes line by line (<paramref name="rows"/>: an icon,
    /// what, how much), where, and how it comes back. « Annuler » first and focused, the other in the failed state's ink.
    /// </summary>
    private Task<bool> ConfirmRecycleAsync(string title, string sub, IReadOnlyList<(string Icon, string Label, string Detail)> rows, string where, string info)
    {
        if (_confirm is not null) return Task.FromResult(false);
        ConfirmCard.Width = 580; ConfirmNote.Visibility = Visibility.Collapsed; ConfirmDetails.Visibility = Visibility.Visible;
        ConfirmSub.Text = sub; ConfirmPath.Text = where; ConfirmInfo.Text = info;
        ConfirmRows.Children.Clear();
        foreach (var (icon, label, detail) in rows)
        {
            if (ConfirmRows.Children.Count > 0) ConfirmRows.Children.Add(new Border { Height = 1, Background = Palette.Get("Rule") });
            var line = new DockPanel { Margin = new Thickness(0, 13, 0, 13) };
            var right = new TextBlock { Text = detail, FontSize = 13.5, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250 };
            DockPanel.SetDock(right, Dock.Right);
            line.Children.Add(right);
            line.Children.Add(new Icon { Kind = icon, Width = 18, Height = 18, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = label, FontSize = 14.5, Foreground = Palette.Get("Text"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            ConfirmRows.Children.Add(line);
        }
        ConfirmYes.Style = (Style)FindResource("DangerButton"); ConfirmButtons.HorizontalAlignment = HorizontalAlignment.Right;
        PlaceConfirmButtons(yesFirst: false);
        return Ask(title, "", ActionLabel("trash", L("Mettre à la Corbeille", "Move to Recycle Bin")), L("Annuler", "Cancel"));
    }

    private void PlaceConfirmButtons(bool yesFirst)
    {
        ConfirmButtons.Children.Clear();
        ConfirmButtons.Children.Add(yesFirst ? ConfirmYes : ConfirmNo); ConfirmButtons.Children.Add(yesFirst ? ConfirmNo : ConfirmYes);
        ConfirmYes.Margin = new Thickness(yesFirst ? 0 : 8, 0, 0, 0); ConfirmNo.Margin = new Thickness(yesFirst ? 8 : 0, 0, 0, 0);
        // « Annuler » of the Recycle Bin a button of its own, as solid as the other: never a bare word beside it.
        ConfirmNo.Style = (Style)FindResource(yesFirst ? "Quiet" : typeof(Button));
    }

    private Task<bool> Ask(string title, string note, object yes, string no)
    {
        _confirm = new TaskCompletionSource<bool>();
        _confirmReturn = Keyboard.FocusedElement;
        ConfirmTitle.Text = title; ConfirmNote.Text = note; ConfirmYes.Content = yes; ConfirmNo.Content = no;
        Motion.Reveal(ConfirmOverlay, 260, 0); Motion.Reveal(ConfirmCard, 360, 16);
        // The safe answer is the one the focus starts on: Enter never confirms by surprise.
        _ = Dispatcher.BeginInvoke(() => ConfirmNo.Focus(), DispatcherPriority.Input);
        return _confirm.Task;
    }
    private void Answer(bool yes)
    {
        if (_confirm is not { } asked) return;
        _confirm = null;
        _ = Motion.HideAsync(ConfirmOverlay);
        if (_confirmReturn is UIElement { IsVisible: true } back) back.Focus();
        _confirmReturn = null;
        asked.SetResult(yes);
    }
    private void ConfirmYes_Click(object sender, RoutedEventArgs e) => Answer(true);
    private void ConfirmNo_Click(object sender, RoutedEventArgs e) => Answer(false);
}
