using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mira.Core.Discord;

/// <summary>
/// The film or series a playback belongs to, as Mira reads it once per title (<see cref="DiscordTitles"/>): its name and
/// facts, the library it is in, and the public addresses the card may carry. <see cref="Poster"/> is the address
/// Jellyfin's metadata gives (image.tmdb.org…), never the Jellyfin server's own.
/// </summary>
public sealed record DiscordTitle(string Id, string Name, string Type, int? Year, string[] Genres, string? Rating, string? LibraryId,
    string? Poster, string? Tmdb, string? Imdb, string? Trailer);

/// <summary>What plays, as the card is made of it.</summary>
/// <param name="Item">The episode or the film playing.</param>
/// <param name="Title">Its film or series once read; null until then (the card waits with the item's own facts).</param>
/// <param name="Friends">The others in the group while watching together.</param>
/// <param name="PartyLink">This account's party link when it reaches the internet (Funnel); null otherwise.</param>
/// <param name="Guest">The guest of a friend's party: their Jellyfin, their titles.</param>
/// <param name="Trailer">A trailer before the film: never on Discord.</param>
public sealed record DiscordPlayback(MediaItem Item, DiscordTitle? Title, bool Paused, double Position, double Duration, double Rate,
    bool InGroup = false, int Friends = 0, string? PartyLink = null, bool Guest = false, bool Trailer = false, bool HideSpoilers = false);

/// <summary>One button of the card.</summary>
public sealed record DiscordLink(string Label, string Url);

/// <summary>
/// The card as friends see it: Discord's own heading (« Watching Mira »), then <see cref="Details"/> and
/// <see cref="State"/>, the bar of <see cref="Start"/> to <see cref="End"/>, the large and small pictures, two buttons.
/// The same record draws the preview of Réglages.
/// </summary>
public sealed record DiscordCard(string Details, string? State, string? DetailsUrl, DateTimeOffset? Start, DateTimeOffset? End,
    string LargeImage, string? LargeText, string? LargeUrl, string? SmallImage, string? SmallText, IReadOnlyList<DiscordLink> Buttons,
    bool ShowsTitle, bool Paused, bool HasPoster)
{
    /// <summary>The activity sent by SET_ACTIVITY: Watching (3), times in milliseconds (Discord reads seconds too, by their size).</summary>
    public JsonObject Activity()
    {
        var activity = new JsonObject
        {
            ["type"] = 3,
            // The member list's short line: « Watching Arcane » (details) or « Watching Mira » (the app's name).
            ["status_display_type"] = ShowsTitle ? 2 : 0,
            ["details"] = Details,
        };
        if (DetailsUrl is not null) activity["details_url"] = DetailsUrl;
        if (State is not null) activity["state"] = State;
        if (Start is { } start && End is { } end)
            activity["timestamps"] = new JsonObject { ["start"] = start.ToUnixTimeMilliseconds(), ["end"] = end.ToUnixTimeMilliseconds() };
        var assets = new JsonObject { ["large_image"] = LargeImage };
        if (LargeText is not null) assets["large_text"] = LargeText;
        if (LargeUrl is not null) assets["large_url"] = LargeUrl;
        if (SmallImage is not null) assets["small_image"] = SmallImage;
        if (SmallText is not null) assets["small_text"] = SmallText;
        activity["assets"] = assets;
        if (Buttons.Count > 0) activity["buttons"] = new JsonArray(Buttons.Select(x => (JsonNode)new JsonObject { ["label"] = x.Label, ["url"] = x.Url }).ToArray());
        activity["instance"] = false;
        return activity;
    }
}

/// <summary>
/// The Rich Presence of Mira (Windows and Mac; the iPhone has none, Discord's RPC does not exist on iOS): what plays,
/// and the settings of Réglages → Discord, made into the card the Discord app of this computer shows on the profile.
/// Pure rules, shared by both apps (cases in tests/fixtures/discord.json). Mira opens no connection for it: the card goes
/// to the Discord app through its local pipe (<see cref="DiscordIpc"/>), and Discord fetches the poster itself.
/// Nothing that names the server goes: its address, a Tailscale address, the account, the PC (<see cref="Leaks"/>).
/// </summary>
public static class DiscordPresence
{
    /// <summary>Discord's application « Mira » (its developer portal; public by nature): the heading's name and icon.</summary>
    public const string ApplicationId = "1571521751660241077";
    /// <summary>Mira's icon and play and pause signs, served by miracinema.app (site/img): Discord fetches them, never the viewer.</summary>
    public const string Icon = "https://miracinema.app/img/icon-512.png";
    public const string PlaySign = "https://miracinema.app/img/discord/play.png";
    public const string PauseSign = "https://miracinema.app/img/discord/pause.png";
    /// <summary>The only hosts a poster may come from: the metadata sources' public image servers, in https.</summary>
    public static readonly string[] PosterHosts = ["image.tmdb.org", "artworks.thetvdb.com", "www.thetvdb.com", "s4.anilist.co"];
    private const int TextMost = 128;

    /// <summary>The card for <paramref name="playback"/>, or null when nothing shows (off, hidden, a trailer, a pause gone on too long).</summary>
    /// <param name="pausedFor">How long it has been paused.</param>
    /// <param name="app">« Windows » or « Mac », said by the badge.</param>
    public static DiscordCard? Card(DiscordSettings settings, DiscordPlayback playback, DateTimeOffset now, TimeSpan pausedFor, string app)
    {
        if (playback.Paused && settings.PausedMinutes >= 0 && pausedFor >= TimeSpan.FromMinutes(settings.PausedMinutes)) return null;
        var way = Way(settings, playback);
        if (way == DiscordShow.Hide) return null;
        var series = playback.Item.Type == "Episode" || playback.Title?.Type == "Series";
        if (way == DiscordShow.Discreet) return Discreet(settings, playback, series);

        var title = playback.Title;
        var name = title?.Name ?? playback.Item.DisplayTitle;
        var showTitle = settings.ShowTitle && name.Length > 0;
        var parts = new List<string>();
        if (playback.Paused) parts.Add(L("En pause", "Paused"));
        if (series)
        {
            var item = playback.Item;
            if (settings.ShowEpisode && item.Type == "Episode")
                parts.Add(item.IndexNumber is { } episode ? L($"S{item.ParentIndexNumber ?? 1} · É{episode}", $"S{item.ParentIndexNumber ?? 1} · E{episode}") : L("Épisode", "Episode"));
            if (showTitle && settings.ShowEpisodeName && item.Type == "Episode" && item.Name.Length > 0 && !(playback.HideSpoilers && Spoilers.Hides(item, true)) && !playback.Paused && playback.Friends <= 0)
                parts.Add(item.Name);
        }
        else
        {
            if ((title?.Year ?? playback.Item.ProductionYear) is { } year) parts.Add(year.ToString(CultureInfo.InvariantCulture));
            var genres = title?.Genres ?? playback.Item.Genres;
            if (!playback.Paused && playback.Friends <= 0 && genres.Length > 0) parts.Add(string.Join(", ", genres.Take(2)));
        }
        if (playback.Friends > 0) { var friends = Lang.Count(playback.Friends, "ami", "amis", "friend", "friends"); parts.Add(L($"avec {friends}", $"with {friends}")); }
        var state = parts.Count > 0 ? Fit(string.Join(" · ", parts)) : null;

        var link = showTitle && title is not null ? TitleLink(settings.TitleLink, title) : null;
        var poster = showTitle && settings.ShowPoster ? Poster(title?.Poster) : null;
        DateTimeOffset? start = null, end = null;
        if (settings.ShowTime && !playback.Paused && double.IsFinite(playback.Duration) && playback.Duration > 0)
        {
            var rate = double.IsFinite(playback.Rate) && playback.Rate > 0 ? playback.Rate : 1;
            var position = Math.Clamp(double.IsFinite(playback.Position) ? playback.Position : 0, 0, playback.Duration);
            // To the second: the same playback gives the same times, tick after tick.
            var at = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
            start = at - TimeSpan.FromSeconds(Math.Round(position / rate)); end = at + TimeSpan.FromSeconds(Math.Round((playback.Duration - position) / rate));
        }
        string? small = null, smallText = null;
        if (settings.ShowBadge)
        {
            small = playback.Paused ? PauseSign : poster is not null ? Icon : PlaySign;
            smallText = playback.Paused ? L("En pause", "Paused") : L($"Mira pour {app}", $"Mira for {app}");
        }
        var shownYear = title?.Year ?? (series ? null : playback.Item.ProductionYear);
        var largeText = showTitle ? Fit(shownYear is { } y ? $"{name} ({y})" : name) : "Mira";
        return new(showTitle ? Fit(name) : Generic(series), state, link, start, end, poster ?? Icon, largeText, poster is not null ? link : null,
            small, smallText, Buttons(settings, playback, title, showTitle), showTitle && settings.MemberLine == DiscordStatusLine.Title, playback.Paused, poster is not null);
    }

    /// <summary>« Browsing the library » while Mira is open and nothing plays, when asked for; null otherwise.</summary>
    public static DiscordCard? Browsing(DiscordSettings settings) => settings.ShowBrowsing
        ? new(L("Parcourt sa bibliothèque", "Browsing the library"), null, null, null, null, Icon, "Mira", null, null, null, Buttons(settings, null, null, false), false, false, false)
        : null;

    /// <summary>« Watching a film » or « a series », nothing of the title: neither poster, nor time, nor a link that names it.</summary>
    private static DiscordCard Discreet(DiscordSettings settings, DiscordPlayback playback, bool series)
    {
        var state = playback.Paused ? L("En pause", "Paused") : playback.InGroup || playback.Friends > 0 ? L("Avec des amis", "With friends") : null;
        return new(Generic(series), state, null, null, null, Icon, "Mira", null, null, null, Buttons(settings, playback, null, false), false, playback.Paused, false);
    }

    private static string Generic(bool series) => series ? L("Regarde une série", "Watching a series") : L("Regarde un film", "Watching a film");

    /// <summary>
    /// Show, discreet or hide for this playback: a trailer and a title hidden never show; a friend's party follows
    /// « As a guest »; otherwise its library's way, made stricter by « Mature titles » for a mature rating; then the style.
    /// </summary>
    public static string Way(DiscordSettings settings, DiscordPlayback playback)
    {
        if (!settings.Enabled || playback.Trailer) return DiscordShow.Hide;
        var ids = new[] { playback.Title?.Id, playback.Item.SeriesId, playback.Item.Id }.OfType<string>();
        if (settings.Hidden.Any(x => ids.Contains(x.Id))) return DiscordShow.Hide;
        string way;
        if (playback.Guest) way = DiscordShow.Normal(settings.Guest);
        else
        {
            way = playback.Title?.LibraryId is { } library ? DiscordShow.Normal(settings.Libraries.GetValueOrDefault(library)) : DiscordShow.Show;
            if (Mature(playback.Title?.Rating ?? playback.Item.OfficialRating)) way = DiscordShow.Stricter(way, settings.Mature);
        }
        if (settings.Style == DiscordStyle.Discreet) way = DiscordShow.Stricter(way, DiscordShow.Discreet);
        return way;
    }

    private static readonly Regex Age = new(@"(\d{2})\s*\+?$", RegexOptions.CultureInvariant);
    /// <summary>
    /// A rating for adults: R, NC-17, TV-MA, X, or an age of 16 and above in any country's form (« 16 », « FR-16 », « -18 »,
    /// « FSK-18 », « R18+ », « 16+ »).
    /// </summary>
    public static bool Mature(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating)) return false;
        var r = rating.Trim().ToUpperInvariant();
        if (r is "R" or "NC-17" or "NC17" or "TV-MA" or "X" or "XXX" or "AO" or "NR-17" || r.EndsWith("-R", StringComparison.Ordinal) || r.EndsWith("-TV-MA", StringComparison.Ordinal) || r.EndsWith("-NC-17", StringComparison.Ordinal)) return true;
        return Age.Match(r) is { Success: true } m && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is >= 16 and <= 21;
    }

    /// <summary>A poster's address if it is a public https picture of a metadata source (<see cref="PosterHosts"/>), TMDB's at 500 px; null otherwise.</summary>
    public static string? Poster(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return null;
        if (!PosterHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || uri.AbsoluteUri.Length > 300) return null;
        var address = uri.AbsoluteUri;
        if (uri.Host.Equals("image.tmdb.org", StringComparison.OrdinalIgnoreCase)) address = Regex.Replace(address, "/t/p/(original|w\\d+)/", "/t/p/w500/", RegexOptions.CultureInvariant);
        return address;
    }

    /// <summary>The title's page on TMDB or IMDb, from the ids Jellyfin matched it with.</summary>
    public static string? TitleLink(string where, DiscordTitle title) => where switch
    {
        DiscordButton.Tmdb when title.Tmdb is { } id && id.All(char.IsAsciiDigit) && id.Length is > 0 and < 12 => $"https://www.themoviedb.org/{(title.Type == "Series" ? "tv" : "movie")}/{id}",
        DiscordButton.Imdb when title.Imdb is { } id && Regex.IsMatch(id, "^tt\\d{5,10}$", RegexOptions.CultureInvariant) => $"https://www.imdb.com/title/{id}/",
        _ => null,
    };

    /// <summary>
    /// The card's buttons: « Join the party » first while watching together when allowed (Funnel's public link only), then
    /// those chosen, each skipped when it has nowhere to lead; two at most, never twice the same page. Without the title
    /// shown, only buttons that do not name it.
    /// </summary>
    public static IReadOnlyList<DiscordLink> Buttons(DiscordSettings settings, DiscordPlayback? playback, DiscordTitle? title, bool showTitle)
    {
        var links = new List<DiscordLink>();
        if (settings.JoinParty && playback is { InGroup: true, Guest: false } && PartyButton(playback.PartyLink) is { } party) links.Add(party);
        foreach (var button in new[] { settings.FirstButton, settings.SecondButton })
        {
            if (links.Count == 2) break;
            if (Button(button, showTitle ? title : null) is { } link && links.All(x => x.Url != link.Url)) links.Add(link);
        }
        return links;
    }

    private static DiscordLink? Button(string button, DiscordTitle? title) => button switch
    {
        DiscordButton.Mira => new(DiscordButton.Label(button), DiscordButton.Site),
        DiscordButton.GitHub => new(DiscordButton.Label(button), DiscordButton.Repository),
        DiscordButton.Community => new(DiscordButton.Label(button), DiscordButton.Server),
        DiscordButton.Tmdb or DiscordButton.Imdb when title is not null && TitleLink(button, title) is { } page => new(DiscordButton.Label(button), page),
        DiscordButton.Trailer when title?.Trailer is { } trailer && Trailers.WebLink(trailer) is { } uri && uri.AbsoluteUri.Length <= 512 => new(DiscordButton.Label(button), uri.AbsoluteUri),
        _ => null,
    };

    /// <summary>« Join the party »: the party's link only when it is Funnel's public https address, never a home or Tailscale one.</summary>
    public static DiscordLink? PartyButton(string? link)
    {
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.AbsoluteUri.Length > 512) return null;
        if (!uri.Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith("/Mira/party/", StringComparison.Ordinal)) return null;
        return new(L("Rejoindre la party", "Join the party"), uri.AbsoluteUri);
    }

    /// <summary>A line Discord takes: 2 to 128 characters.</summary>
    private static string Fit(string text)
    {
        text = text.Trim();
        if (text.Length < 2) text = $"“{text}”";
        return text.Length <= TextMost ? text : text[..(TextMost - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// What in <paramref name="activity"/> would name the server or this person: the server's host, its address, a
    /// Tailscale address (100.64.0.0/10, .ts.net), a private or loopback address, the account's or the PC's name in an address. The
    /// party's own button (<paramref name="allowed"/>, Funnel's public link) is the one exception. Empty: it may go.
    /// </summary>
    public static IReadOnlyList<string> Leaks(JsonNode activity, IEnumerable<string?> names, string? allowed = null)
    {
        var found = new List<string>();
        var forbidden = names.OfType<string>().Select(x => x.Trim()).Where(x => x.Length >= 3).ToList();
        foreach (var text in Strings(activity))
        {
            if (allowed is not null && text == allowed) continue;
            // In an address only: a title may well hold a word that is also someone's name.
            if (text.Contains("://", StringComparison.Ordinal))
                foreach (var name in forbidden) if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) found.Add($"« {name} » in « {text} »");
            if (text.Contains(".ts.net", StringComparison.OrdinalIgnoreCase)) found.Add($"Tailscale name in « {text} »");
            foreach (Match m in Regex.Matches(text, @"\b\d{1,3}(\.\d{1,3}){3}\b", RegexOptions.CultureInvariant))
                if (IPAddress.TryParse(m.Value, out var ip) && Private(ip)) found.Add($"address {m.Value} in « {text} »");
            if (Regex.IsMatch(text, @"\blocalhost\b|\[[0-9a-f:]+\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) found.Add($"local address in « {text} »");
        }
        return found;
    }

    private static bool Private(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o: foreach (var (_, v) in o) foreach (var s in Strings(v)) yield return s; break;
            case JsonArray a: foreach (var v in a) foreach (var s in Strings(v)) yield return s; break;
            case JsonValue v when v.TryGetValue<string>(out var s): yield return s; break;
        }
    }
}
