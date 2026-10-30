using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
    /// <summary>
    /// « Informations » of the ⋮ menu, as on Windows: what mpv plays and how it draws it, to tell a stream converted by
    /// Jellyfin (« Qualité » above says it), a Mac without OpenGL or without its screen's colours from a file of poor quality.
    /// </summary>
    private Control? PlaybackInfo() => InfoText() is { } lines
        ? new TextBlock { Text = lines, FontSize = 12, LineHeight = 18, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 10, 8) }
        : null;
    /// <summary>The lines of « Informations »; null before the file is loaded.</summary>
    public string? InfoText()
    {
        if (_mpv is not { } mpv || !_loaded) return null;
        var width = mpv.Number("width"); var height = mpv.Number("height");
        var video = string.Join(" · ", new[] { PlayerText.Codec(mpv.Get("video-format")), width > 0 && height > 0 ? $"{width:0} × {height:0}" : null, mpv.Get("video-params/pixelformat") is { Length: > 0 } format && format.Contains("10") ? L("10 bits", "10-bit") : null }.OfType<string>());
        var decoding = mpv.Get("hwdec-current") is { Length: > 0 } hwdec and not "no" ? L($"matériel ({hwdec})", $"hardware ({hwdec})") : L("logiciel", "software");
        var shownWidth = mpv.Number("osd-width"); var shownHeight = mpv.Number("osd-height");
        var drawing = _surface is VideoView gl ? $"OpenGL{(gl.Renderer.Length > 0 ? " · " + gl.Renderer : "")}" : L("logiciel (sans OpenGL)", "software (no OpenGL)");
        if (shownWidth > 0 && shownHeight > 0) drawing += $" · {shownWidth:0} × {shownHeight:0}";
        var colours = _surface is VideoView { ColourProfile.Length: > 0 } view ? L($"profil de l’écran ({view.ColourProfile})", $"screen profile ({view.ColourProfile})") : L("non gérées", "not managed");
        return L($"Vidéo  {(video.Length > 0 ? video : "—")}\nDécodage  {decoding}\nRendu  {drawing}\nCouleurs  {colours}\nImage  {PictureInfo}\nImages perdues  {mpv.Get("frame-drop-count") ?? "0"}",
            $"Video  {(video.Length > 0 ? video : "—")}\nDecoding  {decoding}\nRendering  {drawing}\nColours  {colours}\nPicture  {PictureInfo}\nDropped frames  {mpv.Get("frame-drop-count") ?? "0"}");
    }
    /// <summary>The level playing, said in « Informations ».</summary>
    private string PictureInfo => PictureQuality.Label(_picture) + (_picture != PictureQuality.Known(_shell.Settings.Picture) ? L(" (allégée)", " (lightened)") : "");
}
