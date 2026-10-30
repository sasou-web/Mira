using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Titres à corriger » (Réglages, an administrator), as on Windows: the films and series Jellyfin did not recognize, in
/// double, or without poster, year or summary (<see cref="TitleIdentity.Check"/>); « Identifier » opens the search of
/// the right title, « Ouvrir » its page. Hidden while Jellyfin recognized everything.
/// </summary>
public sealed class FixTitlesSection
{
    private const int Shown = 8;
    private readonly MainWindow _shell;
    private readonly StackPanel _list = new() { Spacing = 14 };
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly Button _more;
    private List<TitleIdentity.Entry> _entries = [];
    private bool _all;

    public Control Section { get; }
    /// <summary>For the self-check.</summary>
    public int Count => _entries.Count;

    public FixTitlesSection(MainWindow shell, Func<string, Control[], Control> section)
    {
        _shell = shell;
        _more = new Button { Classes = { "quiet" } };
        _more.Click += (_, _) => { _all = !_all; Render(); };
        _more.HorizontalAlignment = HorizontalAlignment.Left;
        Section = section(L("Titres à corriger", "Titles to fix"), [Ui.Column(14, _status, _list, _more)]);
        Section.IsVisible = false;
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_shell.Session is not { } session) return;
        try
        {
            if (await session.Client.IsAdministratorAsync(session.Lifetime) != true) return;
            var titles = await session.Client.TitlesToCheckAsync(session.Lifetime);
            var entries = await Task.Run(() => TitleIdentity.Check(titles));
            if (!ReferenceEquals(session, _shell.Session)) return;
            _entries = entries; Render();
        }
        // Jellyfin out of reach: the rest of the page says so already.
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }
    private void Render()
    {
        _list.Children.Clear();
        Section.IsVisible = _entries.Count > 0;
        var unknown = _entries.Count(x => x.Problems.Contains(TitleIdentity.Problem.Unidentified));
        var count = Lang.Count(_entries.Count, "titre", "titres", "title", "titles");
        _status.Text = unknown > 0
            ? L($"{Lang.Count(unknown, "titre non reconnu", "titres non reconnus", "", "")} par Jellyfin, sur {_entries.Count} à revoir.", $"{Lang.Count(unknown, "", "", "title", "titles")} Jellyfin didn’t recognize, of {_entries.Count} to check.")
            : L($"{count} à revoir.", $"{count} to check.");
        foreach (var entry in _all ? _entries : _entries.Take(Shown))
        {
            var item = entry.Item; var named = TitleIdentity.Named(item);
            var text = Ui.Column(3,
                new TextBlock { Text = named, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = TitleIdentity.Text(entry), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
            text.VerticalAlignment = VerticalAlignment.Center;
            var open = Ui.Action(L("Ouvrir", "Open"), null, "quiet", () => _shell.Open(item));
            Avalonia.Automation.AutomationProperties.SetName(open, L($"Ouvrir la fiche de « {named} »", $"Open the page of “{named}”"));
            var identify = Ui.Action(L("Identifier", "Identify"), null, "quiet", () => _ = IdentifyAsync(item));
            Avalonia.Automation.AutomationProperties.SetName(identify, L($"Identifier « {named} »", $"Identify “{named}”"));
            var buttons = Ui.Row(8, open, identify); buttons.VerticalAlignment = VerticalAlignment.Center; buttons.Margin = new Thickness(12, 0, 0, 0);
            var row = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons); row.Children.Add(text);
            _list.Children.Add(row);
        }
        _more.IsVisible = _entries.Count > Shown;
        _more.Content = _all ? L("Voir moins", "Show fewer") : L($"Voir les {_entries.Count}", $"See all {_entries.Count}");
    }
    private async Task IdentifyAsync(MediaItem item)
    {
        if (_shell.Session is not { } session) return;
        var window = new IdentifyWindow(session, item);
        await window.ShowDialog(_shell);
        if (window.Said is { } said) _shell.Notice(said);
        if (window.Applied) await LoadAsync();
    }
}
