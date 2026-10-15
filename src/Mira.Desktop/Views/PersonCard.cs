using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop.Views;

/// <summary>
/// An actor or director found by a search (« Personnes »): their photo in a circle, their initials without one, and
/// their name under it. A click shows their titles.
/// </summary>
public sealed class PersonCard : Button
{
    public const double Size = 88;
    public MediaItem Person { get; }
    private readonly Ellipse _photo = new() { Width = Size, Height = Size, Opacity = 0 };
    private readonly Ellipse _ring = new() { Width = Size, Height = Size, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false };
    public PersonCard(MediaItem person)
    {
        Person = person;
        Style = (Style)Application.Current.FindResource("Quiet");
        Background = Brushes.Transparent; Padding = new Thickness(0); BorderThickness = new Thickness(0);
        Width = 112; Margin = new Thickness(0, 0, 18, 22); VerticalContentAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(this, person.Name);
        ToolTip = $"Les titres avec {person.Name} dans ta bibliothèque";
        _ring.Stroke = Palette.Get("Text");
        var circle = new Grid { Width = Size, Height = Size, HorizontalAlignment = HorizontalAlignment.Center };
        circle.Children.Add(new Ellipse { Fill = Palette.Get("Raised") });
        circle.Children.Add(new TextBlock { Text = Catalog.Initials(person.Name), FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        circle.Children.Add(_photo); circle.Children.Add(_ring);
        var root = new StackPanel();
        root.Children.Add(circle);
        root.Children.Add(new TextBlock { Text = person.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 38, LineHeight = 19, Margin = new Thickness(0, 10, 0, 0), Foreground = Palette.Get("Text") });
        Content = root;
        // The ring is the hover and keyboard focus indicator, as on the posters.
        FocusVisualStyle = null;
        MouseEnter += (_, _) => _ring.Opacity = 1; MouseLeave += (_, _) => { if (!IsKeyboardFocused) _ring.Opacity = 0; };
        GotKeyboardFocus += (_, _) => _ring.Opacity = 1; LostKeyboardFocus += (_, _) => { if (!IsMouseOver) _ring.Opacity = 0; };
    }
    /// <summary>Their photo, decoded at the size shown, when Jellyfin has one; the initials stay otherwise.</summary>
    public async Task LoadImageAsync(ImageCache images)
    {
        if (!Person.ImageTags.ContainsKey("Primary")) return;
        var bitmap = await images.GetAsync(Person, false, images.Pixels(Size));
        if (bitmap is null) return;
        _photo.Fill = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        Motion.Fade(_photo, 1, 180, 0, Motion.Soft);
    }
}
