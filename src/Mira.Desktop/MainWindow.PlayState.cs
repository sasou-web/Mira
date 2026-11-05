using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// The pause and play flash in the middle of the picture, as streaming players do.
public partial class MainWindow
{
    private int _playStateFlash;
    /// <summary>The flash's sizes and shadows, from <see cref="PlayStateBadge"/>: once, before it first shows.</summary>
    private void InitializePlayState()
    {
        PlayStateLayer.Box = new Size(PlayStateBadge.Room, PlayStateBadge.Room);
        PlayStateGlyph.Width = PlayStateGlyph.Height = PlayStateIcon.Width = PlayStateIcon.Height = PlayStateBadge.Glyph;
        // The close shadow on the icon, the wide one around both: as CSS chains its drop-shadow filters.
        PlayStateIcon.Effect = Shadow(PlayStateBadge.Edge); PlayStateGlyph.Effect = Shadow(PlayStateBadge.Glow);
    }
    /// <summary>Falling straight down; WPF's blur radius taken as three standard deviations of its Gaussian.</summary>
    private static DropShadowEffect Shadow(PlayStateBadge.Shadow shadow)
    {
        var effect = new DropShadowEffect { Color = Colors.Black, Direction = 270, ShadowDepth = shadow.Down, BlurRadius = 3 * shadow.Sigma, Opacity = shadow.Opacity, RenderingBias = RenderingBias.Quality };
        effect.Freeze(); return effect;
    }
    /// <summary>
    /// The state just asked for, by a click on the picture or the button, Space, K or a media key: the icon alone over
    /// its shadows, there at once, gone in 0.62 s while it opens up (<see cref="PlayStateBadge"/>). One animation each
    /// for opacity and scale, in a window of its own size. Never in the mini-player nor over the loading spinner; with
    /// reduced motion, shown still.
    /// </summary>
    private async void ShowPlayState(bool paused)
    {
        if (_miniPlayer || PlayerLoading.Visibility == Visibility.Visible || !PlayerOverlayLayer.IsOpen) return;
        var flash = ++_playStateFlash;
        PlayStateIcon.Kind = paused ? "pause" : "play";
        PlayStateNudge.X = paused ? 0 : PlayStateBadge.PlayNudge * PlayStateBadge.Glyph;
        SeekPill.Visibility = Visibility.Collapsed;
        PlayStateFlash.Visibility = Visibility.Visible;
        PlayStateLayer.IsOpen = true; PlayStateLayer.Reposition();
        if (Motion.Reduced)
        {
            PlayStateFlash.BeginAnimation(OpacityProperty, null); PlayStateFlash.Opacity = 1;
            PlayStateScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); PlayStateScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PlayStateScale.ScaleX = PlayStateScale.ScaleY = 1;
            await Task.Delay(TimeSpan.FromMilliseconds(PlayStateBadge.Still));
        }
        else
        {
            // Pressed again while it shows: the opacity goes on from where it is, the size starts again.
            PlayStateFlash.BeginAnimation(OpacityProperty, PlayStateMotion.Fade, HandoffBehavior.SnapshotAndReplace);
            PlayStateScale.BeginAnimation(ScaleTransform.ScaleXProperty, PlayStateMotion.Grow, HandoffBehavior.SnapshotAndReplace);
            PlayStateScale.BeginAnimation(ScaleTransform.ScaleYProperty, PlayStateMotion.Grow, HandoffBehavior.SnapshotAndReplace);
            await Task.Delay(TimeSpan.FromMilliseconds(PlayStateBadge.End + 20));
        }
        // A later press restarted the flash: it hides it itself.
        if (flash == _playStateFlash) HidePlayState();
    }
    private void HidePlayState()
    {
        ++_playStateFlash;
        PlayStateFlash.BeginAnimation(OpacityProperty, null); PlayStateFlash.Opacity = 0;
        PlayStateFlash.Visibility = Visibility.Collapsed;
        PlayStateLayer.IsOpen = false;
    }
}

/// <summary>
/// <see cref="PlayStateBadge.At"/> as WPF keyframes, made once: each easing is the one of its stretch of the curve
/// (checked against it by tests/Mira.Tests). The opacity has no keyframe at 0, so it starts from the one it has.
/// </summary>
internal static class PlayStateMotion
{
    public static readonly DoubleAnimationUsingKeyFrames Fade = Frozen(
        new EasingDoubleKeyFrame(1, At(PlayStateBadge.In), Ease(new QuadraticEase { EasingMode = EasingMode.EaseOut })),
        new LinearDoubleKeyFrame(1, At(PlayStateBadge.Hold)),
        new EasingDoubleKeyFrame(0, At(PlayStateBadge.End), Ease(new QuadraticEase { EasingMode = EasingMode.EaseIn })));
    public static readonly DoubleAnimationUsingKeyFrames Grow = Frozen(
        new DiscreteDoubleKeyFrame(PlayStateBadge.FromScale, At(0)),
        new EasingDoubleKeyFrame(1, At(PlayStateBadge.Settle), Ease(new CubicEase { EasingMode = EasingMode.EaseOut })),
        new LinearDoubleKeyFrame(1, At(PlayStateBadge.Hold)),
        new EasingDoubleKeyFrame(PlayStateBadge.ToScale, At(PlayStateBadge.End), Ease(new QuadraticEase { EasingMode = EasingMode.EaseOut })));
    private static KeyTime At(double ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
    private static IEasingFunction Ease(EasingFunctionBase ease) { ease.Freeze(); return ease; }
    private static DoubleAnimationUsingKeyFrames Frozen(params DoubleKeyFrame[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(PlayStateBadge.End) };
        foreach (var frame in frames) animation.KeyFrames.Add(frame);
        animation.Freeze();
        return animation;
    }
}
