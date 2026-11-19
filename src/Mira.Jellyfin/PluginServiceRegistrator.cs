using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mira.Jellyfin;

/// <summary>
/// What the extension adds to Jellyfin's services: « Mira » among its subtitle providers (<see cref="MiraSubtitleProvider"/>),
/// the loop of « Sous-titres automatiques » (<see cref="SubtitleAutomation"/>), the episode order of each series
/// (<see cref="TvdbOrders"/>), a title refreshed without all its extras at once (<see cref="TitleRefresh"/>), a name without the code a download tool put before its file's (<see cref="TitleNameRepair"/>), the guard of what comes in through
/// Tailscale Funnel (<see cref="TunnelGuard"/>), the title a party's guest may reach (<see cref="PartyTitles"/>) and the
/// sweep of the guests' accounts from Jellyfin's start (<see cref="GuestCleanupService"/>).
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ISubtitleProvider, MiraSubtitleProvider>();
        serviceCollection.AddHostedService<SubtitleAutomation>();
        serviceCollection.AddSingleton<TitleRefresh>();
        serviceCollection.AddHostedService(services => services.GetRequiredService<TitleRefresh>());
        serviceCollection.AddHostedService<TvdbOrders>();
        serviceCollection.AddHostedService<TitleNameRepair>();
        serviceCollection.AddTransient<IStartupFilter, TunnelGuard>();
        serviceCollection.AddSingleton<PartyTitles>();
        serviceCollection.AddHostedService<GuestCleanupService>();
    }
}
