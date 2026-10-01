using System.Security.Cryptography;

namespace Mira.Core;

/// <summary>Something Mira installs on request that could not be installed; the message is written for the person waiting.</summary>
public sealed class InstallException(string message) : IOException(message);

/// <summary>
/// A download kept only if its SHA-256 (and size, when known) is the expected one. Redirects to mirrors are followed,
/// never back to plain HTTP; a transfer that receives nothing for <see cref="StallTimeout"/> is abandoned.
/// </summary>
public static class VerifiedDownload
{
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Downloads <paramref name="url"/> into <paramref name="target"/>. <paramref name="subject"/> names it in
    /// messages ("Le moteur vidéo", "Jellyfin"); <paramref name="progress"/> goes from 0 to 1.</summary>
    public static async Task DownloadAsync(Uri url, string target, string sha256, long? size, long maximumSize, string subject,
        HttpMessageHandler? handler = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mira/" + JellyfinClient.AppVersion);
        HttpResponseMessage response;
        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headers.CancelAfter(StallTimeout);
            try { response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InstallException("Le serveur de téléchargement ne répond pas. Réessaie plus tard."); }
            catch (HttpRequestException) { throw new InstallException($"{subject} n’a pas pu être téléchargé : vérifie la connexion Internet, puis réessaie."); }
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new InstallException($"{subject} n’a pas pu être téléchargé (code {(int)response.StatusCode}). Réessaie plus tard.");
            var expected = size ?? response.Content.Headers.ContentLength;
            if (response.Content.Headers.ContentLength is { } length && (size is { } known && length != known || length > maximumSize)) throw Unexpected();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16]; long total = 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct); stall.CancelAfter(StallTimeout);
                int read;
                try { read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InstallException("Le téléchargement s’est interrompu. Réessaie."); }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { throw new InstallException("Le téléchargement s’est interrompu. Réessaie."); }
                if (read == 0) break;
                total += read;
                if (total > (size ?? maximumSize)) throw Unexpected();
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (expected is > 0) progress?.Report(Math.Min(1, total / (double)expected.Value));
            }
            if (size is { } whole && total != whole) throw new InstallException("Le téléchargement est incomplet. Réessaie.");
            if (!Matches(sha.GetHashAndReset(), sha256)) throw new InstallException("Le fichier téléchargé ne correspond pas à la version vérifiée par Mira : il n’est pas installé.");
        }
    }
    public static bool Matches(byte[] hash, string expected) => string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);
    private static InstallException Unexpected() => new("Le fichier proposé n’est pas celui attendu : il n’est pas installé.");
}

/// <summary>Reports a step of a longer task as its share of the whole, on the caller's thread.</summary>
internal sealed class Scaled(IProgress<double> inner, double offset, double share) : IProgress<double>
{
    public void Report(double value) => inner.Report(offset + share * value);
    public static IProgress<double>? By(IProgress<double>? inner, double share, double offset = 0) => inner is null ? null : new Scaled(inner, offset, share);
}
