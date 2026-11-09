using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Mira.Core;

/// <summary>
/// The certificate authorities .NET trusted for Mira's servers, for mpv. libmpv opens https:// through FFmpeg's own TLS
/// (OpenSSL on Windows and Mac), which knows neither Windows' certificate store nor the Mac's keychain: Mira asks it to
/// check each certificate (tls-verify, <see cref="MpvClient"/>) against the authorities of the chains .NET accepted for
/// Jellyfin in this run (tls-ca-file, <see cref="File"/>), intermediates included (Windows fetches those a server leaves
/// out). FFmpeg then checks the server's name itself: a certificate made for another name is refused, as .NET refuses it.
/// A server .NET refused gives mpv nothing, so it never receives the account's token.
/// </summary>
public sealed class ServerTrust
{
    private readonly object _gate = new();
    /// <summary>Each authority once, by its SHA-256, in the order met.</summary>
    private readonly Dictionary<string, byte[]> _authorities = [];
    private readonly string _folder;
    private string? _path;
    private bool _written;

    /// <param name="folder">Where the file goes (the tests'); by default the temporary folder of this account.</param>
    public ServerTrust(string? folder = null) => _folder = folder ?? Path.GetTempPath();

    /// <summary>
    /// For SslOptions.RemoteCertificateValidationCallback: .NET's own verdict, unchanged; a certificate it accepted gives
    /// the authorities of its chain.
    /// </summary>
    public bool Check(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.None) return false;
        if (chain is not null) Remember(chain);
        return true;
    }

    /// <summary>
    /// The authorities of a chain .NET accepted: all but the server's own certificate, or that one alone when it is the
    /// chain (a certificate made by the server for itself, that this computer was told to trust).
    /// </summary>
    public void Remember(X509Chain chain)
    {
        var elements = chain.ChainElements;
        var authorities = new List<byte[]>(elements.Count);
        for (var i = elements.Count == 1 ? 0 : 1; i < elements.Count; i++) authorities.Add(elements[i].Certificate.RawData);
        Remember(authorities);
    }

    /// <summary>Authorities, as DER bytes.</summary>
    public void Remember(IEnumerable<byte[]> authorities)
    {
        lock (_gate)
            foreach (var der in authorities)
                if (_authorities.TryAdd(Convert.ToHexString(SHA256.HashData(der)), der)) _written = false;
    }

    /// <summary>How many authorities are known.</summary>
    public int Count { get { lock (_gate) return _authorities.Count; } }

    /// <summary>
    /// The authorities as a PEM file for mpv's tls-ca-file, written again when one was added. Always a file of Mira's:
    /// empty while no authority is known, or a name nobody made when it could not be written, so that mpv trusts
    /// nothing rather than what its build looks for by default (on Windows, a folder any account may create).
    /// </summary>
    public string File()
    {
        lock (_gate)
        {
            if (_written && _path is not null) return _path;
            var text = new StringBuilder();
            foreach (var der in _authorities.Values) text.Append(new string(PemEncoding.Write("CERTIFICATE", der))).Append('\n');
            try
            {
                // A name of its own, made only by this process; replaced whole, never half written: mpv may read it now.
                if (_path is null)
                {
                    var made = Path.Combine(_folder, $"mira-tls-{Guid.NewGuid():N}.pem");
                    using (new FileStream(made, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    _path = made;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Forget();
                }
                var next = _path + ".next";
                System.IO.File.WriteAllText(next, text.ToString(), new UTF8Encoding(false));
                System.IO.File.Move(next, _path, overwrite: true);
                _written = true;
                return _path;
            }
            // Open by mpv at this moment (Windows), or the folder unwritable: the previous file stays, tried again next time.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return _path is not null && System.IO.File.Exists(_path) ? _path : Path.Combine(_folder, $"mira-tls-{Guid.NewGuid():N}.pem");
            }
        }
    }

    /// <summary>The file removed (the process ends).</summary>
    public void Forget()
    {
        lock (_gate)
        {
            if (_path is null) return;
            try { System.IO.File.Delete(_path); System.IO.File.Delete(_path + ".next"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _path = null; _written = false;
        }
    }
}
