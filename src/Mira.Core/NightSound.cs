namespace Mira.Core;

/// <summary>
/// « Son de nuit » of Mira for Windows and Mac (⋮ → Audio, kept in the settings): the loud passages brought down and the
/// quiet ones raised, so that voices stay clear at a low volume, late at night. A compressor, not a normaliser:
/// <c>dynaudnorm</c> looks ahead and delays the sound, <c>acompressor</c> adds no delay. It goes first in mpv's audio
/// chain, before the boost above 100 % (<see cref="VolumeBoost"/>), and never with passthrough (what goes untouched to an
/// amplifier cannot be filtered). Not on iPhone: AVPlayer has no audio filter.
/// </summary>
public static class NightSound
{
    /// <summary>The filter's label in mpv's audio chain.</summary>
    public const string Label = "mira-night";

    /// <summary>
    /// The filter for mpv's <c>af</c> list: above −24 dB (0.0625), a quarter of what passes the threshold is kept, with a
    /// soft knee; 10 ms to catch an explosion, 300 ms to let go so voices do not pump; +8 dB (2.5) of make-up gain
    /// raises what stayed quiet; then a limiter at −0.4 dB that adds no gain of its own. Measured with ffmpeg on a tone at
    /// −54 dB RMS then noise at −9 dB: −46 dB and −16 dB, a gap of 30 dB instead of 45.
    /// </summary>
    public const string Filter = $"@{Label}:lavfi=[acompressor=threshold=0.0625:ratio=4:attack=10:release=300:makeup=2.5:knee=4,alimiter=limit=0.95:level=disabled]";

    /// <summary>Whether the filter goes in mpv's chain: chosen, and the sound not sent to an amplifier as it is (<see cref="AudioOutput.Codecs"/>).</summary>
    public static bool Applies(bool chosen, string? passthrough) => chosen && AudioOutput.Codecs(passthrough) is null;

    /// <summary>The menu's line under its name: what it does, or why it does nothing now.</summary>
    public static string Details(bool chosen, string? passthrough, bool refused) =>
        refused ? Lang.L("Ce lecteur ne sait pas le faire.", "This player can’t do it.")
        : chosen && !Applies(chosen, passthrough) ? Lang.L("Pas avec le son envoyé à l’ampli", "Not with sound sent to the amplifier")
        : Lang.L("Explosions plus douces, voix plus claires", "Softer explosions, clearer voices");

    public static string Name => Lang.L("Son de nuit", "Night sound");
}
