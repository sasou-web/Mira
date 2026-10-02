using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Mira.Core;
using Mira.Core.TorLink;

namespace Mira.Desktop.TorLink;

/// <summary>
/// Turning TorLink on from the Downloads page: Mira's own Node.js (official zip, checked), then TorLink from npm into
/// "data\torlink", for this Windows account, without administrator rights and without touching the PC's PATH.
/// </summary>
internal static class TorLinkSetup
{
    /// <summary>What the page shows while it works: a step and its share of the whole (0 to 1).</summary>
    public sealed record Step(string Text, double Progress);

    /// <summary>Installs what is missing and returns the installation; an existing one of the same version is kept.</summary>
    public static async Task<TorLinkInstallation> InstallAsync(string profile, IProgress<Step> progress, CancellationToken ct = default)
    {
        var package = TorLinkPackage.Current;
        if (TorLinkInstallation.Validate(TorLinkPackage.PackageRoot(profile), profile) is { HasRuntime: true } ready && ready.Version == package.Version && NodeInstaller.IsInstalled(profile))
        { progress.Report(new("TorLink est prêt.", 1)); return ready; }
        var node = NodePackage.Current;
        progress.Report(new($"Téléchargement de Node.js {node.Version}…", 0));
        await NodeInstaller.InstallAsync(profile, node, progress: new Percent((percent, p) => progress.Report(new($"Téléchargement de Node.js {node.Version}… {percent} %", p * .6))), ct: ct);
        progress.Report(new($"Installation de TorLink {package.Version}…", .65));
        await RunNpmAsync(profile, package, ct);
        var installed = TorLinkInstallation.Validate(TorLinkPackage.PackageRoot(profile), profile);
        if (installed is not { HasRuntime: true } || installed.Version != package.Version)
            throw new InstallException("TorLink s’est installé de façon incomplète. Réessaie ; si cela persiste, vérifie la connexion à registry.npmjs.org.");
        await File.WriteAllTextAsync(Path.Combine(TorLinkPackage.Folder(profile), "SOURCE.txt"),
            $"TorLink ({TorLinkPackage.Name} {package.Version}), installé par Mira depuis registry.npmjs.org avec Node.js {node.Version}.\n" +
            "Projet indépendant de Mira, sous licence MIT : https://github.com/baairon/torlink\n", ct);
        progress.Report(new("TorLink est prêt.", 1));
        return installed;
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

    /// <summary>npm, run by Mira's Node.js in a hidden window; its errors become one readable line.</summary>
    private static async Task RunNpmAsync(string profile, TorLinkPackage package, CancellationToken ct)
    {
        Directory.CreateDirectory(TorLinkPackage.Folder(profile));
        var start = new ProcessStartInfo(NodeInstaller.Executable(profile))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = TorLinkPackage.Folder(profile)
        };
        foreach (var argument in package.NpmArguments(NodeInstaller.NpmCli(profile), profile)) start.ArgumentList.Add(argument);
        // npm finds node through PATH for its own child steps: Mira's Node.js comes first.
        start.Environment["PATH"] = NodeInstaller.Folder(profile) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        Process process;
        try { process = Process.Start(start) ?? throw new InstallException("npm n’a pas pu démarrer."); }
        catch (Win32Exception ex) { throw new InstallException("npm n’a pas pu démarrer : " + ex.Message); }
        using (process)
        {
            var output = new StringBuilder();
            var reading = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
            async Task Read(StreamReader reader) { string? line; while ((line = await reader.ReadLineAsync(ct)) is not null) lock (output) output.AppendLine(line); }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(limit.Token); await reading; }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                if (ct.IsCancellationRequested) throw;
                throw new InstallException("L’installation de TorLink a pris plus de 10 minutes : vérifie la connexion, puis réessaie.");
            }
            if (process.ExitCode != 0)
            {
                string text; lock (output) text = output.ToString();
                throw new InstallException("TorLink n’a pas pu s’installer" + (TorLinkPackage.NpmProblem(text) is { } problem ? " : " + problem : $" (code {process.ExitCode}).")
                    + (text.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase) || text.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase) ? " Vérifie la connexion à Internet." : ""));
            }
        }
    }
}
