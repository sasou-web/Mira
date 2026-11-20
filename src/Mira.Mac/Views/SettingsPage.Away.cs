using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Core.Family;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Loin de chez toi » and « Tes proches », as on Windows (MainWindow.Away.cs there; ogsas, 2026-11-20, maquette A
/// « En tête de liste »): the state of watching away from home on top of its page, beside its name in the list, and
/// on « L’essentiel » with the family and friends, each a tile with its way there.
/// </summary>
public sealed partial class SettingsPage
{
    private readonly TextBlock _awayTitle = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _awayText = new() { FontSize = 12, LineHeight = 19, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly Icon _awayIcon = new("check", 20) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private Control? _awaySummary;
    private readonly List<Action> _awayShown = [];

    /// <summary>The state on top of « Loin de chez toi », and « Vérifier à nouveau ».</summary>
    private Control AwaySummary()
    {
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_awayTitle, Avalonia.Automation.AutomationLiveSetting.Polite);
        var again = Ui.Action(L("Vérifier à nouveau", "Check again"), null, "", () => _ = Shell.ReadAwayAsync(again: true));
        again.VerticalAlignment = VerticalAlignment.Center; again.Margin = new Thickness(14, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(again, Dock.Right); row.Children.Add(again);
        DockPanel.SetDock(_awayIcon, Dock.Left); row.Children.Add(_awayIcon);
        var words = Ui.Column(0, _awayTitle, _awayText); words.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(words);
        var card = Card(row);
        _awaySummary = card;
        Page.WhileShown(card, shown => { if (shown) { Shell.AwayChanged += ShowAway; ShowAway(); _ = Shell.ReadAwayAsync(again: true); } else Shell.AwayChanged -= ShowAway; });
        return card;
    }

    /// <summary>Where away from home stands, wherever it shows in Réglages.</summary>
    private void ShowAway()
    {
        var away = Shell.AwayNow;
        var dot = Dot(away);
        _awayTitle.Text = away.Title; _awayText.Text = away.Text;
        _awayIcon.Kind = away.Level == AwayLevel.Ready ? "check" : "info";
        _awayIcon.Foreground = dot ?? Ui.Muted;
        if (_awaySummary is not null) _awaySummary.IsVisible = away.Level != AwayLevel.Unknown;
        foreach (var shown in _awayShown) shown();
    }

    private static IBrush? Dot(AwayState away) => away.Level switch { AwayLevel.Ready => Ui.Success, AwayLevel.Attention or AwayLevel.Off => Ui.Warning, _ => null };

    /// <summary>A coloured dot (or a count) beside a section's name, told again with the state.</summary>
    private void MarkNav(string key, Func<(string? Count, IBrush? Dot)> mark)
    {
        if (!_tabButtons.TryGetValue(key, out var button) || button.Content is not DockPanel row) return;
        var marks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(8, 0, 2, 0), Spacing = 8 };
        DockPanel.SetDock(marks, Dock.Right); row.Children.Insert(1, marks);
        void Show()
        {
            var (count, dot) = mark();
            marks.Children.Clear();
            if (count is { Length: > 0 })
            {
                var text = new TextBlock { Text = count, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = .7, VerticalAlignment = VerticalAlignment.Center };
                text.Bind(TextBlock.ForegroundProperty, button.GetObservable(Button.ForegroundProperty));
                marks.Children.Add(text);
            }
            if (dot is not null) marks.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
            marks.IsVisible = marks.Children.Count > 0;
        }
        _awayShown.Add(Show);
        Page.WhileShown(button, shown => { if (shown) { Shell.AwayChanged += Show; Show(); } else Shell.AwayChanged -= Show; });
    }

    /// <summary>« Tes proches » on « L’essentiel »: the family, friends and away from home, each with its state and its way there.</summary>
    private Control EssentialsPeople()
    {
        var family = Tile("home", L("Famille & profils", "Family & profiles"), L("Gérer", "Manage"), () => Go("family"), out var familyState, out var familyText, out _, out _);
        var friends = Tile("users", L("Amis & invités", "Friends & guests"), L("Inviter un ami…", "Invite a friend…"), () => ShowHelp("friend"), out var friendsState, out var friendsText, out _, out _);
        var away = Tile("globe", L("Loin de chez toi", "Away from home"), L("Détails", "Details"), () => Go("away"), out var awayState, out var awayText, out var awayDot, out var awayButton);
        friendsState.Text = L("Leur propre compte", "Their own account");
        friendsText.Text = L("Pour regarder ensemble ou chacun de son côté.", "To watch together or each on their own.");
        var tiles = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, -10, 0), Children = { family, friends, away } };
        var label = new TextBlock { Text = L("Tes proches", "Your people"), FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Faint, Margin = new Thickness(4, 14, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(label, 2);
        var people = Ui.Column(10, label, tiles);
        void Show()
        {
            var state = Shell.AwayNow;
            var admin = Shell.Administrator == true;
            family.IsVisible = Shell.AwayFamily is not null;
            if (Shell.AwayFamily is { } home)
            {
                familyState.Text = Count(home.Profiles.Count(x => x.Offered), "profil", "profils", "profile", "profiles") + " · " + Count(home.Devices.Count, "appareil", "appareils", "device", "devices");
                familyText.Text = L("Un mot de passe pour ta famille, puis chacun son profil.", "One password for your family, then a profile each.");
            }
            friends.IsVisible = admin && !Shell.InParty;
            away.IsVisible = state.Level != AwayLevel.Unknown;
            awayDot.Fill = Dot(state); awayDot.IsVisible = awayDot.Fill is not null;
            awayState.Text = state.Word;
            awayText.Text = AwayFromHome.Short(state.Level);
            if (awayButton.Content is StackPanel { Children: [.., TextBlock words] }) words.Text = state.Level == AwayLevel.Ready ? L("Détails", "Details") : L("Régler…", "Set up…");
            people.IsVisible = family.IsVisible || friends.IsVisible || away.IsVisible;
        }
        _awayShown.Add(Show);
        Page.WhileShown(people, shown => { if (shown) { Shell.AwayChanged += Show; Show(); _ = Shell.ReadAwayAsync(); } else Shell.AwayChanged -= Show; });
        return people;

        static Border Tile(string icon, string title, string action, Action click, out TextBlock state, out TextBlock text, out Ellipse dot, out Button button)
        {
            state = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            text = new TextBlock { FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
            button = Ui.Action(action, null, "", click);
            button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new Thickness(0, 12, 0, 0);
            var head = new DockPanel();
            var glyph = new Icon(icon, 16) { Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(glyph, Dock.Left); head.Children.Add(glyph);
            head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var line = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
            DockPanel.SetDock(dot, Dock.Left); line.Children.Add(dot); line.Children.Add(state);
            var body = new DockPanel();
            DockPanel.SetDock(button, Dock.Bottom); body.Children.Add(button);
            body.Children.Add(Ui.Column(0, head, line, text));
            var card = Card(body);
            card.Padding = new Thickness(16, 14); card.Margin = new Thickness(0, 0, 10, 0);
            return card;
        }
    }
}
