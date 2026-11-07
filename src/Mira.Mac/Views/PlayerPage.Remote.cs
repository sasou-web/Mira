using Mira.Core;

namespace Mira.Mac.Views;

// Jellyfin's remote control in the player (MainWindow.Remote.cs): pause, seek, volume, full screen and a message from
// another device, as the player's own keys.
public sealed partial class PlayerPage
{
    private string? _remoteSaid;
    private DateTimeOffset _remoteSaidUntil;

    /// <summary>A command of another device. False when the player cannot take it now (nothing loaded yet, the mini-player's message).</summary>
    public bool Steer(RemoteCommand command)
    {
        // Someone is there, steering from elsewhere (« Tu regardes toujours ? »).
        Touched();
        if (command.Action == RemoteAction.Message)
        {
            if (_mini || FailureShown) return false;
            _remoteSaid = RemoteControl.Said(command); _remoteSaidUntil = DateTimeOffset.UtcNow.AddSeconds(6); ShowGroup();
            return true;
        }
        if (_mpv is not { } mpv || !_loaded || _stopped || FailureShown) return false;
        switch (command.Action)
        {
            case RemoteAction.Pause or RemoteAction.Unpause or RemoteAction.TogglePause:
                var key = command.Action switch { RemoteAction.Pause => MediaKey.Pause, RemoteAction.Unpause => MediaKey.Play, _ => MediaKey.TogglePause };
                if (NowPlaying.Changes(key, mpv.Flag("pause"))) TogglePause();
                break;
            case RemoteAction.Seek when double.IsFinite(command.Position): SeekTo(command.Position, exact: true); ShowControls(); break;
            case RemoteAction.Back: Jump(-RemoteControl.Skip); break;
            case RemoteAction.Forward: Jump(RemoteControl.Skip); break;
            case RemoteAction.Next when _skips.Next is { } next: _ = PlayNextAsync(next); break;
            case RemoteAction.Previous: PlayPrevious(); break;
            case RemoteAction.SetVolume or RemoteAction.VolumeUp or RemoteAction.VolumeDown:
                _volume.Value = RemoteControl.Level(command, _volume.Value);
                if (!_mini) { ShowControls(); RevealVolume(true); HideVolumeSoon(); }
                break;
            case RemoteAction.Mute or RemoteAction.Unmute or RemoteAction.ToggleMute:
                var muted = mpv.Flag("mute") || _volume.Value <= 0;
                if (RemoteControl.Muted(command, muted) is { } wanted && wanted != muted) ToggleMute();
                break;
            case RemoteAction.Fullscreen: ToggleFullscreen(); break;
        }
        TellNowPlaying();
        return true;
    }

    /// <summary>The volume as the slider says it, and whether the sound is cut: what the self-check reads back.</summary>
    public (double Level, bool Muted) Sound => (_volume.Value, _mpv?.Flag("mute") == true);
    /// <summary>The self-check puts back the volume it had (above 100 %, which no remote can ask).</summary>
    internal void PutBackSound(double level) => _volume.Value = level;
    /// <summary>What the pill at the top says, while it shows.</summary>
    public string? PillText => _groupPill is { IsVisible: true, Opacity: > 0 } ? _groupText.Text : null;
}
