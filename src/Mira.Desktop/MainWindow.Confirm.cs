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
        if (_confirm is not null || _card is not null) return Task.FromResult(false);
        ConfirmCard.Width = 480; ConfirmNote.Visibility = Visibility.Visible; ConfirmDetails.Visibility = Visibility.Collapsed; PlainCard();
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
        if (_confirm is not null || _card is not null) return Task.FromResult(false);
        ConfirmCard.Width = 580; ConfirmNote.Visibility = Visibility.Collapsed; ConfirmDetails.Visibility = Visibility.Visible; PlainCard();
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
        ConfirmButtons.Children.Clear(); ConfirmButtons.Orientation = Orientation.Horizontal;
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
        if (_card is not null) { CloseCard(-1); return; }
        if (_confirm is not { } asked) return;
        _confirm = null;
        _ = Motion.HideAsync(ConfirmOverlay);
        if (_confirmReturn is UIElement { IsVisible: true } back) back.Focus();
        _confirmReturn = null;
        asked.SetResult(yes);
    }
    private void ConfirmYes_Click(object sender, RoutedEventArgs e) => Answer(true);

    // ---- A card of its own: choices or a password, and up to three buttons ----

    private TaskCompletionSource<int>? _card;
    private Func<int, Task<bool>>? _cardAct;
    private bool _cardBusy;

    /// <summary>The card as the questions above have it: no step, no content of its own.</summary>
    private void PlainCard()
    {
        ConfirmStep.Visibility = Visibility.Collapsed; ConfirmBody.Content = null; ConfirmBody.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The same card with its own content (<paramref name="body"/>) and its buttons, from left to right: the answer of the
    /// one pressed, or -1 for Esc or Back. <paramref name="act"/>, when given, runs with the card still open (its buttons
    /// off meanwhile): false keeps it open (a wrong password said in the body). <paramref name="step"/> and
    /// <paramref name="progress"/>: « Tout régler… »'s step above the title.
    /// </summary>
    private Task<int> CardAsync(string title, string note, UIElement? body, IReadOnlyList<(object Label, string Style, int Answer)> buttons,
        Func<int, Task<bool>>? act = null, string? step = null, double progress = 0, double width = 520)
    {
        if (_confirm is not null || _card is not null) return Task.FromResult(-1);
        ConfirmCard.Width = width; ConfirmDetails.Visibility = Visibility.Collapsed;
        ConfirmNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmTitle.Text = title; ConfirmNote.Text = note;
        ConfirmStep.Visibility = step is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmStepText.Text = step ?? "";
        ConfirmStepBar.Width = Math.Clamp(progress, 0, 1) * (width - ConfirmCard.Padding.Left - ConfirmCard.Padding.Right);
        ConfirmBody.Content = body; ConfirmBody.Visibility = body is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButtons.Children.Clear(); ConfirmButtons.HorizontalAlignment = HorizontalAlignment.Stretch; ConfirmButtons.Orientation = Orientation.Vertical;
        // The first button on the left when there are three (« Choose a password instead »), the others on the right.
        var row = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal }; var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right); row.Children.Add(left); row.Children.Add(right);
        Button? safe = null;
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, style, answer) = buttons[i];
            var button = new Button { Content = label, Style = (Style)FindResource(style), Padding = new Thickness(18, 9, 18, 9) };
            Motion.SetRadius(button, (CornerRadius)FindResource("RadiusControl"));
            button.Click += (_, _) => _ = PickAsync(answer);
            var side = buttons.Count > 2 && i == 0 ? left : right;
            button.Margin = new Thickness(side.Children.Count == 0 ? 0 : 8, 0, 0, 0);
            side.Children.Add(button);
            if (style != "Primary") safe ??= button;
        }
        ConfirmButtons.Children.Add(row);
        _card = new TaskCompletionSource<int>(); _cardAct = act; _cardBusy = false;
        _confirmReturn = Keyboard.FocusedElement;
        Motion.Reveal(ConfirmOverlay, 260, 0); Motion.Reveal(ConfirmCard, 360, 16);
        // A field of the content first; else the safe answer: Enter never confirms by surprise.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (body is not null && FirstField(body) is { } field) field.Focus();
            else (safe ?? right.Children.OfType<Button>().FirstOrDefault())?.Focus();
        }, DispatcherPriority.Input);
        return _card.Task;
    }

    private static Control? FirstField(DependencyObject root)
    {
        if ((root is System.Windows.Controls.PasswordBox or TextBox || root is RadioButton { IsChecked: true }) && root is Control { IsVisible: true, IsEnabled: true } field) return field;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FirstField(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private async Task PickAsync(int answer)
    {
        if (_card is null || _cardBusy) return;
        if (_cardAct is { } act && answer >= 0)
        {
            _cardBusy = true; ConfirmButtons.IsEnabled = false;
            bool done;
            try { done = await act(answer); }
            finally { _cardBusy = false; ConfirmButtons.IsEnabled = true; }
            if (!done) return;
        }
        CloseCard(answer);
    }

    private void CloseCard(int answer)
    {
        if (_card is not { } asked || _cardBusy) return;
        _card = null; _cardAct = null;
        _ = Motion.HideAsync(ConfirmOverlay);
        if (_confirmReturn is UIElement { IsVisible: true } back) back.Focus();
        _confirmReturn = null;
        asked.SetResult(answer);
    }
    private void ConfirmNo_Click(object sender, RoutedEventArgs e) => Answer(false);
}
