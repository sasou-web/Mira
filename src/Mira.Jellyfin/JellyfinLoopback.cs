using System.Net;
using System.Text;
using MediaBrowser.Controller;

namespace Mira.Jellyfin;

/// <summary>
/// This server, as the plugin reaches it: on the loopback address first, then on the address it gives for its own
/// network. Both come from Jellyfin's settings, never from the request (<see cref="HlsController"/> and the gateway of
/// the guests, <see cref="GuestController"/>, ask Jellyfin there).
/// </summary>
internal static class JellyfinLoopback
{
    /// <summary>Each root of this server, its base URL (<paramref name="pathBase"/>) after it, ending with « / ».</summary>
    public static IEnumerable<string> Roots(IServerApplicationHost host, string pathBase)
    {
        yield return $"http://127.0.0.1:{host.HttpPort}{pathBase}/";
        string? local = null;
        try
        {
            local = host.GetApiUrlForLocalAccess(null, false);
        }
        catch (InvalidOperationException)
        {
            // No address for its own network.
        }

        if (!string.IsNullOrEmpty(local))
        {
            yield return local.TrimEnd('/') + "/";
        }
    }

    /// <summary>
    /// One request to this server with a small body (JSON, or none), tried on each root until one listens: its status
    /// and its text. 502 when none answers within <paramref name="within"/> each.
    /// </summary>
    public static async Task<(int Status, string Text)> CallAsync(HttpClient client, IEnumerable<string> roots, HttpMethod method, string pathAndQuery,
        IEnumerable<(string Name, string Value)> headers, string? json, CancellationToken cancellationToken, TimeSpan? within = null)
    {
        foreach (var root in roots)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(within ?? TimeSpan.FromSeconds(15));
            using var message = new HttpRequestMessage(method, new Uri(root + pathAndQuery));
            foreach (var (name, value) in headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }

            if (json is not null)
            {
                message.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            try
            {
                using var response = await client.SendAsync(message, limit.Token).ConfigureAwait(false);
                return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            }
            catch (HttpRequestException)
            {
                // Not listening there: the next address.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Too slow there: the next address.
            }
        }

        return ((int)HttpStatusCode.BadGateway, "");
    }
}
