namespace Mira.Core.Discord;

/// <summary>
/// Réglages → Discord (Windows and Mac, panel A « Aperçu en direct », ogsas 2026-11-15): what the Discord app of this
/// computer may show of what plays in Mira. Kept on the device (<see cref="PlayerSettings.Discord"/>), never in the choices
/// kept on Jellyfin: another computer may have no Discord, or another person in front of it. Off until turned on.
/// </summary>
public sealed class DiscordSettings
{
    /// <summary>« Show what I'm watching on Discord »: off by default; off, Mira does not even look for Discord's pipe.</summary>
    public bool Enabled { get; set; }
    /// <summary>The one offer made at a playback's start while Discord runs: answered or put away, never made again.</summary>
    public bool Offered { get; set; }
    /// <summary><see cref="DiscordStyle.Full"/>, <see cref="DiscordStyle.NoArtwork"/> or <see cref="DiscordStyle.Discreet"/>.</summary>
    public string Style { get; set; } = DiscordStyle.Full;
    public bool ShowTitle { get; set; } = true;
    public bool ShowEpisode { get; set; } = true;
    /// <summary>The episode's own name; an episode not watched yet keeps it hidden while « Hide spoilers » is on.</summary>
    public bool ShowEpisodeName { get; set; } = true;
    /// <summary>The poster, a public address Jellyfin's metadata gives (TMDB): never the Jellyfin server's own.</summary>
    public bool ShowPoster { get; set; } = true;
    public bool ShowTime { get; set; } = true;
    /// <summary>Mira's badge on the poster (the play or pause sign with « No artwork »).</summary>
    public bool ShowBadge { get; set; } = true;
    /// <summary>The two buttons (<see cref="DiscordButton"/>): Discord shows two at most, to the others only.</summary>
    public string FirstButton { get; set; } = DiscordButton.Mira;
    public string SecondButton { get; set; } = DiscordButton.Tmdb;
    /// <summary>« Custom link… » of a button: its label (1 to 32 characters) and its https address.</summary>
    public string CustomLabel { get; set; } = "";
    public string CustomUrl { get; set; } = "";
    /// <summary>
    /// « Let friends join my party from Discord »: while watching together, the second button is « Join the party », the
    /// party's public link (Funnel only). Off by default: anyone who sees the profile can then open it.
    /// </summary>
    public bool JoinParty { get; set; }
    /// <summary>Where the title and the poster lead: <see cref="DiscordButton.Tmdb"/>, <see cref="DiscordButton.Imdb"/> or <see cref="DiscordButton.None"/>.</summary>
    public string TitleLink { get; set; } = DiscordButton.Tmdb;
    /// <summary>Each library's way (<see cref="DiscordShow"/>), by its id; a library missing here shows.</summary>
    public Dictionary<string, string> Libraries { get; set; } = [];
    /// <summary>Titles rated R, TV-MA, 16+ and above (<see cref="DiscordPresence.Mature"/>).</summary>
    public string Mature { get; set; } = DiscordShow.Discreet;
    /// <summary>« Hide on Discord » of a title's menu: never shown, by the id of the film or the series.</summary>
    public List<DiscordHiddenTitle> Hidden { get; set; } = [];
    /// <summary>Minutes a pause stays on the card as « Paused » before it goes; 0: at once, -1: as long as the player is open.</summary>
    public int PausedMinutes { get; set; } = 10;
    /// <summary>The short line under the name in the member list: <see cref="DiscordStatusLine.Title"/> (« Watching Arcane ») or <see cref="DiscordStatusLine.App"/> (« Watching Mira »).</summary>
    public string MemberLine { get; set; } = DiscordStatusLine.Title;
    /// <summary>When nothing plays: nothing (default) or « Browsing the library » while Mira is open.</summary>
    public bool ShowBrowsing { get; set; }
    /// <summary>As the guest of a friend's party (their Jellyfin, their titles): <see cref="DiscordShow.Discreet"/> by default.</summary>
    public string Guest { get; set; } = DiscordShow.Discreet;
}

/// <summary>A title kept off Discord, with its name for the list of Réglages.</summary>
public sealed record DiscordHiddenTitle(string Id, string Name);

/// <summary>The three styles of the card, which are also the presets of Réglages.</summary>
public static class DiscordStyle
{
    public const string Full = "full", NoArtwork = "no-artwork", Discreet = "discreet";
    public static readonly string[] All = [Full, NoArtwork, Discreet];
    public static string Name(string style) => style switch
    {
        NoArtwork => L("Sans affiche", "No artwork"),
        Discreet => L("Discret", "Discreet"),
        _ => L("Complet", "Full"),
    };
    public static string About(string style) => style switch
    {
        NoArtwork => L("Titre et épisode seulement", "Title and episode only"),
        Discreet => L("« Regarde une série »", "“Watching a series”"),
        _ => L("Affiche, épisode, barre de temps", "Poster, episode, time bar"),
    };
}

/// <summary>What a library, the mature titles or a friend's party show: all, only « a film » or « a series », or nothing.</summary>
public static class DiscordShow
{
    public const string Show = "show", Discreet = "discreet", Hide = "hide";
    public static readonly string[] All = [Show, Discreet, Hide];
    public static string Name(string show) => show switch
    {
        Discreet => L("Discret", "Discreet"),
        Hide => L("Masquer", "Hide"),
        _ => L("Montrer", "Show"),
    };
    /// <summary>The stricter of two ways: hide before discreet before show.</summary>
    public static string Stricter(string a, string b) => Rank(a) >= Rank(b) ? Normal(a) : Normal(b);
    public static string Normal(string? show) => show is Discreet or Hide ? show : Show;
    private static int Rank(string? show) => show switch { Hide => 2, Discreet => 1, _ => 0 };
}

/// <summary>The line of the member list: the title (status_display_type 2, « details ») or Mira's name (0).</summary>
public static class DiscordStatusLine
{
    public const string Title = "title", App = "app";
}

/// <summary>The buttons a card may carry, two at most (Discord's limit), each a plain https page without any tracking.</summary>
public static class DiscordButton
{
    public const string None = "none", Mira = "mira", Tmdb = "tmdb", Imdb = "imdb", Trailer = "trailer", GitHub = "github", Community = "discord", Custom = "custom";
    public static readonly string[] All = [None, Mira, Tmdb, Imdb, Trailer, GitHub, Community, Custom];
    public const string Site = "https://miracinema.app/";
    public const string Repository = "https://github.com/sasou-web/Mira";
    public const string Server = "https://discord.gg/Bh3F6PDUvB";
    /// <summary>The name in the list of Réglages.</summary>
    public static string Name(string button) => button switch
    {
        None => L("Aucun", "None"),
        Mira => Label(Mira),
        Tmdb => L("Voir sur TMDB", "View on TMDB"),
        Imdb => L("Voir sur IMDb", "View on IMDb"),
        Trailer => L("Voir la bande-annonce", "Watch the trailer"),
        GitHub => L("Mira sur GitHub", "Mira on GitHub"),
        Community => L("Rejoindre le Discord de Mira", "Join Mira’s Discord"),
        Custom => L("Lien personnalisé…", "Custom link…"),
        _ => button,
    };
    /// <summary>Where it leads, as the list says it.</summary>
    public static string Hint(string button) => button switch
    {
        Mira => "miracinema.app",
        Tmdb or Imdb => L("la page du titre", "this title’s page"),
        Trailer => "YouTube",
        _ => "",
    };
    /// <summary>The label on the card (1 to 32 characters).</summary>
    public static string Label(string button) => button switch
    {
        Mira => L("Obtenir Mira", "Get Mira"),
        _ => Name(button),
    };
}
