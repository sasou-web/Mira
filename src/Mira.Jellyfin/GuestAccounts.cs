using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Mira.Jellyfin;

/// <summary>
/// The guests' accounts on Jellyfin, made and kept through its own API on its loopback address: with an administrator's
/// session (« Inviter par lien », <see cref="InvitesController"/>), or with the plugin's API key when a guest comes by a
/// party link and no administrator is there (<see cref="GuestController"/>).
/// </summary>
internal static class GuestAccounts
{
    // The name Jellyfin's dashboard shows for the plugin's API key (Tableau de bord → Clés API).
    public const string KeyName = "Mira";

    public enum Made { Done, BadName, NoAnswer, Failed }

    /// <summary>The plugin's API key as a session.</summary>
    public static IReadOnlyList<(string, string)> WithKey(string key) => [("Authorization", $"MediaBrowser Token=\"{key}\"")];

    /// <summary>The person's own session, as this request carried it: what the plugin's calls to Jellyfin for them use.</summary>
    public static IReadOnlyList<(string, string)> SessionOf(HttpRequest request)
    {
        var headers = new List<(string, string)>();
        foreach (var name in new[] { "Authorization", "X-Emby-Authorization", "X-Emby-Token", "X-MediaBrowser-Token" })
        {
            if (request.Headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                headers.Add((name, value.ToString()));
            }
        }

        var key = request.Query.FirstOrDefault(x => x.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase)).Value.ToString();
        // Jellyfin 12.1 reads a session from Authorization or the query, no longer from X-Emby-Token.
        if (!string.IsNullOrEmpty(key) && !headers.Any(x => x.Item1 == "Authorization"))
        {
            headers.Add(("Authorization", $"MediaBrowser Token=\"{key}\""));
        }

        return headers;
    }

    /// <summary>
    /// A guest's account: hidden, its policy a guest's (<see cref="GuestRoutes.Policy"/>), its password random and never
    /// shown, its name <paramref name="wanted"/> made unique (or <paramref name="fallback"/> numbered). Deleted again
    /// when its policy does not pass.
    /// </summary>
    public static async Task<(Made Result, string? UserId, string? Name, string? Password)> CreateAsync(HttpClient client, IEnumerable<string> roots,
        IReadOnlyList<(string, string)> admin, string? wanted, string fallback, CancellationToken cancellationToken)
    {
        var (usersStatus, usersText) = await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Get, "Users", admin, null, cancellationToken).ConfigureAwait(false);
        if (usersStatus != (int)HttpStatusCode.OK || Parse(usersText) is not JsonArray users)
        {
            return (Made.NoAnswer, null, null, null);
        }

        var name = GuestRoutes.GuestName(wanted, fallback, users.Select(x => Text(x?["Name"])).OfType<string>());
        if (name is null)
        {
            return (Made.BadName, null, null, null);
        }

        var password = GuestRoutes.NewPassword();
        var (madeStatus, madeText) = await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Post, "Users/New", admin, JsonSerializer.Serialize(new { Name = name, Password = password }), cancellationToken).ConfigureAwait(false);
        var userId = madeStatus == (int)HttpStatusCode.OK ? Text(Parse(madeText)?["Id"]) : null;
        if (userId is null)
        {
            return (Made.Failed, null, null, null);
        }

        // A new account sees every library and downloads (Jellyfin 12.1): its policy is read, made a guest's, sent back whole.
        var (userStatus, userText) = await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Get, $"Users/{userId}", admin, null, cancellationToken).ConfigureAwait(false);
        var policy = userStatus == (int)HttpStatusCode.OK ? Parse(userText)?["Policy"] as JsonObject : null;
        var (policyStatus, _) = policy is null ? ((int)HttpStatusCode.BadGateway, "")
            : await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Post, $"Users/{userId}/Policy", admin, GuestRoutes.Policy(policy).ToJsonString(), cancellationToken).ConfigureAwait(false);
        if (policyStatus is not ((int)HttpStatusCode.OK or (int)HttpStatusCode.NoContent))
        {
            await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Delete, $"Users/{userId}", admin, null, CancellationToken.None).ConfigureAwait(false);
            return (Made.Failed, null, null, null);
        }

        return (Made.Done, userId, name, password);
    }

    /// <summary>
    /// Jellyfin's API key for the plugin, kept to delete the accounts of links gone unused while no administrator is
    /// there, and to make the accounts of a party's guests: made once, with an administrator's session, and made again
    /// if the dashboard deleted it.
    /// </summary>
    public static async Task KeepKeyAsync(HttpClient client, IEnumerable<string> roots, IReadOnlyList<(string, string)> admin, CancellationToken cancellationToken)
    {
        static IEnumerable<JsonNode> Keys(string text) => Parse(text)?["Items"] is JsonArray items ? items.OfType<JsonNode>() : [];
        var (status, text) = await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Get, "Auth/Keys", admin, null, cancellationToken).ConfigureAwait(false);
        if (status != (int)HttpStatusCode.OK || (Plugin.Invites.Key is { } kept && Keys(text).Any(x => Text(x["AccessToken"]) == kept)))
        {
            return;
        }

        var before = Keys(text).Select(x => Text(x["AccessToken"])).ToHashSet();
        await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Post, $"Auth/Keys?app={KeyName}", admin, null, cancellationToken).ConfigureAwait(false);
        (status, text) = await JellyfinLoopback.CallAsync(client, roots, HttpMethod.Get, "Auth/Keys", admin, null, cancellationToken).ConfigureAwait(false);
        var made = status == (int)HttpStatusCode.OK
            ? Keys(text).Where(x => Text(x["AppName"]) == KeyName && !before.Contains(Text(x["AccessToken"]))).Select(x => Text(x["AccessToken"])).FirstOrDefault()
            : null;
        if (made is not null)
        {
            Plugin.Invites.Key = made;
        }
    }

    public static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    public static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
}
