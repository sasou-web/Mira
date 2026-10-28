using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Mira.Jellyfin;

/// <summary>
/// Mira web on Jellyfin: the plugin only declares itself; <see cref="WebController"/> serves the app at /Mira, where
/// phones open it and add it to their home screen. It has no settings (Jellyfin's base class, which records the
/// plugin's version, comes with an empty configuration). What it keeps on the server: the guests' links of « Inviter
/// par lien » (<see cref="GuestInvites"/>), in one file beside the plugins' settings, and their accounts on Jellyfin;
/// the series followed of « Suivre une série » (<see cref="TorLinkRelay"/>), in another file there.
/// </summary>
public sealed class Plugin : BasePlugin<BasePluginConfiguration>
{
    public static readonly Guid PluginId = new("4ea89259-3350-45b0-8045-f1ab627214dd");

    private static readonly object Gate = new();
    private static string? _invitesFile;
    private static GuestInvites? _invites;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        // Beside the plugins' settings: an update of the plugin (another folder) keeps the links.
        _invitesFile = Path.Combine(applicationPaths.PluginConfigurationsPath, "Mira.invites.json");
        TorLinkRelay.FollowsFile = Path.Combine(applicationPaths.PluginConfigurationsPath, "Mira.follows.json");
        _subtitlesFile = Path.Combine(applicationPaths.PluginConfigurationsPath, "Mira.subtitles.json");
        _journalFile = Path.Combine(applicationPaths.PluginConfigurationsPath, "Mira.subtitles-journal.json");
    }

    private static string? _subtitlesFile, _journalFile;
    private static SubtitleConfig? _subtitles;
    private static SubtitleJournal? _journal;

    /// <summary>The settings of « Sous-titres automatiques », read from their file the first time they are needed.</summary>
    public static SubtitleConfig Subtitles
    {
        get { lock (Gate) return _subtitles ??= new SubtitleConfig(_subtitlesFile); }
    }

    /// <summary>What « Sous-titres automatiques » did lately.</summary>
    public static SubtitleJournal SubtitleJournal
    {
        get { lock (Gate) return _journal ??= new SubtitleJournal(_journalFile); }
    }

    /// <summary>The guests' links, read from their file the first time they are needed.</summary>
    public static GuestInvites Invites
    {
        get
        {
            lock (Gate)
            {
                return _invites ??= new GuestInvites(_invitesFile, () => DateTimeOffset.UtcNow);
            }
        }
    }

    public override string Name => "Mira";
    public override Guid Id => PluginId;
    public override string Description =>
        "Mira on iPhone, iPad and Android: open http://<server address>:8096/Mira in the browser, then add the page to the Home Screen.";
}
