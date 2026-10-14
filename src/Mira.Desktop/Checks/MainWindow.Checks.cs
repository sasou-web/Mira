namespace Mira.Desktop;

/// <summary>
/// The validation harnesses of the window, all in this folder: captures (--visual-check, --public-gallery), the real
/// player (--player-check, --qa-window), Windows integration (--windows-check) and TorLink (--torlink-check). They drive
/// the window's own state, hence partial classes of it. The window starts them from here; App.xaml.cs only places
/// their window off screen and writes failure.txt. None of this folder is in the packages: tools/package.ps1 builds with
/// -p:MiraValidation=false, where a harness option ends Mira at once (App.OnStartup).
/// </summary>
public partial class MainWindow
{
    /// <summary>Once the first screen shows: runs the check the command line asks for; true when one ran (the window's usual start stops there).</summary>
    private async Task<bool> RunRequestedCheckAsync()
    {
        if (_args.Contains("--public-gallery")) await RunPublicGalleryAsync();
        else if (_args.Contains("--visual-check")) await RunVisualCheckAsync();
        else if (_args.Contains("--player-check")) await RunPlayerCheckAsync();
        else if (_args.Contains("--windows-check")) await RunWindowsCheckAsync();
        else if (_args.Contains("--torlink-check")) await RunTorLinkCheckAsync();
        else return false;
        return true;
    }
}
