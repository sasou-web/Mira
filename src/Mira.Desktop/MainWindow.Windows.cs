using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

public partial class MainWindow
{
    private WindowsIntegration? _windows;
    private WindowState _lastVisibleState = WindowState.Normal;
    private int _windowsArtworkGeneration;
    private void InitializeWindowsIntegration()
    {
        // Offscreen visual/player fixtures must not publish fictitious titles to the user's Shell.
        if (_args.Contains("--visual-check") || _args.Contains("--player-check") || _args.Contains("--public-gallery")) return;
        SourceInitialized += (_, _) => _windows = new WindowsIntegration(this, App.ShellAppId, App.ShellValidation, WindowsCommand);
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _lastVisibleState = WindowState; };
    }
    internal void RestoreFromWindows()
    {
        if (_closing) return;
        Show(); if (WindowState == WindowState.Minimized) WindowState = _lastVisibleState;
        Activate(); if (_playing) ShowPlayerControls();
    }
    private async void WindowsCommand(DesktopCommand command, double position)
    {
        if (_closing) return;
        try
        {
            switch (command)
            {
                case DesktopCommand.Show: RestoreFromWindows(); break;
                case DesktopCommand.Hide:
                    if (_fullscreen) ToggleFullscreen();
                    WindowState = WindowState.Minimized; Hide(); break;
                case DesktopCommand.Quit: Close(); break;
                case DesktopCommand.Play: SetWindowsPause(false); break;
                case DesktopCommand.Pause: SetWindowsPause(true); break;
                case DesktopCommand.TogglePause: if (_loaded) Pause_Click(this, new()); break;
                case DesktopCommand.Back: if (_loaded) SeekRelative(-10); break;
                case DesktopCommand.Forward: if (_loaded) SeekRelative(10); break;
                case DesktopCommand.Next: if (_loaded && _nextItem is not null) Next_Click(this, new()); break;
                case DesktopCommand.Stop: if (_playing) { await StopPlaybackAsync(); ReturnToLibrary(); } break;
                case DesktopCommand.Seek: if (_loaded && double.IsFinite(position)) SeekRelative(position - _position); break;
            }
            UpdateWindowsPlayback();
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
    }
    private void SetWindowsPause(bool paused)
    {
        if (_loaded && _mpv is not null && _mpv.Flag("pause") != paused) Pause_Click(this, new());
    }
    private void PublishWindowsTrack(MediaItem item)
    {
        if (_windows is null) return;
        _windows.SetTrack(item.DisplayTitle, item.Subtitle);
        _ = PublishWindowsArtworkAsync(item, ++_windowsArtworkGeneration);
    }
    private async Task PublishWindowsArtworkAsync(MediaItem item, int generation)
    {
        ImageSource? image = _demo ? DemoLibrary.Artwork(Math.Max(0, _items.FindIndex(x => x.Id == item.Id))) :
            _images is { } cache ? await cache.GetAsync(item, targetWidth: 512) : null;
        if (generation != _windowsArtworkGeneration || _closing || _windows is null || _playingItem?.Id != item.Id) return;
        image ??= new BitmapImage(new Uri("pack://application:,,,/Assets/mira.png"));
        await _windows.SetArtworkAsync(image);
    }
    private void UpdateWindowsPlayback()
    {
        if (!_playing || _mpv is null) return;
        _windows?.Update(_loaded, _mpv.Flag("pause"), _nextItem is not null, _position, _duration, _mpv.Number("speed", 1));
    }
}
