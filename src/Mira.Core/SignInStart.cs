namespace Mira.Core;

/// <summary>
/// Mira back by itself after a restart, on the PC of Jellyfin (idea 1 of 2026-11-18, chosen by ogsas): a value « Mira »
/// in the Run key of the person's account (<c>HKCU</c>, no Windows authorization), so that when they sign in (or Windows
/// signs them in after an update) Mira starts by the clock, without a window: the followed series, the downloads sent
/// from the iPhone, qBittorrent, the PC kept awake while someone watches go on without opening it. Written only while
/// the account's Jellyfin runs on this PC and « Ouvrir Mira à l’ouverture de session » is on; taken away otherwise. Windows'
/// own Startup apps (Settings, Task Manager) still decide: Mira never writes their <c>StartupApproved</c> choice.
/// Read and written by Mira for Windows (<c>Services/SignInLaunch.cs</c>, <c>MainWindow.SignIn.cs</c>).
/// </summary>
public static class SignInStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Where Windows' Startup apps keep « off » for a value of <see cref="RunKey"/>.</summary>
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string Name = "Mira";
    /// <summary>The option of a start at sign-in: by the clock, never in front, never a message box.</summary>
    public const string Flag = "--at-sign-in";

    public enum Step { Keep, Write, Remove }

    /// <summary>The value written for this copy of Mira.</summary>
    public static string Command(string executable) => $"\"{executable}\" {Flag}";

    /// <summary>A value written by Mira (another app may be called « Mira » too: its value is never touched).</summary>
    public static bool Written(string? value) => value?.Contains(Flag, StringComparison.Ordinal) == true;

    /// <summary>
    /// What to do with the Run key's value <paramref name="current"/>: written (or rewritten, this copy having moved)
    /// while Jellyfin is on this PC and the setting is on, removed otherwise when it is Mira's.
    /// </summary>
    public static Step Plan(bool jellyfinHere, bool wanted, string? current, string executable)
    {
        if (jellyfinHere && wanted) return current == Command(executable) ? Step.Keep : Step.Write;
        return Written(current) ? Step.Remove : Step.Keep;
    }

    /// <summary>
    /// Turned off in Windows' Startup apps: its <see cref="ApprovedKey"/> value starts with an odd byte (03 off, 02 or 06 on).
    /// </summary>
    public static bool TurnedOffInWindows(byte[]? approved) => approved is { Length: > 0 } bytes && (bytes[0] & 1) == 1;

    /// <summary>What the setting's row says beside its switch, the state of Windows' own choice included.</summary>
    public static string State(bool wanted, bool written, bool offInWindows) => !wanted
        ? L("Coupé : après un redémarrage, ouvre Mira toi-même pour que tes autres appareils retrouvent leurs téléchargements.", "Off: after a restart, open Mira yourself so your other devices get their downloads back.")
        : offInWindows ? L("Coupé dans les applications de démarrage de Windows (Paramètres → Applications → Démarrage) : c’est là qu’il se rallume.", "Turned off in Windows’ startup apps (Settings → Apps → Startup): that’s where it turns back on.")
        : written ? L("Prêt : Mira revient tout seul près de l’horloge quand tu ouvres ta session.", "Ready: Mira comes back by itself by the clock when you sign in.")
        : L("Windows n’a pas pu l’enregistrer : réessaie en rouvrant Mira.", "Windows couldn’t save it: try again by reopening Mira.");
}
