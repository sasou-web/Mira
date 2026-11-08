using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Réglages → Confidentialité: every connection that leaves this PC, then those Jellyfin opens for Mira's features
/// (<see cref="Privacy"/> of Core, the Mac's too), each with where it goes, when, what it sends and how it stops: a
/// switch here for the update checks, else a button to the section that turns it on and off. Drawn each time the
/// section opens, so a switch changed elsewhere shows as it is.
/// </summary>
public partial class MainWindow
{
    private void ShowPrivacy()
    {
        PrivacySettings.Children.Clear();
        PrivacySettings.Children.Add(new TextBlock { Text = Privacy.Summary, Style = (Style)FindResource("SettingDescription"), FontSize = 13.5, Margin = new Thickness(0, 0, 30, 20) });
        var connections = Privacy.Connections(PrivacyApp.Windows);
        foreach (var server in new[] { false, true })
        {
            var heading = new TextBlock { Text = server ? Privacy.FromServer : Privacy.FromDevice(PrivacyApp.Windows), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Faint"), Margin = new Thickness(4, server ? 18 : 0, 0, 10) };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
            PrivacySettings.Children.Add(heading);
            if (server) PrivacySettings.Children.Add(new TextBlock { Text = Privacy.ServerNote, Style = (Style)FindResource("SettingDescription"), Margin = new Thickness(4, -4, 30, 12) });
            foreach (var connection in connections.Where(x => x.FromServer == server)) PrivacySettings.Children.Add(PrivacyRow(connection));
        }
        var details = new Button { Content = Privacy.DocumentLabel, Style = (Style)FindResource("Quiet"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        details.Click += (_, _) => OpenWebPage(Privacy.Document);
        PrivacySettings.Children.Add(details);
    }

    private Border PrivacyRow(PrivacyConnection connection)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = connection.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Fact(L("Vers", "To"), connection.To));
        text.Children.Add(Fact(L("Quand", "When"), connection.When));
        text.Children.Add(Fact(L("Envoie", "Sends"), connection.Sends));
        text.Children.Add(Fact(L("Pour l’arrêter", "To stop it"), connection.Stop));
        var row = new DockPanel();
        if (PrivacyControlFor(connection) is { } control) { DockPanel.SetDock(control, Dock.Right); row.Children.Add(control); }
        row.Children.Add(text);
        return new Border { Style = (Style)FindResource("SettingRow"), Child = row };
    }

    private TextBlock Fact(string label, string value)
    {
        var line = new TextBlock { Style = (Style)FindResource("SettingDescription") };
        line.Inlines.Add(new Run(label + " · ") { FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new Run(value));
        return line;
    }

    /// <summary>The switch of the row, or the button to its section; null when nothing in Mira stops it.</summary>
    private FrameworkElement? PrivacyControlFor(PrivacyConnection connection)
    {
        switch (connection.Control)
        {
            case PrivacyControl.Updates:
                // The same setting as « Mettre à jour automatiquement »: that switch changed, Réglages applies it as it does there.
                return PrivacySwitch(connection.Title, _settings.AutoUpdate, on => AutoUpdateCheck.IsChecked = on);
            case PrivacyControl.JellyfinReleases:
                return PrivacySwitch(connection.Title, _settings.JellyfinReleaseCheck, on =>
                {
                    _settings.JellyfinReleaseCheck = on;
                    if (!TrySaveSettings()) SetNotice(SettingsNotSaved);
                });
            case PrivacyControl.Tailscale when TailnetRow.Visibility == Visibility.Visible || _settings.Tailnet:
                return PrivacyGo(_settings.Tailnet ? L("Connecté · Gérer", "Signed in · Manage") : L("Coupé · Gérer", "Off · Manage"), connection.Title, () =>
                {
                    SelectSettingsTab("server");
                    Dispatcher.BeginInvoke(() => TailnetRow.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
                });
            case PrivacyControl.Downloads when _torlinkEnabled:
                return PrivacyGo(L("Gérer", "Manage"), connection.Title, () => SelectSettingsTab("torlink"));
            case PrivacyControl.GuestLinks when SharingSettingsNav.Visibility == Visibility.Visible:
                return PrivacyGo(L("Gérer", "Manage"), connection.Title, () => SelectSettingsTab("sharing"));
            case PrivacyControl.Maintenance when CareSettingsNav.Visibility == Visibility.Visible:
                return PrivacyGo(L("Gérer", "Manage"), connection.Title, () => SelectSettingsTab("care"));
            default:
                return null;
        }
    }

    private static CheckBox PrivacySwitch(string name, bool on, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(box, name);
        box.Click += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    private static Button PrivacyGo(string label, string name, Action go)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(button, label + " · " + name);
        button.Click += (_, _) => go();
        return button;
    }
}
