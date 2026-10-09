using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Help for someone new: the welcome on a profile Mira has never opened, the guide (where videos go, Jellyfin's own
/// page, other devices), and the main points of a version once it is installed.
/// </summary>
public partial class MainWindow
{
    private int _guideVersion;
    private string? _remoteAddress, _awayAddress, _awayAction, _webState;

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
        _ = DescribeAwayAsync(version);
        _ = DescribeWebAsync(version);
        await ShowGuideFoldersAsync(version);
    }
    /// <summary>
    /// « Hors de chez toi », for a Jellyfin on this PC: the home address only works on the home network. With Tailscale
    /// connected here, its address, once Jellyfin lets Tailscale's devices in; otherwise, how to get one.
    /// </summary>
    private async Task DescribeAwayAsync(int version)
    {
        var server = _demo ? null : _client?.Connection.Server;
        var local = server is not null && Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.IsLoopback;
        GuideAway.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        if (!local || _client is not { } client) return;
        var tailnet = await Task.Run(LocalNetwork.ThisPcOnTailnet);
        if (version != _guideVersion) return;
        _awayAddress = tailnet is null ? null : LocalNetwork.ForOtherDevices(server!, tailnet);
        bool? allowed = null;
        if (_awayAddress is not null)
        {
            try { allowed = await client.TailnetAllowedAsync(); }
            catch (Exception ex) when (IsExpected(ex)) { }
        }
        if (version == _guideVersion) ShowAway(allowed);
    }
    /// <summary>Without Tailscale: how to get it. With it: its address, and whether Jellyfin still has to let it in.</summary>
    private void ShowAway(bool? allowed)
    {
        GuideAwayLine.Visibility = _awayAddress is null ? Visibility.Collapsed : Visibility.Visible;
        GuideAwayAddress.Text = _awayAddress ?? "";
        (GuideAwayHint.Text, _awayAction) = (_awayAddress, allowed) switch
        {
            (null, _) => ("L’adresse ci-dessus ne marche que chez toi. Pour regarder ailleurs sans ouvrir ta box à Internet, installe Tailscale (gratuit) sur ce PC et sur l’appareil, avec le même compte.", "install"),
            (_, false) => ("Jellyfin refuse encore les appareils Tailscale. Autorise-les : le reste d’Internet reste bloqué.", "allow"),
            (_, null) => ("Seul un administrateur de Jellyfin peut autoriser les appareils Tailscale.", null),
            _ => ("Tailscale doit aussi être ouvert sur l’appareil, avec le même compte.", null)
        };
        GuideAwayAction.Content = _awayAction == "install" ? "Installer Tailscale" : "Autoriser Tailscale";
        GuideAwayAction.Visibility = _awayAction is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void GuideAwayAction_Click(object sender, RoutedEventArgs e)
    {
        if (_awayAction == "install") { OpenWebPage("https://tailscale.com/download/windows"); return; }
        if (_awayAction != "allow" || _client is not { } client) return;
        GuideAwayAction.IsEnabled = false;
        try
        {
            var allowed = await client.AllowTailnetAsync();
            ShowAway(allowed ? true : null);
            if (allowed) SetNotice("Jellyfin accepte maintenant tes appareils Tailscale.");
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
        finally { GuideAwayAction.IsEnabled = true; }
    }
    /// <summary>« Mira sur iPhone et Android »: Mira web on this Jellyfin, or the button that adds it.</summary>
    private async Task DescribeWebAsync(int version)
    {
        var server = _demo ? null : _client?.Connection.Server;
        GuideWeb.Visibility = server is null ? Visibility.Collapsed : Visibility.Visible;
        if (server is null || _client is not { } client || _webState == "installing") return;
        var ready = await client.WebAppAvailableAsync();
        if (version == _guideVersion) ShowWeb(ready ? "ready" : "install");
    }
    private void ShowWeb(string state)
    {
        _webState = state;
        (GuideWebHint.Text, var action) = state switch
        {
            "ready" => ("Ouvre Mira dans Safari ou Chrome sur ton téléphone, puis ajoute-la à l’écran d’accueil : elle s’ouvre comme une app.", "Afficher le QR code"),
            "installing" => ("Installation de Mira web dans Jellyfin, qui redémarre ensuite quelques secondes…", "Installation…"),
            "admin" => ("Seul un administrateur de Jellyfin peut ajouter Mira web : connecte Mira avec son compte, ou installe l’extension « Mira » depuis le tableau de bord de Jellyfin.", null),
            "unavailable" => ("Jellyfin n’a pas pu télécharger Mira web. Vérifie que ce PC a accès à Internet, puis réessaie.", "Réessayer"),
            _ => ("Ajoute Mira à ton serveur Jellyfin : ton téléphone l’ouvre ensuite comme une app, sans rien installer ni compte Apple.", "Installer Mira web")
        };
        GuideWebAction.Content = action;
        GuideWebAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        GuideWebAction.IsEnabled = state != "installing";
    }
    private async void GuideWebAction_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client) return;
        // Phones reach Jellyfin at this PC's address on the home network, not at 127.0.0.1.
        var share = MiraWeb.SharePage(_remoteAddress ?? client.Connection.Server);
        if (_webState == "ready") { OpenWebPage(share); return; }
        ShowWeb("installing");
        try
        {
            switch (await client.InstallWebAppAsync())
            {
                case WebAppInstall.Ready:
                    ShowWeb("ready");
                    SetNotice("Mira web est prêt : scanne le QR code avec ton téléphone.");
                    OpenWebPage(share);
                    break;
                case WebAppInstall.Restarting:
                    ShowWeb("ready");
                    SetNotice("Mira web est installé ; Jellyfin redémarre encore. Le QR code s’affichera dans un instant.");
                    break;
                case WebAppInstall.NotAllowed: ShowWeb("admin"); break;
                default: ShowWeb("unavailable"); break;
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowWeb("unavailable"); SetNotice(Friendly(ex)); }
    }
    private void CopyAwayAddress_Click(object sender, RoutedEventArgs e)
    {
        if (_awayAddress is null) return;
        try { Clipboard.SetText(_awayAddress); SetNotice("Adresse copiée."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetNotice("Le presse-papiers est occupé : réessaie."); }
    }
    /// <summary>Jellyfin's page and the address for other devices: this PC's network address when Jellyfin runs here.</summary>
    private void DescribeServerForGuide()
    {
        var server = _demo ? null : _client?.Connection.Server;
        GuideJellyfinActions.Visibility = server is null ? Visibility.Collapsed : Visibility.Visible;
        GuideServerAddress.Text = server?.TrimEnd('/') ?? "";
        _remoteAddress = server is null ? null : LocalNetwork.ForOtherDevices(server, LocalNetwork.ThisPc());
        GuideRemote.Visibility = _remoteAddress is null ? Visibility.Collapsed : Visibility.Visible;
        GuideRemoteAddress.Text = _remoteAddress ?? "";
        var local = server is not null && Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.IsLoopback;
        GuideRemoteHint.Text = server is null
            ? "Connecte-toi d’abord à Jellyfin : l’adresse s’affichera ici."
            : _remoteAddress is null
                ? "Connecte ce PC à ta box (Wi-Fi ou câble), puis rouvre ce guide."
                : local
                    ? "Ce PC doit rester allumé et sur la même box. Si un appareil ne trouve pas le serveur, autorise Jellyfin dans le pare-feu de Windows."
                    : "Puis connecte-toi avec ton compte Jellyfin.";
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
