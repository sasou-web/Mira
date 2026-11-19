using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mira.Core;
using Mira.Core.Family;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Le foyer » over the window, as on Windows (FamilyOverlay; ogsas, 2026-11-19, maquette A « Le hall »): joining a home
/// with its password (found on this network, or opened from a home link), « Who's watching? » with the home's profiles,
/// and a profile's PIN, four digits checked by the extension. What it does is the window's (MainWindow.Family.cs).
/// </summary>
public sealed class FamilyView : UserControl
{
    public enum Stage { Join, Hall, Pin }

    // —— Joining ——
    private readonly TextBlock _joinHeading = Ui.Heading("", 24, 1, FontWeight.Bold);
    private readonly Icon _sourceIcon = new("link", 16) { Foreground = Ui.Success, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
    private readonly TextBlock _sourceTitle = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sourceNote = new() { FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBox _password = new() { PasswordChar = '•', RevealPassword = false };
    private readonly Button _eye;
    private readonly TextBlock _caps = new() { Text = L("Verr. Maj est activé", "Caps Lock is on"), FontSize = 12, Foreground = Ui.Warning, IsVisible = false };
    private readonly TextBlock _joinError = new() { Foreground = Ui.Danger, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _join;
    private readonly TextBlock _joinText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _ownAccount;
    private readonly TextBlock _ownAccountText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Control _joinPage;

    // —— The hall ——
    private readonly TextBlock _hallHome = new() { FontSize = 14, Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 1, 0, 0) };
    private readonly WrapPanel _tiles = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _hallStatus = new() { FontSize = 13.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 560, Margin = new Thickness(0, 26, 0, 0), IsVisible = false };
    private readonly Button _retry;
    private readonly Control _hallPage;

    // —— A profile's PIN ——
    private readonly Panel _pinFace = new() { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _pinHeading = Ui.Heading("", 28, 1, FontWeight.Bold);
    private readonly StackPanel _pinBoxes = new() { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBox _pin = new() { MaxLength = 4, Opacity = 0, Width = 1, Height = 1 };
    private readonly TextBlock _pinError = new() { Foreground = Ui.Danger, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, IsVisible = false };
    private readonly TextBlock _pinForgot = new() { FontSize = 13, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Control _pinPage;

    private readonly StackPanel _busy;
    private readonly TextBlock _busyText = new() { FontSize = 13, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly MainWindow _shell;

    /// <summary>A tile chosen (a click, Return).</summary>
    public event Action<HomeProfile>? Chosen;
    /// <summary>Four digits typed.</summary>
    public event Action<string>? PinTyped;
    public event Action? Join, OwnAccount, Leave, Retry, PinBack, Escape;

    public FamilyView(MainWindow shell)
    {
        _shell = shell;
        Avalonia.Automation.AutomationProperties.SetName(_password, HomeText.Password);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_joinError, Avalonia.Automation.AutomationLiveSetting.Assertive);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_pinError, Avalonia.Automation.AutomationLiveSetting.Assertive);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_hallStatus, Avalonia.Automation.AutomationLiveSetting.Polite);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_busyText, Avalonia.Automation.AutomationLiveSetting.Polite);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_caps, Avalonia.Automation.AutomationLiveSetting.Polite);

        // —— Joining: the home's card, its password, « Continue » ——
        _eye = Ui.IconButton("eye", L("Afficher le mot de passe", "Show password"), RevealPassword, "icon", 18);
        _eye.Width = _eye.Height = 32; _eye.Margin = new Thickness(0, 0, 5, 0); _eye.Foreground = Ui.TextTertiary;
        _password.InnerRightContent = _eye;
        _password.GotFocus += (_, _) => ShowCaps(); _password.LostFocus += (_, _) => _caps.IsVisible = false;
        _password.AddHandler(KeyDownEvent, (_, _) => ShowCaps(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _password.AddHandler(KeyUpEvent, (_, _) => ShowCaps(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _join = new Button { Content = _joinText, Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(18, 13) };
        _join.Click += (_, _) => Join?.Invoke();
        _password.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; if (_join.IsEnabled) Join?.Invoke(); } };
        _ownAccount = new Button { Content = _ownAccountText, Classes = { "quiet" }, Padding = new Thickness(12, 8), HorizontalAlignment = HorizontalAlignment.Center };
        _ownAccount.Click += (_, _) => OwnAccount?.Invoke();
        var brand = new Icon("brand", 30) { Foreground = Ui.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var head = new DockPanel();
        DockPanel.SetDock(brand, Dock.Left); head.Children.Add(brand);
        _joinHeading.TextWrapping = TextWrapping.Wrap; _joinHeading.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_joinHeading);
        var source = new DockPanel();
        DockPanel.SetDock(_sourceIcon, Dock.Left); source.Children.Add(_sourceIcon);
        source.Children.Add(Ui.Column(0, _sourceTitle, _sourceNote));
        var sourceBox = new Border
        {
            Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(10), Background = Ui.Surface, BorderBrush = Ui.MenuLine, BorderThickness = new Thickness(1), Child = source,
        };
        var card = Ui.Column(14, head, sourceBox,
            Ui.Column(7, new TextBlock { Text = HomeText.Password, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextTertiary, Margin = new Thickness(0, 6, 0, 0) }, _password),
            _caps, _joinError, _join, _ownAccount,
            Ui.Text(HomeText.Next, 12.5, color: Ui.Faint));
        card.Width = 420;
        _joinPage = Centered(new Border { Child = card, Padding = new Thickness(36, 32), CornerRadius = new CornerRadius(18), Background = Ui.Surface, BorderBrush = Ui.MenuLine, BorderThickness = new Thickness(1) });

        // —— The hall: « Who's watching? », the tiles, then what can be done besides ——
        _retry = Ui.Action(L("Réessayer", "Try again"), "refresh", "chip", () => Retry?.Invoke());
        _retry.IsVisible = false;
        var own = Ui.Action(HomeText.OwnAccount, null, "quiet", () => OwnAccount?.Invoke());
        var leave = Ui.Action(HomeText.Leave, "exit", "quiet", () => Leave?.Invoke());
        own.Padding = leave.Padding = new Thickness(14, 9);
        var hallHead = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { new Icon("brand", 22) { Foreground = Ui.Ink }, _hallHome } };
        var hallTitle = Ui.Heading(HomeText.WhoIsWatching, 44, 1, FontWeight.Bold);
        hallTitle.HorizontalAlignment = HorizontalAlignment.Center; hallTitle.TextWrapping = TextWrapping.NoWrap; hallTitle.Margin = new Thickness(0, 16, 0, 40);
        var hallButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0), Children = { _retry, own, leave } };
        _tiles.AddHandler(KeyDownEvent, TileKeys, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _hallPage = Centered(new StackPanel { Children = { hallHead, hallTitle, _tiles, _hallStatus, hallButtons } }, card: false);

        // —— A PIN: four boxes, one hidden field takes the digits typed anywhere on the page ——
        for (var i = 0; i < 4; i++)
            _pinBoxes.Children.Add(new Border
            {
                Width = 54, Height = 62, CornerRadius = new CornerRadius(14), Background = Ui.Raised, BorderBrush = Ui.LineStrong, BorderThickness = new Thickness(1.5),
                Child = new TextBlock { FontSize = 26, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        _pin.TextChanged += (_, _) => PinChanged();
        _pin.GotFocus += (_, _) => RenderPin(); _pin.LostFocus += (_, _) => RenderPin();
        _pin.AddHandler(TextInputEvent, (_, e) => { if (e.Text is { } text && !text.All(char.IsAsciiDigit)) e.Handled = true; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _pinHeading.HorizontalAlignment = HorizontalAlignment.Center; _pinHeading.Margin = new Thickness(0, 18, 0, 24); _pinHeading.TextAlignment = TextAlignment.Center;
        var back = Ui.Action(L("Retour", "Back"), "back", "chip", () => PinBack?.Invoke());
        var pinFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0), Children = { back, _pinForgot } };
        var boxes = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { _pinBoxes, _pin } };
        var pinStack = new StackPanel
        {
            Width = 460,
            Children = { _pinFace, _pinHeading, boxes, Ui.Text(HomeText.PinNote, 12.5, color: Ui.Faint), _pinError, pinFoot },
        };
        foreach (var text in pinStack.Children.OfType<TextBlock>().Where(x => x != _pinHeading)) { text.TextAlignment = TextAlignment.Center; text.Margin = new Thickness(0, 14, 0, 0); }
        _pinPage = Centered(pinStack, card: false);
        // A click anywhere on the PIN page gives the digits back to its field.
        _pinPage.PointerPressed += (_, _) => Dispatcher.UIThread.Post(() => _pin.Focus());

        _busy = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 48), IsVisible = false, IsHitTestVisible = false, Children = { new Icon("refresh", 15) { Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center }, _busyText } };
        Content = new Grid
        {
            Background = Brushes.Black,
            Children = { _joinPage, _hallPage, _pinPage, _busy },
        };
        Show(Stage.Join);
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Escape?.Invoke(); } }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>A page in the middle of the window, scrolled when the window is too small for it.</summary>
    private Control Centered(Control content, bool card = true) => new ScrollViewer
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Content = new Border { Child = content, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 24 + _shell.TitleBarInset, 24, card ? 24 : 40) },
    };

    public Stage Shown { get; private set; }
    public void Show(Stage stage)
    {
        Shown = stage;
        _joinPage.IsVisible = stage == Stage.Join; _hallPage.IsVisible = stage == Stage.Hall; _pinPage.IsVisible = stage == Stage.Pin;
    }

    // —— Joining ——
    public void ShowJoin(string heading, string icon, string title, string note, string? error, string ownAccount, bool canJoin)
    {
        _joinHeading.Text = heading; _sourceIcon.Kind = icon; _sourceTitle.Text = title; _sourceNote.Text = note;
        JoinError(error);
        SetJoinBusy(false); _join.IsEnabled = canJoin;
        _ownAccountText.Text = ownAccount; Avalonia.Automation.AutomationProperties.SetName(_ownAccount, ownAccount);
        _password.Text = ""; _password.RevealPassword = false; ((Icon)_eye.Content!).Kind = "eye";
        Show(Stage.Join);
        Dispatcher.UIThread.Post(() => _password.Focus(), DispatcherPriority.Loaded);
    }
    public string Password { get => _password.Text ?? ""; set => _password.Text = value; }
    public void JoinError(string? text) { _joinError.Text = text; _joinError.IsVisible = !string.IsNullOrEmpty(text); }
    public string? JoinErrorText => _joinError.IsVisible ? _joinError.Text : null;
    public bool CanJoin => _join.IsEnabled;
    public string JoinHeading => _joinHeading.Text ?? "";
    public void SetJoinBusy(bool busy)
    {
        _join.IsEnabled = !busy;
        _joinText.Text = busy ? L("Connexion…", "Signing in…") : L("Continuer", "Continue");
        Avalonia.Automation.AutomationProperties.SetName(_join, _joinText.Text);
    }
    private void RevealPassword()
    {
        var show = !_password.RevealPassword;
        _password.RevealPassword = show;
        ((Icon)_eye.Content!).Kind = show ? "eye-off" : "eye";
        var label = show ? L("Masquer le mot de passe", "Hide password") : L("Afficher le mot de passe", "Show password");
        ToolTip.SetTip(_eye, label); Avalonia.Automation.AutomationProperties.SetName(_eye, label);
        _password.Focus(); _password.CaretIndex = _password.Text?.Length ?? 0;
    }
    private void ShowCaps() => _caps.IsVisible = _password.IsFocused && MacKeyboard.CapsLock;

    // —— The hall ——
    /// <summary>The home's profiles in tiles; <paramref name="current"/> said « On this device ».</summary>
    public void ShowHall(string home, IReadOnlyList<HomeProfile> profiles, string? current, Func<HomeProfile, double, Control> face, string? status, bool retry)
    {
        _hallHome.Text = home;
        _tiles.Children.Clear();
        foreach (var profile in profiles) _tiles.Children.Add(Tile(profile, profile.Id == current, face(profile, 150)));
        HallSays(status, retry);
        var wasHall = Shown == Stage.Hall;
        Show(Stage.Hall);
        if (!wasHall || !_tiles.Children.Any(x => x.IsFocused))
            Dispatcher.UIThread.Post(() => (_tiles.Children.OfType<Button>().FirstOrDefault(x => (x.Tag as HomeProfile)?.Id == current) ?? _tiles.Children.OfType<Button>().FirstOrDefault())?.Focus(), DispatcherPriority.Loaded);
    }
    public void HallSays(string? text, bool retry)
    {
        _hallStatus.Text = text; _hallStatus.IsVisible = !string.IsNullOrEmpty(text);
        _retry.IsVisible = retry;
    }
    public string? HallStatus => _hallStatus.IsVisible ? _hallStatus.Text : null;
    public bool RetryShown => _retry.IsVisible;
    /// <summary>The names on the tiles, in order (the self-check).</summary>
    public IReadOnlyList<string> Tiles => [.. _tiles.Children.OfType<Button>().Select(x => ((HomeProfile)x.Tag!).Name)];
    /// <summary>A tile clicked as the person would (the self-check).</summary>
    public void ClickTile(string name) => _tiles.Children.OfType<Button>().FirstOrDefault(x => ((HomeProfile)x.Tag!).Name == name)?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>A profile's tile: its picture or its letter, its name, a lock when it has a PIN; a ring while focused or under the pointer.</summary>
    private Button Tile(HomeProfile profile, bool current, Control face)
    {
        var ring = new Border { Width = 164, Height = 164, CornerRadius = new CornerRadius(42), BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, Padding = new Thickness(4), Child = face };
        var name = new TextBlock { Text = profile.Name, FontSize = 19, FontWeight = FontWeight.SemiBold, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.TextBody, VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 7, Margin = new Thickness(0, 12, 0, 0), Children = { name } };
        if (profile.Pin) line.Children.Add(new Icon("lock", 15) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        var here = new TextBlock { Text = current ? HomeText.ThisDevice : " ", FontSize = 12, Foreground = Ui.Faint, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var button = new Button
        {
            Content = new StackPanel { Width = 180, Children = { ring, line, here } }, Tag = profile, Padding = new Thickness(0), Margin = new Thickness(14, 0, 14, 12),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand), Classes = { "bare" },
        };
        Avalonia.Automation.AutomationProperties.SetName(button, profile.Pin ? L($"{profile.Name}, avec un code", $"{profile.Name}, with a PIN") : profile.Name);
        if (current) Avalonia.Automation.AutomationProperties.SetHelpText(button, HomeText.ThisDevice);
        void Lit() { var on = button.IsFocused || button.IsPointerOver; ring.BorderBrush = on ? Ui.Ink : Brushes.Transparent; name.Foreground = on ? Ui.Ink : Ui.TextBody; }
        button.GotFocus += (_, _) => Lit(); button.LostFocus += (_, _) => Lit();
        button.PointerEntered += (_, _) => Lit(); button.PointerExited += (_, _) => Lit();
        button.Click += (_, _) => Chosen?.Invoke(profile);
        return button;
    }
    /// <summary>The arrows from tile to tile, as on Windows (couch mode, a controller through them).</summary>
    private void TileKeys(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var tiles = _tiles.Children.OfType<Button>().ToList();
        if (tiles.Count == 0) return;
        var at = tiles.FindIndex(x => x.IsFocused);
        var step = e.Key is Key.Left or Key.Up ? -1 : 1;
        tiles[at < 0 ? 0 : (at + step + tiles.Count) % tiles.Count].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }

    /// <summary>A profile's face: its picture when it has one, else its first letter, on Raised2.</summary>
    public static Border Face(HomeProfile profile, double size, Bitmap? picture)
    {
        var face = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * .24), Background = Ui.Raised2, ClipToBounds = true, IsHitTestVisible = false };
        if (picture is not null) face.Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        else face.Child = new TextBlock { Text = profile.Name.Length > 0 ? profile.Name[..1].ToUpperInvariant() : "?", FontSize = size * .43, FontWeight = FontWeight.SemiBold, Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return face;
    }

    // —— The PIN ——
    public void ShowPin(string heading, Control face, string forgot, string? error)
    {
        _pinFace.Children.Clear(); _pinFace.Children.Add(face);
        _pinHeading.Text = heading; _pinForgot.Text = forgot;
        Avalonia.Automation.AutomationProperties.SetName(_pin, heading);
        PinError(error);
        _pin.Text = ""; RenderPin();
        Show(Stage.Pin);
        Dispatcher.UIThread.Post(() => _pin.Focus(), DispatcherPriority.Loaded);
    }
    public void PinError(string? text) { _pinError.Text = text; _pinError.IsVisible = !string.IsNullOrEmpty(text); if (!string.IsNullOrEmpty(text)) _pin.Text = ""; }
    public string? PinErrorText => _pinError.IsVisible ? _pinError.Text : null;
    public string PinHeading => _pinHeading.Text ?? "";
    /// <summary>Digits typed as the person would (the self-check).</summary>
    public void TypePin(string digits) { _pin.Text = digits; PinChanged(); }
    private string _pinSent = "";
    private void PinChanged()
    {
        var text = _pin.Text ?? "";
        var digits = new string(text.Where(char.IsAsciiDigit).Take(4).ToArray());
        if (digits != text) { _pin.Text = digits; _pin.CaretIndex = digits.Length; return; }
        RenderPin();
        // Said once per four digits: Avalonia may say the same text twice.
        if (digits.Length == 4 && digits != _pinSent) { _pinSent = digits; PinTyped?.Invoke(digits); }
        if (digits.Length < 4) _pinSent = "";
    }
    private void RenderPin()
    {
        var typed = (_pin.Text ?? "").Length;
        for (var i = 0; i < _pinBoxes.Children.Count; i++)
            if (_pinBoxes.Children[i] is Border { Child: TextBlock dot } box)
            {
                dot.Text = i < typed ? "•" : "";
                box.BorderBrush = i == Math.Min(typed, 3) && _pin.IsFocused ? Ui.Ink : Ui.LineStrong;
            }
    }

    // —— What Mira is doing ——
    /// <summary>A button of the page pressed, as a click does (the self-check): join, retry, leave, escape, own.</summary>
    public void PressForCheck(string what)
    {
        switch (what)
        {
            case "join": Join?.Invoke(); break;
            case "retry": Retry?.Invoke(); break;
            case "leave": Leave?.Invoke(); break;
            case "escape": Escape?.Invoke(); break;
            case "own": OwnAccount?.Invoke(); break;
        }
    }
    public void Busy(string? text) { _busyText.Text = text ?? ""; _busy.IsVisible = text is not null; }
    public string? BusyText => _busy.IsVisible ? _busyText.Text : null;
}
