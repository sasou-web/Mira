using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The same hit test follows the pointer and the page. A page scrolling under a still pointer lights no card on its way,
/// each of which would lift and drop again in passing (its picture drawn anew at each frame of it): the card it leaves goes
/// dark, the one under the pointer lights up once the page rests, as in a browser and on Mac (Ui.Scrolling).
/// </summary>
public partial class MainWindow
{
    private MediaCard? _hoverCard;
    private bool _hoverRefreshPending;
    private readonly DispatcherTimer _hoverWarm = new() { Interval = TimeSpan.FromMilliseconds(180) };
    /// <summary>Ticks while the page scrolls; the first tick <see cref="ScrollRestMs"/> after its last step finds it at rest.</summary>
    private readonly DispatcherTimer _scrollRest = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private const int ScrollRestMs = 100;
    private long _scrolledAt;
    /// <summary>The page moved under the pointer moments ago: no card lights up, not even under a pointer that moves.</summary>
    private bool PageScrolling => _scrollRest.IsEnabled;
    private void InitializePreview()
    {
        LibraryShell.PreviewMouseMove += (_, e) => TrackHoverPointer(e.GetPosition(LibraryShell));
        LibraryShell.MouseLeave += (_, _) => { if (_hoverCard is { } card && !(_keyboardNavigation && card.IsKeyboardFocusWithin)) SetHoverCard(null); };
        _hoverWarm.Tick += (_, _) => { _hoverWarm.Stop(); if (_hoverCard is { } card) _ = WarmItemAsync(card.Item); };
        _scrollRest.Tick += (_, _) => { if (Environment.TickCount64 - _scrolledAt < ScrollRestMs) return; _scrollRest.Stop(); CollectAtRest(); HoverCardUnderPointer(); };
    }
    /// <summary>The last collection <see cref="CollectAtRest"/> made (its GC index), for the smoothness check to tell it apart.</summary>
    internal static long CollectedAtRest;
    /// <summary>
    /// A collection that is due anyway (less than 30 % of its budget left: GCCollectionMode.Optimized) is made now, the page
    /// at rest, rather than a few frames into the next scroll, where its pause (9 to 21 ms measured on a 4K 240 Hz screen)
    /// is a jolt; before the card under the pointer lights up, so it does not hitch its zoom. Never a full blocking one.
    /// </summary>
    private static void CollectAtRest()
    {
        var before = GC.CollectionCount(0);
        GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
        if (GC.CollectionCount(0) != before) CollectedAtRest = GC.GetGCMemoryInfo(GCKind.Ephemeral).Index;
    }
    /// <summary>The page scrolled: what is under the pointer changed without the pointer moving.</summary>
    private void NoteScroll()
    {
        _scrolledAt = Environment.TickCount64; if (!_scrollRest.IsEnabled) _scrollRest.Start();
        RefreshHover();
    }
    /// <summary>Cards came, went or moved under the pointer.</summary>
    private void RefreshHover()
    {
        // ScrollChanged runs after layout. Coalesce nested viewers into one hit test before paint.
        if (_hoverRefreshPending) return;
        _hoverRefreshPending = true;
        Dispatcher.BeginInvoke(() => { _hoverRefreshPending = false; HoverCardUnderPointer(); }, DispatcherPriority.Render);
    }
    private bool CanHover() => !((_playing && !_miniPlayer) || SettingsOverlay.IsVisible || DetailOverlay.IsVisible || LoginOverlay.IsVisible);
    private void CardHoverEntered(MediaCard card) { if (CanHover() && !PageScrolling) SetHoverCard(card); }
    /// <summary>While the page scrolls, the card under the pointer only keeps a light it already had.</summary>
    private void TrackHoverPointer(Point point)
    {
        if (!CanHover()) return;
        var card = CardAtLibraryPoint(point);
        SetHoverCard(PageScrolling && card != _hoverCard ? null : card);
    }
    private void CardFocusEntered(MediaCard card) { if (CanHover()) SetHoverCard(card); }
    private void CardHoverLeft(MediaCard card) { if (_hoverCard == card) SetHoverCard(null); else card.SetHover(false); }
    private void SetHoverCard(MediaCard? card)
    {
        if (_hoverCard == card) return;
        _hoverCard?.SetHover(false); _hoverWarm.Stop(); _hoverCard = card;
        if (card is null) return;
        card.SetHover(true);
        // Only prefetch when intent persists. Rendering feedback never waits for this.
        _hoverWarm.Start();
    }
    private MediaCard? CardAtLibraryPoint(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= LibraryShell.ActualWidth || point.Y >= LibraryShell.ActualHeight) return null;
        var over = LibraryShell.InputHitTest(point) as DependencyObject;
        for (; over is not null && over is not MediaCard; over = over is Visual ? VisualTreeHelper.GetParent(over) : LogicalTreeHelper.GetParent(over)) { }
        return over as MediaCard;
    }
    /// <summary>A check's pointer resting on the library (no real pointer is over its window, out of sight for --smooth-check);
    /// null, the real one (always, in the packages: Checks/ is left out).</summary>
    private Point? _hoverPointerForCheck = null;
    private void HoverCardUnderPointer()
    {
        if (_hoverPointerForCheck is { } resting) { TrackHoverPointer(resting); return; }
        if (!CanHover() || !IsActive || !LibraryShell.IsMouseOver) { SetHoverCard(null); return; }
        TrackHoverPointer(Mouse.GetPosition(LibraryShell));
    }
    private void ClosePreview() { _hoverWarm.Stop(); SetHoverCard(null); }
    private async Task WarmItemAsync(MediaItem item)
    {
        try { if (_metadata is { } metadata) await metadata.ItemAsync(item.SeriesId ?? item.Id); }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
}
