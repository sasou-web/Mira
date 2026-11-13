using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Mira.Mac.Views;

// A question answered by two buttons, on Mira's own card over the window, as Windows asks (MainWindow.Confirm.cs there):
// what cannot be seen undone at once, following a series that downloads on its own from then on.
public sealed partial class MainWindow
{
    private TaskCompletionSource<bool>? _confirm;

    /// <summary>
    /// True on the first button; false on the second, Échap or another sheet in its place. The focus starts on the safe
    /// answer, as on Windows: Return never confirms by surprise. Asked again while one is open, the new one is refused.
    /// </summary>
    public Task<bool> ConfirmAsync(string title, string note, string yes, string no)
    {
        if (_confirm is not null) return Task.FromResult(false);
        var asked = _confirm = new TaskCompletionSource<bool>();
        Sheet? sheet = null;
        void Answer(bool answer)
        {
            if (_confirm != asked) return;
            _confirm = null;
            if (sheet is not null) CloseSheet(sheet);
            asked.TrySetResult(answer);
        }
        var yesButton = Ui.Action(yes, null, "", () => Answer(true));
        var noButton = Ui.Action(no, null, "quiet", () => Answer(false));
        yesButton.Padding = new Thickness(22, 10);
        var heading = Ui.Heading(title, 24, 1);
        heading.TextWrapping = TextWrapping.Wrap;
        var card = new StackPanel
        {
            Margin = new Thickness(34, 30),
            Children =
            {
                heading,
                new TextBlock { Text = note, FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 22) },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { yesButton, noButton } },
            },
        };
        sheet = new Sheet(card, 480) { FirstFocus = noButton };
        sheet.Closed += () => Answer(false);
        ShowSheet(sheet);
        return asked.Task;
    }
    /// <summary>The question open, answered as if its button were clicked: for the self-check.</summary>
    internal bool AnswerConfirm(bool yes)
    {
        if (_confirm is null || CurrentSheet is not { } sheet) return false;
        var button = sheet.GetLogicalDescendants().OfType<Button>().ElementAtOrDefault(yes ? 0 : 1);
        if (button is null) return false;
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return true;
    }
    /// <summary>A question is open (<see cref="ConfirmAsync"/>).</summary>
    internal bool Asking => _confirm is not null;
}
