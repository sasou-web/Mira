using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mira.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

public sealed record UserData
{
    public long PlaybackPositionTicks { get; set; }
    public bool Played { get; set; }
    public bool IsFavorite { get; set; }
    public double? PlayedPercentage { get; set; }
    public DateTimeOffset? LastPlayedDate { get; set; }
}

public sealed record MediaItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "Movie";
    public string? SeriesName { get; init; }
    public string? SeriesId { get; init; }
    public string? SeasonId { get; init; }
    public string? Overview { get; init; }
    public int? ProductionYear { get; init; }
    public int? IndexNumber { get; init; }
    public int? ParentIndexNumber { get; init; }
    public long? RunTimeTicks { get; init; }
    public float? CommunityRating { get; init; }
    public string[] Genres { get; init; } = [];
    public Dictionary<string, string> ImageTags { get; init; } = [];
    public string[] BackdropImageTags { get; init; } = [];
    public string? ParentBackdropItemId { get; init; }
    public string[] ParentBackdropImageTags { get; init; } = [];
    public string? SeriesPrimaryImageTag { get; init; }
    public string? ParentLogoItemId { get; init; }
    public string? ParentLogoImageTag { get; init; }
    /// <summary>The thumbnail of the episode's season, filled in by Mira (Jellyfin does not send it with the episode).</summary>
    public string? SeasonThumbImageTag { get; set; }
    public int? ChildCount { get; init; }
    public string? OfficialRating { get; init; }
    /// <summary>Cast and crew, returned by the single-item request only.</summary>
    public List<PersonInfo> People { get; init; } = [];
    public UserData UserData { get; set; } = new();
    [JsonIgnore] public string DisplayTitle => Type == "Episode" ? SeriesName ?? Name : Name;
    [JsonIgnore]
    public string Subtitle => Type == "Episode"
        ? $"S{ParentIndexNumber ?? 1:00} · E{IndexNumber ?? 1:00}  —  {Name}"
        : string.Join("  ·  ", new[] { ProductionYear?.ToString(), Type == "Series" ? "Série" : DurationLabel }.Where(s => !string.IsNullOrEmpty(s)));
    [JsonIgnore] public string DurationLabel => RunTimeTicks is > 0 ? FormatDuration(TimeSpan.FromTicks(RunTimeTicks.Value)) : "";
    [JsonIgnore] public double Progress => RunTimeTicks is > 0 ? Math.Clamp((double)UserData.PlaybackPositionTicks / RunTimeTicks.Value, 0, 1) : 0;
    /// <summary>The leading actors, in the order Jellyfin credits them.</summary>
    [JsonIgnore] public string[] Cast => Credited("Actor", 6);
    [JsonIgnore] public string[] Directors => Credited("Director", 3);
    private string[] Credited(string type, int count) => People.Where(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase))
        .Select(x => x.Name.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(count).ToArray();
    public static string FormatDuration(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00}" : $"{Math.Max(1, span.Minutes)} min";
}

public sealed record PersonInfo
{
    public string Name { get; init; } = "";
    public string? Role { get; init; }
    /// <summary>"Actor", "Director", "Writer", "Producer", "GuestStar"…</summary>
    public string? Type { get; init; }
}

public sealed record ItemsResult
{
    public List<MediaItem> Items { get; init; } = [];
    public int TotalRecordCount { get; init; }
}
public sealed record CatalogFilters
{
    public string[] Genres { get; init; } = [];
    public int[] Years { get; init; } = [];
}
public sealed record CatalogQuery(string? Genre = null, int? Year = null, bool? Played = null, string Sort = "recent")
{
    public string CacheKey => $"{Genre}|{Year}|{Played}|{Sort}";
    public string Parameters =>
        (string.IsNullOrWhiteSpace(Genre) ? "" : "&genres=" + Uri.EscapeDataString(Genre)) +
        (Year is null ? "" : "&years=" + Year.Value) +
        (Played is null ? "" : "&isPlayed=" + Played.Value.ToString().ToLowerInvariant()) +
        (Sort switch
        {
            "title" => "&sortBy=SortName&sortOrder=Ascending",
            "year" => "&sortBy=ProductionYear,SortName&sortOrder=Descending",
            "rating" => "&sortBy=CommunityRating,SortName&sortOrder=Descending",
            "played" => "&sortBy=DatePlayed,SortName&sortOrder=Descending",
            _ => "&sortBy=DateCreated,SortName&sortOrder=Descending"
        });
}
public sealed record LibrarySection(string Id, string Name, string? CollectionType);
/// <summary>A server library with its folders, as returned by Library/VirtualFolders.</summary>
public sealed record VirtualFolder(string Name, string? CollectionType, string[] Locations);
public sealed record AuthUser(string Id, string Name);
public sealed record AuthenticationResult(string AccessToken, AuthUser User);
public sealed record Connection(string Server, string UserId, string UserName, string Token, string DeviceId);
public sealed record MediaStream
{
    public int Index { get; init; }
    public string Type { get; init; } = "";
    public string? Codec { get; init; }
    public string? Language { get; init; }
    public string? DisplayTitle { get; init; }
    public bool IsExternal { get; init; }
    public string? DeliveryUrl { get; init; }
}
public sealed record MediaSource
{
    public string Id { get; init; } = "";
    public string? Container { get; init; }
    public string? Path { get; init; }
    public string? Protocol { get; init; }
    public List<MediaStream> MediaStreams { get; init; } = [];
}
public sealed record PlaybackInfo
{
    public string PlaySessionId { get; init; } = "";
    public List<MediaSource> MediaSources { get; init; } = [];
    public string? ErrorCode { get; init; }
}
public sealed record PlaybackReport
{
    public string ItemId { get; init; } = "";
    public string MediaSourceId { get; init; } = "";
    public string PlaySessionId { get; init; } = "";
    public long PositionTicks { get; init; }
    public bool IsPaused { get; init; }
    public bool IsMuted { get; init; }
    public int VolumeLevel { get; init; } = 80;
    public bool CanSeek { get; init; } = true;
    public string PlayMethod { get; init; } = "DirectPlay";
    public string RepeatMode { get; init; } = "RepeatNone";
}
public sealed record PendingReport(long Id, string Kind, PlaybackReport Report);
/// <summary>Jellyfin's library scan: Progress from 0 to 100 while it runs; LastEnded, when the previous one finished.</summary>
public sealed record LibraryScan(bool Running, double? Progress, DateTimeOffset? LastEnded);
public sealed class PlayerSettings
{
    public string MpvPath { get; set; } = "";
    public double Volume { get; set; } = 80;
    public string AudioLanguage { get; set; } = "jpn,ja,fre,fra,fr,eng,en";
    public string SubtitleLanguage { get; set; } = "fre,fra,fr,eng,en";
    public bool HardwareDecoding { get; set; } = true;
    public bool AutoNext { get; set; } = true;
    public int SubtitleSize { get; set; } = 40;
    public bool ShowProgress { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public string PosterDensity { get; set; } = "Comfortable";
    public bool RememberPosition { get; set; } = true;
    public bool HeroAutoPlay { get; set; } = true;
    /// <summary>Checks GitHub for new versions, downloads them and installs them when Mira closes.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>TorLink folder; empty: found through its shortcuts.</summary>
    public string TorLinkPath { get; set; } = "";
    public bool TorLinkAutoImport { get; set; } = true;
    /// <summary>TorLink turned on or off in the Downloads page; null until chosen: on when a TorLink is already installed.</summary>
    public bool? TorLinkActive { get; set; }
    /// <summary>
    /// False (default): the download moves into the library. True: a hard link on the same drive, so TorLink keeps
    /// sharing it. Replaces TorLinkKeepSeeding (on by default before 0.5.7), which saved profiles leave behind: they move too.
    /// </summary>
    public bool TorLinkKeepShared { get; set; }
    /// <summary>Manual library folders; empty: the folders of the Jellyfin libraries.</summary>
    public string TorLinkMoviesFolder { get; set; } = "";
    public string TorLinkSeriesFolder { get; set; } = "";
    public string TorLinkAnimeFolder { get; set; } = "";
    /// <summary>The last version whose welcome or "what's new" screen this profile went past; empty before 0.5.5.</summary>
    public string SeenVersion { get; set; } = "";
    /// <summary>The guide opened once already: it is not offered again after a connection.</summary>
    public bool GuideSeen { get; set; }
}
