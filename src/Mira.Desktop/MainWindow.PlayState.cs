using System.Windows;
using System.Windows.Media.Animation;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// The pause and play flash in the middle of the picture, as streaming players do.
public partial class MainWindow
{
    private int _playStateFlash;
    /// <summary>
    /// The state just asked for, by a click on the picture or the button, Space, K or a media key: a round badge that
    /// pops in and fades out in about 0.7 s. One animation each for opacity and scale, a few dozen frames of the
    /// overlay window in all. Never in the mini-player nor over the loading spinner; with reduced motion, shown still.
    /// </summary>
    private async void ShowPlayState(bool paused)
    {
        if (_miniPlayer || PlayerLoading.Visibility == Visibility.Visible || !PlayerOverlayLayer.IsOpen) return;
        var flash = ++_playStateFlash;
        PlayStateIcon.Kind = paused ? "pause" : "play";
        // The play triangle's weight sits left of its box: nudged so it looks centred in the circle.
        PlayStateIcon.Margin = paused ? new Thickness(0) : new Thickness(3, 0, 0, 0);
        SeekPill.Visibility = Visibility.Collapsed;
        PlayStateFlash.Visibility = Visibility.Visible;
        PlayStateLayer.IsOpen = true; PlayStateLayer.Reposition();
        if (Motion.Reduced)
        {
            PlayStateFlash.BeginAnimation(OpacityProperty, null); PlayStateFlash.Opacity = 1;
            PlayStateScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null); PlayStateScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            PlayStateScale.ScaleX = PlayStateScale.ScaleY = 1;
            await Task.Delay(650);
        }
        else
        {
            var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), Motion.EaseOut));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330))));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), Motion.Soft));
            var grow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
            grow.KeyFrames.Add(new DiscreteDoubleKeyFrame(.82, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), Motion.Enter));
            grow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330))));
            grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), Motion.Soft));
            fade.Freeze(); grow.Freeze();
            PlayStateFlash.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            PlayStateScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow, HandoffBehavior.SnapshotAndReplace);
            PlayStateScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow, HandoffBehavior.SnapshotAndReplace);
            await Task.Delay(720);
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
