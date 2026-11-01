using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The player going from one place to another (reduced to the mini-player, expanded from it, closed): the picture as
/// mpv shows it now moves and resizes between the two places, rounded on the way. The video is a native window that
/// WPF can neither move smoothly nor fade (airspace): it waits hidden, already at its new size, while a still of it
/// moves, and comes back where the still lands. The page under it is laid out before the movement starts, so that the
/// interface's thread has nothing heavy to do while it runs.
/// </summary>
public partial class MainWindow
{
    private const int MorphMs = 340, CloseMs = 260;
    private Canvas? _morphLayer;
    private Border? _morphCard, _morphPicture;
    private bool _morphing;
    private int _morphVersion;

    /// <summary>Reduces or expands the player as a person asks it: moving between the two places when it can.</summary>
    private async void SetMiniPlayerSmoothly(bool mini)
    {
        if (!_playing || _morphing || mini == _miniPlayer) return;
        if (Motion.Reduced || _fullscreen || _miniStowed || WindowState == WindowState.Minimized || PlayerErrorShown || !_loaded)
        {
            FrameProbe.Note($"{(mini ? "reduce" : "expand")} without movement: reduced motion {Motion.Reduced}, fullscreen {_fullscreen}, tucked away {_miniStowed}, error {PlayerErrorShown}, loaded {_loaded}");
            SetMiniPlayer(mini); return;
        }
        FrameProbe.Begin(mini ? "reduce" : "expand");
        var version = ++_morphVersion; _morphing = true;
        var from = VideoRect(); var fromRadius = _miniPlayer ? MiniRadius : 0d;
        if (!await CoverVideoAsync(from, fromRadius) || version != _morphVersion) return;
        using (FrameProbe.Time("SetMiniPlayer")) SetMiniPlayer(mini);
        // Expanding: the page stays under the growing picture until the picture covers it.
        if (!mini) LibraryShell.Visibility = NavigationRail.Visibility = TitleBar.Visibility = Visibility.Visible;
        PlayerShell.Opacity = 0; PlayerShell.IsHitTestVisible = false; MiniFrame.Opacity = 0;
        ClosePlayerPopups();
        // The new page laid out, and what that queued done, under the still rather than during the movement.
        using (FrameProbe.Time("layout")) UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        if (version != _morphVersion) return;
        await MoveMorphAsync(from, VideoRect(), fromRadius, mini ? MiniRadius : 0, MorphMs);
        if (version != _morphVersion) return;
        if (!_miniPlayer) LibraryShell.Visibility = NavigationRail.Visibility = TitleBar.Visibility = Visibility.Collapsed;
        EndMorph();
        if (_playing) { if (_miniPlayer) Motion.Fade(MiniFrame, 1, 140, 0); ShowPlayerControls(); }
    }
    /// <summary>
    /// Closing the player (Stop, ✕ of the mini-player): the still of the picture shrinks a little and fades over the page
    /// it goes back to, drawn first behind it with the progress just made. mpv ends after the movement (its ending holds
    /// the interface's thread up while its window goes).
    /// </summary>
    private async Task<bool> ClosePlayerSmoothlyAsync()
    {
        if (Motion.Reduced || _fullscreen || _miniStowed || PlayerShell.Visibility != Visibility.Visible || PlayerErrorShown || _mpv is not { } mpv || !_loaded || _morphing)
        {
            FrameProbe.Note($"close without movement: reduced motion {Motion.Reduced}, fullscreen {_fullscreen}, tucked away {_miniStowed}, error {PlayerErrorShown}, loaded {_loaded}, moving {_morphing}");
            return false;
        }
        var version = ++_morphVersion; _morphing = true;
        mpv.Set("pause", "yes"); mpv.Set("mute", "yes");
        var from = VideoRect(); var radius = _miniPlayer ? MiniRadius : 0d;
        var moved = new TaskCompletionSource(); _releaseWhen = moved.Task;
        try
        {
            if (!await CoverVideoAsync(from, radius)) return true;
            PlayerShell.Opacity = 0; PlayerShell.IsHitTestVisible = false; MiniFrame.Opacity = 0;
            await StopPlaybackAsync(); FrameProbe.Mark("stopped");
            // The page is there at once, under the still that fades: no second fade of its own.
            if (_returnToDetail is { } page && _detail?.Id == page.Id) { DetailOverlay.BeginAnimation(OpacityProperty, null); DetailOverlay.Opacity = 1; DetailOverlay.Visibility = Visibility.Visible; }
            using (FrameProbe.Time("ReturnToLibrary")) ReturnToLibrary(reveal: false);
            using (FrameProbe.Time("layout")) UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (version != _morphVersion) return true;
            var shrunk = new Rect(from.X + from.Width * .04, from.Y + from.Height * .04, from.Width * .92, from.Height * .92);
            await MoveMorphAsync(from, shrunk, radius, Math.Max(radius, 14), CloseMs, fade: true);
            if (version == _morphVersion) EndMorph();
            return true;
        }
        finally { _releaseWhen = Task.CompletedTask; moved.TrySetResult(); }
    }
    /// <summary>
    /// The still of the picture over the video, then the video hidden once a frame with the still has been drawn: the
    /// screen does not change, and the video window no longer covers what WPF draws.
    /// </summary>
    private async Task<bool> CoverVideoAsync(Rect at, double radius)
    {
        var version = _morphVersion;
        ImageSource? picture; using (FrameProbe.Time("picture")) picture = VideoPicture();
        ShowMorph(picture, at, radius); ClosePlayerPopups();
        await RenderedAsync(2);
        if (version != _morphVersion) return false;
        VideoContainer.Visibility = Visibility.Hidden;
        return true;
    }
    /// <summary>After <paramref name="frames"/> frames drawn by WPF.</summary>
    private static Task RenderedAsync(int frames)
    {
        var done = new TaskCompletionSource(); var seen = 0;
        void Frame(object? sender, EventArgs e) { if (++seen < frames) return; CompositionTarget.Rendering -= Frame; done.TrySetResult(); }
        CompositionTarget.Rendering += Frame;
        return done.Task;
    }
    /// <summary>Puts everything back as the player shows it, the still gone.</summary>
    private void EndMorph()
    {
        _morphing = false;
        if (_morphLayer is not null) { _morphLayer.Visibility = Visibility.Collapsed; _morphPicture!.Background = null; }
        MiniFrame.BeginAnimation(OpacityProperty, null); MiniFrame.Opacity = 1;
        PlayerShell.BeginAnimation(OpacityProperty, null); PlayerShell.Opacity = 1; PlayerShell.IsHitTestVisible = true;
        if (!PlayerErrorShown) VideoContainer.Visibility = Visibility.Visible;
    }
    /// <summary>Where the video is drawn now, in the window's units.</summary>
    private Rect VideoRect()
    {
        if (VideoContainer.ActualWidth < 1 || PlayerShell.Visibility != Visibility.Visible) return new Rect(0, 0, Root.ActualWidth, Root.ActualHeight);
        return VideoContainer.TransformToAncestor(Root).TransformBounds(new Rect(VideoContainer.RenderSize));
    }
    /// <summary>
    /// The picture as mpv shows it now, without the black bars it adds around a picture of another shape (drawn again
    /// around the still, at each size, as mpv would). Null when mpv cannot give it: a black card moves instead.
    /// </summary>
    private ImageSource? VideoPicture()
    {
        if (_mpv is not { } mpv) return null;
        try
        {
            if (mpv.WindowPicture() is not { } shot) return null;
            var bitmap = BitmapSource.Create(shot.Width, shot.Height, 96, 96, PixelFormats.Bgr32, null, shot.Pixels, shot.Stride);
            // The margins are in mpv's on-screen units, the window's pixels: scaled if the still is of another size.
            var scale = mpv.Number("osd-dimensions/w") is > 0 and var w ? shot.Width / w : 1;
            int Margin(string side) => (int)Math.Round(Math.Max(0, mpv.Number("osd-dimensions/m" + side)) * scale);
            var (left, top, right, bottom) = (Margin("l"), Margin("t"), Margin("r"), Margin("b"));
            ImageSource picture = bitmap;
            var width = shot.Width - left - right; var height = shot.Height - top - bottom;
            if ((left | top | right | bottom) != 0 && width > 16 && height > 16) picture = new CroppedBitmap(bitmap, new Int32Rect(left, top, width, height));
            picture.Freeze();
            return picture;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { return null; }
    }
    private void ShowMorph(ImageSource? picture, Rect at, double radius)
    {
        if (_morphLayer is null)
        {
            _morphPicture = new Border();
            _morphCard = new Border { Background = Brushes.Black, Child = _morphPicture, SnapsToDevicePixels = false, UseLayoutRounding = false };
            _morphLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _morphLayer.Children.Add(_morphCard);
            Grid.SetRowSpan(_morphLayer, 2); Panel.SetZIndex(_morphLayer, 32);
            ((Panel)Root).Children.Add(_morphLayer);
        }
        _morphPicture!.Background = picture is null ? null : new ImageBrush(picture) { Stretch = Stretch.Uniform };
        _morphCard!.BeginAnimation(OpacityProperty, null); _morphCard.Opacity = 1;
        PlaceMorph(at, radius);
        _morphLayer.Visibility = Visibility.Visible;
    }
    private void PlaceMorph(Rect at, double radius)
    {
        Canvas.SetLeft(_morphCard!, at.X); Canvas.SetTop(_morphCard!, at.Y);
        _morphCard!.Width = Math.Max(1, at.Width); _morphCard.Height = Math.Max(1, at.Height);
        _morphCard.CornerRadius = _morphPicture!.CornerRadius = new CornerRadius(Math.Max(0, radius));
    }
    /// <summary>
    /// Moves the still from one place to the other, timed by the clock rather than counted in frames: a late frame
    /// shows the still further on, never a slower movement. Starts once a frame with the new page has been drawn.
    /// </summary>
    private Task MoveMorphAsync(Rect from, Rect to, double fromRadius, double toRadius, int ms, bool fade = false)
    {
        var done = new TaskCompletionSource();
        var version = _morphVersion; var frames = 0; Stopwatch? clock = null;
        void Frame(object? sender, EventArgs e)
        {
            if (version != _morphVersion) { CompositionTarget.Rendering -= Frame; done.TrySetResult(); return; }
            // The first frame draws the page made visible under the still: the movement starts after it.
            if (++frames == 1) return;
            clock ??= Stopwatch.StartNew();
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            // A quick start and a long, soft landing (an ease-out of the fourth degree).
            var k = 1 - Math.Pow(1 - t, 4);
            static double Mix(double a, double b, double k) => a + (b - a) * k;
            PlaceMorph(new Rect(Mix(from.X, to.X, k), Mix(from.Y, to.Y, k), Mix(from.Width, to.Width, k), Mix(from.Height, to.Height, k)), Mix(fromRadius, toRadius, k));
            if (fade) _morphCard!.Opacity = 1 - (t < .25 ? 0 : (t - .25) / .75);
            if (t >= 1) { CompositionTarget.Rendering -= Frame; done.TrySetResult(); }
        }
        CompositionTarget.Rendering += Frame;
        return done.Task;
    }
}
