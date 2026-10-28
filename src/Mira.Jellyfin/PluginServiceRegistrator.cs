using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Mira.Jellyfin;

/// <summary>What the extension adds to Jellyfin's services: « Mira » among its subtitle providers (<see cref="MiraSubtitleProvider"/>).</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ISubtitleProvider, MiraSubtitleProvider>();
    }
}
