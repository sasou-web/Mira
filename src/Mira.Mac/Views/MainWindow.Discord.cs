using Avalonia.Threading;
using Mira.Core;
using Mira.Core.Discord;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// The Rich Presence of Discord, as on Windows (MainWindow.Discord.cs there; rules in Core): what plays shown on the
/// person's Discord profile by the Discord app of this Mac, through its local socket, never a connection of Mira's own.
/// Off until turned on, offered once after a playback while Discord runs. The player tells it at each tick
/// (<see cref="TellDiscord"/>); Réglages → Discord (SettingsPage.Discord.cs) chooses what shows. Never for the checks.
/// </summary>
public sealed partial class MainWindow
{
    private DiscordStatus? _discord;
    private readonly DiscordTitles _discordTitles = new();
    private DiscordTitle? _discordTitle;
    private string? _discordTitleFor;
    private object? _discordClient;
    private string? _discordParty;
    private object? _discordPartyFor;
    private DateTime _discordAt;
    private string? _discordSent, _discordWithheld;
    private volatile string?[] _discordPrivate = [Environment.MachineName];
    private List<LibrarySection> _discordLibraries = [];
    private object? _discordLibrariesFor;

    /// <summary>Raised on the UI thread when the card, the pipe or a setting changed: Réglages → Discord follows.</summary>
    public event Action? DiscordShown;
    private bool DiscordAllowed => !IsCheckRun && !_closing;
    public DiscordState DiscordState => !Settings.Discord.Enabled ? DiscordState.Off : _discord?.State ?? DiscordState.Idle;
    public string? DiscordError => _discord?.Error;
    public bool DiscordHiddenForNow => _discord?.HiddenForNow == true;
    /// <summary>This server's libraries, for « Who sees what » (read once per account).</summary>
    public IReadOnlyList<LibrarySection> DiscordLibraries => _discordLibraries;

    /// <summary>What plays told to Discord: each tick of the player (at most once a second unless something changed), its start and its end.</summary>
    public void TellDiscord(bool force = false)
    {
        if (!DiscordAllowed) return;
        var settings = Settings.Discord;
        if (_discord is null)
        {
            if (!settings.Enabled) return;
            _discord = new DiscordStatus("Mac") { Private = () => _discordPrivate };
            _discord.Changed += () => Dispatcher.UIThread.Post(DiscordChanged);
        }
        var playback = DiscordPlaying();
        var key = playback is null ? "" : $"{playback.Item.Id}|{playback.Paused}|{playback.Friends}|{playback.Title is not null}|{playback.PartyLink}";
        var now = DateTime.UtcNow;
        if (!force && key == _discordSent && now - _discordAt < TimeSpan.FromSeconds(1)) return;
        _discordSent = key; _discordAt = now;
        var names = new List<string?> { Environment.MachineName, Environment.UserName };
        if (Session?.Connection is { } connection)
        {
            names.Add(connection.UserName);
            if (Uri.TryCreate(connection.Server, UriKind.Absolute, out var server)) names.Add(server.Host);
        }
        _discordPrivate = [.. names];
        _discord.Update(settings, playback, DateTimeOffset.UtcNow);
    }

    /// <summary>The playback as the card is made of it; null when nothing plays.</summary>
    public DiscordPlayback? DiscordPlaying()
    {
        if (Player?.DiscordNow is not { } now || Session is not { } session) return null;
        var settings = Settings.Discord;
        if (!ReferenceEquals(_discordClient, session)) { _discordClient = session; _discordTitles.Clear(); _discordTitleFor = null; _discordParty = null; _discordPartyFor = null; }
        _ = LoadDiscordLibrariesAsync();
        var titleKey = now.Item.Id + (settings.ShowPoster ? "+poster" : "");
        if (_discordTitleFor != titleKey) { _discordTitleFor = titleKey; _discordTitle = null; _ = ReadDiscordTitleAsync(session, now.Item, titleKey, settings.ShowPoster); }
        var inGroup = Group.Active && Group.Group is not null;
        var friends = inGroup ? Math.Max(0, Group.Group!.Participants.Count - 1) : 0;
        if (inGroup && settings.JoinParty && !InParty) _ = ReadDiscordPartyAsync(session);
        return new(now.Item, _discordTitle, now.Paused, now.Position, now.Duration, now.Rate, inGroup, friends,
            inGroup && settings.JoinParty && !InParty ? _discordParty : null, InParty, now.Trailer, Settings.HideSpoilers);
    }

    /// <summary>This account's libraries, once per session (the card's library, Réglages' « Who sees what »).</summary>
    public async Task LoadDiscordLibrariesAsync()
    {
        if (Session is not { } session || ReferenceEquals(_discordLibrariesFor, session)) return;
        _discordLibrariesFor = session;
        try
        {
            var libraries = await session.Client.LibrariesAsync(session.Lifetime);
            if (!ReferenceEquals(session, Session)) return;
            _discordLibraries = [.. libraries.Where(x => !LibrarySection.Groups(x.CollectionType))];
            DiscordShown?.Invoke();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _discordLibrariesFor = null; }
    }

    private async Task ReadDiscordTitleAsync(Session session, MediaItem item, string key, bool poster)
    {
        try
        {
            var title = await _discordTitles.ReadAsync(session.Client, item, _discordLibraries.Select(x => x.Id).ToList(), poster);
            if (_discordTitleFor != key || _closing) return;
            _discordTitle = title; TellDiscord(force: true); DiscordShown?.Invoke();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }

    /// <summary>This account's party link, once per session: only one that reaches the internet (Funnel) may go on a card.</summary>
    private async Task ReadDiscordPartyAsync(Session session)
    {
        if (ReferenceEquals(_discordPartyFor, session)) return;
        _discordPartyFor = session; _discordParty = null;
        try
        {
            var party = await session.Client.PartyAsync(ct: session.Lifetime);
            if (!ReferenceEquals(_discordPartyFor, session)) return;
            _discordParty = party is { Everywhere: true } ? DiscordPresence.PartyButton(party.Link)?.Url : null;
            TellDiscord(force: true);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }

    private void DiscordChanged()
    {
        if (_closing || _discord is not { } discord) return;
        if (discord.Withheld.Count > 0 && string.Join("; ", discord.Withheld) is var withheld && withheld != _discordWithheld)
        {
            _discordWithheld = withheld;
            ErrorLog.Note(Profile.DirectoryPath, "Discord: a card naming the server or this Mac was held back: " + withheld);
        }
        DiscordShown?.Invoke();
    }

    /// <summary>« Discord status » of the player's ⋮ and Réglages: hidden for now, or shown again (until Mira quits).</summary>
    public void ToggleDiscordForNow()
    {
        if (!Settings.Discord.Enabled || !DiscordAllowed) return;
        TellDiscord(force: true);
        if (_discord is not { } discord) return;
        discord.HiddenForNow = !discord.HiddenForNow;
        TellDiscord(force: true);
        DiscordShown?.Invoke();
    }

    /// <summary>A choice of Réglages → Discord or a menu: saved, the card follows at once.</summary>
    public void DiscordSettingChanged()
    {
        Profile.SaveSettings(Settings);
        if (!Settings.Discord.Enabled && _discord is { } discord) discord.HiddenForNow = false;
        _discordTitleFor = null;
        TellDiscord(force: true);
        DiscordShown?.Invoke();
    }

    /// <summary>« Hide on Discord » of a title's « … »: films and series, while the card is on.</summary>
    public bool DiscordHideable(MediaItem item) => Settings.Discord.Enabled && item.Type is "Movie" or "Series";
    public bool HiddenOnDiscord(MediaItem item) => Settings.Discord.Hidden.Any(x => x.Id == item.Id);
    public void HideOnDiscord(MediaItem item, bool hide)
    {
        var list = Settings.Discord.Hidden;
        list.RemoveAll(x => x.Id == item.Id);
        if (hide) list.Add(new(item.Id, item.Name));
        DiscordSettingChanged();
    }

    /// <summary>The one notice, after a playback while Discord runs on this Mac: the card is on by default, said once.</summary>
    private void OfferDiscord()
    {
        var settings = Settings.Discord;
        if (!settings.Enabled || settings.Offered || !DiscordAllowed || InParty || !DiscordIpc.Available()) return;
        settings.Offered = true; Profile.SaveSettings(Settings);
        Notice(DiscordPreview.Told, L("Réglages", "Settings"), () => { SettingsPage.OpenAt("discord"); OpenSheet(); }, sticky: true);
    }

    private async Task CloseDiscordAsync()
    {
        if (_discord is not { } discord) return;
        _discord = null;
        await discord.DisposeAsync();
    }
}
