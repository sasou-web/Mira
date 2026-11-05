using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Mira.Core;
using Mira.Mac.Playback;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « Image dans l’image », as on Windows (MainWindow.PictureInPicture.cs): the video out of Mira's window into a small one
// above every other (PipWindow), from the player or the mini-player. Mira's window goes back to the library as with the
// mini-player (_mini stays true), the player's own place hidden. mpv plays on as it was, nothing reloads, the next episode
// plays there too, and the subtitles are mpv's own, raised to stay readable at that size (PictureInPicture.SubtitleScale).
// mpv's view cannot change windows with its OpenGL context: it is let go and made again in the other window, the video
// track off meanwhile and the film paused for that moment, as the repair of a grey picture does (PlayerPage.Picture.cs).
public sealed partial class PlayerPage
{
    private PipWindow? _pip;
    /// <summary>The view on its way from one window to the other: nothing else moves it meanwhile.</summary>
    private bool _pipMoving;
    /// <summary>The engine whose subtitles were sized for the small window: a new title brings a new engine, sized again.</summary>
    private MpvPlayer? _pipEngine;
    private double _pipSubtitleScale = 1;
    /// <summary>That engine's own sub-margin-y, put back when the controls go and when the video leaves.</summary>
    private int _pipMargin = 22;
    /// <summary>The sub-pos set while the controls show (<see cref="PictureInPicture.SubtitleLift"/>); null: the subtitles at their own place.</summary>
    private int? _pipLift;
    private long _pipTracksAt;
    private bool? _pipSubtitles;
    /// <summary>The subtitles shown before C or the small window's button hid them, shown again by the next press.</summary>
    private string? _subtitlesBeforeOff;
    private int? _convertedBeforeOff;
    /// <summary>The small window while it is open (self-check).</summary>
    public PipWindow? Pip => _pip;
    /// <summary>Where mpv's view goes: the small window while it is open.</summary>
    private Panel VideoPanel => _pip?.VideoHost ?? _videoHost;

    /// <summary>The video out into its own window, the library back in Mira's.</summary>
    public async Task EnterPipAsync()
    {
        if (!CanMovePicture()) return;
        if (_mpv!.Get("vid") is not { Length: > 0 } and not "no")
        { _shell.Notice(L("Ce titre n’a pas d’image à mettre dans sa propre fenêtre.", "This title has no picture to put in a window of its own.")); return; }
        // Out of macOS's full screen first: the small window shows on the desktop Mira's window goes back to.
        if (_shell.WindowState == WindowState.FullScreen) { ToggleFullscreen(); await Task.Delay(800); if (!CanMovePicture()) return; }
        var mpv = _mpv!;
        foreach (var button in new[] { _tracks, _more, _episodesButton }) (button.Flyout as Flyout)?.Hide();
        var screens = PipWindow.ScreensOf(_shell);
        var aspect = VideoAspect(mpv) ?? 16d / 9;
        var settings = _shell.Settings;
        var remembered = settings.PipX is { } x && settings.PipY is { } y && settings.PipWidth > 0 ? new PictureInPicture.Spot(x, y, settings.PipWidth) : (PictureInPicture.Spot?)null;
        var middle = _shell.PointToScreen(new Point(_shell.Bounds.Width / 2, _shell.Bounds.Height / 2));
        if (PictureInPicture.Open(screens, remembered, (middle.X, middle.Y), aspect) is not { } at || PictureInPicture.ScreenOf(at, screens) is not { } screen) return;

        var pip = new PipWindow(at, screen.Scale, aspect);
        pip.Place(at, screen);
        pip.Back += () => _ = ReturnFromPipAsync();
        pip.Stop += () => _ = _shell.StopPlaybackAsync();
        pip.PlayPause += TogglePause;
        pip.Seek += SeekBy;
        pip.Subtitles += () => { ToggleSubtitles(); _pipTracksAt = 0; UpdatePipSubtitles(); };
        pip.KeyPressed += PipKey;
        pip.Placed += () => RememberPip(pip);
        pip.VideoResized += SizePipSubtitles;
        pip.ControlsChanged += LiftPipSubtitles;
        _pip = pip; _pipEngine = null; _pipSubtitles = null; _pipTracksAt = 0;
        _pipMoving = true;
        bool moved;
        try
        {
            pip.Show();
            // The library comes back as with the mini-player, the player's place left empty.
            SetMini(true); IsVisible = false;
            moved = await MoveVideoAsync(mpv, pip.VideoHost);
        }
        finally { _pipMoving = false; }
        if (moved) { UpdatePip(); return; }
        // Never a video lost between two windows: it stayed in (or went back to) Mira's, the mini-player shows it.
        _pip = null; IsVisible = true;
        if (_surface?.View.Parent == pip.VideoHost && _surface is { } left) await DetachAsync(left);
        pip.Dismiss();
        if (_mpv == mpv && !_stopped) _shell.Notice(L("L’image dans l’image n’a pas pu s’ouvrir : la vidéo reste dans Mira.", "Picture in picture couldn’t open: the video stays in Mira."));
    }
    /// <summary>« Revenir dans Mira », a double click on it, or I: the whole player in Mira's window, brought to the front.</summary>
    public async Task ReturnFromPipAsync()
    {
        if (_pip is not { } pip || _pipMoving) return;
        if (_mpv is not { } mpv || _stopped) { ClosePip(); return; }
        _pipMoving = true;
        bool moved;
        try
        {
            IsVisible = true;
            SetMini(false);
            _shell.Activate();
            moved = await MoveVideoAsync(mpv, _videoHost);
        }
        finally { _pipMoving = false; }
        if (moved || _surface?.View.Parent != pip.VideoHost) { ClosePip(); return; }
        // Mira's window could not draw it: it stays in the small window.
        SetMini(true); IsVisible = false;
    }
    /// <summary>
    /// The small window gone, its place kept: once the video is back in Mira's window, or when the playback stops (its
    /// engine and view already released).
    /// </summary>
    public void ClosePip()
    {
        if (_pip is not { } pip) return;
        _pip = null;
        RememberPip(pip);
        // Closing the window with mpv's view inside would take OpenGL from under mpv.
        if (_surface is { } surface && surface.View.Parent == pip.VideoHost) { pip.VideoHost.Children.Remove(surface.View); surface.Release(); _surface = null; }
        if (_mpv is { } mpv && ReferenceEquals(mpv, _pipEngine))
        {
            if (_pipSubtitleScale != 1) mpv.Set("sub-scale", "1");
            if (_pipLift is not null) LowerPipSubtitles(mpv);
        }
        _pipEngine = null; _pipSubtitleScale = 1; _pipLift = null;
        pip.Dismiss();
        IsVisible = true;
    }
    private bool CanMovePicture() => _pip is null && !_pipMoving && !_repairing && !_stopped && _loaded && _mpv is not null && _surface is not null;

    /// <summary>
    /// mpv's view made again in <paramref name="to"/>, of the same kind (OpenGL, or software when this Mac draws without
    /// it), at the same place of the film: the video track let go first (mpv would otherwise lose its output with the
    /// OpenGL context), the view freed, the new one drawing, then the track taken again. False when it could not draw
    /// there: back where it was then.
    /// </summary>
    private async Task<bool> MoveVideoAsync(MpvPlayer mpv, Panel to)
    {
        if (_surface is not { } old) return false;
        var from = old.View.Parent as Panel;
        var track = mpv.Get("vid") is { Length: > 0 } and not "no" and not "auto" and var id ? id : "1";
        var paused = mpv.Flag("pause");
        mpv.Set("pause", "yes"); mpv.Set("vid", "no");
        try
        {
            await DetachAsync(old);
            for (var i = 0; i < 40 && old is VideoView { Drawing: true }; i++) await Task.Delay(50);
            if (_mpv != mpv || _stopped || old is VideoView { Drawing: true }) return false;
            var software = old is SoftwareVideoView;
            if (await AttachHereAsync(mpv, to, software)) return true;
            if (from is not null && _mpv == mpv && !_stopped) await AttachHereAsync(mpv, from, software);
            return false;
        }
        finally
        {
            _greyFlat = 0; _greyLookAt = Environment.TickCount64 + 1500;
            if (_mpv == mpv && !_stopped) { mpv.Set("vid", track); if (!paused) mpv.Set("pause", "no"); }
        }
    }
    /// <summary>A new view for <paramref name="mpv"/> in <paramref name="host"/>; OpenGL failing there, the software one, which always works.</summary>
    private async Task<bool> AttachHereAsync(MpvPlayer mpv, Panel host, bool software)
    {
        IVideoSurface surface = software ? new SoftwareVideoView() : new VideoView();
        // The repairs of a grey picture this Mac needed stay (PlayerPage.Picture.cs).
        if (_shell.Settings.VideoRepair >= 1 && surface is VideoView view) view.DropColours();
        try
        {
            if (await AttachAsync(surface, mpv, host)) return true;
            await DetachAsync(surface);
            if (software || _mpv != mpv || _stopped) return false;
            var fallback = new SoftwareVideoView();
            if (await AttachAsync(fallback, mpv, host)) return true;
            await DetachAsync(fallback);
            return false;
        }
        catch (InvalidOperationException) { if (_surface is { } made) await DetachAsync(made); return false; }
    }

    private void RememberPip(PipWindow pip)
    {
        if (pip.Placement() is not { } at || PictureInPicture.ScreenOf(at, PipWindow.ScreensOf(pip)) is not { } screen) return;
        var spot = PictureInPicture.Remember(at, screen);
        (_shell.Settings.PipX, _shell.Settings.PipY, _shell.Settings.PipWidth) = (spot.X, spot.Y, spot.Width);
    }
    /// <summary>The picture's shape as mpv shows it (with its pixel aspect); null until it knows.</summary>
    private static double? VideoAspect(MpvPlayer mpv) =>
        mpv.Number("dwidth") is > 0 and var width && mpv.Number("dheight") is > 0 and var height ? PictureInPicture.Aspect(width, height) : null;

    /// <summary>Each tick of the player: the small window's title, state, time and shape, and the subtitles sized for it.</summary>
    private void UpdatePip()
    {
        if (_pip is not { } pip || _mpv is not { } mpv || _pipMoving) return;
        if (_session.Item is { } item) pip.SetTitle(item.Type == "Episode" && _subtitle.Text is { Length: > 0 } episode ? $"{item.DisplayTitle} · {episode}" : item.DisplayTitle);
        pip.SetPaused(_session.Paused);
        pip.SetTime($"{Timeline.Clock(_session.Position)} / {Timeline.Clock(_session.Duration)}", _session.Duration > 0 ? _session.Position / _session.Duration : 0);
        if (_loaded && VideoAspect(mpv) is { } aspect && Math.Abs(aspect - pip.Aspect) > .01) pip.Refit(aspect);
        if (!ReferenceEquals(mpv, _pipEngine))
        {
            _pipEngine = mpv; _pipSubtitleScale = 1; _pipTracksAt = 0;
            _pipMargin = mpv.Number("sub-margin-y", 22) is var margin and >= 0 and <= 600 ? (int)margin : 22; _pipLift = null;
            SizePipSubtitles();
        }
        UpdatePipSubtitles();
    }
    /// <summary>
    /// mpv sizes subtitles to the view's height: in the small window, raised to stay readable (never lowered); set again
    /// at each new size and each new engine, back to mpv's own size in Mira's window.
    /// </summary>
    private void SizePipSubtitles()
    {
        if (_pip is not { } pip || _mpv is not { } mpv || !ReferenceEquals(mpv, _pipEngine)) return;
        var scale = PictureInPicture.SubtitleScale(_shell.Settings.SubtitleSize, pip.VideoHeight);
        if (scale != _pipSubtitleScale && mpv.Set("sub-scale", scale.ToString(CultureInfo.InvariantCulture))) _pipSubtitleScale = scale;
        LiftPipSubtitles();
    }
    /// <summary>
    /// While the controls show, the subtitles go above the time and the subtitles' button, as on video sites
    /// (<see cref="PictureInPicture.SubtitleLift"/>); back to their place once the controls go.
    /// </summary>
    private void LiftPipSubtitles()
    {
        if (_pip is not { } pip || _mpv is not { } mpv || !ReferenceEquals(mpv, _pipEngine) || _pipMoving) return;
        var lift = pip.ControlsShown ? PictureInPicture.SubtitleLift(pip.Fit.Lift, pip.VideoHeight, SubtitleStyle.Height(_shell.Settings.SubtitleHeight)) : null;
        if (lift == _pipLift) return;
        if (lift is { } position) { mpv.Set("sub-margin-y", "0"); mpv.Set("sub-pos", position.ToString(CultureInfo.InvariantCulture)); }
        else LowerPipSubtitles(mpv);
        _pipLift = lift;
    }
    /// <summary>The subtitles at their own place: the engine's sub-margin-y, the height chosen in Réglages.</summary>
    private void LowerPipSubtitles(MpvPlayer mpv)
    {
        mpv.Set("sub-margin-y", _pipMargin.ToString(CultureInfo.InvariantCulture));
        mpv.Set("sub-pos", SubtitleStyle.Height(_shell.Settings.SubtitleHeight).ToString(CultureInfo.InvariantCulture));
    }
    /// <summary>The subtitles' button: shown with a track to show, lit while one shows. The track list is read at most once a second.</summary>
    private void UpdatePipSubtitles()
    {
        if (_pip is not { } pip || _mpv is not { } mpv || !_loaded) return;
        var shown = !ConvertedTracks.Off(mpv.Get("sid")) || _converted?.Stream.Burned is not null;
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
        if (e.Handled || _stopped || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Space or Key.K: TogglePause(); break;
            case Key.Left or Key.J: SeekBy(-10); break;
            case Key.Right or Key.L: SeekBy(10); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: ToggleMute(); break;
            case Key.C: ToggleSubtitles(); _pipTracksAt = 0; UpdatePipSubtitles(); break;
            case Key.N: if (_skips.Next is { } next) _ = PlayNextAsync(next); break;
            case Key.I: _ = ReturnFromPipAsync(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// C, as on Windows: the subtitles hidden, or shown again (those shown before, else the first track), said by the
    /// pill in the middle of the picture. A conversion's go through its own tracks (PlayerPage.Stream.cs).
    /// </summary>
    private void ToggleSubtitles()
    {
        if (_mpv is null || !_loaded) return;
        if (_converted is { } converted)
        {
            var sid = _mpv.Get("sid"); var shown = converted.Shown(sid);
            if (shown is not null || converted.Stream.Burned is not null || converted.ShowsOther(sid))
            {
                _convertedBeforeOff = shown; _subtitlesBeforeOff = shown is null && !ConvertedTracks.Off(sid) ? sid : null;
                if (shown is null && _subtitlesBeforeOff is not null) _mpv.Set("sid", "no"); else _ = ChooseConvertedAsync(text: -1);
                ShowSeekPill(L("Sous-titres désactivés", "Subtitles off"));
                return;
            }
            if (_subtitlesBeforeOff is not null) { _mpv.Set("sid", _subtitlesBeforeOff); ShowSeekPill(L("Sous-titres activés", "Subtitles on")); return; }
            var first = _convertedBeforeOff ?? converted.Subtitles.Select(ConvertedTracks.Index).FirstOrDefault(i => !converted.IsPicture(i) && converted.Path(i) is not null, -1);
            if (first < 0) { ShowSeekPill(L("Aucun sous-titre", "No subtitles")); return; }
            _ = ChooseConvertedAsync(text: first); ShowSeekPill(L("Sous-titres activés", "Subtitles on"));
            return;
        }
        var current = _mpv.Get("sid");
        if (!ConvertedTracks.Off(current)) { _subtitlesBeforeOff = current; _mpv.Set("sid", "no"); ShowSeekPill(L("Sous-titres désactivés", "Subtitles off")); RememberTracks(); return; }
        var back = _subtitlesBeforeOff ?? _mpv.Tracks().FirstOrDefault(t => t.Type == "sub")?.Id;
        if (back is null) { ShowSeekPill(L("Aucun sous-titre", "No subtitles")); return; }
        _mpv.Set("sid", back); ShowSeekPill(L("Sous-titres activés", "Subtitles on")); RememberTracks();
    }
}
