using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Mira.Core;
using Mira.Core.TorLink;

namespace Mira.Desktop.TorLink;

/// <summary>
/// Turning TorLink on from the Downloads page: Mira's own Node.js (official zip, checked), then TorLink from npm into
/// "data\torlink", following the lock shipped in Mira, for this Windows account, without administrator rights and without
/// touching the PC's PATH. Later versions pinned by Mira are prepared beside them and take their place while TorLink is closed.
/// </summary>
internal static class TorLinkSetup
{
    /// <summary>What the page shows while it works: a step and its share of the whole (0 to 1).</summary>
    public sealed record Step(string Text, double Progress);

    /// <summary>Mira's TorLink and its Node.js are the versions this Mira pins (TorLink installed from its lock).</summary>
    public static bool IsCurrent(string profile) => NodeInstaller.IsInstalled(profile) && TorLinkPackage.Current.IsInstalledIn(TorLinkPackage.Folder(profile));
    /// <summary>Newer versions wait beside the current ones for TorLink's next opening.</summary>
    public static bool IsPrepared(string profile) =>
        NodeInstaller.IsReady(NodeInstaller.NextFolder(profile), NodePackage.Current) || TorLinkPackage.Current.IsInstalledIn(TorLinkPackage.NextFolder(profile));

    /// <summary>
    /// Installs what is missing or older and returns the installation; a current one is kept as it is. Off the interface
    /// thread, as <see cref="PrepareAsync"/>: TorLink's folder holds thousands of files to write, delete or move.
    /// </summary>
    public static Task<TorLinkInstallation> InstallAsync(string profile, IProgress<Step> progress, CancellationToken ct = default) => Task.Run(async () =>
    {
        var package = TorLinkPackage.Current;
        if (IsCurrent(profile) && TorLinkInstallation.Validate(TorLinkPackage.PackageRoot(profile), profile) is { HasRuntime: true } ready)
        { progress.Report(new("TorLink est prêt.", 1)); return ready; }
        await PrepareCoreAsync(profile, progress, ct).ConfigureAwait(false);
        if (!Activate(profile)) throw new InstallException("TorLink ou Node.js est en cours d’utilisation : ferme TorLink, puis réessaie.");
        var installed = TorLinkInstallation.Validate(TorLinkPackage.PackageRoot(profile), profile);
        if (installed is not { HasRuntime: true } || installed.Version != package.Version)
            throw new InstallException("TorLink s’est installé de façon incomplète. Réessaie ; si cela persiste, vérifie la connexion à registry.npmjs.org.");
        progress.Report(new("TorLink est prêt.", 1));
        return installed;
    }, ct);

    /// <summary>
    /// The pinned Node.js and TorLink, built beside Mira's current ones ("node-next", "torlink-next") and never in them:
    /// TorLink may be running meanwhile. What is already current or prepared is not downloaded again.
    /// </summary>
    public static Task PrepareAsync(string profile, IProgress<Step>? progress, CancellationToken ct = default) => Task.Run(() => PrepareCoreAsync(profile, progress, ct), ct);
    private static async Task PrepareCoreAsync(string profile, IProgress<Step>? progress, CancellationToken ct)
    {
        var node = NodePackage.Current; var package = TorLinkPackage.Current;
        progress?.Report(new($"Téléchargement de Node.js {node.Version}…", 0));
        var runtime = await NodeInstaller.PrepareAsync(profile, node, progress: progress is null ? null : new Percent((percent, p) => progress.Report(new($"Téléchargement de Node.js {node.Version}… {percent} %", p * .6))), ct: ct).ConfigureAwait(false);
        var next = TorLinkPackage.NextFolder(profile);
        if (package.IsInstalledIn(TorLinkPackage.Folder(profile)) || package.IsInstalledIn(next)) return;
        progress?.Report(new($"Installation de TorLink {package.Version}…", .65));
        var staging = next + ".partial";
        try
        {
            PreparedFolder.Delete(staging); Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(Path.Combine(staging, "package.json"), TorLinkPackage.Manifest, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(staging, "package-lock.json"), TorLinkPackage.Lock, ct).ConfigureAwait(false);
            await RunNpmAsync(runtime, staging, ct).ConfigureAwait(false);
            if (TorLinkInstallation.Validate(TorLinkPackage.PackageIn(staging)) is not { } built || built.Version != package.Version)
                throw new InstallException("TorLink s’est installé de façon incomplète. Réessaie ; si cela persiste, vérifie la connexion à registry.npmjs.org.");
            await File.WriteAllTextAsync(Path.Combine(staging, "SOURCE.txt"),
                $"TorLink ({TorLinkPackage.Name} {package.Version}), installé par Mira depuis registry.npmjs.org avec Node.js {node.Version}.\n" +
                "Ses dépendances sont celles du fichier package-lock.json fourni par Mira, chacune vérifiée par son empreinte sha512.\n" +
                "Projet indépendant de Mira, sous licence MIT : https://github.com/baairon/torlink\n", ct).ConfigureAwait(false);
            // Last, once every file npm wrote is on the disk: after a power cut, a stamped TorLink is a whole one.
            PreparedFolder.Flush(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, TorLinkPackage.VersionFile), package.Stamp, ct).ConfigureAwait(false);
            PreparedFolder.Delete(next);
            Directory.Move(staging, next);
        }
        catch (Exception ex) when (ex is not InstallException && ex is IOException or UnauthorizedAccessException)
        { throw new InstallException("TorLink n’a pas pu être enregistré dans le dossier de Mira : vérifie l’espace libre, puis réessaie."); }
        finally { PreparedFolder.Delete(staging); }
    }

    /// <summary>
    /// What <see cref="PrepareAsync"/> built takes the place of Mira's Node.js and TorLink; to call only while TorLink is
    /// closed. True when both are current afterwards; false when something was still in use, the rest staying as it was.
    /// </summary>
    public static bool Activate(string profile)
    {
        var runtime = NodeInstaller.ActivatePrepared(profile);
        var folder = TorLinkPackage.Folder(profile); var next = TorLinkPackage.NextFolder(profile);
        PreparedFolder.Settle(folder);
        if (TorLinkPackage.Current.IsInstalledIn(next))
        {
            if (TorLinkPackage.Current.IsInstalledIn(folder)) PreparedFolder.Delete(next);
            else if (!PreparedFolder.Replace(folder, next)) return false;
        }
        return runtime && TorLinkPackage.Current.IsInstalledIn(folder);
    }

    /// <summary>Whole percents only: the download reports each block it reads, the page needs a hundred steps at most.</summary>
    private sealed class Percent(Action<int, double> report) : IProgress<double>
    {
        private int _shown = -1;
        public void Report(double value)
        {
            var percent = (int)Math.Floor(Math.Clamp(value, 0, 1) * 100);
            if (Interlocked.Exchange(ref _shown, percent) != percent) report(percent, value);
        }
    }

    /// <summary>npm ci, run by the Node.js of <paramref name="runtime"/> in a hidden window; its errors become one readable line.</summary>
    private static async Task RunNpmAsync(string runtime, string folder, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(runtime, "node.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = folder
        };
        foreach (var argument in TorLinkPackage.NpmArguments(Path.Combine(runtime, "node_modules", "npm", "bin", "npm-cli.js"), folder)) start.ArgumentList.Add(argument);
        // npm finds node through PATH for its own child steps: Mira's Node.js comes first.
        start.Environment["PATH"] = runtime + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        Process process;
        try { process = Process.Start(start) ?? throw new InstallException("npm n’a pas pu démarrer."); }
        catch (Win32Exception ex) { throw new InstallException("npm n’a pas pu démarrer : " + ex.Message); }
        using (process)
        {
            var output = new StringBuilder();
            var reading = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
            async Task Read(StreamReader reader) { string? line; while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null) lock (output) output.AppendLine(line); }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); await reading.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                if (ct.IsCancellationRequested) throw;
                throw new InstallException("L’installation de TorLink a pris plus de 10 minutes : vérifie la connexion, puis réessaie.");
            }
            if (process.ExitCode != 0)
            {
                string text; lock (output) text = output.ToString();
                // A package that is not byte for byte the one of the lock: nothing of it is installed.
                if (text.Contains("EINTEGRITY", StringComparison.OrdinalIgnoreCase))
                    throw new InstallException("Un paquet téléchargé pour TorLink ne correspond pas à la version vérifiée par Mira : TorLink n’est pas installé. Réessaie plus tard.");
                throw new InstallException("TorLink n’a pas pu s’installer" + (TorLinkPackage.NpmProblem(text) is { } problem ? " : " + problem : $" (code {process.ExitCode}).")
                    + (text.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase) || text.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase) ? " Vérifie la connexion à Internet." : ""));
            }
        }
    }
}
