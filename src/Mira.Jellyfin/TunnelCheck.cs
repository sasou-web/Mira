// System.IO and System.Net.Http named here: the tests compile this file in a WPF project, whose implicit usings leave them out.
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Mira.Jellyfin;

/// <summary>
/// « Inviter par lien »: the address of the links (Tailscale Funnel's) tried from this server, as a visitor from the
/// Internet would. This Jellyfin's Mira must answer at the gateway, and Jellyfin nowhere else: not at its own paths,
/// nor at those that would slip out of /Mira, sent as written (« /Mira/%2e%2e/… », which Jellyfin's server reads as
/// « /System/… »). Where it answers outside Mira, no link is made (<see cref="InvitesController"/>). Free of ASP.NET,
/// for the tests.
/// </summary>
public static class TunnelCheck
{
    public enum State
    {
        /// <summary>Only Mira answers there.</summary>
        Closed,
        /// <summary>Jellyfin answers outside Mira, at <see cref="Result.Path"/>.</summary>
        Open,
        /// <summary>This Jellyfin's Mira does not answer there, or not yet (Funnel off, its certificate on its way, another address).</summary>
        Unreachable,
    }

    public sealed record Result(string Address, State State, DateTimeOffset At, string? Path = null);

    /// <summary>Where this server must answer: its public card, through the gateway.</summary>
    public const string Inside = "/Mira/guest/System/Info/Public";

    /// <summary>The same card by the ways out of /Mira: the whole server, then paths Jellyfin's server reads back to it.</summary>
    public static readonly IReadOnlyList<string> Detours =
    [
        "/System/Info/Public",
        "/Mira/../System/Info/Public",
        "/Mira/%2e%2e/System/Info/Public",
        "/Mira/%2E%2E/System/Info/Public",
        "/Mira/.%2e/System/Info/Public",
        "/Mira/guest/%2e%2e/%2e%2e/System/Info/Public",
        "/Mira/v/%2e%2e/%2e%2e/System/Info/Public",
    ];

    /// <summary>How long each answer may take: a few hundred milliseconds when the address answers at all.</summary>
    public static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private const int MaxText = 65_536;
    private static readonly object Gate = new();
    private static Result? _last;

    /// <summary>The last result for this address, if it was tried since Jellyfin started.</summary>
    public static Result? Last(string? address)
    {
        lock (Gate)
        {
            return _last is { } last && address is not null && last.Address == address ? last : null;
        }
    }

    /// <summary>Keeps a result, the one <see cref="Last"/> gives from now on.</summary>
    public static void Keep(Result result)
    {
        lock (Gate)
        {
            _last = result;
        }
    }

    /// <summary>
    /// Whether to try the address again: never tried, Jellyfin answering outside Mira or Mira not answering for more
    /// than <paramref name="retry"/>, all well for more than <paramref name="fresh"/>.
    /// </summary>
    public static bool Stale(Result? last, DateTimeOffset now, TimeSpan fresh, TimeSpan retry) =>
        last is null || now - last.At >= (last.State == State.Closed ? fresh : retry);

    /// <summary>
    /// The address tried, every path at once, by a client that follows no redirection: this server's card
    /// (<paramref name="serverId"/>, its Id) at the gateway, and nowhere else.
    /// </summary>
    public static async Task<Result> RunAsync(HttpClient client, string address, string serverId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var root = address.TrimEnd('/');
        var inside = AnswersAsync(client, root + Inside, serverId, cancellationToken);
        var detours = Detours.Select(path => AnswersAsync(client, root + path, serverId, cancellationToken)).ToList();
        await Task.WhenAll(detours.Append(inside)).ConfigureAwait(false);
        for (var i = 0; i < detours.Count; i++)
        {
            if (detours[i].Result)
            {
                return new(address, State.Open, now, Detours[i]);
            }
        }

        return new(address, inside.Result ? State.Closed : State.Unreachable, now);
    }

    /// <summary>Whether this server's card answers at <paramref name="url"/>, its path sent as written.</summary>
    private static async Task<bool> AnswersAsync(HttpClient client, string url, string serverId, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Within);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(url, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            var buffer = new byte[MaxText];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token).ConfigureAwait(false)) > 0)
            {
                length += read;
            }

            return SameServer(ServerId(Encoding.UTF8.GetString(buffer, 0, length)), serverId);
        }
        catch (Exception error) when (error is HttpRequestException or UriFormatException or IOException || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>The Id of a Jellyfin's public card (System/Info/Public), or null for anything else.</summary>
    public static string? ServerId(string? text)
    {
        try
        {
            using var card = JsonDocument.Parse(text ?? "");
            return card.RootElement.ValueKind == JsonValueKind.Object && card.RootElement.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SameServer(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && string.Equals(a.Replace("-", "", StringComparison.Ordinal), b.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    /// <summary>What the host reads about the address, in the language of the request.</summary>
    public static string Text(Result result, bool french) => result.State switch
    {
        State.Closed => french
            ? "Adresse vérifiée : depuis Internet, seul Mira y répond."
            : "Address checked: from the Internet, only Mira answers there.",
        State.Open => french
            ? $"Le tunnel laisse passer plus que Mira : Jellyfin répond à {result.Address}{result.Path}. Aucun lien n’est fait tant que Funnel n’est pas ouvert pour /Mira seulement (tailscale funnel status pour voir ce qu’il ouvre)."
            : $"The tunnel lets more than Mira through: Jellyfin answers at {result.Address}{result.Path}. No link is made until Funnel is open for /Mira only (tailscale funnel status shows what it opens).",
        _ => french
            ? "Mira ne répond pas encore à cette adresse depuis le serveur : Funnel est-il ouvert ? Son certificat peut prendre une minute la première fois."
            : "Mira doesn’t answer at this address from the server yet: is Funnel open? Its certificate can take a minute the first time.",
    };
}
