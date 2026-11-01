using System.Windows;
using System.Windows.Input;

namespace Mira.Desktop;

// The bottom bar's buttons in a window of their own (TransportLayer), over PlayerTransportSlot.
public partial class MainWindow
{
    /// <summary>
    /// Every frame of an animation in a layered window sends the whole window to Windows: in the controls' window, as
    /// large as the video (3840 × 2160 on a 4K screen), a button's hover could not keep up with the screen. The buttons'
    /// row has a window of its own size; the controls' window behind it only changes when they show, hide or update.
    /// </summary>
    private void InitializeTransportLayer()
    {
        TransportLayer.Region = TransportRegion;
        PlayerSurface.PreviewKeyDown += PlayerTab;
        PlayerTransport.PreviewKeyDown += PlayerTab;
        // The slot moves with the surface and the bar (mini-player, fullscreen): the row follows it once laid out.
        SizeChangedEventHandler follow = (_, _) => Dispatcher.BeginInvoke(() => { if (TransportLayer.IsOpen) RepositionPlayerLayers(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        PlayerSurface.SizeChanged += follow; PlayerControlsBody.SizeChanged += follow; PlayerTransportSlot.SizeChanged += follow;
    }
    private Rect TransportRegion()
    {
        if (!PlayerTransportSlot.IsVisible || PresentationSource.FromVisual(PlayerTransportSlot) is null) return Rect.Empty;
        return new Rect(PlayerTransportSlot.TranslatePoint(new Point(), PlayerSurface), PlayerTransportSlot.RenderSize);
    }
    /// <summary>The overlays in their order, bottom to top: the controls, their buttons' row, the pause and play flash.</summary>
    private void RepositionPlayerLayers()
    {
        var moved = PlayerOverlayLayer.Reposition();
        if (TransportLayer.Reposition() | moved) { TransportLayer.Raise(); moved = true; }
        if (PlayStateLayer.Reposition() | moved) PlayStateLayer.Raise();
    }
    /// <summary>A point of a player control in the controls' surface, whichever overlay window it is in.</summary>
    private Point InSurface(FrameworkElement element, Point point) =>
        PresentationSource.FromVisual(element) == PresentationSource.FromVisual(PlayerSurface) ? element.TranslatePoint(point, PlayerSurface) : PlayerSurface.PointFromScreen(element.PointToScreen(point));
    /// <summary>
    /// Tab goes from the controls' window to the buttons' row and back: each window stops at its last control
    /// (KeyboardNavigation Contained), then the other takes the focus.
    /// </summary>
    private void PlayerTab(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Tab || (Keyboard.Modifiers & ~ModifierKeys.Shift) != 0 || sender is not FrameworkElement root) return;
        if (Keyboard.FocusedElement is not UIElement focused || !(ReferenceEquals(focused, root) || root.IsAncestorOf(focused))) return;
        var back = Keyboard.Modifiers == ModifierKeys.Shift;
        _keyboardNavigation = true;
        if (focused.MoveFocus(new TraversalRequest(back ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next))) { e.Handled = true; return; }
        var other = ReferenceEquals(root, PlayerTransport) ? (FrameworkElement)PlayerSurface : PlayerTransport;
        if (other.IsVisible && other.IsHitTestVisible) other.MoveFocus(new TraversalRequest(back ? FocusNavigationDirection.Last : FocusNavigationDirection.First));
        e.Handled = true;
    }
}
