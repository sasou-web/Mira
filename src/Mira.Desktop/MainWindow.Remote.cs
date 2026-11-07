using System.Windows;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

// Jellyfin's remote control (RemoteControl of Core): this Mira says what it takes each time its WebSocket opens, then
// obeys what another device sends through it: « Lire sur… » of the iPhone and of Jellyfin's pages (Play), pause,
// seek, next and stop (Playstate, « Arrêter » of « En ce moment sur Jellyfin » included), volume, full screen and a
// message on screen (GeneralCommand). The same keys as the system media controls (WindowsCommand), never a second
// player. Jellyfin decides who may send them (the account itself, or an administrator allowed to); a party's guest
// takes none.
public partial class MainWindow
{
    /// <summary>A message from another device, said by the player's pill until <see cref="_remoteSaidUntil"/>.</summary>
    private string? _remoteSaid;
    private DateTimeOffset _remoteSaidUntil;

    private void Remote(JellyfinClient client, RemoteCommand command)
    {
        if (_closing || !ReferenceEquals(client, _client) || InParty) return;
        // Someone is there, steering from elsewhere (« Tu regardes toujours ? »).
        _stillWatching.Touched(DateTimeOffset.Now);
        switch (command.Action)
        {
            case RemoteAction.Play when command.ItemId is { } id: _ = PlayFromElsewhereAsync(client, id, command.Start); break;
            case RemoteAction.Pause: WindowsCommand(DesktopCommand.Pause, 0); break;
            case RemoteAction.Unpause: WindowsCommand(DesktopCommand.Play, 0); break;
            case RemoteAction.TogglePause: WindowsCommand(DesktopCommand.TogglePause, 0); break;
            case RemoteAction.Stop: WindowsCommand(DesktopCommand.Stop, 0); break;
            case RemoteAction.Seek: WindowsCommand(DesktopCommand.Seek, command.Position); break;
            case RemoteAction.Back: WindowsCommand(DesktopCommand.Back, 0); break;
            case RemoteAction.Forward: WindowsCommand(DesktopCommand.Forward, 0); break;
            case RemoteAction.Next: WindowsCommand(DesktopCommand.Next, 0); break;
            case RemoteAction.Previous: if (_loaded) Previous_Click(this, new()); break;
            case RemoteAction.SetVolume or RemoteAction.VolumeUp or RemoteAction.VolumeDown when _mpv is not null && _playing:
                VolumeSlider.Value = RemoteControl.Level(command, VolumeSlider.Value);
                UpdateMuteIcon();
                if (!_miniPlayer) { ShowPlayerControls(); _volumeHide.Stop(); SetVolumeReveal(true); HideVolumeSoon(); }
                break;
            case RemoteAction.Mute or RemoteAction.Unmute or RemoteAction.ToggleMute when _mpv is not null && _playing:
                var muted = _mpv.Flag("mute") || VolumeSlider.Value <= 0;
                if (RemoteControl.Muted(command, muted) is { } wanted && wanted != muted) Mute_Click(this, new());
                break;
            case RemoteAction.Fullscreen when _playing: ToggleFullscreen(); break;
            case RemoteAction.Message:
                var said = RemoteControl.Said(command);
                if (_playing && !_miniPlayer) { _remoteSaid = said; _remoteSaidUntil = DateTimeOffset.UtcNow.AddSeconds(6); UpdateGroupPill(); }
                else SetNotice(said);
                break;
        }
    }

    /// <summary>
    /// « Lire sur… »: the title from where the other device said (0 = from the start; otherwise where this account left
    /// it), the window brought back if it was in the notification area. In a group, as Lecture is: for the group.
    /// </summary>
    private async Task PlayFromElsewhereAsync(JellyfinClient client, string id, double? start)
    {
        try
        {
            var item = await client.ItemAsync(id, _connectionLifetime.Token);
            if (!ReferenceEquals(client, _client) || _closing) return;
            if (!IsVisible || WindowState == WindowState.Minimized) RestoreFromWindows();
            await PlayAsync(item, fromStart: start is 0, chosen: true);
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { if (ReferenceEquals(client, _client)) SetError(Friendly(ex)); }
    }
}
