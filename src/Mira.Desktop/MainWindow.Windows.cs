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
        if (_args.Contains("--visual-check") || _args.Contains("--player-check") || _args.Contains("--public-gallery") || _args.Contains("--smooth-check")) return;
        SourceInitialized += (_, _) =>
        {
            _windows = new WindowsIntegration(this, App.ShellAppId, App.ShellValidation, WindowsCommand);
            // The icon's tooltip says what Mira does by the clock (TrayMenuPlan.Tooltip): read every few seconds, set only when it changes.
            var work = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            work.Tick += (_, _) => { if (_closing) work.Stop(); else _windows?.SetWork(_torlinkHeadless.Running || _torlinkTerminal?.Running == true, _offlineRunning, _servingRelay); };
            work.Start();
        };
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _lastVisibleState = WindowState; };
    }
    internal void RestoreFromWindows()
    {
        if (_closing) return;
        Show(); if (WindowState == WindowState.Minimized) WindowState = _lastVisibleState;
        Activate(); if (_playing) ShowPlayerControls();
    }
    /// <summary>The cross of the window (its button, Alt+F4, the taskbar's « Close window »), not « Quit Mira » nor a restart.</summary>
    private bool _closeFromCross;
    /// <summary>On the PC of Jellyfin, answering the relay for the other devices' Downloads pages (MainWindow.TorLinkRemote.cs).</summary>
    private bool _servingRelay;
    /// <summary>
    /// The cross keeps Mira by the clock while it works (ogsas, 2026-11-14, « Range si Mira travaille »): on the PC of
    /// Jellyfin for the other devices, TorLink downloading, a title being kept on this PC. Otherwise it quits, as Windows
    /// expects. Never for a check run, which ends by closing.
    /// </summary>
    private bool KeptByTheClock() => _windows is not null && !IsCheckRun && (_servingRelay || _torlinkHeadless.Running || _torlinkTerminal?.Running == true || _offlineRunning);
    private bool IsCheckRun => _args.Any(x => x.EndsWith("-check", StringComparison.Ordinal) || x is "--ui-audit" or "--public-gallery" or "--readme-gallery" or "--visual-check");
    /// <summary>Put away by the clock: a playback ends first (nothing plays in a hidden window), said the first time.</summary>
    private async void PutByTheClock()
    {
        try
        {
            if (_playing) { _playerLeft++; await StopPlaybackAsync(); ReturnToLibrary(); }
            WindowsCommand(DesktopCommand.Hide, 0);
            if (_settings.CloseKeptTold) return;
            _settings.CloseKeptTold = true; TrySaveSettings();
            Toasts.Show(L("Mira continue près de l’horloge", "Mira keeps going by the clock"),
                L("Il travaille encore pour tes autres appareils ou tes téléchargements. Pour le fermer : clic droit sur son icône, « Quitter Mira ».",
                    "It’s still working for your other devices or your downloads. To close it: right-click its icon, then “Quit Mira”."),
                null, () => Dispatcher.BeginInvoke(RestoreFromWindows), "mira");
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
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
                    // Couch mode comes back with the window (InitializeCouch).
                    SetCouchScreen(false);
                    WindowState = WindowState.Minimized; Hide(); break;
                case DesktopCommand.Quit: Close(); break;
                case DesktopCommand.Play: SetWindowsPause(false); break;
                case DesktopCommand.Pause: SetWindowsPause(true); break;
                case DesktopCommand.TogglePause: if (_loaded) Pause_Click(this, new()); break;
                case DesktopCommand.Back: if (_loaded) SeekRelative(-10); break;
                case DesktopCommand.Forward: if (_loaded) SeekRelative(10); break;
                case DesktopCommand.Next: if (_loaded && _skips.Next is not null) Next_Click(this, new()); break;
                case DesktopCommand.Stop: _playerLeft++; if (_playing) { await StopPlaybackAsync(); ReturnToLibrary(); } break;
                case DesktopCommand.DiscordStatus: ToggleDiscordForNow(); break;
                case DesktopCommand.Seek: if (_loaded && double.IsFinite(position)) SeekRelative(position - _session.Position); break;
            }
            UpdateWindowsPlayback();
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }
    private void SetWindowsPause(bool paused)
    {
        if (_loaded && _mpv is not null && _mpv.Flag("pause") != paused) Pause_Click(this, new());
    }
    private void PublishWindowsTrack(MediaItem item)
    {
        UpdateDiscord(force: true);
        if (_windows is null) return;
        _windows.SetTrack(item);
        var generation = ++_windowsArtworkGeneration;
        _ = PublishWindowsArtworkAsync(item, generation);
        _ = PublishTrayPictureAsync(item, generation);
    }
    /// <summary>The wide picture of the card in the notification area's menu (the episode's still or the film's backdrop, spoilers kept hidden).</summary>
    private async Task PublishTrayPictureAsync(MediaItem item, int generation)
    {
        try
        {
            ImageSource? image = _demo ? DemoLibrary.Artwork(Math.Max(0, _catalog.Items.FindIndex(x => x.Id == item.Id)), landscape: true) :
                _images is { } cache ? await cache.LandscapeAsync(item, cache.Pixels(Mira.Core.TrayMenuPlan.Width)) : null;
            if (image is null || generation != _windowsArtworkGeneration || _closing || _windows is null || _session.Item?.Id != item.Id) return;
            _windows.SetCardPicture(image);
        }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
    private async Task PublishWindowsArtworkAsync(MediaItem item, int generation)
    {
        ImageSource? image = _demo ? DemoLibrary.Artwork(Math.Max(0, _catalog.Items.FindIndex(x => x.Id == item.Id))) :
            _images is { } cache ? await cache.GetAsync(item, targetWidth: 512) : null;
        if (generation != _windowsArtworkGeneration || _closing || _windows is null || _session.Item?.Id != item.Id) return;
        image ??= new BitmapImage(new Uri("pack://application:,,,/Assets/mira.png"));
        await _windows.SetArtworkAsync(image);
    }
    private void UpdateWindowsPlayback()
    {
        UpdateDiscord();
        if (!_playing || _mpv is null) return;
        _windows?.Update(_loaded, _mpv.Flag("pause"), _skips.Next is not null, _session.Position, _session.Duration, _mpv.Number("speed", 1));
    }
}
