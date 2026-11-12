using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Mira.Core;
using Mira.Desktop.Playback;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// « Image dans l’image »: the video out of Mira's window into a small one above every other (<see cref="PipWindow"/>),
/// from the player or the mini-player. Mira's window goes back to the library as with the mini-player (_miniPlayer stays
/// true), the mini-player's place left empty. The video's native window is moved there and back, mpv going on as it was:
/// nothing reloads, the next episode plays there too, and the subtitles are mpv's own, raised to stay readable at that
/// size (<see cref="PictureInPicture.SubtitleScale"/>) and above the controls while they show. Back to Mira by its button,
/// a double click or I; « Fermer » stops.
/// </summary>
public partial class MainWindow
{
    private PipWindow? _pip;
    /// <summary>The engine whose subtitles were sized for the small window: a new title brings a new engine, sized again.</summary>
    private MpvEngine? _pipEngine;
    private double _pipSubtitleScale = 1;
    /// <summary>That engine's own sub-margin-y, put back when the controls go and when the video leaves.</summary>
    private int _pipMargin = 22;
    /// <summary>The sub-pos set while the controls show (<see cref="PictureInPicture.SubtitleLift"/>); null: the subtitles at their own place.</summary>
    private int? _pipLift;
    private long _pipTracksAt;
    private bool? _pipSubtitles;

    private void Pip_Click(object sender, RoutedEventArgs e) => EnterPip();

    /// <summary>The video out into its own window, the library back in this one.</summary>
    private void EnterPip()
    {
        if (!_playing || _pip is not null || _videoHost is not { } video || PlayerErrorShown || _morphing || _videoWaits || !VideoContainer.Children.Contains(video)) return;
        if (_fullscreen) ToggleFullscreen();
        ClosePlayerPopups(); ClosePreview();
        // The mini-player sliding: its video placed by layout again before it changes window.
        if (_miniSliding) { CancelMiniSlide(); FinishMiniSlide(); }
        var aspect = VideoAspect() ?? 16d / 9;
        var remembered = _settings.PipX is { } x && _settings.PipY is { } y && _settings.PipWidth > 0 ? new PictureInPicture.Spot(x, y, _settings.PipWidth) : (PictureInPicture.Spot?)null;
        if (PictureInPicture.Open(Screens.All(), remembered, PipWindow.Middle(new WindowInteropHelper(this).Handle), aspect) is not { } at) return;
        var pip = new PipWindow(at) { Aspect = aspect };
        pip.Back += ReturnFromPip;
        pip.Stop += StopFromPip;
        pip.PlayPause += () => Pause_Click(this, new());
        pip.Seek += seconds => SeekRelative(seconds);
        pip.Subtitles += () => { ToggleSubtitles(); _pipTracksAt = 0; UpdatePipSubtitles(); };
        pip.Key += PipKey;
        pip.Placed += () => RememberPip(pip);
        pip.Resized += SizePipSubtitles;
        pip.ControlsChanged += () => LiftPipSubtitles();
        _pip = pip; _pipEngine = null; _pipSubtitles = null; _pipTracksAt = 0;
        try
        {
            pip.Show();
            // Out of this window, then into the small one: WPF moves the native window from one to the other.
            VideoContainer.Children.Remove(video); video.SetCornerRadius(0); pip.Hold(video);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Never a video lost between two windows: back where it was, and the player as it was.
            _pip = null; pip.Release(video);
            if (!VideoContainer.Children.Contains(video)) VideoContainer.Children.Add(video);
            pip.Dismiss(); ErrorLog.Append(AppFiles.ProfileDirectory, ex);
            return;
        }
        // The library comes back as with the mini-player; the next tick fills in the small window.
        _miniTucked = false;
        SetMiniPlayer(true);
        UpdatePip();
        FrameProbe.Note("picture in picture opened");
    }
    /// <summary>The player's own surface hidden: the video plays in the small window, the library is alone in this one.</summary>
    private void HideForPip()
    {
        PlayerShell.Visibility = MiniFrame.Visibility = Visibility.Collapsed;
        _videoHost?.SetCornerRadius(0);
    }
    /// <summary>
    /// The video back into this window and the small one closed, its place kept; the player's surface shown again when
    /// <paramref name="show"/> (the whole player), or left hidden (playback stopped, another title, Mira closing).
    /// </summary>
    private void EndPip(bool show)
    {
        if (_pip is not { } pip) return;
        _pip = null;
        Attempt(() => RememberPip(pip));
        if (_videoHost is { } video)
        {
            // Taken back before the small window closes, which would destroy it with mpv's own window inside.
            pip.Release(video);
            if (!VideoContainer.Children.Contains(video)) VideoContainer.Children.Add(video);
        }
        if (_mpv is { } mpv && ReferenceEquals(mpv, _pipEngine))
        {
            if (_pipSubtitleScale != 1) mpv.Set("sub-scale", "1");
            if (_pipLift is not null) LowerPipSubtitles(mpv);
        }
        _pipEngine = null; _pipSubtitleScale = 1; _pipLift = null;
        pip.Dismiss();
        if (show) PlayerShell.Visibility = Visibility.Visible;
    }
    /// <summary>« Revenir dans Mira »: the whole player in Mira's window, brought to the front.</summary>
    private void ReturnFromPip()
    {
        if (_pip is null) return;
        if (WindowState == WindowState.Minimized) WindowState = _lastVisibleState;
        SetMiniPlayer(false);
        Activate(); PlayerSurface.Focus();
    }
    /// <summary>« Fermer »: the small window gone at once, the playback stopped and the library refreshed as from the mini-player.</summary>
    private void StopFromPip()
    {
        if (_pip is not { } pip) return;
        pip.Hide();
        StopPlayer_Click(this, new());
    }
    private void RememberPip(PipWindow pip)
    {
        if (ValidationRun || PictureInPicture.ScreenOf(pip.Bounds(), Screens.All()) is not { } screen) return;
        var spot = PictureInPicture.Remember(pip.Bounds(), screen);
        (_settings.PipX, _settings.PipY, _settings.PipWidth) = (spot.X, spot.Y, spot.Width);
    }
    /// <summary>The picture's shape as mpv shows it (with its pixel aspect); null until it knows (a stream opening again included).</summary>
    private double? VideoAspect() =>
        _mpv is { } mpv && mpv.Number("dwidth") is > 0 and var width && mpv.Number("dheight") is > 0 and var height ? PictureInPicture.Aspect(width, height) : null;

    /// <summary>Each tick of the player: the small window's title, state, time and shape, and the subtitles sized for it.</summary>
    private void UpdatePip()
    {
        if (_pip is not { } pip || _mpv is not { } mpv) return;
        if (_session.Item is { } item) pip.SetTitle(item.DisplayTitle);
        pip.SetPaused(_session.Paused);
        pip.SetTime($"{TimeLabel(_session.Position)} / {TimeLabel(_session.Duration)}", _session.Duration > 0 ? _session.Position / _session.Duration : 0);
        if (_loaded && VideoAspect() is { } aspect && Math.Abs(aspect - pip.Aspect) > .01) { pip.Aspect = aspect; pip.Refit(); }
        if (!ReferenceEquals(mpv, _pipEngine))
        {
            _pipEngine = mpv; _pipSubtitleScale = 1; _pipTracksAt = 0;
            _pipMargin = mpv.Number("sub-margin-y", 22) is var margin and >= 0 and <= 600 ? (int)margin : 22; _pipLift = null;
            SizePipSubtitles();
        }
        UpdatePipSubtitles();
    }
    /// <summary>
    /// mpv sizes subtitles to the window's height: in the small window, raised to stay readable (never lowered); set
    /// again at each new size and each new engine, back to mpv's own size in Mira's window.
    /// </summary>
    private void SizePipSubtitles()
    {
        if (_pip is not { } pip || _mpv is not { } mpv || !ReferenceEquals(mpv, _pipEngine)) return;
        var scale = PictureInPicture.SubtitleScale(_settings.SubtitleSize, pip.VideoHeight);
        if (scale != _pipSubtitleScale && mpv.Set("sub-scale", scale.ToString(CultureInfo.InvariantCulture))) _pipSubtitleScale = scale;
        LiftPipSubtitles();
    }
    /// <summary>
    /// While the controls show, the subtitles go above the time and the subtitles' button, as on video sites
    /// (<see cref="PictureInPicture.SubtitleLift"/>); back to their place once the controls go. <paramref name="again"/>:
    /// set again after Réglages restyled the subtitles (their own height, maybe changed, put back over the lift).
    /// </summary>
    private void LiftPipSubtitles(bool again = false)
    {
        if (_pip is not { } pip || _mpv is not { } mpv || !ReferenceEquals(mpv, _pipEngine)) return;
        var lift = pip.ControlsShown ? PictureInPicture.SubtitleLift(pip.Fit.Lift, pip.VideoHeight, SubtitleStyle.Height(_settings.SubtitleHeight)) : null;
        if (lift == _pipLift && !again) return;
        if (lift is { } position) { mpv.Set("sub-margin-y", "0"); mpv.Set("sub-pos", position.ToString(CultureInfo.InvariantCulture)); }
        else LowerPipSubtitles(mpv);
        _pipLift = lift;
    }
    /// <summary>The subtitles at their own place: the engine's sub-margin-y, the height chosen in Réglages.</summary>
    private void LowerPipSubtitles(MpvEngine mpv)
    {
        mpv.Set("sub-margin-y", _pipMargin.ToString(CultureInfo.InvariantCulture));
        mpv.Set("sub-pos", SubtitleStyle.Height(_settings.SubtitleHeight).ToString(CultureInfo.InvariantCulture));
    }
    /// <summary>The subtitles' button: shown with a track to show, lit while one shows. The track list is read at most once a second.</summary>
    private void UpdatePipSubtitles()
    {
        if (_pip is not { } pip || _mpv is not { } mpv || !_loaded) return;
        var sid = mpv.Get("sid");
        var shown = sid is not (null or "no" or "" or "false") || _converted?.Stream.Burned is not null;
        if (!shown && Environment.TickCount64 - _pipTracksAt > 1000)
        {
            _pipTracksAt = Environment.TickCount64;
            _pipSubtitles = _converted is { } converted ? converted.Subtitles.Any() : mpv.Tracks().Any(t => t.Type == "sub");
        }
        pip.SetSubtitles(shown ? true : _pipSubtitles == true ? false : null);
    }
    /// <summary>The player's keys while the small window is in front: Space or K, J and L or the arrows, M, C, N, and I back to Mira.</summary>
    private void PipKey(KeyEventArgs e)
    {
        if (e.Handled || !_playing || _pip is null) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key is Key.Left or Key.Right) { SeekRelative(e.Key == Key.Left ? -LongJump : LongJump); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Space or Key.K: if (!e.IsRepeat) Pause_Click(this, new()); break;
            case Key.Left or Key.J: SeekRelative(-10); break;
            case Key.Right or Key.L: SeekRelative(10); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: if (!e.IsRepeat) Mute_Click(this, new()); break;
            case Key.C: if (!e.IsRepeat) { ToggleSubtitles(); _pipTracksAt = 0; UpdatePipSubtitles(); } break;
            case Key.N: if (!NextButton.IsEnabled) return; if (!e.IsRepeat) Next_Click(this, new()); break;
            case Key.I: if (!e.IsRepeat) ReturnFromPip(); break;
            default: return;
        }
        e.Handled = true;
    }
}
