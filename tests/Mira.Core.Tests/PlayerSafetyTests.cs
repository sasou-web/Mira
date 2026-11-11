using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Mira.Checks;
using Mira.Core;
using Mira.Core.Tailscale;
using static TestSupport;

/// <summary>
/// The player's safety: the authorities mpv trusts for https:// (ServerTrust), then, with a real libmpv (MIRA_MPV, the
/// Linux CI's), https:// against three certificates, yt-dlp never run, and the subtitles a film starts with (with ffmpeg).
/// </summary>
internal static class PlayerSafetyTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Lecteur : mpv ne croit que les autorités des certificats que .NET a acceptés pour Jellyfin (ServerTrust)", () =>
        {
            using var authority = PlayerSafety.Authority();
            using var leaf = PlayerSafety.Leaf(authority, "jellyfin.example");
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert(chain.Build(leaf), "Test chain: " + string.Join(", ", chain.ChainStatus.Select(s => s.Status)));
            var folder = Path.Combine(run.Root, "trust"); Directory.CreateDirectory(folder);
            var trust = new ServerTrust(folder);
            var empty = trust.File();
            Assert(File.Exists(empty) && new FileInfo(empty).Length == 0 && Path.GetDirectoryName(empty) == folder, "Nothing accepted yet: an empty file of Mira's, never mpv's own defaults: " + empty);
            Assert(!trust.Check(new(), leaf, chain, SslPolicyErrors.RemoteCertificateNameMismatch) && trust.Count == 0, "A certificate .NET refused: refused, nothing kept");
            Assert(!trust.Check(new(), leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors) && trust.Count == 0, "Nor an unknown authority");
            Assert(trust.Check(new(), leaf, chain, SslPolicyErrors.None) && trust.Count == 1, "Accepted: its authority kept, never the server's own certificate");
            Assert(trust.Check(new(), leaf, chain, SslPolicyErrors.None) && trust.Count == 1, "Once");
            var path = trust.File();
            var text = File.ReadAllText(path);
            Assert(path == empty && text.Split("BEGIN CERTIFICATE").Length == 2 && text.Contains(Convert.ToBase64String(authority.RawData)[..60], StringComparison.Ordinal)
                && !text.Contains(Convert.ToBase64String(leaf.RawData)[..60], StringComparison.Ordinal), "The authority written for mpv, as PEM, in the same file: " + text);
            // A certificate the server made for itself, that this computer trusts: the chain is that one alone.
            using var own = PlayerSafety.Leaf(null, "nas.example");
            using var alone = new X509Chain();
            alone.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; alone.ChainPolicy.CustomTrustStore.Add(own);
            Assert(alone.Build(own) && trust.Check(new(), own, alone, SslPolicyErrors.None) && trust.Count == 2 && File.ReadAllText(trust.File()).Split("BEGIN CERTIFICATE").Length == 3, "A self-made certificate trusted here: itself");
            trust.Forget();
            Assert(!File.Exists(path), "Removed when Mira quits");
            // Mira's connections give their verdict through it.
            var network = new TailnetNetwork(() => [], "test");
            Assert(network.Handler.SslOptions.RemoteCertificateValidationCallback is { } callback && callback(new(), leaf, chain, SslPolicyErrors.None) && network.Trust.Count == 1
                && !callback(new(), leaf, chain, SslPolicyErrors.RemoteCertificateNameMismatch), "Every connection of Mira's network goes through it, .NET's verdict unchanged");
            network.Trust.Forget();
            return Task.CompletedTask;
        });

        if (Environment.GetEnvironmentVariable("MIRA_MPV") is not { Length: > 0 } library) return;
        await Test("Lecteur : https:// avec le vrai mpv, certificat vérifié avant d’envoyer le jeton, jamais yt-dlp", async () =>
        {
            string? fake = null;
            if (!OperatingSystem.IsWindows())
            {
                fake = Path.Combine(run.Root, "yt-dlp");
                File.WriteAllText(fake, "#!/bin/sh\necho \"$@\" > \"$0.ran\"\nexit 1\n");
                File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var results = await PlayerSafety.HttpsAsync(network => new HeadlessMpv(library) { Network = network }, fake);
            foreach (var result in results) Console.WriteLine($"      {(result.Info ? "INFO" : result.Passed ? "ok" : "ÉCHEC")} {result.Message}");
            Assert(results.All(r => r.Passed), string.Join(" | ", results.Where(r => !r.Passed).Select(r => r.Message)));
        });
        if (Ffmpeg() is not { } ffmpeg) return;
        await Test("Lecteur : sous-titres au départ avec le vrai mpv, choisis par Mira (Mira en anglais et en français, sous-titres de Jellyfin à côté)", async () =>
        {
            var folder = Path.Combine(run.Root, "tracks"); Directory.CreateDirectory(folder);
            MakeTrackMedia(ffmpeg, folder);
            var results = await PlayerSafety.TracksAsync(languages => new HeadlessMpv(library, new() { AudioLanguage = languages.Audio, SubtitleLanguage = languages.Subtitles }), folder);
            foreach (var result in results) Console.WriteLine($"      {(result.Passed ? "ok" : "ÉCHEC")} {result.Message}");
            Assert(results.All(r => r.Passed), string.Join(" | ", results.Where(r => !r.Passed).Select(r => r.Message)));
        });
    }

    private static string? Ffmpeg()
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(folder, "ffmpeg"))) return Path.Combine(folder, "ffmpeg");
        return null;
    }

    /// <summary>The films of <see cref="PlayerSafety.TracksAsync"/>, as tools/player-check.ps1 and mac.yml make them.</summary>
    private static void MakeTrackMedia(string ffmpeg, string folder)
    {
        var srt = Path.Combine(folder, "lines.srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:02,500\nA line\n");
        File.Copy(srt, Path.Combine(folder, "french.srt"), overwrite: true);
        void Make(string file, string audio, params string[] subtitles)
        {
            var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true };
            var arguments = new List<string> { "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=duration=3:size=320x240:rate=24", "-f", "lavfi", "-i", "sine=duration=3" };
            foreach (var _ in subtitles) arguments.AddRange(["-i", srt]);
            arguments.AddRange(["-map", "0:v", "-map", "1:a"]);
            for (var i = 0; i < subtitles.Length; i++)
            {
                var (language, forced) = (subtitles[i].Split(':')[0], subtitles[i].EndsWith(":forced", StringComparison.Ordinal));
                arguments.AddRange(["-map", $"{i + 2}:s", $"-metadata:s:s:{i}", "language=" + language, $"-disposition:s:{i}", forced ? "forced" : "0"]);
            }
            arguments.AddRange(["-c:v", "mpeg4", "-c:a", "mp2", "-c:s", "srt", "-metadata:s:a:0", "language=" + audio, Path.Combine(folder, file)]);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert(process.ExitCode == 0, "ffmpeg: " + error);
        }
        Make("english.mkv", "eng", "eng");
        Make("forced.mkv", "eng", "eng", "eng:forced");
        Make("japanese.mkv", "jpn", "eng");
        Make("bilingual.mkv", "eng", "fre", "eng");
        Make("french.mkv", "fre", "eng");
    }

    /// <summary>mpv without a window or sound, from the system's libmpv (Linux CI), with Mira's own options.</summary>
    private sealed class HeadlessMpv : MpvClient
    {
        public HeadlessMpv(string library, PlayerSettings? settings = null) : base(library, NativeLibrary.Load(library), unload: true)
        {
            Option("vo", "null"); Option("ao", "null");
            if (settings is not null) PlayerOptions(settings);
            Initialize("mpv did not start");
        }
        protected override string PlaybackFailure => "mpv could not play the file";
    }
}
