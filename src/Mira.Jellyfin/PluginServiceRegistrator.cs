using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mira.Jellyfin;

/// <summary>
/// What the extension adds to Jellyfin's services: « Mira » among its subtitle providers (<see cref="MiraSubtitleProvider"/>),
/// the loop of « Sous-titres automatiques » (<see cref="SubtitleAutomation"/>), and the guard of what comes in through
/// Tailscale Funnel (<see cref="TunnelGuard"/>).
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ISubtitleProvider, MiraSubtitleProvider>();
        serviceCollection.AddHostedService<SubtitleAutomation>();
        serviceCollection.AddTransient<IStartupFilter, TunnelGuard>();
    }
}
