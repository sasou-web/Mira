using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Mira.Desktop.TorLink;

public enum TerminalState { Idle, Starting, Running, Exited, Unavailable }

/// <summary>
/// The TorLink terminal inside Mira. TorLink runs unchanged in a pseudo console and xterm.js draws it in a local
/// WebView2 page served from Mira's own resources. The page only exchanges terminal text and its size with Mira:
/// it cannot navigate elsewhere, open windows, download, or reach the network.
/// </summary>
internal sealed class TorLinkTerminal : IDisposable
{
    public const string HostName = "torlink.mira.example";
    private const string Origin = "https://" + HostName + "/";
    private readonly Dispatcher _dispatcher;
    private readonly string _userData;
    private readonly StringBuilder _pending = new(), _tail = new();
    private WebView2CompositionControl? _view;
    private PseudoConsole? _console;
    private TorLinkInstallation? _installation;
    private IReadOnlyDictionary<string, string>? _environment;
    private Task? _initialization;
    private short _columns = 100, _rows = 30;
    private bool _flushQueued, _pageReady, _startWhenReady;
    public event Action? StateChanged;
    /// <summary>Alt+← or the Back key typed in the terminal, when WebView2 delivered it to the page instead of WPF.</summary>
    public event Action? BackRequested;
    public TerminalState State { get; private set; }
    public int? ExitCode { get; private set; }
    public string? Problem { get; private set; }
    public int? ProcessId => _console?.ProcessId;
    public bool Running => _console is { HasExited: false };
    /// <summary>The page's control: keys WebView2 forwards to WPF originate from it.</summary>
    public WebView2CompositionControl? View => _view;

    public TorLinkTerminal(Dispatcher dispatcher, string userDataFolder)
    { _dispatcher = dispatcher; _userData = userDataFolder; }

    /// <summary>True once the page has loaded and reported its size.</summary>
    public bool PageReady => _pageReady;

    // The page's files, embedded in Mira: "terminal.html", "xterm/xterm.js"… Nothing else can be served.
    private static readonly Dictionary<string, string> Pages = typeof(TorLinkTerminal).Assembly.GetManifestResourceNames()
        .Where(x => x.StartsWith("Mira.TorLink/", StringComparison.Ordinal))
        .ToDictionary(x => x["Mira.TorLink/".Length..].Replace('\\', '/'), x => x, StringComparer.Ordinal);

    private static CoreWebView2WebResourceResponse Serve(CoreWebView2Environment environment, string uri)
    {
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host == HostName ? parsed.AbsolutePath.TrimStart('/') : "";
        if (!Pages.TryGetValue(path, out var name) || typeof(TorLinkTerminal).Assembly.GetManifestResourceStream(name) is not { } resource)
            return environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
        var buffer = new MemoryStream();
        using (resource) resource.CopyTo(buffer);
        buffer.Position = 0;
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8", _ => "application/octet-stream"
        };
        return environment.CreateWebResourceResponse(buffer, 200, "OK", $"Content-Type: {type}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store");
    }

    /// <summary>Creates the page once, inside <paramref name="host"/>. Leaves <see cref="State"/> Unavailable without WebView2.</summary>
    public Task InitializeAsync(Decorator host) => _initialization ??= InitializeCoreAsync(host);

    private async Task InitializeCoreAsync(Decorator host)
    {
        try
        {
            Directory.CreateDirectory(_userData);
            // A local page needs no background services (field trials, component downloads, reputation lookups).
            var options = new CoreWebView2EnvironmentOptions("--disable-background-networking") { Language = "fr-FR", AreBrowserExtensionsEnabled = false };
            var environment = await CoreWebView2Environment.CreateAsync(null, _userData, options);
            // Composition hosting: drawn by WPF, so the mini-player and notices can overlap it.
            var view = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 14, 14, 16), Focusable = true };
            AutomationProperties.SetName(view, "Terminal TorLink");
            host.Child = view;
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            var settings = core.Settings;
            settings.AreDevToolsEnabled = false; settings.AreDefaultContextMenusEnabled = false; settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreHostObjectsAllowed = false; settings.AreBrowserAcceleratorKeysEnabled = false; settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false; settings.IsPinchZoomEnabled = false; settings.IsSwipeNavigationEnabled = false;
            settings.IsGeneralAutofillEnabled = false; settings.IsPasswordAutosaveEnabled = false; settings.IsBuiltInErrorPageEnabled = false;
            settings.IsReputationCheckingRequired = false; settings.IsWebMessageEnabled = true;
            // Every request to the page's reserved host is answered from Mira's own resources, before any network.
            core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => e.Response = Serve(environment, e.Request.Uri);
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin + "terminal.html", StringComparison.Ordinal)) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += (_, _) => { _pageReady = false; _dispatcher.BeginInvoke(() => { try { core.Reload(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { } }); };
            _view = view;
            core.Navigate($"{Origin}terminal.html?build={Environment.OSVersion.Version.Build}");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            host.Child = null;
            Problem = ex is WebView2RuntimeNotFoundException ? "Le moteur WebView2 de Microsoft Edge est absent de ce PC." : "Le terminal intégré n’a pas pu s’ouvrir.";
            SetState(TerminalState.Unavailable);
        }
    }

    /// <summary>Starts TorLink as soon as the page knows its size (at once if it already does). Does nothing while it runs.</summary>
    public void Start(TorLinkInstallation installation)
    {
        if (Running || State == TerminalState.Unavailable) return;
        _installation = installation; _environment = ChildEnvironment();
        SetState(TerminalState.Starting);
        if (_pageReady) Launch(); else _startWhenReady = true;
    }

    public void Focus()
    {
        if (_view is null) return;
        _view.Focus();
        Post(new { type = "focus" });
    }

    /// <summary>Loads the page again when it never reported ready; a waiting start follows, a running TorLink is redrawn.</summary>
    public void Reload()
    {
        if (_view?.CoreWebView2 is not { } core) return;
        _pageReady = false;
        try { core.Navigate($"{Origin}terminal.html?build={Environment.OSVersion.Version.Build}"); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { }
    }

    /// <summary>Asks TorLink to quit (Ctrl+C lets it save its queue), then ends it after a short grace period.</summary>
    public async Task StopAsync()
    {
        var console = _console;
        _console = null;
        if (console is null) return;
        await console.StopAsync(TimeSpan.FromSeconds(3));
        console.Dispose();
    }

    private void Launch()
    {
        _startWhenReady = false;
        if (_installation is null || _environment is null) return;
        lock (_pending) _tail.Clear();
        Post(new { type = "reset" });
        try
        {
            PseudoConsole? console = null;
            // The exit is handled on the dispatcher, after this method has stored the console.
            console = PseudoConsole.Start(_installation.Node, [_installation.Entry], _installation.Root, _environment, _columns, _rows,
                OnOutput, code => _dispatcher.BeginInvoke(() => OnExited(console!, code)));
            _console = console; ExitCode = null; Problem = null;
            SetState(TerminalState.Running);
            Focus();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Problem = "TorLink n’a pas pu démarrer : " + ex.Message;
            SetState(TerminalState.Exited);
        }
    }

    private void OnExited(PseudoConsole console, int code)
    {
        if (!ReferenceEquals(console, _console)) return;
        Flush();
        ExitCode = code; _console = null;
        // TorLink leaves with 0 when asked to (q, Ctrl+C, Échap); otherwise it printed the reason just before.
        if (code != 0) Problem = $"TorLink s’est arrêté sur une erreur (code {code})" + (LastLine() is { } line ? " : « " + line + " »" : ".");
        Post(new { type = "exit", code });
        console.Dispose();
        SetState(TerminalState.Exited);
    }

    private void OnOutput(string text)
    {
        lock (_pending)
        {
            _pending.Append(text);
            _tail.Append(text);
            if (_tail.Length > 4096) _tail.Remove(0, _tail.Length - 4096);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        // Everything the reader collected meanwhile leaves in one message.
        _dispatcher.BeginInvoke(Flush);
    }

    private void Flush()
    {
        string text;
        lock (_pending) { text = _pending.ToString(); _pending.Clear(); _flushQueued = false; }
        if (text.Length > 0) Post(new { type = "output", data = text });
    }

    private void Post(object message)
    {
        if (!_pageReady || _view?.CoreWebView2 is not { } core) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>Only four messages are accepted from the page: ready, resize (bounded sizes), input (bounded text) and back.</summary>
    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.Ordinal)) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            switch (type.GetString())
            {
                case "input" when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String && data.GetString() is { Length: > 0 and <= 65536 } text:
                    _console?.Write(text);
                    break;
                case "resize" when Size(root) is { } size:
                    (_columns, _rows) = size;
                    _console?.Resize(_columns, _rows);
                    break;
                case "back":
                    BackRequested?.Invoke();
                    break;
                case "ready" when Size(root) is { } size:
                    (_columns, _rows) = size;
                    _pageReady = true;
                    if (_startWhenReady) Launch();
                    else if (_console is { HasExited: false } running) Repaint(running);
                    break;
            }
        }
        catch (JsonException) { }
    }

    /// <summary>The last line TorLink printed, without terminal control sequences (its error message when it fails).</summary>
    private string? LastLine()
    {
        string text;
        lock (_pending) text = _tail.ToString();
        var plain = System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-_]|[\x00-\x08\x0b-\x1f\x7f]", "");
        var line = plain.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0);
        return line is null ? null : line.Length > 160 ? line[..160] + "…" : line;
    }

    /// <summary>A reloaded page starts blank: a size change makes the pseudo console redraw the whole screen.</summary>
    private void Repaint(PseudoConsole console)
    {
        console.Resize(_columns, (short)Math.Max(5, _rows - 1));
        console.Resize(_columns, _rows);
    }

    private static (short Columns, short Rows)? Size(JsonElement root) =>
        root.TryGetProperty("cols", out var c) && c.TryGetInt32(out var columns) && root.TryGetProperty("rows", out var r) && r.TryGetInt32(out var rows)
        && columns is >= 20 and <= 500 && rows is >= 5 and <= 300 ? ((short)columns, (short)rows) : null;

    /// <summary>Mira's own environment, plus true-colour support for TorLink's palette. No Jellyfin credential lives in it.</summary>
    private static IReadOnlyDictionary<string, string> ChildEnvironment()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.Length > 0 && !key.StartsWith('=') && entry.Value is string value) values[key] = value;
        values["COLORTERM"] = "truecolor";
        return values;
    }

    private void SetState(TerminalState state) { State = state; StateChanged?.Invoke(); }

    public void Dispose()
    {
        _console?.Dispose(); _console = null;
        _view?.Dispose(); _view = null;
    }
}
