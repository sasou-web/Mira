using System.Windows;
using System.Windows.Controls;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « Lectures impossibles signalées » (Réglages → Jellyfin & synchronisation): the reports Mira web left in Jellyfin's
// logs (« Envoyer le détail à Jellyfin » under « Lecture impossible »), for an administrator, with « Copier ».
public partial class MainWindow
{
    private bool _failuresBusy;
    /// <summary>Read each time the tab opens; hidden while there is none, or when this account may not read the logs.</summary>
    private async Task ShowFailuresAsync()
    {
        if (_demo || _client is not { } client) { FailuresRow.Visibility = Visibility.Collapsed; return; }
        if (_failuresBusy) return;
        _failuresBusy = true;
        var ct = _connectionLifetime.Token;
        try
        {
            var failures = await client.PlaybackFailuresAsync(ct);
            if (client == _client) RenderFailures(failures ?? []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        // Jellyfin out of reach: the rest of the tab says so already; these come back with it.
        catch (Exception ex) when (IsExpected(ex)) { }
        finally { _failuresBusy = false; }
    }
    private void RenderFailures(List<PlaybackFailure> failures)
    {
        FailuresPanel.Children.Clear();
        FailuresRow.Visibility = failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var now = DateTimeOffset.Now;
        foreach (var failure in failures)
        {
            var at = failure.At.ToLocalTime();
            var heading = PlaybackFailures.Heading(failure);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = PlaybackFailures.When(at, now), Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 3, 0, 0) });
            if (failure.Media is { } media) text.Children.Add(new TextBlock { Text = media, Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            if (failure.Message.Length > 0) text.Children.Add(new TextBlock { Text = failure.Message, Foreground = Palette.Get("TextBody"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            foreach (var attempt in failure.Attempts)
                text.Children.Add(new TextBlock { Text = "· " + attempt, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(0, 2, 0, 0) });
            var copy = new Button { Content = "Copier", Style = (Style)FindResource("Quiet"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(copy, $"Copier le détail : {heading}");
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(PlaybackFailures.Text(failure, at)); SetNotice("Détail copié : colle-le à qui t’aide."); }
                catch (System.Runtime.InteropServices.ExternalException) { SetNotice("Le presse-papiers est occupé : réessaie."); }
            };
            var row = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy);
            row.Children.Add(text);
            FailuresPanel.Children.Add(row);
        }
    }
}
