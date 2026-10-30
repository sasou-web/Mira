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
    /// <summary>The page of a release as GitHub gives it, kept only when it is one of Mira's own release pages: the only
    /// pages the apps open in the browser from what GitHub answers.</summary>
    public static Uri? ReleasePage(Uri? page) =>
        // Checked first and apart: a relative address throws on Scheme, and the order inside one pattern is not guaranteed.
        page is { IsAbsoluteUri: true } && page is { Scheme: "https", Host: "github.com", IsDefaultPort: true, UserInfo: "" }
        && page.AbsolutePath.StartsWith("/sasou-web/Mira/releases/", StringComparison.Ordinal) ? page : null;
    public static Uri? ReleasePage(string? page) => Uri.TryCreate(page, UriKind.Absolute, out var uri) ? ReleasePage(uri) : null;

    public static List<UpdateRelease> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new UpdateException(L("La réponse de GitHub est inattendue.", "GitHub’s answer is unexpected."));
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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new UpdateException(L("La réponse de GitHub est illisible.", "GitHub’s answer can’t be read.")); }
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
    // One release of Mira in the API, by its tag (ReleaseInfo).
    private static readonly Regex ReleaseInfoPath = new(@"^/repos/sasou-web/Mira/releases/tags/v\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
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
        if (!Allowed(_feed)) throw new ArgumentException(L("Seuls GitHub et, pour les essais, une adresse locale peuvent servir les mises à jour.", "Only GitHub and, for tests, a local address can serve updates."), nameof(feed));
        // Redirects are followed by hand, so that each one is checked before it is contacted.
        _http = handler is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mira", current.ToString(3)));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/sasou-web/Mira)"));
    }

    /// <summary>The newest signed release above the running version, with the file for <paramref name="kind"/>
    /// ("installer", "portable" or "zip" on Windows, "mac" on a Mac); null when Mira is up to date.</summary>
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
                throw new UpdateException(L($"La version {release.Version.ToString(3)} n’est pas signée par Mira : elle n’est pas installée.", $"Version {release.Version.ToString(3)} isn’t signed by Mira: it isn’t installed."));
            var manifest = UpdateManifest.Parse(manifestBytes);
            if (manifest.Version != release.Version) throw new UpdateException(L($"La release {release.Tag} annonce une autre version : elle n’est pas installée.", $"Release {release.Tag} announces another version: it isn’t installed."));
            // The Mac disk image is among the "others", which Windows copies up to 0.7.4 never read.
            var file = manifest.Files.Concat(manifest.Others).FirstOrDefault(f => f.Kind == kind) ?? throw new UpdateException(L($"La version {release.Version.ToString(3)} ne fournit pas de fichier pour ce type d’installation.", $"Version {release.Version.ToString(3)} has no file for this kind of installation."));
            var asset = release.Asset(file.Name) ?? throw new UpdateException(L($"Le fichier {file.Name} manque dans la release {release.Tag}.", $"The file {file.Name} is missing from release {release.Tag}."));
            if (asset.Size != file.Size) throw new UpdateException(L($"Le fichier {file.Name} ne correspond pas à la version signée.", $"The file {file.Name} doesn’t match the signed version."));
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException(L("GitHub met trop de temps à répondre.", "GitHub is taking too long to answer."), network: true); }
        }
        using var response = started;
        // A server that ignores the range sends the whole file again: start over.
        var resumed = kept > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == kept;
        if (!resumed) kept = 0;
        if (response.Content.Headers.ContentLength is { } length && length != offer.File.Size - kept) { Discard(); throw new UpdateException(L("Le fichier reçu n’a pas la taille annoncée.", "The file received doesn’t have the announced size.")); }
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
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException(L("Le téléchargement de la mise à jour s’est interrompu : il reprendra à la prochaine vérification.", "The update’s download stopped: it’ll resume at the next check.")); }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { throw new UpdateException(L("Le téléchargement de la mise à jour s’est interrompu : il reprendra à la prochaine vérification.", "The update’s download stopped: it’ll resume at the next check.")); }
                if (read == 0) break;
                total += read;
                if (total > offer.File.Size) { await output.DisposeAsync().ConfigureAwait(false); Discard(); throw new UpdateException(L("Le fichier reçu dépasse la taille annoncée.", "The file received is larger than announced.")); }
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(total / (double)offer.File.Size);
            }
            if (total != offer.File.Size) throw new UpdateException(L("Le téléchargement de la mise à jour est incomplet : il reprendra à la prochaine vérification.", "The update’s download is incomplete: it’ll resume at the next check."));
        }
        if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), offer.File.Sha256, StringComparison.OrdinalIgnoreCase))
        { Discard(); throw new UpdateException(L("Le fichier reçu ne correspond pas à la version signée : il est supprimé.", "The file received doesn’t match the signed version: it’s deleted.")); }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Where GitHub serves the file <paramref name="name"/> of release v<paramref name="version"/>: a fixed
    /// address, which only ever names that release's file.</summary>
    public static Uri ReleaseFile(Version version, string name) => new($"https://github.com/sasou-web/Mira/releases/download/v{version.ToString(3)}/{Uri.EscapeDataString(name)}");
    /// <summary>A small file of release v<paramref name="version"/>, in memory, <paramref name="limit"/> bytes at most;
    /// fetched like an update (GitHub only, every redirect checked). Missing: an <see cref="UpdateException"/> with 404.</summary>
    public Task<byte[]> ReleaseFileAsync(Version version, string name, int limit, CancellationToken ct = default) =>
        GetBytesAsync(ReleaseFile(version, name), limit, "application/octet-stream", ct);
    /// <summary>GitHub's API entry for release v<paramref name="version"/>: published releases only, a draft has no tag.</summary>
    public static Uri ReleaseInfo(Version version) => new($"https://api.github.com/repos/sasou-web/Mira/releases/tags/v{version.ToString(3)}");
    /// <summary>
    /// Whether GitHub keeps release v<paramref name="version"/> immutable: once published, neither its files nor its tag
    /// can change, even from the maintainer's account. Missing (or a draft): an <see cref="UpdateException"/> with 404.
    /// </summary>
    public async Task<bool> ImmutableAsync(Version version, CancellationToken ct = default)
    {
        var json = await GetBytesAsync(ReleaseInfo(version), 1 << 20, "application/vnd.github+json", ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("immutable", out var immutable) && immutable.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { throw new UpdateException(L("La réponse de GitHub est illisible.", "GitHub’s answer can’t be read.")); }
    }
    /// <summary>Whether <paramref name="manifest"/> carries a valid signature from one of the keys this client trusts.</summary>
    public bool Signed(byte[] manifest, byte[]? signature) => signature is not null && UpdateSignature.Verify(manifest, signature, _keys);

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
            if (response.Content.Headers.ContentLength > limit) throw new UpdateException(L("La réponse de GitHub est trop volumineuse.", "GitHub’s answer is too large."));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit) throw new UpdateException(L("La réponse de GitHub est trop volumineuse.", "GitHub’s answer is too large."));
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException(L("GitHub met trop de temps à répondre.", "GitHub is taking too long to answer."), network: true); }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { throw new UpdateException(L("GitHub est injoignable pour le moment.", "GitHub can’t be reached right now."), network: true); }
    }
    private async Task<HttpResponseMessage> SendAsync(Uri url, string accept, CancellationToken ct, long? from = null)
    {
        if (!Allowed(url) || url != _feed && !IsReleaseFile(url) && !IsReleaseInfo(url)) throw new UpdateException(L("Adresse de mise à jour refusée : ", "Update address refused: ") + url.GetLeftPart(UriPartial.Authority));
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(accept);
            if (url.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (from is > 0) request.Headers.Range = new RangeHeaderValue(from, null);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException) { throw new UpdateException(L("GitHub est injoignable pour le moment.", "GitHub can’t be reached right now."), network: true); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null || hop >= MaximumRedirects) throw new UpdateException(L("Redirection de mise à jour invalide.", "Invalid update redirect."));
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                // Every hop is checked before it is contacted, not only the last one.
                if (!Allowed(next)) throw new UpdateException(L("Redirection de mise à jour refusée : ", "Update redirect refused: ") + next.GetLeftPart(UriPartial.Authority));
                url = next;
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) { var limited = response.StatusCode; response.Dispose(); throw new UpdateException(L("GitHub limite les vérifications pour le moment : nouvel essai plus tard.", "GitHub is limiting checks for now: Mira will try again later."), limited); }
            if (!response.IsSuccessStatusCode) { var status = response.StatusCode; response.Dispose(); throw new UpdateException(L($"GitHub a répondu {(int)status} à la vérification des mises à jour.", $"GitHub answered {(int)status} to the update check."), status); }
            return response;
        }
    }
    /// <summary>GitHub over HTTPS (API, release pages and their file servers), or the loopback origin of a validation feed.</summary>
    private bool Allowed(Uri url) => _feed is { IsLoopback: true }
        ? url.IsLoopback && url.Scheme is "http" or "https" && url.Host == _feed.Host && url.Port == _feed.Port
        : url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && GitHubHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
    private bool IsReleaseFile(Uri url) => _feed.IsLoopback || url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && DownloadPath.IsMatch(url.AbsolutePath);
    private static bool IsReleaseInfo(Uri url) => url.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && ReleaseInfoPath.IsMatch(url.AbsolutePath);
    public void Dispose() => _http.Dispose();
}
