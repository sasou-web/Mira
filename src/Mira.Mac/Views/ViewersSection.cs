using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « En ce moment sur Jellyfin » (Réglages), as on Windows: who plays what, the file as it is or converted, on which
/// hardware and why, with « Arrêter » for a device that takes it (Mira web while it plays). Read every 5 s while the
/// settings are on screen, never otherwise.
/// </summary>
public sealed class ViewersSection
{
    private readonly MainWindow _shell;
    private readonly StackPanel _list = new() { Spacing = 16 };
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string _shown = "";
    private bool _busy;

    public Control Section { get; }
    /// <summary>For the self-check: the sessions listed.</summary>
    public int Count { get; private set; } = -1;

    public ViewersSection(MainWindow shell, Func<string, Control[], Control> section)
    {
        _shell = shell;
        Section = section(L("En ce moment sur Jellyfin", "On Jellyfin right now"), [Ui.Column(0, _status, _list)]);
        Section.IsVisible = false;
        _timer.Tick += (_, _) => _ = LoadAsync();
        Section.AttachedToVisualTree += (_, _) => { _shown = ""; _timer.Start(); _ = LoadAsync(); };
        Section.DetachedFromVisualTree += (_, _) => _timer.Stop();
    }
    private async Task LoadAsync()
    {
        if (_busy || _shell.Session is not { } session) return;
        // Not under the player, nor while Mira is in the Dock.
        if (_shell.Watching || _shell.WindowState == WindowState.Minimized) return;
        _busy = true;
        try
        {
            var viewers = await session.Client.ViewersAsync(session.Lifetime);
            if (ReferenceEquals(session, _shell.Session)) Render(viewers);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Section.IsVisible = true; _status.Text = Errors.Friendly(ex); _shown = ""; }
        finally { _busy = false; }
    }
    private void Render(List<Viewer> viewers)
    {
        Section.IsVisible = true; Count = viewers.Count;
        var lines = viewers.Select(v => (Viewer: v, Who: Viewers.Who(v), What: Viewers.What(v), How: Viewers.How(v))).ToList();
        // Redrawn only when something changed: a button under the pointer or the keyboard stays where it is.
        var shown = string.Join("\n", lines.Select(x => $"{x.Viewer.SessionId}|{x.Who}|{x.What}|{x.How}|{x.Viewer.CanStop}"));
        if (shown == _shown) return;
        _shown = shown;
        _status.Text = lines.Count == 0 ? L("Personne ne regarde rien pour l’instant.", "No one is watching anything right now.") : lines.Count == 1 ? L("1 lecture en cours.", "Playing on 1 device.") : L($"{lines.Count} lectures en cours.", $"Playing on {lines.Count} devices.");
        _list.Children.Clear();
        _list.Margin = new Thickness(0, lines.Count > 0 ? 16 : 0, 0, 0);
        foreach (var (viewer, who, what, (how, why)) in lines)
        {
            var text = Ui.Column(3,
                new TextBlock { Text = who, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = what, FontSize = 13.5, Foreground = Ui.Brush("#DEDEE4"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = how, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
            if (why is not null) text.Children.Add(new TextBlock { Text = why, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
            var row = new DockPanel();
            if (viewer.CanStop && !viewer.Own)
            {
                Button? stop = null;
                stop = Ui.Action(L("Arrêter", "Stop"), null, "quiet", () => _ = StopAsync(viewer, who, stop!));
                Avalonia.Automation.AutomationProperties.SetName(stop, L($"Arrêter la lecture sur {who}", $"Stop playback on {who}"));
                stop.VerticalAlignment = VerticalAlignment.Top; stop.Margin = new Thickness(14, 0, 0, 0);
                DockPanel.SetDock(stop, Dock.Right); row.Children.Add(stop);
            }
            row.Children.Add(text);
            _list.Children.Add(row);
        }
    }
    private async Task StopAsync(Viewer viewer, string who, Button button)
    {
        if (_shell.Session is not { } session) return;
        button.IsEnabled = false;
        try
        {
            _status.Text = await session.Client.StopViewerAsync(viewer.SessionId, session.Lifetime) ? L($"Arrêt demandé à {who}.", $"Stop sent to {who}.") : L("Seul un administrateur de Jellyfin peut arrêter la lecture d’un autre compte.", "Only a Jellyfin administrator can stop another account’s playback.");
            await Task.Delay(1500); _shown = ""; await LoadAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { _status.Text = Errors.Friendly(ex); button.IsEnabled = true; }
    }
}
