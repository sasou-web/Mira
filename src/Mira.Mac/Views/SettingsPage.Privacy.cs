using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Réglages → Confidentialité, as on Windows (<see cref="Privacy"/> of Core): every connection that leaves this Mac,
/// then those Jellyfin opens for Mira's features, each with where it goes, when, what it sends and how it stops; the
/// update check has its switch here (the same as « Mettre à jour automatiquement »), the rest a button to its section.
/// </summary>
public sealed partial class SettingsPage
{
    private ToggleSwitch _updatesSwitch = null!;
    /// <summary>For the self-check: the update check's switch on this page, once drawn.</summary>
    public ToggleSwitch? PrivacyUpdatesSwitch { get; private set; }

    private void DrawPrivacy()
    {
        var tab = _tabs["privacy"];
        tab.Children.Clear();
        tab.Children.Add(new TextBlock { Text = Privacy.Summary, FontSize = 13.5, LineHeight = 20, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 30, 10) });
        var connections = Privacy.Connections(PrivacyApp.Mac);
        foreach (var server in new[] { false, true })
        {
            var rows = connections.Where(x => x.FromServer == server).Select(PrivacyRow).ToList();
            if (server) rows.Insert(0, Description(Privacy.ServerNote, 0));
            var section = (StackPanel)Section(server ? Privacy.FromServer : Privacy.FromDevice(PrivacyApp.Mac), [.. rows]);
            // The note goes under the heading, not on a card of its own.
            if (server && section.Children[1] is Border { Child: TextBlock note } card) { card.Child = null; section.Children[1] = note; note.Margin = new Thickness(4, 0, 30, 0); }
            tab.Children.Add(section);
        }
        var details = Ui.Action(Privacy.DocumentLabel, "globe", "quiet", () => Updates.Open(new Uri(Privacy.Document)));
        details.HorizontalAlignment = HorizontalAlignment.Left; details.Margin = new Thickness(0, 6, 0, 0);
        tab.Children.Add(details);
    }

    private Control PrivacyRow(PrivacyConnection connection)
    {
        var text = Ui.Column(0, CardTitle(connection.Title), Fact(L("Vers", "To"), connection.To), Fact(L("Quand", "When"), connection.When),
            Fact(L("Envoie", "Sends"), connection.Sends), Fact(L("Pour l’arrêter", "To stop it"), connection.Stop));
        text.Margin = new Thickness(0, 0, 20, 0);
        var dock = new DockPanel();
        if (PrivacyControlFor(connection) is { } control) { control.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(control, Dock.Right); dock.Children.Add(control); }
        dock.Children.Add(text);
        return dock;
    }

    private static TextBlock Fact(string label, string value)
    {
        var line = Description("");
        line.IsVisible = true;
        line.Inlines = [new Run(label + " · ") { FontWeight = FontWeight.SemiBold }, new Run(value)];
        return line;
    }

    /// <summary>The switch of the row, or the button to its section; null when nothing in Mira stops it.</summary>
    private Control? PrivacyControlFor(PrivacyConnection connection)
    {
        switch (connection.Control)
        {
            case PrivacyControl.Updates:
                // The switch of « Mettre à jour automatiquement » moved, which saves and asks GitHub again when turned on.
                var toggle = new ToggleSwitch { IsChecked = Shell.Settings.AutoUpdate, OnContent = null, OffContent = null, MinWidth = 0 };
                Avalonia.Automation.AutomationProperties.SetName(toggle, connection.Title);
                toggle.IsCheckedChanged += (_, _) => _updatesSwitch.IsChecked = toggle.IsChecked == true;
                return PrivacyUpdatesSwitch = toggle;
            case PrivacyControl.Tailscale when Tailnet.Section.IsVisible || Shell.Settings.Tailnet:
                return PrivacyGo(Shell.Settings.Tailnet ? L("Connecté · Gérer", "Signed in · Manage") : L("Coupé · Gérer", "Off · Manage"), connection.Title, () =>
                {
                    Show("server");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Tailnet.Section.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
                });
            case PrivacyControl.Maintenance when _tabButtons["care"].IsVisible:
                return PrivacyGo(L("Gérer", "Manage"), connection.Title, () => Show("care"));
            default:
                return null;
        }
    }

    private static Button PrivacyGo(string label, string name, Action go)
    {
        var button = Ui.Action(label, null, "", go);
        Avalonia.Automation.AutomationProperties.SetName(button, label + " · " + name);
        return button;
    }
}
