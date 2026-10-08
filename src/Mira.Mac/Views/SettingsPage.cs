using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>Réglages: the account, playback, languages and subtitles, and the version of Mira.</summary>
public sealed class SettingsPage : Page
{
    public override string Rail => "settings";
    private static readonly (string Label, string Value)[] Audio =
        [("Japonais, puis français", "jpn,ja,fre,fra,fr,eng,en"), ("Français, puis anglais", "fre,fra,fr,eng,en"), ("Anglais, puis français", "eng,en,fre,fra,fr")];
    private static readonly (string Label, string Value)[] Subtitles =
        [("Français, puis anglais", "fre,fra,fr,eng,en"), ("Anglais", "eng,en"), ("Japonais", "jpn,ja")];

    public SettingsPage(MainWindow shell) : base(shell)
    {
        var settings = shell.Settings;
        var page = new StackPanel { Spacing = 34, Margin = new Thickness(56, 44 + shell.TitleBarInset, 56, 60), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Ui.Text("Réglages", 34, FontWeight.ExtraBold));

        var connection = shell.Session?.Connection;
        page.Children.Add(Section("Compte",
            Row("Serveur Jellyfin", connection?.Server ?? "—", null),
            Row("Utilisateur", connection?.UserName ?? "—", Ui.Action("Se déconnecter", "exit", "", () => _ = Shell.SignOutAsync()))));

        page.Children.Add(Section("Lecture",
            Toggle("Reprendre là où tu t’es arrêté", "Sinon, chaque titre repart du début.", settings.RememberPosition, v => settings.RememberPosition = v),
            Toggle("Épisode suivant automatique", "À la fin d’un épisode, le suivant démarre.", settings.AutoNext, v => settings.AutoNext = v),
            Toggle("Décodage matériel", "Utilise la puce du Mac pour décoder la vidéo : moins de chauffe, plus d’autonomie.", settings.HardwareDecoding, v => settings.HardwareDecoding = v)));

        page.Children.Add(Section("Langues",
            Choice("Audio", Audio, settings.AudioLanguage, v => settings.AudioLanguage = v),
            Choice("Sous-titres", Subtitles, settings.SubtitleLanguage, v => settings.SubtitleLanguage = v)));

        var size = new Slider { Minimum = 24, Maximum = 64, Value = settings.SubtitleSize, Width = 240, TickFrequency = 2, IsSnapToTickEnabled = true };
        var sizeLabel = new TextBlock { Text = $"{settings.SubtitleSize}", Width = 36, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted };
        size.ValueChanged += (_, e) => { settings.SubtitleSize = (int)e.NewValue; sizeLabel.Text = $"{settings.SubtitleSize}"; Save(); };
        Avalonia.Automation.AutomationProperties.SetName(size, "Taille des sous-titres");
        page.Children.Add(Section("Sous-titres", Row("Taille", "Appliquée à la prochaine lecture.", Ui.Row(12, size, sizeLabel))));

        page.Children.Add(Section("Mira",
            Row($"Version {Updates.Current}", "Mira pour Mac (Apple Silicon).", Ui.Action("Rechercher une mise à jour", "download", "", () => _ = Updates.CheckAsync(Shell, asked: true))),
            Row("Projet", "Le code, les nouveautés et les versions pour Windows et Mac.", Ui.Action("Ouvrir GitHub", "globe", "", () => Updates.Open("https://github.com/sasou-web/Mira")))));
        Content = new ScrollViewer { Content = page };
    }
    private void Save() => Shell.Profile.SaveSettings(Shell.Settings);

    private static Control Section(string title, params Control[] rows)
    {
        var list = new StackPanel { Spacing = 1 };
        for (var i = 0; i < rows.Length; i++)
        {
            var radius = new CornerRadius(i == 0 ? 12 : 0, i == 0 ? 12 : 0, i == rows.Length - 1 ? 12 : 0, i == rows.Length - 1 ? 12 : 0);
            list.Children.Add(new Border { Background = Ui.Brush("#111113"), CornerRadius = radius, Padding = new Thickness(22, 16), Child = rows[i] });
        }
        return Ui.Column(12, new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.Bold }, list);
    }
    private static Control Row(string title, string detail, Control? action)
    {
        var dock = new DockPanel();
        if (action is not null) { action.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(action, Dock.Right); dock.Children.Add(action); }
        var text = Ui.Column(3, new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = detail, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        text.Margin = new Thickness(0, 0, 24, 0);
        dock.Children.Add(text);
        return dock;
    }
    private Control Toggle(string title, string detail, bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = null, OffContent = null, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(toggle, title);
        toggle.IsCheckedChanged += (_, _) => { set(toggle.IsChecked == true); Save(); };
        return Row(title, detail, toggle);
    }
    private Control Choice(string title, (string Label, string Value)[] options, string current, Action<string> set)
    {
        var box = new ComboBox { MinWidth = 240 };
        foreach (var (label, value) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        if (options.All(x => x.Value != current)) box.Items.Add(new ComboBoxItem { Content = "Personnalisé : " + current, Tag = current });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(x => (string)x.Tag! == current);
        Avalonia.Automation.AutomationProperties.SetName(box, title);
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string value }) { set(value); Save(); } };
        return Row(title, "Les pistes choisies en premier, dans cet ordre.", box);
    }
}
