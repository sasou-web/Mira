namespace Mira.Core;

/// <summary>
/// « Cacher les spoilers » (Réglages → Lecture, off by default): an episode not watched yet, not even started, shows
/// neither its own picture nor its summary, as Jellyfin web's « Masquer les vignettes des épisodes non vus », Plex and
/// Netflix do; its name and number stay. Its picture becomes Jellyfin's BlurHash of it (colours, no shapes), and the
/// picture itself is never fetched. Started (some progress) or watched: shown as before. The same on Windows, Mac and
/// iPhone (Spoilers.swift; cases shared in tests/fixtures/spoilers.json). Nothing leaves the device for it.
/// </summary>
public static class Spoilers
{
    /// <summary>
    /// This device's setting (<see cref="PlayerSettings.HideSpoilers"/>), set by the app when it reads its settings and
    /// when the setting changes; read by <see cref="Artwork.Landscape"/>, which has no settings of its own.
    /// </summary>
    public static bool On { get; set; }

    /// <summary>True when <paramref name="item"/> is an episode whose picture and summary stay hidden.</summary>
    public static bool Hides(MediaItem item, bool on) =>
        on && item.Type == "Episode" && !item.UserData.Played && item.UserData.PlaybackPositionTicks <= 0;
    /// <inheritdoc cref="Hides(MediaItem, bool)"/>
    public static bool Hides(MediaItem item) => Hides(item, On);

    /// <summary>The episode's own still (Primary, else Thumb): what its BlurHash is read from in a list of episodes.</summary>
    public static ImageRef Still(MediaItem episode)
    {
        var type = episode.ImageTags.ContainsKey("Primary") ? "Primary" : "Thumb";
        return new(episode.Id, type, episode.ImageTags.GetValueOrDefault(type) ?? "");
    }

    /// <summary>The line in place of a hidden summary.</summary>
    public static string HiddenSummary => Lang.L("Pas encore vu : résumé caché.", "Not watched yet: summary hidden.");
    public static string Setting => Lang.L("Cacher les spoilers", "Hide spoilers");
    public static string SettingHelp => Lang.L("Les épisodes que tu n’as pas encore commencés montrent une image floutée et pas de résumé.",
        "Episodes you haven’t started yet show a blurred picture and no summary.");
}
