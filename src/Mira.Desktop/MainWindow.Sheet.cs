using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Réglages and Aide, one window over Mira (choice of ogsas, 2026-11-12): the app behind stays where it was, a still,
/// blurred picture of it under a wash, the window in the middle with its list on the left. Réglages | Aide at the top
/// of the list turns the window over at once; Échap, the cross or a click beside it closes it, back to the page under
/// it (a title's page stays). Its search finds every setting, changed right where it is found.
/// </summary>
public partial class MainWindow
{
    /// <summary>The side of the window shown: Réglages or Aide, open and not leaving.</summary>
    private bool SheetOpen => Shown(SettingsOverlay) || Shown(GuideOverlay);
    private static bool Shown(FrameworkElement overlay) => overlay.Visibility == Visibility.Visible && overlay.IsHitTestVisible;

    /// <summary>
    /// <paramref name="sheet"/> shown: from the other side at once, on the same picture; otherwise over a new picture of
    /// the app, fading in while the window rises a little. <paramref name="at"/>: there at once (back from the player).
    /// </summary>
    private void ShowSheet(FrameworkElement sheet, bool at = false)
    {
        var other = sheet == SettingsOverlay ? GuideOverlay : SettingsOverlay;
        if (Shown(sheet)) return;
        if (Shown(other)) { Motion.Show(sheet); Motion.Hide(other); return; }
        if (other.Visibility == Visibility.Visible) Motion.Hide(other);
        // Opened again while it fades: back on the picture it has, never one of itself fading.
        if (sheet.Visibility == Visibility.Visible && (sheet == SettingsOverlay ? SettingsBackdrop : GuideBackdrop).Source is not null) { Motion.Reveal(sheet, 200, 0); return; }
        TakeBackdrop();
        if (at) { Motion.Show(sheet); return; }
        Motion.Reveal(sheet, 200, 0);
        RiseSheet(sheet == SettingsOverlay ? SettingsSheet : GuideSheet);
    }
    /// <summary>The window rises into place as one picture: nothing inside it is drawn again meanwhile.</summary>
    private static void RiseSheet(Squircle window)
    {
        if (Motion.Reduced) return;
        var move = window.RenderTransform as TranslateTransform ?? new TranslateTransform(); window.RenderTransform = move;
        window.CacheMode = RenderCache.Picture;
        Motion.Animate(move, TranslateTransform.YProperty, 0, 300, 14, Motion.Enter);
        _ = LetGoAsync();
        async Task LetGoAsync() { await Task.Delay(360); if (move.Y == 0) window.ClearValue(UIElement.CacheModeProperty); }
    }
    /// <summary>The window fades out as one picture, back to the page under it.</summary>
    private static async Task HideSheetAsync(FrameworkElement overlay, Squircle window)
    {
        if (!Motion.Reduced && overlay.Visibility == Visibility.Visible) window.CacheMode = RenderCache.Picture;
        await Motion.HideAsync(overlay);
        if (overlay.Visibility != Visibility.Visible) window.ClearValue(UIElement.CacheModeProperty);
    }
    /// <summary>The picture let go once neither side shows: it holds the library as it was.</summary>
    private void ForgetBackdrop()
    {
        if (SettingsOverlay.Visibility == Visibility.Visible || GuideOverlay.Visibility == Visibility.Visible) return;
        SettingsBackdrop.Source = GuideBackdrop.Source = null;
    }

    /// <summary>
    /// The still of the app under the window: what the screen shows of Mira, copied small (<see cref="SheetWindow.BackdropScale"/>),
    /// then blurred and dimmed (<see cref="SheetWindow.Soften"/>) and stretched back. Made once: a live blur would be drawn
    /// again at each frame of a scroll in the window. Copied from the screen, as Windows composed it (mpv's picture
    /// included), in a few milliseconds: WPF drawing the whole window again by itself (RenderTargetBitmap) held the
    /// interface 100 ms on a runner. Only while Mira is in front and whole on its screens: otherwise the screen holds
    /// something else there, and WPF draws it (no mpv picture then; a check's window, or one half off its screen).
    /// In high contrast, no picture.
    /// </summary>
    private void TakeBackdrop()
    {
        BitmapSource? picture = null;
        var handle = new WindowInteropHelper(this).Handle;
        if (!SystemParameters.HighContrast && Content is FrameworkElement root && root.ActualWidth >= 1 && root.ActualHeight >= 1
            && handle != IntPtr.Zero && ForegroundWindow() == handle && !IsIconic(handle))
        {
            try
            {
                var scale = SheetWindow.BackdropScale;
                int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * scale)), height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * scale));
                var from = root.PointToScreen(new Point(0, 0)); var to = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
                int left = (int)Math.Round(from.X), top = (int)Math.Round(from.Y), right = (int)Math.Round(to.X), bottom = (int)Math.Round(to.Y);
                int screenLeft = GetSystemMetrics(76), screenTop = GetSystemMetrics(77), screenRight = screenLeft + GetSystemMetrics(78), screenBottom = screenTop + GetSystemMetrics(79);
                if (right > left && bottom > top && left >= screenLeft && top >= screenTop && right <= screenRight && bottom <= screenBottom
                    && CopyScreen(IntPtr.Zero, left, top, right - left, bottom - top, width, height) is { } pixels)
                    picture = Softened(pixels, width, height);
            }
            // Without its picture, the window still opens: the wash over the page.
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { picture = null; }
        }
        // Not in front, or partly off its screens: the window as Windows composed it all the same, asked of Windows.
        if (picture is null && !SystemParameters.HighContrast && handle != IntPtr.Zero && !IsIconic(handle) && ClientArea(handle, out var client)
            && client.Right > 0 && client.Bottom > 0 && Content is FrameworkElement whole && whole.ActualWidth >= 1 && whole.ActualHeight >= 1)
        {
            var scale = SheetWindow.BackdropScale;
            int width = Math.Max(1, (int)Math.Ceiling(whole.ActualWidth * scale)), height = Math.Max(1, (int)Math.Ceiling(whole.ActualHeight * scale));
            if (CopyScreen(handle, 0, 0, client.Right, client.Bottom, width, height) is { } pixels) picture = Softened(pixels, width, height);
        }
        // Last, WPF drawing it all again itself (long: a tenth of a second on a runner).
        if (picture is null && !SystemParameters.HighContrast && Content is FrameworkElement drawn && drawn.ActualWidth >= 1 && drawn.ActualHeight >= 1)
        {
            try
            {
                var scale = SheetWindow.BackdropScale;
                int width = Math.Max(1, (int)Math.Ceiling(drawn.ActualWidth * scale)), height = Math.Max(1, (int)Math.Ceiling(drawn.ActualHeight * scale));
                var target = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                target.Render(drawn);
                var pixels = new byte[width * height * 4];
                target.CopyPixels(pixels, width * 4, 0);
                picture = Softened(pixels, width, height);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { picture = null; }
        }
        SettingsBackdrop.Source = GuideBackdrop.Source = picture;
    }
    private static BitmapSource Softened(byte[] pixels, int width, int height)
    {
        SheetWindow.Soften(pixels, width, height);
        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        picture.Freeze();
        return picture;
    }
    /// <summary>
    /// The screen's pixels in this rectangle, shrunk to <paramref name="toWidth"/> × <paramref name="toHeight"/> (blue,
    /// green, red, then a byte to ignore). With a <paramref name="window"/>, its client area as Windows composed it
    /// (PrintWindow, whole content), wherever it is and whatever covers it.
    /// </summary>
    private static byte[]? CopyScreen(IntPtr window, int left, int top, int width, int height, int toWidth, int toHeight)
    {
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, printed = IntPtr.Zero, printedBitmap = IntPtr.Zero, printedPrevious = IntPtr.Zero;
        try
        {
            var source = screen;
            if (window != IntPtr.Zero)
            {
                printed = CreateCompatibleDC(screen);
                printedBitmap = CreateCompatibleBitmap(screen, width, height);
                if (printed == IntPtr.Zero || printedBitmap == IntPtr.Zero) return null;
                printedPrevious = SelectObject(printed, printedBitmap);
                // PW_CLIENTONLY | PW_RENDERFULLCONTENT: what DWM composed, DirectX content included.
                if (!PrintWholeWindow(window, printed, 3)) return null;
                source = printed;
            }
            memory = CreateCompatibleDC(screen);
            // Top-down rows (a negative height), 32 bits a pixel: the order SheetWindow.Soften reads.
            var header = new BitmapHeader { Size = 40, Width = toWidth, Height = -toHeight, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            var previous = SelectObject(memory, bitmap);
            // Each pixel the mean of those it stands for: no shimmer from the text left out.
            SetStretchBltMode(memory, 4);
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            var copied = StretchBlt(memory, 0, 0, toWidth, toHeight, source, left, top, width, height, 0x00CC0020);
            GdiFlush();
            SelectObject(memory, previous);
            if (!copied) return null;
            var pixels = new byte[toWidth * toHeight * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (printedPrevious != IntPtr.Zero) SelectObject(printed, printedPrevious);
            if (printedBitmap != IntPtr.Zero) DeleteObject(printedBitmap);
            if (printed != IntPtr.Zero) DeleteDC(printed);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPixelsPerMeter, YPixelsPerMeter, ColoursUsed, ColoursImportant; }
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr ForegroundWindow();
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private readonly record struct ClientRect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientArea(IntPtr window, out ClientRect rect);
    [DllImport("user32.dll", EntryPoint = "PrintWindow")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWholeWindow(IntPtr window, IntPtr context, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr context, int width, int height);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr context);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr context);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr context, ref BitmapHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr context, IntPtr picture);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr picture);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr context);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr context, int mode);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetBrushOrgEx(IntPtr context, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();

    /// <summary>
    /// The pages under the window (a title's page, Téléchargements, a party) are no longer drawn while the window covers
    /// them whole with its picture, as the library is not (<see cref="UpdateLibraryCover"/>): a scroll in Réglages would
    /// draw them again at each frame. An empty clip leaves them laid out and as they were; gone as soon as it fades.
    /// </summary>
    private void UpdateSheetCover()
    {
        var covered = (SettingsOverlay.IsVisible && SettingsOverlay.Opacity >= .995 && SettingsBackdrop.Source is not null)
            || (GuideOverlay.IsVisible && GuideOverlay.Opacity >= .995 && GuideBackdrop.Source is not null);
        var clip = covered ? Geometry.Empty : null;
        foreach (var page in new UIElement[] { DetailOverlay, TorLinkOverlay, PartyOverlay })
            if (!ReferenceEquals(page.Clip, clip)) page.Clip = clip;
    }

    // A click beside the window closes it: pressed and let go there (a list closing under the pointer is no close).
    private bool _scrimPressed;
    private void SheetScrim_MouseDown(object sender, MouseButtonEventArgs e) { _scrimPressed = true; e.Handled = true; }
    private void SheetScrim_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrimPressed) return;
        _scrimPressed = false; e.Handled = true;
        CloseSheet();
    }
    private void CloseSheet()
    {
        if (Shown(GuideOverlay)) _ = CloseGuideAsync();
        else if (Shown(SettingsOverlay)) _ = CloseSettingsAnimatedAsync();
    }
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => _ = CloseSettingsAnimatedAsync();
    private void SheetToHelp_Click(object sender, RoutedEventArgs e) { if (!Shown(GuideOverlay)) OpenGuide(); }
    private void SheetToSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Shown(SettingsOverlay)) return;
        var tab = _settingsTab;
        Settings_Click(sender, e);
        if (tab != "playback") SelectSettingsTab(tab, animate: false);
    }
    /// <summary>Ctrl+F in the window: its own search, not the library's.</summary>
    private bool FocusSheetSearch()
    {
        var box = Shown(GuideOverlay) ? GuideSearchBox : Shown(SettingsOverlay) ? SettingsSearchBox : null;
        if (box is null) return false;
        box.Focus(); box.SelectAll(); return true;
    }
    /// <summary>Échap in a search with something typed: the search goes first, then the window.</summary>
    private bool ClearSheetSearch()
    {
        if (Shown(GuideOverlay) && GuideSearchBox.Text.Length > 0) { GuideSearchBox.Clear(); GuideSearchBox.Focus(); return true; }
        if (Shown(SettingsOverlay) && SettingsSearchBox.Text.Length > 0) { SettingsSearchBox.Clear(); SettingsSearchBox.Focus(); return true; }
        return false;
    }
    /// <summary>Entrée opens what was found first; ↓ goes to it.</summary>
    private void SheetSearch_KeyDown(object sender, KeyEventArgs e)
    {
        var settings = sender == SettingsSearchBox;
        if (e.Key == Key.Enter) { e.Handled = true; if (settings) OpenFirstSetting(); else OpenFirstTopic(); }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            if (settings) SettingsPage.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            else GuideNavigation.Children.OfType<Button>().FirstOrDefault(x => x.Visibility == Visibility.Visible)?.Focus();
        }
    }

    /// <summary>
    /// A button of the window's list: its icon, its name, room on the right for what the search found there (or the
    /// arrow of a page that opens in the browser). Read by its name.
    /// </summary>
    private static void DrawSheetNav(Button button, string icon, bool outside = false)
    {
        if (button.Content is not string label) return;
        var row = new DockPanel { LastChildFill = true };
        var count = new Squircle { Background = Palette.Get("Raised2"), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Child = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Palette.Get("TextSecondary") } };
        DockPanel.SetDock(count, Dock.Right); row.Children.Add(count);
        if (outside) { var arrow = new Icon { Kind = "external", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(arrow, Dock.Right); row.Children.Add(arrow); }
        var glyph = new Icon { Kind = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 12, 0) }; DockPanel.SetDock(glyph, Dock.Left); row.Children.Add(glyph);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Content = row; AutomationProperties.SetName(button, label);
    }
    /// <summary>The settings a search found in this section, beside its name (none: the badge goes).</summary>
    private static void ShowSheetCount(Button button, int? count)
    {
        if (button.Content is not DockPanel { Children: [Squircle { Child: TextBlock text } badge, ..] }) return;
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        text.Text = count?.ToString(Lang.Culture) ?? "";
    }
    /// <summary>The window's list: the page shown lit (as the rail's), the others quiet; during a search, those with nothing found dimmer.</summary>
    private static void LightSheetNav(Panel list, string? shown, Func<Button, bool>? found = null)
    {
        foreach (var button in list.Children.OfType<Button>())
        {
            var active = shown is not null && button.Tag?.ToString() == shown;
            button.Background = active ? Palette.Get("Text") : Brushes.Transparent;
            button.Foreground = active ? Palette.Get("OnAccent") : found is null || found(button) ? Palette.Get("TextBody") : Palette.Get("Faint");
        }
    }

    // The search of Réglages: every section's settings at once, those found kept where they are (their switches and
    // lists work there), the rest folded away. Rows Mira shows or hides by itself (an administrator's) keep doing so:
    // folding sets their height, never their visibility.
    private string _settingsTab = "playback";
    private bool _settingsSearching;
    private readonly Dictionary<FrameworkElement, (object Margin, double MaxHeight, KeyboardNavigationMode Tab)> _folded = [];
    private readonly Dictionary<StackPanel, TextBlock> _settingsCrumbs = [];
    private readonly Dictionary<Expander, bool> _searchOpened = [];
    private (StackPanel Panel, Button Nav)[] SettingsSections =>
    [
        (PlaybackSettings, PlaybackSettingsNav), (SubtitleSettings, SubtitleSettingsNav), (AppearanceSettings, AppearanceSettingsNav), (ServerSettings, ServerSettingsNav),
        (UpdateSettings, UpdateSettingsNav), (PrivacySettings, PrivacySettingsNav), (ReportSettings, ReportSettingsNav),
        (SharingSettings, SharingSettingsNav), (CareSettings, CareSettingsNav), (TorLinkSettings, TorLinkSettingsNav)
    ];

    private void SettingsSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var query = SettingsSearchBox.Text;
        SettingsSearchPlaceholder.Visibility = SettingsSearchKeys.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchClear.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (query.Trim().Length == 0) EndSettingsSearch(); else SearchSettings(query.Trim());
    }
    private void SettingsSearchClear_Click(object sender, RoutedEventArgs e) { SettingsSearchBox.Clear(); SettingsSearchBox.Focus(); }

    private void SearchSettings(string query)
    {
        if (!_settingsSearching) { _settingsSearching = true; if (PrivacySettings.Children.Count == 0) ShowPrivacy(); }
        WatchViewers(false);
        var total = 0;
        var counts = new Dictionary<Button, int>();
        foreach (var (panel, nav) in SettingsSections)
        {
            var count = 0;
            if (nav.Visibility == Visibility.Visible)
                foreach (var child in panel.Children.OfType<FrameworkElement>())
                {
                    if (child is Expander { Content: Panel inside } expander)
                    {
                        var any = false;
                        foreach (var row in inside.Children.OfType<FrameworkElement>())
                        {
                            var hit = row.Visibility == Visibility.Visible && SheetWindow.Matches(Words(row), query);
                            Fold(row, !hit); if (hit) { any = true; count++; }
                        }
                        Fold(expander, !any);
                        if (any && !_searchOpened.ContainsKey(expander)) { _searchOpened[expander] = expander.IsExpanded; expander.IsExpanded = true; }
                        continue;
                    }
                    var match = child is Border or Squircle && child.Visibility == Visibility.Visible && SheetWindow.Matches(Words(child), query);
                    Fold(child, !match); if (match) count++;
                }
            panel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            panel.BeginAnimation(OpacityProperty, null); panel.Opacity = 1;
            var crumb = SettingsCrumb(panel, AutomationProperties.GetName(nav));
            crumb.Visibility = panel.Visibility;
            counts[nav] = count; ShowSheetCount(nav, count); total += count;
        }
        // The headings between sections: none before the first one found.
        var first = true;
        foreach (var (panel, _) in SettingsSections)
            if (panel.Visibility == Visibility.Visible) { _settingsCrumbs[panel].Margin = new Thickness(2, first ? 0 : 14, 0, 8); first = false; }
        LightSheetNav(SettingsNavigation, null, button => counts.GetValueOrDefault(button) > 0);
        SettingsSectionTitle.Text = total == 0 ? L("Aucun résultat", "No results") : Lang.Count(total, "résultat", "résultats", "result", "results");
        SettingsSectionDescription.Text = total == 0
            ? L($"Aucun réglage ne correspond à « {query} ».", $"No setting matches “{query}”.")
            : L($"Pour « {query} », dans toutes les sections. Change-les ici même ; Entrée ouvre le premier dans sa section.", $"For “{query}”, in every section. Change them right here; Enter opens the first one in its section.");
        SettingsScroll.ScrollToTop();
    }
    /// <summary>Back to the section open before the search, everything as it was.</summary>
    private void EndSettingsSearch()
    {
        if (!_settingsSearching) return;
        _settingsSearching = false;
        foreach (var element in _folded.Keys.ToList()) Fold(element, false);
        foreach (var (expander, open) in _searchOpened) expander.IsExpanded = open;
        _searchOpened.Clear();
        foreach (var crumb in _settingsCrumbs.Values) crumb.Visibility = Visibility.Collapsed;
        foreach (var (_, nav) in SettingsSections) ShowSheetCount(nav, null);
        SelectSettingsTab(_settingsTab, animate: false);
    }
    /// <summary>Entrée in the search: the first setting found, in its own section, with the keyboard on it.</summary>
    private void OpenFirstSetting()
    {
        if (!_settingsSearching) { SettingsPage.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); return; }
        foreach (var (panel, nav) in SettingsSections)
        {
            if (panel.Visibility != Visibility.Visible) continue;
            var row = panel.Children.OfType<FrameworkElement>().SelectMany(x => x is Expander { Content: Panel inside } ? inside.Children.OfType<FrameworkElement>() : new[] { x })
                .FirstOrDefault(x => x.Visibility == Visibility.Visible && !_folded.ContainsKey(x) && x is Border or Squircle);
            _settingsTab = nav.Tag?.ToString() ?? _settingsTab;
            SettingsSearchBox.Clear();
            if (row is null) return;
            _ = Dispatcher.BeginInvoke(() =>
            {
                row.BringIntoView();
                if (!row.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) SettingsPage.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }, DispatcherPriority.Loaded);
            return;
        }
    }
    /// <summary>A section's name above what the search found in it, made once, kept out of view otherwise.</summary>
    private TextBlock SettingsCrumb(StackPanel panel, string title)
    {
        if (_settingsCrumbs.TryGetValue(panel, out var crumb)) { crumb.Text = title; return crumb; }
        crumb = new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Faint"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetHeadingLevel(crumb, AutomationHeadingLevel.Level2);
        SettingsPage.Children.Insert(SettingsPage.Children.IndexOf(panel), crumb);
        _settingsCrumbs[panel] = crumb;
        return crumb;
    }
    /// <summary>Folded away by a search (no height, out of the Tab order), or back as it was.</summary>
    private void Fold(FrameworkElement element, bool fold)
    {
        if (fold)
        {
            if (_folded.ContainsKey(element)) return;
            _folded[element] = (element.ReadLocalValue(MarginProperty), element.MaxHeight, KeyboardNavigation.GetTabNavigation(element));
            element.Margin = new Thickness(0); element.MaxHeight = 0; KeyboardNavigation.SetTabNavigation(element, KeyboardNavigationMode.None);
            return;
        }
        if (!_folded.Remove(element, out var was)) return;
        if (was.Margin == DependencyProperty.UnsetValue) element.ClearValue(MarginProperty); else element.Margin = (Thickness)was.Margin;
        element.MaxHeight = was.MaxHeight; KeyboardNavigation.SetTabNavigation(element, was.Tab);
    }
    /// <summary>What a row says: its texts and the names of its controls, as a screen reader reads them.</summary>
    private static string Words(DependencyObject root)
    {
        var words = new StringBuilder();
        void Read(object node)
        {
            switch (node)
            {
                case TextBlock block: words.Append(block.Text).Append(' '); break;
                case string text: words.Append(text).Append(' '); return;
                case HeaderedContentControl { Header: string header }: words.Append(header).Append(' '); break;
            }
            if (node is not DependencyObject element) return;
            if (AutomationProperties.GetName(element) is { Length: > 0 } name) words.Append(name).Append(' ');
            foreach (var child in LogicalTreeHelper.GetChildren(element)) Read(child);
        }
        Read(root);
        return words.ToString();
    }
}
