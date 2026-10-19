namespace Mira.Core;

/// <summary>
/// The PC Jellyfin runs on, asleep: unreachable, from home too, and Tailscale cannot wake it. What Windows' power plan
/// does on mains power (sleep after some idle time, hibernation, closing a laptop's lid) is said on the phone's card,
/// with « Pas de veille sur secteur » at the person's choice, never on battery; and while Mira runs, even minimized,
/// the PC is kept awake as long as another device plays from Jellyfin. Jellyfin 12.1 does neither. Decided here, so
/// that the tests check every case; read and set by Mira for Windows.
/// </summary>
public static class ServerSleep
{
    /// <summary>
    /// On mains power: <paramref name="Standby"/> and <paramref name="Hibernate"/>, idle seconds before sleep and
    /// hibernation (0: never; hibernation after sleep, or alone); <paramref name="LidAction"/>, null without a lid,
    /// otherwise 0 nothing, 1 sleep, 2 hibernate, 3 shut down. <paramref name="Battery"/>: the PC has one.
    /// </summary>
    public sealed record Plan(int Standby, int Hibernate, int? LidAction, bool Battery);
    public static string NeverOnMains => L("Pas de veille sur secteur", "No sleep when plugged in");

    public static bool Sleeps(Plan plan) => plan.Standby > 0 || plan.Hibernate > 0 || plan.LidAction is > 0;

    /// <summary>« 30 min », « 1 h », « 1 h 30 », « 45 s » ("1 hr", "1 hr 30 min").</summary>
    public static string Duration(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 => $"{seconds / 60} min",
        _ when seconds / 60 % 60 == 0 => L($"{seconds / 3600} h", $"{seconds / 3600} hr"),
        _ => L($"{seconds / 3600} h {seconds / 60 % 60:00}", $"{seconds / 3600} hr {seconds / 60 % 60} min"),
    };

    /// <summary>The phone card's step: its title and its text.</summary>
    public static (string Title, string Text) Says(Plan plan)
    {
        if (!Sleeps(plan)) return (L("Ce PC reste éveillé sur secteur", "This PC stays awake when plugged in"),
            L("Ton téléphone le trouve à toute heure", "Your phone finds it at any time") + (plan.Battery ? L(", tant qu’il est branché.", ", as long as it’s plugged in.") : "."));
        var lid = plan.LidAction is > 0;
        var title = plan.Standby > 0 ? L($"Ce PC se met en veille après {Duration(plan.Standby)}", $"This PC goes to sleep after {Duration(plan.Standby)}")
            : plan.Hibernate > 0 ? L($"Ce PC se met en veille prolongée après {Duration(plan.Hibernate)}", $"This PC hibernates after {Duration(plan.Hibernate)}")
            : plan.LidAction == 3 ? L("Fermer son écran éteint ce PC", "Closing its lid shuts this PC down") : L("Fermer son écran met ce PC en veille", "Closing its lid puts this PC to sleep");
        var text = L("Endormi, il est injoignable : loin de chez toi, ton téléphone ne le trouve plus, et Tailscale ne sait pas le réveiller.",
                "Asleep, it can’t be reached: away from home, your phone no longer finds it, and Tailscale can’t wake it up.")
            + (lid && (plan.Standby > 0 || plan.Hibernate > 0) ? (plan.LidAction == 3 ? L(" Fermer son écran l’éteint aussi.", " Closing its lid shuts it down too.") : L(" Fermer son écran l’endort aussi.", " Closing its lid puts it to sleep too.")) : "")
            + L(" Tant que Mira est ouvert, même réduit, il le garde éveillé pendant qu’un autre appareil regarde un film.", " While Mira is open, even minimized, it keeps it awake while another device watches a movie.")
            + L($" « {NeverOnMains} » le garde éveillé tout le temps quand il est branché", $" “{NeverOnMains}” keeps it awake all the time while it’s plugged in")
            + (lid ? L(", écran fermé compris", ", lid closed included") : "") + (plan.Battery ? L(", jamais sur batterie.", ", never on battery.") : ".");
        return (title, text);
    }

    /// <summary>Another device playing from Jellyfin, paused or not (until Jellyfin forgets it): this PC kept awake.</summary>
    public static bool KeepAwake(IEnumerable<Viewer> viewers) => viewers.Any(x => !x.Own);
}
