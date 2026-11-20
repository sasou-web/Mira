namespace Mira.Core.Family;

/// <summary>Where watching away from home stands, in one word for the lists and in a sentence on its page.</summary>
public enum AwayLevel
{
    /// <summary>Nothing known yet (Jellyfin not read, or not answering).</summary>
    Unknown,
    /// <summary>It works from anywhere.</summary>
    Ready,
    /// <summary>Set up, but something is in the way (the tailnet's policy).</summary>
    Attention,
    /// <summary>Not set up: it works at home only.</summary>
    Off,
}

/// <summary>Watching away from home said once, the same in the list, on « L’essentiel », in the profile menu and on its page.</summary>
public sealed record AwayState(AwayLevel Level, string Word, string Title, string Text);

/// <summary>
/// « Loin de chez toi » (ogsas, 2026-11-20, maquette A « En tête de liste »): the three ways in from elsewhere on one page
/// of Réglages, and their state everywhere it is told. For the administrator of a Jellyfin with the home, the home's
/// Tailscale (its links, and friends' invitations) decides; for anyone else, this device's own way (built-in Tailscale, or
/// the Tailscale app on this computer).
/// </summary>
public static class AwayFromHome
{
    /// <summary>The state in a few words, for a tile.</summary>
    public static string Short(AwayLevel level) => level switch
    {
        AwayLevel.Ready => L("Les liens marchent de partout.", "Links work from anywhere."),
        AwayLevel.Attention => L("Ta politique Tailscale est à corriger.", "Your Tailscale policy needs fixing."),
        _ => L("Seulement à la maison pour l’instant.", "At home only for now."),
    };

    /// <param name="home">The home's Tailscale as its extension says it; null when this account doesn't administer a home.</param>
    /// <param name="homeKnown">False while the home is being read, or could not be.</param>
    /// <param name="youReach">This device reaches Jellyfin from anywhere by itself (built-in Tailscale signed in, or the Tailscale app here).</param>
    public static AwayState For(HomeTailnetState? home, bool homeKnown, bool youReach)
    {
        if (home is not null)
        {
            if (!home.Connected)
                return new(AwayLevel.Off, L("À régler", "Set up"), L("Laisse les tiens regarder d’ailleurs", "Let your people watch from elsewhere"),
                    L("Une seule fois : ensuite, les liens du foyer et les invitations marchent loin de chez toi.", "Once only: then home links and friend invites work away from home."));
            if (home.Policy is not ("ok" or "harden") || home.Broad)
                return new(AwayLevel.Attention, L("À vérifier", "Check"), L("Ta politique Tailscale est à corriger", "Your Tailscale policy needs fixing"),
                    L("Les liens attendent qu’elle soit juste : Mira te dit quoi changer, plus bas.", "Links wait until it’s right: Mira tells you what to change, below."));
            return new(AwayLevel.Ready, L("Prêt", "Ready"), L("Prêt : les tiens regardent de partout", "Ready: your people can watch from anywhere"),
                L("Les liens du foyer et les invitations marchent loin de chez toi. Les appareils qui entrent ne voient que Jellyfin sur ce PC.", "Home links and friend invites work away from home. Devices that join see only Jellyfin on this PC."));
        }
        if (!homeKnown && !youReach) return new(AwayLevel.Unknown, "", "", "");
        return youReach
            ? new(AwayLevel.Ready, L("Prêt", "Ready"), L("Prêt : tu regardes de partout", "Ready: you can watch from anywhere"),
                L("Mira joint ton Jellyfin par Tailscale quand tu n’es pas chez toi.", "Mira reaches your Jellyfin through Tailscale when you’re not at home."))
            : new(AwayLevel.Off, L("Coupé", "Off"), L("Seulement à la maison pour l’instant", "Only at home for now"),
                L("Connecte Tailscale intégré plus bas pour regarder ailleurs.", "Sign in to built-in Tailscale below to watch elsewhere."));
    }
}
