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
/// <see cref="GuestRoutes.FunnelHeader"/>) reaches Mira web's page and the guests' gateway, and Jellyfin's WebSocket
/// only with a guest's session (<see cref="GuestRoutes.ThroughTunnel"/>); anything else is a 404 before Jellyfin sees
/// it. Funnel, opened for /Mira only, already refuses the rest, « /Mira/%2e%2e/… » included: this holds if it ever
/// let more through, or was opened for all of Jellyfin. Requests from the house carry no such header and go on as
/// they are. Put first among Jellyfin's middleware as an <see cref="IStartupFilter"/> (<see cref="PluginServiceRegistrator"/>).
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
        if (!request.Headers.ContainsKey(GuestRoutes.FunnelHeader))
        {
            return rest(context);
        }

        var upgrade = request.Headers.Upgrade.Count > 0 || context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true;
        var pass = GuestRoutes.ThroughTunnel(request.Method, (request.PathBase + request.Path).Value ?? "", BaseUrl(config), upgrade);
        if (pass == GuestRoutes.Tunnel.Socket && !IsGuest(request))
        {
            pass = GuestRoutes.Tunnel.None;
        }

        if (pass != GuestRoutes.Tunnel.None)
        {
            return rest(context);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }

    /// <summary>The session of a link's device, and no other, as the only one the request carries.</summary>
    private static bool IsGuest(HttpRequest request)
    {
        var tokens = GuestController.Tokens(request).Distinct(StringComparer.Ordinal).ToList();
        return tokens.Count == 1 && Plugin.Invites.ByToken(tokens[0])?.Device is not null;
    }

    /// <summary>
    /// Jellyfin's base URL (« » or « /jellyfin »), read by name from its network settings, whose type moved between
    /// versions. « » when they cannot be read: a request under a base URL is then refused, never let through.
    /// </summary>
    private static string BaseUrl(IConfigurationManager? config)
    {
        try
        {
            var network = config?.GetConfiguration("network");
            var value = network?.GetType().GetProperty("BaseUrl")?.GetValue(network) as string;
            return string.IsNullOrWhiteSpace(value) ? "" : "/" + value.Trim().Trim('/');
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Reflection.TargetInvocationException or System.Reflection.AmbiguousMatchException)
        {
            return "";
        }
    }
}
