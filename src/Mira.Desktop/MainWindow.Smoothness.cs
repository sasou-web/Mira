using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The pages' fluidity on a large, fast screen (measured by --smooth-check: on a 4K screen at 240 Hz, the wheel and the
/// pages' transitions showed one refresh in two to three). WPF's render thread draws again, at each refresh, all of the
/// window that changed and everything under it, hidden or not: the posters and the library are kept as pictures where
/// they can be (<see cref="RenderCache"/>), and the library is no longer drawn under a page that hides it.
/// </summary>
public partial class MainWindow
{
    /// <summary>The pages that hide the library entirely once in place (opaque, the size of the window).</summary>
    private UIElement[] _libraryCovers = [];
    private bool _libraryCovered;
    /// <summary>Cards taken off a page, to come back with it (<see cref="AddCards"/>).</summary>
    private readonly CardShelf _cardShelf = new(600);
    /// <summary>The cards' pictures looked at again after a scroll or a change of page, at most every 60 ms.</summary>
    private readonly DispatcherTimer _picturesTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };

    private void InitializeSmoothness()
    {
        // Pictures while they come and go (Motion.Page). The library's background, the window's under it until now, makes
        // its picture opaque: its text keeps ClearType in it. Réglages and Aide keep their window alone as one (RiseSheet).
        foreach (var page in new FrameworkElement[] { LibraryScroll, DetailOverlay, TorLinkOverlay, LoginOverlay, PartyOverlay }) Motion.SetPage(page, true);
        _libraryCovers = [TorLinkOverlay, DetailOverlay, SettingsOverlay, GuideOverlay, LoginOverlay, WelcomeOverlay, PartyOverlay, StartupVeil];
        var opacity = DependencyPropertyDescriptor.FromProperty(OpacityProperty, typeof(UIElement));
        foreach (var cover in _libraryCovers) { cover.IsVisibleChanged += (_, _) => UpdateLibraryCover(); opacity.AddValueChanged(cover, (_, _) => UpdateLibraryCover()); }
        // Not while a page comes in: a poster's picture made or let go draws the page's again.
        _picturesTimer.Tick += (_, _) => { if (Environment.TickCount64 >= MediaCard.QuietUntil) { _picturesTimer.Stop(); UpdateCardPictures(); } };
        LayoutUpdated += (_, _) => { if (!_picturesTimer.IsEnabled) _picturesTimer.Start(); };
        RenderCache.Changed += () => { foreach (var card in MediaCard.Live) if (card.Pictured) card.Pictured = true; };
        Loaded += (_, _) => UsePictureScale(); DpiChanged += (_, _) => UsePictureScale();
        // Minimized, the pictures go (memory of the graphics card, for what runs meanwhile): made again on the way back.
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) foreach (var card in MediaCard.Live) card.Pictured = false; };
    }

    /// <summary>How long a page of the library takes to come in, until its picture is let go (<see cref="Motion.Reveal"/>).</summary>
    private const int LibraryRevealMs = 380;
    /// <summary>
    /// A page of the library coming in (a page of the rail, one left and found again): one picture while it does, which
    /// nothing inside must change at each refresh. The images that arrive meanwhile come with it, set together without
    /// a fade of their own (<see cref="MediaCard.QuietUntil"/>); the placeholders' pulse waits for its end.
    /// </summary>
    private void RevealLibrary()
    {
        MediaCard.QuietUntil = Environment.TickCount64 + LibraryRevealMs;
        Motion.Reveal(LibraryScroll, 260, 10);
    }

    /// <summary>Pictures made at the scale the pages are drawn at: the screen's, times their zoom (Windows' text size).</summary>
    private void UsePictureScale() => RenderCache.UseScale(VisualTreeHelper.GetDpi(this).DpiScaleX * _zoom);

    /// <summary>
    /// The library no longer drawn under a page that hides it all (a title's page, Réglages, Téléchargements…): WPF draws
    /// all that lies under what changes, hidden or not, so each frame of a scroll in Réglages drew the library under it
    /// too. Opacity 0 leaves it as it is otherwise (laid out, focusable, its pictures kept); it is drawn again as soon as
    /// the page starts to go.
    /// </summary>
    private void UpdateLibraryCover()
    {
        UpdateSheetCover();
        var covered = false;
        foreach (var cover in _libraryCovers) if (cover.IsVisible && cover.Opacity >= .995) { covered = true; break; }
        if (covered == _libraryCovered) return;
        _libraryCovered = covered; LibraryShell.Opacity = covered ? 0 : 1;
    }
    /// <summary>The library drawn again under <paramref name="leaving"/>, before it starts to go (unless another page still covers it).</summary>
    private void UncoverLibrary(UIElement leaving)
    {
        foreach (var cover in _libraryCovers) if (cover != leaving && cover.IsVisible && cover.Opacity >= .995) return;
        _libraryCovered = false; LibraryShell.Opacity = 1;
    }

    /// <summary>
    /// Each card keeps its picture within a window's height (and width) of the window, and lets it go beyond two, or on
    /// a page put away: a picture takes memory of the graphics card (0.9 MB for a poster at 175 %), and WPF keeps it once
    /// made, even for a card scrolled far away.
    /// </summary>
    private void UpdateCardPictures()
    {
        if (WindowState == WindowState.Minimized || ActualWidth <= 0 || ActualHeight <= 0) return;
        var near = new Rect(-ActualWidth, -ActualHeight, 3 * ActualWidth, 3 * ActualHeight);
        var far = new Rect(-2 * ActualWidth, -2 * ActualHeight, 5 * ActualWidth, 5 * ActualHeight);
        // The cards of a row or of the grid share their place in the window: found once for each.
        var places = new Dictionary<Visual, GeneralTransform?>();
        foreach (var card in MediaCard.Live)
        {
            if (!card.IsVisible) { card.Pictured = false; continue; }
            if (VisualTreeHelper.GetParent(card) is not Visual parent) continue;
            if (!places.TryGetValue(parent, out var place))
            {
                try { place = parent.IsDescendantOf(this) ? parent.TransformToAncestor(this) : null; }
                catch (InvalidOperationException) { place = null; }
                places[parent] = place;
            }
            if (place is null) continue;
            var bounds = place.TransformBounds(new Rect((Point)VisualTreeHelper.GetOffset(card), card.RenderSize));
            if (bounds.IntersectsWith(near)) card.Pictured = true;
            else if (!bounds.IntersectsWith(far)) card.Pictured = false;
        }
    }

    /// <summary>Another account: what was kept for the previous one's pages goes.</summary>
    private void ForgetKeptCards()
    {
        _cardShelf.Clear();
        foreach (var key in _shelves.Keys.Where(key => !HomeShelves.Children.Contains(_shelves[key].Section)).ToList()) _shelves.Remove(key);
    }

    /// <summary>
    /// Cards taken off a page, by what they show (<see cref="MediaCard.Key"/>), put back when their page comes back; the
    /// longest unused go past <c>capacity</c>. A card on the shelf holds no artwork (<see cref="MediaCard.Shelve"/>).
    /// </summary>
    private sealed class CardShelf(int capacity)
    {
        private readonly Dictionary<string, LinkedListNode<MediaCard>> _cards = [];
        private readonly LinkedList<MediaCard> _order = new();
        private static string Key(string shown, bool wide, bool found) => (wide ? "w" : "p") + (found ? "f|" : "-|") + shown;
        public void Put(MediaCard card)
        {
            card.Shelve();
            var key = Key(card.RenderKey, card.Wide, card.Found);
            if (_cards.Remove(key, out var older)) _order.Remove(older);
            _cards[key] = _order.AddLast(card);
            while (_order.Count > capacity) { var oldest = _order.First!.Value; _order.RemoveFirst(); _cards.Remove(Key(oldest.RenderKey, oldest.Wide, oldest.Found)); }
        }
        public MediaCard? Take(string shown, bool wide, bool found)
        {
            if (!_cards.Remove(Key(shown, wide, found), out var node)) return null;
            _order.Remove(node); return node.Value;
        }
        public void Clear() { _cards.Clear(); _order.Clear(); }
    }
}
