using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Windows.Media;
using Windows.Storage.Streams;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace Mira.Desktop.Services;

internal enum DesktopCommand { Show, Hide, Quit, TogglePause, Play, Pause, Back, Forward, Next, Stop, Seek }

/// <summary>Windows surfaces are remote controls for the existing mpv session, never a second player.</summary>
internal sealed class WindowsIntegration : IDisposable
{
    private readonly Window _window;
    private readonly Action<DesktopCommand, double> _command;
    private readonly string _idleTitle;
    private readonly Dictionary<string, ImageSource> _glyphs = new();
    private Brush _glyphBrush = ShellGlyphBrush();
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly System.Drawing.Icon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayPause, _trayBack, _trayForward, _trayNext, _trayStop;
    private readonly ThumbButtonInfo _back, _pause, _forward, _next;
    private SystemMediaTransportControls? _media;
    private InMemoryRandomAccessStream? _artwork;
    private int _generation;
    private bool _disposed, _active, _paused, _ready, _canNext;
    private string _title = "";
    private double _lastPosition = -1, _lastDuration = -1, _lastRate = -1;
    private DateTimeOffset _lastTimeline;
    public bool MediaAvailable => _media is not null;
    public bool TrayVisible => _tray.Visible;
    public string? MediaError { get; private set; }

    public WindowsIntegration(Window window, string appId, bool validation, Action<DesktopCommand, double> command)
    {
        _window = window; _command = command; _idleTitle = window.Title;
        var hwnd = new WindowInteropHelper(window).Handle;
        try { WindowsIdentity.ApplyToWindow(hwnd, appId, validation); }
        catch (COMException) { }
        var taskbar = new TaskbarItemInfo();
        _back = AddButton(taskbar, "rewind", "Reculer de 10 secondes", DesktopCommand.Back);
        _pause = AddButton(taskbar, "play", "Lire", DesktopCommand.TogglePause);
        _forward = AddButton(taskbar, "forward", "Avancer de 10 secondes", DesktopCommand.Forward);
        _next = AddButton(taskbar, "skip", "Épisode suivant", DesktopCommand.Next);
        window.TaskbarItemInfo = taskbar;

        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        AddMenu("Ouvrir Mira", DesktopCommand.Show).Font = new System.Drawing.Font(_menu.Font, System.Drawing.FontStyle.Bold);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _trayPause = AddMenu("Lire", DesktopCommand.TogglePause);
        _trayBack = AddMenu("Reculer de 10 secondes", DesktopCommand.Back);
        _trayForward = AddMenu("Avancer de 10 secondes", DesktopCommand.Forward);
        _trayNext = AddMenu("Épisode suivant", DesktopCommand.Next);
        _trayStop = AddMenu("Arrêter la lecture", DesktopCommand.Stop);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddMenu("Réduire dans la zone de notification", DesktopCommand.Hide);
        AddMenu("Quitter Mira", DesktopCommand.Quit);
        _trayIcon = new System.Drawing.Icon(WindowsIdentity.IconPath, 32, 32);
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = validation ? "Mira · Validation" : "Mira", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += TrayClicked;
        SystemEvents.UserPreferenceChanged += ShellThemeChanged;
        UpdateButtons();
        try
        {
            _media = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _media.IsEnabled = false;
            _media.ButtonPressed += MediaButtonPressed;
            _media.PlaybackPositionChangeRequested += MediaSeekRequested;
        }
        catch (Exception ex) when (ShellError(ex)) { MediaError = ex.GetType().Name; _media = null; }
    }

    private ThumbButtonInfo AddButton(TaskbarItemInfo taskbar, string icon, string description, DesktopCommand command)
    {
        var button = new ThumbButtonInfo { ImageSource = Glyph(icon), Description = description, Visibility = Visibility.Collapsed };
        button.Click += (_, _) => Dispatch(command); taskbar.ThumbButtonInfos.Add(button); return button;
    }
    private Forms.ToolStripMenuItem AddMenu(string label, DesktopCommand command)
    {
        var item = new Forms.ToolStripMenuItem(label); item.Click += (_, _) => Dispatch(command); _menu.Items.Add(item); return item;
    }
    private ImageSource Glyph(string kind)
    {
        if (_glyphs.TryGetValue(kind, out var cached)) return cached;
        var icon = new Views.Icon { Kind = kind, Width = 32, Height = 32, Foreground = _glyphBrush };
        icon.Measure(new Size(32, 32)); icon.Arrange(new Rect(0, 0, 32, 32));
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bitmap.Render(icon); bitmap.Freeze();
        _glyphs[kind] = bitmap; return bitmap;
    }
    private static Brush ShellGlyphBrush()
    {
        if (SystemParameters.HighContrast) return SystemColors.WindowTextBrush;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light == 1 ? Brushes.Black : Brushes.WhiteSmoke;
    }
    private void ShellThemeChanged(object sender, UserPreferenceChangedEventArgs e) => _window.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        _glyphBrush = ShellGlyphBrush(); _glyphs.Clear();
        _back.ImageSource = Glyph("rewind"); _forward.ImageSource = Glyph("forward"); _next.ImageSource = Glyph("skip"); UpdateButtons();
    });
    private void TrayClicked(object? sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Dispatch(DesktopCommand.Show); }
    private void Dispatch(DesktopCommand command, double value = 0)
    {
        var generation = Volatile.Read(ref _generation);
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (command is DesktopCommand.Show or DesktopCommand.Hide or DesktopCommand.Quit || generation == _generation && _active && _ready)
                _command(command, value);
        });
    }
    private void MediaButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        switch (e.Button)
        {
            case SystemMediaTransportControlsButton.Play: Dispatch(DesktopCommand.Play); break;
            case SystemMediaTransportControlsButton.Pause: Dispatch(DesktopCommand.Pause); break;
            case SystemMediaTransportControlsButton.Stop: Dispatch(DesktopCommand.Stop); break;
            case SystemMediaTransportControlsButton.Previous: case SystemMediaTransportControlsButton.Rewind: Dispatch(DesktopCommand.Back); break;
            case SystemMediaTransportControlsButton.FastForward: Dispatch(DesktopCommand.Forward); break;
            case SystemMediaTransportControlsButton.Next: Dispatch(DesktopCommand.Next); break;
        }
    }
    private void MediaSeekRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs e)
        => Dispatch(DesktopCommand.Seek, e.RequestedPlaybackPosition.TotalSeconds);

    public void SetTrack(string title, string subtitle)
    {
        ++_generation; _active = true; _ready = false; _canNext = false; _paused = false; _title = title;
        _lastPosition = _lastDuration = _lastRate = -1;
        _window.Title = title + " — Mira";
        UpdateButtons();
        WithMedia(media =>
        {
            media.IsEnabled = true; media.PlaybackStatus = MediaPlaybackStatus.Changing;
            var display = media.DisplayUpdater; display.ClearAll(); display.Type = MediaPlaybackType.Video;
            display.VideoProperties.Title = title; display.VideoProperties.Subtitle = subtitle;
            display.Update();
        });
        _artwork?.Dispose(); _artwork = null;
    }
    public async Task SetArtworkAsync(ImageSource image)
    {
        if (_disposed || !_active || _media is null) return;
        var generation = _generation;
        if (image is not BitmapSource bitmap)
        {
            var preview = new System.Windows.Controls.Image { Source = image, Width = 512, Height = 512, Stretch = Stretch.UniformToFill, ClipToBounds = true };
            preview.Measure(new Size(512, 512)); preview.Arrange(new Rect(0, 0, 512, 512));
            var rendered = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32); rendered.Render(preview); bitmap = rendered;
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var bytes = new MemoryStream(); encoder.Save(bytes);
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes.ToArray()); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            if (_disposed || generation != _generation || !_active || _media is null) return;
            WithMedia(media => { media.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream); media.DisplayUpdater.Update(); });
            _artwork?.Dispose(); _artwork = stream; stream = null!;
        }
        catch (Exception ex) when (ShellError(ex)) { MediaError = ex.GetType().Name; }
        finally { stream?.Dispose(); }
    }
    public void Update(bool ready, bool paused, bool canNext, double position, double duration, double rate)
    {
        if (_disposed || !_active) return;
        var changed = ready != _ready || paused != _paused || canNext != _canNext;
        _ready = ready; _paused = paused; _canNext = canNext;
        if (changed) UpdateButtons();
        position = double.IsFinite(position) ? Math.Max(0, position) : 0;
        duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        position = Math.Min(position, duration);
        var now = DateTimeOffset.UtcNow;
        if (changed || Math.Abs(position - _lastPosition) > 2 || duration != _lastDuration || rate != _lastRate || now - _lastTimeline >= TimeSpan.FromSeconds(1))
        {
            _lastTimeline = now; _lastPosition = position; _lastDuration = duration; _lastRate = rate;
            WithMedia(media =>
            {
                media.IsPlayEnabled = media.IsPauseEnabled = media.IsStopEnabled = ready;
                media.IsPreviousEnabled = media.IsRewindEnabled = media.IsFastForwardEnabled = ready && duration > 0;
                media.IsNextEnabled = ready && canNext;
                media.PlaybackStatus = !ready ? MediaPlaybackStatus.Changing : paused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
                media.PlaybackRate = double.IsFinite(rate) && rate > 0 ? rate : 1;
                media.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                { StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(duration), MinSeekTime = TimeSpan.Zero,
                    MaxSeekTime = TimeSpan.FromSeconds(duration), Position = TimeSpan.FromSeconds(position) });
            });
        }
    }
    private void UpdateButtons()
    {
        foreach (var button in new[] { _back, _pause, _forward, _next })
        { button.Visibility = _active ? Visibility.Visible : Visibility.Collapsed; button.IsEnabled = _active && _ready; }
        _next.IsEnabled = _active && _ready && _canNext;
        _pause.ImageSource = Glyph(_paused ? "play" : "pause"); _pause.Description = _paused ? "Reprendre la lecture" : "Mettre en pause";
        _trayPause.Text = _paused ? "Reprendre la lecture" : "Mettre en pause";
        _trayPause.Enabled = _trayBack.Enabled = _trayForward.Enabled = _trayStop.Enabled = _active && _ready;
        _trayNext.Enabled = _active && _ready && _canNext;
        var text = _active ? (_paused ? "En pause · " : "Lecture · ") + _title : _idleTitle;
        if (text.Length > 63) { text = text[..61]; if (char.IsHighSurrogate(text[^1])) text = text[..^1]; text += "…"; }
        _tray.Text = text;
    }
    public void Clear()
    {
        ++_generation; _active = _ready = _canNext = false;
        _window.Title = _idleTitle; UpdateButtons();
        WithMedia(media => { media.PlaybackStatus = MediaPlaybackStatus.Closed; media.IsEnabled = false; media.DisplayUpdater.ClearAll(); media.DisplayUpdater.Update(); });
        _artwork?.Dispose(); _artwork = null;
    }
    private void WithMedia(Action<SystemMediaTransportControls> update)
    {
        if (_media is null) return;
        try { update(_media); }
        catch (Exception ex) when (ShellError(ex)) { MediaError = ex.GetType().Name; }
    }
    private static bool ShellError(Exception ex) => ex is COMException or InvalidOperationException or UnauthorizedAccessException or IOException;
    public void Dispose()
    {
        if (_disposed) return;
        Clear(); _disposed = true;
        SystemEvents.UserPreferenceChanged -= ShellThemeChanged;
        if (_media is not null) WithMedia(media => { media.ButtonPressed -= MediaButtonPressed; media.PlaybackPositionChangeRequested -= MediaSeekRequested; });
        _media = null; _tray.Visible = false; _tray.Dispose(); _menu.Dispose(); _trayIcon.Dispose();
        _window.TaskbarItemInfo = null;
    }
}
