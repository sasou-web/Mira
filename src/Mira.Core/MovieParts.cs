using static Mira.Core.Lang;

namespace Mira.Core;

/// <summary>
/// A title in several files (« Film - cd1.mkv », « Film - cd2.mkv », « part1 », « disc2 »…, put together by Jellyfin):
/// one title in the library (<see cref="MediaItem.PartCount"/>) whose length, stream and progress are those of its
/// first file; each other file is an item of its own, hidden from the library (Videos/{id}/AdditionalParts, checked on
/// Jellyfin 12.1). They play one after the other in the same player, each reported under its own id, as Jellyfin's web
/// client does; Jellyfin marks the title watched at the end of its first part. Windows and Mac; the iPhone keeps its
/// own copy (Player/MovieParts.swift) with the cases of tests/fixtures/parts.json.
/// </summary>
public sealed class MovieParts
{
    public MovieParts(MediaItem title, IEnumerable<MediaItem> others)
    {
        Title = title;
        Parts = [title, .. others.Where(x => x.Id != title.Id)];
    }

    public MediaItem Title { get; }
    /// <summary>The title first, then its other files in Jellyfin's order.</summary>
    public IReadOnlyList<MediaItem> Parts { get; }

    /// <summary>A film or an episode in more than one file: its other parts are worth asking Jellyfin for.</summary>
    public static bool Has(MediaItem item) => item.PartCount > 1 && item.Type is "Movie" or "Episode";

    public bool Holds(string id) => IndexOf(id) >= 0;

    /// <summary>The part after this one; null after the last one, and for an id that is none of them.</summary>
    public MediaItem? After(string id) => IndexOf(id) is var at and >= 0 && at + 1 < Parts.Count ? Parts[at + 1] : null;

    /// <summary>
    /// Where « Lecture » starts: a later part left halfway, from where it stopped (Jellyfin marked the title watched at
    /// the end of its first part), unless the first part was left halfway after it (Jellyfin 12 keeps a title watched
    /// when it is watched again, with its position: checked on 12.1). Otherwise, and for « Depuis le début », the first.
    /// </summary>
    public MediaItem Start(bool fromStart)
    {
        if (fromStart) return Title;
        var later = Parts.Skip(1).Where(x => x.UserData is { Played: false, PlaybackPositionTicks: > 0 }).MaxBy(Seen);
        return later is null || Title.UserData.PlaybackPositionTicks > 0 && Seen(Title) >= Seen(later) ? Title : later;
    }
    private static DateTimeOffset Seen(MediaItem item) => item.UserData.LastPlayedDate ?? DateTimeOffset.MinValue;

    /// <summary>« Partie 2 sur 3 »; empty for an id that is none of them.</summary>
    public string Label(string id) => IndexOf(id) is var at and >= 0 ? L($"Partie {at + 1} sur {Parts.Count}", $"Part {at + 1} of {Parts.Count}") : "";

    /// <summary>
    /// The line under the title in the player: the episode (numbered among <paramref name="seriesEpisodes"/>), or the
    /// film's year (its length would be that of its first part only), then which part plays.
    /// </summary>
    public string Subtitle(string id, IReadOnlyList<MediaItem>? seriesEpisodes = null) =>
        string.Join("  ·  ", new[] { Title.Type == "Episode" ? PlayerText.Subtitle(Title, seriesEpisodes) : Title.ProductionYear?.ToString(), Label(id) }.Where(x => !string.IsNullOrEmpty(x)));

    private int IndexOf(string id)
    {
        for (var at = 0; at < Parts.Count; at++) if (Parts[at].Id == id) return at;
        return -1;
    }
}
