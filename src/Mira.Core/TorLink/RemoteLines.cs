using System.Net;

namespace Mira.Core.TorLink;

/// <summary>
/// « Télécharger sur le PC serveur » away from the PC of Jellyfin: what Mira for Windows or Mac writes of the PC, a
/// result, a request and a download, as Mira web writes it (js/torlink.js; cases shared in tests/fixtures/torlink-lines.json).
/// </summary>
public static class RemoteLines
{
    /// <summary>What a title found in a category is, on its card: « Film », « Série », « Anime »; empty when unknown.</summary>
    public static string KindLabel(string? key) => key switch { "movies" => L("Film", "Movie"), "tv" => L("Série", "Show"), "anime" => L("Anime", "Anime"), _ => "" };

    /// <summary>What a pause, a resume or a cancel shows until TorLink did it.</summary>
    public static string? ActionLine(string? action) => action switch
    {
        "pause" => L("Pause demandée au PC serveur…", "Pause sent to the server PC…"),
        "resume" => L("Reprise demandée au PC serveur…", "Resume sent to the server PC…"),
        "delete" => L("Annulation demandée au PC serveur…", "Cancel sent to the server PC…"),
        _ => null
    };

    /// <summary>What the relay refuses, in Mira's words: an account that is not an administrator, an extension without it.</summary>
    public static string? Refused(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden => L("Seuls les comptes administrateurs de Jellyfin peuvent télécharger sur le PC serveur.", "Only Jellyfin administrator accounts can download on the server PC."),
        HttpStatusCode.NotFound => L("L’extension Mira de ton serveur ne connaît pas encore cet écran : mets à jour Mira sur le PC serveur, puis redémarre Jellyfin.",
            "Your server’s Mira plugin doesn’t know this screen yet: update Mira on the server PC, then restart Jellyfin."),
        _ => null
    };

    /// <summary>« 850 Mo », « 4,2 Go », « 12 Go ».</summary>
    public static string Size(long bytes) => OfflineCopies.SizeText(Math.Max(0, bytes));

    /// <summary>« 2,4 Mo/s », « 350 Ko/s »; empty when nothing comes.</summary>
    public static string Speed(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? ""
        : bytesPerSecond >= 1L << 20 ? (bytesPerSecond / (double)(1L << 20)).ToString(bytesPerSecond >= 10L << 20 ? "0" : "0.0", Culture) + L(" Mo/s", " MB/s")
        : Math.Max(1, Round(bytesPerSecond / 1024.0)) + L(" Ko/s", " KB/s");

    private static string Ago(double seconds) =>
        seconds < 90 ? L("à l’instant", "just now") : seconds < 5400 ? L($"il y a {Round(seconds / 60)} min", $"{Round(seconds / 60)} min ago")
        : seconds < 129600 ? L($"il y a {Round(seconds / 3600)} h", $"{Round(seconds / 3600)} hr ago") : L($"il y a {Round(seconds / 86400)} jours", $"{Round(seconds / 86400)} days ago");

    /// <summary>
    /// Where the PC of Jellyfin stands, from the relay's agent, at <paramref name="now"/>: <c>Ready</c> when it takes searches
    /// (online), even while its downloads must wait.
    /// </summary>
    public static (string Text, bool Ready) Agent(RelayAgent? agent, DateTimeOffset now)
    {
        if (agent is null) return (L("Mira n’a pas encore été ouvert sur le PC serveur : c’est lui qui cherche et télécharge, avec TorLink. Ouvre-le là-bas, connecté avec un compte administrateur.",
            "Mira hasn’t been opened on the server PC yet: it’s the one that searches and downloads, with TorLink. Open it there, signed in with an administrator account."), false);
        if (!agent.Online)
        {
            var seen = agent.Seen == default ? "" : L($" (vu {Ago(Math.Max(0, (now - agent.Seen).TotalSeconds))})", $" (seen {Ago(Math.Max(0, (now - agent.Seen).TotalSeconds))})");
            return (L($"Mira n’est pas ouvert sur {agent.Name}{seen} : tes demandes l’attendent, il les fera à son retour.", $"Mira isn’t open on {agent.Name}{seen}: your requests wait for it and it’ll handle them when it’s back."), false);
        }
        if (!string.IsNullOrEmpty(agent.Problem)) return (agent.Problem, true);
        return (L($"{agent.Name} est prêt : TorLink y télécharge, puis Mira range le titre dans ta bibliothèque.", $"{agent.Name} is ready: TorLink downloads there, then Mira puts the title in your library."), true);
    }

    /// <summary>A search result: « 1,4 Go · 120 sources · YTS »; nothing of sources for a site that does not count them (SubsPlease).</summary>
    public static string Hit(RelayHit hit)
    {
        var seeders = Math.Max(0, hit.Seeders);
        var sources = !ReleaseRanking.ReportsHealth(hit.Source) && seeders == 0 ? "" : seeders switch { 0 => L("aucune source", "no sources"), 1 => "1 source", _ => $"{seeders} sources" };
        return Join(Size(hit.Size), sources, hit.Source);
    }

    /// <summary>A request not yet in TorLink's list: waiting for the PC, on its way, or refused (and why).</summary>
    public static string Request(RelayRequest request) => request.State switch
    {
        "waiting" => L("En attente du PC serveur", "Waiting for the server PC"),
        "claimed" => L("Envoi à TorLink…", "Sending to TorLink…"),
        "sent" => string.IsNullOrEmpty(request.Error) ? L("Envoyé à TorLink", "Sent to TorLink") : L($"Envoyé à TorLink : {request.Error}", $"Sent to TorLink: {request.Error}"),
        _ => string.IsNullOrEmpty(request.Error) ? L("TorLink a refusé ce téléchargement.", "TorLink refused this download.") : request.Error
    };

    /// <summary>« moins d’une minute », « 12 min », « 1 h 05 », « 3 jours » ("1 hr 5 min", "3 days"): what is left, in seconds.</summary>
    public static string Remaining(double seconds)
    {
        var n = Math.Max(0, seconds);
        if (n < 60) return L("moins d’une minute", "less than a minute");
        var minutes = Round(n / 60);
        if (minutes < 60) return $"{minutes} min";
        if (minutes < 1440) return L($"{minutes / 60} h {minutes % 60:00}", $"{minutes / 60} hr {minutes % 60} min");
        var days = Round(n / 86400);
        return Count(days, "jour", "jours", "day", "days");
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
        return done > 0 && done < download.Size ? L($"{Size(done)} sur {Size(download.Size)}", $"{Size(done)} of {Size(download.Size)}") : Size(download.Size);
    }

    /// <summary>
    /// A download in TorLink: « 42 % · 1,2 Go sur 2,9 Go · 2,4 Mo/s · reste 12 min · 12 pairs », « En pause · 42 % »,
    /// « Arrêté · 42 % ».
    /// </summary>
    public static string Download(RelayDownload download)
    {
        var progress = L($"{Math.Clamp(download.Progress, 0, 100)} %", $"{Math.Clamp(download.Progress, 0, 100)}%");
        var peers = Math.Max(0, download.Peers);
        switch (download.Status)
        {
            case "downloading":
                var rate = Speed(download.Speed);
                if (rate.Length == 0) return Join(progress, Amount(download), peers switch { 0 => L("recherche de pairs…", "looking for peers…"), 1 => L("1 pair, en attente de données", "1 peer, waiting for data"), _ => L($"{peers} pairs, en attente de données", $"{peers} peers, waiting for data") });
                return Join(progress, Amount(download), rate, TimeLeft(download) is { } left ? L($"reste {Remaining(left)}", $"{Remaining(left)} left") : "", peers switch { 0 => "", 1 => L("1 pair", "1 peer"), _ => L($"{peers} pairs", $"{peers} peers") });
            case "queued": return Join(L("En file d’attente · ", "Queued · ") + progress, Amount(download));
            case "paused": return Join(L("En pause · ", "Paused · ") + progress, Amount(download));
            case "stopped": return L($"Arrêté · {progress} · reprend quand TorLink rouvre sur le PC serveur", $"Stopped · {progress} · resumes when TorLink opens again on the server PC");
            case "failed": return Join(L("Échec · ", "Failed · ") + progress, Failure(download.Error));
            case "completed" or "seeding": return L("Terminé · rangement dans ta bibliothèque…", "Done · adding it to your library…");
            default: return progress;
        }
    }

    /// <summary>
    /// Why TorLink gave up, as Mira web says it (torlink.js: failure): a full disk or a folder it cannot write to in words,
    /// anything else in TorLink's own; empty when it did not say.
    /// </summary>
    public static string Failure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "";
        if (error.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase) || error.Contains("no space", StringComparison.OrdinalIgnoreCase)) return L("plus de place sur le disque du PC serveur", "the server PC’s disk is full");
        if (error.Contains("EACCES", StringComparison.Ordinal) || error.Contains("EPERM", StringComparison.Ordinal)) return L("TorLink ne peut pas écrire dans son dossier", "TorLink can’t write to its folder");
        return error.Trim();
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
