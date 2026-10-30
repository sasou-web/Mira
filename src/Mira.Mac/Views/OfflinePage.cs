using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Sur ce Mac », as Windows' « Sur ce PC »: every copy kept on this Mac, a series' episodes together; a card plays at
/// once, even without Jellyfin. The line under the title says what is ready and what is still coming.
/// </summary>
public sealed class OfflinePage : Page
{
    private readonly CardGrid _grid = new() { MinItemWidth = 180 };
    private readonly EmptyState _empty = new() { Margin = new Thickness(0, 56, 0, 40) };
    private readonly TextBlock _summary = new() { FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private string _shown = "";
    public override string Rail => "offline";
    /// <summary>For the self-check.</summary>
    public int Count => _grid.Children.Count;

    public OfflinePage(MainWindow shell) : base(shell)
    {
        var header = Ui.Column(8, Ui.Heading(L("Sur ce Mac", "On this Mac"), 32, 1, FontWeight.SemiBold), _summary);
        header.Margin = new Thickness(0, 0, 0, 28);
        var page = new StackPanel { Margin = new Thickness(56, 44 + shell.TitleBarInset, 56, 60), Children = { header, _grid, _empty } };
        Content = new ScrollViewer { Content = page };
        void Updated() => Render();
        void Progressed() => _summary.Text = Shell.OfflineSummary();
        AttachedToVisualTree += (_, _) => { Shell.OfflineUpdated += Updated; Shell.OfflineProgressed += Progressed; Render(); };
        DetachedFromVisualTree += (_, _) => { Shell.OfflineUpdated -= Updated; Shell.OfflineProgressed -= Progressed; };
    }
    public override Task RefreshAsync() { _shown = ""; Render(); return Task.CompletedTask; }
    private void Render()
    {
        _summary.Text = Shell.OfflineSummary();
        var items = OfflineCopies.Ordered(Shell.OfflineCopiesKept).Select(x => x.Item with { UserData = x.Item.UserData with { } }).ToList();
        // The progress made since the copy was taken, as the rest of the library shows it.
        if (Shell.Session is { } session) Errors.TryCache(() => session.Store.ApplyLocalProgress(items));
        _empty.IsVisible = items.Count == 0;
        if (items.Count == 0) _empty.Show("laptop", L("Rien sur ce Mac", "Nothing on this Mac"), L("Sur la fiche d’un film ou d’une série, « Garder sur ce Mac » garde son fichier d’origine ici, pour le regarder sans connexion.", "On a movie’s or a show’s page, “Keep on this Mac” keeps its original file here, to watch it offline."));
        // Redrawn only when the copies changed: a card under the pointer stays where it is.
        var shown = string.Join("|", items.Select(x => x.Id));
        if (shown == _shown) return;
        _shown = shown;
        _grid.Children.Clear();
        var images = Shell.Session?.Images;
        foreach (var item in items) _grid.Children.Add(Cards.Poster(item, images, x => _ = Shell.PlayAsync(x), Shell.Menu));
    }
}
