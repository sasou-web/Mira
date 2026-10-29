using System.Text;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// « Sortie audio » of Mira for Windows (Réglages → Audio et sous-titres, and the player's ⋮): the device mpv plays to,
/// the channels it sends, and the compressed formats it hands untouched to an amplifier or a sound bar (passthrough:
/// they decode it, Dolby Atmos and DTS:X included, which a PCM signal loses; an optical cable carries 5.1 only that way).
/// Windows' default device and channels are mpv's own defaults; a device chosen but unplugged plays to the default.
/// </summary>
public static class AudioOutput
{
    /// <summary>mpv's « auto »: Windows' default device, following it when it changes.</summary>
    public const string Default = "auto";
    public const string AutoChannels = "auto-safe";
    public const string Off = "off", Optical = "optical", Hdmi = "hdmi";

    /// <summary>
    /// mpv's <c>audio-channels</c>: Windows' layout for the device (Paramètres → Son), or a list from the widest down,
    /// so a stereo film stays stereo; never mpv's <c>auto</c>, which over HDMI sends layouts a receiver may not take.
    /// </summary>
    public static IReadOnlyList<(string Key, string Label, string Details)> ChannelChoices =>
    [
        (AutoChannels, L("Automatique", "Automatic"), L("Ce que Windows règle pour cette sortie (Paramètres → Son)", "What Windows sets for this output (Settings → Sound)")),
        ("stereo", L("Stéréo", "Stereo"), L("Tout mixé en deux canaux : casque, enceintes de PC", "Everything mixed to two channels: headphones, PC speakers")),
        ("5.1,stereo", "5.1", L("Jusqu’à 5.1, pour un ampli ou une barre de son 5.1", "Up to 5.1, for a 5.1 amplifier or sound bar")),
        ("7.1,5.1,stereo", "7.1", L("Jusqu’à 7.1, pour un ampli 7.1", "Up to 7.1, for a 7.1 amplifier")),
    ];
    public static IReadOnlyList<(string Key, string Label, string Details)> PassthroughChoices =>
    [
        (Off, L("Non", "Off"), L("Mira décode le son, le volume reste dans Mira", "Mira decodes the sound; the volume stays in Mira")),
        (Optical, L("Câble optique", "Optical cable"), L("Dolby Digital et DTS envoyés tels quels", "Dolby Digital and DTS sent as they are")),
        (Hdmi, "HDMI", L("Aussi Dolby TrueHD, Atmos et DTS-HD", "Also Dolby TrueHD, Atmos and DTS-HD")),
    ];
    public static string KnownChannels(string? choice) => ChannelChoices.Any(x => x.Key == choice) ? choice! : AutoChannels;
    public static string KnownPassthrough(string? choice) => PassthroughChoices.Any(x => x.Key == choice) ? choice! : Off;
    /// <summary>mpv's <c>audio-spdif</c>: an optical (S/PDIF) cable carries only Dolby Digital and the DTS core.</summary>
    public static string? Codecs(string? passthrough) => KnownPassthrough(passthrough) switch
    {
        Optical => "ac3,dts",
        Hdmi => "ac3,eac3,dts-hd,truehd",
        _ => null,
    };
    /// <summary>mpv's options for the settings, each one every choice sets: its value, or null for mpv's own default.</summary>
    public static IReadOnlyList<(string Name, string? Value)> Options(PlayerSettings settings) =>
    [
        ("audio-channels", KnownChannels(settings.AudioChannels) is var channels && channels != AutoChannels ? channels : null),
        ("audio-spdif", Codecs(settings.AudioPassthrough)),
    ];

    /// <summary>
    /// The devices of mpv's <c>audio-device-list</c> (JSON) for one of its outputs (« wasapi » on Windows), as named
    /// by the system; mpv's « auto » left out.
    /// </summary>
    public static IReadOnlyList<(string Name, string Label)> Devices(string? list, string driver)
    {
        try
        {
            using var doc = JsonDocument.Parse(list ?? "[]");
            var devices = new List<(string, string)>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var n) || n.GetString() is not { } name || !name.StartsWith(driver + "/", StringComparison.Ordinal)) continue;
                if (devices.Any(x => x.Item1 == name)) continue;
                var label = entry.TryGetProperty("description", out var d) && d.GetString() is { } text && text.Trim().Length > 0 ? text.Trim() : name[(driver.Length + 1)..];
                devices.Add((name, label));
            }
            return devices;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return []; }
    }
    /// <summary>The device to play to: the one chosen while the system has it, else the default.</summary>
    public static string Device(string? chosen, IEnumerable<(string Name, string Label)> present) =>
        chosen is { Length: > 0 } name && name != Default && present.Any(x => x.Name == name) ? name : Default;

    // WAVE_FORMAT_EXTENSIBLE speaker bits, in the order of a frame's samples.
    private const int FrontLeft = 0x1, FrontRight = 0x2, Center = 0x4, Lfe = 0x8, BackLeft = 0x10, BackRight = 0x20, SideLeft = 0x200, SideRight = 0x400;
    private static IReadOnlyList<(int Bit, string Name)> Speakers(string? channels)
    {
        (int, string)[] stereo = [(FrontLeft, L("gauche", "left")), (FrontRight, L("droite", "right"))];
        (int, string)[] surround = [(FrontLeft, L("avant gauche", "front left")), (FrontRight, L("avant droite", "front right")), (Center, L("centre", "center")), (Lfe, L("caisson de basses", "subwoofer")), (BackLeft, L("arrière gauche", "rear left")), (BackRight, L("arrière droite", "rear right"))];
        return KnownChannels(channels) switch
        {
            "5.1,stereo" => surround,
            "7.1,5.1,stereo" => [.. surround, (SideLeft, L("côté gauche", "side left")), (SideRight, L("côté droit", "side right"))],
            // Automatic: two speakers named, Windows spreads them over its own layout.
            _ => stereo,
        };
    }
    public const int TestRate = 48_000;
    public static readonly TimeSpan TestStep = TimeSpan.FromSeconds(.75), TestTone = TimeSpan.FromSeconds(.6);
    /// <summary>
    /// « Tester »: a short tone on each speaker in turn (low for the subwoofer), as a 16-bit WAV file with its speaker
    /// layout, and the speakers in the order heard. Made here: nothing to download, nothing mpv must know how to build.
    /// </summary>
    public static (byte[] Wav, IReadOnlyList<string> Speakers) TestSound(string? channels)
    {
        var speakers = Speakers(channels);
        int count = speakers.Count, step = (int)(TestRate * TestStep.TotalSeconds), tone = (int)(TestRate * TestTone.TotalSeconds), frames = step * count;
        var mask = speakers.Aggregate(0, (all, s) => all | s.Bit);
        var data = frames * count * 2;
        using var stream = new MemoryStream(68 + data);
        using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(60 + data); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(40); w.Write((ushort)0xFFFE); w.Write((ushort)count); w.Write(TestRate); w.Write(TestRate * count * 2); w.Write((ushort)(count * 2)); w.Write((ushort)16);
            // cbSize, valid bits, channel mask, KSDATAFORMAT_SUBTYPE_PCM.
            w.Write((ushort)22); w.Write((ushort)16); w.Write(mask); w.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
            w.Write("data"u8); w.Write(data);
            for (var frame = 0; frame < frames; frame++)
                for (var channel = 0; channel < count; channel++)
                {
                    var at = frame - channel * step; var sample = 0.0;
                    if (at >= 0 && at < tone)
                    {
                        var hertz = speakers[channel].Bit == Lfe ? 60 : 440;
                        // A rounded start and end: no click.
                        sample = .3 * Math.Sin(2 * Math.PI * hertz * at / TestRate) * Math.Sin(Math.PI * at / tone);
                    }
                    w.Write((short)Math.Round(sample * short.MaxValue));
                }
        }
        return (stream.ToArray(), speakers.Select(s => s.Name).ToList());
    }
}
