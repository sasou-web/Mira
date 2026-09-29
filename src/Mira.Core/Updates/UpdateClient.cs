using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core.Updates;

public sealed record ReleaseAsset(string Name, long Size, Uri Url);
public sealed record UpdateRelease(Version Version, string Tag, Uri? Page, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => a.Name == name);
}
/// <summary>A newer version, signed, with the file this installation needs.</summary>
public sealed record UpdateOffer(Version Version, string Tag, Uri? Page, UpdateFile File, Uri Url);

/// <summary>GitHub's release list (/repos/{owner}/{repo}/releases). Drafts and tags other than vX.Y.Z are ignored;
/// pre-releases count, since every Mira release is still a preview. Unexpected JSON is refused, never thrown as is.</summary>
public static class ReleaseFeed
{
    private static readonly Regex Tag = new(@"^v?(\d{1,4})\.(\d{1,4})\.(\d{1,4})$", RegexOptions.CultureInvariant);
    public static Version? ParseVersion(string? text) =>
        text is not null && Tag.Match(text.Trim()) is { Success: true } match
            ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) : null;

    public static List<UpdateRelease> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new UpdateException("La réponse de GitHub est inattendue.");
            var releases = new List<UpdateRelease>();
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object || release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                var tag = Text(release, "tag_name");
                if (ParseVersion(tag) is not { } version) continue;
                var assets = new List<ReleaseAsset>();
                if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var asset in list.EnumerateArray())
                        if (asset.ValueKind == JsonValueKind.Object && Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url) && Text(asset, "name") is { Length: > 0 } name)
                            assets.Add(new ReleaseAsset(name, Number(asset, "size"), url));
                releases.Add(new UpdateRelease(version, tag, Uri.TryCreate(Text(release, "html_url"), UriKind.Absolute, out var page) ? page : null, assets));
            }
            return releases;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new UpdateException("La réponse de GitHub est illisible."); }
    }
    internal static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    internal static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : -1;
}

/// <summary>
/// Looks for a newer Mira on GitHub and downloads it. Every request, and every redirect it follows, stays on HTTPS and
/// GitHub's own hosts (a loopback feed is accepted for the isolated validation runs). The manifest must be signed by a
/// trusted key, and a download is kept only if its size and SHA-256 match that manifest. An interrupted download resumes.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    /// <summary>The API follows a renamed repository with a redirect; the path is otherwise fixed in every installed copy.</summary>
    public static readonly Uri GitHubReleases = new("https://api.github.com/repos/sasou-web/Mira/releases?per_page=20");
    // Release files: github.com/<owner>/<repo>/releases/download/…, then GitHub's file servers.
    private static readonly Regex DownloadPath = new("^/[^/]+/[^/]+/releases/download/", RegexOptions.CultureInvariant);
    private static readonly string[] GitHubHosts = ["api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"];
    private const int MaximumRedirects = 5;
    private readonly HttpClient _http;
    private readonly Uri _feed;
    private readonly Version _current;
    private readonly IReadOnlyList<string> _keys;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A download that receives nothing for this long is abandoned; the next check resumes it.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public UpdateClient(Version current, IReadOnlyList<string> trustedKeys, Uri? feed = null, HttpMessageHandler? handler = null)
    {
        _current = current; _keys = trustedKeys; _feed = feed ?? GitHubReleases;
        if (!Allowed(_feed)) throw new ArgumentException("Seuls GitHub et, pour les essais, une adresse locale peuvent servir les mises à jour.", nameof(feed));
        // Redirects are followed by hand, so that each one is checked before it is contacted.
        _http = handler is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mira", current.ToString(3)));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/sasou-web/Mira)"));
    }

    /// <summary>The newest signed release above the running version, with the file for <paramref name="kind"/>
    /// ("installer", "portable" or "zip"); null when Mira is up to date.</summary>
    public async Task<UpdateOffer?> CheckAsync(string kind, CancellationToken ct = default)
    {
        var json = Encoding.UTF8.GetString(await GetBytesAsync(_feed, 4 << 20, "application/vnd.github+json", ct).ConfigureAwait(false));
        foreach (var release in ReleaseFeed.Parse(json).Where(r => r.Version > _current).OrderByDescending(r => r.Version))
        {
            var manifestAsset = release.Asset(UpdateManifest.AssetName); var signatureAsset = release.Asset(UpdateManifest.SignatureName);
            // A release published without its signed manifest is not offered; an older signed one still can be.
            if (manifestAsset is null || signatureAsset is null) continue;
            var manifestBytes = await GetBytesAsync(manifestAsset.Url, 64 * 1024, "application/octet-stream", ct).ConfigureAwait(false);
            var signature = UpdateSignature.Decode(Encoding.ASCII.GetString(await GetBytesAsync(signatureAsset.Url, 4096, "application/octet-stream", ct).ConfigureAwait(false)));
            if (signature is null || !UpdateSignature.Verify(manifestBytes, signature, _keys))
                throw new UpdateException($"La version {release.Version.ToString(3)} n’est pas signée par Mira : elle n’est pas installée.");
            var manifest = UpdateManifest.Parse(manifestBytes);
            if (manifest.Version != release.Version) throw new UpdateException($"La release {release.Tag} annonce une autre version : elle n’est pas installée.");
            var file = manifest.Files.FirstOrDefault(f => f.Kind == kind) ?? throw new UpdateException($"La version {release.Version.ToString(3)} ne fournit pas de fichier pour ce type d’installation.");
            var asset = release.Asset(file.Name) ?? throw new UpdateException($"Le fichier {file.Name} manque dans la release {release.Tag}.");
            if (asset.Size != file.Size) throw new UpdateException($"Le fichier {file.Name} ne correspond pas à la version signée.");
            return new UpdateOffer(release.Version, release.Tag, release.Page, file, asset.Url);
        }
        return null;
    }

    /// <summary>
    /// Downloads the offer into <paramref name="directory"/> (a file already there with the right hash is kept). The file
    /// grows as .partial, which a later call resumes, and is renamed only once its size and SHA-256 match the signed manifest.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateOffer offer, string directory, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, offer.File.Name);
        if (File.Exists(target) && await MatchesAsync(target, offer.File, ct).ConfigureAwait(false)) { progress?.Report(1); return target; }
        var partial = target + ".partial";
        var kept = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (kept >= offer.File.Size) { File.Delete(partial); kept = 0; }
        void Discard() { try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

        HttpResponseMessage started;
        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headers.CancelAfter(RequestTimeout);
            try { started = await SendAsync(offer.Url, "application/octet-stream", headers.Token, kept > 0 ? kept : null).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException("GitHub met trop de temps à répondre."); }
        }
        using var response = started;
        // A server that ignores the range sends the whole file again: start over.
        var resumed = kept > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == kept;
        if (!resumed) kept = 0;
        if (response.Content.Headers.ContentLength is { } length && length != offer.File.Size - kept) { Discard(); throw new UpdateException("Le fichier reçu n’a pas la taille annoncée."); }
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        await using (var output = new FileStream(partial, resumed ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true))
        {
            if (resumed)
            {
                // The bytes received before count in the hash; writing continues at their end.
                int count;
                while ((count = await output.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) sha.AppendData(buffer, 0, count);
            }
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var total = kept;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct); stall.CancelAfter(StallTimeout);
                int read;
                try { read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException("Le téléchargement de la mise à jour s’est interrompu : il reprendra à la prochaine vérification."); }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { throw new UpdateException("Le téléchargement de la mise à jour s’est interrompu : il reprendra à la prochaine vérification."); }
                if (read == 0) break;
                total += read;
                if (total > offer.File.Size) { await output.DisposeAsync().ConfigureAwait(false); Discard(); throw new UpdateException("Le fichier reçu dépasse la taille annoncée."); }
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(total / (double)offer.File.Size);
            }
            if (total != offer.File.Size) throw new UpdateException("Le téléchargement de la mise à jour est incomplet : il reprendra à la prochaine vérification.");
        }
        if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), offer.File.Sha256, StringComparison.OrdinalIgnoreCase))
        { Discard(); throw new UpdateException("Le fichier reçu ne correspond pas à la version signée : il est supprimé."); }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Synchronous <see cref="MatchesAsync"/>, for the last check just before an update starts.</summary>
    public static bool Matches(string path, UpdateFile file)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return stream.Length == file.Size && string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>True when the file at <paramref name="path"/> is exactly <paramref name="file"/> (size and SHA-256).</summary>
    public static async Task<bool> MatchesAsync(string path, UpdateFile file, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            if (stream.Length != file.Size) return false;
            return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)), file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task<byte[]> GetBytesAsync(Uri url, int limit, string accept, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await SendAsync(url, accept, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > limit) throw new UpdateException("La réponse de GitHub est trop volumineuse.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit) throw new UpdateException("La réponse de GitHub est trop volumineuse.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException("GitHub met trop de temps à répondre."); }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { throw new UpdateException("GitHub est injoignable pour le moment."); }
    }
    private async Task<HttpResponseMessage> SendAsync(Uri url, string accept, CancellationToken ct, long? from = null)
    {
        if (!Allowed(url) || url != _feed && !IsReleaseFile(url)) throw new UpdateException("Adresse de mise à jour refusée : " + url.GetLeftPart(UriPartial.Authority));
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(accept);
            if (url.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (from is > 0) request.Headers.Range = new RangeHeaderValue(from, null);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException) { throw new UpdateException("GitHub est injoignable pour le moment."); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null || hop >= MaximumRedirects) throw new UpdateException("Redirection de mise à jour invalide.");
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                // Every hop is checked before it is contacted, not only the last one.
                if (!Allowed(next)) throw new UpdateException("Redirection de mise à jour refusée : " + next.GetLeftPart(UriPartial.Authority));
                url = next;
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) { response.Dispose(); throw new UpdateException("GitHub limite les vérifications pour le moment : nouvel essai plus tard."); }
            if (!response.IsSuccessStatusCode) { var code = (int)response.StatusCode; response.Dispose(); throw new UpdateException($"GitHub a répondu {code} à la vérification des mises à jour."); }
            return response;
        }
    }
    /// <summary>GitHub over HTTPS (API, release pages and their file servers), or the loopback origin of a validation feed.</summary>
    private bool Allowed(Uri url) => _feed is { IsLoopback: true }
        ? url.IsLoopback && url.Scheme is "http" or "https" && url.Host == _feed.Host && url.Port == _feed.Port
        : url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && GitHubHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
    private bool IsReleaseFile(Uri url) => _feed.IsLoopback || url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && DownloadPath.IsMatch(url.AbsolutePath);
    public void Dispose() => _http.Dispose();
}
