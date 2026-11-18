using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// How a friend reaches this Jellyfin (ogsas, 2026-11-18, maquette B « Le guide », /mnt/project-files/inviter-un-ami/):
/// the home address only answers on the network it lives on; the Tailscale address answers from anywhere, to a device on
/// the tailnet, so this PC has to be shared with the friend (Tailscale's console, Share…); the party link is for an
/// evening, not an account. Decided here for Windows and Mac; the login screen says the same when an address stays silent
/// (<see cref="NoAnswer"/>, iPhone <c>ServerKind</c>).
/// </summary>
public static class FriendReach
{
    public enum Kind { Other, Home, Tailnet }

    /// <summary>
    /// What kind of address this is (a URL, or a host as typed): a private or link-local IPv4 address or a « .local »
    /// name is the home's; one of 100.64.0.0/10 or a « .ts.net » name is Tailscale's; anything else, unknown.
    /// </summary>
    public static Kind Of(string? address)
    {
        var host = Host(address);
        if (host is null) return Kind.Other;
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return Kind.Tailnet;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return Kind.Home;
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return Kind.Other;
        return ip.GetAddressBytes() switch
        {
            [100, >= 64 and < 128, ..] => Kind.Tailnet,
            [10, ..] or [172, >= 16 and < 32, ..] or [192, 168, ..] or [169, 254, ..] => Kind.Home,
            _ => Kind.Other,
        };
    }
    private static string? Host(string? address)
    {
        var text = address?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.IdnHost.Trim('[', ']').TrimEnd('.') : null;
    }
    /// <summary>The bare host of an address, for a sentence (« 192.168.1.42 »).</summary>
    public static string HostOf(string address) => Host(address) ?? address;

    /// <summary>
    /// The address a friend types at home: this PC's on the home network when Jellyfin runs here (Mira itself may reach
    /// it at 127.0.0.1), else the account's own when it is a home address; null when Mira cannot tell it.
    /// </summary>
    public static string? HomeAddress(string server, bool onThisPc, string? thisPc)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) return null;
        if (onThisPc && thisPc is not null) return Address(uri, thisPc);
        return Of(server) == Kind.Home ? Address(uri, null) : null;
    }
    /// <summary>
    /// The address a friend types from anywhere: the account's own when it is a Tailscale one, else this PC's on Tailscale
    /// when Jellyfin runs here and the Tailscale app is connected; null when Jellyfin's PC is not on Tailscale (or Mira
    /// cannot tell from here).
    /// </summary>
    public static string? AwayAddress(string server, bool onThisPc, string? tailnet)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) return null;
        if (Of(server) == Kind.Tailnet) return Address(uri, null);
        return onThisPc && tailnet is not null ? Address(uri, tailnet) : null;
    }
    private static string Address(Uri uri, string? host) =>
        ((host is null ? uri : new UriBuilder(uri) { Host = host }.Uri).GetLeftPart(UriPartial.Authority) + uri.AbsolutePath).TrimEnd('/');

    /// <summary>
    /// Who this PC is shared with on Tailscale, from <c>tailscale status --json</c>: once someone accepts the share and
    /// has a device signed in, that device is in this PC's network map as a « sharee » node (<c>ShareeNode</c>, Tailscale
    /// 1.104.4), hidden from <c>tailscale status</c> but in its JSON. Their display names, each once, in order; empty
    /// when nobody has accepted yet; null when the status cannot be read.
    /// </summary>
    public static IReadOnlyList<string>? Sharees(string? status)
    {
        if (status is null) return null;
        try
        {
            using var document = JsonDocument.Parse(status);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("BackendState", out _)) return null;
            var names = new List<string>();
            if (!root.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object) return names;
            root.TryGetProperty("User", out var users);
            foreach (var peer in peers.EnumerateObject().Select(x => x.Value))
            {
                if (peer.ValueKind != JsonValueKind.Object || !peer.TryGetProperty("ShareeNode", out var sharee) || sharee.ValueKind != JsonValueKind.True) continue;
                var name = peer.TryGetProperty("UserID", out var id) && users.ValueKind == JsonValueKind.Object
                    && users.TryGetProperty(id.ToString(), out var user) && user.ValueKind == JsonValueKind.Object
                    && user.TryGetProperty("DisplayName", out var display) && display.ValueKind == JsonValueKind.String && display.GetString() is { Length: > 0 } shown
                    ? shown : L("quelqu’un", "someone");
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
            return names;
        }
        catch (JsonException) { return null; }
    }

    // The guide's words, in both languages (Windows and Mac).
    public static string Question(string name) => L($"D’où {name} va regarder ?", $"Where will {name} watch from?");
    public static string AtHome => L("Chez toi", "At your home");
    public static string Elsewhere => L("Ailleurs", "Somewhere else");
    public static string Created => L("Compte créé", "Account created");
    public static string CreatedLine(string name, IReadOnlyList<string> shared) =>
        L($"{name} · {Friends.Joined(shared)} · ne peut rien changer", $"{name} · {Friends.Joined(shared)} · can’t change anything");
    public static string GiveHome(string name) => L($"Donne l’adresse à {name}", $"Give {name} the address");
    public static string HomeLine => L("L’adresse de ta maison : elle ne marche que sur ton Wi-Fi ou ton câble. Loin de chez toi, elle ne mène nulle part.",
        "Your home address: it only works on your own Wi-Fi or cable. Away from your home it leads nowhere.");
    public static string HomeUnknown => L("Mira ne connaît pas l’adresse de ta maison d’ici : donne celle que la page de Jellyfin montre sur le PC qui le fait tourner.",
        "Mira doesn’t know your home address from here: give the one Jellyfin’s page shows on the PC it runs on.");
    public static string Share(string name) => L($"Partage ce PC avec {name} sur Tailscale", $"Share this PC with {name} on Tailscale");
    public static string ShareLine(string name) => L($"La maison de {name} n’est pas sur ton réseau : Tailscale relie les deux, en privé. Mira ouvre la page des machines : à côté de ce PC, « … » → Share…, puis envoie à {name} le lien obtenu. Il l’accepte avec un compte Tailscale gratuit.",
        $"{name}’s home isn’t on your network: Tailscale links the two, privately. Mira opens the machines page: next to this PC, “…” → Share…, then send {name} the link it gives. They accept with a free Tailscale account.");
    public static string ShareElsewhere(string name) => L($"La maison de {name} n’est pas sur ton réseau : Tailscale relie les deux, en privé. Sur la page des machines, à côté du PC de Jellyfin, « … » → Share…, puis envoie à {name} le lien obtenu.",
        $"{name}’s home isn’t on your network: Tailscale links the two, privately. On the machines page, next to Jellyfin’s PC, “…” → Share…, then send {name} the link it gives.");
    public static string NotOnTailnet => L("Le PC de Jellyfin n’est pas encore sur Tailscale : installe Tailscale dessus et connecte-le, puis partage-le d’ici.",
        "Jellyfin’s PC isn’t on Tailscale yet: install Tailscale on it and sign in, then share it from here.");
    public static string OpenTailscale => L("Ouvrir Tailscale", "Open Tailscale");
    public static string NotShared => L("Partagé avec personne pour l’instant · Mira vérifie tout seul", "Not shared with anyone yet · Mira checks by itself");
    public static string SharedWith(IReadOnlyList<string> names) => L($"Ce PC est partagé avec {Friends.Joined(names)}", $"This PC is shared with {Friends.Joined(names)}");
    public static string SendAway(string name) => L($"Envoie l’adresse à {name}", $"Send {name} the address");
    public static string AwayLine(string name) => L($"Celle qui marche de partout. Dans Mira, {name} se connecte à Tailscale (intégré), puis la tape.",
        $"The one that works from anywhere. In Mira, {name} connects to Tailscale (built in), then types it.");
    public static string AwayUnknown => L("Elle commence par 100. et apparaît ici dès que le PC de Jellyfin est sur Tailscale.",
        "It starts with 100. and shows here as soon as Jellyfin’s PC is on Tailscale.");
    public static string LetIn => L("Laisse entrer son appareil", "Let their device in");
    public static string LetInCode(string name) => L($"{name} choisit « Se connecter avec un code » et te donne les 6 chiffres. Aucun mot de passe à envoyer.",
        $"{name} chooses “Sign in with a code” and tells you the 6 digits. No password to send.");
    public static string LetInPassword(string name) => L($"{name} se connecte avec son prénom et ce mot de passe. Envoie-le-lui : Mira ne le garde pas.",
        $"{name} signs in with their name and this password. Send it to them: Mira doesn’t keep it.");
    public static string Party => L("Juste pour ce soir ? Ton lien de party ne demande ni compte ni Tailscale : qui l’ouvre rejoint ton groupe en invité.",
        "Only for tonight? Your party link needs no account and no Tailscale: whoever opens it joins your group as a guest.");
    public static string CopyParty => L("Copier ton lien de party", "Copy your party link");

    /// <summary>
    /// The login screen, when every try at this address timed out: what kind of address it is and what to do, the case
    /// of a friend given the address of someone else's home (ogsas, 2026-11-18). Null for any other address.
    /// </summary>
    public static string? NoAnswer(string address) => Of(address) switch
    {
        Kind.Home => L($"Pas de réponse. {HostOf(address)} est une adresse de maison : elle ne répond que sur le réseau où elle se trouve. Chez elle, vérifie que Jellyfin est lancé ; si ce serveur est chez un ami, demande-lui son adresse Tailscale (elle commence par 100.) et de partager son PC avec toi.",
            $"No answer. {HostOf(address)} is a home address: it only answers on the network it lives on. At that home, check that Jellyfin is running; if this server is at a friend’s home, ask them for its Tailscale address (it starts with 100.) and to share their PC with you."),
        Kind.Tailnet => L("Pas de réponse. C’est une adresse Tailscale : elle ne répond qu’une fois sur son réseau Tailscale. Accepte le lien de partage que ton ami t’a envoyé, connecte-toi à Tailscale (Mira l’intègre), et vérifie que son PC est allumé.",
            "No answer. This is a Tailscale address: it only answers once you’re on its Tailscale network. Accept the share link your friend sent you, connect to Tailscale (built into Mira), and check that their PC is on."),
        _ => null,
    };
}
