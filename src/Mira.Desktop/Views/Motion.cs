using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mira.Desktop.Views;

public static class Motion
{
    private static readonly DependencyProperty VisibilityVersionProperty = DependencyProperty.RegisterAttached("VisibilityVersion", typeof(int), typeof(Motion), new PropertyMetadata(0));
    /// <summary>
    /// Mira's « Réduire les animations », or Windows' « Effets d'animation » turned off (Accessibility, Visual effects),
    /// read as it is now. Setting it sets Mira's own choice.
    /// </summary>
    public static bool Reduced { get => Chosen || FollowsWindows && !SystemParameters.ClientAreaAnimation; set => Chosen = value; }
    /// <summary>Mira's « Réduire les animations ».</summary>
    public static bool Chosen { get; set; }
    /// <summary>False for validation runs, whose captures and motion checks must not depend on the PC they run on.</summary>
    public static bool FollowsWindows { get; set; } = true;
    public static readonly QuadraticEase Ease = new() { EasingMode = EasingMode.EaseInOut };
    /// <summary>Fast start, soft landing: used for pointer feedback so it reacts immediately without snapping.</summary>
    public static readonly CubicEase EaseOut = new() { EasingMode = EasingMode.EaseOut };
    /// <summary>Gentler landing for pointer feedback: a cubic curve covers most of a short hover in its
    /// first frames and then crawls, which reads as a jump followed by a second, slower movement.</summary>
    public static readonly QuadraticEase Soft = new() { EasingMode = EasingMode.EaseOut };
    /// <summary>Entrances: fast start, long soft landing, so a page answers at once and settles without a visible stop.</summary>
    public static readonly ExponentialEase Enter = new() { EasingMode = EasingMode.EaseOut, Exponent = 5 };
    /// <summary>Exits: a short lift-off, gone quickly, so the next page is not kept waiting.</summary>
    public static readonly CubicEase Exit = new() { EasingMode = EasingMode.EaseIn };
    /// <summary>Movements across the screen (the rail's tile): a gentle start and a long landing.</summary>
    public static readonly BackEase Glide = new() { EasingMode = EasingMode.EaseOut, Amplitude = .18 };
    static Motion() { Ease.Freeze(); EaseOut.Freeze(); Soft.Freeze(); Enter.Freeze(); Exit.Freeze(); Glide.Freeze(); }
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
        var version = (int)target.GetValue(VisibilityVersionProperty) + 1; target.SetValue(VisibilityVersionProperty, version);
        target.Visibility = Visibility.Visible; target.IsHitTestVisible = true;
        Picture(target, version, delay + ms + 60);
        var move = target.RenderTransform as TranslateTransform ?? new TranslateTransform(); target.RenderTransform = move;
        if (Reduced || delay <= 0) { Animate(move, TranslateTransform.YProperty, 0, ms + 60, Reduced ? 0 : distance, Enter); Fade(target, 1, ms, Reduced ? 1 : 0, Enter); return; }
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
    /// <param name="delay">Still until then: a page coming in stays one picture (<see cref="PageProperty"/>).</param>
    public static void Pulse(UIElement target, bool active, int delay = 0)
    {
        if (!active || Reduced) { target.BeginAnimation(UIElement.OpacityProperty, null); target.Opacity = 1; return; }
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, .55, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Ease, BeginTime = TimeSpan.FromMilliseconds(delay) });
    }
    /// <summary>Shown at once, without a transition (Réglages turned over to Aide, the same window): any other transition of it stops.</summary>
    public static void Show(FrameworkElement target)
    {
        target.SetValue(VisibilityVersionProperty, (int)target.GetValue(VisibilityVersionProperty) + 1);
        target.BeginAnimation(UIElement.OpacityProperty, null); target.Opacity = 1;
        if (target.RenderTransform is TranslateTransform move) { move.BeginAnimation(TranslateTransform.YProperty, null); move.Y = 0; }
        target.ClearValue(UIElement.CacheModeProperty);
        target.Visibility = Visibility.Visible; target.IsHitTestVisible = true;
    }
    /// <summary>Gone at once (<see cref="Show"/>'s other half).</summary>
    public static void Hide(FrameworkElement target)
    {
        target.SetValue(VisibilityVersionProperty, (int)target.GetValue(VisibilityVersionProperty) + 1);
        target.IsHitTestVisible = false; target.Visibility = Visibility.Collapsed;
    }
    public static async Task HideAsync(FrameworkElement target, int ms = 140)
    {
        if (target.Visibility != Visibility.Visible) return;
        var version = (int)target.GetValue(VisibilityVersionProperty) + 1; target.SetValue(VisibilityVersionProperty, version);
        target.IsHitTestVisible = false; Picture(target, version, ms); Fade(target, 0, ms, ease: Exit);
        if (!Reduced) await Task.Delay(ms);
        // Only the latest transition may finish when navigation changes rapidly.
        if (version == (int)target.GetValue(VisibilityVersionProperty)) target.Visibility = Visibility.Collapsed;
    }
    /// <summary>
    /// A whole page of the window, opaque (<see cref="Reveal"/> and <see cref="HideAsync"/> move and fade it): while
    /// it comes or goes, WPF keeps it as one picture (<see cref="RenderCache.Picture"/>) that it moves and fades at each
    /// refresh. Otherwise it draws all of the page again at each refresh into a layer it then fades: on a 4K screen at
    /// 240 Hz, one image in three. Its content must not move by itself meanwhile (the picture would be drawn again).
    /// </summary>
    public static readonly DependencyProperty PageProperty = DependencyProperty.RegisterAttached("Page", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    public static bool GetPage(DependencyObject value) => (bool)value.GetValue(PageProperty);
    public static void SetPage(DependencyObject value, bool enabled) => value.SetValue(PageProperty, enabled);
    /// <summary>A page kept as a picture for the <paramref name="ms"/> of its transition, then drawn as usual again (its own scroll would draw all the picture again at each frame).</summary>
    private static void Picture(FrameworkElement target, int version, int ms)
    {
        if (Reduced || !GetPage(target)) return;
        target.CacheMode = RenderCache.Picture;
        _ = LetGoAsync();
        async Task LetGoAsync()
        {
            await Task.Delay(ms + 60);
            // A newer transition of the page keeps it until its own end.
            if (version == (int)target.GetValue(VisibilityVersionProperty)) target.ClearValue(UIElement.CacheModeProperty);
        }
    }
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached("Interactive", typeof(bool), typeof(Motion), new PropertyMetadata(false, InteractiveChanged));
    public static bool GetInteractive(DependencyObject value) => (bool)value.GetValue(InteractiveProperty);
    public static void SetInteractive(DependencyObject value, bool enabled) => value.SetValue(InteractiveProperty, enabled);
    /// <summary>Corner radius of the default button template (and of its hover wash).</summary>
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(CornerRadius), typeof(Motion), new FrameworkPropertyMetadata(new CornerRadius(10)));
    public static CornerRadius GetRadius(DependencyObject value) => (CornerRadius)value.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject value, CornerRadius radius) => value.SetValue(RadiusProperty, radius);
    public const double HoverWash = .11, PressWash = .19;
    /// <summary>Its pointer handlers in place: added once, whatever turns <see cref="InteractiveProperty"/> on and off.</summary>
    private static readonly DependencyProperty WiredProperty = DependencyProperty.RegisterAttached("Wired", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    /// <summary>
    /// The default template's wash, set at once: buttons stay still, no fade, no scale (ogsas, 2026-11-12: an animated
    /// hover looked neither smooth nor good). Pressed is a little darker than hovered.
    /// </summary>
    /// <remarks>
    /// Read at each event, not only when it is set: the default style sets it true as soon as a button gets its style, so a
    /// button turned off afterwards (<c>SetInteractive(button, false)</c> after <c>Style = …</c>, the carousel's bars) kept
    /// the handlers and showed the wash anyway (0.9.7).
    /// </remarks>
    private static void InteractiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Button button || button is MediaCard) return;
        if (e.NewValue is not true) { if ((bool)button.GetValue(WiredProperty)) Wash(button, 0); return; }
        if ((bool)button.GetValue(WiredProperty)) return;
        button.SetValue(WiredProperty, true);
        // Lit by the pointer: only then is there something to put out when the button goes.
        var lifted = false;
        void To(double wash)
        {
            if (!GetInteractive(button)) wash = 0;
            if (!lifted && wash == 0) return;
            lifted = wash > 0;
            Wash(button, wash);
        }
        button.MouseEnter += (_, _) => To(HoverWash);
        button.MouseLeave += (_, _) => To(0);
        button.PreviewMouseLeftButtonDown += (_, _) => To(PressWash);
        button.PreviewMouseLeftButtonUp += (_, _) => To(button.IsMouseOver ? HoverWash : 0);
        // A button hidden or disabled under the pointer must not keep its highlight.
        button.IsVisibleChanged += (_, _) => { if (!button.IsVisible) To(0); };
        button.IsEnabledChanged += (_, _) => { if (!button.IsEnabled) To(0); };
    }
    private static void Wash(Button button, double wash)
    {
        button.ApplyTemplate();
        if (button.Template?.FindName("Hover", button) is UIElement layer) { layer.BeginAnimation(UIElement.OpacityProperty, null); layer.Opacity = wash; }
    }

    /// <summary>Changes a glyph at once: a state change on a button does not animate.</summary>
    public static void Swap(Icon icon, string kind) => icon.Kind = kind;
    public static readonly DependencyProperty SwitchProperty = DependencyProperty.RegisterAttached("Switch", typeof(bool), typeof(Motion), new PropertyMetadata(false, SwitchChanged));
    public static bool GetSwitch(DependencyObject value) => (bool)value.GetValue(SwitchProperty);
    public static void SetSwitch(DependencyObject value, bool enabled) => value.SetValue(SwitchProperty, enabled);
    private static void SwitchChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not CheckBox check || e.NewValue is not true) return;
        void Update(bool animate)
        {
            check.ApplyTemplate();
            if (check.Template.FindName("Track", check) is not Squircle track || check.Template.FindName("Dot", check) is not System.Windows.Shapes.Ellipse dot) return;
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
