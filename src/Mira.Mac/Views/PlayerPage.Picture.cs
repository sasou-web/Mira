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
        mpv.Picture(lower, displaySync: false, deband: false);
        _shell.Notice(L($"Image passée en « {PictureQuality.Label(lower)} » : ce Mac perdait des images. Jusqu’à la fermeture de Mira (Réglages → Lecture → Image).",
            $"Picture set to “{PictureQuality.Label(lower)}”: this Mac was dropping frames. Until Mira quits (Settings → Playback → Picture)."));
    }
    // —— A grey picture ——
    // A Mac where mpv's OpenGL picture came out a single grey while the sound and the bar went on (0.8.7, a friend's
    // Mac): the view looks at a frame each second (VideoView.Flat), and four flat ones in a row while it plays take a
    // step back from what OpenGL does there: the screen's colours and debanding, then hardware decoding, then
    // no OpenGL at all (SoftwareVideoView). The step is kept (PlayerSettings.VideoRepair) until « Image » is chosen again.
    private long _greyLookAt; private int _greyLooks, _greyFlat;
    private bool _repairing;
    /// <summary>Each tick of a loaded playback: a look at the picture once a second while it plays.</summary>
    private void WatchGrey(MpvPlayer mpv, bool paused)
    {
        if (_surface is not VideoView view || _repairing || paused || mpv.Flag("seeking") || mpv.Flag("paused-for-cache")) return;
        var (flat, looks) = view.Flat;
        if (looks != _greyLooks) { _greyLooks = looks; _greyFlat = flat ? _greyFlat + 1 : 0; }
        if (_greyFlat >= 4) { _greyFlat = 0; _ = RepairAsync(mpv, view, asked: false); return; }
        if (Environment.TickCount64 - _greyLookAt < 1000) return;
        _greyLookAt = Environment.TickCount64;
        view.LookNextFrame();
    }
    /// <summary>The steps up to <paramref name="level"/> that need no other view, on a player about to play or playing.</summary>
    private static void Repair(MpvPlayer mpv, IVideoSurface surface, int level)
    {
        if (level >= 1)
        {
            mpv.Set("deband", "no");
            if (surface is VideoView view) view.DropColours();
        }
        if (level >= 2) mpv.Set("hwdec", "no");
    }
    /// <summary>
    /// The next step, at once: four flat looks in a row (<paramref name="asked"/> false), or « Réparer » of ⋮ when the
    /// picture stays grey or black on the screen while the frame mpv drew is not (macOS composing the window, which no
    /// look can see), each click one step further, said each time.
    /// </summary>
    private async Task RepairAsync(MpvPlayer mpv, VideoView view, bool asked)
    {
        var level = Math.Min(3, _shell.Settings.VideoRepair + 1);
        _shell.Settings.VideoRepair = level;
        System.Diagnostics.Trace.WriteLine($"Mira: grey picture, repair {level}{(asked ? " (asked)" : "")}, look {view.LastLook}");
        // The next looks after the change has reached the screen.
        _greyLookAt = Environment.TickCount64 + 1500; _greyFlat = 0;
        if (level < 3)
        {
            Repair(mpv, view, level);
            if (asked) _shell.Notice(level == 1
                ? L("Réparation 1 sur 3 : couleurs de l’écran et lissage coupés. Toujours grise ? ⋮ → Réparer encore.",
                    "Repair 1 of 3: screen colours and smoothing off. Still grey? ⋮ → Repair again.")
                : L("Réparation 2 sur 3 : décodage matériel coupé. Toujours grise ? ⋮ → Réparer encore.",
                    "Repair 2 of 3: hardware decoding off. Still grey? ⋮ → Repair again."));
            return;
        }
        _repairing = true;
        try { await DrawWithoutOpenGlAsync(mpv, view); }
        finally { _repairing = false; }
    }
    /// <summary>
    /// The playing video moved to <see cref="SoftwareVideoView"/>, paused meanwhile: the video track let go first (mpv
    /// would otherwise lose its output with the OpenGL context and drop the video for good), the context freed with the
    /// view, the other one made, then the track taken again at the same place.
    /// </summary>
    private async Task DrawWithoutOpenGlAsync(MpvPlayer mpv, VideoView view)
    {
        var track = mpv.Get("vid") is { Length: > 0 } and not "no" and not "auto" and var id ? id : "1";
        var paused = mpv.Flag("pause");
        mpv.Set("pause", "yes"); mpv.Set("vid", "no");
        await DetachAsync(view);
        for (var i = 0; i < 40 && view.Drawing; i++) await Task.Delay(50);
        if (_mpv != mpv || _stopped || view.Drawing) return;
        var software = new SoftwareVideoView();
        try { if (!await AttachAsync(software, mpv)) return; }
        catch (InvalidOperationException) { return; }
        mpv.Set("vid", track);
        if (!paused) mpv.Set("pause", "no");
        _shell.Notice(L("L’image restait grise : la vidéo s’affiche maintenant sans OpenGL. Jusqu’à un nouveau choix dans Réglages → Lecture → Image.",
            "The picture stayed grey: the video is now drawn without OpenGL. Until Settings → Playback → Picture is chosen again."));
    }

    /// <summary>« Image grise ou noire ? Réparer » under « Informations », while OpenGL draws the video.</summary>
    private Control? GreyRepairLine()
    {
        if (_mpv is not { } mpv || !_loaded || _surface is not VideoView view || _repairing) return null;
        var next = Math.Min(3, _shell.Settings.VideoRepair + 1);
        var repair = new Button { Content = _shell.Settings.VideoRepair > 0 ? L("Réparer encore", "Repair again") : L("Réparer", "Repair"), Classes = { "menuchip" }, MinWidth = 96, Margin = new Thickness(0) };
        var tip = L($"Essayer la réparation {next} sur 3 : ", $"Try repair {next} of 3: ") + (next switch
        {
            1 => L("couleurs de l’écran et lissage coupés", "screen colours and smoothing off"),
            2 => L("décodage matériel coupé", "hardware decoding off"),
            _ => L("image dessinée sans OpenGL", "picture drawn without OpenGL")
        });
        ToolTip.SetTip(repair, tip);
        Avalonia.Automation.AutomationProperties.SetName(repair, tip);
        repair.Click += (_, _) => { (_more.Flyout as Flyout)?.Hide(); _ = RepairAsync(mpv, view, asked: true); };
        var line = new DockPanel { Margin = new Thickness(10, 0, 6, 8) };
        DockPanel.SetDock(repair, Dock.Right); line.Children.Add(repair);
        line.Children.Add(new TextBlock { Text = L("Image grise ou noire ?", "Grey or black picture?"), FontSize = 13, Foreground = Ui.Brush("#DEDEE4"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return line;
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
        var colours = _surface is VideoView { ColourProfile.Length: > 0 } view ? L($"écran large gamme ({view.ColourProfile})", $"wide-gamut screen ({view.ColourProfile})") : L("non gérées", "not managed");
        // What the frame mpv drew holds, looked at once a second while it plays: « unie #BBBBBB » with a grey screen = mpv's
        // own picture is grey; « variée » = mpv draws the film and the grey comes after it, when macOS shows the window.
        var look = _surface is VideoView { LastLook: { Length: > 0 } seen } looked
            ? (seen == "~" ? L("variée", "varied") : seen.StartsWith('#') ? L("unie ", "single colour ") + seen : seen) + $" ({looked.Flat.Looks})"
            : null;
        var repair = _shell.Settings.VideoRepair switch
        {
            1 => L("\nRéparé  image grise : couleurs de l’écran et lissage coupés", "\nRepaired  grey picture: screen colours and smoothing off"),
            2 => L("\nRéparé  image grise : couleurs de l’écran, lissage et décodage matériel coupés", "\nRepaired  grey picture: screen colours, smoothing and hardware decoding off"),
            >= 3 => L("\nRéparé  image grise : rendu sans OpenGL", "\nRepaired  grey picture: drawn without OpenGL"),
            _ => ""
        };
        var seenLine = look is null ? "" : L($"\nImage lue  {look}", $"\nFrame read  {look}");
        return L($"Vidéo  {(video.Length > 0 ? video : "—")}\nDécodage  {decoding}\nRendu  {drawing}\nCouleurs  {colours}\nImage  {PictureInfo}\nImages perdues  {mpv.Get("frame-drop-count") ?? "0"}{seenLine}{repair}",
            $"Video  {(video.Length > 0 ? video : "—")}\nDecoding  {decoding}\nRendering  {drawing}\nColours  {colours}\nPicture  {PictureInfo}\nDropped frames  {mpv.Get("frame-drop-count") ?? "0"}{seenLine}{repair}");
    }
    /// <summary>The level playing, said in « Informations ».</summary>
    private string PictureInfo => PictureQuality.Label(_picture) + (_picture != PictureQuality.Known(_shell.Settings.Picture) ? L(" (allégée)", " (lightened)") : "");
}
