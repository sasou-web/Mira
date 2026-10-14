using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « En ce moment sur Jellyfin » (Réglages → Jellyfin & synchronisation): who plays what, the file as it is or converted,
// on which hardware and why, with « Arrêter » for a device that takes it (Mira web while it plays).
public partial class MainWindow
{
    private readonly DispatcherTimer _viewersTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _viewersBusy, _viewersHooked;
    private string _viewersShown = "";
    /// <summary>Follows the sessions while the Jellyfin tab of the settings shows, every 5 s; stops otherwise.</summary>
    private void WatchViewers(bool on)
    {
        if (!_viewersHooked) { _viewersHooked = true; _viewersTimer.Tick += (_, _) => _ = ShowViewersAsync(); }
        if (!on || _demo) { _viewersTimer.Stop(); return; }
        _viewersShown = ""; _viewersTimer.Start(); _ = ShowViewersAsync();
    }
    private bool ViewersOnScreen => SettingsOverlay.Visibility == Visibility.Visible && ServerSettings.Visibility == Visibility.Visible;
    private async Task ShowViewersAsync()
    {
        if (!ViewersOnScreen) { _viewersTimer.Stop(); return; }
        if (_viewersBusy || _client is not { } client) return;
        _viewersBusy = true;
        var ct = _connectionLifetime.Token;
        try
        {
            var viewers = await client.ViewersAsync(ct);
            if (client == _client && ViewersOnScreen) RenderViewers(viewers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { ViewersRow.Visibility = Visibility.Visible; ViewersStatus.Text = Friendly(ex); _viewersShown = ""; }
        finally { _viewersBusy = false; }
    }
    private void RenderViewers(List<Viewer> viewers)
    {
        ViewersRow.Visibility = Visibility.Visible;
        var lines = viewers.Select(v => (Viewer: v, Who: Viewers.Who(v), What: Viewers.What(v), How: Viewers.How(v))).ToList();
        // Redrawn only when something changed: a button under the pointer or the keyboard stays where it is.
        var shown = string.Join("\n", lines.Select(x => $"{x.Viewer.SessionId}|{x.Who}|{x.What}|{x.How}|{x.Viewer.CanStop}"));
        if (shown == _viewersShown) return;
        _viewersShown = shown;
        ViewersStatus.Text = lines.Count == 0 ? "Personne ne regarde rien pour l’instant." : lines.Count == 1 ? "1 lecture en cours." : $"{lines.Count} lectures en cours.";
        ViewersPanel.Children.Clear();
        foreach (var (viewer, who, what, (how, why)) in lines)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = who, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = what, Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            text.Children.Add(new TextBlock { Text = how, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 3, 0, 0) });
            if (why is not null) text.Children.Add(new TextBlock { Text = why, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 1, 0, 0) });
            var row = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            if (viewer.CanStop && !viewer.Own)
            {
                var stop = new Button { Content = "Arrêter", Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 0, 0, 0) };
                System.Windows.Automation.AutomationProperties.SetName(stop, $"Arrêter la lecture sur {who}");
                stop.Click += async (_, _) => await StopViewerAsync(viewer, who, stop);
                DockPanel.SetDock(stop, Dock.Right); row.Children.Add(stop);
            }
            row.Children.Add(text);
            ViewersPanel.Children.Add(row);
        }
    }
    private async Task StopViewerAsync(Viewer viewer, string who, Button button)
    {
        if (_client is not { } client) return;
        button.IsEnabled = false;
        var ct = _connectionLifetime.Token;
        try
        {
            ViewersStatus.Text = await client.StopViewerAsync(viewer.SessionId, ct) ? $"Arrêt demandé à {who}." : "Seul un administrateur de Jellyfin peut arrêter la lecture d’un autre compte.";
            await Task.Delay(1500, ct); _viewersShown = ""; await ShowViewersAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { ViewersStatus.Text = Friendly(ex); button.IsEnabled = true; }
    }
}
