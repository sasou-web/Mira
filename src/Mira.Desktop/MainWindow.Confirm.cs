using System.Windows;
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
