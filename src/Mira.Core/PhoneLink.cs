using Net.Codecrete.QrCodeGenerator;

namespace Mira.Core;

/// <summary>
/// The QR code of « Mira sur ton iPhone » (Windows' guide, Mac's Settings): mira://connect?server=…&amp;home=…, read by the
/// iPhone's camera, opens Mira on the iPhone with the server filled in, then Quick Connect or the password. Only
/// addresses: no account, no password, no token; made on this computer, sent nowhere. <c>server</c> is the address that
/// works everywhere when there is one (Tailscale), <c>home</c> the home network's, which the iPhone also learns. Cases
/// shared with the iPhone: <c>tests/fixtures/connect.json</c>.
/// </summary>
public static class PhoneLink
{
    /// <summary>The link for <paramref name="server"/>, and <paramref name="home"/> when it is another; null without a server address.</summary>
    public static string? Link(string? server, string? home = null)
    {
        if (Clean(server) is not { } main) return null;
        var other = Clean(home);
        return "mira://connect?server=" + Uri.EscapeDataString(main)
            + (other is null || string.Equals(other, main, StringComparison.OrdinalIgnoreCase) ? "" : "&home=" + Uri.EscapeDataString(other));
    }

    /// <summary>An http(s) address with a host and nothing after its path, without its last /; null otherwise.</summary>
    private static string? Clean(string? address) =>
        Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/') : null;

    /// <summary>
    /// The QR code of <paramref name="text"/> (error correction M, enough for a screen seen at an angle): the side in
    /// modules, and its dark modules as path data (one unit per module, no quiet zone: the view draws it light around),
    /// which WPF's and Avalonia's geometries both read.
    /// </summary>
    public static (int Size, string Path) Qr(string text)
    {
        var code = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        var path = new System.Text.StringBuilder();
        for (var y = 0; y < code.Size; y++)
            for (var x = 0; x < code.Size; x++)
            {
                if (!code.GetModule(x, y)) continue;
                var start = x;
                while (x + 1 < code.Size && code.GetModule(x + 1, y)) x++;
                path.Append(System.Globalization.CultureInfo.InvariantCulture, $"M{start},{y}h{x - start + 1}v1h{start - x - 1}z");
            }
        return (code.Size, path.ToString());
    }
}
