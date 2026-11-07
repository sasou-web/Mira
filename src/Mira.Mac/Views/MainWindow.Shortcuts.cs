using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// Aide → « Raccourcis clavier » (⌘/): the keys Mira reads on a Mac, as the Windows guide lists its own, keycaps beside
// what they do. Kept with the keys themselves: OnKeyDown (MainWindow.cs), the menu bar (MainWindow.Menu.cs), the player
// (PlayerPage.Keys.cs), the zoom of the posters (MainWindow.Trackpad.cs).
public sealed partial class MainWindow
{
    /// <summary>The shortcuts by group, each its keys then what it does: for the sheet and the self-check.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<(string[] Keys, string Text)> Lines)> Shortcuts =>
    [
        (L("Partout", "Everywhere"),
        [
            (["⌘", "F"], L("rechercher", "search")),
            (["⌘", ","], L("réglages", "settings")),
            (["⌘", "["], L("page précédente", "previous page")),
            (["⌘", "]"], L("page suivante", "next page")),
            ([L("Échap", "Esc")], L("revenir en arrière", "go back")),
            (["⌘", "+ −"], L("affiches plus grandes, plus petites", "bigger, smaller posters")),
            (["⌘", "0"], L("affiches à leur taille", "posters at their size")),
            (["⌃", "⌘", "F"], L("plein écran", "full screen")),
            (["⌘", "M"], L("placer dans le Dock", "minimize")),
            (["⌘", "W"], L("fermer la fenêtre (Mira reste dans le Dock)", "close the window (Mira stays in the Dock)")),
            (["⌘", "Q"], L("quitter Mira", "quit Mira")),
            (["⌘", "/"], L("ces raccourcis", "these shortcuts")),
        ]),
        (L("Pendant la lecture", "While playing"),
        [
            ([L("Espace", "Space")], L("lecture ou pause", "play or pause")),
            (["←", "→"], L("10 s en arrière, en avant", "10 s back, forward")),
            (["⇧", "← →"], L("1 min en arrière, en avant", "1 min back, forward")),
            (["0", L("à", "to") + " 9"], L("aller à 0 – 90 %", "jump to 0 – 90 %")),
            (["↑", "↓"], L("volume", "volume")),
            (["M"], L("couper le son", "mute")),
            (["C"], L("sous-titres", "subtitles")),
            (["F"], L("plein écran", "full screen")),
            (["S"], L("passer le générique", "skip the intro")),
            (["N"], L("épisode suivant", "next episode")),
            (["P"], L("épisode précédent", "previous episode")),
            (["E"], L("épisodes", "episodes")),
            (["I"], L("mini-lecteur", "mini player")),
            (["⌘", "["], L("réduire le lecteur", "shrink the player")),
            ([L("Échap", "Esc")], L("quitter le plein écran, sinon réduire le lecteur", "leave full screen, else shrink the player")),
        ]),
    ];

    /// <summary>The sheet of the shortcuts, two columns of keycaps; Escape or « Fermer » closes it.</summary>
    public void ShowShortcuts()
    {
        if (CurrentSheet is { Tag: "shortcuts" }) return;
        var sheet = new Sheet(L("AIDE", "HELP"), "info", L("Raccourcis clavier", "Keyboard shortcuts"), width: 760) { Tag = "shortcuts" };
        foreach (var (group, lines) in Shortcuts)
        {
            sheet.Body.Children.Add(new TextBlock { Text = group, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextTertiary, Margin = new Thickness(0, 18, 0, 10) });
            var grid = new UniformGrid { Columns = 2 };
            foreach (var (keys, text) in lines) grid.Children.Add(ShortcutLine(keys, text));
            sheet.Body.Children.Add(grid);
        }
        sheet.CloseButton(() => CloseSheet(sheet));
        sheet.FirstFocus = sheet.Body.Children.OfType<Button>().LastOrDefault();
        ShowSheet(sheet);
    }
    private static Control ShortcutLine(string[] keys, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 12, 9) };
        foreach (var key in keys)
            row.Children.Add(new Border
            {
                MinWidth = 28, Height = 26, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(6),
                Background = Ui.Raised, BorderBrush = Ui.Brush("#34343A"), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = key, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        row.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 0, 0, 0), MaxWidth = 250 });
        // VoiceOver reads the line as one: the keys, then what they do.
        Avalonia.Automation.AutomationProperties.SetName(row, string.Join(" ", keys) + " : " + text);
        return row;
    }
}
