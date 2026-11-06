using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « Lecture impossible », as on Windows and Mira web: in the player, its own screen says why, with « Retour » and another
// try (or what helps, Tailscale's sign-in); in the mini-player or the small window, the person is browsing: the library
// stays, with a notice that waits to be closed.
public sealed partial class PlayerPage
{
    private Grid? _failure;
    private readonly TextBlock _failureTitle = new() { FontSize = 14, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _failureText = new() { FontSize = 14, LineHeight = 21, Foreground = Ui.Brush("#C5C5CA"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Button _failureBack = new() { Content = L("Retour", "Back"), Padding = new Thickness(18, 10) };
    private readonly Button _failureAction = new() { Classes = { "primary" }, Padding = new Thickness(22, 10) };
    private Action? _failureAct;

    /// <summary>The screen of a title that could not play, while it shows.</summary>
    public bool FailureShown => _failure is { IsVisible: true };
    /// <summary>What it says: for the self-check.</summary>
    public string? FailureText => FailureShown ? _failureText.Text : null;

    /// <summary>
    /// <paramref name="item"/> could not play: why, and another try of it (or <paramref name="action"/>, what helps;
    /// nothing without <paramref name="retry"/>). The playback is stopped first; the group carries on without this Mac.
    /// </summary>
    private async Task FailAsync(string message, MediaItem? item, string? action = null, Action? act = null, bool retry = true)
    {
        await StopAsync();
        var retries = retry && act is null && item is not null;
        if (retries) { action = L("Réessayer", "Try again"); act = () => _ = RetryAsync(item!); }
        if (_mini || _pip is not null || _shell.Player != this)
        {
            await _shell.CloseOverlayPlayerAsync(this);
            _shell.Notice(message, action, act, sticky: true, tone: NoticeTone.Error);
            return;
        }
        Group.SetAway(true);
        _tracks.Flyout?.Hide(); _more.Flyout?.Hide(); _episodesButton.Flyout?.Hide();
        if (_shell.WindowState == WindowState.FullScreen && !_shell.InCouchMode) { _shell.WindowState = WindowState.Normal; _fullscreenIcon.Kind = "fullscreen"; }
        // The bars under it: neither VoiceOver nor Tab reach them.
        _chrome.IsVisible = _skipHost.IsVisible = _groupPill.IsVisible = false;
        _hide.Stop(); _spinner.IsVisible = false; ShowStatus(null); HideSeekPill(); HidePlayState(); Cursor = Cursor.Default;
        EnsureFailure();
        _failureTitle.Text = item?.DisplayTitle ?? ""; _failureTitle.IsVisible = _failureTitle.Text.Length > 0;
        _failureText.Text = message; _failureAct = act;
        _failureAction.Content = action; _failureAction.IsVisible = act is not null; _failureAction.IsEnabled = true;
        AutomationProperties.SetName(_failureAction, action);
        // Above all the player has, the pills made since included.
        _root.Children.Remove(_failure!); _root.Children.Add(_failure!);
        _failure!.IsVisible = true; _failure.Opacity = 1;
        // The keys go to its main button, shown as the main one, as the sheets do: its ring comes with Tab, never on its own.
        Dispatcher.UIThread.Post(() => (act is null ? _failureBack : _failureAction).Focus(), DispatcherPriority.Input);
    }
    private void EnsureFailure()
    {
        if (_failure is not null) return;
        AutomationProperties.SetLiveSetting(_failureText, AutomationLiveSetting.Assertive);
        ToolTip.SetTip(_failureBack, L("Revenir à la bibliothèque · Échap", "Back to the library · Esc"));
        AutomationProperties.SetName(_failureBack, L("Revenir à la bibliothèque", "Back to the library"));
        _failureBack.Click += (_, _) => _ = LeaveFailureAsync();
        _failureAction.Click += (_, _) => _failureAct?.Invoke();
        var buttons = Ui.Row(10, _failureBack, _failureAction);
        buttons.HorizontalAlignment = HorizontalAlignment.Center; buttons.Margin = new Thickness(0, 26, 0, 0);
        var body = new StackPanel
        {
            MaxWidth = 540, Margin = new Thickness(40), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Icon("warning", 40) { Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = L("Lecture impossible", "Can’t play this"), FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) },
                _failureTitle, _failureText, buttons
            }
        };
        // The picture's last frame and the controls stay under it: nothing of them shows nor takes a click.
        _failure = new Grid
        {
            Background = Ui.Brush("#070708"), IsVisible = false, Opacity = 0, Children = { body },
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }]
        };
        KeyboardNavigation.SetTabNavigation(_failure, KeyboardNavigationMode.Cycle);
        _root.Children.Add(_failure);
    }
    private void HideFailure()
    {
        if (_failure is null) return;
        _failure.IsVisible = false; _failure.Opacity = 0; _failureAct = null;
        _chrome.IsVisible = _skipHost.IsVisible = true; ShowGroup();
    }
    /// <summary>« Retour » or Échap: the library, as it was.</summary>
    private async Task LeaveFailureAsync()
    {
        HideFailure();
        await _shell.CloseOverlayPlayerAsync(this);
    }
    /// <summary>
    /// Another try in this same player: the screen stays, « Nouvel essai… », until the picture comes or the title fails
    /// again (which says why once more). In a group, the group puts it on again.
    /// </summary>
    private async Task RetryAsync(MediaItem item)
    {
        if (Group.Active || _shell.Player != this) { HideFailure(); await _shell.PlayAsync(item); return; }
        _failureAction.IsEnabled = false; _failureText.Text = L("Nouvel essai…", "Trying again…");
        if (!await StartAsync(item)) await LeaveFailureAsync();
    }
    /// <summary>« Réessayer » of the screen: for the self-check.</summary>
    public void RetryForCheck() { if (FailureShown && _failureAction.IsEnabled) _failureAct?.Invoke(); }
}
