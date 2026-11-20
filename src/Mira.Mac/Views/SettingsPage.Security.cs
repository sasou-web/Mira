using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Family;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Sécurité », as on Windows (MainWindow.Security.cs there; ogsas, 2026-11-20, maquette A « La page Security » with the
/// steps of C behind « Tout régler… »): a banner, the accounts from the most urgent to the safest, every device signed in
/// with its « Déconnecter »; an amber or green dot in the list, a line on « L’essentiel » while something is to fix.
/// </summary>
public sealed partial class SettingsPage
{
    private bool _securityAllShown, _securityDevicesShown;
    private bool _fixing;

    /// <summary>The section, its dot in the list, drawn again each time the accounts and devices are read.</summary>
    private void SecuritySection()
    {
        var tab = _tabs["security"];
        var button = _tabButtons["security"];
        var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0), IsHitTestVisible = false, IsVisible = false };
        if (button.Content is DockPanel row) { DockPanel.SetDock(dot, Dock.Right); row.Children.Insert(1, dot); }
        void Tell()
        {
            var report = Shell.Security;
            button.IsVisible = report is not null || Shell.SecurityProblem.Length > 0 && Shell.Administrator == true;
            var nagging = report is null ? [] : AccountSecurity.Issues(report, Shell.SecurityQuiet);
            dot.IsVisible = report is not null; dot.Fill = nagging.Count > 0 ? Ui.Warning : Ui.Success;
            Avalonia.Automation.AutomationProperties.SetHelpText(button, report is null ? "" : SecurityText.Headline(nagging.Count));
            if (CurrentTab == "security" || tab.Children.Count == 0) DrawSecurity();
            if (CurrentTab == "security" && HelpTopic is null && !button.IsVisible) Show("home");
        }
        Page.WhileShown(button, shown => { if (shown) { Shell.SecurityChanged += Tell; Tell(); _ = Shell.ReadSecurityAsync(again: true); } else Shell.SecurityChanged -= Tell; });
    }

    private void DrawSecurity()
    {
        var page = _tabs["security"];
        page.Children.Clear();
        if (Shell.Security is not { } report)
        {
            var problem = Shell.SecurityProblem;
            var column = Ui.Column(12, Description(problem.Length > 0 ? problem : SecurityText.Reading, 0));
            if (problem.Length > 0) { var again = Ui.Action(L("Réessayer", "Try again"), null, "", async () => await Shell.ReadSecurityAsync(again: true)); again.HorizontalAlignment = HorizontalAlignment.Left; column.Children.Add(again); }
            page.Children.Add(Card(column));
            return;
        }
        var issues = AccountSecurity.Issues(report);
        page.Children.Add(Banner());
        if (!report.Known) page.Children.Add(Description(SecurityText.Unknown, 0));

        // Who reaches the server, before the accounts, as on Windows.
        if (report.Server?.Internet is { } internet)
        {
            page.Children.Add(Label(SecurityText.Server, null));
            page.Children.Add(ServerRow("globe", SecurityText.InternetTitle(internet.Open), SecurityText.InternetLine(internet.Open), internet.Open, async () => await CloseInternetAsync(null, 0)));
        }

        page.Children.Add(Label(SecurityText.Accounts + " · " + report.Accounts.Count.ToString(Lang.Culture), null));
        var folded = issues.Count == 0 && report.Accounts.Count > 3 && !_securityAllShown;
        foreach (var account in folded ? report.Accounts.Take(2) : report.Accounts) page.Children.Add(AccountRow(account));
        if (folded)
        {
            var rest = report.Accounts.Skip(2).Select(x => x.Name).ToList();
            var line = new DockPanel();
            var all = Ui.Action(L("Tout afficher", "Show all"), null, "quiet", () => { _securityAllShown = true; DrawSecurity(); });
            all.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(all, Dock.Right); line.Children.Add(all);
            line.Children.Add(new TextBlock { Text = L($"{Names(rest)} sont protégés aussi", $"{Names(rest)} are protected too"), FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            page.Children.Add(Small(line));
        }

        Button? others = null;
        if (report.Devices.Any(x => !x.This)) { others = Ui.Action(SecurityText.SignOutAll, null, "quiet", async () => await SignOutOthersAsync()); others.FontSize = 12.5; }
        page.Children.Add(Label(SecurityText.Devices + " · " + report.Devices.Count.ToString(Lang.Culture), others));
        // Many devices (34 on ogsas's server): the first ones, then « Show all », as on Windows.
        var foldDevices = report.Devices.Count > 8 && !_securityDevicesShown;
        foreach (var device in foldDevices ? report.Devices.Take(6) : report.Devices) page.Children.Add(DeviceRow(device, report.Checked));
        if (foldDevices)
        {
            var line = new DockPanel();
            var all = Ui.Action(L("Tout afficher", "Show all"), null, "quiet", () => { _securityDevicesShown = true; DrawSecurity(); });
            all.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(all, Dock.Right); line.Children.Add(all);
            line.Children.Add(new TextBlock { Text = L($"{report.Devices.Count - 6} autres appareils", $"{report.Devices.Count - 6} more devices"), FontSize = 12, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center });
            page.Children.Add(Small(line));
        }
        page.Children.Add(Description(SecurityText.SignOutNote, 0));

        Control Banner()
        {
            var bad = issues.Count > 0;
            var line = new DockPanel();
            var act = bad ? Ui.Action(SecurityText.FixAll, null, "primary", async () => await FixAllAsync()) : Ui.Action(SecurityText.CheckAgain, null, "", async () => await Shell.ReadSecurityAsync(again: true));
            act.VerticalAlignment = VerticalAlignment.Center; act.Margin = new Thickness(14, 0, 0, 0);
            DockPanel.SetDock(act, Dock.Right); line.Children.Add(act);
            var icon = new Icon(bad ? "warning" : "check", 20) { Foreground = bad ? Ui.Warning : Ui.Success, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
            var title = CardTitle(SecurityText.Headline(issues.Count));
            Avalonia.Automation.AutomationProperties.SetLiveSetting(title, Avalonia.Automation.AutomationLiveSetting.Polite);
            var words = Ui.Column(0, title, Description(bad ? SecurityText.Summary(issues) : SecurityText.Good(report), 3));
            words.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(words);
            var card = Card(line);
            if (bad) { card.Background = Ui.Brush("#F21C1A16"); card.BorderBrush = Ui.Brush("#4D8C7650"); card.BorderThickness = new Thickness(1); }
            card.Margin = new Thickness(0, 0, 0, 8);
            return card;
        }
        static string Names(IReadOnlyList<string> names) => names.Count switch
        {
            1 => names[0],
            2 => names[0] + L(" et ", " and ") + names[1],
            _ => string.Join(", ", names.Take(names.Count - 1)) + L(" et ", " and ") + names[^1],
        };
    }

    private Control AccountRow(SecurityAccount account)
    {
        var line = new DockPanel();
        Button? act = null;
        if (account.You) act = Ui.Action(account.NoPassword ? SecurityText.ChooseOwn : SecurityText.Change, null, account.Weak ? "primary" : "", async () => await ChangeOwnAsync(account, null, 0));
        else if (account.Weak) act = Ui.Action(SecurityText.Protect, null, "primary", async () => await ProtectAsync(account, null, 0));
        if (act is not null)
        {
            act.VerticalAlignment = VerticalAlignment.Center; act.Margin = new Thickness(12, 0, 0, 0);
            Avalonia.Automation.AutomationProperties.SetName(act, (account.You ? account.NoPassword ? SecurityText.ChooseOwn : SecurityText.Change : SecurityText.Protect) + " · " + account.Name);
            DockPanel.SetDock(act, Dock.Right); line.Children.Add(act);
        }
        var pill = account.Disabled ? Pill(SecurityText.Disabled, null) : account.NoPassword ? Pill(SecurityText.NoPassword, "warning")
            : account.Short ? Pill(SecurityText.ShortPassword, "warning") : Pill(SecurityText.Protected, "success");
        DockPanel.SetDock(pill, Dock.Right); line.Children.Add(pill);
        var face = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(new Ellipse { Fill = Ui.LineStrong });
        face.Children.Add(new TextBlock { Text = account.Name.Length > 0 ? account.Name[..1].ToUpper(Lang.Culture) : "?", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBody, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(face, Dock.Left); line.Children.Add(face);
        var name = new WrapPanel();
        name.Children.Add(new TextBlock { Text = account.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var tag = account.You ? SecurityText.You : account.Way switch { AccountWay.Friend => SecurityText.Friend, AccountWay.Home => SecurityText.Profile, _ => null };
        if (tag is not null) name.Children.Add(SecurityTag(tag));
        var words = Ui.Column(0, name, Description(SecurityText.Line(account), 2));
        words.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(words);
        var row = Small(line);
        Avalonia.Automation.AutomationProperties.SetName(row, account.Name);
        return row;
    }

    /// <summary>An opening of the server: its sign, what it is, « Too open » and « Close it… », or « Protected ».</summary>
    private Control ServerRow(string icon, string title, string about, bool open, Action close)
    {
        var line = new DockPanel();
        if (open)
        {
            var act = Ui.Action(SecurityText.CloseIt, null, "primary", close);
            act.VerticalAlignment = VerticalAlignment.Center; act.Margin = new Thickness(12, 0, 0, 0);
            Avalonia.Automation.AutomationProperties.SetName(act, SecurityText.CloseIt + " · " + title);
            DockPanel.SetDock(act, Dock.Right); line.Children.Add(act);
        }
        var pill = open ? Pill(SecurityText.TooOpen, "warning") : Pill(SecurityText.Protected, "success");
        DockPanel.SetDock(pill, Dock.Right); line.Children.Add(pill);
        var sign = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = open ? Ui.Brush("#F21C1A16") : Ui.Raised2,
            Child = new Icon(icon, 18) { Foreground = open ? Ui.Warning : Ui.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(sign, Dock.Left); line.Children.Add(sign);
        var words = Ui.Column(0, new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, Description(about, 2));
        words.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(words);
        var row = Small(line);
        Avalonia.Automation.AutomationProperties.SetName(row, title);
        return row;
    }

    /// <summary>
    /// « Fermer… » (Internet), as on Windows: the devices seen lately from outside, which would be cut, named first;
    /// never from a Mac that itself comes from outside. In « Tout régler… », « Laisser ouvert » quiets it for 30 days.
    /// </summary>
    private async Task<bool> CloseInternetAsync(string? step, double progress)
    {
        if (Shell.Session is not { } session) return false;
        InternetOpening? opening;
        try { opening = await session.Client.InternetOpeningAsync(session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); return false; }
        if (opening is not { Open: true }) { await Shell.ReadSecurityAsync(again: true); return opening is not null; }
        if (opening.ThisOutside) return await Shell.CardAsync(SecurityText.FromHomeTitle, SecurityText.FromHomeText, null, [(L("OK", "OK"), null, "primary", 1)], null, step, progress) == 1;
        var status = Status();
        var body = new StackPanel();
        if (opening.Outside.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = SecurityText.WouldCut(opening.Outside.Count), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBody, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            foreach (var device in opening.Outside.Take(6))
            {
                var line = new DockPanel();
                var where = new TextBlock { Text = SecurityText.FromOutside, FontSize = 12.5, Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(where, Dock.Right); line.Children.Add(where);
                var icon = new Icon(DeviceIcon(new SecurityDevice(device.Id, device.Name, device.App, null, null, device.UserName, device.LastActive, false, false, false, DeviceWay.Other)), 16)
                { Foreground = Ui.TextSecondary, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
                line.Children.Add(Ui.Column(0, new TextBlock { Text = device.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, Description(SecurityText.OutsideLine(device), 0)));
                var card = Small(line);
                card.Background = Ui.Raised; card.Padding = new Thickness(14, 9, 14, 9); card.Margin = new Thickness(0, 0, 0, 6);
                body.Children.Add(card);
            }
            body.Children.Add(Description(SecurityText.CutHint(opening.Outside.Count), 6));
        }
        body.Children.Add(status);
        var answer = await Shell.CardAsync(step is null ? SecurityText.CloseInternetTitle : SecurityText.InternetStepTitle,
            step is null ? SecurityText.CloseInternetText : SecurityText.InternetStepText + " " + SecurityText.CloseInternetText, body,
            [(SecurityText.KeepOpen, null, "quiet", 2), (SecurityText.CloseItNow, "lock", "primary", 1)], async pressed =>
            {
                if (pressed != 1) return true;
                try
                {
                    if (await session.Client.CloseToInternetAsync(session.Lifetime)) return true;
                    Say(status, SecurityText.FromHomeText); return false;
                }
                catch (Exception ex) when (Errors.Expected(ex)) { Say(status, Errors.Friendly(ex)); return false; }
            }, step, progress);
        if (answer == 2 && step is not null) Shell.QuietSecurity(AccountSecurity.InternetKey);
        if (answer == 1) await Shell.ReadSecurityAsync(again: true);
        return answer is 1 or 2;
    }

    private Control DeviceRow(SecurityDevice device, DateTimeOffset now)
    {
        var line = new DockPanel();
        if (!device.This)
        {
            var sign = Ui.Action(SecurityText.SignOut, null, "", async () => await SignOutAsync(device));
            sign.VerticalAlignment = VerticalAlignment.Center; sign.Margin = new Thickness(12, 0, 0, 0);
            Avalonia.Automation.AutomationProperties.SetName(sign, SecurityText.SignOut + " · " + device.Name);
            DockPanel.SetDock(sign, Dock.Right); line.Children.Add(sign);
        }
        var seen = new TextBlock { Text = SecurityText.Seen(device, now), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = device.Stale(now) ? Ui.Warning : device.Active ? Ui.Success : Ui.TextSecondary };
        DockPanel.SetDock(seen, Dock.Right); line.Children.Add(seen);
        var icon = new Icon(DeviceIcon(device), 18) { Foreground = Ui.TextSecondary, Margin = new Thickness(8, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        var name = new WrapPanel();
        name.Children.Add(new TextBlock { Text = device.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (device.This) name.Children.Add(SecurityTag(SecurityText.ThisDevice(mac: true)));
        var words = Ui.Column(0, name, Description(SecurityText.DeviceLine(device), 2));
        words.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(words);
        var row = Small(line);
        Avalonia.Automation.AutomationProperties.SetName(row, device.Name);
        return row;
    }

    /// <summary>A device's picture from what it says it is, as on Windows.</summary>
    private static string DeviceIcon(SecurityDevice device)
    {
        var said = (device.App + " " + device.Name).ToLowerInvariant();
        if (said.Contains("iphone") || said.Contains("android") || said.Contains("ipad") || said.Contains("phone")) return "phone";
        if (said.Contains(" tv") || said.Contains("tv ") || said.Contains("roku") || said.Contains("kodi") || said.Contains("télé") || said.Contains("webos") || said.Contains("tizen")) return "tv";
        if (said.Contains("web") || said.Contains("chrome") || said.Contains("firefox") || said.Contains("safari") || said.Contains("edge")) return "globe";
        return "laptop";
    }

    private static Border Small(Control content)
    {
        var card = Card(content);
        card.Padding = new Thickness(18, 12, 14, 12);
        return card;
    }

    private static DockPanel Label(string text, Button? right)
    {
        var line = new DockPanel { Margin = new Thickness(4, 10, 0, 0) };
        if (right is not null) { DockPanel.SetDock(right, Dock.Right); right.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(right); }
        var label = new TextBlock { Text = text.ToUpper(Lang.Culture), FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center, Classes = { "label" } };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(label, 2);
        line.Children.Add(label);
        return line;
    }

    private static Border SecurityTag(string text) => new()
    {
        Background = Ui.Raised2, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextSecondary },
    };

    private static Border Pill(string text, string? tone)
    {
        var ink = tone switch { "warning" => Ui.WarningText, "success" => Ui.Success, _ => Ui.TextSecondary };
        var words = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (tone is not null) words.Children.Add(new Icon(tone == "success" ? "check" : "warning", 12) { Foreground = tone == "success" ? Ui.Success : Ui.Warning, VerticalAlignment = VerticalAlignment.Center });
        words.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = Ui.Brush(tone switch { "warning" => "#F21C1A16", "success" => "#14182A20", _ => "#1C1C1C" }),
            BorderBrush = Ui.Brush(tone switch { "warning" => "#4D8C7650", "success" => "#4D5C8A70", _ => "#2A2A2A" }), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center, Child = words,
        };
    }

    /// <summary>« L’essentiel »: one line, only while something is to fix (and not quieted).</summary>
    private Control EssentialsSecurity()
    {
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var text = Description("", 3);
        var review = Ui.Action(SecurityText.Review, null, "", () => Go("security"));
        review.VerticalAlignment = VerticalAlignment.Center; review.Margin = new Thickness(14, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(review, SecurityText.Review + " · " + SecurityText.Title);
        var line = new DockPanel();
        DockPanel.SetDock(review, Dock.Right); line.Children.Add(review);
        var icon = new Icon("warning", 18) { Foreground = Ui.Warning, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        var words = Ui.Column(0, title, text); words.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(words);
        var card = Card(line);
        card.Background = Ui.Brush("#F21C1A16"); card.BorderBrush = Ui.Brush("#4D8C7650"); card.BorderThickness = new Thickness(1); card.Padding = new Thickness(18, 14, 14, 14);
        card.IsVisible = false;
        void Show()
        {
            var issues = Shell.Security is { } report ? AccountSecurity.Issues(report, Shell.SecurityQuiet) : [];
            card.IsVisible = issues.Count > 0;
            title.Text = SecurityText.Alert(issues.Count); text.Text = SecurityText.AlertLine(issues); text.IsVisible = text.Text.Length > 0;
        }
        Page.WhileShown(card, shown => { if (shown) { Shell.SecurityChanged += Show; Show(); _ = Shell.ReadSecurityAsync(); } else Shell.SecurityChanged -= Show; });
        return card;
    }

    // ---- The questions ----

    private static TextBox PasswordField(string name, double width = 300)
    {
        var box = new TextBox { Width = width, FontSize = 14, PasswordChar = '•', HorizontalAlignment = HorizontalAlignment.Left };
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        return box;
    }

    private static TextBlock Status() => new() { FontSize = 12, Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
    private static void Say(TextBlock status, string text) { status.Text = text; status.IsVisible = text.Length > 0; }

    /// <summary>One choice of a card: its name (and « Recommended »), what it does under it.</summary>
    private static RadioButton Choice(string title, string about, string? badge, string group)
    {
        var head = new WrapPanel();
        head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold });
        if (badge is not null) head.Children.Add(SecurityTag(badge));
        var choice = new RadioButton { GroupName = group, Content = Ui.Column(3, head, new TextBlock { Text = about, FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap }), Margin = new Thickness(0, 0, 0, 12) };
        Avalonia.Automation.AutomationProperties.SetName(choice, title);
        return choice;
    }

    private async Task<bool> ProtectAsync(SecurityAccount account, string? step, double progress)
    {
        if (Shell.Session is not { } session) return false;
        var group = "protect-" + account.Id;
        var lockIt = Choice(SecurityText.LockIt, SecurityText.LockAbout(account), account.Way != AccountWay.Password ? SecurityText.Recommended : null, group);
        var choose = Choice(SecurityText.Choose, SecurityText.ChooseAbout(account.Name), account.Way == AccountWay.Password ? SecurityText.Recommended : null, group);
        (account.Way == AccountWay.Password ? choose : lockIt).IsChecked = true;
        var password = PasswordField(SecurityText.NewPassword);
        var strength = Description(SecurityText.Strength(0), 6);
        password.TextChanged += (_, _) => strength.Text = SecurityText.Strength(password.Text?.Length ?? 0);
        var chosen = Ui.Column(0, password, strength);
        chosen.Margin = new Thickness(30, 0, 0, 0); chosen.IsVisible = choose.IsChecked == true;
        choose.IsCheckedChanged += (_, _) => { chosen.IsVisible = choose.IsChecked == true; if (chosen.IsVisible) password.Focus(); };
        var status = Status();
        var body = Ui.Column(0, lockIt, choose, chosen, Description(SecurityText.SignedOutToo(account.Name), 12), status);
        var buttons = step is null
            ? new (string, string?, string, int)[] { (L("Annuler", "Cancel"), null, "quiet", 0), (SecurityText.Protect, "lock", "primary", 1) }
            : [(SecurityText.Skip, null, "quiet", 2), (SecurityText.Protect, "lock", "primary", 1)];
        var answer = await Shell.CardAsync(step is null ? SecurityText.ProtectTitle(account.Name) : SecurityText.NoPasswordTitle(account), SecurityText.ProtectNote, body, buttons, async pressed =>
        {
            if (pressed != 1) return true;
            var mine = choose.IsChecked == true;
            var chosenPassword = mine ? password.Text ?? "" : AccountSecurity.LockPassword();
            if (mine && chosenPassword.Length < AccountSecurity.MinLength) { Say(status, SecurityText.TooShort); password.Focus(); return false; }
            try { await session.Client.SetPasswordAsync(account.Id, chosenPassword, session.Lifetime); return true; }
            catch (Exception ex) when (Errors.Expected(ex)) { Say(status, Errors.Friendly(ex)); return false; }
        }, step, progress);
        if (answer == 2) Shell.QuietSecurity(AccountSecurity.AccountKey(account.Id));
        if (answer != 1) return answer == 2;
        await Shell.ReadSecurityAsync(again: true);
        return true;
    }

    private async Task<bool> ChangeOwnAsync(SecurityAccount account, string? step, double progress)
    {
        if (Shell.Session is not { } session) return false;
        var body = new StackPanel();
        TextBox? current = null;
        if (!account.NoPassword)
        {
            current = PasswordField(SecurityText.CurrentPassword, 320);
            body.Children.Add(new TextBlock { Text = SecurityText.CurrentPassword, FontSize = 12.5, Foreground = Ui.TextSecondary, Margin = new Thickness(0, 0, 0, 6) });
            body.Children.Add(current);
        }
        var password = PasswordField(SecurityText.NewPassword, 320);
        body.Children.Add(new TextBlock { Text = SecurityText.NewPassword, FontSize = 12.5, Foreground = Ui.TextSecondary, Margin = new Thickness(0, current is null ? 0 : 14, 0, 6) });
        body.Children.Add(password);
        var strength = Description(SecurityText.Strength(0), 6);
        password.TextChanged += (_, _) => { var length = password.Text?.Length ?? 0; strength.Text = SecurityText.Strength(length); strength.Foreground = length >= AccountSecurity.MinLength ? Ui.Success : Ui.Muted; };
        body.Children.Add(strength);
        var status = Status();
        body.Children.Add(status);
        var title = step is not null ? account.NoPassword ? SecurityText.NoPasswordTitle(account) : SecurityText.ShortTitle : account.NoPassword ? SecurityText.ChooseTitle : SecurityText.ChangeTitle;
        var note = step is not null && account.Short ? SecurityText.ShortText(account.Length ?? 0) : SecurityText.ChangeNote;
        var buttons = step is null
            ? new (string, string?, string, int)[] { (L("Annuler", "Cancel"), null, "quiet", 0), (SecurityText.ChangeButton, null, "primary", 1) }
            : [(SecurityText.Skip, null, "quiet", 2), (SecurityText.ChangeButton, null, "primary", 1)];
        var answer = await Shell.CardAsync(title, note, body, buttons, async pressed =>
        {
            if (pressed != 1) return true;
            var chosen = password.Text ?? "";
            if (chosen.Length < AccountSecurity.MinLength) { Say(status, SecurityText.TooShort); password.Focus(); return false; }
            try { await session.Client.ChangeOwnPasswordAsync(current?.Text ?? "", chosen, session.Lifetime); }
            catch (UnauthorizedAccessException ex) { Say(status, ex.Message); if (current is not null) { current.Text = ""; current.Focus(); } return false; }
            catch (Exception ex) when (Errors.Expected(ex)) { Say(status, Errors.Friendly(ex)); return false; }
            await Shell.KeepPasswordLengthAsync(session.Client.Connection);
            return true;
        }, step, progress, width: 500);
        if (answer == 2) Shell.QuietSecurity(AccountSecurity.AccountKey(account.Id));
        if (answer != 1) return answer == 2;
        await Shell.ReadSecurityAsync(again: true);
        return true;
    }

    private async Task SignOutAsync(SecurityDevice device)
    {
        if (Shell.Session is not { } session) return;
        if (!await Shell.ConfirmAsync(SecurityText.SignOutTitle(device), SecurityText.SignOutText(device), SecurityText.SignOut, L("Annuler", "Cancel"))) return;
        try { await session.Client.SignOutDeviceAsync(device, session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); }
        await Shell.ReadSecurityAsync(again: true);
    }

    private async Task SignOutOthersAsync()
    {
        if (Shell.Session is not { } session || Shell.Security is not { } report) return;
        var others = report.Devices.Where(x => !x.This).ToList();
        if (others.Count == 0 || !await Shell.ConfirmAsync(SecurityText.SignOutAllTitle(others.Count), SecurityText.SignOutAllText(mac: true), SecurityText.SignOut, L("Annuler", "Cancel"))) return;
        var failed = 0;
        foreach (var device in others)
            try { await session.Client.SignOutDeviceAsync(device, session.Lifetime); }
            catch (Exception ex) when (Errors.Expected(ex)) { failed++; }
        if (failed > 0) Shell.Notice(failed == 1 ? L("Un appareil n’a pas pu être déconnecté.", "One device couldn’t be signed out.") : L($"{failed} appareils n’ont pas pu être déconnectés.", $"{failed} devices couldn’t be signed out."));
        await Shell.ReadSecurityAsync(again: true);
    }

    /// <summary>« Tout régler… »: each thing to fix on its own card, as on Windows; Escape stops there.</summary>
    private async Task FixAllAsync()
    {
        if (_fixing || Shell.Session is not { } session || Shell.Security is not { } report) return;
        _fixing = true;
        try
        {
            var issues = AccountSecurity.Issues(report);
            for (var i = 0; i < issues.Count; i++)
            {
                var step = SecurityText.Step(i + 1, issues.Count);
                var progress = (i + 1.0) / issues.Count;
                bool go;
                if (issues[i].Server == ServerOpening.Internet) go = await CloseInternetAsync(step, progress);
                else if (issues[i].Server is not null) go = true;
                else if (issues[i].Account is { You: true } you) go = await ChangeOwnAsync(you, step, progress);
                else if (issues[i].Account is { Way: AccountWay.Password } plain) go = await ProtectAsync(plain, step, progress);
                else if (issues[i].Account is { } account) go = await LockStepAsync(session, account, step, progress);
                else go = await DeviceStepAsync(session, issues[i].Device!, report.Checked, step, progress);
                if (!go) break;
            }
        }
        finally { _fixing = false; }
        await Shell.ReadSecurityAsync(again: true);
    }

    private async Task<bool> LockStepAsync(Session session, SecurityAccount account, string step, double progress)
    {
        // The name alone: the card's note already says anyone can open it.
        var who = Small(new TextBlock { Text = account.Name, FontSize = 14, FontWeight = FontWeight.SemiBold });
        who.Background = Ui.Raised; who.Margin = new Thickness(0, 0, 0, 14);
        var status = Status();
        var body = Ui.Column(0, who, new TextBlock { Text = SecurityText.LockAbout(account), FontSize = 13.5, Foreground = Ui.TextBody, TextWrapping = TextWrapping.Wrap },
            Description(SecurityText.SignedOutToo(account.Name), 8), status);
        var answer = await Shell.CardAsync(SecurityText.NoPasswordTitle(account), SecurityText.ProtectNote, body,
            [(SecurityText.ChooseInstead, null, "quiet", 3), (SecurityText.Skip, null, "quiet", 2), (SecurityText.LockItShort, "lock", "primary", 1)], async pressed =>
            {
                if (pressed != 1) return true;
                try { await session.Client.SetPasswordAsync(account.Id, AccountSecurity.LockPassword(), session.Lifetime); return true; }
                catch (Exception ex) when (Errors.Expected(ex)) { Say(status, Errors.Friendly(ex)); return false; }
            }, step, progress);
        if (answer == 2) Shell.QuietSecurity(AccountSecurity.AccountKey(account.Id));
        if (answer == 3) return await ProtectAsync(account with { Way = AccountWay.Password }, step, progress);
        return answer is 1 or 2;
    }

    private async Task<bool> DeviceStepAsync(Session session, SecurityDevice device, DateTimeOffset now, string step, double progress)
    {
        var line = new DockPanel();
        var icon = new Icon(DeviceIcon(device), 18) { Foreground = Ui.TextSecondary, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        line.Children.Add(Ui.Column(2, new TextBlock { Text = device.Name, FontSize = 14, FontWeight = FontWeight.SemiBold },
            Description(SecurityText.DeviceLine(device) + (device.LastActive is { } last ? " · " + L($"vu le {Day(last.ToLocalTime())}", $"last used {Day(last.ToLocalTime())}") : ""), 0)));
        var card = Small(line);
        card.Background = Ui.Raised; card.Margin = new Thickness(0, 0, 0, 14);
        var status = Status();
        var body = Ui.Column(0, card, new TextBlock { Text = SecurityText.SignOutText(device), FontSize = 13.5, Foreground = Ui.TextBody, TextWrapping = TextWrapping.Wrap }, status);
        var answer = await Shell.CardAsync(SecurityText.StaleTitle, SecurityText.StaleText(device, now), body,
            [(SecurityText.Keep, null, "quiet", 2), (SecurityText.SignItOut, "exit", "primary", 1)], async pressed =>
            {
                if (pressed != 1) return true;
                try { await session.Client.SignOutDeviceAsync(device, session.Lifetime); return true; }
                catch (Exception ex) when (Errors.Expected(ex)) { Say(status, Errors.Friendly(ex)); return false; }
            }, step, progress);
        if (answer == 2) Shell.QuietSecurity(AccountSecurity.DeviceKey(device.Id));
        return answer is 1 or 2;
    }
}
