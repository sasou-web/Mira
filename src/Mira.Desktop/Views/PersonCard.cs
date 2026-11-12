using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop.Views;

/// <summary>
/// An actor or director found by a search (« Personnes »): their photo in a circle, their initials without one, and
/// their name under it, and on a title's page the part they play. A click shows their titles.
/// </summary>
public sealed class PersonCard : Button
{
    public const double Size = 88;
    /// <summary>On a title's page: one row of smaller photos after its episodes and facts.</summary>
    public const double Small = 72;
    public MediaItem Person { get; }
    private readonly double _size;
    private readonly Ellipse _photo, _ring;
    public PersonCard(MediaItem person, string? role = null, double size = Size)
    {
        Person = person; _size = size;
        _photo = new() { Width = size, Height = size, Opacity = 0 };
        _ring = new() { Width = size, Height = size, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false };
        Style = (Style)Application.Current.FindResource("Quiet");
        Background = Brushes.Transparent; Padding = new Thickness(0); BorderThickness = new Thickness(0);
        Width = size < Size ? 104 : 112; Margin = new Thickness(0, 0, 18, 22); VerticalContentAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(this, person.Name);
        ToolTip = L($"Les titres avec {person.Name} dans ta bibliothèque", $"Titles with {person.Name} in your library");
        _ring.Stroke = Palette.Get("Text");
        var circle = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        circle.Children.Add(new Ellipse { Fill = Palette.Get("Raised") });
        circle.Children.Add(new TextBlock { Text = Catalog.Initials(person.Name), FontSize = size * 26 / Size, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        circle.Children.Add(_photo); circle.Children.Add(_ring);
        var root = new StackPanel();
        root.Children.Add(circle);
        root.Children.Add(new TextBlock { Text = person.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 38, LineHeight = 19, Margin = new Thickness(0, 10, 0, 0), Foreground = Palette.Get("Text") });
        if (role?.Trim() is { Length: > 0 } part)
            root.Children.Add(new TextBlock { Text = part, FontSize = 12.5, FontWeight = FontWeights.Normal, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), Foreground = Palette.Get("Muted") });
        Content = root;
        // The ring is the hover and keyboard focus indicator, as on the posters.
        FocusVisualStyle = null; Accessible.SetOwnFocusRing(this, true);
        MouseEnter += (_, _) => _ring.Opacity = 1; MouseLeave += (_, _) => { if (!IsKeyboardFocused) _ring.Opacity = 0; };
        GotKeyboardFocus += (_, _) => { if (IsMouseOver || Accessible.GetFocusRingShown(this)) _ring.Opacity = 1; }; LostKeyboardFocus += (_, _) => { if (!IsMouseOver) _ring.Opacity = 0; };
    }
    /// <summary>Their photo, decoded at the size shown, when Jellyfin has one; the initials stay otherwise.</summary>
    public async Task LoadImageAsync(ImageCache images)
    {
        if (!Person.ImageTags.ContainsKey("Primary")) return;
        var bitmap = await images.GetAsync(Person, false, images.Pixels(_size));
        if (bitmap is null) return;
        _photo.Fill = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        Motion.Fade(_photo, 1, 180, 0, Motion.Soft);
    }
}
