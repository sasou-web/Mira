using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>Connexion à Jellyfin: the address as typed (« 192.168.1.20 », « nas:8096 », a copied URL), then the account.</summary>
public sealed class LoginPage : UserControl
{
    private readonly MainWindow _shell;
    private readonly TextBox _server = new() { Watermark = "Adresse du serveur (ex. 192.168.1.20)" };
    private readonly TextBox _user = new() { Watermark = "Nom d’utilisateur" };
    private readonly TextBox _password = new() { Watermark = "Mot de passe", PasswordChar = '•', RevealPassword = false };
    private readonly Button _connect;
    private readonly TextBlock _error = new() { Foreground = Ui.Brush("#F0B4A7"), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    public string Server { get => _server.Text ?? ""; set => _server.Text = value; }

    public LoginPage(MainWindow shell, string? error = null)
    {
        _shell = shell;
        Avalonia.Automation.AutomationProperties.SetName(_server, "Adresse du serveur Jellyfin");
        Avalonia.Automation.AutomationProperties.SetName(_user, "Nom d’utilisateur");
        Avalonia.Automation.AutomationProperties.SetName(_password, "Mot de passe");
        _connect = Ui.Action("Se connecter", null, "primary", () => _ = ConnectAsync());
        _connect.HorizontalAlignment = HorizontalAlignment.Stretch; _connect.HorizontalContentAlignment = HorizontalAlignment.Center; _connect.Padding = new Thickness(18, 13);
        foreach (var box in new[] { _server, _user, _password }) box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = ConnectAsync(); } };
        if (error is not null) ShowError(error);

        var form = Ui.Column(14,
            new Icon("brand", 52) { Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) },
            Ui.Text("Connexion à Jellyfin", 28, FontWeight.Bold),
            Ui.Text("Mira lit ta bibliothèque Jellyfin. Chez toi, l’adresse du PC qui l’héberge suffit ; ailleurs, celle que Tailscale lui donne.", 14.5, color: Ui.Muted),
            Spaced(_server, 10), _user, _password, _error, Spaced(_connect, 6),
            Ui.Text("Ton mot de passe n’est jamais enregistré : Mira garde seulement la session ouverte par Jellyfin.", 12.5, color: Ui.Faint));
        form.Width = 420;
        Content = new Grid
        {
            Background = new RadialGradientBrush { Center = new RelativePoint(0.3, 0.2, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.3, 0.2, RelativeUnit.Relative), RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.9, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#1A1D22"), 0), new GradientStop(Color.Parse("#070708"), 1) } },
            Children = { new Border { Child = form, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(40), CornerRadius = new CornerRadius(18), Background = Ui.Brush("#C00E0E10"), BorderBrush = Ui.Brush("#1F1F23"), BorderThickness = new Thickness(1) } }
        };
        AttachedToVisualTree += (_, _) => (string.IsNullOrWhiteSpace(_server.Text) ? _server : _user).Focus();
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

    private bool _busy;
    private async Task ConnectAsync()
    {
        if (_busy) return; _busy = true;
        _connect.IsEnabled = false; _error.IsVisible = false;
        var label = ((StackPanel)_connect.Content!).Children.OfType<TextBlock>().Single();
        JellyfinClient? client = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_user.Text)) throw new ArgumentException("Indique ton nom d’utilisateur Jellyfin.");
            // Find the server before sending the password.
            label.Text = "Recherche du serveur…";
            var server = await ServerAddress.DiscoverAsync(_server.Text ?? "");
            _server.Text = server.Address; label.Text = "Connexion…";
            JellyfinClient.DeviceName = "Mac";
            client = new JellyfinClient(new(server.Address, "", _user.Text.Trim(), "", _shell.Profile.DeviceId));
            var connection = await client.LoginAsync(_user.Text.Trim(), _password.Text ?? "");
            _shell.Profile.SaveConnection(connection); _password.Text = "";
            await _shell.ActivateAsync(connection);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { ShowError(Errors.Friendly(ex)); }
        finally { client?.Dispose(); _connect.IsEnabled = true; label.Text = "Se connecter"; _busy = false; }
    }
}
