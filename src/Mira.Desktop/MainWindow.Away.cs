using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The connection and the family, easy to find (ogsas, 2026-11-20: « pas intuitif »; maquette A « En tête de liste » with
/// D « Le menu du profil »): « Toi et tes proches » first in Réglages, one page « Loin de chez toi » for the three ways in
/// from elsewhere, their state on « L’essentiel » and in the list, and the rail's picture opening a menu that leads to them.
/// </summary>
public partial class MainWindow
{
    /// <summary>The home's Tailscale as its extension says it (its administrator only), and the home as read with it.</summary>
    private HomeTailnetState? _awayHome;
    private HomeSettings? _awayFamily;
    private bool _awayKnown;
    private JellyfinClient? _awayRead, _awayReading;
    private int _awayDraw;

    /// <summary>
    /// The home and its Tailscale read again for this account (an administrator's), then told everywhere: the page, the
    /// list's dot, the tiles of « L’essentiel ». <paramref name="again"/>: even when already read for this client.
    /// </summary>
    private async Task ReadAwayAsync(bool again = false)
    {
        if (_demo || InParty || _client is not { } client) { ++_awayDraw; _awayHome = null; _awayFamily = null; _awayKnown = true; _awayRead = _awayReading = null; ShowAway(); return; }
        // Already read, or being read, for this account: told as it is (a second read would drop the first one's answer).
        if (!again && (ReferenceEquals(client, _awayRead) || ReferenceEquals(client, _awayReading))) { ShowAway(); return; }
        var draw = ++_awayDraw; _awayReading = client;
        if (!ReferenceEquals(client, _awayRead)) { _awayKnown = false; ShowAway(); }
        HomeSettings? family = null; HomeTailnetState? home = null;
        try
        {
            if (await IsAdministratorAsync(client))
            {
                family = await client.HomeSettingsAsync(_connectionLifetime.Token);
                if (family is not null)
                    try { home = await client.HomeTailnetAsync(_connectionLifetime.Token); }
                    catch (RelayRefusedException) { home = null; }
            }
        }
        catch (Exception ex) when (IsExpected(ex) || ex is RelayRefusedException)
        {
            if (draw != _awayDraw || !ReferenceEquals(client, _client)) return;
            _awayKnown = true; _awayRead = _awayReading = null; ShowAway(); return;
        }
        if (draw != _awayDraw || !ReferenceEquals(client, _client)) return;
        _awayReading = null;
        _awayFamily = family; _awayHome = home; _awayKnown = true; _awayRead = client;
        ShowAway();
    }

    /// <summary>This PC reaches Jellyfin from anywhere by itself: built-in Tailscale signed in, or the Tailscale app here.</summary>
    private bool YouReachAway => _settings.Tailnet || _tailscaleAppHere;

    private AwayState AwayNow => AwayFromHome.For(_awayHome, _awayKnown, YouReachAway);

    /// <summary>Where away from home stands, told on its page, beside its name in the list and on « L’essentiel ».</summary>
    private void ShowAway()
    {
        var away = AwayNow;
        var dot = away.Level switch { AwayLevel.Ready => "Success", AwayLevel.Attention or AwayLevel.Off => "Warning", _ => null };
        AwaySummary.Visibility = away.Level == AwayLevel.Unknown ? Visibility.Collapsed : Visibility.Visible;
        AwaySummaryTitle.Text = away.Title; AwaySummaryText.Text = away.Text;
        AwaySummaryIcon.Kind = away.Level == AwayLevel.Ready ? "check" : "info";
        AwaySummaryIcon.Foreground = Palette.Get(dot ?? "Muted");
        // The home's way in: for the administrator of a Jellyfin whose extension has the home.
        AwayFamilyHost.Visibility = _awayHome is not null || AwayFamilyCard.Children.Count > 0 && _awayFamily is not null ? Visibility.Visible : Visibility.Collapsed;
        AwayFunnelRow.Visibility = new FrameworkElement[] { GuestFunnelPanel, GuestAddressPanel, GuestAddressLine }.Any(x => x.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        var any = new FrameworkElement[] { TailnetRow, AwayFamilyHost, AwayFunnelRow }.Any(x => x.Visibility == Visibility.Visible);
        AwayWaysLabel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        AwayEmpty.Visibility = any || away.Level != AwayLevel.Unknown ? Visibility.Collapsed : Visibility.Visible;
        // Listed for whoever has a way in to set: built-in Tailscale on this PC, or a home's or a party's.
        AwaySettingsNav.Visibility = any || away.Level != AwayLevel.Unknown ? Visibility.Visible : Visibility.Collapsed;
        MarkSettingsNav(AwaySettingsNav, null, dot, away.Word);
        MarkSettingsNav(FamilySettingsNav, _awayFamily is { } family ? family.Profiles.Count(x => x.Offered).ToString(Lang.Culture) : null, null, null);
        ShowEssentialsPeople(away, dot);
    }

    /// <summary>A count or a coloured dot beside a section's name (its search count, first in the row, stays apart).</summary>
    private void MarkSettingsNav(Button nav, string? count, string? dot, string? said)
    {
        if (!_settingsNavDrawn || nav.Content is not DockPanel row) return;
        var mark = row.Children.OfType<FrameworkElement>().FirstOrDefault(x => Equals(x.Tag, "mark"));
        if (mark is null)
        {
            mark = new StackPanel { Orientation = Orientation.Horizontal, Tag = "mark", VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(8, 0, 2, 0) };
            DockPanel.SetDock(mark, Dock.Right); row.Children.Insert(1, mark);
        }
        var marks = (StackPanel)mark;
        marks.Children.Clear();
        if (count is { Length: > 0 })
        {
            // The button's own ink, a little quieter: white on the quiet list, dark on the lit one.
            var text = new TextBlock { Text = count, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = .7, VerticalAlignment = VerticalAlignment.Center };
            text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = nav });
            marks.Children.Add(text);
        }
        if (dot is not null) marks.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Palette.Get(dot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(count is null ? 0 : 8, 0, 0, 0) });
        mark.Visibility = marks.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(nav, said ?? "");
    }

    /// <summary>« Tes proches » on « L’essentiel »: the family, friends and away from home, each with its state and its way there.</summary>
    private void ShowEssentialsPeople(AwayState away, string? dot)
    {
        var admin = Administers;
        var family = _awayFamily;
        EssentialsFamily.Visibility = family is not null ? Visibility.Visible : Visibility.Collapsed;
        if (family is not null)
        {
            var profiles = family.Profiles.Count(x => x.Offered);
            EssentialsFamilyState.Text = Count(profiles, "profil", "profils", "profile", "profiles") + " · " + Count(family.Devices.Count, "appareil", "appareils", "device", "devices");
            EssentialsFamilyText.Text = L("Un mot de passe pour ta famille, puis chacun son profil.", "One password for your family, then a profile each.");
        }
        EssentialsFriends.Visibility = admin && SharingSettingsNav.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        EssentialsFriendsState.Text = L("Leur propre compte", "Their own account");
        EssentialsFriendsText.Text = L("Pour regarder ensemble ou chacun de son côté.", "To watch together or each on their own.");
        EssentialsAway.Visibility = away.Level == AwayLevel.Unknown ? Visibility.Collapsed : Visibility.Visible;
        EssentialsAwayDot.Visibility = dot is null ? Visibility.Collapsed : Visibility.Visible;
        if (dot is not null) EssentialsAwayDot.Fill = Palette.Get(dot);
        EssentialsAwayState.Text = away.Word;
        EssentialsAwayText.Text = AwayFromHome.Short(away.Level);
        EssentialsAwayButton.Content = away.Level == AwayLevel.Ready ? L("Détails", "Details") : L("Régler…", "Set up…");
        EssentialsPeople.Visibility = new[] { EssentialsFamily, EssentialsFriends, EssentialsAway }.Any(x => x.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AwayCheck_Click(object sender, RoutedEventArgs e)
    {
        _ = ReadAwayAsync(again: true); _ = ReadTailscaleAppAsync();
        if (FamilySettingsNav.Visibility == Visibility.Visible) _ = ShowFamilySettingsAsync();
    }

    /// <summary>« L’essentiel » → « Changer de profil »: « Qui regarde ? » of the home.</summary>
    private async void EssentialsSwitch_Click(object sender, RoutedEventArgs e)
    {
        await CloseSettingsAnimatedAsync();
        ShowHall(); _ = RefreshHallAsync();
    }

    /// <summary>Réglages opened on a section, from outside them (the profile menu).</summary>
    private void OpenSettingsAt(string tab)
    {
        if (SettingsOverlay.Visibility != Visibility.Visible) Settings_Click(this, new());
        SelectSettingsTab(tab);
    }

    // ---- The rail's picture: who watches, and the way to the family, friends and away from home (D « Le menu du profil ») ----

    /// <summary>The rail's picture (or Ctrl+P in a home): the profile menu. False when there is nobody to show it for.</summary>
    private bool OpenProfileMenu()
    {
        if (_demo || (_client is null && _home is null)) return false;
        if (_familyMenu is { IsOpen: true }) { CloseFamilyMenu(); return true; }
        _ = ShowProfileMenuAsync();
        return true;
    }

    private async Task ShowProfileMenuAsync()
    {
        // Who may see the family and friends: known once, kept for the session.
        if (_client is { } client)
            try { await IsAdministratorAsync(client); }
            catch (Exception ex) when (IsExpected(ex)) { }
        if (_familyMenu is { IsOpen: true }) return;
        ShowEssentialsAccount();
        var admin = Administers;
        var body = new Squircle { Style = (Style)FindResource("PopoverWindow"), Width = 320, Padding = new Thickness(8) };
        TextElement.SetFontFamily(body, (FontFamily)FindResource("AppFont")); TextElement.SetForeground(body, Palette.Get("Text"));
        var list = new StackPanel();

        // Who: the picture (its camera changes it), the name, the server and whether it answers.
        var head = new DockPanel { Margin = new Thickness(10, 8, 10, 10) };
        var face = new Grid { Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(new Ellipse { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Fill = Palette.Get("LineStrong") });
        face.Children.Add(new TextBlock { Text = AvatarLetter.Text, Width = 44, Padding = new Thickness(0, 11, 0, 0), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody") });
        if (EssentialsPhoto.Visibility == Visibility.Visible) face.Children.Add(new Ellipse { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Fill = EssentialsPhoto.Fill });
        if (_client is not null)
        {
            var camera = new Button { Style = (Style)FindResource("IconButton"), Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Background = Palette.Get("Raised2"), BorderBrush = Palette.Get("Bg"), BorderThickness = new Thickness(2), Content = new Icon { Kind = "camera", Width = 12, Height = 12 }, ToolTip = L("Changer ta photo", "Change your photo") };
            Motion.SetRadius(camera, new CornerRadius(11));
            AutomationProperties.SetName(camera, L("Changer ta photo", "Change your photo"));
            camera.Click += (_, _) => { CloseFamilyMenu(); ChoosePhoto_Click(this, new()); };
            face.Children.Add(camera);
        }
        DockPanel.SetDock(face, Dock.Left); head.Children.Add(face);
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new TextBlock { Text = EssentialsName.Text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var state = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var stateDot = new Ellipse { Width = 7, Height = 7, Fill = EssentialsServerDot.Fill, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(stateDot, Dock.Left); state.Children.Add(stateDot);
        state.Children.Add(new TextBlock { Text = EssentialsServerState.Text, FontSize = 12, Foreground = Palette.Get("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        who.Children.Add(state);
        head.Children.Add(who);
        list.Children.Add(head);

        // In a home: the other profiles, one click each, and « Qui regarde ? » whole.
        if (_home is { } home && home.Profiles.Count > 0)
        {
            var label = new DockPanel { Margin = new Thickness(12, 4, 12, 8) };
            var keys = new TextBlock { Text = "Ctrl + P", FontSize = 11, Foreground = Palette.Get("Faint") };
            DockPanel.SetDock(keys, Dock.Right); label.Children.Add(keys);
            label.Children.Add(new TextBlock { Text = HomeText.WhoIsWatching.ToUpper(Lang.Culture), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextSecondary") });
            list.Children.Add(label);
            var faces = new WrapPanel { Margin = new Thickness(10, 0, 10, 6) };
            foreach (var profile in home.Profiles)
            {
                var current = profile.Id == home.Current && _client?.Connection.UserId == profile.Id;
                if (current) continue;
                var picture = ProfileFace(home, profile, 40, 16);
                var button = new Button { Content = picture, Tag = profile, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 8), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = profile.Name, Template = BareButton() };
                AutomationProperties.SetName(button, profile.Pin ? L($"{profile.Name}, avec un code", $"{profile.Name}, with a PIN") : profile.Name);
                button.Click += (_, _) => _ = ChooseProfileAsync(profile);
                faces.Children.Add(button);
            }
            var all = new Button { Style = (Style)FindResource("IconButton"), Width = 40, Height = 40, Margin = new Thickness(0, 0, 8, 8), Content = new Icon { Kind = "swap", Width = 16, Height = 16 }, ToolTip = HomeText.WhoIsWatching };
            Motion.SetRadius(all, new CornerRadius(12));
            AutomationProperties.SetName(all, HomeText.WhoIsWatching);
            all.Click += (_, _) => { CloseFamilyMenu(); ShowHall(); _ = RefreshHallAsync(); };
            faces.Children.Add(all);
            list.Children.Add(faces);
        }

        // The family, friends and away from home: their pages of Réglages, for whom they are.
        var ways = new List<Button>();
        if (admin && FamilySettingsNav.Visibility == Visibility.Visible)
            ways.Add(MenuRow(MenuLine("home", L("Famille & profils", "Family & profiles"), _awayFamily is { } family ? Count(family.Profiles.Count(x => x.Offered), "profil", "profils", "profile", "profiles") : null), L("Famille & profils", "Family & profiles"), () => { CloseFamilyMenu(); OpenSettingsAt("family"); }));
        if (admin && SharingSettingsNav.Visibility == Visibility.Visible)
            ways.Add(MenuRow(MenuLine("users", L("Inviter un ami…", "Invite a friend…")), L("Inviter un ami…", "Invite a friend…"), () => { CloseFamilyMenu(); InviteAddFriend_Click(this, new()); }));
        if (AwaySettingsNav.Visibility == Visibility.Visible)
            ways.Add(MenuRow(MenuLine("globe", L("Loin de chez toi", "Away from home"), AwayNow.Word), L("Loin de chez toi", "Away from home"), () => { CloseFamilyMenu(); OpenSettingsAt("away"); }));
        if (ways.Count > 0)
        {
            list.Children.Add(new Border { Height = 1, Background = Palette.Get("Line"), Margin = new Thickness(8, 4, 8, 4) });
            foreach (var way in ways) list.Children.Add(way);
        }

        // The account, and the way out.
        list.Children.Add(new Border { Height = 1, Background = Palette.Get("Line"), Margin = new Thickness(8, 4, 8, 4) });
        if (_client is not null)
            list.Children.Add(MenuRow(MenuLine("user", L("Compte", "Account")), L("Compte", "Account"), () => { CloseFamilyMenu(); OpenSettingsAt("server"); }));
        if (_home is not null)
        {
            list.Children.Add(MenuRow(MenuLine("user", HomeText.OwnAccount), HomeText.OwnAccount, () => { CloseFamilyMenu(); Account_Click(this, new()); }));
            list.Children.Add(MenuRow(MenuLine("exit", HomeText.Leave), HomeText.Leave, () => _ = LeaveHomeAsync()));
        }
        else list.Children.Add(MenuRow(MenuLine("exit", L("Se déconnecter…", "Sign out…")), L("Se déconnecter…", "Sign out…"), () => { CloseFamilyMenu(); Account_Click(this, new()); }));

        body.Child = list;
        var popup = _familyMenu = new Popup { PlacementTarget = AccountButton, Placement = PlacementMode.Custom, StaysOpen = false, AllowsTransparency = true, Child = body, PopupAnimation = PopupAnimation.None };
        popup.CustomPopupPlacementCallback = (size, target, _) => [new CustomPopupPlacement(new Point(target.Width + 10, target.Height - size.Height), PopupPrimaryAxis.None)];
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseFamilyMenu(); AccountButton.Focus(); e.Handled = true; } };
        popup.Closed += (_, _) => { if (ReferenceEquals(_familyMenu, popup)) _familyMenu = null; };
        popup.IsOpen = true;
        _ = Dispatcher.BeginInvoke(() => list.Children.OfType<Button>().FirstOrDefault()?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        if (_home is not null) _ = RefreshHallAsync();
        _ = ReadAwayAsync();
    }
}
