using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Mira.Core;
using Mira.Desktop.Views;
using static Mira.Core.Lang;

namespace Mira.Desktop;

/// <summary>
/// Your friend's party, on their Jellyfin: Mira web as their guest (#/party/&lt;code&gt;), in a window of Mira rather than
/// the browser. Only the party's own server opens in it (anything else goes to the browser); its guest stays in the
/// profile's own WebView2 data, so the same PC comes back as the same guest. Video in full screen fills the window.
/// </summary>
public sealed class PartyWindow : Window
{
    private readonly Uri _page;
    private readonly string _userData;
    private readonly WebView2 _view = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 14, 14, 16) };
    private WindowState _before = WindowState.Normal;
    private System.Windows.Threading.DispatcherTimer? _arrival;
    private bool _arrived;

    /// <summary>The window of a party already open: a second link to it comes back here.</summary>
    public static PartyWindow? Open { get; private set; }

    private PartyWindow(Uri page, string userData)
    {
        _page = page; _userData = userData;
        Title = L("Party Mira", "Mira party"); Width = 1180; Height = 760; MinWidth = 420; MinHeight = 520;
        Background = Palette.Get("Bg");
        AutomationProperties.SetName(_view, L("Party Mira", "Mira party"));
        Content = _view;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => { _arrival?.Stop(); if (ReferenceEquals(Open, this)) Open = null; _view.Dispose(); };
    }

    /// <summary>Opens the party at <paramref name="page"/> (Mira web's #/party/… on its server), or shows it when open.</summary>
    public static void Enter(Uri page, string userData, Action<string> fallback)
    {
        if (Open is { } open && open._page == page) { open.Activate(); return; }
        Open?.Close();
        var window = new PartyWindow(page, userData) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window._fallback = fallback;
        Open = window;
        window.Show();
    }
    private Action<string>? _fallback;

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(_userData);
            var options = new CoreWebView2EnvironmentOptions { Language = Culture.Name, AreBrowserExtensionsEnabled = false };
            var environment = await CoreWebView2Environment.CreateAsync(null, _userData, options);
            await _view.EnsureCoreWebView2Async(environment);
            var core = _view.CoreWebView2;
            var settings = core.Settings;
            settings.AreDevToolsEnabled = false; settings.AreHostObjectsAllowed = false; settings.IsStatusBarEnabled = false;
            settings.IsGeneralAutofillEnabled = false; settings.IsPasswordAutosaveEnabled = false; settings.IsReputationCheckingRequired = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            // The party's server only: its other links (a title's page elsewhere, a help page) open in the browser.
            core.NavigationStarting += (_, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var to) && SameOrigin(to)) return;
                e.Cancel = true; Browse(e.Uri);
            };
            core.FrameNavigationStarting += (_, e) => { if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var to) || !SameOrigin(to)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; Browse(e.Uri); };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? L("Party Mira", "Mira party") : core.DocumentTitle;
            core.ContainsFullScreenElementChanged += (_, _) => FullScreen(core.ContainsFullScreenElement);
            // The browser can reach a party this window can't (its own secure DNS, while WebView2 asks Windows, whose
            // MagicDNS gives a tailnet address for a Funnel name): until the party's page has come once, a failure or
            // a page that doesn't come goes to the browser instead of an error page.
            core.NavigationCompleted += (_, e) =>
            {
                if (_arrived) return;
                if (e.IsSuccess) { _arrived = true; _arrival?.Stop(); return; }
                if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) ToBrowser();
            };
            _arrival = new System.Windows.Threading.DispatcherTimer { Interval = Arrival };
            _arrival.Tick += (_, _) => { _arrival.Stop(); if (!_arrived) ToBrowser(); };
            _arrival.Start();
            core.Navigate(_page.AbsoluteUri);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // No WebView2 here: the browser does the same.
            Close();
            Browse(_page.AbsoluteUri);
        }
    }

    /// <summary>How long the party's page may take to come the first time before the browser takes over.</summary>
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(15);

    private void ToBrowser()
    {
        if (_arrived) return;
        _arrived = true; _arrival?.Stop();
        _fallback?.Invoke(L("Mira n’a pas pu joindre cette party : elle s’ouvre dans ton navigateur.", "Mira couldn’t reach this party: it opens in your browser."));
        Close();
        Browse(_page.AbsoluteUri);
    }

    private bool SameOrigin(Uri to) =>
        to.Scheme == _page.Scheme && to.Port == _page.Port && to.Host.Equals(_page.Host, StringComparison.OrdinalIgnoreCase);

    private void FullScreen(bool on)
    {
        if (on)
        {
            _before = WindowState;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
            WindowState = _before;
        }
    }

    private void Browse(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { _fallback?.Invoke(L("Windows n’a pas pu ouvrir le navigateur : ", "Windows couldn’t open the browser: ") + uri.AbsoluteUri); }
    }
}
