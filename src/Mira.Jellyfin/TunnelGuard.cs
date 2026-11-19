using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter par lien », the belt behind Tailscale Funnel: a request Funnel brought in from the Internet (its header,
/// <see cref="GuestRoutes.FunnelHeader"/>) reaches Mira's pages and the guests' gateway, and Jellyfin's WebSocket
/// only with a guest's session (<see cref="GuestRoutes.ThroughTunnel"/>); anything else is a 404 before Jellyfin sees
/// it. Funnel, opened for /Mira only, already refuses the rest, « /Mira/%2e%2e/… » included: this holds if it ever
/// let more through, or was opened for all of Jellyfin. Requests from the house carry no such header and go on as
/// they are. And wherever a request comes from, a guest's session reaches Jellyfin only through the gateway: anywhere
/// else, unless the plugin itself passes it on (<see cref="JellyfinLoopback.Header"/>), a 404. Its account sees every
/// library for SyncPlay's sake (<see cref="GuestRoutes.Policy"/>): this keeps a guest at home, on the same network as
/// Jellyfin, from using its session straight on Jellyfin's API. Put first among Jellyfin's middleware as an <see cref="IStartupFilter"/> (<see cref="PluginServiceRegistrator"/>).
/// </summary>
internal sealed class TunnelGuard : IStartupFilter
{
    private readonly ILogger<TunnelGuard> _logger;

    public TunnelGuard(ILogger<TunnelGuard> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // Whatever happens here, Jellyfin starts: without the belt rather than not at all.
        try
        {
            Add(app);
        }
        catch (Exception error) when (error is TypeLoadException or MissingMethodException or InvalidOperationException)
        {
            _logger.LogError(error, "Mira: the guard of « Inviter par lien » could not be put before Jellyfin");
        }

        next(app);
    };

    private static void Add(IApplicationBuilder app)
    {
        var config = app.ApplicationServices.GetService<IConfigurationManager>();
        app.Use(rest => context => GuardAsync(context, rest, config));
    }

    private static Task GuardAsync(HttpContext context, RequestDelegate rest, IConfigurationManager? config)
    {
        var request = context.Request;
        var path = (request.PathBase + request.Path).Value ?? "";
        var baseUrl = JellyfinLoopback.BaseUrl(config);
        if (!request.Headers.ContainsKey(GuestRoutes.FunnelHeader))
        {
            return GuestOutsideGateway(request, path, baseUrl) ? NotFound(context) : rest(context);
        }

        var upgrade = request.Headers.Upgrade.Count > 0 || context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true;
        var pass = GuestRoutes.ThroughTunnel(request.Method, path, baseUrl, upgrade);
        if (pass == GuestRoutes.Tunnel.Socket && !IsGuest(request))
        {
            pass = GuestRoutes.Tunnel.None;
        }

        if (pass != GuestRoutes.Tunnel.None)
        {
            return rest(context);
        }

        return NotFound(context);
    }

    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }

    /// <summary>
    /// A guest's session (any token the request carries that is one) outside the gateway's path, not passed on by the
    /// plugin. Before the plugin's file of links is known, nothing is.
    /// </summary>
    private static bool GuestOutsideGateway(HttpRequest request, string path, string baseUrl)
    {
        if (!Plugin.InvitesReady || path.StartsWith(baseUrl + GuestRoutes.Prefix + "/", StringComparison.OrdinalIgnoreCase)
            || (request.Headers.TryGetValue(JellyfinLoopback.Header, out var gate) && JellyfinLoopback.FromPlugin(gate.ToString())))
        {
            return false;
        }

        foreach (var token in GuestController.Tokens(request).Distinct(StringComparer.Ordinal))
        {
            if (Plugin.Invites.IsGuestToken(token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The session of a link's device, and no other, as the only one the request carries.</summary>
    private static bool IsGuest(HttpRequest request)
    {
        var tokens = GuestController.Tokens(request).Distinct(StringComparer.Ordinal).ToList();
        return tokens.Count == 1 && Plugin.Invites.ByToken(tokens[0])?.Device is not null;
    }
}
