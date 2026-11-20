using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.TorLink;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// The connection and the family, easy to find, as on Windows (MainWindow.Away.cs there; ogsas, 2026-11-20, maquette A
/// « En tête de liste » with D « Le menu du profil »): where watching away from home stands, read once for Réglages,
/// « L’essentiel » and the rail's menu, and that menu leading to the family, friends and away from home.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The home's Tailscale as its extension says it (its administrator only), and the home read with it.</summary>
    internal HomeTailnetState? AwayHome { get; private set; }
    internal HomeSettings? AwayFamily { get; private set; }
    internal bool AwayKnown { get; private set; }
    /// <summary>This account administers its Jellyfin (null: not known yet).</summary>
    internal bool? Administrator { get; private set; }
    /// <summary>Told again each time the state is read: Réglages' list, « L’essentiel », « Loin de chez toi ».</summary>
    internal event Action? AwayChanged;
    private Session? _awayRead, _awayReading;
    private int _awayDraw;

    internal AwayState AwayNow => AwayFromHome.For(AwayHome, AwayKnown, Settings.Tailnet);

    /// <summary>The home and its Tailscale read for this account (an administrator's); <paramref name="again"/>: even when already read.</summary>
    internal async Task ReadAwayAsync(bool again = false)
    {
        if (InParty || Session is not { } session) { ++_awayDraw; AwayHome = null; AwayFamily = null; AwayKnown = true; _awayRead = _awayReading = null; AwayChanged?.Invoke(); return; }
        // Already read, or being read, for this session: told as it is (a second read would drop the first one's answer).
        if (!again && (ReferenceEquals(session, _awayRead) || ReferenceEquals(session, _awayReading))) { AwayChanged?.Invoke(); return; }
        var draw = ++_awayDraw; _awayReading = session;
        if (!ReferenceEquals(session, _awayRead)) { AwayKnown = false; AwayChanged?.Invoke(); }
        HomeSettings? family = null; HomeTailnetState? home = null; bool? admin;
        try
        {
            admin = await session.Client.IsAdministratorAsync(session.Lifetime);
            if (admin == true)
            {
                family = await session.Client.HomeSettingsAsync(session.Lifetime);
                if (family is not null)
                    try { home = await session.Client.HomeTailnetAsync(session.Lifetime); }
                    catch (RelayRefusedException) { home = null; }
            }
        }
        catch (Exception ex) when (Errors.Expected(ex) || ex is RelayRefusedException)
        {
            if (draw != _awayDraw || !ReferenceEquals(session, Session)) return;
            AwayKnown = true; _awayRead = _awayReading = null; AwayChanged?.Invoke(); return;
        }
        if (draw != _awayDraw || !ReferenceEquals(session, Session)) return;
        _awayReading = null;
        Administrator = admin; AwayFamily = family; AwayHome = home; AwayKnown = true; _awayRead = session;
        AwayChanged?.Invoke();
    }

    /// <summary>The home and its Tailscale as « Famille & profils » just read them (an administrator's): told everywhere.</summary>
    internal void TellAway(HomeSettings family, HomeTailnetState? home)
    {
        if (Session is not { } session) return;
        ++_awayDraw; _awayReading = null; _awayRead = session;
        Administrator = true; AwayFamily = family; AwayHome = home; AwayKnown = true;
        AwayChanged?.Invoke();
    }

    /// <summary>Réglages opened on a section, from outside them (the rail's menu).</summary>
    internal void OpenSettingsAt(string key)
    {
        if (Current is SettingsPage open) { open.Show(key); return; }
        SettingsPage.OpenAt(key); OpenSheet();
    }

    // —— The rail's picture: who watches, and the way to the family, friends and away from home (D « Le menu du profil ») ——

    /// <summary>The rail's picture (or ⌘P in a home): the profile menu. False when there is nobody to show it for.</summary>
    private bool OpenProfileMenu()
    {
        if (Session is not { } session || InParty || Family is not null) return false;
        var menu = new ContextMenu();
        // Who: the name, the server and whether it answers; the photo changed from here (ogsas, 2026-11-20).
        var host = Uri.TryCreate(session.Connection.Server, UriKind.Absolute, out var server) ? server.Host : session.Connection.Server;
        var name = new TextBlock { Text = session.Connection.UserName, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var where = new TextBlock { Text = host + " · " + (NotAnswering ? L("ne répond pas", "not answering") : L("connecté", "connected")), FontSize = 11.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
        var who = new StackPanel();
        who.Children.Add(name); who.Children.Add(where);
        menu.Items.Add(new MenuItem { Header = who, Icon = AccountFace(26), IsEnabled = false });
        menu.Items.Add(Ui.Entry(Photo is null ? L("Choisir une photo…", "Choose a photo…") : L("Changer de photo…", "Change photo…"), "camera", () => _ = ChangePhotoFromMenuAsync()));

        // In a home: its profiles, one click each.
        if (_home is { } home && home.Profiles.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = HomeText.WhoIsWatching.ToUpper(Lang.Culture) + "   ·   ⌘P", IsEnabled = false });
            foreach (var profile in home.Profiles)
            {
                var current = profile.Id == home.Current && session.Connection.UserId == profile.Id;
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = profile.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis } } };
                if (current) words.Children.Add(new TextBlock { Text = L("En ce moment sur ce Mac", "Watching now on this Mac"), FontSize = 11.5, Foreground = Ui.Muted });
                var line = new DockPanel { MinWidth = 210 };
                if (current || profile.Pin)
                {
                    var mark = new Icon(current ? "check" : "lock", 15) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                    DockPanel.SetDock(mark, Dock.Right); line.Children.Add(mark);
                }
                line.Children.Add(words);
                var item = new MenuItem { Header = line, Icon = ProfileFace(home, profile, 26) };
                Avalonia.Automation.AutomationProperties.SetName(item, current ? L($"{profile.Name}, en ce moment sur ce Mac", $"{profile.Name}, watching now on this Mac") : profile.Pin ? L($"{profile.Name}, avec un code", $"{profile.Name}, with a PIN") : profile.Name);
                if (!current) item.Click += (_, _) => _ = ChooseProfileAsync(profile);
                menu.Items.Add(item);
            }
        }

        // The family, friends and away from home: their pages of Réglages, for whom they are.
        var admin = Administrator == true;
        var ways = new List<MenuItem>();
        if (admin && AwayFamily is { } family)
            ways.Add(Detailed(L("Famille & profils", "Family & profiles"), "home", Count(family.Profiles.Count(x => x.Offered), "profil", "profils", "profile", "profiles"), () => OpenSettingsAt("family")));
        if (admin) ways.Add(Ui.Entry(L("Inviter un ami…", "Invite a friend…"), "users", () => OpenSheet("friend")));
        ways.Add(Detailed(L("Loin de chez toi", "Away from home"), "globe", AwayNow.Word, () => OpenSettingsAt("away")));
        if (ways.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var way in ways) menu.Items.Add(way);
        }

        // The account, and the way out.
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Entry(L("Compte", "Account"), "user", OpenAccount));
        if (_home is not null) menu.Items.Add(Ui.Entry(HomeText.Leave, "exit", () => _ = LeaveHomeAsync()));
        else menu.Items.Add(Ui.Entry(L("Se déconnecter…", "Sign out…"), "exit", OpenAccount));

        menu.PlacementTarget = _accountRail; menu.Placement = PlacementMode.RightEdgeAlignedBottom; menu.HorizontalOffset = 8;
        _familyMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_familyMenu, menu)) _familyMenu = null; };
        menu.Open(_accountRail);
        if (_home is not null) _ = RefreshHallAsync();
        _ = ReadAwayAsync();
        return true;

        static MenuItem Detailed(string label, string icon, string detail, Action action)
        {
            var line = new DockPanel { MinWidth = 210 };
            if (detail.Length > 0)
            {
                var said = new TextBlock { Text = detail, FontSize = 12, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                DockPanel.SetDock(said, Dock.Right); line.Children.Add(said);
            }
            line.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var entry = new MenuItem { Header = line, Icon = new Icon(icon, 17) { Foreground = Ui.TextSecondary } };
            Avalonia.Automation.AutomationProperties.SetName(entry, detail.Length > 0 ? label + ", " + detail : label);
            entry.Click += (_, _) => action();
            return entry;
        }
    }

    /// <summary>The account's picture for the menu: its photo, or its letter.</summary>
    private Control AccountFace(double size)
    {
        var letter = Session?.Connection.UserName is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "?";
        var face = new Grid { Width = size, Height = size, Children = { new Avalonia.Controls.Shapes.Ellipse { Fill = Ui.Brush("#3F3F3F") }, new TextBlock { Text = letter, FontSize = size * .45, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#DEDEE4"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } } };
        if (Photo is { } photo) face.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Fill = new ImageBrush(photo) { Stretch = Stretch.UniformToFill } });
        return face;
    }

    /// <summary>« Changer de photo… » of the rail's menu: the picker, then what went wrong said, as Réglages does.</summary>
    private async Task ChangePhotoFromMenuAsync()
    {
        // The rail's picture shows the change; only what went wrong is said.
        if (await ChoosePhotoAsync() is { } said && said != L("Photo changée.", "Photo changed.")) Notice(said, tone: NoticeTone.Info);
    }
}
