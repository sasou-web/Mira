using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Mira.Core;
using Mira.Core.Tailscale;

namespace Mira.Checks;

/// <summary>
/// The player's own safety, with the real libmpv of each app: Windows' (--player-check), the Mac's (--self-check) and
/// Linux's (tests/Mira.Core.Tests, MIRA_MPV). Shared by link, as StandInJellyfin is.
/// <list type="bullet">
/// <item>https:// (<see cref="HttpsAsync"/>): three servers on 127.0.0.1, one with a certificate of an authority Mira
/// trusts (<see cref="ServerTrust"/>), one made by itself, one of the same authority but for another name. Only the
/// first may receive a request, and with it the token; mpv never runs yt-dlp.</item>
/// <item>The subtitles at the start (<see cref="TracksAsync"/>): in the audio's language, the forced ones only; in
/// another, the preferred language's.</item>
/// </list>
/// </summary>
public static class PlayerSafety
{
    /// <summary>One line of a check: passed or failed, or only said (<paramref name="Info"/>).</summary>
    public sealed record Result(bool Passed, string Message, bool Info = false);

    public const string Token = "player-safety-check";
    private const string Authorization = $"MediaBrowser Client=\"Mira\", Token=\"{Token}\"";

    /// <param name="make">A headless mpv of the app, following the network given.</param>
    /// <param name="fakeYtdl">An executable that writes « <c>&lt;itself&gt;.ran</c> » when started (Linux): yt-dlp's
    /// stand-in, to prove mpv never runs it; without one, mpv's option only is read.</param>
    public static async Task<List<Result>> HttpsAsync(Func<TailnetNetwork, MpvClient> make, string? fakeYtdl = null)
    {
        var results = new List<Result>();
        var (wav, _) = AudioOutput.TestSound("stereo");
        using var authority = Authority();
        using var trusted = Leaf(authority, "127.0.0.1");
        using var otherName = Leaf(authority, "not-the-server.example");
        using var selfMade = Leaf(null, "127.0.0.1");
        using var good = new TlsServer(trusted, wav);
        using var other = new TlsServer(otherName, wav);
        using var self = new TlsServer(selfMade, wav);
        var network = new TailnetNetwork(() => [], "mira-check");
        network.Trust.Remember([authority.RawData]);
        try
        {
            var (opened, said) = await OpenAsync(make(network), good.Url("Audio/1/stream.wav"));
            var asked = good.Requests.ToArray();
            results.Add(new(opened && asked.Length > 0 && asked.All(r => r.Contains(Token, StringComparison.Ordinal)),
                $"https:// : un certificat d’une autorité acceptée par Mira est lu, avec le jeton ({said}; {asked.Length} requête(s))"));
            foreach (var (server, name) in new[] { (self, "fait par le serveur lui-même"), (other, "fait pour un autre nom") })
            {
                (opened, said) = await OpenAsync(make(network), server.Url("Audio/1/stream.wav"));
                results.Add(new(!opened && server.Requests.IsEmpty,
                    $"https:// : un certificat {name} est refusé avant toute requête, le jeton ne part pas ({said}; {server.Requests.Count} requête(s))"));
            }
            // Nothing accepted by .NET in this run: mpv trusts nothing, never what its build looks for by default.
            var before = good.Requests.Count;
            (opened, said) = await OpenAsync(make(new TailnetNetwork(() => [], "mira-check")), good.Url("Audio/1/stream.wav"));
            results.Add(new(!opened && good.Requests.Count == before, $"https:// : sans autorité acceptée par Mira, mpv n’en croit aucune ({said})"));

            using (var mpv = make(network))
                results.Add(new(mpv.Get("ytdl") == "no", $"mpv ne lance jamais yt-dlp (ytdl = {mpv.Get("ytdl")})"));
            if (fakeYtdl is not null)
            {
                var ran = fakeYtdl + ".ran"; File.Delete(ran);
                var missing = good.Url("Videos/missing/stream");
                using (var mpv = make(network))
                {
                    mpv.Set("script-opts", "ytdl_hook-ytdl_path=" + fakeYtdl);
                    (opened, said) = await OpenAsync(mpv, missing, close: false);
                    await Task.Delay(1500);
                    results.Add(new(!opened && !File.Exists(ran), $"un flux en échec (404) ne lance pas yt-dlp ({said})"));
                }
                // The same with mpv's default: the stand-in does run, so the check above proves something.
                using (var mpv = make(network))
                {
                    mpv.Set("script-opts", "ytdl_hook-ytdl_path=" + fakeYtdl); mpv.Set("ytdl", "yes");
                    await OpenAsync(mpv, missing, close: false);
                    var until = Stopwatch.StartNew();
                    while (!File.Exists(ran) && until.Elapsed < TimeSpan.FromSeconds(10)) { mpv.Poll(); await Task.Delay(100); }
                    results.Add(File.Exists(ran) ? new(true, "témoin : avec ytdl=yes (défaut de mpv), le même flux lance yt-dlp")
                        : new(true, "témoin : ce mpv ne lance pas yt-dlp même avec ytdl=yes (sans son script ytdl_hook)", Info: true));
                }
            }
        }
        finally { network.Trust.Forget(); }
        return results;
    }

    /// <param name="make">A headless mpv of the app, with Mira's languages for an app in English (audio jpn, eng, fre;
    /// subtitles eng, fre).</param>
    /// <param name="folder">The films of tools/player-check.ps1 and mac.yml: english.mkv (audio and subtitles in
    /// English), forced.mkv (the same, then forced subtitles in English), japanese.mkv (audio in Japanese, subtitles in
    /// English).</param>
    public static async Task<List<Result>> TracksAsync(Func<MpvClient> make, string folder)
    {
        var results = new List<Result>();
        foreach (var (file, expected, what) in new[]
        {
            ("english.mkv", "no", "film en anglais : pas ses sous-titres anglais"),
            ("forced.mkv", "2", "film en anglais : ses seuls sous-titres forcés"),
            ("japanese.mkv", "1", "anime en japonais : ses sous-titres anglais"),
        })
        {
            using var mpv = make();
            var (opened, said) = await OpenAsync(mpv, Path.Combine(folder, file), close: false);
            var sid = mpv.Get("sid"); var option = mpv.Get("subs-with-matching-audio");
            // mpv 0.37 and older know only yes or no: in the audio's language, no subtitles at all, forced ones too.
            var passed = opened && (sid == expected || (file == "forced.mkv" && sid == "no" && option == "no"));
            results.Add(new(passed, $"sous-titres au départ, {what} (piste {sid}, subs-with-matching-audio = {option}; {said})"));
        }
        return results;
    }

    /// <summary>Opens a file paused: true once mpv read it, false when it failed or ended first; what mpv said.</summary>
    private static async Task<(bool Opened, string Said)> OpenAsync(MpvClient mpv, string location, bool close = true)
    {
        try
        {
            var outcome = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            mpv.FileLoaded += () => outcome.TrySetResult(null);
            mpv.Error += message => outcome.TrySetResult("erreur : " + message);
            mpv.Ended += _ => outcome.TrySetResult("fini avant d’être lu");
            mpv.Load(location, 0, Authorization, paused: true);
            var timer = Stopwatch.StartNew();
            while (!outcome.Task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(20)) { mpv.Poll(); await Task.Delay(50); }
            return outcome.Task.IsCompleted ? (outcome.Task.Result is null, outcome.Task.Result ?? "lu") : (false, "aucune réponse en 20 s");
        }
        finally { if (close) mpv.Dispose(); }
    }

    /// <summary>A certificate authority of the check's own, for its servers.</summary>
    public static X509Certificate2 Authority()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Mira check authority", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
    }

    /// <summary>A server's certificate for <paramref name="name"/> (an address also as a name, for an FFmpeg that checks it as one), of <paramref name="issuer"/> or of itself.</summary>
    public static X509Certificate2 Leaf(X509Certificate2? issuer, string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(name, out var address)) names.AddIpAddress(address);
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        // Within the authority's own dates: a second between the two would make .NET refuse the leaf.
        var from = DateTimeOffset.UtcNow.AddDays(-1); var to = DateTimeOffset.UtcNow.AddDays(6);
        X509Certificate2 made;
        if (issuer is null) made = request.CreateSelfSigned(from, to);
        else
        {
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            using var bare = request.Create(issuer, from, to, RandomNumberGenerator.GetBytes(12));
            made = bare.CopyWithPrivateKey(key);
        }
        // Windows' TLS serves only a key it stored: the certificate goes through PKCS#12 once.
        using (made) return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    /// <summary>An HTTPS server on 127.0.0.1 that serves one file (ranges too) and notes each request's line and token.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _closing = new();
        private readonly X509Certificate2 _certificate;
        private readonly byte[] _file;
        public ConcurrentQueue<string> Requests { get; } = new();

        public TlsServer(X509Certificate2 certificate, byte[] file)
        {
            _certificate = certificate; _file = file;
            _listener.Start();
            _ = AcceptAsync();
        }
        public string Url(string path) => $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}";

        private async Task AcceptAsync()
        {
            while (!_closing.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_closing.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                await using var tls = new SslStream(client.GetStream());
                try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _closing.Token); }
                // mpv refused the certificate (TLS 1.3 finishes on the server's side before the client checks it: no count).
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or System.ComponentModel.Win32Exception) { return; }
                var head = await ReadHeadAsync(tls);
                if (head is null) return;
                var lines = head.Split("\r\n");
                var authorization = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) ?? "(sans Authorization)";
                Requests.Enqueue(lines[0] + " | " + authorization);
                var path = lines[0].Split(' ') is [_, var target, ..] ? target : "";
                if (!path.Contains("stream.wav", StringComparison.Ordinal))
                {
                    await WriteAsync(tls, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }
                long from = 0, to = _file.Length - 1; var status = "200 OK"; var range = "";
                if (lines.FirstOrDefault(l => l.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) is { } asked
                    && asked[13..].Split('-') is [var first, var last] && long.TryParse(first, out from))
                {
                    if (long.TryParse(last, out var end)) to = Math.Min(end, to);
                    if (from > to) { await WriteAsync(tls, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{_file.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
                    status = "206 Partial Content"; range = $"Content-Range: bytes {from}-{to}/{_file.Length}\r\n";
                }
                await WriteAsync(tls, $"HTTP/1.1 {status}\r\nContent-Type: audio/wav\r\nAccept-Ranges: bytes\r\n{range}Content-Length: {to - from + 1}\r\nConnection: close\r\n\r\n");
                await tls.WriteAsync(_file.AsMemory((int)from, (int)(to - from + 1)), _closing.Token);
                await tls.FlushAsync(_closing.Token);
            }
            // mpv closing what it no longer needs, or the check ending.
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task WriteAsync(Stream stream, string text) { await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _closing.Token); await stream.FlushAsync(_closing.Token); }

        private async Task<string?> ReadHeadAsync(Stream stream)
        {
            var head = new List<byte>(512); var one = new byte[1];
            while (head.Count < 16384)
            {
                if (await stream.ReadAsync(one, _closing.Token) == 0) return null;
                head.Add(one[0]);
                if (head.Count >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r') return Encoding.ASCII.GetString(head.ToArray(), 0, head.Count - 4);
            }
            return null;
        }

        public void Dispose()
        {
            _closing.Cancel(); _listener.Stop();
            _closing.Dispose(); _certificate.Dispose();
        }
    }
}
