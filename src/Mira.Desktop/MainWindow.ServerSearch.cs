using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Mira.Core;
using Mira.Core.Tailscale;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The login screen's server, found rather than typed when no address is known yet (<see cref="ServerSearch"/>):
/// Jellyfin on this PC, on the home network or on Tailscale, the others found offered beside it. 127.0.0.1 only for a
/// Jellyfin on this PC. What the person typed is never replaced.
/// </summary>
public partial class MainWindow
{
    private int _loginSearchVersion;
    /// <summary>The search running or done: « Se connecter » with no address waits for it.</summary>
    private Task _loginSearch = Task.CompletedTask;
    private IReadOnlyList<ServerSearch.Found> _loginServers = [];
    /// <summary>The last search found nothing: « Tailscale intégré » is offered (Jellyfin may be on Tailscale).</summary>
    private bool _loginNoneFound;
    /// <summary>The address the search put in the field: replaced by a choice or another search, unlike one typed.</summary>
    private string? _loginFoundAddress;
    private bool ServerFieldFree => ServerBox.Text.Trim().Length == 0 || ServerBox.Text == _loginFoundAddress;
    private static string ThisPc => L("ce PC", "this PC");

    private void FindServer()
    {
        if (ValidationRun || _demo) return;
        _loginSearch = FindServerAsync(++_loginSearchVersion);
    }
    private void ServerSearchAgain_Click(object sender, RoutedEventArgs e) => FindServer();
    private async Task FindServerAsync(int version)
    {
        ServerHint.Text = ServerSearch.Searching; ServerSearchAgain.Visibility = Visibility.Collapsed; _loginNoneFound = false;
        IReadOnlyList<ServerSearch.Found> found = []; var installed = false;
        RenderTailnetLogin();
        try
        {
            // Tailscale's devices only when it is connected here: its command line is not started for nothing.
            var (app, tailnet) = await Task.Run(() => LocalNetwork.ThisPcOnTailnet() is null ? (false, (IReadOnlyList<string>)[]) : (true, ServerSearch.TailnetDevices(TailscaleCli.Status())));
            _tailscaleAppHere = app;
            // Without the app, those « Tailscale intégré » sees, once it is in.
            if (!app && _tailnet?.Node is { Helper: TailnetHelper.Running } node) tailnet = ServerSearch.NodeDevices(node.Status);
            found = await ServerSearch.FindAsync(tailnet);
            if (found.Count == 0) installed = await Task.Run(JellyfinInstaller.IsInstalled);
        }
        catch (Exception ex) when (IsExpected(ex) || ex is SocketException or NetworkInformationException or System.Security.SecurityException) { }
        if (version != _loginSearchVersion) return;
        _loginServers = found; _loginNoneFound = found.Count == 0;
        if (found.Count > 0 && ServerFieldFree) { UseServer(found[0]); RenderTailnetLogin(); return; }
        // Typed meanwhile: kept, with what was found beside it, and nothing said when nothing was.
        if (!ServerFieldFree) { ServerHint.Text = ""; ShowServerChoices(null); RenderTailnetLogin(); return; }
        // A Jellyfin installed here but silent (stopped, still starting): its address, and what is wrong with it.
        if (installed && ServerFieldFree) { _loginFoundAddress = JellyfinSetup.LocalServer.AbsoluteUri; ServerBox.Text = _loginFoundAddress; }
        ServerHint.Text = installed ? L("Jellyfin est installé sur ce PC, mais ne répond pas encore : vérifie qu’il est lancé.", "Jellyfin is installed on this PC, but isn’t answering yet: check that it’s running.")
            : ServerSearch.NoneFound(ThisPc);
        ShowServerChoices(null); ServerSearchAgain.Visibility = Visibility.Visible;
        RenderTailnetLogin();
    }
    private void UseServer(ServerSearch.Found found)
    {
        _loginFoundAddress = found.Address; ServerBox.Text = found.Address;
        ServerHint.Text = ServerSearch.Said(found, ThisPc); LoginError.Text = "";
        ShowServerChoices(found);
    }
    /// <summary>The servers found other than <paramref name="chosen"/>, one button each.</summary>
    private void ShowServerChoices(ServerSearch.Found? chosen)
    {
        ServerChoices.Children.Clear();
        var others = _loginServers.Where(x => x != chosen).ToList();
        ServerChoices.Visibility = others.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (others.Count == 0) return;
        ServerChoices.Children.Add(new TextBlock { Text = chosen is null ? L("Serveurs trouvés :", "Servers found:") : ServerSearch.Others, FontSize = 12, Foreground = Palette.Get("Faint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 8, 6) });
        foreach (var server in others)
        {
            var button = new Button { Content = ServerSearch.Choice(server), Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6), ToolTip = server.Address };
            button.Click += (_, _) => { UseServer(server); FocusLogin(); };
            ServerChoices.Children.Add(button);
        }
    }
    /// <summary>An address typed over the one found: what was said about it no longer is.</summary>
    private void ServerTyped()
    {
        ServerPlaceholder.Visibility = ServerBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loginFoundAddress is not null && ServerBox.Text != _loginFoundAddress && ServerHint.Text != ServerSearch.Searching) { ServerHint.Text = ""; _loginFoundAddress = null; ShowServerChoices(null); }
        // An address on Tailscale typed: « Tailscale intégré » offered for it.
        if (IsLoaded) RenderTailnetLogin();
    }
}
