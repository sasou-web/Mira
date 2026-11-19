using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.Tailscale;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Le foyer » on Windows (ogsas, 2026-11-19: maquette A « Le hall », with the quick switch of C): a PC joins a home
/// with the home's password, found on this network or opened from a home link (mira://home#…, whose key brings the
/// home's own Tailscale node in, <see cref="HomeNode"/>), then « Who's watching? » at each opening of Mira, a profile's
/// PIN checked by the extension, and the profiles again from the rail's picture (Ctrl+P). Each profile is a real
/// Jellyfin session of its account, opened by the extension (/Mira/home). Never an administrator's account.
/// </summary>
public partial class MainWindow
{
    /// <summary>This PC in a home: read at the start, saved as it changes; null in none.</summary>
    private HomeMembership? _home;
    private HomeNode? _homeNode;
    /// <summary>What the join card is joining: the link opened, or the server found on this network.</summary>
    private HomeLink? _homeLink;
    private (string Server, string Name, string? ServerId)? _homeFound;
    /// <summary>The server found on this network has a home, and the person chose their own account instead: not offered again.</summary>
    private bool _homeDeclined;
    private bool _familyBusy;
    private HomeProfile? _pinProfile;
    /// <summary>The PIN asked from the rail's menu, over the library: « Back » goes back to it, not to the hall.</summary>
    private bool _pinFromMenu;
    private Popup? _familyMenu;
    private bool _familyAttached;
    /// <summary>A profile picture by « user id/tag », read once per opening of Mira.</summary>
    private readonly Dictionary<string, ImageBrush?> _familyFaces = [];

    private const string FamilyNodePrefix = "mira-home-";

    /// <summary>The home's own node of this profile, made at the first need.</summary>
    private HomeNode FamilyNode => _homeNode ??= new HomeNode(new()
    {
        Locate = () => TailnetExecutable.Locate(_profile.DirectoryPath), Directory = _profile.HomeNodeDirectory, StateKey = _profile.HomeNodeKey,
        Forget = _profile.ForgetHomeNode, AppVersion = JellyfinClient.AppVersion, Started = ChildProcesses.BindToMira,
#if MIRA_VALIDATION
        ControlUrl = TailnetControl,
#endif
    });

    private void AttachFamily()
    {
        if (_familyAttached) return;
        _familyAttached = true;
        PasswordReveal.Attach(FamilyPassword, FamilyPasswordPlain, FamilyPasswordEye, FamilyPasswordEyeIcon, FamilyPasswordCaps);
        FamilyHallHeading.Text = HomeText.WhoIsWatching;
        FamilyLeaveText.Text = HomeText.Leave;
        FamilyOwnAccount.Content = FamilyOwnAccountHall.Content = HomeText.OwnAccount;
        FamilyPinNote.Text = HomeText.PinNote;
        FamilyJoinNote.Text = HomeText.Next;
        FamilyPinBoxes.Children.Clear();
        for (var i = 0; i < 4; i++)
            FamilyPinBoxes.Children.Add(new Squircle { Width = 54, Height = 62, Margin = new Thickness(i == 0 ? 0 : 12, 0, 0, 0), CornerRadius = new CornerRadius(14), Background = Palette.Get("Field"), BorderThickness = new Thickness(1.5), BorderBrush = Palette.Get("Line"), Child = new TextBlock { FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
    }

    // ---- Opening Mira, and the hall ----

    /// <summary>At Mira's start, in a home: « Who's watching? », its profiles as last read, then as the home says now.</summary>
    private bool OpenHomeAtStart()
    {
        HomeMembership? home;
        try { home = _profile.LoadHome(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { home = null; }
        if (home is null) return false;
        _home = home;
        ShowHall();
        _ = RefreshHallAsync();
        return true;
    }

    /// <summary>The rail's picture: in a home, its profiles (C « Le raccourci »); else the account page as always.</summary>
    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (!OpenFamilyMenu()) Account_Click(sender, e);
    }

    /// <summary>Ctrl+P: the profiles of the home, from anywhere the rail can be reached.</summary>
    private bool FamilyShortcut()
    {
        if (_home is null || FamilyOverlay.Visibility == Visibility.Visible || LoginOverlay.Visibility == Visibility.Visible || ConfirmOverlay.Visibility == Visibility.Visible) return false;
        return OpenFamilyMenu();
    }

    private void ShowFamilyPage(FrameworkElement page)
    {
        AttachFamily();
        foreach (var other in new FrameworkElement[] { FamilyJoin, FamilyHall, FamilyPin }) other.Visibility = other == page ? Visibility.Visible : Visibility.Collapsed;
        if (FamilyOverlay.Visibility != Visibility.Visible) { FamilyOverlay.Visibility = Visibility.Visible; Motion.Reveal(FamilyOverlay, 220, 0); }
        else Motion.Reveal(page, 200, 6);
    }

    private void ShowHall(string? said = null)
    {
        if (_home is not { } home) return;
        _pinProfile = null;
        FamilyHallHome.Text = HomeText.HomeOf(home.Name);
        FamilyHallStatus.Text = said ?? (home.Profiles.Count == 0 ? HomeText.NoProfiles : "");
        RenderTiles(home);
        ShowFamilyPage(FamilyHall);
        Dispatcher.BeginInvoke(() => (FamilyTiles.Children.OfType<Button>().FirstOrDefault(x => (x.Tag as HomeProfile)?.Id == home.Current) ?? FamilyTiles.Children.OfType<Button>().FirstOrDefault())?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RenderTiles(HomeMembership home)
    {
        FamilyTiles.Children.Clear();
        foreach (var profile in home.Profiles) FamilyTiles.Children.Add(ProfileTile(home, profile));
    }

    /// <summary>A profile's tile: its picture or its letter, its name, a lock when it has a PIN; a ring while focused.</summary>
    private Button ProfileTile(HomeMembership home, HomeProfile profile)
    {
        var face = ProfileFace(home, profile, 150, 64);
        var ring = new Squircle { Width = 164, Height = 164, CornerRadius = new CornerRadius(42), BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, Padding = new Thickness(4), Child = face };
        var name = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        name.Children.Add(new TextBlock { Text = profile.Name, FontSize = 19, FontWeight = FontWeights.SemiBold, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Palette.Get("TextBody") });
        if (profile.Pin) name.Children.Add(new Icon { Kind = "lock", Width = 15, Height = 15, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get("TextSecondary") });
        var stack = new StackPanel { Width = 180 };
        stack.Children.Add(ring); stack.Children.Add(name);
        stack.Children.Add(new TextBlock { Text = profile.Id == home.Current ? HomeText.ThisDevice : " ", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Palette.Get("Faint"), Margin = new Thickness(0, 4, 0, 0) });
        var button = new Button { Content = stack, Tag = profile, Padding = new Thickness(0), Margin = new Thickness(14, 0, 14, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FocusVisualStyle = null, Cursor = Cursors.Hand };
        button.Template = BareButton();
        AutomationProperties.SetName(button, profile.Pin ? L($"{profile.Name}, avec un code", $"{profile.Name}, with a PIN") : profile.Name);
        void Lit() { var on = button.IsKeyboardFocused || button.IsMouseOver; ring.BorderBrush = on ? Palette.Get("Text") : Brushes.Transparent; ((TextBlock)name.Children[0]).Foreground = Palette.Get(on ? "Text" : "TextBody"); }
        button.GotKeyboardFocus += (_, _) => Lit(); button.LostKeyboardFocus += (_, _) => Lit(); button.MouseEnter += (_, _) => Lit(); button.MouseLeave += (_, _) => Lit();
        button.Click += (_, _) => _ = ChooseProfileAsync(profile);
        return button;
    }

    private static ControlTemplate? _bareButton;
    /// <summary>A button drawn by its content alone (the tiles' ring says focus and hover).</summary>
    private static ControlTemplate BareButton()
    {
        if (_bareButton is not null) return _bareButton;
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        root.AppendChild(presenter);
        _bareButton = new ControlTemplate(typeof(Button)) { VisualTree = root };
        _bareButton.Seal();
        return _bareButton;
    }

    /// <summary>A profile's face: its Jellyfin picture when it has one (read without a session), else its letter.</summary>
    private Squircle ProfileFace(HomeMembership home, HomeProfile profile, double size, double letter)
    {
        var face = new Squircle { Width = size, Height = size, CornerRadius = new CornerRadius(size * .24), Background = Palette.Get("Raised2"), IsHitTestVisible = false };
        var text = new TextBlock { Text = profile.Name.Length > 0 ? profile.Name[..1].ToUpperInvariant() : "?", FontSize = letter, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Text"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        face.Child = text;
        if (profile.ImageTag is { Length: > 0 } tag)
        {
            var key = profile.Id + "/" + tag;
            if (_familyFaces.TryGetValue(key, out var known)) { if (known is not null) { face.Background = known; text.Visibility = Visibility.Collapsed; } }
            else _ = LoadFaceAsync();
            async Task LoadFaceAsync()
            {
                byte[]? bytes;
                using (var client = new JellyfinClient(new(home.Server, "", "", "", _profile.DeviceId))) bytes = await client.ProfilePictureAsync(profile.Id, tag, 320);
                ImageBrush? brush = null;
                if (bytes is { Length: > 0 })
                    try { brush = new ImageBrush(PictureBytes.Decode(bytes, 320)) { Stretch = Stretch.UniformToFill }; brush.Freeze(); }
                    catch (Exception ex) when (ex is NotSupportedException or System.IO.FileFormatException or System.IO.IOException or InvalidOperationException) { brush = null; }
                _familyFaces[key] = brush;
                if (brush is null) return;
                face.Background = brush; text.Visibility = Visibility.Collapsed;
            }
        }
        return face;
    }

    /// <summary>The home's profiles as it says now; the hall says why not when it cannot (the home's node, the network, out of the home).</summary>
    private async Task RefreshHallAsync()
    {
        if (_home is not { } home) return;
        FamilyRetry.Visibility = Visibility.Collapsed;
        if (!await ReachHomeAsync(home, null)) return;
        using var client = HomeClient(home.Server);
        try
        {
            var now = await client.HomeProfilesAsync(home.Token);
            if (!ReferenceEquals(_home, home)) return;
            _home = home with { Profiles = now.Profiles, Current = now.Current, DeviceId = now.DeviceId };
            SaveHome();
            if (FamilyHall.Visibility == Visibility.Visible && FamilyOverlay.Visibility == Visibility.Visible) { RenderTiles(_home); FamilyHallStatus.Text = _home.Profiles.Count == 0 ? HomeText.NoProfiles : ""; }
        }
        catch (HomeRefusedException ex) when (ex.Kind is HomeRefusal.OutOfHome) { AskHomePasswordAgain(home, ex.Message); }
        catch (HomeRefusedException ex) when (ex.Kind is HomeRefusal.NoHome) { HallSays(ex.Message, retry: false); }
        catch (Exception ex) when (IsExpected(ex)) { HallSays(HomeText.Unreachable(home.Name), retry: true); }
    }

    private void HallSays(string text, bool retry)
    {
        if (FamilyHall.Visibility != Visibility.Visible) return;
        FamilyHallStatus.Text = text; FamilyRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void FamilyRetry_Click(object sender, RoutedEventArgs e) => await RefreshHallAsync();

    /// <summary>
    /// The home's Jellyfin reachable: through the home's node when it was joined from a link (its key on the first time,
    /// then its saved state). False, said on the page shown, when the node cannot take this PC there.
    /// </summary>
    private async Task<bool> ReachHomeAsync(HomeMembership home, HomeLink? link)
    {
        if (!home.ThroughTailnet && link is null) return true;
        if (!Uri.TryCreate(home.Server, UriKind.Absolute, out var address)) return false;
        if (FamilyNode.Node is null) Busy(HomeText.Reaching(home.Name));
        string? code;
        try { code = await FamilyNode.StartAsync(address.Host, link?.Key); }
        finally { Busy(null); }
        if (code is null) return true;
        // Without a key, signed out means the home's administrator took this device off their network.
        var said = code == TailnetCodes.NeedsLogin && link is null ? HomeText.Removed(home.Name) : HomeText.Node(code, home.Name);
        if (FamilyJoin.Visibility == Visibility.Visible) FamilyJoinError.Text = said;
        else HallSays(said, retry: code != TailnetCodes.NeedsLogin);
        return false;
    }

    private JellyfinClient HomeClient(string server) => new(new(server, "", "", "", _profile.DeviceId));

    private void SaveHome()
    {
        if (_home is null) return;
        try { _profile.SaveHome(_home); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { AddNotice(SettingsNotSaved); }
    }

    private void Busy(string? text)
    {
        FamilyBusyText.Text = text ?? "";
        FamilyBusy.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- A profile chosen ----

    /// <summary>
    /// A tile, or a profile of the rail's menu: the profile open on this PC goes on with its session; another one asks
    /// for its PIN when it has one, then the extension opens its session.
    /// </summary>
    private async Task ChooseProfileAsync(HomeProfile profile)
    {
        if (_familyBusy || _home is not { } home) return;
        CloseFamilyMenu();
        if (profile.Id == home.Current && _profile.LoadConnection() is { } saved && saved.UserId == profile.Id && saved.Server == home.Server)
        {
            // Away from home, the profile's Jellyfin is only reached through the home's node.
            if (_client is null && !await ReachHomeAsync(home, null)) return;
            await HideFamilyAsync();
            if (_client?.Connection.UserId != saved.UserId || _client?.Connection.Server != saved.Server) await ActivateConnectionAsync(saved);
            return;
        }
        if (profile.Pin) { _pinFromMenu = FamilyOverlay.Visibility != Visibility.Visible && _client is not null; AskPin(profile); return; }
        await OpenProfileAsync(profile, null);
    }

    private async Task OpenProfileAsync(HomeProfile profile, string? pin)
    {
        if (_home is not { } home || _familyBusy) return;
        _familyBusy = true; FamilyPinError.Text = "";
        Busy(L($"Ouverture du profil {profile.Name}…", $"Opening {profile.Name}’s profile…"));
        try
        {
            if (!await ReachHomeAsync(home, null)) { if (FamilyOverlay.Visibility != Visibility.Visible) { ShowHall(); await RefreshHallAsync(); } return; }
            using var client = HomeClient(home.Server);
            var connection = await client.HomeOpenAsync(home.Token, profile.Id, pin);
            if (!ReferenceEquals(_home, home)) return;
            _home = home with { Current = profile.Id, DeviceId = connection.DeviceId };
            SaveHome();
            var previous = _profile.LoadConnection();
            _profile.SaveConnection(connection);
            await HideFamilyAsync();
            await ActivateConnectionAsync(connection);
            // Signed in with an account of its own before: that session ends on its Jellyfin, nothing here could revoke it later.
            if (previous is not null && !previous.DeviceId.StartsWith(FamilyNodePrefix, StringComparison.Ordinal) && (previous.Server != connection.Server || previous.UserId != connection.UserId)) _ = EndReplacedSessionAsync(previous);
        }
        catch (HomeRefusedException ex)
        {
            switch (ex.Kind)
            {
                case HomeRefusal.PinAsked: AskPin(profile); break;
                case HomeRefusal.PinWrong: AskPin(profile, HomeText.PinLeft(ex.Left ?? 0)); break;
                case HomeRefusal.PinWait: AskPin(profile, HomeText.PinWait(ex.Wait ?? 300)); break;
                case HomeRefusal.OutOfHome: AskHomePasswordAgain(home, ex.Message); break;
                default: FamilyFailed(ex.Message); await RefreshHallAsync(); break;
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { FamilyFailed(HomeText.Unreachable(home.Name)); }
        finally { _familyBusy = false; Busy(null); }
    }

    /// <summary>A failure said where the person is: on the PIN page, the hall, or as a notice from the rail's menu.</summary>
    private void FamilyFailed(string text)
    {
        if (FamilyOverlay.Visibility != Visibility.Visible) { SetNotice(text); return; }
        if (FamilyPin.Visibility == Visibility.Visible) { FamilyPinError.Text = text; FamilyPinBox.Clear(); return; }
        HallSays(text, retry: true);
    }

    private async Task HideFamilyAsync()
    {
        if (FamilyOverlay.Visibility != Visibility.Visible) return;
        await Motion.HideAsync(FamilyOverlay);
        FamilyPassword.Clear(); FamilyPinBox.Clear();
    }

    // ---- The PIN ----

    private void AskPin(HomeProfile profile, string? error = null)
    {
        if (_home is not { } home) return;
        _pinProfile = profile;
        FamilyPinFace.Children.Clear();
        FamilyPinFace.Children.Add(ProfileFace(home, profile, 96, 42));
        FamilyPinHeading.Text = HomeText.PinOf(profile.Name);
        FamilyPinForgot.Text = L($"Oublié ? Demande à l’administrateur de {home.Name}.", $"Forgot it? Ask {home.Name}’s administrator.");
        FamilyPinError.Text = error ?? "";
        FamilyPinBox.Clear(); RenderPin();
        if (FamilyPin.Visibility != Visibility.Visible || FamilyOverlay.Visibility != Visibility.Visible) ShowFamilyPage(FamilyPin);
        Dispatcher.BeginInvoke(() => FamilyPinBox.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FamilyPin_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    /// <summary>A pasted PIN goes through <see cref="FamilyPin_TextChanged"/>, which keeps its four digits; cut and copy have nothing to show.</summary>
    private void FamilyPin_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut) e.Handled = true;
    }

    private void FamilyPin_TextChanged(object sender, TextChangedEventArgs e)
    {
        var digits = new string(FamilyPinBox.Text.Where(char.IsAsciiDigit).Take(4).ToArray());
        if (digits != FamilyPinBox.Text) { FamilyPinBox.Text = digits; FamilyPinBox.CaretIndex = digits.Length; return; }
        RenderPin();
        if (digits.Length == 4 && _pinProfile is { } profile) _ = OpenProfileAsync(profile, digits);
    }

    private void RenderPin()
    {
        var typed = FamilyPinBox.Text.Length;
        for (var i = 0; i < FamilyPinBoxes.Children.Count; i++)
            if (FamilyPinBoxes.Children[i] is Squircle { Child: TextBlock dot } box)
            {
                dot.Text = i < typed ? "•" : "";
                box.BorderBrush = Palette.Get(i == Math.Min(typed, 3) && FamilyPinBox.IsKeyboardFocused ? "FieldFocus" : "Line");
            }
    }

    private void FamilyPinBack_Click(object sender, RoutedEventArgs e) => PinBack();
    private void PinBack()
    {
        _pinProfile = null; FamilyPinBox.Clear();
        // Opened from the rail's menu, over the library: back to it.
        if (_pinFromMenu && _client is not null) { _pinFromMenu = false; _ = HideFamilyAsync(); return; }
        ShowHall();
    }

    private void FamilyOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (FamilyPin.Visibility == Visibility.Visible) { PinBack(); e.Handled = true; return; }
        // The hall over the library (opened from the rail): Escape goes back to what was playing.
        if (FamilyHall.Visibility == Visibility.Visible && _client is not null) { _ = HideFamilyAsync(); e.Handled = true; return; }
        if (FamilyJoin.Visibility == Visibility.Visible) { FamilyOwnAccount_Click(this, new()); e.Handled = true; }
    }

    // ---- Joining ----

    /// <summary>A home link opened in Mira (mira://home#…, from its page): the home's card, its password asked.</summary>
    private void OpenHomeLink(string text)
    {
        if (HomeLink.Read(text) is not { } link) { SetNotice(HomeText.LinkBroken); return; }
        RestoreFromWindows();
        // Already in this home: its hall.
        if (_home is { } home && link.Is(home.ServerId)) { ShowHall(); _ = RefreshHallAsync(); return; }
        _homeLink = link; _homeFound = null;
        ShowJoin(HomeText.Join(link.Name), "link", HomeText.FromLink(link.Name), HomeText.FromLinkNote, link.Expired(DateTimeOffset.UtcNow) ? HomeText.LinkExpired(link.Name) : null);
    }

    /// <summary>
    /// The sign-in found a server on this network that has a home, for a PC with no account yet: its password, no
    /// address to type (« Use an address or my own account » goes back to the form).
    /// </summary>
    private async Task OfferHomeAsync(ServerSearch.Found found)
    {
        if (_homeDeclined || _home is not null || _homeLink is not null || _client is not null || _demo || _profile.LoadConnection() is not null) return;
        bool? on;
        using (var client = HomeClient(found.Address))
            try { on = await client.HomeOnAsync(); }
            catch (Exception ex) when (IsExpected(ex)) { on = null; }
        if (on != true || LoginOverlay.Visibility != Visibility.Visible || _client is not null || ServerBox.Text != found.Address) return;
        _homeFound = (found.Address, found.Name, found.Id);
        ShowJoin(L("Se connecter", "Sign in"), "wifi", HomeText.HomeOf(found.Name), HomeText.FoundHere, null, ownAccount: L("Une adresse ou mon propre compte", "Use an address or my own account"));
    }

    private void ShowJoin(string heading, string icon, string source, string note, string? error, string? ownAccount = null)
    {
        AttachFamily();
        FamilyJoinHeading.Text = heading;
        FamilySourceIcon.Kind = icon; FamilySourceTitle.Text = source; FamilySourceNote.Text = note;
        FamilyJoinError.Text = error ?? "";
        FamilyJoinText.Text = L("Continuer", "Continue");
        FamilyOwnAccount.Content = ownAccount ?? HomeText.OwnAccount;
        FamilyJoinButton.IsEnabled = error is null || _homeLink is null || !_homeLink.Expired(DateTimeOffset.UtcNow);
        FamilyPassword.Clear();
        ShowFamilyPage(FamilyJoin);
        Dispatcher.BeginInvoke(() => FamilyPassword.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Out of the home (removed, its password changed): the password again, where it was.</summary>
    private void AskHomePasswordAgain(HomeMembership home, string said)
    {
        _homeLink = null;
        _homeFound = (home.Server, home.Name, home.ServerId);
        ShowJoin(HomeText.Join(home.Name), home.ThroughTailnet ? "link" : "wifi", HomeText.HomeOf(home.Name), home.ThroughTailnet ? HomeText.FromLinkNote : HomeText.FoundHere, said);
    }

    private void FamilyPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !FamilyJoinButton.IsEnabled) return;
        e.Handled = true; FamilyJoin_Click(FamilyJoinButton, new());
    }

    private async void FamilyJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_familyBusy) return;
        var password = FamilyPassword.Password;
        if (password.Length == 0) { FamilyJoinError.Text = L("Tape le mot de passe du foyer.", "Type the home’s password."); return; }
        var link = _homeLink;
        var (server, name, serverId) = link is not null ? (link.Address, link.Name, link.ServerId) : _homeFound ?? default;
        if (server is null) return;
        if (link?.Expired(DateTimeOffset.UtcNow) == true && _home?.ThroughTailnet != true) { FamilyJoinError.Text = HomeText.LinkExpired(link.Name); return; }
        _familyBusy = true; FamilyJoinButton.IsEnabled = false; FamilyJoinError.Text = "";
        FamilyJoinText.Text = L("Connexion…", "Signing in…");
        try
        {
            var through = link is not null || _home?.ThroughTailnet == true && _home.Server == server;
            var trial = new HomeMembership { Server = server, Name = name, Token = "", DeviceId = "", ServerId = serverId, ThroughTailnet = through };
            if (through && !await ReachHomeAsync(trial, link)) return;
            using var client = HomeClient(server);
            // The Jellyfin the link names, and no other: its own id first.
            var id = await client.LearnServerIdAsync();
            if (serverId is not null && !string.Equals(id?.Replace("-", ""), serverId.Replace("-", ""), StringComparison.OrdinalIgnoreCase)) { FamilyJoinError.Text = HomeText.NotThisServer(name); return; }
            var joined = await client.HomeJoinAsync(password, _profile.DeviceId, HomeText.DeviceName("Mira", Environment.MachineName));
            _home = trial with { Server = client.Connection.Server, ServerId = id ?? serverId, Token = joined.Token, DeviceId = joined.DeviceId, Profiles = joined.Profiles, Current = null };
            _homeLink = null; _homeFound = null;
            SaveHome();
            if (LoginOverlay.Visibility == Visibility.Visible) LoginOverlay.Visibility = Visibility.Collapsed;
            ShowHall();
        }
        catch (HomeRefusedException ex) { FamilyJoinError.Text = ex.Message; }
        catch (Exception ex) when (IsExpected(ex)) { FamilyJoinError.Text = HomeText.Unreachable(name); }
        finally { _familyBusy = false; FamilyJoinButton.IsEnabled = true; FamilyJoinText.Text = L("Continuer", "Continue"); }
    }

    /// <summary>« I have my own account »: the usual sign-in, the home left aside (a home of this PC stays).</summary>
    private void FamilyOwnAccount_Click(object sender, RoutedEventArgs e)
    {
        _homeDeclined = true; _homeLink = null;
        FamilyOverlay.Visibility = Visibility.Collapsed;
        if (_client is not null) { Account_Click(this, new()); return; }
        if (LoginOverlay.Visibility != Visibility.Visible) Motion.Reveal(LoginOverlay);
        ShowSignInForm(); FocusLogin();
    }

    // ---- Leaving ----

    private async void FamilyLeave_Click(object sender, RoutedEventArgs e) => await LeaveHomeAsync();

    /// <summary>
    /// « Leave this home »: asked first (coming back takes the password, and a new link away from home); the extension
    /// told, the home's node off the tailnet and its state gone, the profile's session closed here.
    /// </summary>
    private async Task LeaveHomeAsync()
    {
        if (_home is not { } home || _familyBusy) return;
        CloseFamilyMenu();
        if (!await ConfirmAsync(HomeText.Leave + " ?", home.ThroughTailnet
                ? L($"Pour revenir, il faudra le mot de passe du foyer et un nouveau lien de {home.Name}.", $"To come back, you’ll need the home’s password and a new link from {home.Name}.")
                : L("Pour revenir, il faudra le mot de passe du foyer.", "To come back, you’ll need the home’s password."), HomeText.Leave, L("Annuler", "Cancel"))) return;
        _familyBusy = true;
        try
        {
            var told = false;
            if (await ReachHomeAsync(home, null).WaitAsync(TimeSpan.FromSeconds(20)).ContinueWith(t => t.IsCompletedSuccessfully && t.Result))
                using (var client = HomeClient(home.Server)) told = await client.HomeLeaveAsync(home.Token);
            if (home.ThroughTailnet) await FamilyNode.LeaveAsync();
            _home = null; _familyFaces.Clear();
            try { _profile.ClearHome(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
            // The profile's session was the home's: it ended with the device on the server.
            if (_profile.LoadConnection() is { } saved && saved.DeviceId.StartsWith(FamilyNodePrefix, StringComparison.Ordinal))
            {
                try { _profile.ClearConnection(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                await DisconnectServicesAsync();
            }
            FamilyOverlay.Visibility = Visibility.Collapsed;
            if (_client is null) { LoginOverlay.Visibility = Visibility.Visible; ShowSignInForm(); FocusLogin(); }
            SetNotice(told ? HomeText.Left(home.Name) : HomeText.LeftHere(home.Name));
        }
        finally { _familyBusy = false; }
    }

    // ---- The rail's picture: the quick switch (C) ----

    /// <summary>In a home, the rail's picture (or Ctrl+P) opens the profiles over what shows, rather than the sign-in.</summary>
    private bool OpenFamilyMenu()
    {
        if (_home is not { } home || _demo) return false;
        if (_familyMenu is { IsOpen: true }) { CloseFamilyMenu(); return true; }
        var body = new Squircle { Style = (Style)FindResource("PopoverWindow"), Width = 300, Padding = new Thickness(8) };
        TextElement.SetFontFamily(body, (FontFamily)FindResource("AppFont")); TextElement.SetForeground(body, Palette.Get("Text"));
        var list = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var keys = new TextBlock { Text = "Ctrl + P", FontSize = 11, Foreground = Palette.Get("Faint") };
        DockPanel.SetDock(keys, Dock.Right); head.Children.Add(keys);
        head.Children.Add(new TextBlock { Text = HomeText.HomeOf(home.Name).ToUpper(Lang.Culture), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis });
        list.Children.Add(head);
        foreach (var profile in home.Profiles)
        {
            var row = new DockPanel();
            var face = ProfileFace(home, profile, 34, 15); face.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(face, Dock.Left); row.Children.Add(face);
            var current = profile.Id == home.Current && _client?.Connection.UserId == profile.Id;
            if (current || profile.Pin)
            {
                var mark = new Icon { Kind = current ? "check" : "lock", Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get("TextSecondary") };
                DockPanel.SetDock(mark, Dock.Right); row.Children.Add(mark);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = profile.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (current) text.Children.Add(new TextBlock { Text = L("En ce moment sur ce PC", "Watching now on this PC"), FontSize = 11.5, Foreground = Palette.Get("Muted") });
            row.Children.Add(text);
            list.Children.Add(MenuRow(row, profile.Name, () => { if (!current) _ = ChooseProfileAsync(profile); else CloseFamilyMenu(); }));
        }
        list.Children.Add(new Border { Height = 1, Background = Palette.Get("Line"), Margin = new Thickness(8, 6, 8, 6) });
        list.Children.Add(MenuRow(MenuLine("user", HomeText.OwnAccount), HomeText.OwnAccount, () => { CloseFamilyMenu(); Account_Click(this, new()); }));
        list.Children.Add(MenuRow(MenuLine("exit", HomeText.Leave), HomeText.Leave, () => _ = LeaveHomeAsync()));
        body.Child = list;
        var popup = _familyMenu = new Popup { PlacementTarget = AccountButton, Placement = PlacementMode.Custom, StaysOpen = false, AllowsTransparency = true, Child = body, PopupAnimation = PopupAnimation.None };
        popup.CustomPopupPlacementCallback = (size, target, _) => [new CustomPopupPlacement(new Point(target.Width + 10, target.Height - size.Height), PopupPrimaryAxis.None)];
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseFamilyMenu(); AccountButton.Focus(); e.Handled = true; } };
        popup.Closed += (_, _) => { if (ReferenceEquals(_familyMenu, popup)) _familyMenu = null; };
        popup.IsOpen = true;
        Dispatcher.BeginInvoke(() => list.Children.OfType<Button>().FirstOrDefault()?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        _ = RefreshHallAsync();
        return true;
    }

    private static DockPanel MenuLine(string icon, string label)
    {
        var line = new DockPanel();
        var glyph = new Icon { Kind = icon, Width = 16, Height = 16, Margin = new Thickness(9, 0, 21, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(glyph, Dock.Left); line.Children.Add(glyph);
        line.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        return line;
    }

    private Button MenuRow(FrameworkElement content, string name, Action act)
    {
        var button = new Button { Content = content, Style = (Style)FindResource("Quiet"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 12, 8), Margin = new Thickness(0, 1, 0, 1) };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => act();
        return button;
    }

    private void CloseFamilyMenu() { if (_familyMenu is { IsOpen: true } menu) menu.IsOpen = false; }
}
