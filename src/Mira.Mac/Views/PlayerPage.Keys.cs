using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// The player's keys, as on Windows: read before the control that has the focus (MainWindow), the jump just made said
// in the middle of the picture. ⌘, ⌃ and ⌥ stay the Mac's, but ⌘F and ⌃⌘F (full screen) and ⌘[ (the mini-player).
public sealed partial class PlayerPage
{
    /// <summary>⇧← and ⇧→: a minute, as on Windows.</summary>
    private const double LongJump = 60;
    /// <summary>
    /// The keys down: Avalonia does not say which press is the key repeating, and Space, M, F, C or a digit held would
    /// undo itself ten times a second. Seeks and the volume repeat, as on Windows.
    /// </summary>
    private readonly HashSet<Key> _held = [];
    /// <summary>
    /// The keys the player took on their way down: their release is taken too. Avalonia presses a focused button when
    /// Space is released (Button.OnKeyUp), and Lecture, focused by its click under the player, started the film over.
    /// </summary>
    private readonly HashSet<Key> _taken = [];
    private readonly TextBlock _seekText = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
    private Border? _seekPill;
    private DispatcherTimer? _seekHide;
    private double _seekTotal;

    public void HandleKey(KeyEventArgs e)
    {
        if (e.Handled) return;
        ReadKey(e);
        if (e.Handled) _taken.Add(e.Key);
    }
    private void ReadKey(KeyEventArgs e)
    {
        var again = !_held.Add(e.Key);
        // « Lecture impossible »: Échap is « Retour »; Return and Space press the button that has the focus.
        if (FailureShown) { if (e.Key == Key.Escape && !again) { _ = LeaveFailureAsync(); e.Handled = true; } return; }
        if (e.Source is TextBox) return;
        var modifiers = e.KeyModifiers;
        if (e.Key == Key.F && modifiers is KeyModifiers.Meta or (KeyModifiers.Meta | KeyModifiers.Control) && !_mini) { if (!again) ToggleFullscreen(); e.Handled = true; return; }
        // In the mini-player, Échap and ⌘[ are the library's.
        if (e.Key == Key.OemOpenBrackets && modifiers == KeyModifiers.Meta && !_mini) { SetMini(true); e.Handled = true; return; }
        if (e.Key is Key.Left or Key.Right && modifiers == KeyModifiers.Shift) { Jump(e.Key == Key.Left ? -LongJump : LongJump); e.Handled = true; return; }
        if (modifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Space or Key.K: if (!again) TogglePause(); break;
            // J, K and L as on video sites.
            case Key.Left or Key.J: Jump(-10); break;
            case Key.Right or Key.L: Jump(10); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: if (!again) ToggleMute(); break;
            case Key.C: if (!again) ToggleSubtitles(); break;
            case Key.F: if (!again) ToggleFullscreen(); break;
            // 0 to 9 (top row or keypad): 0 to 90 % of the title.
            case >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9: if (!again) SeekToTenth(e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0); break;
            case Key.S: if (_skips.Active is null) return; if (!again) Skip(); break;
            case Key.N: if (_skips.Next is not { } next) return; if (!again) _ = PlayNextAsync(next); break;
            case Key.P: if (!_previous.IsVisible || !_previous.IsEnabled) return; if (!again) PlayPrevious(); break;
            case Key.E: if (!again) ToggleEpisodes(); break;
            case Key.I: if (!again) SetMini(!_mini); break;
            case Key.Escape when !_mini:
                if (again) break;
                // In couch mode, as on a TV: back to the title, the playback stopped.
                if (_shell.InCouchMode) _ = _shell.StopPlaybackAsync();
                else if (_shell.WindowState == WindowState.FullScreen) ToggleFullscreen(); else SetMini(true);
                break;
            case Key.Back when !_mini && _shell.InCouchMode: if (!again) _ = _shell.StopPlaybackAsync(); break;
            default: return;
        }
        e.Handled = true;
    }
    public void HandleKeyUp(KeyEventArgs e)
    {
        _held.Remove(e.Key);
        if (_taken.Remove(e.Key)) e.Handled = true;
    }
    /// <summary>Mira left the front with a key down: its release went to another app.</summary>
    public void ForgetHeldKeys() { _held.Clear(); _taken.Clear(); }

    /// <summary>← and →, J and L, ⇧← and ⇧→: the jump, and how far it went in the middle of the picture.</summary>
    private void Jump(double seconds)
    {
        if (_mpv is null || !_loaded) return;
        SeekBy(seconds); ShowSeekPill(seconds);
    }
    /// <summary>0 to 9: that tenth of the title (0 = the start), to the frame.</summary>
    private void SeekToTenth(int tenth)
    {
        if (_mpv is null || !_loaded || _session.Duration <= 0) return;
        var target = Math.Min(_session.Duration * tenth / 10, Math.Max(0, _session.Duration - .5));
        if (!_mpv.TryCommand("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact")) return;
        Hold(target); ShowSeekPill($"{tenth * 10} %"); ShowControls();
    }

    /// <summary>
    /// A short pill in the middle of the picture, as on Windows: the jump (« +10 s », adding up while the key repeats),
    /// the tenth, the subtitles switched with C. Never in the mini-player.
    /// </summary>
    private void ShowSeekPill(string text)
    {
        if (_mini || FailureShown) return;
        if (_seekPill is null)
        {
            AutomationProperties.SetLiveSetting(_seekText, AutomationLiveSetting.Polite);
            _seekPill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(18, 10), CornerRadius = new CornerRadius(20),
                Background = Ui.Brush("#E0161616"), BorderBrush = Ui.Brush("#30FFFFFF"), BorderThickness = new Thickness(1),
                IsHitTestVisible = false, IsVisible = false, Opacity = 0, Child = _seekText,
                Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }]
            };
            _root.Children.Add(_seekPill);
            _seekHide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _seekHide.Tick += (_, _) => HideSeekPill();
        }
        _seekText.Text = text;
        HidePlayState();
        _seekPill.IsVisible = true; _seekPill.Opacity = 1;
        _seekHide!.Stop(); _seekHide.Start();
    }
    /// <summary>« ±10 s »: the sum of the jumps made within the same second, one way.</summary>
    private void ShowSeekPill(double seconds)
    {
        if (_seekHide is not { IsEnabled: true } || Math.Sign(_seekTotal) != Math.Sign(seconds)) _seekTotal = 0;
        _seekTotal += seconds;
        ShowSeekPill((_seekTotal < 0 ? "−" : "+") + Math.Abs(_seekTotal).ToString("0", CultureInfo.InvariantCulture) + " s");
    }
    private void HideSeekPill()
    {
        _seekHide?.Stop(); _seekTotal = 0;
        if (_seekPill is null) return;
        _seekPill.IsVisible = false; _seekPill.Opacity = 0;
    }
    /// <summary>The title's length, as mpv reads it: for the self-check.</summary>
    public double Duration => _session.Duration;
    /// <summary>What the pill says, while it shows: for the self-check.</summary>
    public string? SeekPillText => _seekPill is { IsVisible: true } ? _seekText.Text : null;
}
