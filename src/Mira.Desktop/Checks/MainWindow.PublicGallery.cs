using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Core.Updates;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    // Render real views with the built-in fictional catalogue; no connection is used.
    private async Task RunPublicGalleryAsync()
    {
        var index = Array.IndexOf(_args, "--public-gallery");
        var output = Path.GetFullPath(_args[index + 1]); Directory.CreateDirectory(output);
        try
        {
            if (!_demo || _client is not null) throw new InvalidOperationException("Public captures require an offline demo.");
            _fallbackRefresh.Stop(); _searchTimer.Stop(); Width = 1440; Height = 960;
            BrandAssets.Export(output);
            async Task Page(string view, string name, bool favorites = false)
            {
                SettingsOverlay.Visibility = DetailOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
                ResetFilters(); _catalog.View = view; _catalog.Favorites = favorites; ShowFavoritesFilter(); RenderDemo(); SmoothScroll.Jump(LibraryScroll);
                await _heroReady; await CaptureAsync(output, name);
            }
            await Page("home", "01-home");
            await Page("library", "02-library");
            // « Filtrer » unfolds the genre, year, progress and library choices; a filter chosen is counted on it.
            // They open over the grid: nothing below them moves.
            UpdateLayout(); var gridAt = LibraryRows.TranslatePoint(new Point(), LibraryScroll).Y;
            FiltersToggle_Click(this, new()); _filterBusy = true; GenreFilter.SelectedIndex = Math.Min(1, GenreFilter.Items.Count - 1); _filterBusy = false; RenderDemo();
            await CaptureAsync(output, "02-library-filters");
            UpdateLayout();
            if (Math.Abs(LibraryRows.TranslatePoint(new Point(), LibraryScroll).Y - gridAt) > 0.5) throw new InvalidOperationException("« Filtrer » pushed the grid down instead of opening over it");
            if (GenreFilter.SelectedIndex > 0 && GenreChipsScroll.Visibility != Visibility.Visible && ActiveFilters.Children.Count != 1) throw new InvalidOperationException("The genre chosen has no chip beside « Trier par »");
            if (CatalogFilters.Visibility != Visibility.Visible || GenreFilter.SelectedIndex > 0 != FiltersText.Text.Contains('1')) throw new InvalidOperationException("« Filtrer » does not show or count its filters: " + FiltersText.Text);
            _filtersOpen = false;
            await Page("Movie", "03-films");
            await Page("Series", "04-series");
            await Page("library", "05-favorites", favorites: true);
            await ShowDetailsAsync(_demoItems!.First(x => x.Type == "Movie"));
            await CaptureAsync(output, "06-film-details");
            // Every button a title's page can have, at Mira's smallest window: one line that never cuts a button off;
            // Lecture and « Regarder ensemble » keep their words, the others are round icons named for the Narrator and in their tooltip (or their word when on).
            // (A runner's screen is too small for a wide window: Windows keeps a window within it.)
            ShowEveryDetailAction();
            Width = 960; Height = 600; await CaptureAsync(output, "06-film-actions-minimum");
            void AssertActions(string when)
            {
                var actions = DetailActions.Children.OfType<Button>().Where(x => x.IsVisible).ToList();
                foreach (var action in actions)
                    if (action.TranslatePoint(new Point(action.ActualWidth, 0), DetailActions).X > DetailActions.ActualWidth + 1)
                        throw new InvalidOperationException($"« {Accessible.ContentText(action.Content)} » is cut off on the title page {when}.");
                if (!ActionRow.ShowsLabel(DetailPlay)) throw new InvalidOperationException($"Lecture lost its label {when}.");
                if (actions.Skip(1).FirstOrDefault(x => ActionRow.ShowsLabel(x) && x.Content is Panel && !ActionRow.GetKeep(x) || Accessible.ContentText(x.Content).Length == 0 && x.ToolTip is null) is { } odd)
                    throw new InvalidOperationException($"A button of the title page is not a named round icon {when}: « {Accessible.ContentText(odd.Content)} ».");
                // A state button is the same rounded square on or off: its glyph alone says which.
                if (actions.FirstOrDefault(x => x.Content is StateIcon && (Math.Abs(x.ActualWidth - 46) > .5 || Math.Abs(x.ActualHeight - 46) > .5)) is { } grown)
                    throw new InvalidOperationException($"« {AutomationProperties.GetName(grown)} » is not a 46 px square {when}: {grown.ActualWidth:0} × {grown.ActualHeight:0}.");
            }
            AssertActions("at 960 px");
            TogetherButton.Visibility = OfflineButton.Visibility = FollowButton.Visibility = Visibility.Collapsed;
            await CaptureAsync(output, "06-film-actions-fewer"); AssertActions("with four buttons at 960 px");
            Width = 1440; Height = 960;
            // Every state button on, then off with a copy a little under half way: the same squares, their glyphs alone changed.
            ShowEveryDetailAction(); var shown = _detail!;
            ShowFavorite(true); ShowWatched(shown, true); ShowState(FollowButton, "follow", true, L("Suivie", "Following"), L("Suivie", "Following"));
            await CaptureAsync(output, "06-film-states-on"); AssertActions("all on");
            ShowFavorite(false); ShowWatched(shown, false); ShowState(FollowButton, "follow", false, L("Suivre…", "Follow…"), L("Suivre…", "Follow…"));
            ShowState(OfflineButton, "offline", false, L("Arrive sur ce PC · 42 %", "Coming to this PC · 42%"), L("Arrive sur ce PC · 42 %", "Coming to this PC · 42%"), .42);
            await CaptureAsync(output, "06-film-states-off"); AssertActions("all off, a copy coming");
            // Réglages opens on « L’essentiel »: Mira's card, the account's, and six settings that are those of their sections.
            Settings_Click(this, new());
            if (!EssentialsSettings.IsVisible || EssentialsQuality.Items.Count != QualityChoice.Items.Count || EssentialsQuality.SelectedIndex != QualityChoice.SelectedIndex)
                throw new InvalidOperationException("Réglages did not open on « L’essentiel », its quality the one of Lecture.");
            await CaptureAsync(output, "07a-essentials-settings");
            // A switch applies on its own, with no button to press: in the settings a moment later, and back the same way;
            // turned on « L’essentiel », it is the one of Lecture.
            var autoNext = _settings.AutoNext;
            for (var turn = 0; turn < 2; turn++)
            {
                EssentialsAutoNext.IsChecked = !(EssentialsAutoNext.IsChecked == true); await Task.Delay(800);
                if (AutoNextCheck.IsChecked != EssentialsAutoNext.IsChecked) throw new InvalidOperationException("A switch of « L’essentiel » is not the one of its section.");
                if (_settings.AutoNext != (AutoNextCheck.IsChecked == true)) throw new InvalidOperationException("A switch of the settings was not applied on its own.");
            }
            if (_settings.AutoNext != autoNext) throw new InvalidOperationException("The settings did not come back as they were.");
            SelectSettingsTab("playback"); await CaptureAsync(output, "07-playback-settings");
            SelectSettingsTab("subtitles"); await CaptureAsync(output, "08-subtitles-settings");
            SelectSettingsTab("library"); await CaptureAsync(output, "09b-library-settings");
            SelectSettingsTab("general"); await CaptureAsync(output, "09-appearance-settings");
            SelectSettingsTab("server"); await CaptureAsync(output, "10-server-settings");
            SelectSettingsTab("updates"); await CaptureAsync(output, "10b-this-pc-settings");
            SelectSettingsTab("privacy"); await CaptureAsync(output, "10d-privacy-settings");
            // What a beginner needs first, the rest behind « Réglages avancés »: opened, it shows the picture and the decoding.
            SelectSettingsTab("playback"); PlaybackAdvanced.IsExpanded = true; await CaptureAsync(output, "10c-playback-advanced"); PlaybackAdvanced.IsExpanded = false;
            // The search: every section's settings at once, changed right there; Échap gives the section back as it was.
            SettingsSearchBox.Text = L("sous-titres", "subtitles"); await CaptureAsync(output, "10e-settings-search");
            if (!SubtitleSettings.IsVisible || SettingsSectionTitle.Text == L("Aucun résultat", "No results"))
                throw new InvalidOperationException("The settings' search did not find the subtitles in their sections: " + SettingsSectionTitle.Text);
            // The person's own words: « mettre à jour » finds « Mises à jour » in English as in French, and « L’essentiel »
            // never shows its shortcuts a second time among the results.
            SettingsSearchBox.Text = "mettre à jour"; await CaptureAsync(output, "10f-settings-search-update");
            if (!UpdateSettings.IsVisible || EssentialsSettings.IsVisible)
                throw new InvalidOperationException("The settings' search did not find the updates by another word for them: " + SettingsSectionTitle.Text);
            if (!ClearSheetSearch() || !PlaybackSettings.IsVisible || SubtitleSettings.IsVisible || PlaybackAdvanced.IsExpanded)
                throw new InvalidOperationException("Clearing the settings' search did not give « Lecture » back as it was.");
            // Aide, the other side of the same window: turned over at once, on the same picture of the app.
            SheetToHelp_Click(this, new());
            if (!Shown(GuideOverlay) || SettingsOverlay.Visibility == Visibility.Visible || GuideBackdrop.Source is null && !SystemParameters.HighContrast)
                throw new InvalidOperationException("« Aide » did not turn the window over.");
            // Its Réglages | Aide: the pill slides over from Réglages (Segmented, 200 ms), seen between the two on its way.
            var between = false;
            for (var end = Environment.TickCount64 + 2000; Environment.TickCount64 < end && GuideSwitch.Position < .999; await Task.Delay(10))
                between |= GuideSwitch.Position is > .02 and < .98;
            if (GuideSwitch.Position < .999 || !between && !Motion.Reduced)
                throw new InvalidOperationException($"Réglages | Aide's pill did not slide over to Aide: at {GuideSwitch.Position:0.##}, seen between: {between}.");
            await CaptureAsync(output, "10f-help"); SheetToSettings_Click(this, new());
            await Page("library", "11-hover");
            CardHoverEntered((MediaCard)PosterCards.Children[0]);
            await CaptureAsync(output, "11-hover"); ClosePreview();
            // Recherche: its field alone, an invitation before the first letter, then what was found.
            ResetFilters(); _catalog.View = "search"; RenderDemo(); await CaptureAsync(output, "12-search-start");
            if (EmptyState.Visibility != Visibility.Visible || PosterCards.Visibility == Visibility.Visible || CatalogTools.Visibility == Visibility.Visible || SearchField.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The search page does not wait for a first letter.");
            _filterBusy = true; SearchBox.Text = L("étoiles", "stars"); _filterBusy = false; RenderDemo();
            await CaptureAsync(output, "12-search");
            await Page("home", "01-home");
            Width = 1024; Height = 720; await CaptureAsync(output, "13-compact-window");
            Width = 1440; Height = 960;
            LoginArt.Source = null; UsernameBox.Text = ""; PasswordBox.Clear(); LoginError.Text = "";
            // The server the login screen found on the home network (fictional, like the rest).
            _loginServers = []; UseServer(new("http://192.168.1.20:8096/", "MEDIA-PC", "demo", ServerSearch.Place.Home));
            BackToLibrary.Visibility = LogoutButton.Visibility = Visibility.Collapsed;
            LoginOverlay.Visibility = Visibility.Visible; LoginOverlay.Opacity = 1;
            await CaptureAsync(output, "14-login");
            // Help for someone new, at the sizes of a 1080p screen (100, 125 and 150 % scaling) and of Mira's smallest
            // window: the welcome, sign-in, Jellyfin setup and "what's new" must fit whole, with nothing to scroll to.
            var sizes = new (int Width, int Height, string Name)[] { (1920, 1032, "1080p-100"), (1536, 826, "1080p-125"), (1280, 688, "1080p-150"), (960, 600, "minimum") };
            var drawn = new List<string>();
            async Task At((int Width, int Height, string Name) size, string name, FrameworkElement? fit, FrameworkElement? across = null)
            {
                Width = size.Width; Height = size.Height; await CaptureAsync(output, $"{name}-{size.Name}");
                var root = (FrameworkElement)Content; drawn.Add($"{name}-{size.Name}: {root.ActualWidth:0} × {root.ActualHeight:0}");
                if (fit is not null) AssertFits(fit, $"{name} at {size.Name}");
                if (across is not null) AssertContained(across, $"{name} at {size.Name}");
            }
            foreach (var size in sizes)
            {
                LoginOverlay.Visibility = Visibility.Visible; LoginOverlay.Opacity = 1; ShowWelcome(); await At(size, "15-welcome", WelcomeContent);
                WelcomeOverlay.Visibility = Visibility.Collapsed; ShowSignInForm(); LoginError.Text = L("Nom d’utilisateur ou mot de passe incorrect.", "Wrong username or password."); await At(size, "16-sign-in", LoginPanel);
                LoginError.Text = ""; ShowServerSetup(true); SetupFolderBox.Text = $@"C:\Users\{You}\Videos\Jellyfin"; SetupUserBox.Text = You;
                SetupStatus.Text = L("Accepte l’autorisation de Windows. Si l’installateur affiche « Could not start the Jellyfin Server service », choisis Ignorer.", "Accept Windows’ permission prompt. If the installer shows “Could not start the Jellyfin Server service”, choose Ignore.");
                await At(size, "17-jellyfin-setup", ServerSetupPanel);
                ShowSignInForm(); LoginOverlay.Visibility = Visibility.Collapsed;
                ShowWhatsNew(WhatsNew.Recent(ReleaseFeed.ParseVersion(JellyfinClient.AppVersion) ?? new Version(0, 0, 0))); await At(size, "18-whats-new", WhatsNewCard);
                WhatsNewOverlay.Visibility = Visibility.Collapsed;
            }
            OpenGuide(L($@"Jellyfin est prêt. Range tes vidéos dans C:\Users\{You}\Videos\Jellyfin.", $@"Jellyfin is ready. Put your videos in C:\Users\{You}\Videos\Jellyfin."));
            // Fictional folders and address: the demo has no server to read them from.
            await Task.Delay(50); GuideFolders.Children.Clear(); GuideFoldersHint.Visibility = Visibility.Collapsed;
            foreach (var library in new[] { L("Animes", "Anime"), L("Films", "Movies"), L("Séries", "Shows") }) GuideFolders.Children.Add(FolderRow(library, $@"C:\Users\{You}\Videos\Jellyfin\{library}", true));
            GuideJellyfinActions.Visibility = GuideRemote.Visibility = Visibility.Visible; GuideServerAddress.Text = "http://127.0.0.1:8096";
            GuideRemoteAddress.Text = "http://192.168.1.20:8096";
            // The phone, half ready: Mira web there, the firewall still in the way, Tailscale not installed yet.
            ++_phoneVersion; _phone = null; _phoneReadAt = DateTime.MaxValue;
            ShowPhone(new PhoneSetup.Facts("http://127.0.0.1:8096", true, Web: true, Home: "http://192.168.1.20:8096", Admin: true, TailnetAllowed: false,
                Firewall: new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Private), Boot: new(true, true, false, true)));
            // « Bien démarrer », then « Mira sur ton iPhone » whole, with « Tout préparer » and the address to type.
            foreach (var size in new[] { sizes[0], sizes[2], sizes[3] }) { SelectGuideTopic("start", animate: false); await At(size, "19-guide", null, GuideSheet); }
            foreach (var size in new[] { sizes[0], sizes[2] }) { SelectGuideTopic("phone", animate: false); await At(size, "19-guide-phone", null, GuideSheet); }
            GuideOverlay.Visibility = Visibility.Collapsed;
            // The downloads page before TorLink is turned on: the list of downloaders.
            TorLinkOverlay.Visibility = Visibility.Visible; TorLinkOverlay.Opacity = 1; ShowDownloaders(true);
            foreach (var size in new[] { sizes[0], sizes[2] }) await At(size, "20-downloads", null, DownloadersPanel);
            // Away from the PC of Jellyfin, the same page sends to it: what it has under way, and a search with the best
            // release of each episode first (fictional names; no relay is asked).
            // « Régie »: the page at rest (the queue in columns, the series followed, the titles placed), then during a search.
            ShowServerDownloadsSample();
            foreach (var size in new[] { sizes[0], sizes[2] }) await At(size, "20-downloads-server", null, ServerDownloadsPanel);
            ShowServerSearchSample();
            foreach (var size in new[] { sizes[0], sizes[2] }) await At(size, "20b-downloads-search", null, ServerDownloadsPanel);
            ServerQueryBox.Clear();
            HideServerDownloads(); ShowDownloaders(false);
            TorLinkOverlay.Visibility = Visibility.Collapsed;
            _demo = false; _catalog.Items = []; _catalog.Resume = []; _catalog.NextUp = []; _catalog.TotalCount = 0; _catalog.Loading = false; _catalog.View = "home"; RenderLibrary();
            await At(sizes[2], "21-empty-library", null, EmptyState);
            if (EmptyGuide.Visibility != Visibility.Visible) throw new InvalidOperationException("The empty library does not lead to the guide.");
            _demo = true;
            // Maximized, as Mira opens on a screen smaller than its default size: everything on the screen, to its edges.
            ResetFilters(); _catalog.View = "home"; RenderDemo(); SmoothScroll.Jump(LibraryScroll); WindowState = WindowState.Maximized; await CaptureAsync(output, "22-maximized");
            drawn.Add($"22-maximized (Windows {Environment.OSVersion.Version.Build}): {AssertMaximizedOnScreen()}");
            WindowState = WindowState.Normal;
            // A title that cannot play: the player's own screen, then « Retour » back to the library.
            PlaybackFailed(L("mpv n’a pas pu lire ce média. Vérifie l’accès au fichier et la connexion Jellyfin.", "mpv couldn’t play this media. Check access to the file and the connection to Jellyfin."), _demoItems!.First(x => x.Type == "Movie"), mini: false, RetryText, () => Task.CompletedTask);
            foreach (var size in new[] { sizes[0], sizes[3] }) await At(size, "23-player-error", null, PlayerError);
            GoBack();
            if (PlayerErrorShown || PlayerShell.IsVisible || !LibraryShell.IsVisible) throw new InvalidOperationException("« Retour » does not leave the player's error screen.");
            // A click on Favori or Vu moves nothing on the title page, during the change and after it.
            drawn.AddRange(await CheckTitleRowStillAsync(output));
            // « Mode canapé »: full screen, zoomed, the arrows and a controller anywhere on the screen.
            drawn.Add(await CheckCouchAsync(output));
            await File.WriteAllLinesAsync(Path.Combine(output, "sizes.txt"), drawn);
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), $"PASS: {16 + drawn.Count} application views; built-in fictional catalogue; no account, server or personal media used.");
        }
        catch (Exception ex)
        {
            // The message names the view and the sizes; the gallery only ever shows the fictional catalogue.
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), $"FAIL: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }

    /// <summary>The page's buttons that the demo leaves out (a group, this PC, Jellyfin's files, TorLink), as they read.</summary>
    private void ShowEveryDetailAction()
    {
        DetailPlay.Content = ActionLabel("play", L("Reprendre", "Resume")); DetailRestart.Visibility = Visibility.Visible;
        TogetherButton.Content = ActionLabel("users", L("Regarder ensemble", "Watch together"));
        ShowState(OfflineButton, "offline", true, L("Sur ce PC", "On this PC"), L("Sur ce PC · 4,2 Go. Clique pour le retirer", "On this PC · 4.2 GB. Click to remove it"));
        _detailFollow = null; DescribeFollowButton();
        TogetherButton.Visibility = OfflineButton.Visibility = FilesButton.Visibility = FollowButton.Visibility = Visibility.Visible;
    }

    /// <summary>The Windows account of the captures' sample paths.</summary>
    private static string You => L("Toi", "You");

    private void ShowServerDownloadsSample()
    {
        _serverScreen = true;
        DownloadersPanel.Visibility = TorLinkTerminalHost.Visibility = Visibility.Collapsed;
        DownloadersButton.Visibility = TorLinkFolderButton.Visibility = TorLinkSettingsButton.Visibility = TorLinkKeysHint.Visibility = TorLinkViewButton.Visibility = Visibility.Collapsed;
        ServerDownloadsPanel.Visibility = Visibility.Visible; ServerDownloadsPanel.Opacity = 1;
        const long GiB = 1L << 30, MiB = 1L << 20, TiB = 1L << 40;
        var now = DateTimeOffset.UtcNow;
        static string Hash(char c) => new(c, 40);
        // Fictional names only; no picture (Jellyfin is not asked in the demo): each line its plain tile.
        const string next = "Les.Etoiles.du.Nord.S01E04.MULTI.1080p.WEB-DL.H264-NOTAG";
        List<RelayFollow> follows =
        [
            new("f1", "s1", "Les Étoiles du Nord", null, now.AddDays(-40), now.AddHours(-2), now.AddHours(4), false, new FollowNote("found", "S01E04"), "S01E03",
                [new("S01E04", next, now.AddHours(-1))], [], new RelayAiring("S01E05", now.AddDays(1))),
            new("f2", "s2", "Brume", "Brume (2024)", now.AddDays(-90), now.AddHours(-3), now.AddHours(3), false, new FollowNote("nothing", "S02E08"), "S02E08",
                [new("S02E08", "Brume.2024.S02E08.1080p.WEB-DL-NOTAG", now.AddDays(-6))], [], new RelayAiring("S02E09", now.AddDays(5))),
            new("f3", "s3", "Ville Haute", null, now.AddDays(-12), now.AddMinutes(-20), now.AddHours(6), false, new FollowNote("found", "S01E07"), "S01E06",
                [], [new("p1", "S01E07", "Ville.Haute.S01E07.1080p.WEB.H264-NOTAG", 14 * GiB / 10, "waiting", now.AddMinutes(8), null)], null),
        ];
        RenderServerStatus(new RelayStatus(new("SALON-PC", true, now, true, null, Free: 5 * TiB + 4 * TiB / 10),
            [new("r1", "La.Riviere.Lente.2021.1080p.BluRay.x264-NOTAG", 9 * GiB / 2, Hash('b'), "waiting", null, now)], [],
            [new(Hash('a'), next, "downloading", 41, 9 * MiB, 34, 29 * GiB / 10, 12 * GiB / 10, 190),
             new(Hash('e'), "Les.Jardins.Suspendus.2019.2160p.UHD.BluRay.x265.10bit.HDR-NOTAG", "downloading", 12, 5 * MiB, 18, 22 * GiB, 26 * GiB / 10, 3900),
             new(Hash('c'), "La.Maison.du.Lac.2023.1080p.BluRay.x265.10bit-NOTAG", "paused", 63, 0, 0, 4 * GiB, 5 * GiB / 2)],
            [new(Hash('d'), "Le Dernier Phare", L("disponible dans Jellyfin", "available in Jellyfin"), null, true),
             new(Hash('f'), "Les Étoiles du Nord · S01E03", L("disponible dans Jellyfin", "available in Jellyfin"), null, true),
             new(Hash('g'), "Papier Lune", L("disponible dans Jellyfin", "available in Jellyfin"), null, true),
             new(Hash('h'), "Brume · S02E08", L("disponible dans Jellyfin", "available in Jellyfin"), null, true),
             new(Hash('i'), "Le Col", L("disponible dans Jellyfin", "available in Jellyfin"), null, true)], follows), now);
    }

    private void ShowServerSearchSample()
    {
        const long GiB = 1L << 30;
        ServerQueryBox.Text = "Les Étoiles du Nord";
        ShowServerHits("sample", "Les Étoiles du Nord",
        [
            new("1", "Les.Etoiles.du.Nord.S01E05.MULTI.1080p.WEB-DL.H264-NOTAG", 3 * GiB / 2, 85, 4, "1337x", null, "tv"),
            new("2", "Les Etoiles du Nord S01E05 1080p WEBRip x265-PLUME", 9 * GiB / 10, 240, 9, "tpb", null, "tv"),
            new("3", "Les.Etoiles.du.Nord.S01E05.2160p.WEB-DL.DV.HEVC-NOTAG", 6 * GiB, 30, 2, "1337x", null, "tv"),
            new("4", "Les.Etoiles.du.Nord.S01E04.MULTI.1080p.WEB-DL.H264-NOTAG", 3 * GiB / 2, 60, 3, "1337x", null, "tv"),
            new("5", "Les.Etoiles.du.Nord.S01E04.VOSTFR.720p.HDTV.x264-NOTAG", 7 * GiB / 10, 12, 1, "tpb", null, "tv"),
            new("6", "Les.Etoiles.du.Nord.S01.COMPLETE.MULTI.1080p.WEB-DL.x264-NOTAG", 9 * GiB, 20, 5, "1337x", null, "tv"),
        ]);
    }
}
