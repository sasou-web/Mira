using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mira.Core;

namespace Mira.Desktop;

/// <summary>
/// « Image » (Réglages → Lecture, <see cref="PictureQuality"/>): set when a playback starts. While it plays, a PC that
/// drops frames at that level (<see cref="PictureWatch"/>) goes to the level below, said once, and stays there until
/// Mira closes or « Image » is chosen again: the next episode would stutter the same way.
/// </summary>
public partial class MainWindow
{
    private readonly PictureWatch _pictureWatch = new();
    /// <summary>The level of the playback, and the lighter one this PC was put on since Mira started.</summary>
    private string _picture = PictureQuality.Cinema;
    private string? _pictureCap;
    private (double Width, double Height) _pictureSize;
    /// <summary>« Décalage de l’audio » of the player's ⋮ (Bluetooth headphones): kept for the next playbacks until Mira closes.</summary>
    private double _audioDelay;
    private static int PictureRank(string level) => PictureQuality.Choices.ToList().FindIndex(x => x.Key == PictureQuality.Known(level));
    private string PictureForPlayback()
    {
        var chosen = PictureQuality.Known(_settings.Picture);
        _picture = _pictureCap is { } cap && PictureRank(cap) < PictureRank(chosen) ? cap : chosen;
        _pictureWatch.Reset(DateTimeOffset.UtcNow); _pictureSize = default;
        return _picture;
    }
    /// <summary>Each tick of a loaded playback: steady means playing at normal speed, with data, in a window that kept its size.</summary>
    private void WatchPicture(bool paused)
    {
        if (_mpv is null || PictureQuality.Lower(_picture) is not { } lower) return;
        var size = (_mpv.Number("osd-width"), _mpv.Number("osd-height"));
        var steady = !paused && !_seeking && size == _pictureSize && !_mpv.Flag("seeking") && !_mpv.Flag("paused-for-cache") && Math.Abs(_mpv.Number("speed", 1) - 1) < .001;
        _pictureSize = size;
        if (!_pictureWatch.Struggling(DateTimeOffset.UtcNow, (long)_mpv.Number("frame-drop-count"), steady)) return;
        _picture = _pictureCap = lower; _mpv.Picture(lower);
        SetNotice(L($"Image passée en « {PictureQuality.Label(lower)} » : ce PC perdait des images. Jusqu’à la fermeture de Mira (Réglages → Lecture → Image).",
            $"Picture set to “{PictureQuality.Label(lower)}”: this PC was dropping frames. Until Mira closes (Settings → Playback → Picture)."));
    }
    private string PictureInfo => PictureQuality.Label(_picture) + (_pictureCap == _picture && _picture != PictureQuality.Known(_settings.Picture) ? L(" (allégée)", " (lightened)") : "");
    private void AudioDelay_Click(object sender, RoutedEventArgs e)
    {
        var delta = double.Parse(((Button)sender).Tag.ToString()!, CultureInfo.InvariantCulture);
        _audioDelay = delta == 0 ? 0 : Math.Round((_mpv?.Number("audio-delay") ?? _audioDelay) + delta, 1);
        _mpv?.Set("audio-delay", _audioDelay.ToString(CultureInfo.InvariantCulture)); AudioDelayReset.Content = DelayLabel(_audioDelay);
        ShowPlayerControls();
    }
}
