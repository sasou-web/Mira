using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Mira.Mac.Views;

/// <summary>
/// A sheet over the window, as Windows' overlays (« Nouveautés », « Inviter quelqu’un »…): the page dimmed behind a
/// scrim, a card 600 wide in the middle, scaled down when the window is too small for it, never up. The Tab key stays
/// in it and Escape closes it (<see cref="MainWindow.ShowSheet"/>), its first button taking the focus.
/// </summary>
public sealed class Sheet : Grid
{
    /// <summary>The card's own content: its kicker, title and lines, then its buttons.</summary>
    public StackPanel Body { get; } = new();
    /// <summary>Said when it goes, whichever way: a button, Escape, or another sheet in its place.</summary>
    public event Action? Closed;
    /// <summary>The control the focus goes to when it opens.</summary>
    public Control? FirstFocus { get; set; }
    /// <summary>Said once it is on screen, the focus given.</summary>
    public event Action? Shown;
    /// <summary>While true, Escape does not close it: Jellyfin is doing what it asked (« Identifier… » applied).</summary>
    public bool Busy { get; set; }

    /// <param name="width">The card's own width, as on Windows: 600, 660 for « Identifier… », 560 for a collection.</param>
    public Sheet(string kicker, string icon, string title, string? lead = null, double width = 600, double titleSize = 32)
    {
        Background = Ui.Brush("#F4000000");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new Icon(icon, 16) { Foreground = Ui.TextTertiary }, new TextBlock { Text = kicker, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextTertiary, LetterSpacing = 0.4, VerticalAlignment = VerticalAlignment.Center } } };
        var heading = Ui.Heading(title, titleSize, 1);
        heading.Margin = new Thickness(0, 10, 0, 6); heading.TextWrapping = TextWrapping.Wrap;
        Body.Children.Add(head); Body.Children.Add(heading);
        if (lead is not null) Body.Children.Add(new TextBlock { Text = lead, FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        Place(Body, width, new Thickness(38, 34));
    }
    /// <summary>A sheet whose card is <paramref name="content"/> alone, edge to edge (« Nouveautés »: its own head, list and foot).</summary>
    public Sheet(Control content, double width)
    {
        Background = Ui.Brush("#F4000000");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Place(content, width, new Thickness(0));
    }
    private void Place(Control content, double width, Thickness padding)
    {
        var card = new Border
        {
            Width = width, Padding = padding, Background = Ui.Brush("#F0111111"), BorderBrush = Ui.Brush("#2A2A2A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20),
            ClipToBounds = true, Child = content,
        };
        Children.Add(new Viewbox { Margin = new Thickness(28, 44, 28, 24), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = card });
    }

    /// <summary>The buttons at its foot: the main one on the right, the others on the left, as on Windows.</summary>
    public void Buttons(Button main, params Button[] others)
    {
        var row = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        main.Padding = new Thickness(26, 11);
        DockPanel.SetDock(main, Dock.Right); row.Children.Add(main);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var other in others) left.Children.Add(other);
        row.Children.Add(left);
        Body.Children.Add(row);
        FirstFocus ??= main;
    }

    /// <summary>A « Fermer » at its foot, on the left, for a sheet whose main buttons are in its lines.</summary>
    public void CloseButton(Action close)
    {
        var button = Ui.Action(Mira.Core.Lang.L("Fermer", "Close"), null, "quiet", close);
        button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 16, 0, 0);
        Body.Children.Add(button);
    }

    internal void Close() => Closed?.Invoke();
    internal void Opened() => Shown?.Invoke();
}
