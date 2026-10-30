using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Tailscale;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>Connexion à Jellyfin: the address as typed (« 192.168.1.20 », « nas:8096 », a copied URL), then the account.</summary>
public sealed class LoginPage : UserControl
{
    private readonly MainWindow _shell;
    private readonly TextBox _server = new() { Watermark = L("Adresse du serveur (ex. 192.168.1.20)", "Server address (e.g. 192.168.1.20)") };
    private readonly TextBox _user = new() { Watermark = L("Nom d’utilisateur", "User name") };
    private readonly TextBox _password = new() { Watermark = L("Mot de passe", "Password"), PasswordChar = '•', RevealPassword = false };
    private readonly Button _connect;
    private readonly TextBlock _error = new() { Foreground = Ui.Brush("#F0B4A7"), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    /// <summary>The server found on this Mac, the home network or Tailscale (<see cref="ServerSearch"/>), and the others found.</summary>
    private readonly TextBlock _found = new() { Foreground = Ui.Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly WrapPanel _choices = new() { IsVisible = false };
    private readonly Button _searchAgain;
    // « Tailscale intégré », for a Mac without the Tailscale app: offered when the server is on Tailscale or none was found.
    private readonly TextBlock _tailnetText = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _tailnetNote = new() { FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly Button _tailnetButton;
    private readonly Border _tailnet;
    /// <summary>The last search found nothing: « Tailscale intégré » is offered (Jellyfin may be on Tailscale).</summary>
    private bool _noneFound;
    public string Server { get => _server.Text ?? ""; set => _server.Text = value; }

    public LoginPage(MainWindow shell, string? error = null)
    {
        _shell = shell;
        Avalonia.Automation.AutomationProperties.SetName(_server, L("Adresse du serveur Jellyfin", "Jellyfin server address"));
        Avalonia.Automation.AutomationProperties.SetName(_user, L("Nom d’utilisateur", "User name"));
        Avalonia.Automation.AutomationProperties.SetName(_password, L("Mot de passe", "Password"));
        _connect = Ui.Action(L("Se connecter", "Sign in"), null, "primary", () => _ = ConnectAsync());
        _searchAgain = Ui.Action(ServerSearch.SearchAgain, "refresh", "chip quiet", FindServer);
        _searchAgain.HorizontalAlignment = HorizontalAlignment.Left; _searchAgain.IsVisible = false;
        _connect.HorizontalAlignment = HorizontalAlignment.Stretch; _connect.HorizontalContentAlignment = HorizontalAlignment.Center; _connect.Padding = new Thickness(18, 13);
        _tailnetButton = Ui.Action(TailnetText.SignIn, null, "chip", TailnetClicked);
        _tailnetButton.HorizontalAlignment = HorizontalAlignment.Left;
        _tailnet = new Border
        {
            IsVisible = false, Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(10), Background = Ui.Brush("#14FFFFFF"), BorderBrush = Ui.Brush("#26FFFFFF"), BorderThickness = new Thickness(1),
            Child = Ui.Column(8, _tailnetText, _tailnetNote, _tailnetButton)
        };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_tailnetText, Avalonia.Automation.AutomationLiveSetting.Polite);
        foreach (var box in new[] { _server, _user, _password }) box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = ConnectAsync(); } };
        // Another address is another question: the warning given for the previous one no longer answers it.
        _server.TextChanged += (_, _) => { ServerTyped(); RenderTailnet(); if (_warned is not null && !_busy && _server.Text != _warned) { _warned = null; Label.Text = L("Se connecter", "Sign in"); _error.IsVisible = false; } };
        if (error is not null) ShowError(error);

        var form = Ui.Column(14,
            new Icon("brand", 52) { Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) },
            Ui.Heading(L("Connexion à Jellyfin", "Sign in to Jellyfin"), 28, 1),
            Ui.Text(L("Mira lit ta bibliothèque Jellyfin. Chez toi, l’adresse du PC qui l’héberge suffit ; ailleurs, celle que Tailscale lui donne.", "Mira plays your Jellyfin library. At home, the address of the PC it runs on is enough; elsewhere, the one Tailscale gives it."), 14.5, color: Ui.Muted),
            Spaced(_server, 10), _found, _choices, _searchAgain, _tailnet, _user, _password, _error, Spaced(_connect, 6),
            Ui.Text(L("Ton mot de passe n’est jamais enregistré : Mira garde seulement la session ouverte par Jellyfin.", "Your password is never saved: Mira only keeps the session Jellyfin opened."), 12.5, color: Ui.Faint));
        form.Width = 420;
        Content = new Grid
        {
            Background = new RadialGradientBrush { Center = new RelativePoint(0.3, 0.2, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.3, 0.2, RelativeUnit.Relative), RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.9, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#1A1D22"), 0), new GradientStop(Color.Parse("#070708"), 1) } },
            Children = { new Border { Child = form, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(40), CornerRadius = new CornerRadius(18), Background = Ui.Brush("#C00E0E10"), BorderBrush = Ui.Brush("#1F1F23"), BorderThickness = new Thickness(1) } }
        };
        AttachedToVisualTree += (_, _) =>
        {
            // No address yet (none kept, no invitation): the one the search finds; the account meanwhile.
            if (string.IsNullOrWhiteSpace(_server.Text) && !_shell.Args.Contains("--self-check")) FindServer();
            (string.IsNullOrWhiteSpace(_server.Text) && _search.IsCompleted ? _server : _user).Focus();
            _shell.Tailnet.Changed += RenderTailnet; RenderTailnet();
        };
        DetachedFromVisualTree += (_, _) => _shell.Tailnet.Changed -= RenderTailnet;
    }
    private static Control Spaced(Control control, double top) { control.Margin = new Thickness(0, top, 0, 0); return control; }
    private void ShowError(string text) { _error.Text = text; _error.IsVisible = true; }

    /// <summary>Fills the form and signs in, as a click on « Se connecter » would (the self-check).</summary>
    public Task SignInAsync(string server, string user, string password)
    {
        _server.Text = server; _user.Text = user; _password.Text = password;
        return ConnectAsync();
    }
    public string? Error => _error.IsVisible ? _error.Text : null;

    private int _searchVersion;
    /// <summary>The search running or done: « Se connecter » with no address waits for it.</summary>
    private Task _search = Task.CompletedTask;
    private IReadOnlyList<ServerSearch.Found> _servers = [];
    /// <summary>The address the search put in the field: replaced by a choice or another search, unlike one typed.</summary>
    private string? _foundAddress;
    private bool FieldFree => string.IsNullOrWhiteSpace(_server.Text) || _server.Text == _foundAddress;
    private static string ThisMac => L("ce Mac", "this Mac");
    private void FindServer() => _search = FindServerAsync(++_searchVersion);
    /// <summary>« Tailscale intégré » just joined the tailnet: its devices searched, unless an address was typed.</summary>
    public void FindAgain() { if (FieldFree) FindServer(); }
    private async Task FindServerAsync(int version)
    {
        ShowFound(ServerSearch.Searching); _searchAgain.IsVisible = false; _noneFound = false;
        RenderTailnet();
        IReadOnlyList<ServerSearch.Found> found = [];
        try
        {
            var (app, devices) = await Task.Run(() => (LocalNetwork.ThisPcOnTailnet() is not null, ServerSearch.TailnetDevices(TailscaleCli.Status())));
            _shell.Tailnet.AppHere = app;
            // Without the app, those « Tailscale intégré » sees, once it is in.
            if (!app && _shell.Tailnet.Current?.Node is { Helper: TailnetHelper.Running } node) devices = ServerSearch.NodeDevices(node.Status);
            found = await ServerSearch.FindAsync(devices);
        }
        catch (Exception ex) when (Errors.Expected(ex) || ex is System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException) { }
        if (version != _searchVersion) return;
        _servers = found; _noneFound = found.Count == 0;
        if (found.Count > 0 && FieldFree) { UseServer(found[0]); RenderTailnet(); return; }
        // Typed meanwhile: kept, with what was found beside it, and nothing said when nothing was.
        if (!FieldFree) { ShowFound(null); ShowChoices(null); RenderTailnet(); return; }
        ShowFound(ServerSearch.NoneFound(ThisMac)); ShowChoices(null); _searchAgain.IsVisible = true;
        RenderTailnet();
    }
    private void ShowFound(string? text) { _found.Text = text; _found.IsVisible = text is not null; }
    private void UseServer(ServerSearch.Found found)
    {
        _foundAddress = found.Address; _server.Text = found.Address;
        ShowFound(ServerSearch.Said(found, ThisMac)); _error.IsVisible = false;
        ShowChoices(found);
    }
    /// <summary>The servers found other than <paramref name="chosen"/>, one button each.</summary>
    private void ShowChoices(ServerSearch.Found? chosen)
    {
        _choices.Children.Clear();
        var others = _servers.Where(x => x != chosen).ToList();
        _choices.IsVisible = others.Count > 0;
        if (others.Count == 0) return;
        _choices.Children.Add(new TextBlock { Text = chosen is null ? L("Serveurs trouvés :", "Servers found:") : ServerSearch.Others, FontSize = 12.5, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
        foreach (var server in others)
        {
            var button = Ui.Action(ServerSearch.Choice(server), null, "chip", () => { UseServer(server); _user.Focus(); });
            button.Margin = new Thickness(0, 0, 6, 6); ToolTip.SetTip(button, server.Address);
            _choices.Children.Add(button);
        }
    }
    /// <summary>An address typed over the one found: what was said about it no longer is.</summary>
    private void ServerTyped()
    {
        if (_foundAddress is not null && _server.Text != _foundAddress && _found.Text != ServerSearch.Searching) { ShowFound(null); _foundAddress = null; ShowChoices(null); }
    }

    private bool _busy;
    /// <summary>The http:// address on the Internet whose warning was shown: the next click on it signs in anyway.</summary>
    private string? _warned;
    private TextBlock Label => ((StackPanel)_connect.Content!).Children.OfType<TextBlock>().Single();
    private async Task ConnectAsync()
    {
        if (_busy) return; _busy = true;
        _connect.IsEnabled = false; _error.IsVisible = false;
        var label = Label;
        JellyfinClient? client = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_user.Text)) throw new UserInputException(L("Indique ton nom d’utilisateur Jellyfin.", "Enter your Jellyfin user name."));
            // Find the server before sending the password.
            label.Text = L("Recherche du serveur…", "Finding the server…");
            // No address yet: the one the search is finding.
            if (string.IsNullOrWhiteSpace(_server.Text)) await _search;
            // A server on Tailscale, and nothing here to reach it: said at once, rather than after every address timed out.
            if (_shell.Tailnet.Offered && BuiltInTailscale.OnTailnet(_server.Text))
            {
                await _shell.Tailnet.ReadAppAsync();
                if (!_shell.Tailnet.AppHere && !_shell.Settings.Tailnet && _shell.Tailnet.Current?.SigningIn != true)
                { ShowError(L("Ce serveur est sur Tailscale : connecte d’abord Tailscale intégré, juste au-dessus, ou installe l’app Tailscale.", "This server is on Tailscale: first sign in to Built-in Tailscale, just above, or install the Tailscale app.")); return; }
            }
            var server = await ServerAddress.DiscoverAsync(_server.Text ?? "");
            _server.Text = server.Address;
            if (server.Address != _warned && await ServerAddress.ExposesPasswordAsync(server.Address)) { _warned = server.Address; ShowError(ServerAddress.PlainHttpWarning); return; }
            label.Text = L("Connexion…", "Signing in…");
            JellyfinClient.DeviceName = "Mac";
            client = new JellyfinClient(new(server.Address, "", _user.Text.Trim(), "", _shell.Profile.DeviceId));
            var connection = await client.LoginAsync(_user.Text.Trim(), _password.Text ?? "");
            // The keychain may ask the person: never on the UI thread.
            await Task.Run(() => _shell.Profile.SaveConnection(connection)); _password.Text = ""; _warned = null;
            await _shell.ActivateAsync(connection);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { ShowError(Errors.Friendly(ex)); }
        finally { client?.Dispose(); _connect.IsEnabled = true; label.Text = _warned == _server.Text ? L("Se connecter quand même", "Sign in anyway") : L("Se connecter", "Sign in"); _busy = false; }
    }

    /// <summary>
    /// For a Mac without the Tailscale app: « Tailscale intégré » offered when the server is on Tailscale or none was
    /// found here, its sign-in followed, and once in, what it is connected as.
    /// </summary>
    private void RenderTailnet()
    {
        var tailscale = _shell.Tailnet; var tailnet = tailscale.Current;
        var helper = tailnet?.Helper ?? TailnetHelper.Off; var state = tailnet?.State;
        var signingIn = tailnet?.SigningIn == true; var page = tailnet?.Page; var signedIn = _shell.Settings.Tailnet;
        var onTailnet = BuiltInTailscale.OnTailnet(_server.Text);
        var show = tailscale.Offered && !tailscale.AppHere && tailnet?.Problem is null && (signingIn || signedIn || onTailnet || _noneFound);
        _tailnet.IsVisible = show;
        if (!show) return;
        var engaged = signingIn || signedIn;
        _tailnetText.Text = state == TailnetStates.Running ? $"{TailnetText.Name} · {tailscale.Line()}"
            : engaged ? tailscale.Line()
            : onTailnet ? TailnetText.ServerOnTailnet : TailnetText.AwayFromHome;
        _tailnetNote.Text = engaged ? (state is null or TailnetStates.Starting && signingIn ? BuiltInTailscale.Firewall : "") : TailnetText.Privacy;
        _tailnetNote.IsVisible = _tailnetNote.Text.Length > 0;
        var label = state == TailnetStates.MachineAuth ? TailnetText.Console
            : signingIn && page is not null ? TailnetText.ReopenPage
            : engaged && helper is TailnetHelper.Failed or TailnetHelper.Restarting or TailnetHelper.Stopped ? TailnetText.Retry : TailnetText.SignIn;
        if (_tailnetButton.Content is StackPanel content && content.Children.OfType<TextBlock>().FirstOrDefault() is { } text) text.Text = label;
        Avalonia.Automation.AutomationProperties.SetName(_tailnetButton, label);
        _tailnetButton.IsEnabled = !signingIn || page is not null || state == TailnetStates.MachineAuth;
        _tailnetButton.IsVisible = !(state == TailnetStates.Running || helper == TailnetHelper.Starting && !signingIn);
    }
    private void TailnetClicked()
    {
        if (_shell.Tailnet.Current?.State == TailnetStates.MachineAuth) BuiltInTailscale.OpenConsole();
        else _ = _shell.Tailnet.SignInAsync();
    }
    /// <summary>What the login page shows of « Tailscale intégré » (the check): its line, and its button when there is one.</summary>
    public (bool Shown, string Text, string Note, string? Button) TailnetOffer =>
        (_tailnet.IsVisible, _tailnetText.Text ?? "", _tailnetNote.IsVisible ? _tailnetNote.Text ?? "" : "", _tailnetButton.IsVisible ? Avalonia.Automation.AutomationProperties.GetName(_tailnetButton) : null);
    /// <summary>« Se connecter à Tailscale », as a click on the offer would (the check).</summary>
    public void ClickTailnet() => TailnetClicked();
}
