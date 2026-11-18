using System.Windows;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// Mira back by itself after a restart, on the PC of Jellyfin (<see cref="SignInStart"/>): the Run key kept as the
/// setting and this PC say (read every minute with the PC's sessions, MainWindow.KeepAwake.cs), and a start at sign-in
/// by the clock: the window placed and shown only when asked for (its icon, the Start menu, a mira:// link).
/// </summary>
public partial class MainWindow
{
    /// <summary>Started by Windows when the person signed in: never in front, never in the taskbar until shown.</summary>
    private bool AtSignIn => _args.Contains(SignInStart.Flag);
    /// <summary>Where the window goes the first time it shows after a start at sign-in (<see cref="FitToScreen"/>).</summary>
    private ScreenFit.Placement? _placementLater;
    /// <summary>This copy is the one Windows starts at sign-in; null until judged.</summary>
    private bool? _signInWritten;

    private void KeepSignInStart(bool jellyfinHere)
    {
        // Checks, the demo and other profiles (--data) never touch the person's registry.
        if (_demo || App.ShellValidation || IsCheckRun || Environment.ProcessPath is not { } executable) return;
        var current = SignInLaunch.Current();
        var step = SignInStart.Plan(jellyfinHere, _settings.OpenAtSignIn, current, executable);
        if (step != SignInStart.Step.Keep) current = SignInLaunch.Apply(step, executable);
        var written = current == SignInStart.Command(executable);
        var changed = _signInWritten != written;
        _signInWritten = written;
        if (changed && SettingsOverlay.Visibility == Visibility.Visible) ShowSignInStart();
    }

    /// <summary>The row of Réglages → Serveur Jellyfin: only on the PC of the account's Jellyfin.</summary>
    private void ShowSignInStart()
    {
        var here = !_demo && !App.ShellValidation && _client is { } client && _awakeServer is { Local: true } known && known.Server == client.Connection.Server;
        SignInStartRow.Visibility = here ? Visibility.Visible : Visibility.Collapsed;
        if (!here) return;
        SignInStartCheck.IsChecked = _settings.OpenAtSignIn;
        SignInStartState.Text = SignInStart.State(_settings.OpenAtSignIn, _signInWritten == true, SignInLaunch.TurnedOffInWindows());
    }

    private void SignInStart_Click(object sender, RoutedEventArgs e)
    {
        _settings.OpenAtSignIn = SignInStartCheck.IsChecked == true; TrySaveSettings();
        KeepSignInStart(jellyfinHere: true); ShowSignInStart();
    }

    /// <summary>
    /// Before the first show after a start at sign-in: where the window was last left, in the taskbar again. True when it
    /// is to be maximized once shown (set before, Windows would show it at once, wherever it is).
    /// </summary>
    private bool PlaceAfterSignIn()
    {
        if (_placementLater is not { } placement) return false;
        _placementLater = null;
        ShowInTaskbar = true; ShowActivated = true;
        Screens.Move(new System.Windows.Interop.WindowInteropHelper(this).Handle, placement.Window);
        if (_settings.WindowMaximized) _lastVisibleState = WindowState.Maximized;
        return _settings.WindowMaximized;
    }
}
