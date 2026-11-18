using Mira.Core;
using static TestSupport;

/// <summary>Mira back by itself at sign-in on the PC of Jellyfin: the Run key's value, written, kept or taken away.</summary>
internal static class SignInStartTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Ouverture de session : Mira inscrit seulement sur le PC de Jellyfin, réécrit s’il a bougé, jamais la valeur d’une autre app", () =>
        {
            const string exe = @"C:\Users\Alex\AppData\Local\Programs\Mira\Mira.exe";
            var mine = SignInStart.Command(exe);
            Assert(mine == "\"" + exe + "\" --at-sign-in", mine);
            Assert(SignInStart.Plan(jellyfinHere: true, wanted: true, current: null, exe) == SignInStart.Step.Write, "Jellyfin here, nothing yet: written");
            Assert(SignInStart.Plan(true, true, mine, exe) == SignInStart.Step.Keep, "Already there: kept, nothing written");
            Assert(SignInStart.Plan(true, true, "\"D:\\Mira\\Mira.exe\" --at-sign-in", exe) == SignInStart.Step.Write, "Another copy (a portable moved): rewritten for this one");
            Assert(SignInStart.Plan(false, true, mine, exe) == SignInStart.Step.Remove, "Jellyfin on another PC: taken away");
            Assert(SignInStart.Plan(true, false, mine, exe) == SignInStart.Step.Remove, "Turned off in Mira: taken away");
            Assert(SignInStart.Plan(false, true, null, exe) == SignInStart.Step.Keep, "Nothing to take away");
            Assert(SignInStart.Plan(false, false, @"C:\Other\Mira.exe /tray", exe) == SignInStart.Step.Keep, "Another app's « Mira »: never touched");
            // Windows' Startup apps: 02 or 06 on, 03 off.
            Assert(!SignInStart.TurnedOffInWindows(null) && !SignInStart.TurnedOffInWindows([]), "No choice: on");
            Assert(!SignInStart.TurnedOffInWindows([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]) && !SignInStart.TurnedOffInWindows([6, 0, 0, 0]), "On");
            Assert(SignInStart.TurnedOffInWindows([3, 0, 0, 0, 0, 0, 0, 0, 0x10, 0x20, 0x30, 0x40]), "Off");
            Assert(SignInStart.State(true, true, false).StartsWith("Prêt", StringComparison.Ordinal), SignInStart.State(true, true, false));
            Assert(SignInStart.State(true, true, true).Contains("Paramètres → Applications → Démarrage", StringComparison.Ordinal), "Off in Windows: where to turn it back on");
            Assert(SignInStart.State(false, false, true).StartsWith("Coupé :", StringComparison.Ordinal), "Off in Mira first");
            return Task.CompletedTask;
        });
    }
}
