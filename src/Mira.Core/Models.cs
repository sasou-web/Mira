using System.Globalization;
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
    /// <summary>A series' episodes not yet watched.</summary>
    public int? UnplayedItemCount { get; set; }
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
    public int? IndexNumberEnd { get; init; }
    public string? OriginalTitle { get; init; }
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
    /// <summary>Its file (a film) or folder (a series) as Jellyfin sees it, returned by the single-item request (<see cref="TitleFiles"/>).</summary>
    public string? Path { get; init; }
    /// <summary>When Jellyfin added it (« Ajouts récents »), asked for with Fields=DateCreated.</summary>
    public DateTimeOffset? DateCreated { get; init; }
    /// <summary>
    /// The day an episode aired or airs, as TheTVDB gives it (Jellyfin keeps it at midnight UTC): read for the virtual
    /// episodes of <see cref="EpisodeCalendar"/>, asked for with Fields=PremiereDate.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? PremiereDate { get; init; }
    /// <summary>« Virtual » for an episode Jellyfin knows of without its file (TheTVDB's extension); « FileSystem » otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocationType { get; init; }
    /// <summary>A bonus video's kind (« BehindTheScenes », « DeletedScene »…, « Unknown » in an Extras folder); null for a title.</summary>
    public string? ExtraType { get; init; }
    /// <summary>How many bonus videos a film or series has (<see cref="JellyfinClient.ExtrasAsync"/>), returned by the single-item request.</summary>
    public int? SpecialFeatureCount { get; init; }
    /// <summary>
    /// The ids of the title on TMDb, IMDb, TVDb… as Jellyfin matched it (« Tmdb » → « 438631 »), asked for with
    /// Fields=ProviderIds; none when Jellyfin recognized nothing (<see cref="TitleIdentity"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ProviderIds { get; init; }
    /// <summary>Cast and crew, returned by the single-item request only.</summary>
    public List<PersonInfo> People { get; init; } = [];
    /// <summary>Who made it (« Studio Ghibli », « MAPPA »), returned by the single-item request only.</summary>
    public List<StudioInfo> Studios { get; init; } = [];
    /// <summary>The files of a film or an episode, returned by the single-item request only (<see cref="MediaQuality"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MediaSource>? MediaSources { get; init; }
    public UserData UserData { get; set; } = new();
    [JsonIgnore] public string DisplayTitle => Type == "Episode" ? SeriesName ?? Name : Name;
    [JsonIgnore]
    public string Subtitle => Type == "Episode"
        ? $"S{ParentIndexNumber ?? 1:00} · E{IndexNumber ?? 1:00}  —  {Name}"
        : string.Join("  ·  ", new[] { ProductionYear?.ToString(), Type == "Series" ? L("Série", "Series") : DurationLabel }.Where(s => !string.IsNullOrEmpty(s)));
    /// <summary>
    /// Episodes left in a series already started: the count that shows new ones came out. None before its first episode
    /// is watched, where it would only repeat its length. Jellyfin 10.9 gives the share watched with RecursiveItemCount.
    /// </summary>
    [JsonIgnore]
    public int? EpisodesToWatch => Type == "Series" && !UserData.Played && UserData.PlayedPercentage > 0 && UserData.UnplayedItemCount is int count and > 0 ? count : null;
    /// <summary>The badge on the poster (« 3 », « 99+ ») and what a screen reader says of it.</summary>
    [JsonIgnore] public string? EpisodesToWatchBadge => EpisodesToWatch is { } count ? count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture) : null;
    [JsonIgnore] public string? EpisodesToWatchText => EpisodesToWatch is { } count ? count == 1 ? L("1 épisode à voir", "1 episode to watch") : L($"{count} épisodes à voir", $"{count} episodes to watch") : null;
    [JsonIgnore] public string DurationLabel => RunTimeTicks is > 0 ? FormatDuration(TimeSpan.FromTicks(RunTimeTicks.Value)) : "";
    [JsonIgnore] public double Progress => RunTimeTicks is > 0 ? Math.Clamp((double)UserData.PlaybackPositionTicks / RunTimeTicks.Value, 0, 1) : 0;
    /// <summary>The leading actors, in the order Jellyfin credits them.</summary>
    [JsonIgnore] public string[] Cast => CastPeople.Select(x => x.Name).ToArray();
    [JsonIgnore] public string[] Directors => DirectorPeople.Select(x => x.Name).ToArray();
    /// <summary>The same people with their Jellyfin id, to list their other titles.</summary>
    [JsonIgnore] public PersonInfo[] CastPeople => Credited("Actor", 6);
    [JsonIgnore] public PersonInfo[] DirectorPeople => Credited("Director", 3);
    private PersonInfo[] Credited(string type, int count) => People.Where(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase))
        .Select(x => x with { Name = x.Name.Trim() }).Where(x => x.Name.Length > 0).DistinctBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Take(count).ToArray();
    public static string FormatDuration(TimeSpan span) => span.TotalHours >= 1 ? L($"{(int)span.TotalHours} h {span.Minutes:00}", $"{(int)span.TotalHours} hr {span.Minutes} min") : $"{Math.Max(1, span.Minutes)} min";
}

public sealed record PersonInfo
{
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public string? Role { get; init; }
    /// <summary>"Actor", "Director", "Writer", "Producer", "GuestStar"…</summary>
    public string? Type { get; init; }
}

/// <summary>A studio of a title, with its Jellyfin id to list its other titles.</summary>
public sealed record StudioInfo
{
    public string? Id { get; init; }
    public string Name { get; init; } = "";
}

public sealed record ItemsResult
{
    public List<MediaItem> Items { get; init; } = [];
    public int TotalRecordCount { get; init; }
}
/// <summary>A saga (a Jellyfin collection) seen from one of its films: its name and its other titles, in order.</summary>
public sealed record Saga(string Name, List<MediaItem> Items);
public sealed record CatalogFilters
{
    public string[] Genres { get; init; } = [];
    public int[] Years { get; init; } = [];
}
/// <summary>Catalog filters; <paramref name="PersonId"/>: only the titles with this actor or director; <paramref name="StudioId"/>: of this studio.</summary>
public sealed record CatalogQuery(string? Genre = null, int? Year = null, bool? Played = null, string Sort = "recent", string? PersonId = null, string? StudioId = null)
{
    public string CacheKey => $"{Genre}|{Year}|{Played}|{Sort}|{PersonId}|{StudioId}";
    public string Parameters =>
        (string.IsNullOrWhiteSpace(Genre) ? "" : "&genres=" + Uri.EscapeDataString(Genre)) +
        (string.IsNullOrWhiteSpace(PersonId) ? "" : "&personIds=" + Uri.EscapeDataString(PersonId)) +
        (string.IsNullOrWhiteSpace(StudioId) ? "" : "&studioIds=" + Uri.EscapeDataString(StudioId)) +
        (Year is null ? "" : "&years=" + Year.Value) +
        (Played is null ? "" : "&isPlayed=" + Played.Value.ToString().ToLowerInvariant()) +
        (Sort switch
        {
            "title" => "&sortBy=SortName&sortOrder=Ascending",
            "year" => "&sortBy=ProductionYear,SortName&sortOrder=Descending",
            "rating" => "&sortBy=CommunityRating,SortName&sortOrder=Descending",
            "played" => "&sortBy=DatePlayed,SortName&sortOrder=Descending",
            // A saga's titles in the order they came out.
            "saga" => "&sortBy=ProductionYear,PremiereDate,SortName&sortOrder=Ascending",
            _ => "&sortBy=DateCreated,SortName&sortOrder=Descending"
        });
}
public sealed record LibrarySection(string Id, string Name, string? CollectionType)
{
    /// <summary>
    /// Jellyfin's own groupings of titles from the other libraries (its Collections, made with the sagas, and Playlists):
    /// not listed among the libraries, as on Mac and in Mira web.
    /// </summary>
    public static bool Groups(string? collectionType) => collectionType is "boxsets" or "playlists";
}
/// <summary>A server library with its folders, as returned by Library/VirtualFolders (ItemId: what a policy names).</summary>
public sealed record VirtualFolder(string Name, string? CollectionType, string[] Locations, string? ItemId = null);
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
    /// <summary>An external subtitle's file on the server: tells one just kept from the others (<see cref="JellyfinSubtitles.Arrived"/>).</summary>
    public string? Path { get; init; }
    public string? DeliveryUrl { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    /// <summary>« SDR », « HDR »; VideoRangeType is finer: « HDR10 », « HDR10Plus », « HLG », « DOVI », « DOVIWithHDR10 »…</summary>
    public string? VideoRange { get; init; }
    public string? VideoRangeType { get; init; }
    /// <summary>An audio track's channels (2, 6…).</summary>
    public int? Channels { get; init; }
    public string? Title { get; init; }
    public bool IsForced { get; init; }
    /// <summary>A subtitle track in text (SubRip, ASS, WebVTT): sent beside a conversion; the others are pictures, drawn into it.</summary>
    public bool IsTextSubtitleStream { get; init; }
    /// <summary>How a subtitle track comes with the stream PlaybackInfo chose: « Embed » (in the file), « External » (at <see cref="DeliveryUrl"/>), « Encode » (drawn into the picture).</summary>
    public string? DeliveryMethod { get; init; }
}
public sealed record MediaSource
{
    public string Id { get; init; } = "";
    public string? Container { get; init; }
    public string? Path { get; init; }
    public string? Protocol { get; init; }
    /// <summary>The file's size in bytes, when Jellyfin knows it.</summary>
    public long? Size { get; init; }
    /// <summary>The file's bitrate in bits per second, when Jellyfin knows it.</summary>
    public long? Bitrate { get; init; }
    public List<MediaStream> MediaStreams { get; init; } = [];
    /// <summary>What PlaybackInfo decided for the device profile it was sent (<see cref="ServerStreams"/>): the file as it is, or its conversion at <see cref="TranscodingUrl"/>.</summary>
    public bool SupportsDirectPlay { get; init; }
    public string? TranscodingUrl { get; init; }
    public int? DefaultAudioStreamIndex { get; init; }
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
    /// <summary>Language of Mira chosen in its settings, "en" or "fr" (<see cref="Lang"/>); empty until chosen: the one chosen at
    /// installation, else the system's.</summary>
    public string Language { get; set; } = "";
    public string MpvPath { get; set; } = "";
    public double Volume { get; set; } = 80;
    public string AudioLanguage { get; set; } = DefaultAudioLanguage;
    public string SubtitleLanguage { get; set; } = DefaultSubtitleLanguage;
    /// <summary>
    /// The tracks preferred before any choice: an anime's original Japanese first, then Mira's language, then the other one.
    /// A profile keeps what it saved: only a new one, or a device that never chose, takes the order of its language.
    /// </summary>
    public static string DefaultAudioLanguage => L("jpn,ja,fre,fra,fr,eng,en", "jpn,ja,eng,en,fre,fra,fr");
    public static string DefaultSubtitleLanguage => L("fre,fra,fr,eng,en", "eng,en,fre,fra,fr");
    public bool HardwareDecoding { get; set; } = true;
    /// <summary>« Image » of Mira for Windows and Mac (<see cref="PictureQuality"/>): « standard », « cinema » or « max ».</summary>
    public string Picture { get; set; } = PictureQuality.Cinema;
    /// <summary>
    /// Mira for Mac: how far it went to get the picture back after mpv drew it grey (0: never; 1: screen colours off;
    /// 2: and hardware decoding; 3: drawn without OpenGL). Kept for the next playbacks until « Image » is chosen again.
    /// </summary>
    public int VideoRepair { get; set; }
    /// <summary>« Sortie audio » of Mira for Windows (<see cref="AudioOutput"/>): mpv's device name, « auto » for Windows' default.</summary>
    public string AudioDevice { get; set; } = AudioOutput.Default;
    /// <summary>The device's name as Windows gave it, shown while it is unplugged.</summary>
    public string AudioDeviceLabel { get; set; } = "";
    public string AudioChannels { get; set; } = AudioOutput.AutoChannels;
    /// <summary>« off », « optical » or « hdmi » (<see cref="AudioOutput.Codecs"/>).</summary>
    public string AudioPassthrough { get; set; } = AudioOutput.Off;
    public bool AutoNext { get; set; } = true;
    /// <summary>Openings and recaps jumped over without a press, where Jellyfin's segments or the file's chapters mark them.</summary>
    public bool AutoSkip { get; set; }
    public int SubtitleSize { get; set; } = 40;
    /// <summary>« Taille et style des sous-titres » (<see cref="SubtitleStyle"/>): a dark box behind the text, its colour
    /// (<see cref="SubtitleStyle.White"/> or <see cref="SubtitleStyle.Yellow"/>), bold, and mpv's sub-pos.</summary>
    public bool SubtitleBackground { get; set; }
    public string SubtitleColor { get; set; } = SubtitleStyle.White;
    public bool SubtitleBold { get; set; }
    public int SubtitleHeight { get; set; } = 100;
    public bool ShowProgress { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public string PosterDensity { get; set; } = "Comfortable";
    public bool RememberPosition { get; set; } = true;
    /// <summary>« Reprendre un peu avant »: seconds a title resumes before where it was left (<see cref="PlaybackSession.ResumeBack"/>); 0: exactly there.</summary>
    public int ResumeBack { get; set; } = 5;
    /// <summary>« Qualité » (<see cref="StreamQuality"/>): « auto », « max », or a bitrate in bits per second (« 6000000 »).</summary>
    public string Quality { get; set; } = StreamQuality.Auto;
    public bool HeroAutoPlay { get; set; } = true;
    /// <summary>« Accueil à ta main »: the home page's rows, in order, « - » before one put away (<see cref="HomeLayout"/>); empty: as Mira shows them.</summary>
    public string HomeRows { get; set; } = "";
    /// <summary>Checks GitHub for new versions, downloads them and installs them when Mira closes. On the Mac: asks GitHub at
    /// each launch and says when one is out (« Rechercher les nouvelles versions »).</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>
    /// « Tailscale intégré » signed in from this device: its node starts with Mira and stops with it. False (default)
    /// until the person signs in, and again once they sign out: before that, nothing goes to Tailscale.
    /// </summary>
    public bool Tailnet { get; set; }
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
    /// <summary>
    /// On the PC of Jellyfin: searches and downloads sent from Mira web by an administrator of Jellyfin
    /// (« Télécharger sur le PC serveur ») are run here, by TorLink without a window.
    /// </summary>
    public bool TorLinkRemote { get; set; } = true;
    /// <summary>
    /// The Downloads page on the PC of Jellyfin: false (default) Mira's own screen, the one of Mira web (search, best
    /// release of each title, progress); true TorLink's own interface, in its terminal.
    /// </summary>
    public bool TorLinkTerminal { get; set; }
    /// <summary>Manual library folders; empty: the folders of the Jellyfin libraries.</summary>
    public string TorLinkMoviesFolder { get; set; } = "";
    public string TorLinkSeriesFolder { get; set; } = "";
    public string TorLinkAnimeFolder { get; set; } = "";
    /// <summary>The last version whose welcome or "what's new" screen this profile went past; empty before 0.5.5.</summary>
    public string SeenVersion { get; set; } = "";
    /// <summary>The version of Mira and the Jellyfin server for which Jellyfin's repository of Mira web was last found
    /// or moved where it should be at startup (« 0.7.5 http://192.168.1.20:8096/ »); empty before 0.7.5.</summary>
    public string MiraWebUpdated { get; set; } = "";
    /// <summary>The same, once Jellyfin serves that version of Mira web or a newer one (after its restart); empty before 0.7.9.</summary>
    public string MiraWebServed { get; set; } = "";
    /// <summary>
    /// The Jellyfin version and graphics drivers (<see cref="GpuRecheck.Fingerprint"/>) for which a conversion on the card
    /// last worked, and the failed tries since they changed; empty and 0 before 0.7.9.
    /// </summary>
    public string GpuCheckedFor { get; set; } = "";
    public int GpuFailures { get; set; }
    /// <summary>
    /// The fingerprint for which Mira last tried the card by itself (Jellyfin on the processor), and whether the person
    /// put Jellyfin back on the processor (« Revenir au processeur »): then never tried by itself again.
    /// </summary>
    public string GpuTriedFor { get; set; } = "";
    public bool GpuKeptOff { get; set; }
    /// <summary>The window's size (outside fullscreen and the mini-player) and state when Mira last closed; 0: never saved.</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    /// <summary>Windows: the top-left corner of the window outside maximized and minimized, in device pixels of the
    /// virtual screen; null before 0.7.5 or when it was never saved. Checked against the screens present at startup.</summary>
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    /// <summary>The guide opened once already: it is not offered again after a connection.</summary>
    public bool GuideSeen { get; set; }
    /// <summary>Windows: a notice when an episode of a series this account follows arrives on Jellyfin (<see cref="NewEpisodes"/>).</summary>
    public bool EpisodeNotices { get; set; } = true;
    /// <summary>The account (« server user ») and Jellyfin's date of the newest episode already looked at for those notices.</summary>
    public string EpisodesSeenFor { get; set; } = "";
    public DateTimeOffset? EpisodesSeenUntil { get; set; }
    /// <summary>Windows: where « Garder sur ce PC » puts new copies (<see cref="OfflineCopies"/>); empty: « offline » in the profile.</summary>
    public string OfflineFolder { get; set; } = "";
    /// <summary>Every other folder copies were put in: choosing another leaves the earlier copies listed where they are.</summary>
    public List<string> OfflineFolders { get; set; } = [];
    /// <summary>Windows, on the PC of Jellyfin: the folder its backups are also copied to (<see cref="JellyfinBackups"/>); empty: none.</summary>
    public string JellyfinBackupFolder { get; set; } = "";
    /// <summary>When Mira first read Jellyfin's backups (the reminder's clock while there is none), and when it last reminded; null until then.</summary>
    public DateTimeOffset? JellyfinBackupWatchedSince { get; set; }
    public DateTimeOffset? JellyfinBackupRemindedAt { get; set; }
    /// <summary>
    /// The newer Jellyfin winget's catalogue gave at the last look (« 12.3 »; empty: none, <see cref="JellyfinReleases"/>),
    /// when that was, and the last one a notice said.
    /// </summary>
    public string JellyfinRelease { get; set; } = "";
    public DateTimeOffset? JellyfinReleaseCheckedAt { get; set; }
    public string JellyfinReleaseSaid { get; set; } = "";
}
