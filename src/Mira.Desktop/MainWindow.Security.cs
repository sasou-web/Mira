using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mira.Core;
using Mira.Core.Family;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Sécurité » in « Toi et tes proches » (ogsas, 2026-11-20, maquette A « La page Security » with the steps of C behind
/// « Tout régler… »), an administrator's: a banner of what to fix, the accounts from the most urgent (no password, your
/// own short one) to the safest, then every device signed in, one « Déconnecter » each. « Protéger » sets a long password
/// nobody keeps (the home and friends' links get in without it) or one chosen. A line on « L’essentiel » and an amber dot
/// in the list while something is to fix; « Passer » quiets an item for 30 days there (<see cref="PlayerSettings.SecurityQuiet"/>).
/// </summary>
public partial class MainWindow
{
    private SecurityReport? _security;
    private JellyfinClient? _securityRead;
    private string _securityProblem = "";
    private int _securityDraw;
    private bool _securityAllShown, _securityDevicesShown;

    /// <summary>
    /// The accounts and devices read again for this administrator, then told everywhere: the page, the list's dot, the
    /// line of « L’essentiel ». <paramref name="again"/>: even when already read for this client.
    /// </summary>
    private async Task ReadSecurityAsync(bool again = false)
    {
        if (_demo || InParty || _client is not { } client) { ++_securityDraw; _security = null; _securityRead = null; ShowSecurity(); return; }
        if (!again && ReferenceEquals(client, _securityRead)) { ShowSecurity(); return; }
        var draw = ++_securityDraw;
        if (!ReferenceEquals(client, _securityRead)) { _security = null; _securityProblem = ""; ShowSecurity(); }
        try
        {
            var report = await IsAdministratorAsync(client) ? await client.SecurityAsync(_connectionLifetime.Token) : null;
            if (report is not null) report = report with { Server = await ReadServerReachAsync(client) };
            if (draw != _securityDraw || !ReferenceEquals(client, _client)) return;
            _security = report; _securityRead = client; _securityProblem = "";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (draw != _securityDraw || !ReferenceEquals(client, _client)) return;
            _securityProblem = Friendly(ex);
        }
        ShowSecurity();
    }

    /// <summary>What was read, for the account signed in now only.</summary>
    private SecurityReport? Security => ReferenceEquals(_securityRead, _client) ? _security : null;

    private bool SecurityQuiet(string key) => AccountSecurity.Quiet(_settings.SecurityQuiet, key, DateTimeOffset.UtcNow);

    /// <summary>Where security stands: the list's dot, the line of « L’essentiel », and the page while it shows.</summary>
    private void ShowSecurity()
    {
        var report = Security;
        SecuritySettingsNav.Visibility = report is not null || _securityProblem.Length > 0 && ReferenceEquals(_securityRead, _client) ? Visibility.Visible : Visibility.Collapsed;
        var nagging = report is null ? [] : AccountSecurity.Issues(report, SecurityQuiet);
        MarkSettingsNav(SecuritySettingsNav, null, report is null ? null : nagging.Count > 0 ? "Warning" : "Success",
            report is null ? null : nagging.Count > 0 ? SecurityText.Headline(nagging.Count) : SecurityText.Headline(0));
        ShowEssentialsSecurity(nagging);
        if (_settingsTab == "security" || SecuritySettings.Children.Count == 0) DrawSecurity();
        if (_settingsTab == "security" && report is null && _securityProblem.Length == 0 && SecuritySettingsNav.Visibility != Visibility.Visible) SelectSettingsTab("home");
    }

    /// <summary>« L’essentiel »: one line, only while something is to fix (and not quieted).</summary>
    private void ShowEssentialsSecurity(IReadOnlyList<SecurityIssue> issues)
    {
        EssentialsSecurity.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (issues.Count == 0) { EssentialsSecurity.Child = null; return; }
        EssentialsSecurity.Background = Palette.Get("WarningSurface"); EssentialsSecurity.BorderBrush = Palette.Get("WarningLine");
        EssentialsSecurity.BorderThickness = new Thickness(1); EssentialsSecurity.CornerRadius = new CornerRadius(7); EssentialsSecurity.Padding = new Thickness(18, 14, 14, 14);
        var line = new DockPanel();
        var review = new Button { Content = SecurityText.Review, Tag = "security", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        review.Click += EssentialsGo_Click;
        AutomationProperties.SetName(review, SecurityText.Review + " · " + SecurityText.Title);
        DockPanel.SetDock(review, Dock.Right); line.Children.Add(review);
        var icon = new Icon { Kind = "warning", Width = 18, Height = 18, Foreground = Palette.Get("Warning"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = SecurityText.Alert(issues.Count), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        words.Children.Add(new TextBlock { Text = SecurityText.AlertLine(issues), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 3, 0, 0) });
        line.Children.Add(words);
        EssentialsSecurity.Child = line;
    }

    // ---- The page ----

    private void DrawSecurity()
    {
        var page = SecuritySettings;
        page.Children.Clear();
        var report = Security;
        if (report is null)
        {
            var text = _securityProblem.Length > 0 ? _securityProblem : SecurityText.Reading;
            var row = FamilyRow(new StackPanel());
            ((StackPanel)row.Child).Children.Add(FamilyNote(text, _securityProblem.Length > 0, new Thickness(0)));
            if (_securityProblem.Length > 0) ((StackPanel)row.Child).Children.Add(Spaced(FamilyButton(L("Réessayer", "Try again"), async () => await ReadSecurityAsync(again: true)), 12));
            page.Children.Add(row);
            return;
        }
        var issues = AccountSecurity.Issues(report);
        page.Children.Add(SecurityBanner(report, issues));
        if (!report.Known) page.Children.Add(FamilyNote(SecurityText.Unknown, margin: new Thickness(4, 0, 30, 12)));

        // Who reaches the server, before the accounts: what is open lets anyone try them all.
        if (report.Server is { Known: true } server)
        {
            page.Children.Add(SecurityLabel(SecurityText.Server, null));
            if (server.Internet is { } internet)
                page.Children.Add(SecurityServerRow("globe", SecurityText.InternetTitle(internet.Open), SecurityText.InternetLine(internet.Open), internet.Open, async () => await CloseInternetAsync()));
            if (server.PublicWifi is { } wifi)
                page.Children.Add(SecurityServerRow("wifi", SecurityText.PublicWifiTitle(wifi), SecurityText.PublicWifiLine(wifi), wifi, async () => await ClosePublicWifiAsync()));
            page.Children.Add(new Border { Height = 8 });
        }

        // The accounts, the most urgent first. All safe and many: the first ones, then « Show all ».
        page.Children.Add(SecurityLabel(SecurityText.Accounts + " · " + report.Accounts.Count.ToString(Lang.Culture), null));
        var accounts = report.Accounts;
        var folded = issues.Count == 0 && accounts.Count > 3 && !_securityAllShown;
        foreach (var account in folded ? accounts.Take(2) : accounts) page.Children.Add(SecurityAccountRow(account));
        if (folded)
        {
            var rest = accounts.Skip(2).Select(x => x.Name).ToList();
            var more = SecurityLine();
            var show = FamilyButton(L("Tout afficher", "Show all"), () => { _securityAllShown = true; DrawSecurity(); }, quiet: true);
            show.Margin = new Thickness(14, 0, 0, 0); show.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(show, Dock.Right); more.Children.Add(show);
            more.Children.Add(new TextBlock { Text = L($"{Names(rest)} sont protégés aussi", $"{Names(rest)} are protected too"), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
            page.Children.Add(SecurityCard(more));
        }

        // The devices: this one first, a « Déconnecter » each, all the others at once.
        var others = report.Devices.Count(x => !x.This);
        Button? all = null;
        if (others > 0)
        {
            all = FamilyButton(SecurityText.SignOutAll, async () => await SignOutOthersAsync(), quiet: true);
            all.Margin = new Thickness(0, 0, 14, 0); all.Padding = new Thickness(10, 4, 10, 4); all.FontSize = 12.5;
        }
        page.Children.Add(SecurityLabel(SecurityText.Devices + " · " + report.Devices.Count.ToString(Lang.Culture), all));
        // Many devices (34 on the Dell): the first ones, then « Show all ».
        var foldDevices = report.Devices.Count > 8 && !_securityDevicesShown;
        foreach (var device in foldDevices ? report.Devices.Take(6) : report.Devices) page.Children.Add(SecurityDeviceRow(device, report.Checked));
        if (foldDevices)
        {
            var more = SecurityLine();
            var show = FamilyButton(L("Tout afficher", "Show all"), () => { _securityDevicesShown = true; DrawSecurity(); }, quiet: true);
            show.Margin = new Thickness(14, 0, 0, 0); show.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(show, Dock.Right); more.Children.Add(show);
            more.Children.Add(new TextBlock { Text = L($"{report.Devices.Count - 6} autres appareils", $"{report.Devices.Count - 6} more devices"), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
            page.Children.Add(SecurityCard(more));
        }
        page.Children.Add(FamilyNote(SecurityText.SignOutNote, margin: new Thickness(4, 4, 30, 0)));

        static string Names(IReadOnlyList<string> names) => names.Count switch
        {
            1 => names[0],
            2 => names[0] + L(" et ", " and ") + names[1],
            _ => string.Join(", ", names.Take(names.Count - 1)) + L(" et ", " and ") + names[^1],
        };
    }

    private Border SecurityBanner(SecurityReport report, IReadOnlyList<SecurityIssue> issues)
    {
        var bad = issues.Count > 0;
        var line = new DockPanel();
        var button = bad
            ? FamilyButton(SecurityText.FixAll, async () => await FixAllAsync(), primary: true)
            : FamilyButton(SecurityText.CheckAgain, async () => await ReadSecurityAsync(again: true));
        button.Margin = new Thickness(14, 0, 0, 0); button.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(button, Dock.Right); line.Children.Add(button);
        var icon = new Icon { Kind = bad ? "warning" : "check", Width = 20, Height = 20, Foreground = Palette.Get(bad ? "Warning" : "Success"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = SecurityText.Headline(issues.Count), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        words.Children.Add(title);
        words.Children.Add(new TextBlock { Text = bad ? SecurityText.Summary(issues) : SecurityText.Good(report), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 3, 0, 0) });
        line.Children.Add(words);
        var banner = new Border { Style = (Style)FindResource("SettingRow"), Child = line, Margin = new Thickness(0, 0, 0, 18) };
        if (bad) { banner.Background = Palette.Get("WarningSurface"); banner.BorderBrush = Palette.Get("WarningLine"); banner.BorderThickness = new Thickness(1); }
        return banner;
    }

    private Border SecurityAccountRow(SecurityAccount account)
    {
        var line = SecurityLine();
        // Its button, then its state, on the right.
        Button? act = null;
        if (account.You) act = FamilyButton(account.NoPassword ? SecurityText.ChooseOwn : SecurityText.Change, async () => await ChangeOwnPasswordAsync(account), primary: account.Weak);
        else if (account.Weak) act = FamilyButton(SecurityText.Protect, async () => await ProtectAsync(account), primary: true);
        if (act is not null)
        {
            act.Margin = new Thickness(12, 0, 0, 0); act.VerticalAlignment = VerticalAlignment.Center; act.Padding = new Thickness(14, 6, 14, 6);
            AutomationProperties.SetName(act, (act.Content as string) + " · " + account.Name);
            DockPanel.SetDock(act, Dock.Right); line.Children.Add(act);
        }
        var state = account.Disabled ? SecurityPill(SecurityText.Disabled, null) : account.NoPassword ? SecurityPill(SecurityText.NoPassword, "Warning")
            : account.Short ? SecurityPill(SecurityText.ShortPassword, "Warning") : SecurityPill(SecurityText.Protected, "Success");
        DockPanel.SetDock(state, Dock.Right); line.Children.Add(state);

        var face = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(new Ellipse { Fill = Palette.Get("LineStrong") });
        face.Children.Add(new TextBlock { Text = account.Name.Length > 0 ? account.Name[..1].ToUpper(Lang.Culture) : "?", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (account.ImageTag is { } tag && _client is { } client) _ = SecurityPhotoAsync(client, account.Id, tag, face);
        DockPanel.SetDock(face, Dock.Left); line.Children.Add(face);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new WrapPanel();
        name.Children.Add(new TextBlock { Text = account.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (account.You) name.Children.Add(SecurityTag(SecurityText.You));
        else if (account.Way == AccountWay.Friend) name.Children.Add(SecurityTag(SecurityText.Friend));
        else if (account.Way == AccountWay.Home) name.Children.Add(SecurityTag(SecurityText.Profile));
        words.Children.Add(name);
        words.Children.Add(new TextBlock { Text = SecurityText.Line(account), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 2, 0, 0) });
        line.Children.Add(words);
        var row = SecurityCard(line);
        AutomationProperties.SetName(row, account.Name);
        return row;
    }

    private Border SecurityDeviceRow(SecurityDevice device, DateTimeOffset now)
    {
        var line = SecurityLine();
        if (!device.This)
        {
            var sign = FamilyButton(SecurityText.SignOut, async () => await SignOutAsync(device));
            sign.Margin = new Thickness(12, 0, 0, 0); sign.VerticalAlignment = VerticalAlignment.Center; sign.Padding = new Thickness(14, 6, 14, 6);
            AutomationProperties.SetName(sign, SecurityText.SignOut + " · " + device.Name);
            DockPanel.SetDock(sign, Dock.Right); line.Children.Add(sign);
        }
        var stale = device.Stale(now);
        var seen = new TextBlock { Text = SecurityText.Seen(device, now), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get(stale ? "Warning" : device.Active ? "Success" : "TextSecondary") };
        DockPanel.SetDock(seen, Dock.Right); line.Children.Add(seen);
        var icon = new Icon { Kind = DeviceIcon(device), Width = 18, Height = 18, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(8, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new WrapPanel();
        name.Children.Add(new TextBlock { Text = device.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (device.This) name.Children.Add(SecurityTag(SecurityText.ThisDevice(mac: false)));
        words.Children.Add(name);
        words.Children.Add(new TextBlock { Text = SecurityText.DeviceLine(device), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 2, 0, 0) });
        line.Children.Add(words);
        var row = SecurityCard(line);
        AutomationProperties.SetName(row, device.Name);
        return row;
    }

    /// <summary>A device's picture from what it says it is: a phone, a TV, a browser, else a computer.</summary>
    private static string DeviceIcon(SecurityDevice device)
    {
        var said = (device.App + " " + device.Name).ToLowerInvariant();
        if (said.Contains("iphone") || said.Contains("android") || said.Contains("ipad") || said.Contains("phone")) return "phone";
        if (said.Contains(" tv") || said.Contains("tv ") || said.Contains("roku") || said.Contains("kodi") || said.Contains("télé") || said.Contains("swiftfin tv") || said.Contains("webos") || said.Contains("tizen")) return "tv";
        if (said.Contains("web") || said.Contains("chrome") || said.Contains("firefox") || said.Contains("safari") || said.Contains("edge")) return "globe";
        return "laptop";
    }

    private async Task SecurityPhotoAsync(JellyfinClient client, string userId, string tag, Grid face)
    {
        try
        {
            if (await client.ProfilePictureAsync(userId, tag, 72, _connectionLifetime.Token) is not { Length: > 0 } bytes) return;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit(); image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.StreamSource = new System.IO.MemoryStream(bytes); image.DecodePixelWidth = 72; image.EndInit(); image.Freeze();
            face.Children.Add(new Ellipse { Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill } });
        }
        catch (Exception ex) when (IsExpected(ex) || ex is NotSupportedException or System.IO.IOException or InvalidOperationException) { }
    }

    private static DockPanel SecurityLine() => new();

    private Border SecurityCard(UIElement content) => new()
    {
        Style = (Style)FindResource("SettingRow"), Child = content, Padding = new Thickness(18, 12, 14, 12), Margin = new Thickness(0, 0, 0, 6),
    };

    /// <summary>An opening of the server: its sign, what it is, « Too open » and « Close it… », or « Protected ».</summary>
    private Border SecurityServerRow(string icon, string title, string about, bool open, Action close)
    {
        var line = SecurityLine();
        if (open)
        {
            var button = FamilyButton(SecurityText.CloseIt, close, primary: true);
            button.Margin = new Thickness(12, 0, 0, 0); button.VerticalAlignment = VerticalAlignment.Center; button.Padding = new Thickness(14, 6, 14, 6);
            AutomationProperties.SetName(button, SecurityText.CloseIt + " · " + title);
            DockPanel.SetDock(button, Dock.Right); line.Children.Add(button);
        }
        var state = open ? SecurityPill(SecurityText.TooOpen, "Warning") : SecurityPill(SecurityText.Protected, "Success");
        DockPanel.SetDock(state, Dock.Right); line.Children.Add(state);
        var sign = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Palette.Get(open ? "WarningSurface" : "Raised2"),
            Child = new Icon { Kind = icon, Width = 18, Height = 18, Foreground = Palette.Get(open ? "Warning" : "TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(sign, Dock.Left); line.Children.Add(sign);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        words.Children.Add(new TextBlock { Text = about, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 2, 0, 0) });
        line.Children.Add(words);
        var row = SecurityCard(line);
        AutomationProperties.SetName(row, title);
        return row;
    }

    private DockPanel SecurityLabel(string text, Button? right)
    {
        var line = new DockPanel { Margin = new Thickness(4, 10, 0, 8) };
        if (right is not null) { DockPanel.SetDock(right, Dock.Right); right.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(right); }
        var label = new TextBlock { Text = text.ToUpper(Lang.Culture), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Faint"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        line.Children.Add(label);
        return line;
    }

    private static Border SecurityTag(string text) => new()
    {
        Background = Palette.Get("Raised2"), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextSecondary") },
    };

    /// <summary>An account's state: amber with its sign (no password, short), green with a check (protected).</summary>
    private static Border SecurityPill(string text, string? tone)
    {
        var words = new StackPanel { Orientation = Orientation.Horizontal };
        if (tone is not null) words.Children.Add(new Icon { Kind = tone == "Success" ? "check" : "warning", Width = 12, Height = 12, Foreground = Palette.Get(tone), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        words.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get(tone switch { "Warning" => "WarningText", "Success" => "Success", _ => "TextSecondary" }), VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = Palette.Get(tone switch { "Warning" => "WarningSurface", "Success" => "SuccessSurface", _ => "Raised" }),
            BorderBrush = Palette.Get(tone switch { "Warning" => "WarningLine", "Success" => "SuccessLine", _ => "Line" }), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center, Child = words,
        };
    }

    private static FrameworkElement Spaced(FrameworkElement element, double top) { element.Margin = new Thickness(0, top, 0, 0); element.HorizontalAlignment = HorizontalAlignment.Left; return element; }

    // ---- « Protéger », your own password, a device signed out ----

    /// <summary>« Protéger »: Mira's long password (the home and links get in without it), or one chosen and given.</summary>
    private async Task<bool> ProtectAsync(SecurityAccount account, string? step = null, double progress = 0)
    {
        if (_client is not { } client) return false;
        var (body, lockIt, password, status) = ProtectBody(account);
        var buttons = step is null
            ? new (object, string, int)[] { (L("Annuler", "Cancel"), "Quiet", 0), (ActionLabel("lock", SecurityText.Protect), "Primary", 1) }
            : [(SecurityText.Skip, "Quiet", 2), (ActionLabel("lock", SecurityText.Protect), "Primary", 1)];
        var answer = await CardAsync(step is null ? SecurityText.ProtectTitle(account.Name) : SecurityText.NoPasswordTitle(account), SecurityText.ProtectNote, body, buttons, async pressed =>
        {
            if (pressed != 1) return true;
            var chosen = lockIt.IsChecked == true ? AccountSecurity.LockPassword() : password.Password;
            if (lockIt.IsChecked != true && chosen.Length < AccountSecurity.MinLength) { FamilySay(status, SecurityText.TooShort, true); password.Focus(); return false; }
            try { await client.SetPasswordAsync(account.Id, chosen, _connectionLifetime.Token); return true; }
            catch (Exception ex) when (IsExpected(ex)) { FamilySay(status, Friendly(ex), true); return false; }
        }, step, progress);
        if (answer == 2) QuietSecurity(AccountSecurity.AccountKey(account.Id));
        if (answer != 1) return answer == 2;
        await ReadSecurityAsync(again: true);
        return true;
    }

    private (StackPanel Body, RadioButton Lock, PasswordBox Password, TextBlock Status) ProtectBody(SecurityAccount account)
    {
        var body = new StackPanel();
        // Mira's password first, chosen, unless nothing else lets this account in (a plain account: a password it can type).
        var lockIt = SecurityChoice(SecurityText.LockIt, SecurityText.LockAbout(account), account.Way != AccountWay.Password ? SecurityText.Recommended : null);
        var choose = SecurityChoice(SecurityText.Choose, SecurityText.ChooseAbout(account.Name), account.Way == AccountWay.Password ? SecurityText.Recommended : null);
        var group = "protect-" + account.Id;
        lockIt.GroupName = choose.GroupName = group;
        (account.Way == AccountWay.Password ? choose : lockIt).IsChecked = true;
        var password = new PasswordBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 10, 0, 0) };
        AutomationProperties.SetName(password, SecurityText.NewPassword);
        var strength = FamilyNote(SecurityText.Strength(0), margin: new Thickness(30, 6, 0, 0));
        password.PasswordChanged += (_, _) => strength.Text = SecurityText.Strength(password.Password.Length);
        var chosen = new StackPanel { Visibility = choose.IsChecked == true ? Visibility.Visible : Visibility.Collapsed };
        chosen.Children.Add(password); chosen.Children.Add(strength);
        choose.Checked += (_, _) => { chosen.Visibility = Visibility.Visible; password.Focus(); };
        lockIt.Checked += (_, _) => chosen.Visibility = Visibility.Collapsed;
        body.Children.Add(lockIt); body.Children.Add(choose); body.Children.Add(chosen);
        body.Children.Add(FamilyNote(SecurityText.SignedOutToo(account.Name), margin: new Thickness(0, 12, 0, 0)));
        var status = FamilyNote("");
        body.Children.Add(status);
        return (body, lockIt, password, status);
    }

    /// <summary>One choice of a card: its name (and « Recommended »), what it does under it.</summary>
    private RadioButton SecurityChoice(string title, string about, string? badge)
    {
        var words = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        var head = new WrapPanel();
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Text") });
        if (badge is not null) head.Children.Add(SecurityTag(badge));
        words.Children.Add(head);
        words.Children.Add(new TextBlock { Text = about, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 3, 0, 0) });
        var choice = new RadioButton { Content = words, Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(choice, title);
        AutomationProperties.SetHelpText(choice, about);
        return choice;
    }

    /// <summary>Your own password changed, or chosen when there is none: the current one asked first, its new length kept.</summary>
    private async Task<bool> ChangeOwnPasswordAsync(SecurityAccount account, string? step = null, double progress = 0)
    {
        if (_client is not { } client) return false;
        var body = new StackPanel();
        PasswordBox? current = null;
        if (!account.NoPassword)
        {
            body.Children.Add(new TextBlock { Text = SecurityText.CurrentPassword, FontSize = 12.5, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 0, 0, 6) });
            current = new PasswordBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
            AutomationProperties.SetName(current, SecurityText.CurrentPassword);
            body.Children.Add(current);
        }
        body.Children.Add(new TextBlock { Text = SecurityText.NewPassword, FontSize = 12.5, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 0, 0, 6) });
        var password = new PasswordBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(password, SecurityText.NewPassword);
        body.Children.Add(password);
        var strength = FamilyNote(SecurityText.Strength(0), margin: new Thickness(0, 6, 0, 0));
        password.PasswordChanged += (_, _) =>
        {
            strength.Text = SecurityText.Strength(password.Password.Length);
            if (password.Password.Length >= AccountSecurity.MinLength) strength.Foreground = Palette.Get("Success"); else strength.ClearValue(TextBlock.ForegroundProperty);
        };
        body.Children.Add(strength);
        var status = FamilyNote("");
        body.Children.Add(status);
        var title = step is not null ? account.NoPassword ? SecurityText.NoPasswordTitle(account) : SecurityText.ShortTitle : account.NoPassword ? SecurityText.ChooseTitle : SecurityText.ChangeTitle;
        var note = step is not null && account.Short ? SecurityText.ShortText(account.Length ?? 0) : SecurityText.ChangeNote;
        var buttons = step is null
            ? new (object, string, int)[] { (L("Annuler", "Cancel"), "Quiet", 0), (SecurityText.ChangeButton, "Primary", 1) }
            : [(SecurityText.Skip, "Quiet", 2), (SecurityText.ChangeButton, "Primary", 1)];
        var answer = await CardAsync(title, note, body, buttons, async pressed =>
        {
            if (pressed != 1) return true;
            if (password.Password.Length < AccountSecurity.MinLength) { FamilySay(status, SecurityText.TooShort, true); password.Focus(); return false; }
            try { await client.ChangeOwnPasswordAsync(current?.Password ?? "", password.Password, _connectionLifetime.Token); }
            catch (UnauthorizedAccessException ex) { FamilySay(status, ex.Message, true); current?.Clear(); current?.Focus(); return false; }
            catch (Exception ex) when (IsExpected(ex)) { FamilySay(status, Friendly(ex), true); return false; }
            // Its length kept with this account here, so that « Security » knows it next time too.
            if (_profile.LoadConnection() is { } saved && saved.UserId == client.Connection.UserId && saved.Token == client.Connection.Token)
                _profile.SaveConnection(saved with { PasswordLength = client.Connection.PasswordLength });
            return true;
        }, step, progress, width: 500);
        if (answer == 2) QuietSecurity(AccountSecurity.AccountKey(account.Id));
        if (answer != 1) return answer == 2;
        await ReadSecurityAsync(again: true);
        return true;
    }

    private async Task SignOutAsync(SecurityDevice device)
    {
        if (_client is not { } client) return;
        if (!await ConfirmAsync(SecurityText.SignOutTitle(device), SecurityText.SignOutText(device), SecurityText.SignOut, L("Annuler", "Cancel"))) return;
        try { await client.SignOutDeviceAsync(device, _connectionLifetime.Token); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        await ReadSecurityAsync(again: true);
    }

    private async Task SignOutOthersAsync()
    {
        if (_client is not { } client || Security is not { } report) return;
        var others = report.Devices.Where(x => !x.This).ToList();
        if (others.Count == 0 || !await ConfirmAsync(SecurityText.SignOutAllTitle(others.Count), SecurityText.SignOutAllText(mac: false), SecurityText.SignOut, L("Annuler", "Cancel"))) return;
        var failed = 0;
        foreach (var device in others)
            try { await client.SignOutDeviceAsync(device, _connectionLifetime.Token); }
            catch (Exception ex) when (IsExpected(ex)) { failed++; }
        if (failed > 0) SetNotice(failed == 1 ? L("Un appareil n’a pas pu être déconnecté.", "One device couldn’t be signed out.") : L($"{failed} appareils n’ont pas pu être déconnectés.", $"{failed} devices couldn’t be signed out."));
        await ReadSecurityAsync(again: true);
    }

    private void QuietSecurity(string key)
    {
        AccountSecurity.Forget(_settings.SecurityQuiet, DateTimeOffset.UtcNow);
        _settings.SecurityQuiet[key] = DateTimeOffset.UtcNow;
        SaveSettingsOrSay();
    }

    // ---- « Ton serveur »: closing Jellyfin to the Internet, to public Wi-Fi ----

    /// <summary>
    /// Who reaches Jellyfin: its access from the whole Internet (any administrator), and, on Jellyfin's PC only, the
    /// firewall's rules that open it on public networks.
    /// </summary>
    private async Task<ServerReach> ReadServerReachAsync(JellyfinClient client)
    {
        InternetOpening? internet = null; bool? wifi = null;
        try { internet = await client.InternetOpeningAsync(_connectionLifetime.Token); }
        catch (Exception ex) when (IsExpected(ex)) { }
        if (await JellyfinOnThisPcAsync(client) && Uri.TryCreate(client.Connection.Server, UriKind.Absolute, out var uri))
            wifi = await Task.Run(() => JellyfinAutostart.Program() is { } program ? JellyfinAutostart.PublicOpenings(program, uri.Port).Count > 0 : (bool?)null);
        return new(internet, wifi);
    }

    /// <summary>
    /// « Fermer… » (Internet): the devices seen lately from outside, which would be cut, named first; never from a device
    /// that itself comes from outside. In « Tout régler… », « Laisser ouvert » quiets it for 30 days.
    /// </summary>
    private async Task<bool> CloseInternetAsync(string? step = null, double progress = 0)
    {
        if (_client is not { } client) return false;
        InternetOpening? opening;
        try { opening = await client.InternetOpeningAsync(_connectionLifetime.Token); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); return false; }
        if (opening is not { Open: true })
        {
            await ReadSecurityAsync(again: true);
            return opening is not null;
        }
        if (opening.ThisOutside)
        {
            var ok = await CardAsync(SecurityText.FromHomeTitle, SecurityText.FromHomeText, null, [(L("OK", "OK"), "Primary", 1)], step: step, progress: progress);
            return ok == 1;
        }
        var body = new StackPanel();
        if (opening.Outside.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = SecurityText.WouldCut(opening.Outside.Count), TextWrapping = TextWrapping.Wrap, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody") });
            var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var device in opening.Outside.Take(6))
            {
                var line = SecurityLine();
                var where = new TextBlock { Text = SecurityText.FromOutside, FontSize = 12.5, Foreground = Palette.Get("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(where, Dock.Right); line.Children.Add(where);
                var icon = new Icon { Kind = OutsideIcon(device), Width = 16, Height = 16, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
                var words = new StackPanel();
                words.Children.Add(new TextBlock { Text = device.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                words.Children.Add(new TextBlock { Text = SecurityText.OutsideLine(device), Style = (Style)FindResource("SettingDescription") });
                line.Children.Add(words);
                var card = SecurityCard(line);
                card.Background = Palette.Get("Raised"); card.Padding = new Thickness(14, 9, 14, 9);
                list.Children.Add(card);
            }
            body.Children.Add(list);
            body.Children.Add(FamilyNote(SecurityText.CutHint(opening.Outside.Count), margin: new Thickness(0, 6, 0, 0)));
        }
        var status = FamilyNote("");
        body.Children.Add(status);
        var answer = await CardAsync(step is null ? SecurityText.CloseInternetTitle : SecurityText.InternetStepTitle, step is null ? SecurityText.CloseInternetText : SecurityText.InternetStepText + " " + SecurityText.CloseInternetText,
            body.Children.Count > 1 ? body : null, [(SecurityText.KeepOpen, "Quiet", 2), (ActionLabel("lock", SecurityText.CloseItNow), "Primary", 1)], async pressed =>
            {
                if (pressed != 1) return true;
                try
                {
                    if (await client.CloseToInternetAsync(_connectionLifetime.Token)) return true;
                    FamilySay(status, SecurityText.FromHomeText, true); return false;
                }
                catch (Exception ex) when (IsExpected(ex)) { FamilySay(status, Friendly(ex), true); return false; }
            }, step, progress);
        if (answer == 2 && step is not null) QuietSecurity(AccountSecurity.InternetKey);
        if (answer == 1) await ReadSecurityAsync(again: true);
        return answer is 1 or 2;
    }

    private static string OutsideIcon(OutsideDevice device) => DeviceIcon(new SecurityDevice(device.Id, device.Name, device.App, null, null, device.UserName, device.LastActive, false, false, false, DeviceWay.Other));

    /// <summary>« Fermer… » (Wi-Fi publics): the firewall's rules that open Jellyfin there no longer apply to public networks, with Windows' consent.</summary>
    private async Task<bool> ClosePublicWifiAsync(string? step = null, double progress = 0)
    {
        if (_client is not { } client || !Uri.TryCreate(client.Connection.Server, UriKind.Absolute, out var uri)) return false;
        var answer = await CardAsync(SecurityText.ClosePublicTitle, SecurityText.PublicWifiLine(true) + " " + SecurityText.ClosePublicText, null,
            [(SecurityText.KeepOpen, "Quiet", 2), (ActionLabel("lock", SecurityText.CloseItNow), "Primary", 1)], step: step, progress: progress);
        if (answer == 2 && step is not null) QuietSecurity(AccountSecurity.PublicWifiKey);
        if (answer != 1) return answer == 2;
        switch (await JellyfinAutostart.ClosePublicAsync(uri.Port))
        {
            case AutostartResult.Refused: SetNotice(SecurityText.Refused); break;
            case AutostartResult.Failed: SetNotice(SecurityText.ClosePublicFailed); break;
        }
        await ReadSecurityAsync(again: true);
        return true;
    }

    // ---- « Tout régler… »: one question at a time ----

    private bool _fixing;

    /// <summary>
    /// Each thing to fix asked on its own card (maquette C): an account without a password (« Lock it », a password chosen
    /// instead), your short password, a device not used for months (« Sign it out », « Yes, keep it »). « Skip » leaves it
    /// and asks again in 30 days; Esc stops there.
    /// </summary>
    private async Task FixAllAsync()
    {
        if (_fixing || _client is not { } client || Security is not { } report) return;
        _fixing = true;
        try
        {
            var issues = AccountSecurity.Issues(report);
            for (var i = 0; i < issues.Count; i++)
            {
                var step = SecurityText.Step(i + 1, issues.Count);
                var progress = (i + 1.0) / issues.Count;
                var issue = issues[i];
                bool go;
                if (issue.Server == ServerOpening.Internet) go = await CloseInternetAsync(step, progress);
                else if (issue.Server == ServerOpening.PublicWifi) go = await ClosePublicWifiAsync(step, progress);
                else if (issue.Account is { You: true } you) go = await ChangeOwnPasswordAsync(you, step, progress);
                else if (issue.Account is { Way: AccountWay.Password } plain) go = await ProtectAsync(plain, step, progress);
                else if (issue.Account is { } account) go = await LockStepAsync(client, account, step, progress);
                else go = await DeviceStepAsync(client, issue.Device!, report.Checked, step, progress);
                if (!go) break;
            }
        }
        finally { _fixing = false; }
        await ReadSecurityAsync(again: true);
    }

    /// <summary>An account without a password, as one step: « Lock it » (Mira's password) first, « Choose a password instead ».</summary>
    private async Task<bool> LockStepAsync(JellyfinClient client, SecurityAccount account, string step, double progress)
    {
        var body = new StackPanel();
        // The name alone: the card's note already says anyone can open it.
        var who = SecurityCard(new TextBlock { Text = account.Name, FontWeight = FontWeights.SemiBold });
        who.Background = Palette.Get("Raised"); who.Margin = new Thickness(0, 0, 0, 14);
        body.Children.Add(who);
        body.Children.Add(new TextBlock { Text = SecurityText.LockAbout(account), TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Palette.Get("TextBody") });
        body.Children.Add(FamilyNote(SecurityText.SignedOutToo(account.Name), margin: new Thickness(0, 8, 0, 0)));
        var status = FamilyNote("");
        body.Children.Add(status);
        var answer = await CardAsync(SecurityText.NoPasswordTitle(account), SecurityText.ProtectNote, body,
            [(SecurityText.ChooseInstead, "Quiet", 3), (SecurityText.Skip, "Quiet", 2), (ActionLabel("lock", SecurityText.LockItShort), "Primary", 1)], async pressed =>
            {
                if (pressed != 1) return true;
                try { await client.SetPasswordAsync(account.Id, AccountSecurity.LockPassword(), _connectionLifetime.Token); return true; }
                catch (Exception ex) when (IsExpected(ex)) { FamilySay(status, Friendly(ex), true); return false; }
            }, step, progress);
        if (answer == 2) QuietSecurity(AccountSecurity.AccountKey(account.Id));
        // « Choose a password instead »: the full choice, on the same step.
        if (answer == 3) return await ProtectAsync(account with { Way = AccountWay.Password }, step, progress);
        return answer is 1 or 2;
    }

    private async Task<bool> DeviceStepAsync(JellyfinClient client, SecurityDevice device, DateTimeOffset now, string step, double progress)
    {
        var body = new StackPanel();
        var line = SecurityLine();
        line.Children.Add(new Icon { Kind = DeviceIcon(device), Width = 18, Height = 18, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(line.Children[0], Dock.Left);
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = device.Name, FontWeight = FontWeights.SemiBold });
        words.Children.Add(new TextBlock { Text = SecurityText.DeviceLine(device) + (device.LastActive is { } last ? " · " + L($"vu le {Day(last.ToLocalTime())}", $"last used {Day(last.ToLocalTime())}") : ""), Style = (Style)FindResource("SettingDescription") });
        line.Children.Add(words);
        var card = SecurityCard(line);
        card.Background = Palette.Get("Raised"); card.Margin = new Thickness(0, 0, 0, 14);
        body.Children.Add(card);
        body.Children.Add(new TextBlock { Text = SecurityText.SignOutText(device), TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Palette.Get("TextBody") });
        var status = FamilyNote("");
        body.Children.Add(status);
        var answer = await CardAsync(SecurityText.StaleTitle, SecurityText.StaleText(device, now), body,
            [(SecurityText.Keep, "Quiet", 2), (ActionLabel("exit", SecurityText.SignItOut), "Primary", 1)], async pressed =>
            {
                if (pressed != 1) return true;
                try { await client.SignOutDeviceAsync(device, _connectionLifetime.Token); return true; }
                catch (Exception ex) when (IsExpected(ex)) { FamilySay(status, Friendly(ex), true); return false; }
            }, step, progress);
        if (answer == 2) QuietSecurity(AccountSecurity.DeviceKey(device.Id));
        return answer is 1 or 2;
    }
}
