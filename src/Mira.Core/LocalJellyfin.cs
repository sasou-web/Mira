namespace Mira.Core;

/// <summary>
/// A Jellyfin on this PC that does not answer, as Mira for Windows tells it: stopped (its service, or its program
/// without one), starting, or stopping again after each start. Windows restarts the service three times after an
/// error, then leaves it stopped (<see cref="JellyfinStartup.RestartActions"/>), and a shutdown asked from Jellyfin's
/// dashboard is never undone; meanwhile phones away from home only say « Serveur injoignable ». Decided here, so that
/// the tests check every case; read and started by Mira for Windows.
/// </summary>
public static class LocalJellyfin
{
    /// <summary>Its Windows service as Windows reports it, None without one, Unknown when it cannot be read.</summary>
    public enum Service { Unknown, None, Stopped, Starting, Running, Stopping }
    /// <summary>What Mira says: nothing it knows, stopped (« Démarrer Jellyfin »), starting, or failing (its log).</summary>
    public enum Verdict { Unknown, Stopped, Starting, Failing }
    /// <summary>Starts in a row that did not bring it to answer, after which Mira shows its log instead of starting it again.</summary>
    public const int FailedStartsBeforeLog = 2;
    public static string Start => L("Démarrer Jellyfin", "Start Jellyfin");
    public static string OpenLog => L("Ouvrir son journal", "Open its log");

    /// <summary>
    /// <paramref name="programRunning"/>: jellyfin.exe runs (what tells, without a service); <paramref name="canStart"/>:
    /// Mira found how to start it (its service, or its program); <paramref name="failedStarts"/>: Mira's starts in a row
    /// that it did not answer after.
    /// </summary>
    public static Verdict Judge(Service service, bool programRunning, bool canStart, int failedStarts) => service switch
    {
        Service.Starting or Service.Running => Verdict.Starting,
        Service.None when programRunning => Verdict.Starting,
        Service.Stopped or Service.Stopping or Service.None when failedStarts >= FailedStartsBeforeLog => Verdict.Failing,
        Service.Stopped or Service.Stopping => Verdict.Stopped,
        Service.None when canStart => Verdict.Stopped,
        _ => Verdict.Unknown,
    };

    /// <summary>
    /// What the notice says, and its button; null for Unknown (Mira's usual « momentanément inaccessible »).
    /// <paramref name="service"/>: started as a service, with Windows' consent; <paramref name="cause"/>: the last fatal
    /// error of its log, when there is one.
    /// </summary>
    public static (string Text, string? Action)? Says(Verdict verdict, bool service, string? cause = null) => verdict switch
    {
        Verdict.Stopped => (L("Jellyfin est arrêté sur ce PC : ton téléphone et tes autres appareils ne le trouvent plus.", "Jellyfin is stopped on this PC: your phone and your other devices can’t find it anymore.")
            + (service ? L(" Le démarrer demande une autorisation de Windows.", " Starting it asks for Windows’ permission.") : ""), Start),
        Verdict.Starting => (L("Jellyfin démarre sur ce PC… Mira le retrouve dès qu’il répond.", "Jellyfin is starting on this PC… Mira finds it again as soon as it answers."), null),
        Verdict.Failing => (L("Jellyfin s’arrête juste après avoir démarré : son journal dit pourquoi.", "Jellyfin stops right after starting: its log says why.")
            + (cause is { Length: > 0 } ? L($" Jellyfin indique : « {cause} ».", $" Jellyfin says: “{cause}”.") : ""), OpenLog),
        _ => null,
    };
}
