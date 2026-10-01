using System.Security.Cryptography;
using SharpCompress.Archives.SevenZip;

namespace Mira.Core;

/// <summary>A libmpv install that could not be completed; the message is written for the person waiting for it.</summary>
public sealed class MpvInstallException(string message) : IOException(message);

/// <summary>
/// The libmpv build Mira installs on request: one fixed archive of the Windows builds listed on mpv.io (shinchiro,
/// published on SourceForge), checked by its SHA-256 before anything is extracted, and the extracted library by its own.
/// The archive is downloaded from its distributor, never shipped with Mira: its licence and sources stay theirs.
/// </summary>
public sealed record MpvPackage(string Version, Uri Url, long Size, string ArchiveSha256, string LibrarySha256)
{
    public const string LibraryName = "libmpv-2.dll";
    /// <summary>Generic x86_64 build (the "v3" builds need AVX2). Its SHA-1 and MD5 match the ones SourceForge publishes.</summary>
    public static readonly MpvPackage Current = new("20260927-git-a1bf4b6559",
        new("https://downloads.sourceforge.net/project/mpv-player-windows/libmpv/mpv-dev-x86_64-20260927-git-a1bf4b6559.7z"),
        31_487_676, "3a80c48d271a8aa77f7b6617cd7f182a87370361cd23635562927c9eac74ac5c", "0a81c004aae0ee7d512b9a26e38f66281f9591e84e1663215cc3a36a4bde6f6a");
    public string ArchiveName => Path.GetFileName(Url.AbsolutePath);
}

/// <summary>Installs <see cref="MpvPackage"/> into the profile ("data\mpv"), where updates of Mira never touch it.</summary>
public static class MpvInstaller
{
    public const string VersionFile = "version.txt", SourceFile = "SOURCE.txt";
    /// <summary>A transfer that receives nothing for this long is abandoned.</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public static string Folder(string profile) => Path.Combine(profile, "mpv");
    public static string LibraryPath(string profile) => Path.Combine(Folder(profile), MpvPackage.LibraryName);
    public static bool IsInstalled(string profile, MpvPackage? package = null)
    {
        package ??= MpvPackage.Current;
        try { return File.Exists(LibraryPath(profile)) && File.ReadAllText(Path.Combine(Folder(profile), VersionFile)).Trim() == package.Version; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>
    /// Downloads, checks and extracts the library; returns its path. Already installed: nothing is downloaded.
    /// <paramref name="progress"/> goes from 0 to 1, the download taking the first 80 %.
    /// </summary>
    public static async Task<string> InstallAsync(string profile, MpvPackage? package = null, HttpMessageHandler? handler = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        package ??= MpvPackage.Current;
        var folder = Folder(profile); var library = LibraryPath(profile);
        if (IsInstalled(profile, package)) { progress?.Report(1); return library; }
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, package.ArchiveName + ".partial");
        var extracted = library + ".partial";
        try
        {
            await DownloadAsync(package, archive, handler, progress, ct).ConfigureAwait(false);
            await Task.Run(() => Extract(package, archive, extracted), ct).ConfigureAwait(false);
            progress?.Report(.98);
            try { File.Move(extracted, library, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new MpvInstallException("Le moteur vidéo actuel est en cours d’utilisation : arrête la lecture, puis réessaie."); }
            await File.WriteAllTextAsync(Path.Combine(folder, SourceFile),
                $"libmpv {package.Version}, installé par Mira depuis {package.Url}\nSHA-256 de l’archive : {package.ArchiveSha256}\nSHA-256 de {MpvPackage.LibraryName} : {package.LibrarySha256}\n" +
                "Build Windows de mpv par shinchiro (https://github.com/shinchiro/mpv-winbuild-cmake), listée sur https://mpv.io/installation/.\n" +
                "mpv et les bibliothèques qu’il inclut (dont FFmpeg) restent sous leurs propres licences (GPL) ; sources : https://github.com/mpv-player/mpv\n", ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, VersionFile), package.Version, ct).ConfigureAwait(false);
            progress?.Report(1);
            return library;
        }
        catch (Exception ex) when (ex is not MpvInstallException && ex is IOException or UnauthorizedAccessException) { throw DiskError(); }
        finally { TryDelete(archive); TryDelete(extracted); }
    }
    private static async Task DownloadAsync(MpvPackage package, string target, HttpMessageHandler? handler, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mira/" + JellyfinClient.AppVersion);
        HttpResponseMessage response;
        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headers.CancelAfter(StallTimeout);
            try { response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MpvInstallException("Le serveur du moteur vidéo ne répond pas. Réessaie plus tard."); }
            catch (HttpRequestException) { throw new MpvInstallException("Le moteur vidéo n’a pas pu être téléchargé : vérifie la connexion Internet, puis réessaie."); }
        }
        using (response)
        {
            // A redirect may land on any mirror, but never back to plain HTTP: HttpClient refuses that downgrade.
            if (!response.IsSuccessStatusCode) throw new MpvInstallException($"Le moteur vidéo n’a pas pu être téléchargé (code {(int)response.StatusCode}). Réessaie plus tard.");
            if (response.Content.Headers.ContentLength is { } length && length != package.Size) throw new MpvInstallException("Le fichier proposé n’est pas celui attendu : il n’est pas installé.");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16]; long total = 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct); stall.CancelAfter(StallTimeout);
                int read;
                try { read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MpvInstallException("Le téléchargement du moteur vidéo s’est interrompu. Réessaie."); }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { throw new MpvInstallException("Le téléchargement du moteur vidéo s’est interrompu. Réessaie."); }
                if (read == 0) break;
                total += read;
                if (total > package.Size) throw new MpvInstallException("Le fichier proposé n’est pas celui attendu : il n’est pas installé.");
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(.8 * total / package.Size);
            }
            if (total != package.Size) throw new MpvInstallException("Le téléchargement du moteur vidéo est incomplet. Réessaie.");
            if (!Matches(sha.GetHashAndReset(), package.ArchiveSha256)) throw new MpvInstallException("Le fichier téléchargé ne correspond pas à la version vérifiée par Mira : il n’est pas installé.");
        }
    }
    /// <summary>Only the library leaves the archive, and only if its own SHA-256 is the expected one.</summary>
    private static void Extract(MpvPackage package, string archivePath, string target)
    {
        try
        {
            using var archive = SevenZipArchive.Open(archivePath);
            var entry = archive.Entries.FirstOrDefault(x => !x.IsDirectory && x.Key is MpvPackage.LibraryName)
                ?? throw new MpvInstallException("L’archive téléchargée ne contient pas le moteur vidéo attendu.");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = entry.OpenEntryStream())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { sha.AppendData(buffer, 0, read); output.Write(buffer, 0, read); }
            }
            if (!Matches(sha.GetHashAndReset(), package.LibrarySha256)) throw new MpvInstallException("Le moteur extrait ne correspond pas à la version vérifiée par Mira : il n’est pas installé.");
        }
        catch (MpvInstallException) { throw; }
        // The archive was checked by its hash before this point: a read or write failure here comes from the disk.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw DiskError(); }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        { throw new MpvInstallException("L’archive du moteur vidéo est illisible : elle n’est pas installée."); }
    }
    private static MpvInstallException DiskError() => new("Le moteur vidéo n’a pas pu être enregistré dans le dossier de Mira : vérifie l’espace libre, puis réessaie.");
    private static bool Matches(byte[] hash, string expected) => string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
