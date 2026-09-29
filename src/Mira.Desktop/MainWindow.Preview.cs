using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>The same hit test follows the pointer and the moving page; scrolling never disables hover.</summary>
public partial class MainWindow
{
    private MediaCard? _hoverCard;
    private bool _hoverRefreshPending;
    private readonly DispatcherTimer _hoverWarm = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private void InitializePreview()
    {
        LibraryShell.PreviewMouseMove += (_, e) => TrackHoverPointer(e.GetPosition(LibraryShell));
        LibraryShell.MouseLeave += (_, _) => { if (_hoverCard is { } card && !(_keyboardNavigation && card.IsKeyboardFocusWithin)) SetHoverCard(null); };
        _hoverWarm.Tick += (_, _) => { _hoverWarm.Stop(); if (_hoverCard is { } card) _ = WarmItemAsync(card.Item); };
    }
    private void NoteScroll()
    {
        // ScrollChanged runs after layout. Coalesce nested viewers into one hit test before paint.
        if (_hoverRefreshPending) return;
        _hoverRefreshPending = true;
        Dispatcher.BeginInvoke(() => { _hoverRefreshPending = false; HoverCardUnderPointer(); }, DispatcherPriority.Render);
    }
    private bool CanHover() => !((_playing && !_miniPlayer) || SettingsOverlay.IsVisible || DetailOverlay.IsVisible || LoginOverlay.IsVisible);
    private void CardHoverEntered(MediaCard card) { if (CanHover()) SetHoverCard(card); }
    private void TrackHoverPointer(Point point) { if (CanHover()) SetHoverCard(CardAtLibraryPoint(point)); }
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
    private void HoverCardUnderPointer()
    {
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
