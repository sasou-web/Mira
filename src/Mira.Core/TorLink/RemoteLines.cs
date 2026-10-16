using System.Globalization;
using System.Net;

namespace Mira.Core.TorLink;

/// <summary>
/// « Télécharger sur le PC serveur » away from the PC of Jellyfin: what Mira for Windows or Mac writes of the PC, a
/// result, a request and a download, as Mira web writes it (js/torlink.js; cases shared in tests/fixtures/torlink-lines.json).
/// </summary>
public static class RemoteLines
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>The categories of a search, in Mira's words, in the order offered.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Categories = [("movies", "Films"), ("tv", "Séries"), ("anime", "Animes")];
    public static string CategoryLabel(string? key) => Categories.FirstOrDefault(x => x.Key == key).Label ?? "";

    /// <summary>What a pause, a resume or a cancel shows until TorLink did it.</summary>
    public static string? ActionLine(string? action) => action switch
    {
        "pause" => "Pause demandée au PC serveur…",
        "resume" => "Reprise demandée au PC serveur…",
        "delete" => "Annulation demandée au PC serveur…",
        _ => null
    };

    /// <summary>What the relay refuses, in Mira's words: an account that is not an administrator, an extension without it.</summary>
    public static string? Refused(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden => "Seuls les comptes administrateurs de Jellyfin peuvent télécharger sur le PC serveur.",
        HttpStatusCode.NotFound => "L’extension Mira de ton serveur ne connaît pas encore cet écran : mets à jour Mira sur le PC serveur, puis redémarre Jellyfin.",
        _ => null
    };

    /// <summary>« 850 Mo », « 4,2 Go », « 12 Go ».</summary>
    public static string Size(long bytes) => OfflineCopies.SizeText(Math.Max(0, bytes));

    /// <summary>« 2,4 Mo/s », « 350 Ko/s »; empty when nothing comes.</summary>
    public static string Speed(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? ""
        : bytesPerSecond >= 1L << 20 ? (bytesPerSecond / (double)(1L << 20)).ToString(bytesPerSecond >= 10L << 20 ? "0" : "0.0", French) + " Mo/s"
        : Math.Max(1, Round(bytesPerSecond / 1024.0)) + " Ko/s";

    private static string Ago(double seconds) =>
        seconds < 90 ? "à l’instant" : seconds < 5400 ? $"il y a {Round(seconds / 60)} min"
        : seconds < 129600 ? $"il y a {Round(seconds / 3600)} h" : $"il y a {Round(seconds / 86400)} jours";

    /// <summary>
    /// Where the PC of Jellyfin stands, from the relay's agent, at <paramref name="now"/>: <c>Ready</c> when it takes searches
    /// (online), even while its downloads must wait.
    /// </summary>
    public static (string Text, bool Ready) Agent(RelayAgent? agent, DateTimeOffset now)
    {
        if (agent is null) return ("Mira n’a pas encore été ouvert sur le PC serveur : c’est lui qui cherche et télécharge, avec TorLink. Ouvre-le là-bas, connecté avec un compte administrateur.", false);
        if (!agent.Online)
        {
            var seen = agent.Seen == default ? "" : $" (vu {Ago(Math.Max(0, (now - agent.Seen).TotalSeconds))})";
            return ($"Mira n’est pas ouvert sur {agent.Name}{seen} : tes demandes l’attendent, il les fera à son retour.", false);
        }
        if (!string.IsNullOrEmpty(agent.Problem)) return (agent.Problem, true);
        return ($"{agent.Name} est prêt : TorLink y télécharge, puis Mira range le titre dans ta bibliothèque.", true);
    }

    /// <summary>A search result: « 1,4 Go · 120 sources · YTS »; nothing of sources for a site that does not count them (SubsPlease).</summary>
    public static string Hit(RelayHit hit)
    {
        var seeders = Math.Max(0, hit.Seeders);
        var sources = !ReleaseRanking.ReportsHealth(hit.Source) && seeders == 0 ? "" : seeders switch { 0 => "aucune source", 1 => "1 source", _ => $"{seeders} sources" };
        return Join(Size(hit.Size), sources, hit.Source);
    }

    /// <summary>A request not yet in TorLink's list: waiting for the PC, on its way, or refused (and why).</summary>
    public static string Request(RelayRequest request) => request.State switch
    {
        "waiting" => "En attente du PC serveur",
        "claimed" => "Envoi à TorLink…",
        "sent" => string.IsNullOrEmpty(request.Error) ? "Envoyé à TorLink" : $"Envoyé à TorLink : {request.Error}",
        _ => string.IsNullOrEmpty(request.Error) ? "TorLink a refusé ce téléchargement." : request.Error
    };

    /// <summary>« moins d’une minute », « 12 min », « 1 h 05 », « 3 jours »: what is left, in seconds.</summary>
    public static string Remaining(double seconds)
    {
        var n = Math.Max(0, seconds);
        if (n < 60) return "moins d’une minute";
        var minutes = Round(n / 60);
        if (minutes < 60) return $"{minutes} min";
        if (minutes < 1440) return $"{minutes / 60} h {minutes % 60:00}";
        var days = Round(n / 86400);
        return days == 1 ? "1 jour" : $"{days} jours";
    }

    /// <summary>
    /// Seconds a download still needs: TorLink's own guess, or what is left of its size at its speed (an agent of 0.7.9
    /// sends neither); null when nothing comes or the size is unknown.
    /// </summary>
    public static double? TimeLeft(RelayDownload download)
    {
        if (download.Status != "downloading") return null;
        if (download.Eta is { } eta && eta > 0) return eta;
        if (download.Size <= 0 || download.Speed <= 0) return null;
        return Math.Max(0, download.Size - Math.Max(0, download.Downloaded)) / (double)download.Speed;
    }

    /// <summary>« 1,2 Go sur 2,9 Go », or « 2,9 Go » before anything came; empty when TorLink does not know the size yet.</summary>
    public static string Amount(RelayDownload download)
    {
        if (download.Size <= 0) return "";
        var done = Math.Min(download.Size, Math.Max(0, download.Downloaded));
        return done > 0 && done < download.Size ? $"{Size(done)} sur {Size(download.Size)}" : Size(download.Size);
    }

    /// <summary>
    /// A download in TorLink: « 42 % · 1,2 Go sur 2,9 Go · 2,4 Mo/s · reste 12 min · 12 pairs », « En pause · 42 % »,
    /// « Arrêté · 42 % ».
    /// </summary>
    public static string Download(RelayDownload download)
    {
        var progress = $"{Math.Clamp(download.Progress, 0, 100)} %";
        var peers = Math.Max(0, download.Peers);
        switch (download.Status)
        {
            case "downloading":
                var rate = Speed(download.Speed);
                if (rate.Length == 0) return Join(progress, Amount(download), peers switch { 0 => "recherche de pairs…", 1 => "1 pair, en attente de données", _ => $"{peers} pairs, en attente de données" });
                return Join(progress, Amount(download), rate, TimeLeft(download) is { } left ? $"reste {Remaining(left)}" : "", peers switch { 0 => "", 1 => "1 pair", _ => $"{peers} pairs" });
            case "queued": return Join($"En file d’attente · {progress}", Amount(download));
            case "paused": return Join($"En pause · {progress}", Amount(download));
            case "stopped": return $"Arrêté · {progress} · reprend quand TorLink rouvre sur le PC serveur";
            case "failed": return $"Échec · {progress}";
            case "completed" or "seeding": return "Terminé · rangement dans ta bibliothèque…";
            default: return progress;
        }
    }

    /// <summary>Downloads that can be paused, resumed or cancelled from here: under way, waiting, paused, stopped or failed.</summary>
    public static bool Controllable(RelayDownload download) => download.Status is "downloading" or "queued" or "paused" or "stopped" or "failed";

    /// <summary>Paused or stopped: « Reprendre » is offered rather than « Mettre en pause ».</summary>
    public static bool Paused(RelayDownload download) => download.Status is "paused" or "stopped";

    /// <summary>
    /// Requests TorLink does not list yet, before its downloads: one TorLink has now shows as its download, one sent and
    /// not listed yet as sent.
    /// </summary>
    public static IReadOnlyList<RelayRequest> Pending(RelayStatus status)
    {
        var inTorLink = status.Downloads.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        return status.Requests.Where(x => !(x.State == "sent" && inTorLink.Contains(x.InfoHash))).ToList();
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>What the relay of the extension refused, in Mira's words: not an administrator, an extension without it, a request it turned down.</summary>
public sealed class RelayRefusedException(string message, HttpStatusCode status) : HttpRequestException(message, null, status);
