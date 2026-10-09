using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Mira.Jellyfin;

/// <summary>
/// Mira web on Jellyfin: the plugin only declares itself; <see cref="WebController"/> serves the app at /Mira, where
/// phones open it and add it to their home screen. It reads and changes nothing on the server, and has no settings
/// (Jellyfin's base class, which records the plugin's version, comes with an empty configuration).
/// </summary>
public sealed class Plugin : BasePlugin<BasePluginConfiguration>
{
    public static readonly Guid PluginId = new("4ea89259-3350-45b0-8045-f1ab627214dd");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "Mira";
    public override Guid Id => PluginId;
    public override string Description =>
        "Mira sur iPhone, iPad et Android : ouvre http://<adresse du serveur>:8096/Mira dans le navigateur, puis ajoute la page à l’écran d’accueil.";
}
