namespace Mira.Core;

/// <summary>
/// « Couleurs » of ⋮ → Informations (Windows, Mac): the picture's range as mpv decodes it (SDR, HDR10, HDR10+, HLG,
/// Dolby Vision and its profile), its peak brightness, and what reaches the screen. On Windows, mpv's <c>gpu-next</c>
/// sets the swapchain's colours from the screen (<c>target-colorspace-hint=auto</c>, mpv's default): HDR sent as it is
/// when Windows' HDR is on, tone-mapped to SDR otherwise. A Mac draws into Avalonia's 8-bit OpenGL view: always SDR.
/// </summary>
public static class PictureColours
{
    /// <summary>
    /// The range of the video playing, read from mpv's properties (<paramref name="get"/>); null before mpv knows it.
    /// Dolby Vision: the track's profile (container), or the colour system mpv decodes it into (profile 5 has no other).
    /// </summary>
    public static string? Source(Func<string, string?> get)
    {
        var gamma = get("video-params/gamma"); var matrix = get("video-params/colormatrix");
        if (string.IsNullOrEmpty(gamma) && string.IsNullOrEmpty(matrix)) return null;
        var profile = Value(get("current-tracks/video/dolby-vision-profile"));
        var range = profile > 0 || matrix == "dolbyvision" ? "Dolby Vision" + (profile > 0 ? L($" profil {profile:0}", $" profile {profile:0}") : "")
            : gamma == "pq" ? (Value(get("video-params/scene-max-r")) > 0 ? "HDR10+" : "HDR10")
            : gamma == "hlg" ? "HLG"
            : "SDR";
        if (range == "SDR" || gamma == "hlg") return range;
        // The content's brightest pixel (MaxCLL), else the mastering display's peak (HDR10's metadata).
        var peak = Value(get("video-params/max-cll"));
        if (peak <= 0) peak = Value(get("video-params/max-luma"));
        return peak > 0 ? $"{range} · {Number(peak, "0")} nits" : range;
    }

    /// <summary>True for what mpv sends the screen in HDR (<c>video-target-params/gamma</c>); null when mpv does not say.</summary>
    public static bool? HdrOut(Func<string, string?> get) => get("video-target-params/gamma") is { Length: > 0 } target ? target is "pq" or "hlg" : null;

    /// <summary>
    /// The whole line: the source, then the screen. <paramref name="hdrOut"/> null = unknown (nothing said of the screen).
    /// </summary>
    public static string Line(string? source, bool? hdrOut)
    {
        if (source is null) return "—";
        var hdr = source != "SDR";
        return hdrOut switch
        {
            true => source + L(hdr ? " · envoyé en HDR à l’écran" : " · écran en HDR", hdr ? " · sent to the screen in HDR" : " · screen in HDR"),
            false when hdr => source + L(" · converti en SDR (HDR de Windows coupé ou écran SDR)", " · converted to SDR (Windows HDR off or an SDR screen)"),
            _ => source
        };
    }

    /// <summary>
    /// The line of a Mac: the source, « → SDR (limite du rendu) » when it is HDR, then the screen's colours (<paramref name="screen"/>).
    /// Mira draws mpv into Avalonia's 8-bit OpenGL view, which has no EDR: an HDR film is tone-mapped to SDR on every Mac,
    /// an XDR screen included. Said as a limit of Mira's drawing, not of the file or the screen (the Mac guide of the wiki explains it).
    /// </summary>
    public static string MacLine(string? source, string screen) =>
        (source is null ? "" : source + (source != "SDR" ? L(" → SDR (limite du rendu)", " → SDR (rendering limit)") : "") + " · ") + screen;

    private static double Value(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
}
