using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// --ui-audit &lt;folder&gt;: the small defects a person points at, found by Mira itself (tools/ui-audit.ps1, nightly on
/// the PC of Jellyfin). Signed in with a profile of its own, the window walks Home, each library, the filters and their
/// lists, a film's and a series' page with their menus (« Get season » without searching), Réglages and the Guide page
/// by page, Téléchargements, at 1440 × 900, 1024 × 720 and 1920 × 1080; each screen scrolled through and measured by
/// <see cref="UiInspector"/> (with its open menus), captured, the defects outlined on a copy. Then the hover of the
/// tiles (a synthetic MouseEnter: the pointer never moves) and the transitions, frame by frame. Read-only: no
/// playback, no search, nothing marked or changed on Jellyfin; menus are opened, never chosen from.
/// The window is on screen (a menu is only placed for real there: WPF keeps a popup on the screen, not in the window)
/// but never activated, under every other window, out of the taskbar.
/// </summary>
public partial class MainWindow
{
    private sealed class AuditRun(string output)
    {
        public string Output { get; } = output;
        public List<UiFinding> Findings { get; } = [];
        public List<AuditShot> Shots { get; } = [];
        public List<string> Notes { get; } = [];
        public List<AuditTransition> Transitions { get; } = [];
        public string Size { get; set; } = "";
        public int Count { get; set; }
    }
    private sealed record AuditShot(string Screen, string Capture, string? Annotated, int Defects);
    private sealed record AuditTransition(string Name, int Frames, double Ms, double Jump, string? Wanted);

    private async Task RunUiAuditAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--ui-audit") + 1]);
        foreach (var folder in new[] { "captures", "annotated" }) Directory.CreateDirectory(Path.Combine(output, folder));
        var run = new AuditRun(output); var started = DateTimeOffset.Now; string? failure = null;
        try
        {
            KeepUnderEverything();
            if (_catalog.Items.Count == 0 && !_demo)
                throw new InvalidOperationException("Rien à parcourir : Mira n'est connecté à aucune bibliothèque (session absente ou expirée ?). " + LoginError.Text);
            if (Motion.Reduced) run.Notes.Add("Windows demande moins d'animations (ou Mira) : les transitions et le zoom des tuiles ne sont pas jugés.");
            foreach (var (width, height, every) in new[] { (1440d, 900d, true), (1024d, 720d, false), (1920d, 1080d, false) })
            {
                run.Size = $"{width:0}×{height:0}";
                WindowState = WindowState.Normal; Width = width; Height = height;
                await AuditScreensAsync(run, every);
            }
            run.Size = "1440×900"; Width = 1440; Height = 900;
            if (!Motion.Reduced) { await AuditTilesAsync(run); await AuditTransitionsAsync(run); }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            UiAuditReport.Write(run.Output, run.Findings, run.Shots.Select(x => (x.Screen, x.Capture, x.Annotated, x.Defects)).ToList(), run.Notes,
                run.Transitions.Select(x => (x.Name, x.Frames, x.Ms, x.Jump, x.Wanted)).ToList(), failure, started, SettingsVersion.Text);
            Close();
        }
    }

    /// <summary>Under every other window, never activated (App.OnStartup showed it without activation, out of the taskbar).</summary>
    internal void KeepUnderEverything()
    {
        var handle = new WindowInteropHelper(this).Handle;
        // HWND_BOTTOM; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE.
        SetWindowPos(handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    private async Task AuditScreensAsync(AuditRun run, bool every)
    {
        await AuditBackHomeAsync();
        // A first start's overlays, as a first start shows them.
        foreach (var overlay in new FrameworkElement[] { WelcomeOverlay, WhatsNewOverlay })
            if (overlay.Visibility == Visibility.Visible) { await AuditShotAsync(run, overlay == WelcomeOverlay ? "bienvenue" : "nouveautes"); overlay.Visibility = Visibility.Collapsed; }
        await AuditScrollAsync(run, "accueil", LibraryScroll);
        foreach (var library in _homeLibraries.Where(x => LibrarySection.Browsed(x.CollectionType)).Take(every ? 4 : 1))
        {
            await GoToPageAsync("library", library.Id);
            await AuditScrollAsync(run, "bibliotheque " + library.Name, LibraryScroll);
            if (!every) continue;
            await AuditCardMenuAsync(run, "menu d'une affiche " + library.Name);
        }
        // The filters, open, and each of their lists.
        FiltersToggle_Click(this, new()); await AuditShotAsync(run, "filtres");
        foreach (var box in FilterBoxes.Where(x => x.IsVisible && x.Items.Count > 0).Take(every ? 8 : 2))
        {
            box.IsDropDownOpen = true;
            await AuditShotAsync(run, "liste " + (AutomationProperties.GetName(box) is { Length: > 0 } name ? name : box.Name), onlyWhenFound: true);
            box.IsDropDownOpen = false;
        }
        CloseFilters();
        _catalog.View = "search"; _filterBusy = true; SearchBox.Text = "mira-ui-audit-aucun-titre-93718"; _filterBusy = false; await RefreshAsync();
        await AuditShotAsync(run, "recherche sans résultat");
        ResetFilters(); await GoToPageAsync("home");

        var movie = _catalog.Items.Concat(_catalog.Resume).FirstOrDefault(x => x.Type == "Movie") ?? await AuditFirstAsync("Movie");
        if (movie is not null)
        {
            await ShowDetailsAsync(movie);
            await AuditScrollAsync(run, "fiche d'un film", DetailScroll);
            if (every) foreach (var button in new[] { FilesButton, TogetherButton }.Where(x => x.IsVisible))
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                await AuditShotAsync(run, "menu " + UiInspector.Describe(button));
                CloseAuditPopups();
            }
            await BackFromDetailsAsync();
        }
        await AuditSeriesAsync(run, every);

        Settings_Click(this, new());
        foreach (var tab in SettingsTabs.Select(x => x.Tag).Where(tag => SettingsNavigation.Children.OfType<Button>().Any(b => b.Tag?.ToString() == tag && b.IsVisible)))
        {
            if (!every && tab is not ("home" or "subtitles" or "server")) continue;
            SelectSettingsTab(tab, animate: false);
            await AuditScrollAsync(run, "réglages " + tab, SettingsScroll);
            if (every) await AuditListsAsync(run, "réglages " + tab, SettingsSheet);
        }
        await CloseSettingsAnimatedAsync();

        OpenGuide();
        foreach (var topic in GuideTopics.Select(x => x.Tag).Take(every ? 99 : 2))
        {
            SelectGuideTopic(topic, animate: false);
            await AuditScrollAsync(run, "guide " + topic, GuideScroll);
        }
        await CloseGuideAsync();

        if (_torlinkEnabled && _downloadsOffered)
        {
            await ShowTorLinkAsync();
            await AuditShotAsync(run, "téléchargements");
            await CloseTorLinkAsync();
        }
        await AuditBackHomeAsync();
    }

    /// <summary>A series' page: the first one with « Get season » or « Get E… » (missing episodes known), else the first.</summary>
    private async Task AuditSeriesAsync(AuditRun run, bool every)
    {
        var list = await AuditListAsync("Series", every ? 12 : 1);
        MediaItem? shown = null;
        foreach (var series in list)
        {
            await ShowDetailsAsync(series); await AuditSettleAsync();
            shown = series;
            if (!every || (await AuditGetsAsync()).Count > 0) break;
            await BackFromDetailsAsync();
        }
        if (shown is null) { run.Notes.Add("Aucune série dans la bibliothèque : pages de série non vues."); return; }
        if (DetailOverlay.Visibility != Visibility.Visible) await ShowDetailsAsync(shown);
        await AuditScrollAsync(run, "fiche d'une série", DetailScroll);
        if (every)
        {
            // An episode's right click, at its tile's centre.
            if (Descendants<ButtonBase>(DetailOverlay).FirstOrDefault(x => x.ContextMenu is not null && x.IsVisible) is { } episode)
                await AuditContextMenuAsync(run, "menu d'un épisode", episode);
            var gets = (await AuditGetsAsync()).Take(3).ToList();
            if (gets.Count == 0) run.Notes.Add("Aucun bouton « Get season » ni « Get E… » sur les séries vues (épisodes manquants désactivés, ou rien ne manque) : leur menu n'est pas vu.");
            foreach (var get in gets)
            {
                get.BringIntoView(); await AuditSettleAsync();
                get.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, get));
                await AuditShotAsync(run, "menu " + UiInspector.Describe(get));
                if (_missingPopup is { } popup) popup.IsOpen = false;
            }
        }
        await BackFromDetailsAsync();
    }

    /// <summary>
    /// The « Get » buttons of the series shown, once Jellyfin said whether it offers them (its relay answers after the page
    /// opened): those of the season on screen, else of the first season the library has none of, chosen in the list of
    /// seasons as a person would (« Get season 2 » is on that season's card).
    /// </summary>
    private async Task<List<Button>> AuditGetsAsync()
    {
        for (var wait = 0; wait < 20 && !_missingOffered; wait++) await Task.Delay(200);
        var found = AuditGetButtons().ToList();
        if (found.Count > 0 || !_missingOffered) return found;
        foreach (var page in SeasonSelector.Items.OfType<ComboBoxItem>().Where(x => x.Tag is EpisodePage { Absent: true }).ToList())
        {
            SeasonSelector.SelectedItem = page; await AuditSettleAsync();
            found = AuditGetButtons().ToList();
            if (found.Count > 0) break;
        }
        return found;
    }

    /// <summary>« Get season 2 » and « Get E6–9 » on the page shown (their help text is the privacy line of MissingDownloads).</summary>
    private IEnumerable<Button> AuditGetButtons() => Descendants<Button>(DetailOverlay).Where(x => x.IsVisible && AutomationProperties.GetHelpText(x) == MissingDownloads.Privacy);

    /// <summary>Titles of a kind from the cache of the account, the library's first page otherwise.</summary>
    private async Task<List<MediaItem>> AuditListAsync(string type, int count)
    {
        var found = _catalog.Items.Concat(_catalog.Resume).Where(x => x.Type == type).DistinctBy(x => x.Id).Take(count).ToList();
        if (found.Count >= count) return found;
        foreach (var library in _homeLibraries.Where(x => LibrarySection.Browsed(x.CollectionType)))
        {
            await GoToPageAsync("library", library.Id);
            found.AddRange(_catalog.Items.Where(x => x.Type == type && found.All(y => y.Id != x.Id)));
            if (found.Count >= count) break;
        }
        await GoToPageAsync("home");
        return found.Take(count).ToList();
    }
    private async Task<MediaItem?> AuditFirstAsync(string type) => (await AuditListAsync(type, 1)).FirstOrDefault();

    private async Task AuditBackHomeAsync()
    {
        CloseAuditPopups();
        if (SettingsOverlay.Visibility == Visibility.Visible) await CloseSettingsAnimatedAsync();
        if (GuideOverlay.Visibility == Visibility.Visible) await CloseGuideAsync();
        if (TorLinkOverlay.Visibility == Visibility.Visible) await CloseTorLinkAsync();
        if (DetailOverlay.Visibility == Visibility.Visible) await BackFromDetailsAsync();
        CloseFilters(); ResetFilters();
        await GoToPageAsync("home");
    }

    /// <summary>The right click of the first poster and of the last one of the first row (the window's edge is where a menu leaves it).</summary>
    private async Task AuditCardMenuAsync(AuditRun run, string name)
    {
        var cards = PosterCards.Children.OfType<MediaCard>().Where(x => x.IsVisible && x.ContextMenu is not null).ToList();
        if (cards.Count == 0) return;
        var top = cards[0].TranslatePoint(new Point(), PosterCards).Y;
        var last = cards.TakeWhile(x => Math.Abs(x.TranslatePoint(new Point(), PosterCards).Y - top) < 2).Last();
        await AuditContextMenuAsync(run, name, cards[0]);
        if (!ReferenceEquals(last, cards[0])) await AuditContextMenuAsync(run, name + " (bord droit)", last);
    }

    /// <summary>A right click at the middle of <paramref name="target"/>: its ContextMenu where the pointer would have opened it.</summary>
    private async Task AuditContextMenuAsync(AuditRun run, string name, FrameworkElement target)
    {
        if (target.ContextMenu is not { } menu) return;
        menu.PlacementTarget = target; menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = target.ActualWidth / 2; menu.VerticalOffset = target.ActualHeight / 2;
        menu.IsOpen = true;
        await AuditShotAsync(run, name);
        menu.IsOpen = false;
        foreach (var property in new[] { ContextMenu.PlacementTargetProperty, ContextMenu.PlacementProperty, ContextMenu.HorizontalOffsetProperty, ContextMenu.VerticalOffsetProperty }) menu.ClearValue(property);
    }

    /// <summary>Every list (ComboBox) of a page, opened one at a time: captured only when its list is at fault.</summary>
    private async Task AuditListsAsync(AuditRun run, string name, FrameworkElement page)
    {
        foreach (var box in Descendants<ComboBox>(page).Where(x => x.IsVisible && x.IsEnabled && x.Items.Count > 0).Take(12))
        {
            box.BringIntoView(); box.IsDropDownOpen = true;
            await AuditShotAsync(run, $"{name}, liste {UiInspector.Describe(box)}", onlyWhenFound: true, settle: 250);
            box.IsDropDownOpen = false;
        }
    }

    /// <summary>A page scrolled screen by screen (6 at most): measured at each, captured at the top and wherever something is wrong.</summary>
    private async Task AuditScrollAsync(AuditRun run, string name, ScrollViewer viewer)
    {
        SmoothScroll.Jump(viewer, 0);
        await AuditShotAsync(run, name);
        for (var page = 1; page < 6; page++)
        {
            var offset = Math.Min(viewer.ScrollableHeight, page * Math.Max(200, viewer.ViewportHeight * .85));
            if (offset <= viewer.VerticalOffset + 1) break;
            SmoothScroll.Jump(viewer, offset);
            await AuditShotAsync(run, $"{name} (écran {page + 1})", onlyWhenFound: true);
        }
        SmoothScroll.Jump(viewer, 0);
    }

    private async Task AuditSettleAsync(int ms = 450)
    {
        try { await SettleVisualAsync().WaitAsync(TimeSpan.FromSeconds(8)); } catch (TimeoutException) { }
        await Task.Delay(Motion.Reduced ? 80 : ms);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
    }

    /// <summary>One screen: its defects (and those of its open menus), a capture with its menus where they are, and a copy with the defects outlined.</summary>
    private async Task AuditShotAsync(AuditRun run, string name, bool onlyWhenFound = false, int settle = 450)
    {
        await AuditSettleAsync(settle);
        var screen = $"{run.Size} · {name}";
        var root = (FrameworkElement)Content;
        var window = new Rect(root.RenderSize);
        var findings = UiInspector.Inspect(root, screen);
        foreach (var control in Accessible.Unnamed(root)) findings.Add(new("sans-nom", "défaut", screen, Accessible.Describe(control), AuditBounds(control, root), "Le Narrateur ne peut pas le nommer"));
        foreach (var control in Accessible.WithoutFocusRing(root, (Style)FindResource("KeyboardFocus")))
            findings.Add(new("sans-anneau", "défaut", screen, Accessible.Describe(control), AuditBounds(control, root), "Atteint par Tab sans anneau de focus visible"));
        var popups = new List<(FrameworkElement Root, Rect Place, BitmapSource Picture)>();
        foreach (var (popup, place) in OpenPopups(root))
        {
            var picture = Picture(popup);
            var ink = Ink(picture, popup);
            var drawn = new Rect(place.X + ink.X, place.Y + ink.Y, ink.Width, ink.Height);
            popups.Add((popup, place, picture));
            if (drawn.Left < window.Left - 2 || drawn.Top < window.Top - 2 || drawn.Right > window.Right + 2 || drawn.Bottom > window.Bottom + 2)
            {
                var standard = Descendants<ContextMenu>(popup).Any() || popup is ContextMenu;
                findings.Add(new("menu-hors-fenetre", standard ? "à juger" : "défaut", screen, Describe(popup), drawn,
                    $"Sort de {Outside(drawn, window)}" + (standard ? " (menu contextuel standard : Windows le garde à l'écran, pas dans la fenêtre)" : "")));
            }
            foreach (var inner in UiInspector.Inspect(popup, screen))
                findings.Add(inner with { Bounds = new Rect(inner.Bounds.X + place.X, inner.Bounds.Y + place.Y, inner.Bounds.Width, inner.Bounds.Height) });
        }
        var defects = findings.Count(x => x.Severity != "info");
        run.Findings.AddRange(findings);
        if (onlyWhenFound && defects == 0) return;
        var file = $"{++run.Count:000}-{Slug(run.Size + " " + name)}.png";
        var shot = Compose(root, popups, []);
        Save(shot, Path.Combine(run.Output, "captures", file));
        string? annotated = null;
        if (defects > 0) { annotated = "annotated/" + file; Save(Compose(root, popups, findings.Where(x => x.Severity != "info").ToList()), Path.Combine(run.Output, annotated)); }
        run.Shots.Add(new(screen, "captures/" + file, annotated, defects));
    }

    private static string Outside(Rect drawn, Rect window)
    {
        var sides = new List<string>();
        if (drawn.Left < window.Left - 2) sides.Add($"{window.Left - drawn.Left:0} px à gauche");
        if (drawn.Right > window.Right + 2) sides.Add($"{drawn.Right - window.Right:0} px à droite");
        if (drawn.Top < window.Top - 2) sides.Add($"{window.Top - drawn.Top:0} px en haut");
        if (drawn.Bottom > window.Bottom + 2) sides.Add($"{drawn.Bottom - window.Bottom:0} px en bas");
        return "la fenêtre de " + string.Join(", ", sides);
    }

    private static string Describe(FrameworkElement popup)
    {
        var menu = popup as ContextMenu ?? Descendants<ContextMenu>(popup).FirstOrDefault();
        if (menu is not null) return "Menu contextuel" + (menu.PlacementTarget is FrameworkElement target ? " de " + UiInspector.Describe(target) : "");
        if (Descendants<ComboBoxItem>(popup).FirstOrDefault() is { } item && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox box) return "Liste de " + UiInspector.Describe(box);
        return "Fenêtre surgissante " + (Descendants<TextBlock>(popup).FirstOrDefault(x => x.Text.Length > 0) is { } title ? "« " + UiInspector.Short(title.Text, 40) + " »" : "");
    }

    private static Rect AuditBounds(FrameworkElement element, FrameworkElement root)
    {
        try { return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); } catch (InvalidOperationException) { return Rect.Empty; }
    }

    /// <summary>The menus and lists open now (each a window of its own), and where each is in the window, in its pixels.</summary>
    private List<(FrameworkElement Root, Rect Place)> OpenPopups(FrameworkElement root)
    {
        var found = new List<(FrameworkElement, Rect)>();
        var own = PresentationSource.FromVisual(this);
        var dpi = VisualTreeHelper.GetDpi(root); var origin = root.PointToScreen(new Point());
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
        {
            if (ReferenceEquals(source, own) || source.IsDisposed || source.RootVisual is not FrameworkElement popup || !popup.IsVisible || popup.ActualWidth < 4) continue;
            if (popup.GetType().Name != "PopupRoot") continue;
            // A tooltip is not a menu.
            if (Descendants<ToolTip>(popup).Any()) continue;
            var at = popup.PointToScreen(new Point());
            found.Add((popup, new Rect((at.X - origin.X) / dpi.DpiScaleX, (at.Y - origin.Y) / dpi.DpiScaleY, popup.ActualWidth, popup.ActualHeight)));
        }
        return found;
    }

    private void CloseAuditPopups()
    {
        foreach (var (popup, _) in OpenPopups((FrameworkElement)Content))
        {
            if (popup is ContextMenu own) own.IsOpen = false;
            foreach (var menu in Descendants<ContextMenu>(popup)) menu.IsOpen = false;
            // A list's popup is bound to its ComboBox: closed through it, never by its own IsOpen (that would cut the binding).
            if (Descendants<FrameworkElement>(popup).FirstOrDefault(x => x.Parent is Popup) is { Parent: Popup holder })
            {
                if (holder.TemplatedParent is ComboBox box) box.IsDropDownOpen = false; else holder.IsOpen = false;
            }
        }
        if (_missingPopup is { } missing) missing.IsOpen = false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!ReferenceEquals(node, root) && node is T match) yield return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>An element drawn at the screen's scale, with Mira's background under the window (see <see cref="CaptureAsync"/>).</summary>
    private BitmapSource Picture(FrameworkElement visual, double scale = 1, bool backdrop = false)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX * scale)), Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY * scale)),
            dpi.PixelsPerInchX * scale, dpi.PixelsPerInchY * scale, PixelFormats.Pbgra32);
        if (backdrop)
        {
            var under = new DrawingVisual();
            using (var context = under.RenderOpen()) context.DrawRectangle(Background, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
            bitmap.Render(under);
        }
        var pictured = MediaCard.Live.Where(x => x.Pictured).ToList();
        foreach (var card in pictured) card.Pictured = false;
        try { bitmap.Render(visual); } finally { foreach (var card in pictured) card.Pictured = true; }
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>What a popup really draws (its window is larger: room for the shadow): the box of its pixels that are not see-through.</summary>
    private static Rect Ink(BitmapSource picture, FrameworkElement popup)
    {
        var width = picture.PixelWidth; var height = picture.PixelHeight; var pixels = new byte[width * height * 4];
        picture.CopyPixels(pixels, width * 4, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] > 160) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < 0) return Rect.Empty;
        var sx = popup.ActualWidth / width; var sy = popup.ActualHeight / height;
        return new Rect(left * sx, top * sy, (right - left + 1) * sx, (bottom - top + 1) * sy);
    }

    private static readonly Brush[] AuditInk = [Frozen("#FF3B30"), Frozen("#FF9F0A"), Frozen("#0A84FF"), Frozen("#BF5AF2"), Frozen("#30D158")];

    /// <summary>The window with its open menus where they are (the canvas grows past the window when one leaves it), and the defects outlined and numbered.</summary>
    private BitmapSource Compose(FrameworkElement root, List<(FrameworkElement Root, Rect Place, BitmapSource Picture)> popups, List<UiFinding> marks)
    {
        var window = new Rect(root.RenderSize);
        // The menus where they are, beyond the window if they leave it; a mark past both is cut, never the canvas grown for it.
        var canvas = popups.Aggregate(window, (all, x) => Rect.Union(all, x.Place));
        canvas.Inflate(12, 12);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-canvas.X, -canvas.Y));
            context.DrawRectangle(Frozen("#2A2A30"), null, canvas);
            context.DrawImage(Picture(root, backdrop: true), window);
            if (canvas != window) context.DrawRectangle(null, new Pen(Frozen("#80FFFFFF"), 1) { DashStyle = DashStyles.Dash }, window);
            foreach (var (_, place, picture) in popups) context.DrawImage(picture, place);
            var kinds = UiInspector.Kinds.Select(x => x.Kind).ToList();
            for (var i = 0; i < marks.Count; i++)
            {
                var mark = marks[i]; if (mark.Bounds.IsEmpty) continue;
                var ink = AuditInk[Math.Max(0, kinds.IndexOf(mark.Kind)) % AuditInk.Length];
                var box = mark.Bounds; box.Inflate(2, 2);
                context.DrawRectangle(null, new Pen(ink, 2), box);
                var label = new FormattedText((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(root).PixelsPerDip);
                var tag = new Rect(box.Left, Math.Max(canvas.Top, box.Top - 15), label.Width + 8, 15);
                context.DrawRectangle(ink, null, tag);
                context.DrawText(label, new Point(tag.Left + 4, tag.Top));
            }
            context.Pop();
        }
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(canvas.Width * dpi.DpiScaleX), (int)Math.Ceiling(canvas.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static string Slug(string text)
    {
        var plain = new string(text.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = new string(plain.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length > 60 ? slug[..60] : slug;
    }

    /// <summary>
    /// The tiles with a picture (episodes, people, seasons, collections…) answer the pointer as the posters do: a zoom.
    /// A MouseEnter raised on each kind of tile (the pointer never moves), its scale read 300 ms later. Judged only if one
    /// kind at least zooms under this synthetic pointer (the episode tiles do): otherwise the method, not the tiles, failed.
    /// </summary>
    private async Task AuditTilesAsync(AuditRun run)
    {
        await AuditBackHomeAsync();
        var still = new List<UiFinding>(); var zoomed = 0;
        var series = await AuditFirstAsync("Series");
        var movie = await AuditFirstAsync("Movie");
        foreach (var (title, label) in new[] { (series, "fiche d'une série"), (movie, "fiche d'un film") })
        {
            if (title is null) continue;
            await ShowDetailsAsync(title); await AuditSettleAsync();
            var root = (FrameworkElement)Content;
            foreach (var kind in UiInspector.Tiles(DetailOverlay).GroupBy(x => (x.GetType(), x.Style, Math.Round(x.ActualWidth / 20), Math.Round(x.ActualHeight / 20))).Take(8))
            {
                var tile = kind.First();
                tile.BringIntoView(); await AuditSettleAsync(200);
                if (await ZoomsAsync(tile)) { zoomed++; continue; }
                still.Add(new("tuile-sans-zoom", "défaut", $"{run.Size} · {label}", UiInspector.Describe(tile), AuditBounds(tile, root),
                    $"Aucun zoom sous le pointeur ({kind.Count()} tuile(s) de ce genre), alors que les affiches zooment de {(MediaCard.HoverZoom - 1) * 100:0.#} %"));
            }
            await BackFromDetailsAsync();
        }
        if (zoomed > 0) run.Findings.AddRange(still);
        else run.Notes.Add("Aucune tuile n'a zoomé sous le pointeur simulé : le zoom des tuiles n'est pas jugé cette fois.");

        async Task<bool> ZoomsAsync(FrameworkElement tile)
        {
            var before = UiInspector.LargestScale(tile);
            tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
            await Task.Delay(320);
            var during = UiInspector.LargestScale(tile);
            tile.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
            await Task.Delay(250);
            return during > before + .004;
        }
    }

    /// <summary>
    /// What comes and goes, frame by frame (the window drawn at a quarter of its size at each refresh of WPF): a change
    /// that happens in one frame is a jump. Changes made instant on purpose say so (<c>wanted</c>).
    /// </summary>
    private async Task AuditTransitionsAsync(AuditRun run)
    {
        await AuditBackHomeAsync();
        var movie = await AuditFirstAsync("Movie") ?? await AuditFirstAsync("Series");
        var library = _homeLibraries.FirstOrDefault(x => LibrarySection.Browsed(x.CollectionType));
        var steps = new List<(string Name, Func<Task> Act, string? Wanted)>
        {
            ("Ouvrir les Réglages", () => { Settings_Click(this, new()); return Task.CompletedTask; }, null),
            ("Changer de section dans les Réglages", () => { SelectSettingsTab("playback"); return Task.CompletedTask; }, null),
            ("Fermer les Réglages", CloseSettingsAnimatedAsync, null),
            ("Ouvrir le Guide", () => { OpenGuide(); return Task.CompletedTask; }, null),
            ("Fermer le Guide", CloseGuideAsync, null),
        };
        if (library is not null)
        {
            steps.Add(("Changer de page (rail : une bibliothèque)", () => GoToPageAsync("library", library.Id), null));
            steps.Add(("Ouvrir les filtres", () => { FiltersToggle_Click(this, new()); return Task.CompletedTask; }, "voulu : « rien qui bouge pour un bouton » (ogsas, 2026-11-12)"));
            steps.Add(("Fermer les filtres", () => { CloseFilters(); return Task.CompletedTask; }, "voulu : « rien qui bouge pour un bouton » (ogsas, 2026-11-12)"));
        }
        steps.Add(("Revenir à l'Accueil", () => GoToPageAsync("home"), null));
        if (movie is not null)
        {
            steps.Add(("Ouvrir la fiche d'un titre", () => ShowDetailsAsync(movie), null));
            steps.Add(("Revenir de la fiche", BackFromDetailsAsync, null));
        }
        if (_torlinkEnabled && _downloadsOffered)
        {
            steps.Add(("Ouvrir Téléchargements", ShowTorLinkAsync, null));
            steps.Add(("Fermer Téléchargements", CloseTorLinkAsync, null));
        }
        // Twice: a jump is reported only when both rounds jump (one frame missed by the measure is not a jump).
        var rounds = new List<AuditTransition>();
        for (var round = 0; round < 2; round++)
        {
            foreach (var (name, act, wanted) in steps) if (await AuditTransitionAsync(run, name, act, wanted) is { } measured) rounds.Add(measured);
            await AuditBackHomeAsync();
        }
        foreach (var name in rounds.Select(x => x.Name).Distinct())
        {
            var both = rounds.Where(x => x.Name == name).ToList();
            var best = both.MinBy(x => x.Jump)!;
            run.Transitions.Add(best);
            if (both.Count == 2 && both.All(x => x.Jump > .8 && x.Frames <= 2))
                run.Findings.Add(new("sans-animation", best.Wanted is null ? "à juger" : "info", $"{run.Size} · transition", name, Rect.Empty,
                    $"{best.Jump:P0} du changement en une seule image, deux fois sur deux" + (best.Wanted is null ? "" : " — " + best.Wanted)));
        }
    }

    private async Task<AuditTransition?> AuditTransitionAsync(AuditRun run, string name, Func<Task> act, string? wanted)
    {
        await AuditSettleAsync(600);
        var root = (FrameworkElement)Content;
        var frames = new List<(double Ms, byte[] Pixels)>(); var clock = Stopwatch.StartNew();
        void Grab() => frames.Add((clock.Elapsed.TotalMilliseconds, Gray(Picture(root, .25, backdrop: true))));
        void Tick(object? sender, EventArgs e) { if (frames.Count < 150) Grab(); }
        Grab();
        CompositionTarget.Rendering += Tick;
        try { await act(); await Task.Delay(900); }
        finally { CompositionTarget.Rendering -= Tick; }
        Grab();
        var total = Difference(frames[0].Pixels, frames[^1].Pixels);
        if (total < 1) { run.Notes.Add($"Transition « {name} » : rien n'a changé à l'écran."); return null; }
        var steps = Enumerable.Range(1, frames.Count - 1).Select(i => (frames[i].Ms, Change: Difference(frames[i - 1].Pixels, frames[i].Pixels))).ToList();
        var moving = steps.Where(x => x.Change > total * .01).ToList();
        var jump = steps.Max(x => x.Change) / total;
        var span = moving.Count > 1 ? moving[^1].Ms - moving[0].Ms : 0;
        return new(name, moving.Count, span, jump, wanted);
    }

    private static byte[] Gray(BitmapSource picture)
    {
        var width = picture.PixelWidth; var height = picture.PixelHeight; var pixels = new byte[width * height * 4];
        picture.CopyPixels(pixels, width * 4, 0);
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)((pixels[i * 4] * 29 + pixels[i * 4 + 1] * 150 + pixels[i * 4 + 2] * 77) >> 8);
        return gray;
    }

    /// <summary>Mean change of brightness per pixel, 0 to 255.</summary>
    private static double Difference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return 255;
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
