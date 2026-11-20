using System.Net;
using Mira.Core.TorLink;

namespace Mira.Core.Family;

/// <summary>Friends' links and devices as the administrator sees them (GET Mira/friend).</summary>
public sealed record FriendsState(string? Tailnet, string? Tag, IReadOnlyList<FriendInviteRow>? Invites, IReadOnlyList<FriendDeviceRow>? Devices)
{
    /// <summary>Whether links can be made: the home is on Tailscale and Tailscale answers.</summary>
    public bool Ready => Tailnet == "ok";
    /// <summary>A friend's line: their devices, latest first, and the link still waiting for them, if any.</summary>
    public (IReadOnlyList<FriendDeviceRow> Devices, FriendInviteRow? Waiting) Of(string userId, DateTimeOffset now) =>
        ((Devices ?? []).Where(x => x.User == userId).ToList(), (Invites ?? []).FirstOrDefault(x => x.User == userId && x.State == "open" && x.Until > now));
}

public sealed record FriendInviteRow(string Id, string User, string Name, DateTimeOffset Created, DateTimeOffset Until, DateTimeOffset? Used, string State);

public sealed record FriendDeviceRow(string Id, string User, string Name, DateTimeOffset Joined, bool Tailnet, DateTimeOffset? LastSeen, bool Waiting = false);

/// <summary>A device waiting at a friend's server to be let in: kept (encrypted) so the wait goes on after Mira restarts.</summary>
public sealed record FriendWait(string Server, string Wait);

/// <summary>A friend's link made: the link, its end, its id (to cancel it).</summary>
public sealed record FriendLinkMade(string Link, DateTimeOffset Expires, string Id);

/// <summary>The extension's refusal of a friend's link, in its words, and its state (« used », « ended », « wrong », « disconnected », « policy »…).</summary>
public sealed class FriendRefusedException(string message, HttpStatusCode status, string state) : RelayRefusedException(message, status)
{
    public string State { get; } = state;
}

/// <summary>
/// The words of « Inviter un ami » by a link, both sides, both languages (Windows and Mac; the iPhone has its own,
/// the same). The friend's arrival says, before they join, that their device joins the host's Tailscale network.
/// </summary>
public static class FriendText
{
    // ---- The friend's side ----
    public static string Invited => L("Tu es invité à regarder", "You’re invited to watch");
    public static string Welcome(string name) => L($"Bienvenue, {name}", $"Welcome, {name}");
    public static string On(string server) => L($"Sur {server}", $"On {server}");
    public static string PrivateLine => L("Une ligne privée vers le PC de ton ami, faite pour toi. Rien à installer ni à créer.",
        "A private line to your friend’s PC, made for you. Nothing to install or sign up for.");
    public static string Visible => L("Ton ami voit le nom de cet appareil, son système, son adresse IP publique et sa dernière connexion, et ce que tu regardes sur son serveur, comme sur tout serveur partagé.",
        "Your friend sees this device’s name, its system, its public IP address and when it last connected, and what you watch on their server, like on any shared server.");
    public static string Start => L("Commencer à regarder", "Start watching");
    public static string NotNow => L("Pas maintenant", "Not now");
    public static string Reaching => L("Connexion au serveur de ton ami…", "Reaching your friend’s server…");
    public static string Used => L("Cette invitation a déjà servi", "This invite was already used");
    public static string UsedLine => L("Une invitation ne sert qu’une fois. Demandes-en une nouvelle à ton ami : un clic lui suffit.",
        "An invite works once. Ask your friend for a new one: it takes them one click.");
    public static string Ended => L("Cette invitation a expiré", "This invite has ended");
    public static string EndedLine => L("Une invitation dure 48 heures. Demandes-en une nouvelle à ton ami : un clic lui suffit.",
        "An invite lasts 48 hours. Ask your friend for a new one: it takes them one click.");
    public static string HaveAddress => L("J’ai une adresse à la place", "I have an address instead");
    public static string NoExtension => L("Ce serveur n’a pas encore l’extension Mira à jour : demande à ton ami de mettre Mira à jour.",
        "This server doesn’t have the up-to-date Mira extension yet: ask your friend to update Mira.");
    public static string InHomeAlready => L("Cet appareil est déjà dans un foyer rejoint par un lien. Quitte ce foyer d’abord, puis rouvre l’invitation.",
        "This device is already in a home joined by a link. Leave that home first, then open the invite again.");
    public static string Removed => L("Ton ami a retiré cet appareil de son serveur. Demande-lui une nouvelle invitation.",
        "Your friend took this device off their server. Ask them for a new invite.");

    // ---- The administrator's side ----
    public static string Invite(string name) => L($"Faire l’invitation de {name}", $"Make {name}’s invite");
    public static string SendTo(string name) => L($"Envoie ce lien à {name}", $"Send this link to {name}");
    public static string SendLine => L("Il ouvre Mira sur ton serveur, de chez toi ou d’ailleurs. Ni adresse à taper, ni mot de passe, ni compte Tailscale. Il sert une fois et expire dans 48 heures.",
        "It opens Mira on your server, from your home or anywhere else. No address to type, no password, no Tailscale account. It works once and ends in 48 hours.");
    public static string CopyLink => L("Copier le lien", "Copy link");
    public static string NewLink => L("Faire un nouveau lien", "Make a new link");
    public static string Waiting(string name) => L($"En attente que {name} l’ouvre", $"Waiting for {name} to open it");
    public static string Cancel => L("Annuler l’invitation", "Cancel the invite");
    public static string Joined(string device, DateTimeOffset when, DateTimeOffset now) => L($"{device} est arrivé {Ago(now - when)}", $"{device} joined {Ago(now - when)}");
    /// <summary>A friend's device seen by Tailscale: when it last connected.</summary>
    public static string Seen(DateTimeOffset when, DateTimeOffset now) => now - when < TimeSpan.FromMinutes(5) ? L("en ligne", "online") : L($"vu {Ago(now - when)}", $"seen {Ago(now - when)}");
    private static string Ago(TimeSpan span)
    {
        var minutes = Math.Max(0, span.TotalMinutes);
        if (minutes < 1.5) return L("à l’instant", "just now");
        if (minutes < 90) return L($"il y a {Math.Round(minutes)} min", $"{Math.Round(minutes)} min ago");
        if (minutes < 2160) return L($"il y a {Math.Round(minutes / 60)} h", $"{Math.Round(minutes / 60)} hr ago");
        var days = Count((int)Math.Round(minutes / 1440), "jour", "jours", "day", "days");
        return L($"il y a {days}", $"{days} ago");
    }
    public static string WaitingForHost => L("Ton ami doit laisser entrer cet appareil : il le voit dans Mira. Garde cette fenêtre ouverte.",
        "Your friend needs to let this device in: they see it in Mira. Keep this window open.");
    public static string Refused => L("Ton ami n’a pas laissé entrer cet appareil. Demande-lui une nouvelle invitation.",
        "Your friend didn’t let this device in. Ask them for a new invite.");
    public static string Knocking(string device) => L($"{device} attend d’entrer", $"{device} is waiting to come in");
    public static string LetIn => L("Laisser entrer", "Let in");
    public static string Refuse => L("Refuser", "Refuse");
    public static string AddDevice => L("Ajouter un appareil", "Add a device");
    public static string RemoveDevice => L("Retirer cet appareil", "Remove this device");
    public static string RemoveDeviceAsk => L("Retirer cet appareil ?", "Remove this device?");
    public static string In => L("Connecté", "In");
    public static string SetUpFirst => L("Pour qu’un ami regarde de chez lui, relie d’abord Mira à Tailscale (Réglages → Loin de chez toi, environ 3 minutes, une seule fois).",
        "For a friend to watch from their home, first connect Mira to Tailscale (Settings → Away from home, about 3 minutes, once).");
    public static string OnlyAtHome => L("Seulement chez moi pour l’instant", "Only at my home for now");
    public static string SetUp => L("Configurer…", "Set up…");
    public static string Copied => L("Lien copié : envoie-le à ton ami.", "Link copied: send it to your friend.");
}
