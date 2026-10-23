/// <summary>
/// Every check that needs neither WPF nor Windows: Mira.Core, the window's update code (UpdateApplier, Updater), the
/// Jellyfin plugin's web files and the Mac profile. Run on Linux by Mira.Core.Tests, and on Windows by Mira.Tests with
/// the checks of the Windows app.
/// </summary>
internal static class PortableTests
{
    public static async Task RunAsync(TestRun run)
    {
        await ConnectionTests.RunAsync(run);
        await LibraryTests.RunAsync(run);
        await SyncTests.RunAsync(run);
        await PlayerTests.RunAsync(run);
        await PhoneTests.RunAsync(run);
        await TorLinkTests.RunAsync(run);
        await RemoteDownloadTests.RunAsync(run);
        await GuestTests.RunAsync(run);
        await UpdateTests.RunAsync(run);
        await LanguageTests.RunAsync(run);
        await AccessibilityTests.RunAsync(run.Test);
        await SupplyChainTests.RunAsync(run.Test, run.Root);
    }
}
