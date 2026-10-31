namespace Mira.Core;

/// <summary>
/// Jellyfin's conversions on the graphics card checked again after an update (« Carte graphique revérifiée »): a new
/// driver, or a new Jellyfin, can break what worked when « Utiliser la carte graphique » kept the card, and phones then
/// only get « Lecture impossible ». Mira for Windows tries a conversion again (<see cref="JellyfinClient.ConversionWorksAsync"/>)
/// once Jellyfin's version or a card's driver changed, never while anyone watches; after two failed tries, Jellyfin goes
/// back to the processor and Mira says so. Decided here, so that the tests check every case.
/// </summary>
public static class GpuRecheck
{
    /// <summary>Failed tries in a row (one per look at most) after which Jellyfin goes back to the processor.</summary>
    public const int FailuresBeforeProcessor = 2;
    /// <summary>How often Mira looks while it runs (a driver or Jellyfin can update meanwhile), and so the gap between two tries.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(30);
    /// <summary>The first look, after the start has settled.</summary>
    public static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(2);

    /// <summary>
    /// What a check holds for: Jellyfin's version and each card with its driver (« 12.1.0 | NVIDIA GeForce RTX 3060
    /// 32.0.15.6094 »), in a stable order. Null when Jellyfin's version or the cards are unknown: nothing is judged.
    /// </summary>
    public static string? Fingerprint(string? jellyfinVersion, IEnumerable<string> drivers)
    {
        var cards = drivers.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return string.IsNullOrWhiteSpace(jellyfinVersion) || cards.Count == 0 ? null : string.Join(" | ", [jellyfinVersion.Trim(), .. cards]);
    }

    public enum Step { Nothing, Wait, Try }
    /// <summary>
    /// <paramref name="kind"/>: what Jellyfin converts with, null when unreadable (not an administrator);
    /// <paramref name="checkedFor"/>: the fingerprint of the last try that converted. Try only on a card, once something
    /// changed, and wait while anyone watches (a try converts on the same card as them).
    /// </summary>
    public static Step Decide(string? kind, string? fingerprint, string checkedFor, bool watching) =>
        kind is null or GpuEncoding.None || fingerprint is null || fingerprint == checkedFor ? Step.Nothing
        : watching ? Step.Wait
        : Step.Try;

    public enum Verdict { Kept, TryAgain, Processor }
    /// <summary>
    /// After a try: converted, or nothing to convert (no film nor episode: nothing to judge), the card is kept for this
    /// fingerprint; a first failure is tried again later; the second puts Jellyfin back on the processor.
    /// </summary>
    public static (Verdict Verdict, int Failures) After(bool? converted, int failures) => converted switch
    {
        true or null => (Verdict.Kept, 0),
        false when failures + 1 >= FailuresBeforeProcessor => (Verdict.Processor, 0),
        _ => (Verdict.TryAgain, failures + 1),
    };

    /// <summary>
    /// Jellyfin on the processor, and this PC has a card it could convert with: tried by Mira itself, once for this
    /// Jellyfin and these drivers, never while anyone watches, never once the person went back to the processor.
    /// </summary>
    public static bool TriesFirst(bool keptOff, int cards, string? fingerprint, string triedFor, bool watching) =>
        !keptOff && cards > 0 && fingerprint is not null && fingerprint != triedFor && !watching;

    /// <summary>What Mira says once it turned the card on by itself.</summary>
    public static string TurnedOn(string card) =>
        L($"Jellyfin convertit maintenant les vidéos avec ta {card} : Mira l’a essayée sur un de tes titres. Les conversions pour ton téléphone vont plus vite et laissent le processeur libre. Pour revenir au processeur : Réglages → Jellyfin.",
        $"Jellyfin now converts videos with your {card}: Mira tried it on one of your titles. Conversions for your phone are faster and leave the processor free. To go back to the processor: Settings → Jellyfin.");

    /// <summary>What Mira says once Jellyfin is back on the processor.</summary>
    public static string Notice(string kind) =>
        L($"Depuis la mise à jour de Jellyfin ou du pilote de ta carte graphique ({GpuEncoding.Maker(kind)}), elle n’a pas réussi deux essais de conversion : "
        + "Jellyfin convertit de nouveau avec le processeur, et tes titres se lisent comme avant. Pour la réessayer : Réglages → Jellyfin & synchronisation → Utiliser la carte graphique.",
        $"Since Jellyfin or your graphics card’s driver ({GpuEncoding.Maker(kind)}) was updated, the card failed two conversion tests: "
        + "Jellyfin converts with the processor again, and your titles play as before. To try it again: Settings → Jellyfin & sync → Use the graphics card.");
}
