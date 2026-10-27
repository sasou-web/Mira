using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace Mira.Core;

/// <summary>
/// « Qualité » of Mira for Windows and Mac, as Mira web's (player/stream.js, its cases in tests/fixtures/bitrate.json):
/// « Automatique » plays the file as it is at home (the server is this computer or on the home network) and, elsewhere,
/// when the connection measured holds it; otherwise, and on a fixed rate, Jellyfin converts it under that rate.
/// </summary>
public static class StreamQuality
{
    public const string Auto = "auto", Max = "max";
    /// <summary>Full quality: the file as it is whenever Jellyfin can send it.</summary>
    public const long FullBitrate = 120_000_000;
    /// <summary>Asked when the connection could not be measured.</summary>
    public const long UnmeasuredBitrate = 8_000_000;
    /// <summary>How long a measure of the connection is kept.</summary>
    public static readonly TimeSpan MeasureKept = TimeSpan.FromHours(1);
    /// <summary>A connection that holds this many times the file's bitrate plays the file as it is.</summary>
    public const double FullMargin = 1.5;

    /// <summary>The choices of Réglages and of the player's menu, as Mira web's: key, name, short name (the player's chips), what it means.</summary>
    public static IReadOnlyList<(string Key, string Label, string Short, string Details)> Choices =>
    [
        (Auto, L("Automatique", "Automatic"), L("Auto", "Auto"), L("Pleine qualité à la maison ou quand ta connexion la tient, sinon adaptée à elle", "Full quality at home or when your connection can handle it, adjusted to it otherwise")),
        (Max, L("Maximale", "Maximum"), L("Max", "Max"), L("Le fichier tel quel dès que possible", "The file as it is, whenever possible")),
        ("20000000", Rate(20), Rate(20), L("1080p, très bonne qualité", "1080p, very good quality")),
        ("10000000", Rate(10), Rate(10), "1080p"),
        ("6000000", Rate(6), Rate(6), "720p"),
        ("3000000", Rate(3), Rate(3), "720p, 4G"),
        ("1500000", Rate(1.5), Rate(1.5), L("480p, connexion faible", "480p, weak connection")),
    ];
    private static string Rate(double megabits) => $"{megabits.ToString("0.#", Culture)} Mb/s";
    /// <summary>
    /// What the player says of a stream (the ⋮ menu): the file as it is, or Jellyfin's conversion, the bitrate it stays
    /// under and why Jellyfin converts (its two first reasons).
    /// </summary>
    public static string Describe(ServerStream stream) => !stream.Converted ? L("Fichier tel quel", "The file as it is")
        : string.Join(" · ", new[] { L("Converti par Jellyfin", "Converted by Jellyfin"), L($"jusqu’à {Rate(stream.Bitrate / 1e6)}", $"up to {Rate(stream.Bitrate / 1e6)}") }.Concat(stream.Reasons.Take(2).Select(Viewers.Reason)));
    /// <summary>A saved choice Mira knows, else « Automatique ».</summary>
    public static string Known(string? choice) => Choices.Any(x => x.Key == choice) ? choice! : Auto;
    /// <summary>The name of a choice, for the player's menu and its information.</summary>
    public static string Label(string? choice) => Choices.First(x => x.Key == Known(choice)).Label;

    /// <summary>
    /// The bitrate known without measuring: the one chosen, full quality (« Maximale », or at home on « Automatique »),
    /// or what a measure still fresh allows for this file (<see cref="AutoBitrate"/>); null when the connection must be measured.
    /// </summary>
    public static long? KnownBitrate(string? choice, bool atHome, ConnectionMeasure? measure, DateTimeOffset now, long fileBitrate = 0)
    {
        choice = Known(choice);
        if (long.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate)) return rate;
        if (choice == Max || atHome) return FullBitrate;
        if (measure is { } fresh && now - fresh.At < MeasureKept) return AutoBitrate(fresh.Speed, fileBitrate);
        return null;
    }
    /// <summary>What a measured speed (bits per second) allows for a conversion: 70 % of it, between 1.5 and 40 Mb/s.</summary>
    public static long MeasuredBitrate(double speed) => Math.Max(1_500_000, Math.Min(40_000_000, (long)Math.Round(speed * 0.7, MidpointRounding.AwayFromZero)));
    /// <summary>
    /// « Automatique » with a measure: full quality, the file as it is (a 4K film or a remux above 40 Mb/s too), when the
    /// connection holds about one and a half times its bitrate; else what the measure allows, converted.
    /// </summary>
    public static long AutoBitrate(double speed, long fileBitrate) => fileBitrate > 0 && speed >= fileBitrate * FullMargin ? FullBitrate : MeasuredBitrate(speed);

    /// <summary>
    /// Whether a server is « at home »: this computer, an address of the home network (private, link-local), or a name
    /// only a home network knows (« nas », « pc.local », « pc.lan »). Not Tailscale: its address is the same from a hotel
    /// or on a phone's 4G, where only a measure tells what the line takes (as Mira web, isLocalHost without viaTailscale).
    /// </summary>
    public static bool AtHome(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) return false;
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (uri.IsLoopback) return true;
        if (IPAddress.TryParse(host, out var address))
        {
            if (LocalNetwork.IsTailnet(address.ToString()) || IsTailnetIPv6(address)) return false;
            return LocalNetwork.IsHome(address) || ThisComputer(host);
        }
        if (host.EndsWith(".ts.net", StringComparison.Ordinal)) return ThisComputer(host);
        return !host.Contains('.') || HomeSuffixes.Any(x => host.EndsWith(x, StringComparison.Ordinal)) || ThisComputer(host);
    }
    private static readonly string[] HomeSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".fritz.box", ".localhost"];
    /// <summary>Tailscale's IPv6 range, fd7a:115c:a1e0::/48.</summary>
    private static bool IsTailnetIPv6(IPAddress address) => address.GetAddressBytes() is [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0, ..];
    /// <summary>This computer under a name or an address of its own (its Tailscale name too): nothing goes over a network.</summary>
    private static bool ThisComputer(string host) => LocalNetwork.IsThisPc(host);
}

/// <summary>A measure of the connection to Jellyfin: when, and its speed in bits per second.</summary>
public sealed record ConnectionMeasure(DateTimeOffset At, double Speed);

/// <summary>
/// The connection's speed to Jellyfin, measured once for whoever asks meanwhile and kept an hour
/// (<see cref="StreamQuality.MeasureKept"/>); forgotten when this computer's network changes (another Wi-Fi, back home).
/// </summary>
public sealed class ConnectionSpeed
{
    private ConnectionMeasure? _measure;
    private Task<double?>? _measuring;
    private readonly object _gate = new();
    public ConnectionSpeed() { NetworkChange.NetworkAddressChanged += (_, _) => Forget(); }
    public ConnectionMeasure? Last => _measure;
    public void Forget() { lock (_gate) _measure = null; }
    /// <summary>
    /// The bitrate to ask for a file of <paramref name="fileBitrate"/>: the choice's, or, on « Automatique » away from
    /// home, what the connection measured by <paramref name="measure"/> allows (<see cref="StreamQuality.UnmeasuredBitrate"/> when it could not be).
    /// </summary>
    public async Task<long> BitrateAsync(string? choice, bool atHome, long fileBitrate, Func<Task<double>> measure)
    {
        if (StreamQuality.KnownBitrate(choice, atHome, _measure, DateTimeOffset.UtcNow, fileBitrate) is { } known) return known;
        var speed = await MeasureAsync(measure);
        return speed is { } value ? StreamQuality.AutoBitrate(value, fileBitrate) : StreamQuality.UnmeasuredBitrate;
    }
    private Task<double?> MeasureAsync(Func<Task<double>> measure)
    {
        lock (_gate)
            return _measuring ??= Run();
        async Task<double?> Run()
        {
            try
            {
                var speed = await measure();
                lock (_gate) _measure = new(DateTimeOffset.UtcNow, speed);
                return speed;
            }
            catch (Exception ex) when (ExpectedErrors.Are(ex) || ex is OperationCanceledException) { return null; }
            finally { lock (_gate) _measuring = null; }
        }
    }
}
