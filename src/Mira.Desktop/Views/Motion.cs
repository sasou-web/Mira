using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mira.Desktop.Views;

public static class Motion
{
    private static readonly DependencyProperty VisibilityVersionProperty = DependencyProperty.RegisterAttached("VisibilityVersion", typeof(int), typeof(Motion), new PropertyMetadata(0));
    public static bool Reduced { get; set; }
    public static readonly QuadraticEase Ease = new() { EasingMode = EasingMode.EaseInOut };
    /// <summary>Fast start, soft landing: used for pointer feedback so it reacts immediately without snapping.</summary>
    public static readonly CubicEase EaseOut = new() { EasingMode = EasingMode.EaseOut };
    /// <summary>Gentler landing for pointer feedback: a cubic curve covers most of a short hover in its
    /// first frames and then crawls, which reads as a jump followed by a second, slower movement.</summary>
    public static readonly QuadraticEase Soft = new() { EasingMode = EasingMode.EaseOut };
    static Motion() { Ease.Freeze(); EaseOut.Freeze(); Soft.Freeze(); }
    public static void Animate(Animatable target, DependencyProperty property, double value, int ms = 220, double? from = null, IEasingFunction? ease = null)
    {
        var animation = new DoubleAnimation { To = value, From = from, Duration = TimeSpan.FromMilliseconds(Reduced ? 0 : ms), EasingFunction = ease ?? Ease };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    public static void Fade(UIElement target, double value, int ms = 200, double? from = null, IEasingFunction? ease = null)
    {
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation { To = value, From = from, Duration = TimeSpan.FromMilliseconds(Reduced ? 0 : ms), EasingFunction = ease ?? Ease }, HandoffBehavior.SnapshotAndReplace);
    }
    /// <param name="delay">Stagger in milliseconds: the element stays hidden, then rises in. Lets a grid
    /// appear as a short cascade instead of every card popping at once.</param>
    public static void Reveal(FrameworkElement target, int ms = 260, double distance = 12, int delay = 0)
    {
        target.SetValue(VisibilityVersionProperty, (int)target.GetValue(VisibilityVersionProperty) + 1);
        target.Visibility = Visibility.Visible; target.IsHitTestVisible = true;
        var move = target.RenderTransform as TranslateTransform ?? new TranslateTransform(); target.RenderTransform = move;
        if (Reduced || delay <= 0) { Animate(move, TranslateTransform.YProperty, 0, ms, Reduced ? 0 : distance); Fade(target, 1, ms, Reduced ? 1 : 0); return; }
        move.BeginAnimation(TranslateTransform.YProperty, Delayed(distance, 0, delay, ms), HandoffBehavior.SnapshotAndReplace);
        target.BeginAnimation(UIElement.OpacityProperty, Delayed(0, 1, delay, ms), HandoffBehavior.SnapshotAndReplace);
    }
    private static DoubleAnimationUsingKeyFrames Delayed(double from, double to, int delay, int ms)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delay + ms) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + ms)), EaseOut));
        return animation;
    }
    /// <summary>Slow breathing opacity for loading placeholders; stops (and settles) with <paramref name="active"/> false.</summary>
    public static void Pulse(UIElement target, bool active)
    {
        if (!active || Reduced) { target.BeginAnimation(UIElement.OpacityProperty, null); target.Opacity = 1; return; }
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, .55, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ease });
    }
    public static async Task HideAsync(FrameworkElement target, int ms = 140)
    {
        if (target.Visibility != Visibility.Visible) return;
        var version = (int)target.GetValue(VisibilityVersionProperty) + 1; target.SetValue(VisibilityVersionProperty, version);
        target.IsHitTestVisible = false; Fade(target, 0, ms);
        if (!Reduced) await Task.Delay(ms);
        // Only the latest transition may finish when navigation changes rapidly.
        if (version == (int)target.GetValue(VisibilityVersionProperty)) target.Visibility = Visibility.Collapsed;
    }
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached("Interactive", typeof(bool), typeof(Motion), new PropertyMetadata(false, InteractiveChanged));
    public static bool GetInteractive(DependencyObject value) => (bool)value.GetValue(InteractiveProperty);
    public static void SetInteractive(DependencyObject value, bool enabled) => value.SetValue(InteractiveProperty, enabled);
    /// <summary>Corner radius of the default button template (and of its hover wash).</summary>
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(CornerRadius), typeof(Motion), new FrameworkPropertyMetadata(new CornerRadius(6)));
    public static CornerRadius GetRadius(DependencyObject value) => (CornerRadius)value.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject value, CornerRadius radius) => value.SetValue(RadiusProperty, radius);
    /// <summary>Icon buttons also grow slightly under the pointer. Text buttons keep a fixed size:
    /// scaling pixel-snapped text makes it shimmer instead of moving smoothly.</summary>
    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.RegisterAttached("HoverScale", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    public static bool GetHoverScale(DependencyObject value) => (bool)value.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject value, bool enabled) => value.SetValue(HoverScaleProperty, enabled);
    public const double HoverWash = .11, PressWash = .19;
    private static void InteractiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Button button || e.NewValue is not true || button is MediaCard) return;
        ScaleTransform? scale = null;
        void To(double wash, double size, int ms)
        {
            button.ApplyTemplate();
            // Wash and scale share one duration and one curve, so they move as a single gesture.
            if (button.Template?.FindName("Hover", button) is UIElement layer) Fade(layer, wash, ms, ease: Soft);
            if (!GetHoverScale(button)) return;
            if (scale is null) { scale = new ScaleTransform(1, 1); button.RenderTransformOrigin = new Point(.5, .5); button.RenderTransform = scale; }
            if (Reduced) size = 1;
            Animate(scale, ScaleTransform.ScaleXProperty, size, ms, ease: Soft); Animate(scale, ScaleTransform.ScaleYProperty, size, ms, ease: Soft);
        }
        double Grow() => 1 + Math.Min(.05, 2.2 / Math.Max(1, button.ActualWidth));
        double Shrink() => 1 - Math.Min(.04, 1.8 / Math.Max(1, button.ActualWidth));
        button.MouseEnter += (_, _) => To(HoverWash, Grow(), 190);
        button.MouseLeave += (_, _) => To(0, 1, 240);
        button.PreviewMouseLeftButtonDown += (_, _) => To(PressWash, Shrink(), 110);
        button.PreviewMouseLeftButtonUp += (_, _) => { if (button.IsMouseOver) To(HoverWash, Grow(), 200); else To(0, 1, 240); };
        // A button hidden or disabled under the pointer must not keep its highlight.
        button.IsVisibleChanged += (_, _) => { if (!button.IsVisible) To(0, 1, 0); };
        button.IsEnabledChanged += (_, _) => { if (!button.IsEnabled) To(0, 1, 120); };
    }

    /// <summary>Changes a glyph with a short pop (scale and fade) so a state change reads as one gesture.</summary>
    public static void Swap(Icon icon, string kind)
    {
        if (icon.Kind == kind) return;
        icon.Kind = kind;
        if (Reduced) return;
        var scale = icon.RenderTransform as ScaleTransform;
        if (scale is null || scale.IsFrozen) { scale = new ScaleTransform(1, 1); icon.RenderTransformOrigin = new Point(.5, .5); icon.RenderTransform = scale; }
        Animate(scale, ScaleTransform.ScaleXProperty, 1, 170, .78, Soft); Animate(scale, ScaleTransform.ScaleYProperty, 1, 170, .78, Soft);
        Fade(icon, 1, 150, .35, Soft);
    }
    public static readonly DependencyProperty SwitchProperty = DependencyProperty.RegisterAttached("Switch", typeof(bool), typeof(Motion), new PropertyMetadata(false, SwitchChanged));
    public static bool GetSwitch(DependencyObject value) => (bool)value.GetValue(SwitchProperty);
    public static void SetSwitch(DependencyObject value, bool enabled) => value.SetValue(SwitchProperty, enabled);
    private static void SwitchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not CheckBox check || e.NewValue is not true) return;
        void Update(bool animate)
        {
            check.ApplyTemplate();
            if (check.Template.FindName("Track", check) is not Border track || check.Template.FindName("Dot", check) is not System.Windows.Shapes.Ellipse dot) return;
            var enabled = check.IsChecked == true;
            var move = dot.RenderTransform as TranslateTransform ?? new TranslateTransform(); dot.RenderTransform = move;
            var color = enabled ? Color.FromRgb(245, 245, 247) : Color.FromRgb(64, 64, 69);
            var brush = new SolidColorBrush((track.Background as SolidColorBrush)?.Color ?? color); track.Background = brush;
            dot.Fill = enabled ? Brushes.Black : Brushes.WhiteSmoke;
            if (animate && !Reduced)
            {
                Animate(move, TranslateTransform.XProperty, enabled ? 17 : 0, 190);
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(190)));
            }
            else { move.BeginAnimation(TranslateTransform.XProperty, null); move.X = enabled ? 17 : 0; brush.Color = color; }
        }
        check.Loaded += (_, _) => Update(false);
        check.IsVisibleChanged += (_, _) => { if (check.IsVisible) check.Dispatcher.InvokeAsync(() => Update(false), System.Windows.Threading.DispatcherPriority.Loaded); };
        check.Dispatcher.InvokeAsync(() => Update(false), System.Windows.Threading.DispatcherPriority.Loaded);
        check.Checked += (_, _) => { if (check.IsLoaded) Update(true); };
        check.Unchecked += (_, _) => { if (check.IsLoaded) Update(true); };
    }
}
