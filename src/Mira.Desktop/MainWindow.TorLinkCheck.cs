using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Mira.Core;
using Mira.Desktop.TorLink;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// End-to-end TorLink check on an isolated profile: the real TorLink in the page, keyboard input through the
    /// terminal, then three synthetic finished downloads placed in a library created inside the output folder.
    /// Requires --demo, --data and TORLINK_STATE_DIR, so neither the real TorLink state nor Jellyfin is touched.
    /// </summary>
    private async Task RunTorLinkCheckAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--torlink-check") + 1]); Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add("PASS " + label); }
        async Task Until(Func<bool> predicate, int seconds, string label)
        { var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException(label); await Task.Delay(80); } }
        async Task<bool> Within(Func<bool> predicate, int seconds)
        { var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.Elapsed.TotalSeconds > seconds) return false; await Task.Delay(80); } return true; }
        try
        {
            // Checked again here; without isolation, TorLink stayed disabled from the start (InitializeTorLink).
            if (!_demo || !_torlinkEnabled || !TorLinkCheckIsolated() || Environment.GetEnvironmentVariable("TORLINK_STATE_DIR") is not { } state)
                throw new InvalidOperationException("The TorLink check requires --demo, an isolated --data profile and TORLINK_STATE_DIR inside the output folder.");
            Width = 1440; Height = 900;
            // Another TorLink on this PC (a Mira in use, its shortcut): Mira rightly refuses a second one. The page itself is
            // still checked (served from Mira's resources and drawn by xterm.js with its style sheet), then the check stops.
            if (TorLinkInstallation.Locate(_settings.TorLinkPath) is { } found && await Task.Run(() => found.RunningElsewhere(null)) is { } other)
            {
                await ShowTorLinkAsync();
                await Until(() => _torlinkTerminal?.PageReady == true, 25, "The terminal page did not load");
                var raw = await _torlinkTerminal!.View!.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify({ xterm: !!document.querySelector('.xterm'), helper: getComputedStyle(document.querySelector('.xterm-helper-textarea')).opacity })");
                using (var page = JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw) ?? "{}"))
                    Require(page.RootElement.TryGetProperty("xterm", out var xterm) && xterm.GetBoolean() && page.RootElement.TryGetProperty("helper", out var helper) && helper.GetString() == "0",
                        "the terminal page loads from Mira's resources, with xterm.js and its style sheet");
                Require(_torlinkBlocker == "elsewhere" && TorLinkStatePanel.Visibility == Visibility.Visible, "with another TorLink running, the page says so instead of starting a second one");
                await CaptureAsync(output, "01-torlink-elsewhere");
                checks.Add($"SKIP TorLink itself, downloads and keys: TorLink is already running on this PC (process {other}); close it to run the whole check");
                await StopTorLinkAsync();
                await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
                return;
            }
            var downloads = Path.Combine(output, "downloads"); var library = Path.Combine(output, "library");
            foreach (var folder in new[] { downloads, Path.Combine(library, "FILMS"), Path.Combine(library, "SERIES"), Path.Combine(library, "ANIME") }) Directory.CreateDirectory(folder);
            PrepareSyntheticDownloads(state, downloads);

            await ShowTorLinkAsync();
            Require(TorLinkOverlay.Visibility == Visibility.Visible && TorLinkNav.Background is SolidColorBrush { Color.R: 0xF4 }, "the rail opens the TorLink page and marks it active");
            await Until(() => _torlinkTerminal?.State == TerminalState.Running, 25, "TorLink did not start in the page");
            var terminal = _torlinkTerminal ?? throw new InvalidOperationException("No terminal");
            var installation = _torlinkInstallation ?? throw new InvalidOperationException("No installation");
            Require(terminal.ProcessId is not null, "TorLink runs from its own folder in Mira's pseudo console");
            await Task.Delay(2500);
            await CaptureAsync(output, "01-torlink-page");
            Require(InkRatio(output, "01-torlink-page") > .004, "TorLink's interface is drawn inside the page");
            Require(installation.RunningElsewhere(terminal.ProcessId) is null, "only one TorLink runs");

            // Escape forwarded by WebView2 (as the composition control raises it) must stay TorLink's: Mira does not go back.
            if (terminal.View is { } view && PresentationSource.FromVisual(view) is { } source)
            {
                var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                view.RaiseEvent(preview);
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
                if (!preview.Handled) view.RaiseEvent(down);
                Require(!preview.Handled && !down.Handled && TorLinkOverlay.IsHitTestVisible, "Escape coming from the terminal is left to TorLink");
            }
            var system = IsForegroundWindow();
            terminal.Focus(); await Task.Delay(500);
            if (system) TypeText("mira"); else await BrowserKeysAsync(terminal, "mira");
            await Task.Delay(1000);
            await CaptureAsync(output, "02-typing");
            if (system) PressKey(0x1B); else await BrowserKeyAsync(terminal, "rawKeyDown", "Escape", "Escape", 27, null);
            await Until(() => terminal.State == TerminalState.Exited, 10, "Escape did not reach TorLink");
            Require(TorLinkOverlay.Visibility == Visibility.Visible && TorLinkStatePanel.Visibility == Visibility.Visible,
                $"keys typed in the terminal ({(system ? "real keyboard" : "browser key events")}) reach TorLink, and its exit is shown in the page");
            await CaptureAsync(output, "03-exited");
            await RunTorLinkActionAsync("retry");
            await Until(() => terminal.State == TerminalState.Running, 20, "TorLink did not restart");
            Require(true, "« Relancer » starts TorLink again");

            _settings.TorLinkMoviesFolder = Path.Combine(library, "FILMS"); _settings.TorLinkSeriesFolder = Path.Combine(library, "SERIES"); _settings.TorLinkAnimeFolder = Path.Combine(library, "ANIME");
            _profile.SaveSettings(_settings);
            ApplyTorLinkLibraries();
            await Until(() => _torlinkImporter!.Entries.Count(x => x.State is TorLinkImportState.Imported or TorLinkImportState.Ignored) >= 4, 20, "The synthetic downloads were not placed");
            var film = Path.Combine(library, "FILMS", "Big Buck Bunny (2008)", "Big Buck Bunny (2008).mkv");
            Require(File.Exists(film) && File.Exists(Path.Combine(library, "FILMS", "Big Buck Bunny (2008)", "Big Buck Bunny (2008).en.srt")), "a film arrives as Title (Year)/Title (Year).mkv with its subtitle");
            Require(!Directory.EnumerateFiles(library, "*.txt", SearchOption.AllDirectories).Any(), "release notes and other annex files stay in TorLink");
            Require(LinkCount(film) == 2, "the film is a hard link: no second copy, TorLink keeps its file");
            var episode = Path.Combine(library, "SERIES", "Sintel Show", "Season 01", "Sintel Show - S01E02.mkv");
            Require(File.Exists(episode), "an episode arrives as Show/Season 01/Show - S01E02.mkv");
            Require(File.Exists(Path.Combine(library, "ANIME", "Tears of Steel", "Season 01", "Tears of Steel - S01E03.mkv")), "an anime from an anime source goes to the anime library");
            Require(_torlinkImporter!.Entries.Any(x => x.State == TorLinkImportState.Ignored && x.Source == "fitgirl"), "a game is not placed in Jellyfin");
            Require(TorLinkImports.Children.Count >= 4 && TorLinkImportsEmpty.Visibility == Visibility.Collapsed, "the page lists each download and what became of it");
            await CaptureAsync(output, "04-imports");

            var series = _torlinkImporter.Entries.First(x => x.Kind == MediaKind.Series);
            await ReclassifyTorLinkAsync(series.Id, MediaKind.Anime);
            Require(File.Exists(Path.Combine(library, "ANIME", "Sintel Show", "Season 01", "Sintel Show - S01E02.mkv")) && !Directory.Exists(Path.Combine(library, "SERIES", "Sintel Show")), "« Classer comme anime » moves the episode and removes the emptied folders");
            Require(_torlinkImporter.Find(series.Id) is { Kind: MediaKind.Anime, Method: "lien physique" } && LinkCount(Path.Combine(library, "ANIME", "Sintel Show", "Season 01", "Sintel Show - S01E02.mkv")) == 2, "the reclassified episode is still a hard link of TorLink's file");
            var entries = _torlinkImporter.Entries.Count;
            await ProcessTorLinkAsync();
            Require(_torlinkImporter.Entries.Count == entries && _torlinkImporter.Entries.All(x => x.State is TorLinkImportState.Imported or TorLinkImportState.Ignored), "a second pass imports nothing twice");

            TorLinkSettings_Click(this, new()); await CaptureAsync(output, "05-settings");
            Require(TorLinkSettings.Visibility == Visibility.Visible && TorLinkPathStatus.Text.Contains("TorLink trouvé"), "Réglages → TorLink shows the installation found");
            await CloseSettingsAnimatedAsync();
            Require(TorLinkOverlay.Visibility == Visibility.Visible, "closing the settings returns to the TorLink page");
            Width = 1024; Height = 700; await Task.Delay(600); await CaptureAsync(output, "06-small");
            AssertContained(TorLinkImportsScroll, "Import list");

            // Leaving the page from the keyboard: Alt+← typed inside the terminal, then Escape once the focus is back in Mira.
            // Real keys need this window in the foreground; otherwise trusted browser key events cover the page's side.
            var seen = new List<string>();
            KeyEventHandler probe = (_, e) => { if (FromTorLinkTerminal(e)) seen.Add($"{e.Key}/{e.SystemKey}/{Keyboard.Modifiers}"); };
            PreviewKeyDown += probe;
            try
            {
                terminal.Focus(); await Task.Delay(500);
                if (IsForegroundWindow())
                {
                    PressAltLeft();
                    var closed = await Within(() => !TorLinkOverlay.IsHitTestVisible, 5);
                    Require(closed, "Alt+← typed in the terminal (real keyboard) leaves the page" + (closed ? "" : " — keys seen from the terminal: " + string.Join(", ", seen)));
                    checks.Add("INFO WPF key events from the terminal: " + (seen.Count > 0 ? string.Join(", ", seen) : "none (the page asked to go back)"));
                    await ShowTorLinkAsync(); await Task.Delay(400);
                    TorLinkNav.Focus(); await Task.Delay(200);
                    if (!IsForegroundWindow()) throw new InvalidOperationException("The check window lost the foreground.");
                    PressKey(0x1B);
                    Require(await Within(() => !TorLinkOverlay.IsHitTestVisible, 5) && terminal.Running, "Escape outside the terminal (real keyboard) leaves the page, TorLink keeps running");
                }
                else
                {
                    await BrowserKeyAsync(terminal, "rawKeyDown", "ArrowLeft", "ArrowLeft", 0x25, null, modifiers: 1);
                    await BrowserKeyAsync(terminal, "keyUp", "ArrowLeft", "ArrowLeft", 0x25, null, modifiers: 1);
                    Require(await Within(() => !TorLinkOverlay.IsHitTestVisible, 5) && terminal.Running, "Alt+← typed in the terminal (browser key events) leaves the page, TorLink keeps running");
                    checks.Add("INFO WPF key events from the terminal: " + (seen.Count > 0 ? string.Join(", ", seen) : "none (the page asked to go back)"));
                }
            }
            finally { PreviewKeyDown -= probe; }
            await Task.Delay(300);

            var process = _torlinkTerminal!.ProcessId;
            await StopTorLinkAsync();
            Require(process is { } pid && !ProcessAlive(pid), "closing Mira ends TorLink cleanly");
            await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
        }
        catch (Exception ex)
        {
            checks.Add("FAIL " + ex.GetType().Name + ": " + ex.Message);
            checks.Add($"     terminal: {_torlinkTerminal?.State.ToString() ?? "none"}, exit code {_torlinkTerminal?.ExitCode?.ToString() ?? "-"}, blocker {_torlinkBlocker ?? "-"}, problem {_torlinkTerminal?.Problem ?? "-"}");
            if (_torlinkImporter is { } importer) checks.AddRange(importer.Entries.Select(x => $"     {x.Name} → {x.State} {x.Title} {x.Message}"));
            try { await CaptureAsync(output, "failure"); } catch (Exception capture) when (capture is IOException or InvalidOperationException or ArgumentException) { }
            await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
        }
        finally { Close(); }
    }

    /// <summary>The TorLink check only works inside its output folder: demo mode, and both --data and TORLINK_STATE_DIR in it.</summary>
    private bool TorLinkCheckIsolated()
    {
        var index = Array.IndexOf(_args, "--torlink-check");
        if (index < 0 || index + 1 >= _args.Length || !_args.Contains("--demo") || !_args.Contains("--data")) return false;
        try
        {
            var output = Path.GetFullPath(_args[index + 1]).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var state = Environment.GetEnvironmentVariable("TORLINK_STATE_DIR");
            return !string.IsNullOrWhiteSpace(state) && Path.GetFullPath(state).StartsWith(output, StringComparison.OrdinalIgnoreCase)
                && Path.GetFullPath(_profile.DirectoryPath).StartsWith(output, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>TorLink's own files, as it writes them, for three finished downloads and a game (tiny synthetic files).</summary>
    private static void PrepareSyntheticDownloads(string state, string downloads)
    {
        var data = Path.Combine(state, "data"); var config = Path.Combine(state, "config");
        Directory.CreateDirectory(data); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "config.json"), JsonSerializer.Serialize(new { downloadDir = downloads }));
        void Write(string relative, int size) { var path = Path.Combine(downloads, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[size]); }
        Write(@"Big.Buck.Bunny.2008.1080p.BluRay.x264-MIRA\Big.Buck.Bunny.2008.1080p.BluRay.x264-MIRA.mkv", 256 * 1024);
        Write(@"Big.Buck.Bunny.2008.1080p.BluRay.x264-MIRA\Subs\English.srt", 512);
        Write(@"Big.Buck.Bunny.2008.1080p.BluRay.x264-MIRA\RELEASE.txt", 64);
        Write("Sintel.Show.S01E02.1080p.WEB-DL-MIRA.mkv", 128 * 1024);
        Write("[Mira] Tears of Steel - 03 [1080p].mkv", 128 * 1024);
        Write(@"Open.Game.Setup\setup.exe", 1024);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        object Entry(string id, string name, string? source, long size) => new { id, name, source, sizeBytes = size, magnet = "magnet:?xt=urn:btih:" + id, dir = downloads, completedAt = now };
        File.WriteAllText(Path.Combine(data, "history.json"), JsonSerializer.Serialize(new object[]
        {
            Entry("1111111111111111111111111111111111111111", "Big.Buck.Bunny.2008.1080p.BluRay.x264-MIRA", "yts", 256 * 1024 + 576),
            Entry("2222222222222222222222222222222222222222", "Sintel.Show.S01E02.1080p.WEB-DL-MIRA.mkv", "eztv", 128 * 1024),
            Entry("3333333333333333333333333333333333333333", "[Mira] Tears of Steel - 03 [1080p].mkv", "nyaa", 128 * 1024),
            Entry("4444444444444444444444444444444444444444", "Open.Game.Setup", "fitgirl", 1024)
        }));
        File.WriteAllText(Path.Combine(data, "queue.json"), "[]"); File.WriteAllText(Path.Combine(data, "seeds.json"), "[]");
    }

    /// <summary>Share of non-background pixels in the terminal area of a capture.</summary>
    private double InkRatio(string output, string name)
    {
        var frame = BitmapFrame.Create(new Uri(Path.Combine(output, name + ".png")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = TorLinkTerminalHost.TranslatePoint(new Point(), (UIElement)Content);
        var area = new Int32Rect((int)(origin.X * dpi.DpiScaleX), (int)(origin.Y * dpi.DpiScaleY), (int)(TorLinkTerminalHost.ActualWidth * dpi.DpiScaleX), (int)(TorLinkTerminalHost.ActualHeight * dpi.DpiScaleY));
        area = new Int32Rect(area.X, area.Y, Math.Min(area.Width, frame.PixelWidth - area.X), Math.Min(area.Height, frame.PixelHeight - area.Y));
        if (area.Width <= 0 || area.Height <= 0) return 0;
        var pixels = new byte[area.Width * area.Height * 4];
        new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(area, pixels, area.Width * 4, 0);
        var ink = 0;
        for (var i = 0; i < pixels.Length; i += 4) if (Math.Abs(pixels[i] - 16) + Math.Abs(pixels[i + 1] - 14) + Math.Abs(pixels[i + 2] - 14) > 60) ink++;
        return ink / (double)(area.Width * area.Height);
    }

    // Without the foreground, trusted key events are injected into the page (DevTools protocol, this check only).
    private static async Task BrowserKeysAsync(TorLinkTerminal terminal, string text)
    {
        foreach (var c in text)
        {
            var code = char.IsLetter(c) ? "Key" + char.ToUpperInvariant(c) : "";
            await BrowserKeyAsync(terminal, "keyDown", c.ToString(), code, char.ToUpperInvariant(c), c.ToString());
            await BrowserKeyAsync(terminal, "keyUp", c.ToString(), code, char.ToUpperInvariant(c), null);
        }
    }
    private static async Task BrowserKeyAsync(TorLinkTerminal terminal, string type, string key, string code, int virtualKey, string? text, int modifiers = 0)
    {
        if (terminal.View?.CoreWebView2 is not { } core) throw new InvalidOperationException("No page to send keys to.");
        // modifiers: 1 Alt, 2 Ctrl, 4 Meta, 8 Shift (DevTools protocol).
        var parameters = new Dictionary<string, object> { ["type"] = type, ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = virtualKey, ["modifiers"] = modifiers };
        if (text is not null) parameters["text"] = text;
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(parameters));
        if (type is "rawKeyDown" or "keyDown") await Task.Delay(60);
    }
    private bool IsForegroundWindow()
    {
        Activate();
        var own = new WindowInteropHelper(this).Handle;
        return GetForegroundWindow() == own;
    }
    // Real keystrokes, sent only while this window is in the foreground (checked just before).
    private void TypeText(string text)
    {
        foreach (var c in text) Send(new KeyInput { Scan = c, Flags = 0x0004 }, new KeyInput { Scan = c, Flags = 0x0004 | 0x0002 });
    }
    private void PressKey(ushort key) => Send(new KeyInput { Key = key }, new KeyInput { Key = key, Flags = 0x0002 });
    // Alt down, Left (an extended key) down and up, Alt up.
    private void PressAltLeft() => Send(new KeyInput { Key = 0x12 }, new KeyInput { Key = 0x25, Flags = 0x0001 }, new KeyInput { Key = 0x25, Flags = 0x0001 | 0x0002 }, new KeyInput { Key = 0x12, Flags = 0x0002 });
    private void Send(params KeyInput[] keys)
    {
        if (GetForegroundWindow() != new WindowInteropHelper(this).Handle) throw new InvalidOperationException("The check window lost the foreground: no key was sent.");
        var inputs = keys.Select(k => new Input { Type = 1, Keyboard = k }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }
    private static bool ProcessAlive(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static uint LinkCount(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Key; public ushort Scan; public uint Flags; public uint Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyInput Keyboard; }
    // BY_HANDLE_FILE_INFORMATION: FILETIME fields are 4-byte aligned.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint Attributes; public long Created, Accessed, Written; public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
