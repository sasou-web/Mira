using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Help for someone new: the welcome on a profile Mira has never opened, the guide (where videos go, Jellyfin's own
/// page, other devices), and the main points of a version once it is installed.
/// </summary>
public partial class MainWindow
{
    private int _guideVersion, _phoneVersion;
    private string? _remoteAddress;
    /// <summary>What « Mira sur ton téléphone » last read, while « Tout préparer » runs, and when it last read.</summary>
    private PhoneSetup.Facts? _phone;
    private bool _preparing, _phoneWatched;
    private DateTime _phoneReadAt;
    /// <summary>
    /// The network Windows would not let « Passer en privé » make private, for this session of Mira, while it is the one
    /// read: the person's to change, which the firewall step then says, rather than Windows' consent asked again at
    /// every click.
    /// </summary>
    private JellyfinFirewall.PublicNetwork? _keptPublic;

    /// <summary>On opening: the welcome on a new profile, the main points after an update, or nothing.</summary>
    private void ShowStartupScreen()
    {
        var current = ReleaseFeed.ParseVersion(JellyfinClient.AppVersion);
        if (current is null || _args.Contains("--demo") || _args.Contains("--autoplay")) { AnnounceUpdateResult(whatsNewShown: false); return; }
        var seen = ReleaseFeed.ParseVersion(_settings.SeenVersion);
        var screen = WhatsNew.Decide(_settings.SeenVersion, current, knownProfile: !_profile.IsNew);
        // Not written now (file held by another program, full disk): the opening goes on, and closing Mira writes it again.
        if (_settings.SeenVersion != current.ToString(3)) { _settings.SeenVersion = current.ToString(3); TrySaveSettings(); }
        if (screen == StartupScreen.Welcome && LoginOverlay.Visibility == Visibility.Visible) ShowWelcome();
        else if (screen == StartupScreen.WhatsNew) ShowWhatsNew(WhatsNew.Since(seen, current));
        AnnounceUpdateResult(whatsNewShown: screen == StartupScreen.WhatsNew);
    }

    // Welcome: what Jellyfin and Mira are, then the three ways in (install, sign in, demo).
    private void ShowWelcome()
    {
        Motion.Reveal(WelcomeOverlay, 260, 0); Motion.Reveal(WelcomeContent, 380, 14);
        _ = Dispatcher.BeginInvoke(() => WelcomeInstall.Focus(), DispatcherPriority.Input);
    }
    private Task LeaveWelcomeAsync() => Motion.HideAsync(WelcomeOverlay);
    private async void WelcomeInstall_Click(object sender, RoutedEventArgs e) { await LeaveWelcomeAsync(); ShowServerSetup(true); }
    private async void WelcomeConnect_Click(object sender, RoutedEventArgs e) { await LeaveWelcomeAsync(); ShowSignInForm(); FocusLogin(); }

    // What's new: the main points of the versions installed since the last one this profile saw.
    private void ShowWhatsNew(IReadOnlyList<ReleaseHighlights> releases)
    {
        if (releases.Count == 0) return;
        var latest = releases[0];
        WhatsNewTitle.Text = $"Mira {latest.Version}"; WhatsNewSummary.Text = latest.Summary;
        WhatsNewItems.Children.Clear();
        foreach (var item in latest.Items) WhatsNewItems.Children.Add(HighlightRow(item));
        // Versions skipped by this update: one line each, never a long list.
        foreach (var release in releases.Skip(1).Take(2))
            WhatsNewItems.Children.Add(new TextBlock { Text = L($"Aussi en {release.Version} : {release.Summary}", $"Also in {release.Version}: {release.Summary}"), FontSize = 12, Foreground = Palette.Get("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        Motion.Reveal(WhatsNewOverlay, 260, 0); Motion.Reveal(WhatsNewCard, 360, 16);
        _ = Dispatcher.BeginInvoke(() => WhatsNewDone.Focus(), DispatcherPriority.Input);
    }
    private UIElement HighlightRow(Highlight item)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var badge = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = Palette.Get("Raised"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0),
            Child = new Icon { Kind = Views.Icon.Draws(item.Icon) ? item.Icon : "sparkle", Width = 20, Height = 20, Foreground = Palette.Get("Text") }
        };
        DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = item.Text, FontSize = 12.5, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 4, 0, 0) });
        row.Children.Add(text);
        return row;
    }
    private async void WhatsNewDone_Click(object sender, RoutedEventArgs e) => await CloseWhatsNewAsync();
    private async Task CloseWhatsNewAsync() { await Motion.HideAsync(WhatsNewOverlay); if (LoginOverlay.Visibility == Visibility.Visible) FocusLogin(); }
    private void WhatsNewDetails_Click(object sender, RoutedEventArgs e) => OpenWebPage($"https://github.com/sasou-web/Mira/releases/tag/v{JellyfinClient.AppVersion}");

    // The guide, from the ? of the rail, F1, an empty library, or once after the first connection.
    private void Guide_Click(object sender, RoutedEventArgs e) => OpenGuide();
    private async void CloseGuide_Click(object sender, RoutedEventArgs e) => await CloseGuideAsync();
    private async Task CloseGuideAsync() { await Motion.HideAsync(GuideOverlay); UpdateNavigation(); UpdateHeroClock(); }
    /// <summary>Once, right after the first connection or the Jellyfin setup, with what is relevant at that moment.</summary>
    private void OfferGuide(string intro) { if (!_settings.GuideSeen) OpenGuide(intro); }
    private async void OpenGuide(string? intro = null)
    {
        if (LoginOverlay.Visibility == Visibility.Visible || WelcomeOverlay.Visibility == Visibility.Visible || _playing && !_miniPlayer) return;
        _settings.GuideSeen = true;
        if (SettingsOverlay.Visibility == Visibility.Visible) { AutoSaveSettings(); _ = Motion.HideAsync(SettingsOverlay); }
        ClosePreview(); ++_detailVersion; _returnToDetail = null; _ = Motion.HideAsync(DetailOverlay); _ = Motion.HideAsync(TorLinkOverlay);
        GuideIntro.Text = intro ?? L("L’essentiel pour bien démarrer.", "The essentials to get you started.");
        GuideTorLink.Visibility = _torlinkEnabled ? Visibility.Visible : Visibility.Collapsed;
        DescribeServerForGuide();
        SmoothScroll.Jump(GuideScroll);
        Motion.Reveal(GuideOverlay, 260, 0); Motion.Reveal(GuideContent, 300, 10);
        UpdateNavigation(); UpdateHeroClock();
        _ = Dispatcher.BeginInvoke(() => GuideClose.Focus(), DispatcherPriority.Input);
        var version = ++_guideVersion;
        // Back from installing Tailscale or from Windows' settings, the steps read again.
        if (!_phoneWatched) { _phoneWatched = true; Activated += (_, _) => { if (GuideOverlay.Visibility == Visibility.Visible && !_preparing && DateTime.UtcNow - _phoneReadAt > TimeSpan.FromSeconds(5)) _ = DescribePhoneAsync(); }; }
        if (!_preparing) _ = DescribePhoneAsync();
        await ShowGuideFoldersAsync(version);
    }
    /// <summary>
    /// « Mira sur ton téléphone »: reads what stands between Jellyfin and a phone (Mira web on Jellyfin, then for a
    /// Jellyfin on this PC: Windows' firewall, Tailscale, the service), then shows it as steps (<see cref="PhoneSetup"/>).
    /// </summary>
    private async Task DescribePhoneAsync()
    {
        var version = ++_phoneVersion;
        _phoneReadAt = DateTime.UtcNow;
        var server = _demo ? null : _client?.Connection.Server;
        if (server is null || _client is not { } client || !Uri.TryCreate(server, UriKind.Absolute, out var uri)) { _phone = null; ShowPhone(null); return; }
        if (_phone?.Server != server) ShowPhone(null, checking: true);
        // Jellyfin on this PC, even reached at this PC's network address: phones get its address here and on Tailscale.
        var local = uri.IsLoopback || await Task.Run(() => LocalNetwork.IsThisPc(uri.Host));
        string? AtThisPc(string? address) => address is null ? null : new UriBuilder(uri) { Host = address }.Uri.GetLeftPart(UriPartial.Authority);
        var home = local ? AtThisPc(await Task.Run(LocalNetwork.ThisPc)) : LocalNetwork.ForOtherDevices(server, null);
        // A Jellyfin restarting or stopped is not one without Mira web: what it says waits until it answers.
        var answering = false;
        try { answering = await client.PingAsync(); } catch (Exception ex) when (IsExpected(ex)) { }
        bool? web = null, allowed = null, admin = null;
        if (answering)
        {
            try { web = await client.WebAppAvailableAsync(); } catch (Exception ex) when (IsExpected(ex)) { }
            try { admin = await client.IsAdministratorAsync(); } catch (Exception ex) when (IsExpected(ex)) { }
            try { allowed = await client.TailnetAllowedAsync(); } catch (Exception ex) when (IsExpected(ex)) { }
        }
        string? tailnet = null, program = null;
        JellyfinFirewall.Reach? firewall = null;
        var rule = JellyfinFirewall.RuleState.Missing;
        JellyfinStartup.State? boot = null;
        TailscaleNode.State? node = null;
        ServerSleep.Plan? power = null;
        if (local)
        {
            (var address, program, firewall, rule, boot, node, power) = await Task.Run(() =>
            {
                var found = JellyfinAutostart.Program();
                var address = LocalNetwork.ThisPcOnTailnet();
                // Tailscale's command line, once it has its address: without a session, and its expiry.
                return (address, found, JellyfinAutostart.Phones(found, uri.Port), JellyfinAutostart.MiraRule(found, uri.Port), JellyfinAutostart.Read(),
                    address is null ? null : TailscaleCli.Read(), PowerPlan.Read());
            });
            tailnet = AtThisPc(address);
        }
        // Silent on this PC: stopped (its button starts it), starting, or stopping after each start.
        var down = local && !answering ? (await ReadLocalJellyfinAsync(server)).Verdict : LocalJellyfin.Verdict.Unknown;
        if (version != _phoneVersion) return;
        _phone = new(server, local, web, home, admin, tailnet, allowed, firewall, rule, boot, Program: program is not null, Answering: answering,
            NetworkKept: firewall?.Public is { } counted && counted == _keptPublic, Down: down, Node: node, Power: power);
        ShowPhone(_phone);
        if (!answering) _ = WaitForJellyfinAsync(client, version);
    }
    /// <summary>
    /// At start, for a Jellyfin on this PC: Mira's firewall rule not as Mira writes it now (an older version's, which lets
    /// public networks in too, or one left on another port) is said, with the way to the guide, where « Tout préparer »
    /// replaces it. Nothing else brings someone set up before back to the card. Said only when no other notice is.
    /// </summary>
    private async Task NoticeStaleRuleAsync()
    {
        if (_demo || _client?.Connection.Server is not { } server || !Uri.TryCreate(server, UriKind.Absolute, out var uri)) return;
        var stale = await Task.Run(() => (uri.IsLoopback || LocalNetwork.IsThisPc(uri.Host)) && JellyfinAutostart.Program() is { } program
            && JellyfinAutostart.MiraRule(program, uri.Port) == JellyfinFirewall.RuleState.Stale);
        if (stale && Notice.Visibility != Visibility.Visible && GuideOverlay.Visibility != Visibility.Visible)
            SetNotice(L("Pour ton téléphone et tes autres ordinateurs, la règle de Mira dans le pare-feu de Windows est à mettre à jour : le guide s’en charge.", "For your phone and your other computers, Mira’s rule in Windows’ firewall needs an update: the guide takes care of it."), L("Ouvrir le guide", "Open the guide"), () => OpenGuide());
    }
    /// <summary>
    /// At start, for a Jellyfin on this PC: this PC's connection to Tailscale expiring within two weeks, or expired, is
    /// said, with Tailscale's page where it stops expiring. Said only when no other notice is.
    /// </summary>
    private async Task NoticeTailscaleExpiryAsync()
    {
        if (_demo || _client?.Connection.Server is not { } server || !Uri.TryCreate(server, UriKind.Absolute, out var uri)) return;
        var node = await Task.Run(() => (uri.IsLoopback || LocalNetwork.IsThisPc(uri.Host)) && LocalNetwork.ThisPcOnTailnet() is not null ? TailscaleCli.Read() : null);
        if (node is not null && TailscaleNode.Warning(node) is { } warning && Notice.Visibility != Visibility.Visible && GuideOverlay.Visibility != Visibility.Visible)
            SetNotice(warning, TailscaleNode.OpenMachines, () => OpenWebPage(TailscaleNode.Machines));
    }
    /// <summary>A Jellyfin restarting (after Mira web's installation) or stopped: the card reads again once it answers.</summary>
    private async Task WaitForJellyfinAsync(JellyfinClient client, int version)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (version != _phoneVersion || _client != client || GuideOverlay.Visibility != Visibility.Visible) return;
            if (_preparing) continue;
            try { if (await client.PingAsync() && version == _phoneVersion && !_preparing) { await DescribePhoneAsync(); return; } }
            catch (Exception ex) when (IsExpected(ex)) { }
        }
    }
    /// <summary>The steps, then « Tout préparer » while one is left, or the QR code once Mira web is there.</summary>
    private void ShowPhone(PhoneSetup.Facts? facts, bool checking = false)
    {
        PhoneSteps.Children.Clear();
        if (facts is null)
        {
            PhoneSteps.Children.Add(new TextBlock { Text = checking ? L("Vérification de ton serveur…", "Checking your server…") : L("Connecte-toi d’abord à Jellyfin : les étapes s’afficheront ici.", "Sign in to Jellyfin first: the steps will show here."), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 0, 0, 12) });
            PhoneActions.Visibility = PhoneStatus.Visibility = PhoneAddressLine.Visibility = Visibility.Collapsed;
            return;
        }
        var steps = PhoneSetup.Steps(facts);
        for (var i = 0; i < steps.Count; i++) PhoneSteps.Children.Add(PhoneStepRow(steps[i], i + 1));
        // Mira's rule in the firewall can be taken back: phones then get in only by rules of someone else's.
        if (facts.Local && facts.MiraRule != JellyfinFirewall.RuleState.Missing)
        {
            var close = new Button
            {
                Content = L("Ne plus ouvrir Jellyfin au réseau", "Close Jellyfin to the network"), Style = (Style)FindResource("Quiet"), Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(38, 0, 0, 14), IsEnabled = !_preparing, ToolTip = L("Retire la règle « Mira - Jellyfin » du pare-feu de Windows, avec son autorisation", "Removes the “Mira - Jellyfin” rule from Windows’ firewall, with its permission")
            };
            close.Click += async (_, _) => await ClosePhoneAsync();
            PhoneSteps.Children.Add(close);
        }
        var prepare = PhoneSetup.CanPrepare(steps);
        var ready = facts.Web == true;
        // A reading that ends while « Tout préparer » runs leaves its button and progress alone.
        if (!_preparing) PhoneMain.Content = prepare ? L("Tout préparer", "Set everything up") : L("Afficher le QR code", "Show the QR code");
        PhoneMain.Visibility = prepare || ready ? Visibility.Visible : Visibility.Collapsed;
        PhoneShare.Visibility = prepare && ready ? Visibility.Visible : Visibility.Collapsed;
        PhoneActions.Visibility = PhoneMain.Visibility;
        var windows = new[] { PhoneSetup.Is(steps, "firewall", PhoneSetup.Mark.ToDo) ? L("le pare-feu", "the firewall") : null, PhoneSetup.Is(steps, "boot", PhoneSetup.Mark.ToDo) ? L("le démarrage de Jellyfin", "Jellyfin’s startup") : null }.OfType<string>().ToList();
        if (!_preparing) ShowPhoneStatus(prepare
            ? windows.Count > 0 ? L($"Une seule autorisation de Windows, pour {string.Join(" et ", windows)}.", $"Just one permission from Windows, for {string.Join(" and ", windows)}.") : ""
            : ready ? PhoneSetup.Everywhere(facts) is not null
                ? L("Le QR code marche chez toi et ailleurs, Tailscale activé sur ton téléphone.", "The QR code works at home and away, with Tailscale active on your phone.")
                // A firewall step left to the person (a public network, « block all »…) keeps the phone out at home.
                : FirewallDone(steps) ? L("Le QR code marche sur le Wi-Fi de la maison.", "The QR code works on your home Wi-Fi.") : L("Chez toi, ton téléphone trouvera Jellyfin une fois l’étape du pare-feu faite.", "At home, your phone will find Jellyfin once the firewall step is done.") : "");
        PhoneAddress.Text = PhoneSetup.Address(facts);
        PhoneAddressLine.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }
    private static bool FirewallDone(IReadOnlyList<PhoneSetup.Step> steps) => steps.All(x => x.Key != "firewall" || x.Mark == PhoneSetup.Mark.Done);
    private void ShowPhoneStatus(string text, bool problem = false)
    {
        PhoneStatus.Text = text;
        PhoneStatus.Foreground = problem ? Palette.Get("Danger") : (Brush)FindResource("Muted");
        PhoneStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>A step: a check once done, its number while to do, a dot for a note; its own button for what Mira cannot do.</summary>
    private UIElement PhoneStepRow(PhoneSetup.Step step, int number)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var done = step.Mark == PhoneSetup.Mark.Done;
        var badge = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
            Background = done ? Palette.Get("Success") : Brushes.Transparent, BorderThickness = new Thickness(done ? 0 : 1.5),
            BorderBrush = Palette.Get(step.Mark == PhoneSetup.Mark.ToDo ? "TextBody" : "LineStrong"),
            Child = step.Mark switch
            {
                PhoneSetup.Mark.Done => new Icon { Kind = "check", Width = 13, Height = 13, Foreground = Palette.Get("OnAccent") },
                PhoneSetup.Mark.Note => new Ellipse { Width = 6, Height = 6, Fill = Palette.Get("Dim") },
                _ => new TextBlock { Text = number.ToString(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge);
        if (step.Action is { } action)
        {
            var button = new Button { Content = action, Style = (Style)FindResource("Quiet"), Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            button.Click += async (_, _) =>
            {
                if (step.Key == "tailscale") OpenWebPage("https://tailscale.com/download/windows");
                else if (step.Key == "web" && action == LocalJellyfin.Start && _client is { } client) { button.IsEnabled = false; await StartJellyfinAsync(client); }
                else if (step.Key == "web" && action == LocalJellyfin.OpenLog) { OpenJellyfinLog(); _ = DescribePhoneAsync(); }
                // « Run unattended », as Tailscale's menu does; where to tick it when Tailscale does not take it.
                else if (step.Key == "unattended")
                {
                    button.IsEnabled = false;
                    if (!await Task.Run(TailscaleCli.KeepConnected)) SetNotice(L($"Tailscale n’a pas pris ce réglage : {TailscaleNode.WhereUnattended}.", $"Tailscale didn’t take this setting: {TailscaleNode.WhereUnattended}."));
                    if (!_preparing) _ = DescribePhoneAsync();
                }
                else if (step.Key == "keyexpiry") OpenWebPage(TailscaleNode.Machines);
                // « Pas de veille sur secteur », on mains power only: where to set it when Windows does not take it.
                else if (step.Key == "sleep")
                {
                    button.IsEnabled = false;
                    if (!await Task.Run(PowerPlan.NeverOnMains))
                        SetNotice(L("Windows n’a pas changé la mise en veille : dans Paramètres → Système → Alimentation, choisis « Jamais » pour la mise en veille sur secteur.", "Windows didn’t change sleep: in Settings → System → Power, choose “Never” for sleep when plugged in."));
                    if (!_preparing) _ = DescribePhoneAsync();
                }
                // « Passer en privé »: the person says the public network is home's.
                else if (step.Key == "firewall") await PreparePhoneAsync(privateNetwork: true);
            };
            DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
        }
        var text = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(new TextBlock { Text = step.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Get(done ? "TextSecondary" : "Text") });
        if (step.Text.Length > 0) text.Children.Add(new TextBlock { Text = step.Text, FontSize = 12, Foreground = Palette.Get("Faint"), TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 3, 0, 0) });
        row.Children.Add(text);
        return row;
    }
    /// <summary>
    /// « Tout préparer »: every step Mira can do, in one go. Windows' consent first, while the person is looking (the
    /// firewall, and the service when there is one), then Tailscale in Jellyfin, then Mira web, whose installation
    /// restarts Jellyfin. Then the QR code. Once there is nothing left to prepare, the same button shows the QR code.
    /// </summary>
    private async void PhoneMain_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is not { } facts) return;
        if (!PhoneSetup.CanPrepare(PhoneSetup.Steps(facts))) { OpenPhoneShare(); return; }
        await PreparePhoneAsync(privateNetwork: false);
    }
    /// <summary>
    /// « Tout préparer », and with <paramref name="privateNetwork"/> (« Passer en privé », the person saying the network
    /// Windows counts as public is home's) that network made private too, with the same consent.
    /// </summary>
    private async Task PreparePhoneAsync(bool privateNetwork)
    {
        if (_preparing || _phone is not { } facts || _client is not { } client || !Uri.TryCreate(facts.Server, UriKind.Absolute, out var uri)) return;
        var steps = PhoneSetup.Steps(facts);
        var problems = new List<string>();
        string? waiting = null;
        var kept = false;
        _preparing = true; PhoneMain.IsEnabled = PhoneShare.IsEnabled = false; PhoneMain.Content = L("Préparation…", "Setting up…");
        try
        {
            if (privateNetwork || PhoneSetup.NeedsWindows(steps))
            {
                // Windows asks for Windows PowerShell, which does it: never a file of Mira's own folder.
                ShowPhoneStatus(L("Accepte l’autorisation de Windows, pour Windows PowerShell…", "Accept Windows’ permission prompt for Windows PowerShell…"));
                switch (await JellyfinAutostart.ApplyAsync(uri.Port, privateNetwork))
                {
                    case AutostartResult.Refused: problems.Add(L("Windows n’a pas reçu l’autorisation : le pare-feu n’a pas changé.", "Windows didn’t get permission: the firewall hasn’t changed.")); break;
                    case AutostartResult.Failed: problems.Add(L("Le pare-feu ou le démarrage de Jellyfin n’a pas pu être réglé. Réessaie.", "The firewall or Jellyfin’s startup couldn’t be set up. Try again.")); break;
                    case AutostartResult.KeptPublic: kept = true; problems.Add(L("Windows garde ton réseau en public : l’étape du pare-feu dit où le passer en privé.", "Windows keeps your network public: the firewall step says where to make it private.")); break;
                }
            }
            if (PhoneSetup.Is(steps, "tailscale", PhoneSetup.Mark.ToDo))
            {
                ShowPhoneStatus(L("Jellyfin accepte tes appareils Tailscale…", "Letting your Tailscale devices into Jellyfin…"));
                try { if (!await client.AllowTailnetAsync()) problems.Add(L("Il faut le compte administrateur de Jellyfin pour autoriser Tailscale.", "Allowing Tailscale needs Jellyfin’s administrator account.")); }
                catch (Exception ex) when (IsExpected(ex)) { problems.Add(Friendly(ex)); }
            }
            if (PhoneSetup.Is(steps, "web", PhoneSetup.Mark.ToDo))
            {
                ShowPhoneStatus(L("Vérification de Mira web…", "Checking Mira web…"));
                try
                {
                    // Checked against this version's signed release before Jellyfin is pointed at it: nothing otherwise.
                    var repository = await VerifiedMiraWebAsync();
                    ShowPhoneStatus(L("Installation de Mira web, puis Jellyfin redémarre quelques secondes…", "Installing Mira web, then Jellyfin restarts for a few seconds…"));
                    switch (await client.InstallWebAppAsync(repository))
                    {
                        case WebAppInstall.NotAllowed: problems.Add(L("Il faut le compte administrateur de Jellyfin pour installer Mira web.", "Installing Mira web needs Jellyfin’s administrator account.")); break;
                        case WebAppInstall.Unavailable: problems.Add(L("Jellyfin n’a pas pu télécharger Mira web : vérifie que ce PC a accès à Internet, puis réessaie.", "Jellyfin couldn’t download Mira web: check that this PC can reach the Internet, then try again.")); break;
                        case WebAppInstall.Restarting: waiting = L("Mira web est installé ; Jellyfin finit de redémarrer, et cette carte se met à jour dès qu’il répond.", "Mira web is installed; Jellyfin is finishing its restart, and this card updates as soon as it answers."); break;
                    }
                }
                catch (UpdateException ex) { problems.Add(ex.Message); }
                catch (Exception ex) when (IsExpected(ex)) { problems.Add(Friendly(ex)); }
            }
            // Read again before anything else may: a reading started meanwhile (Mira's window back to the front) would
            // leave this one unfinished.
            await DescribePhoneAsync();
            // The network Windows kept public, as read now: of two counted public (the home network's and Tailscale's),
            // one may have been made private.
            if (kept && _phone?.Firewall?.Public is { Known: true } left) { _keptPublic = left; _phone = _phone with { NetworkKept = true }; }
        }
        finally { _preparing = false; PhoneMain.IsEnabled = PhoneShare.IsEnabled = true; }
        if (_phone is { } read) ShowPhone(read);
        if (problems.Count > 0) { ShowPhoneStatus(string.Join(" ", problems), problem: true); SetNotice(problems[0]); return; }
        if (waiting is not null) { ShowPhoneStatus(waiting); return; }
        // A firewall step left to the person keeps the phone out at home, unless Tailscale lets it in: the card says so,
        // rather than a QR code that leads nowhere.
        if (_phone is not { Web: true } now || PhoneSetup.Everywhere(now) is null && !FirewallDone(PhoneSetup.Steps(now))) return;
        SetNotice(L("C’est prêt : scanne le QR code avec ton téléphone.", "It’s ready: scan the QR code with your phone."));
        OpenPhoneShare();
    }
    /// <summary>
    /// « Ne plus ouvrir Jellyfin au réseau »: Mira's firewall rule removed, with Windows' consent; the block rules Mira
    /// lifted and the service's settings stay. « Tout préparer » puts the rule back.
    /// </summary>
    private async Task ClosePhoneAsync()
    {
        if (_preparing || _phone is not { } facts || !Uri.TryCreate(facts.Server, UriKind.Absolute, out var uri)) return;
        string? problem = null;
        _preparing = true; PhoneMain.IsEnabled = PhoneShare.IsEnabled = false;
        try
        {
            ShowPhoneStatus(L("Accepte l’autorisation de Windows, pour Windows PowerShell…", "Accept Windows’ permission prompt for Windows PowerShell…"));
            problem = await JellyfinAutostart.CloseAsync(uri.Port) switch
            {
                AutostartResult.Done => null,
                AutostartResult.Refused => L("Windows n’a pas reçu l’autorisation : le pare-feu n’a pas changé.", "Windows didn’t get permission: the firewall hasn’t changed."),
                _ => L("La règle de Mira n’a pas pu être retirée du pare-feu. Réessaie.", "Mira’s rule couldn’t be removed from the firewall. Try again."),
            };
            await DescribePhoneAsync();
        }
        finally { _preparing = false; PhoneMain.IsEnabled = PhoneShare.IsEnabled = true; }
        if (_phone is { } read) ShowPhone(read);
        if (problem is not null) { ShowPhoneStatus(problem, problem: true); SetNotice(problem); return; }
        SetNotice(L("Mira n’ouvre plus Jellyfin au réseau : sa règle est retirée du pare-feu de Windows.", "Mira no longer opens Jellyfin to the network: its rule is removed from Windows’ firewall."));
    }
    private void PhoneShare_Click(object sender, RoutedEventArgs e) => OpenPhoneShare();
    /// <summary>The page with the QR code, in this PC's browser: for the address that works everywhere when there is one.</summary>
    private void OpenPhoneShare() { if (_phone is { } facts) OpenWebPage(PhoneSetup.SharePage(facts)); }
    private void CopyPhoneAddress_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is not { } facts) return;
        try { Clipboard.SetText(PhoneSetup.Address(facts)); SetDone(L("Adresse copiée.", "Address copied.")); }
        catch (System.Runtime.InteropServices.ExternalException) { SetNotice(L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again.")); }
    }
    /// <summary>Jellyfin's page and the address for TVs and consoles: this PC's network address when Jellyfin runs here.</summary>
    private void DescribeServerForGuide()
    {
        var server = _demo ? null : _client?.Connection.Server;
        GuideJellyfinActions.Visibility = server is null ? Visibility.Collapsed : Visibility.Visible;
        GuideServerAddress.Text = server?.TrimEnd('/') ?? "";
        _remoteAddress = server is null ? null : LocalNetwork.ForOtherDevices(server, LocalNetwork.ThisPc());
        GuideRemote.Visibility = _remoteAddress is null ? Visibility.Collapsed : Visibility.Visible;
        GuideRemoteAddress.Text = _remoteAddress ?? "";
    }
    /// <summary>The folders of each Jellyfin library, with a button to open them when they are on this PC.</summary>
    private async Task ShowGuideFoldersAsync(int version)
    {
        GuideFolders.Children.Clear(); GuideFoldersHint.Visibility = Visibility.Collapsed;
        if (_demo || _client is not { } client)
        {
            ShowGuideFoldersHint(L("En démonstration, aucun serveur n’est relié : connecte-toi pour voir tes dossiers ici.", "In the demo, no server is linked: sign in to see your folders here."));
            return;
        }
        List<VirtualFolder>? libraries; Exception? failure = null;
        try { libraries = (await client.VirtualFoldersAsync())?.Where(x => !LibrarySection.Groups(x.CollectionType)).ToList(); }
        catch (Exception ex) when (IsExpected(ex)) { libraries = null; failure = ex; }
        if (version != _guideVersion) return;
        // Jellyfin out of reach is not a question of rights: only a refusal (null) means an account that is not an administrator.
        if (failure is not null) { ShowGuideFoldersHint(Friendly(failure) + L(" Tes dossiers s’afficheront ici dès qu’il répondra.", " Your folders will show here as soon as it answers.")); return; }
        if (libraries is null) { ShowGuideFoldersHint(L("Seul un administrateur de Jellyfin voit ces dossiers : demande-les à la personne qui gère le serveur.", "Only a Jellyfin administrator sees these folders: ask the person who runs the server.")); return; }
        if (libraries.Count == 0) { ShowGuideFoldersHint(L("Aucune bibliothèque : ajoutes-en une dans Jellyfin (Tableau de bord → Bibliothèques).", "No libraries yet: add one in Jellyfin (Dashboard → Libraries).")); return; }
        var reachable = await Task.Run(() => libraries.SelectMany(x => x.Locations).Distinct().ToDictionary(x => x, Exists));
        if (version != _guideVersion) return;
        foreach (var library in libraries.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            foreach (var location in library.Locations.DefaultIfEmpty(""))
                GuideFolders.Children.Add(FolderRow(library.Name, location, location.Length > 0 && reachable.GetValueOrDefault(location)));
        if (!reachable.Values.Any(x => x))
            ShowGuideFoldersHint(L("Ces dossiers sont sur l’ordinateur où tourne Jellyfin.", "These folders are on the computer where Jellyfin runs."));
    }
    /// <summary>Two columns of cards on a wide window, one on a narrow one.</summary>
    private void GuideGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = GuideGrid.ActualWidth;
        if (width > 0) GuideGrid.ItemWidth = width >= 1000 ? Math.Floor(width / 2) : width;
    }
    private void ShowGuideFoldersHint(string text) { GuideFoldersHint.Text = text; GuideFoldersHint.Visibility = Visibility.Visible; }
    private UIElement FolderRow(string library, string location, bool here)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (here)
        {
            var open = new Button { Content = L("Ouvrir", "Open"), Padding = new Thickness(14, 6, 14, 6), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(open, L($"Ouvrir le dossier {library}", $"Open the {library} folder"));
            open.Click += (_, _) => OpenInExplorer(location, null);
            DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = library, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        text.Children.Add(new TextBlock { Text = location.Length > 0 ? location : L("Aucun dossier pour l’instant", "No folder yet"), FontSize = 12, Foreground = Palette.Get("Faint"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = location.Length > 0 ? location : null });
        row.Children.Add(text);
        return row;
    }
    private static bool Exists(string path)
    {
        try { return path.Length > 0 && Directory.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    private void CopyRemoteAddress_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteAddress is null) return;
        try { Clipboard.SetText(_remoteAddress); SetDone(L("Adresse copiée.", "Address copied.")); }
        catch (System.Runtime.InteropServices.ExternalException) { SetNotice(L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again.")); }
    }
    /// <summary>Jellyfin's own web page, signed in with the same account: its dashboard holds the libraries, users and network.</summary>
    private void OpenJellyfin_Click(object sender, RoutedEventArgs e)
    {
        if (_demo || _client?.Connection.Server is not { } server) { SetNotice(L("Connecte-toi d’abord à ton serveur Jellyfin.", "Sign in to your Jellyfin server first.")); return; }
        OpenWebPage(server.TrimEnd('/') + "/web/#/dashboard");
    }
    private void OpenWebPage(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { SetNotice(L("Le navigateur n’a pas pu s’ouvrir.", "The browser couldn’t open.")); }
    }
}
