namespace Mira.Core.Discord;

/// <summary>
/// What Réglages → Discord shows on its left (panel A « Aperçu en direct »): the card friends would see, for what plays
/// now or for an example of each tab (episode, film, paused, party), with the same rules as the real one
/// (<see cref="DiscordPresence.Card"/>). The examples are labelled as such in Réglages and never sent to Discord.
/// </summary>
public static class DiscordPreview
{
    public const string Episode = "episode", Film = "film", Paused = "paused", Party = "party";
    public static readonly string[] Tabs = [Episode, Film, Paused, Party];

    public static string Name(string tab) => tab switch
    {
        Film => L("Film", "Film"),
        Paused => L("En pause", "Paused"),
        Party => L("Party", "Party"),
        _ => L("Épisode", "Episode"),
    };

    /// <summary>An example party link, in the shape <see cref="DiscordPresence.PartyButton"/> accepts (never a real server).</summary>
    public const string ExampleParty = "https://maison-pc.tail0000.ts.net/Mira/party/example";

    /// <summary>The example playback of a tab: a series' episode, or a film, half watched.</summary>
    public static DiscordPlayback Example(string tab)
    {
        if (tab == Film)
        {
            var film = new MediaItem { Id = "example-film", Type = "Movie", Name = "Suzume", ProductionYear = 2022, Genres = ["Animation", "Adventure"] };
            var facts = new DiscordTitle("example-film", "Suzume", "Movie", 2022, ["Animation", "Adventure", "Fantasy"], "PG", null, null, "916224", "tt16428256", null);
            return new(film, facts, false, 2890, 7334, 1);
        }
        var episode = new MediaItem { Id = "example-episode", Type = "Episode", Name = "The Base Violence Necessary for Change", SeriesName = "Arcane", SeriesId = "example-series", IndexNumber = 3, ParentIndexNumber = 1 };
        var series = new DiscordTitle("example-series", "Arcane", "Series", 2021, ["Animation", "Action & Adventure"], "TV-14", null, null, "94605", "tt11126994", "https://www.youtube.com/watch?v=fXmAurh012s");
        return tab switch
        {
            Paused => new(episode, series, true, 767, 2412, 1),
            Party => new(episode, series, false, 767, 2412, 1, InGroup: true, Friends: 2, PartyLink: ExampleParty),
            _ => new(episode, series, false, 767, 2412, 1),
        };
    }

    /// <summary>
    /// The card of the preview: the settings as if turned on (what friends would see once it is), a pause never gone.
    /// An example has a poster when the settings show one (drawn from Mira's own pictures by the app, never fetched).
    /// </summary>
    public static DiscordCard? Card(DiscordSettings settings, DiscordPlayback playback, DateTimeOffset now, string app)
    {
        var shown = Copy(settings); shown.Enabled = true;
        if (shown.PausedMinutes >= 0) shown.PausedMinutes = int.MaxValue / 2;
        var example = playback.Item.Id.StartsWith("example-", StringComparison.Ordinal);
        if (example && playback.Title is { } title) playback = playback with { Title = title with { Poster = "https://image.tmdb.org/t/p/w500/example.jpg" } };
        // The examples' libraries are none of this server's: the « Movies » or « Shows » way does not apply to them.
        return DiscordPresence.Card(shown, playback, now, TimeSpan.Zero, app);
    }

    /// <summary>The short line of the member list under the name: « Watching Arcane », or « Watching Mira ».</summary>
    public static string MemberLine(DiscordCard card) => card.ShowsTitle ? L($"Regarde {card.Details}", $"Watching {card.Details}") : L("Regarde Mira", "Watching Mira");

    /// <summary>A style chosen in Réglages: its preset of the card's switches and buttons (each can be changed after).</summary>
    public static void Apply(DiscordSettings settings, string style)
    {
        settings.Style = style;
        switch (style)
        {
            case DiscordStyle.NoArtwork:
                settings.ShowTitle = settings.ShowEpisode = settings.ShowTime = settings.ShowBadge = true;
                settings.ShowPoster = settings.ShowEpisodeName = false;
                break;
            case DiscordStyle.Discreet:
                // The card itself says only « Watching a series »: the switches stay as they were for the day it changes back.
                break;
            default:
                settings.ShowTitle = settings.ShowEpisode = settings.ShowEpisodeName = settings.ShowPoster = settings.ShowTime = settings.ShowBadge = true;
                if (settings.FirstButton == DiscordButton.None) settings.FirstButton = DiscordButton.Mira;
                break;
        }
    }

    /// <summary>The choices of « When I pause », in minutes (-1: as long as the player is open).</summary>
    public static readonly int[] PauseChoices = [0, 5, 10, 30, -1];
    public static string PauseName(int minutes) => minutes switch
    {
        0 => L("Effacer tout de suite", "Clear it at once"),
        < 0 => L("Garder « En pause » tant que le lecteur est ouvert", "Keep “Paused” while the player is open"),
        _ => L($"Montrer « En pause » {minutes} min", $"Show “Paused” for {minutes} min"),
    };

    /// <summary>A copy to change without touching the settings themselves (the preview's « as if on »).</summary>
    public static DiscordSettings Copy(DiscordSettings s) => new()
    {
        Enabled = s.Enabled, Offered = s.Offered, Style = s.Style, ShowTitle = s.ShowTitle, ShowEpisode = s.ShowEpisode, ShowEpisodeName = s.ShowEpisodeName,
        ShowPoster = s.ShowPoster, ShowTime = s.ShowTime, ShowBadge = s.ShowBadge, FirstButton = s.FirstButton, SecondButton = s.SecondButton,
        CustomLabel = s.CustomLabel, CustomUrl = s.CustomUrl, JoinParty = s.JoinParty, TitleLink = s.TitleLink, Libraries = new(s.Libraries),
        Mature = s.Mature, Hidden = [.. s.Hidden], PausedMinutes = s.PausedMinutes, MemberLine = s.MemberLine, ShowBrowsing = s.ShowBrowsing, Guest = s.Guest,
    };

    /// <summary>The line under the switch: where things stand with Discord.</summary>
    public static string StateLine(DiscordState state, string? error, bool mac = false) => state switch
    {
        DiscordState.Off => L("Coupé : Mira ne cherche même pas Discord.", "Off: Mira doesn’t even look for Discord."),
        DiscordState.HiddenForNow => L("Masqué pour l’instant : rien sur ton profil jusqu’à ce que tu le remettes.", "Hidden for now: nothing on your profile until you turn it back on."),
        DiscordState.Live => mac ? L("Discord est ouvert sur ce Mac · ton statut est en direct", "Discord is open on this Mac · your status is live") : L("Discord est ouvert sur ce PC · ton statut est en direct", "Discord is open on this PC · your status is live"),
        DiscordState.Looking => error is { Length: > 0 }
            ? L($"Discord n’a pas pris la carte : {error}", $"Discord didn’t take the card: {error}")
            : mac ? L("Discord n’est pas ouvert sur ce Mac : la carte part dès qu’il l’est.", "Discord isn’t open on this Mac: the card goes as soon as it is.")
            : L("Discord n’est pas ouvert sur ce PC : la carte part dès qu’il l’est.", "Discord isn’t open on this PC: the card goes as soon as it is."),
        _ => L("Prêt : ta carte apparaît pendant une lecture.", "Ready: your card shows while something plays."),
    };
}
