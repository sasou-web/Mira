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

    /// <summary>On opening: the welcome on a new profile, the main points after an update, or nothing.</summary>
    private void ShowStartupScreen()
    {
        var current = ReleaseFeed.ParseVersion(JellyfinClient.AppVersion);
        if (current is null || _args.Contains("--demo") || _args.Contains("--autoplay")) { AnnounceUpdateResult(whatsNewShown: false); return; }
        var seen = ReleaseFeed.ParseVersion(_settings.SeenVersion);
        var screen = WhatsNew.Decide(_settings.SeenVersion, current, knownProfile: !_profile.IsNew);
        if (_settings.SeenVersion != current.ToString(3)) { _settings.SeenVersion = current.ToString(3); _profile.SaveSettings(_settings); }
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
    private async void WelcomeDemo_Click(object sender, RoutedEventArgs e) { await LeaveWelcomeAsync(); await ShowDemoAsync(); }

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
            WhatsNewItems.Children.Add(new TextBlock { Text = $"Aussi en {release.Version} : {release.Summary}", FontSize = 12, Foreground = Brush("#8E8E96"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        Motion.Reveal(WhatsNewOverlay, 260, 0); Motion.Reveal(WhatsNewCard, 360, 16);
        _ = Dispatcher.BeginInvoke(() => WhatsNewDone.Focus(), DispatcherPriority.Input);
    }
    private UIElement HighlightRow(Highlight item)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var badge = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = Brush("#1B1B1F"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0),
            Child = new Icon { Kind = Views.Icon.Draws(item.Icon) ? item.Icon : "sparkle", Width = 20, Height = 20, Foreground = Brush("#F5F5F7") }
        };
        DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = item.Text, FontSize = 12.5, Foreground = Brush("#A2A2A6"), TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 4, 0, 0) });
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
        GuideIntro.Text = intro ?? "L’essentiel pour bien démarrer.";
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
        JellyfinStartup.State? boot = null;
        if (local)
        {
            (var address, program, firewall, boot) = await Task.Run(() =>
            {
                var found = JellyfinAutostart.Program();
                return (LocalNetwork.ThisPcOnTailnet(), found, JellyfinAutostart.Phones(found, uri.Port), JellyfinAutostart.Read());
            });
            tailnet = AtThisPc(address);
        }
        if (version != _phoneVersion) return;
        _phone = new(server, local, web, home, admin, tailnet, allowed, firewall, boot?.FirewallOpen ?? false, boot, Program: program is not null, Answering: answering);
        ShowPhone(_phone);
        if (!answering) _ = WaitForJellyfinAsync(client, version);
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
            PhoneSteps.Children.Add(new TextBlock { Text = checking ? "Vérification de ton serveur…" : "Connecte-toi d’abord à Jellyfin : les étapes s’afficheront ici.", Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 0, 0, 12) });
            PhoneActions.Visibility = PhoneStatus.Visibility = PhoneAddressLine.Visibility = Visibility.Collapsed;
            return;
        }
        var steps = PhoneSetup.Steps(facts);
        for (var i = 0; i < steps.Count; i++) PhoneSteps.Children.Add(PhoneStepRow(steps[i], i + 1));
        var prepare = PhoneSetup.CanPrepare(steps);
        var ready = facts.Web == true;
        // A reading that ends while « Tout préparer » runs leaves its button and progress alone.
        if (!_preparing) PhoneMain.Content = prepare ? "Tout préparer" : "Afficher le QR code";
        PhoneMain.Visibility = prepare || ready ? Visibility.Visible : Visibility.Collapsed;
        PhoneShare.Visibility = prepare && ready ? Visibility.Visible : Visibility.Collapsed;
        PhoneActions.Visibility = PhoneMain.Visibility;
        var windows = new[] { PhoneSetup.Is(steps, "firewall", PhoneSetup.Mark.ToDo) ? "le pare-feu" : null, PhoneSetup.Is(steps, "boot", PhoneSetup.Mark.ToDo) ? "le démarrage de Jellyfin" : null }.OfType<string>().ToList();
        if (!_preparing) ShowPhoneStatus(prepare
            ? windows.Count > 0 ? $"Une seule autorisation de Windows, pour {string.Join(" et ", windows)}." : ""
            : ready ? PhoneSetup.Everywhere(facts) is not null
                ? "Le QR code marche chez toi et ailleurs, Tailscale activé sur ton téléphone."
                : "Le QR code marche sur le Wi-Fi de la maison." : "");
        PhoneAddress.Text = PhoneSetup.Address(facts);
        PhoneAddressLine.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowPhoneStatus(string text, bool problem = false)
    {
        PhoneStatus.Text = text;
        PhoneStatus.Foreground = problem ? Brush("#F3AD99") : (Brush)FindResource("Muted");
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
            Background = done ? Brush("#86C6A2") : Brushes.Transparent, BorderThickness = new Thickness(done ? 0 : 1.5),
            BorderBrush = Brush(step.Mark == PhoneSetup.Mark.ToDo ? "#E4E4E8" : "#4A4A50"),
            Child = step.Mark switch
            {
                PhoneSetup.Mark.Done => new Icon { Kind = "check", Width = 13, Height = 13, Foreground = Brush("#0E1A13") },
                PhoneSetup.Mark.Note => new Ellipse { Width = 6, Height = 6, Fill = Brush("#77777F") },
                _ => new TextBlock { Text = number.ToString(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge);
        if (step.Action is { } action)
        {
            var button = new Button { Content = action, Style = (Style)FindResource("Quiet"), Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            button.Click += (_, _) => { if (step.Key == "tailscale") OpenWebPage("https://tailscale.com/download/windows"); };
            DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
        }
        var text = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(new TextBlock { Text = step.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Brush(done ? "#C5C5CA" : "#F5F5F7") });
        if (step.Text.Length > 0) text.Children.Add(new TextBlock { Text = step.Text, FontSize = 12, Foreground = Brush("#8E8E96"), TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 3, 0, 0) });
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
        if (_phone is not { } facts || _client is not { } client || !Uri.TryCreate(facts.Server, UriKind.Absolute, out var uri)) return;
        var steps = PhoneSetup.Steps(facts);
        if (!PhoneSetup.CanPrepare(steps)) { OpenPhoneShare(); return; }
        var problems = new List<string>();
        string? waiting = null;
        _preparing = true; PhoneMain.IsEnabled = PhoneShare.IsEnabled = false; PhoneMain.Content = "Préparation…";
        try
        {
            if (PhoneSetup.NeedsWindows(steps))
            {
                ShowPhoneStatus("Accepte l’autorisation de Windows…");
                switch (await JellyfinAutostart.ApplyAsync(uri.Port))
                {
                    case AutostartResult.Refused: problems.Add("Windows n’a pas reçu l’autorisation : le pare-feu n’a pas changé."); break;
                    case AutostartResult.Failed: problems.Add("Le pare-feu ou le démarrage de Jellyfin n’a pas pu être réglé. Réessaie."); break;
                }
            }
            if (PhoneSetup.Is(steps, "tailscale", PhoneSetup.Mark.ToDo))
            {
                ShowPhoneStatus("Jellyfin accepte tes appareils Tailscale…");
                try { if (!await client.AllowTailnetAsync()) problems.Add("Il faut le compte administrateur de Jellyfin pour autoriser Tailscale."); }
                catch (Exception ex) when (IsExpected(ex)) { problems.Add(Friendly(ex)); }
            }
            if (PhoneSetup.Is(steps, "web", PhoneSetup.Mark.ToDo))
            {
                ShowPhoneStatus("Installation de Mira web, puis Jellyfin redémarre quelques secondes…");
                try
                {
                    switch (await client.InstallWebAppAsync())
                    {
                        case WebAppInstall.NotAllowed: problems.Add("Il faut le compte administrateur de Jellyfin pour installer Mira web."); break;
                        case WebAppInstall.Unavailable: problems.Add("Jellyfin n’a pas pu télécharger Mira web : vérifie que ce PC a accès à Internet, puis réessaie."); break;
                        case WebAppInstall.Restarting: waiting = "Mira web est installé ; Jellyfin finit de redémarrer, et cette carte se met à jour dès qu’il répond."; break;
                    }
                }
                catch (Exception ex) when (IsExpected(ex)) { problems.Add(Friendly(ex)); }
            }
            // Read again before anything else may: a reading started meanwhile (Mira's window back to the front) would
            // leave this one unfinished.
            await DescribePhoneAsync();
        }
        finally { _preparing = false; PhoneMain.IsEnabled = PhoneShare.IsEnabled = true; }
        if (_phone is { } read) ShowPhone(read);
        if (problems.Count > 0) { ShowPhoneStatus(string.Join(" ", problems), problem: true); SetNotice(problems[0]); return; }
        if (waiting is not null) { ShowPhoneStatus(waiting); return; }
        if (_phone?.Web != true) return;
        SetNotice("C’est prêt : scanne le QR code avec ton téléphone.");
        OpenPhoneShare();
    }
    private void PhoneShare_Click(object sender, RoutedEventArgs e) => OpenPhoneShare();
    /// <summary>The page with the QR code, in this PC's browser: for the address that works everywhere when there is one.</summary>
    private void OpenPhoneShare() { if (_phone is { } facts) OpenWebPage(PhoneSetup.SharePage(facts)); }
    private void CopyPhoneAddress_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is not { } facts) return;
        try { Clipboard.SetText(PhoneSetup.Address(facts)); SetNotice("Adresse copiée."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetNotice("Le presse-papiers est occupé : réessaie."); }
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
            ShowGuideFoldersHint("En démonstration, aucun serveur n’est relié : connecte-toi pour voir tes dossiers ici.");
            return;
        }
        List<VirtualFolder>? libraries;
        try { libraries = await client.VirtualFoldersAsync(); }
        catch (Exception ex) when (IsExpected(ex)) { libraries = null; }
        if (version != _guideVersion) return;
        if (libraries is null) { ShowGuideFoldersHint("Seul un administrateur de Jellyfin voit ces dossiers : demande-les à la personne qui gère le serveur."); return; }
        if (libraries.Count == 0) { ShowGuideFoldersHint("Aucune bibliothèque : ajoutes-en une dans Jellyfin (Tableau de bord → Bibliothèques)."); return; }
        var reachable = await Task.Run(() => libraries.SelectMany(x => x.Locations).Distinct().ToDictionary(x => x, Exists));
        if (version != _guideVersion) return;
        foreach (var library in libraries.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            foreach (var location in library.Locations.DefaultIfEmpty(""))
                GuideFolders.Children.Add(FolderRow(library.Name, location, location.Length > 0 && reachable.GetValueOrDefault(location)));
        if (!reachable.Values.Any(x => x))
            ShowGuideFoldersHint("Ces dossiers sont sur l’ordinateur où tourne Jellyfin.");
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
            var open = new Button { Content = "Ouvrir", Padding = new Thickness(14, 6, 14, 6), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(open, $"Ouvrir le dossier {library}");
            open.Click += (_, _) => OpenInExplorer(location, null);
            DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = library, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        text.Children.Add(new TextBlock { Text = location.Length > 0 ? location : "Aucun dossier pour l’instant", FontSize = 12, Foreground = Brush("#8E8E96"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = location.Length > 0 ? location : null });
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
        try { Clipboard.SetText(_remoteAddress); SetNotice("Adresse copiée."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetNotice("Le presse-papiers est occupé : réessaie."); }
    }
    /// <summary>Jellyfin's own web page, signed in with the same account: its dashboard holds the libraries, users and network.</summary>
    private void OpenJellyfin_Click(object sender, RoutedEventArgs e)
    {
        if (_demo || _client?.Connection.Server is not { } server) { SetNotice("Connecte-toi d’abord à ton serveur Jellyfin."); return; }
        OpenWebPage(server.TrimEnd('/') + "/web/#/dashboard");
    }
    private void OpenWebPage(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { SetNotice("Le navigateur n’a pas pu s’ouvrir."); }
    }
}
