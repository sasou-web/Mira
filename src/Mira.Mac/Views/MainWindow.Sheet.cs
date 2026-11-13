using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

// Réglages and Aide, one window over Mira, as on Windows (choice of ogsas, 2026-11-12): the page under it stays where it
// was, a still, blurred picture of the app behind the window, the rail under the picture. Échap, ⌘[, the cross or a click
// beside it closes it, back to that page, its history as it was.
public sealed partial class MainWindow
{
    /// <summary>The page under Réglages and Aide's window, shown again when it closes.</summary>
    private Page? _underSheet;
    /// <summary>The still of the app behind the window, made as it opens (<see cref="TakeBackdrop"/>).</summary>
    public Bitmap? SheetBackdrop { get; private set; }
    /// <summary>The page under the window.</summary>
    public Page? PageUnderSheet => _underSheet;
    /// <summary>A window coming or going: what its fade left to do waits for it, unless another came meanwhile.</summary>
    private int _sheetTurn;
    /// <summary>The window closed, fading out over the page under it: kept in the window the time of its fade (<see cref="Host"/>).</summary>
    private SettingsPage? _leaving;

    /// <summary>
    /// Réglages (<paramref name="help"/> null) or Aide on one of its topics, over the page shown. Already open: turned to
    /// that side at once. While the player fills the window, it shrinks first: the window opens over the library.
    /// </summary>
    public void OpenSheet(string? help = null)
    {
        if (Session is null || InParty) return;
        if (Watching && Player is { } player) player.SetMini(true);
        if (Current is SettingsPage open) { if (help is null) open.ShowSettingsSide(); else open.ShowHelp(help); return; }
        EndSlide(turn: false);
        _underSheet = Current;
        SheetBackdrop = TakeBackdrop();
        var page = new SettingsPage(this, help);
        Host(page); Light(page.Lit);
        if (!MacMotion.Reduced)
        {
            // Everything is in place at once; only the picture is late: the page under it and the rail stay drawn the
            // time the window fades in over them (the rail fading, never in the way of a click), then go behind it.
            var turn = ++_sheetTurn;
            var under = _underSheet;
            if (under is not null) under.IsVisible = true;
            _rail.ZIndex = 0; _rail.IsHitTestVisible = false; FadeRail(0);
            page.Enter();
            DispatcherTimer.RunOnce(() =>
            {
                if (turn != _sheetTurn || Current != page) return;
                if (under is not null && under != Current) under.IsVisible = false;
                _rail.ZIndex = -1; _rail.Transitions = null; _rail.Opacity = 1; _rail.IsHitTestVisible = true;
            }, TimeSpan.FromMilliseconds(230));
        }
        _ = LoadSheetAsync(page);
    }
    /// <summary>The rail fades with the window coming or going, rather than jumping over or under it.</summary>
    private void FadeRail(double to)
    {
        _rail.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
        _rail.Opacity = to;
    }
    private async Task LoadSheetAsync(SettingsPage page)
    {
        try { await page.LoadAsync(); }
        catch (Exception ex) when (Errors.Expected(ex)) { Say(ex); }
    }

    /// <summary>
    /// The window closed: the page under it is back at once, as it was, then refreshed quietly; the window fades out over
    /// it, no longer in the way of a click.
    /// </summary>
    public void CloseSettingsWindow()
    {
        if (Current is not SettingsPage page) return;
        var under = _underSheet;
        _underSheet = null; SheetBackdrop = null;
        if (under is null || !_host.Children.Contains(under)) { NavigateRoot("home"); return; }
        var turn = ++_sheetTurn;
        var fades = !MacMotion.Reduced;
        if (fades) _leaving = page;
        Host(under); Light(under.Lit);
        _rail.IsHitTestVisible = true;
        if (fades)
        {
            _rail.Transitions = null; _rail.Opacity = 0;
            Dispatcher.UIThread.Post(() => FadeRail(1), DispatcherPriority.Loaded);
            page.IsVisible = true; page.IsHitTestVisible = false;
            page.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }];
            page.Opacity = 0;
            DispatcherTimer.RunOnce(() =>
            {
                if (_leaving != page) return;
                _leaving = null; _host.Children.Remove(page);
                if (turn == _sheetTurn) _rail.Transitions = null;
            }, TimeSpan.FromMilliseconds(200));
        }
        else _rail.Opacity = 1;
        _ = RefreshUnderAsync(under);
    }
    private async Task RefreshUnderAsync(Page page)
    {
        try { await page.RefreshAsync(); }
        catch (Exception ex) when (Errors.Expected(ex)) { Say(ex); }
    }

    /// <summary>
    /// The still of the app behind the window: the window drawn small (<see cref="SheetWindow.BackdropScale"/>), then
    /// blurred and dimmed (<see cref="SheetWindow.Soften"/>) and stretched back. Made once: a live blur would be drawn again
    /// at each frame of a scroll in the window. mpv's picture (the mini-player) is not in it: it stays above, live.
    /// </summary>
    private Bitmap? TakeBackdrop()
    {
        if (_root.Bounds.Width < 1 || _root.Bounds.Height < 1) return null;
        try
        {
            var scale = SheetWindow.BackdropScale;
            int width = Math.Max(1, (int)Math.Ceiling(_root.Bounds.Width * scale)), height = Math.Max(1, (int)Math.Ceiling(_root.Bounds.Height * scale));
            using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * scale, 96 * scale));
            target.Render(_root);
            var pixels = new byte[width * height * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { target.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
            finally { handle.Free(); }
            SheetWindow.Soften(pixels, width, height);
            var picture = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            using (var buffer = picture.Lock())
                for (var y = 0; y < height; y++) System.Runtime.InteropServices.Marshal.Copy(pixels, y * width * 4, buffer.Address + y * buffer.RowBytes, width * 4);
            return picture;
        }
        // Without its picture, the window still opens: the wash over the window's background.
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException) { return null; }
    }
}
