using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Ta party » of another Jellyfin, as on Windows (PartyOverlay): the first name asked, then the host's group waited
/// for or joined, its members, and « Quitter la party ». Drawn over the window while this Mac is that party's guest
/// (<see cref="MainWindow.InParty"/>); the player opens above it when the group plays, and leaves it in sight when it closes.
/// </summary>
public sealed class PartyView : UserControl
{
    private readonly TextBlock _heading = Ui.Heading("", 30, 1, FontWeight.Bold);
    private readonly TextBlock _lead = Ui.Text("", 14.5, color: Ui.Muted);
    private readonly TextBox _name = new() { MaxLength = 30 };
    private readonly Control _nameField;
    private readonly TextBlock _group = Ui.Text("", 15, FontWeight.SemiBold);
    private readonly TextBlock _members = Ui.Text("", 13.5, color: Ui.Muted);
    private readonly Control _membersBox;
    private readonly TextBlock _error = new() { Foreground = Ui.Brush("#F0B4A7"), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _primaryText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _primary, _secondary;
    private readonly TextBlock _secondaryText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Action _onPrimary, _onSecondary;

    /// <param name="primary">« Rejoindre la party », « Chercher à nouveau » or « Revenir au titre du groupe », as the stage is.</param>
    /// <param name="secondary">« Annuler », « Quitter la party » or « Fermer ».</param>
    public PartyView(MainWindow shell, Action primary, Action secondary)
    {
        _onPrimary = primary; _onSecondary = secondary;
        Avalonia.Automation.AutomationProperties.SetName(_name, L("Ton prénom", "Your first name"));
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_heading, Avalonia.Automation.AutomationLiveSetting.Polite);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_error, Avalonia.Automation.AutomationLiveSetting.Assertive);
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; primary(); } };
        _primary = new Button { Content = _primaryText, Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(18, 13) };
        _primary.Click += (_, _) => primary();
        _secondary = new Button { Content = _secondaryText, Classes = { "chip" }, HorizontalAlignment = HorizontalAlignment.Center };
        _secondary.Click += (_, _) => secondary();
        _nameField = Ui.Column(7, new TextBlock { Text = L("Ton prénom", "Your first name"), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextTertiary }, _name);
        _membersBox = new Border
        {
            Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(10), Background = Ui.Brush("#14FFFFFF"), BorderBrush = Ui.Brush("#26FFFFFF"), BorderThickness = new Thickness(1),
            Child = Ui.Column(4, _group, _members)
        };
        var card = Ui.Column(14, _heading, _lead, _nameField, _membersBox, _error, _primary, _secondary);
        card.Width = 440;
        // As the sign-in page: Mira's mark top left, the card in the middle on the same dark glow.
        Content = new Grid
        {
            Background = new RadialGradientBrush { Center = new RelativePoint(0.3, 0.2, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.3, 0.2, RelativeUnit.Relative), RadiusX = new RelativeScalar(0.9, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.9, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#1A1D22"), 0), new GradientStop(Color.Parse("#070708"), 1) } },
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(38, 40 + shell.TitleBarInset, 0, 0), IsHitTestVisible = false,
                    Children = { new Icon("brand", 31) { Foreground = Ui.Ink }, new TextBlock { Text = "mira", FontSize = 25, FontWeight = FontWeight.ExtraBold, VerticalAlignment = VerticalAlignment.Center } } },
                new ScrollViewer
                {
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 24 + shell.TitleBarInset, 24, 24),
                    Content = new Border { Child = card, Padding = new Thickness(40), CornerRadius = new CornerRadius(18), Background = Ui.Brush("#C00E0E10"), BorderBrush = Ui.Brush("#1F1F23"), BorderThickness = new Thickness(1) }
                }
            }
        };
    }

    public string FirstName { get => _name.Text ?? ""; set => _name.Text = value; }
    public string Heading => _heading.Text ?? "";
    public string? Error => _error.IsVisible ? _error.Text : null;
    public bool PrimaryShown => _primary.IsVisible;
    public string Members => _membersBox.IsVisible ? _members.Text ?? "" : "";
    /// <summary>Clicks the primary button as the person would (the self-check).</summary>
    public void ClickPrimary() { if (_primary is { IsVisible: true, IsEnabled: true }) _onPrimary(); }
    public void ClickSecondary() => _onSecondary();

    /// <summary>What the stage says: its texts, the name field (<paramref name="nameShown"/>, typed while <paramref name="nameEnabled"/>), the group and the buttons.</summary>
    public void Show(string heading, string lead, bool nameShown, bool nameEnabled, Together.Group? group, string? primary, bool primaryEnabled, string secondary)
    {
        _heading.Text = heading; _lead.Text = lead;
        _nameField.IsVisible = nameShown; _name.IsEnabled = nameEnabled;
        _membersBox.IsVisible = group is not null;
        if (group is not null) { _group.Text = Together.GroupText(group); _members.Text = Together.Members(group); }
        _primary.IsVisible = primary is not null; _primary.IsEnabled = primaryEnabled;
        _primaryText.Text = primary ?? "";
        Avalonia.Automation.AutomationProperties.SetName(_primary, primary ?? "");
        _secondaryText.Text = secondary;
        Avalonia.Automation.AutomationProperties.SetName(_secondary, secondary);
    }
    public void ShowError(string? text) { _error.Text = text; _error.IsVisible = !string.IsNullOrEmpty(text); }
    public void FocusName() { _name.Focus(); _name.SelectAll(); }
}
