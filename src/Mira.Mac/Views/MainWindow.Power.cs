using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// The Mac sleeping and waking (MacPower), as Windows' PowerModeChanged: the player pauses before, and opens its stream
/// from Jellyfin again after (PlayerPage.Power.cs); « Tailscale intégré » looks at the network afresh on waking.
/// </summary>
public sealed partial class MainWindow
{
    private void InitializePower()
    {
        MacPower.Failed = ex => ErrorLog.Append(Profile.DirectoryPath, ex);
        MacPower.WillSleep += MacWillSleep;
        MacPower.DidWake += MacDidWake;
        Closed += (_, _) => { MacPower.WillSleep -= MacWillSleep; MacPower.DidWake -= MacDidWake; };
        MacPower.Listen();
    }
    // macOS waits only a moment before sleeping: the pause is made before this returns, on the UI thread (the main one, where the notice comes).
    private void MacWillSleep() => OnUiThread(() => Player?.Suspending());
    private void MacDidWake() => OnUiThread(() => { _ = Tailnet.RebindAsync(); Player?.Woke(); });
    private static void OnUiThread(Action act)
    {
        if (Dispatcher.UIThread.CheckAccess()) act();
        else Dispatcher.UIThread.Invoke(act, DispatcherPriority.Send);
    }
}
