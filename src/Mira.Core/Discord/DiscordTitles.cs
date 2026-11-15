using System.Net.Http;
using System.Text.Json;

namespace Mira.Core.Discord;

/// <summary>
/// The film or series of what plays, read once per title for the card (<see cref="DiscordTitle"/>), from the Jellyfin
/// server alone: its facts (Items/{id}), its library (its ancestors), and the public address of its poster that
/// Jellyfin's metadata sources give (Items/{id}/RemoteImages: TMDB first; any signed-in account may ask). Mira opens no
/// other connection for it; the server asks its sources as it does when it scans. A failure leaves the card without
/// what failed, never without the card.
/// </summary>
public sealed class DiscordTitles
{
    private readonly Dictionary<string, Task<DiscordTitle>> _read = [];

    /// <summary>The title of <paramref name="playing"/>, read once (<paramref name="poster"/>: its poster asked for too).</summary>
    public Task<DiscordTitle> ReadAsync(JellyfinClient client, MediaItem playing, IReadOnlyCollection<string> libraryIds, bool poster)
    {
        var id = playing.Type == "Episode" && playing.SeriesId is { Length: > 0 } series ? series : playing.Id;
        var key = id + (poster ? "+poster" : "");
        lock (_read)
        {
            if (_read.TryGetValue(key, out var known) && !known.IsFaulted) return known;
            return _read[key] = Task.Run(() => ReadAsync(client, playing, id, libraryIds, poster));
        }
    }

    /// <summary>Another account or server: nothing read for the last one is kept.</summary>
    public void Clear() { lock (_read) _read.Clear(); }

    private static async Task<DiscordTitle> ReadAsync(JellyfinClient client, MediaItem playing, string id, IReadOnlyCollection<string> libraryIds, bool poster)
    {
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = time.Token;
        var item = playing.Id == id ? playing : null;
        try { item = await client.ItemAsync(id, ct); } catch (Exception ex) when (Expected(ex)) { }
        string? library = null;
        try
        {
            var ancestors = await client.GetAsync<List<MediaItem>>($"Items/{Uri.EscapeDataString(id)}/Ancestors?userId={client.Connection.UserId}", ct);
            library = ancestors.Select(x => x.Id).FirstOrDefault(libraryIds.Contains);
        }
        catch (Exception ex) when (Expected(ex)) { }
        string? picture = null;
        if (poster)
            try
            {
                var images = await client.GetAsync<JsonElement>($"Items/{Uri.EscapeDataString(id)}/RemoteImages?type=Primary&limit=8", ct);
                if (images.TryGetProperty("Images", out var list) && list.ValueKind == JsonValueKind.Array)
                    picture = list.EnumerateArray().Select(x => x.TryGetProperty("Url", out var url) ? url.GetString() : null).Select(DiscordPresence.Poster).FirstOrDefault(x => x is not null);
            }
            catch (Exception ex) when (Expected(ex)) { }
        var ids = item?.ProviderIds ?? [];
        return new(id, item?.Name ?? playing.SeriesName ?? playing.Name, item?.Type ?? (playing.Type == "Episode" ? "Series" : playing.Type), item?.ProductionYear,
            item?.Genres ?? [], item?.OfficialRating, library, picture, ids.GetValueOrDefault("Tmdb"), ids.GetValueOrDefault("Imdb"),
            item is null ? null : Trailers.WebLink(item)?.AbsoluteUri);
    }

    private static bool Expected(Exception ex) => ex is HttpRequestException or IOException or OperationCanceledException or JsonException or NotSupportedException;
}
