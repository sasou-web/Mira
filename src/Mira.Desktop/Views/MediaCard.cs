using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop.Views;

public sealed class MediaCard : Button
{
    public MediaItem Item { get; }
    public string RenderKey { get; }
    public ImageSource? ImageSource => _image.Source;
    public Task ImageReady { get; private set; } = Task.CompletedTask;
    public event Action<MediaCard>? HoverEntered;
    public event Action<MediaCard>? FocusEntered;
    public event Action<MediaCard>? HoverLeft;
    private readonly Image _image;
    private readonly Grid _hover;
    private readonly ScaleTransform _zoom = new(1, 1);
    private static readonly DependencyProperty HoverAmountProperty = DependencyProperty.Register(nameof(HoverAmount), typeof(double), typeof(MediaCard),
        new PropertyMetadata(0d, (sender, e) => ((MediaCard)sender).ApplyHover((double)e.NewValue)));
    internal double HoverAmount => (double)GetValue(HoverAmountProperty);
    internal double ArtworkZoom => _zoom.ScaleX;
    internal double HighlightOpacity => _hover.Opacity;
    public bool HoverVisible { get; private set; }
    private readonly Grid _visual;
    private readonly bool _wide;
    private readonly Border? _progress;
    public MediaCard(MediaItem item, bool wide, int index, PlayerSettings settings, bool demo)
    {
        Item = item; _wide = wide; RenderKey = Key(item, settings);
        Style = (Style)Application.Current.FindResource("Quiet");
        Background = Brushes.Transparent; Padding = new Thickness(0);
        BorderThickness = new Thickness(0); HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top; Margin = new Thickness(0, 0, 22, wide ? 5 : 30);
        System.Windows.Automation.AutomationProperties.SetName(this, (wide ? item.Progress > 0 && !item.UserData.Played ? "Reprendre " : "Regarder " : "Ouvrir ") + item.DisplayTitle + (item.UserData.Played ? ", vu" : ""));
        var root = new StackPanel();
        _visual = new Grid { Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)), ClipToBounds = true };
        var placeholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
        placeholder.Children.Add(new Icon { Kind = wide ? "play" : "film", Width = 30, Height = 30, Foreground = new SolidColorBrush(Color.FromRgb(78, 78, 87)), Margin = new Thickness(0, 0, 0, 15) });
        placeholder.Children.Add(new TextBlock { Text = item.DisplayTitle, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 140)), FontSize = 13 });
        _visual.Children.Add(placeholder);
        _image = new Image { Source = demo ? DemoLibrary.Artwork(index, wide) : null, Stretch = Stretch.UniformToFill,
            SnapsToDevicePixels = false, UseLayoutRounding = false };
        _image.RenderTransformOrigin = new Point(.5, .5); _image.RenderTransform = _zoom;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality); _visual.Children.Add(_image);
        if (item.UserData.Played)
        {
            _visual.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(225, 13, 16, 16)), CornerRadius = new CornerRadius(12), Width = 25, Height = 25, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(9), Child = new Icon { Kind = "check", Width = 14, Height = 14, Foreground = new SolidColorBrush(Color.FromRgb(176, 217, 178)) } });
        }
        // Hover: a light ring and a gentle zoom. Posters open their page on click, so no caption is needed;
        // resume cards also show the play button they trigger.
        _hover = new Grid { Opacity = 0, IsHitTestVisible = false };
        if (wide)
        {
            _hover.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(90, 0, 0, 0), 90) });
            _hover.Children.Add(new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = Brushes.WhiteSmoke, Child = new Icon { Kind = "play", Width = 19, Height = 19, Foreground = Brushes.Black } });
        }
        _hover.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(235, 245, 245, 247)), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(5) });
        _visual.Children.Add(_hover);
        FocusVisualStyle = null; // the ring above is the focus indicator
        MouseEnter += (_, _) => HoverEntered?.Invoke(this);
        MouseMove += (_, _) => HoverEntered?.Invoke(this);
        MouseLeave += (_, _) => HoverLeft?.Invoke(this);
        // Focus from a mouse click must not leave a highlight behind once the pointer moves on.
        GotKeyboardFocus += (_, _) => { if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) FocusEntered?.Invoke(this); };
        LostKeyboardFocus += (_, _) => { if (!IsMouseOver) HoverLeft?.Invoke(this); };
        if (item.Progress > 0 && !item.UserData.Played && (wide || settings.ShowProgress))
        {
            _visual.Children.Add(new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)) });
            _progress = new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.WhiteSmoke }; _visual.Children.Add(_progress);
        }
        root.Children.Add(_visual);
        root.Children.Add(new TextBlock { Text = item.DisplayTitle, FontSize = 15, FontWeight = FontWeights.ExtraBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 21, Height = wide ? 23 : 42, Margin = new Thickness(0, 12, 3, 0), Foreground = Brushes.White });
        // Resume cards say how much is left, which is what matters when picking something to finish.
        var remaining = wide && item.Progress > 0 && !item.UserData.Played && item.RunTimeTicks is > 0
            ? "Reste " + MediaItem.FormatDuration(TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMinute, item.RunTimeTicks.Value - item.UserData.PlaybackPositionTicks))) : null;
        var meta = wide && item.Type == "Episode"
            ? string.Join("  ·  ", new[] { $"S{item.ParentIndexNumber ?? 1:00} E{item.IndexNumber ?? 1:00}", remaining, item.Name }.Where(x => !string.IsNullOrEmpty(x)))
            : remaining ?? string.Join("   ·   ", new[] { item.ProductionYear?.ToString(), item.Type == "Series" ? "Série" : item.DurationLabel }.Where(x => !string.IsNullOrEmpty(x)));
        root.Children.Add(new TextBlock { Text = meta, Foreground = new SolidColorBrush(Color.FromRgb(145, 145, 150)), FontSize = 12, FontWeight = FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 7, 0, 0) });
        Content = root; Resize(wide ? 290 : 185);
    }
    public const double HoverZoom = 1.025;
    /// <summary>One clock drives the highlight and artwork. Reversals start at the displayed value.</summary>
    public void SetHover(bool active)
    {
        if (HoverVisible == active) return;
        HoverVisible = active;
        var current = HoverAmount; var target = active ? 1d : 0d;
        if (Motion.Reduced) { BeginAnimation(HoverAmountProperty, null); SetValue(HoverAmountProperty, target); return; }
        // A new WPF clock begins on the next frame. Keep the displayed value underneath it,
        // otherwise reversing exposes the destination for one frame before interpolation starts.
        SetValue(HoverAmountProperty, current);
        BeginAnimation(HoverAmountProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(160))
        { EasingFunction = Motion.Soft }, HandoffBehavior.SnapshotAndReplace);
    }
    private void ApplyHover(double value)
    { _hover.Opacity = value; _zoom.ScaleX = _zoom.ScaleY = 1 + (Motion.Reduced ? 0 : HoverZoom - 1) * value; }
    public void Resize(double width)
    {
        if (Math.Abs(Width - width) < .01 && _visual.Clip is not null) return;
        Width = width; _visual.Width = width; _visual.Height = _wide ? width * 9 / 16 : width * 1.48;
        _visual.Clip = new RectangleGeometry(new Rect(0, 0, width, _visual.Height), 5, 5);
        if (_progress is not null) _progress.Width = Math.Max(2, width * Item.Progress);
    }
    public Task LoadImageAsync(ImageCache? images, bool wide) => ImageReady = LoadArtworkAsync(images, wide);
    private async Task LoadArtworkAsync(ImageCache? images, bool wide)
    {
        if (images is null) return;
        var request = wide ? images.LandscapeAsync(Item, 720) : images.GetAsync(Item, false, 500); var cached = request.IsCompletedSuccessfully;
        var bitmap = await request;
        if (bitmap is not null) { var initial = _image.Source is null; _image.Source = bitmap; if (initial && IsVisible && !cached && !HoverVisible) Motion.Fade(_image, 1, 180, 0, Motion.Soft); }
    }
    public static string Key(MediaItem item, PlayerSettings settings) => string.Join('|', item.Id, item.Name, item.Overview, item.ProductionYear, item.Subtitle, item.UserData.Played, item.UserData.IsFavorite, item.UserData.PlaybackPositionTicks, string.Join(',', item.ImageTags.Values), string.Join(',', item.BackdropImageTags), item.SeasonThumbImageTag, settings.ShowProgress);
}
