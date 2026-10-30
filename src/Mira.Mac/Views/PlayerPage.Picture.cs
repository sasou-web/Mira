using Mira.Core;
using Mira.Mac.Playback;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Image » (Réglages → Lecture, <see cref="PictureQuality"/>), as on Windows (MainWindow.Picture.cs): set when a playback
/// starts. While it plays, a Mac that drops frames at that level (<see cref="PictureWatch"/>) goes to the level below,
/// said once, and stays there until Mira quits or « Image » is chosen again: the next episode would stutter the same way.
/// </summary>
public sealed partial class PlayerPage
{
    private readonly PictureWatch _pictureWatch = new();
    /// <summary>The level of this playback; the lighter one this Mac was put on since Mira started (one per app, as the pages come and go).</summary>
    private string _picture = PictureQuality.Cinema;
    private static string? _pictureCap;
    private static string? _pictureCapFor;
    private (double Width, double Height) _pictureSize;
    private static int PictureRank(string level) => PictureQuality.MacChoices.ToList().FindIndex(x => x.Key == PictureQuality.Known(level));
    private string PictureForPlayback()
    {
        var chosen = PictureQuality.Known(_shell.Settings.Picture);
        // Chosen again in Réglages since: the person's new choice is tried.
        if (_pictureCapFor != chosen) _pictureCap = null;
        _picture = _pictureCap is { } cap && PictureRank(cap) < PictureRank(chosen) ? cap : chosen;
        _pictureWatch.Reset(DateTimeOffset.UtcNow); _pictureSize = default;
        return _picture;
    }
    /// <summary>Each tick of a loaded playback: steady means playing at normal speed, with data, in a view that kept its size.</summary>
    private void WatchPicture(MpvPlayer mpv, bool paused)
    {
        if (PictureQuality.Lower(_picture) is not { } lower) return;
        var size = (mpv.Number("osd-width"), mpv.Number("osd-height"));
        var steady = !paused && !_timeline.Dragging && size == _pictureSize && !mpv.Flag("seeking") && !mpv.Flag("paused-for-cache") && Math.Abs(mpv.Number("speed", 1) - 1) < .001;
        _pictureSize = size;
        if (!_pictureWatch.Struggling(DateTimeOffset.UtcNow, (long)mpv.Number("frame-drop-count"), steady)) return;
        _picture = _pictureCap = lower; _pictureCapFor = PictureQuality.Known(_shell.Settings.Picture);
        mpv.Picture(lower, displaySync: false);
        _shell.Notice(L($"Image passée en « {PictureQuality.Label(lower)} » : ce Mac perdait des images. Jusqu’à la fermeture de Mira (Réglages → Lecture → Image).",
            $"Picture set to “{PictureQuality.Label(lower)}”: this Mac was dropping frames. Until Mira quits (Settings → Playback → Picture)."));
    }
    /// <summary>The level playing, said in « Infos de lecture ».</summary>
    private string PictureInfo => PictureQuality.Label(_picture) + (_picture != PictureQuality.Known(_shell.Settings.Picture) ? L(" (allégée)", " (lightened)") : "");
}
