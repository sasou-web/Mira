using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

// The Mac's media controls (MacNowPlaying): F7 to F9, the button of AirPods or a headset, « Now Playing » of Control
// Center and the menu bar. A remote for the player that is open, the mini-player and « Image dans l’image » included,
// as the system media controls of Windows; with no player, they go to the other apps.
public sealed partial class MainWindow
{
    private void ListenToMediaKeys()
    {
        MacNowPlaying.Failed = ex => ErrorLog.Append(Profile.DirectoryPath, ex);
        MacNowPlaying.Pressed += MediaKeyPressed;
        Closed += (_, _) => { MacNowPlaying.Pressed -= MediaKeyPressed; MacNowPlaying.Clear(); };
        MacNowPlaying.Listen();
    }
    private void MediaKeyPressed(MediaKey key, double position)
    {
        if (Player is not { } player) return;
        if (key == MediaKey.Stop) { _ = StopPlaybackAsync(); return; }
        player.Remote(key, position);
    }
}
