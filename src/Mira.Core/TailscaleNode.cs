using System.Globalization;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// Tailscale on the PC Jellyfin runs on, as its command line tells it (<c>tailscale status --json</c>, then
/// <c>tailscale debug prefs</c>; on Windows, the person who signed in to Tailscale reads and changes them without
/// administrator rights): whether it stays connected without a Windows session (« Run unattended », its ForceDaemon
/// preference), and when this PC's connection to the tailnet expires (its node key, after 180 days by default). Without
/// the first, phones away from home stop finding this PC after a restart nobody signs in after (Windows Update at
/// night); on the day of the second, until someone signs in to Tailscale again on it. Decided here, so that the tests
/// check every case; read by Mira for Windows.
/// </summary>
public static class TailscaleNode
{
    /// <summary>
    /// <paramref name="Readable"/>: Tailscale's command line answered. <paramref name="Unattended"/>: null when its
    /// preferences could not be read. <paramref name="KeyExpiry"/>: null when this PC's connection never expires.
    /// <paramref name="ReadAt"/>: when, which tells how soon it expires.
    /// </summary>
    public sealed record State(bool Readable, bool? Unattended = null, DateTimeOffset? KeyExpiry = null, bool Expired = false, string? Name = null, DateTimeOffset ReadAt = default);
    public static readonly State Unreadable = new(false);
    /// <summary>Days before the expiry from which Mira says it.</summary>
    public const int WarnDays = 14;
    /// <summary>Tailscale's admin console, where each machine's menu has « Disable key expiry ».</summary>
    public const string Machines = "https://login.tailscale.com/admin/machines";
    public static string KeepConnected => L("Garder Tailscale connecté", "Keep Tailscale connected");
    public static string OpenMachines => L("Ouvrir la page de Tailscale", "Open Tailscale’s page");
    /// <summary>Where the person ticks it, when Mira cannot.</summary>
    public static string WhereUnattended => L("dans le menu de Tailscale (son icône près de l’horloge, clic droit) → Preferences, coche « Run unattended »",
        "in Tailscale’s menu (its icon near the clock, right-click) → Preferences, tick “Run unattended”");

    /// <summary>
    /// <paramref name="status"/>: the output of <c>tailscale status --json</c>, null when it failed; <paramref name="prefs"/>:
    /// that of <c>tailscale debug prefs</c> (JSON, where ForceDaemon is left out when false).
    /// </summary>
    public static State Parse(string? status, string? prefs, DateTimeOffset now)
    {
        if (status is null) return Unreadable;
        try
        {
            using var document = JsonDocument.Parse(status);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("BackendState", out var backend) || backend.ValueKind != JsonValueKind.String) return Unreadable;
            DateTimeOffset? expiry = null; var expired = false; string? name = null;
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                if (self.TryGetProperty("KeyExpiry", out var at) && at.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) expiry = parsed;
                expired = self.TryGetProperty("Expired", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (self.TryGetProperty("HostName", out var host) && host.ValueKind == JsonValueKind.String && host.GetString() is { Length: > 0 } hostName) name = hostName;
            }
            return new(true, Unattended(prefs), expiry, expired || expiry <= now, name, now);
        }
        catch (JsonException) { return Unreadable; }
    }
    private static bool? Unattended(string? prefs)
    {
        if (prefs is null) return null;
        try
        {
            using var document = JsonDocument.Parse(prefs);
            var root = document.RootElement;
            // ControlURL is always written: what tells its preferences from an error.
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ControlURL", out _)) return null;
            return root.TryGetProperty("ForceDaemon", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Expired, or expiring within <see cref="WarnDays"/> days of when it was read.</summary>
    public static bool ExpiresSoon(State state) => state.Readable && (state.Expired || state.KeyExpiry is { } at && at - state.ReadAt <= TimeSpan.FromDays(WarnDays));
    /// <summary>Its day, in this PC's time zone (« 3 novembre », "November 3").</summary>
    public static string Day(DateTimeOffset at) => Lang.Day(at.ToLocalTime().DateTime);

    /// <summary>Said once at Mira's start for an expiry within <see cref="WarnDays"/> days; null otherwise.</summary>
    public static string? Warning(State state) => !ExpiresSoon(state) ? null : state.Expired
        ? L("La connexion de ce PC à Tailscale a expiré : hors de chez toi, ton téléphone ne le trouve plus. Reconnecte-toi dans le menu de Tailscale, puis coupe cette expiration sur sa page Machines.",
            "This PC’s Tailscale connection has expired: away from home, your phone can’t find it anymore. Sign in again from Tailscale’s menu, then turn off this expiry on its Machines page.")
        : L($"La connexion de ce PC à Tailscale expire le {Day(state.KeyExpiry!.Value)} : ensuite, hors de chez toi, ton téléphone ne le trouvera plus. Coupe cette expiration sur la page Machines de Tailscale.",
            $"This PC’s Tailscale connection expires on {Day(state.KeyExpiry!.Value)}: after that, away from home, your phone won’t find it. Turn off this expiry on Tailscale’s Machines page.");

    /// <summary>On the machine's line of the page Machines: its menu, then « Disable key expiry ».</summary>
    public static string Disable(State state) => L($"sur la page Machines de Tailscale, ouvre le menu ⋯ de « {state.Name ?? "ce PC"} » puis choisis « Disable key expiry »",
        $"on Tailscale’s Machines page, open the ⋯ menu of “{state.Name ?? "this PC"}” and choose “Disable key expiry”");
}
