using Avalonia.Automation;
using Avalonia.Controls;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// The title and the line under it open their page, as on Windows (MainWindow.PlayerLinks.cs): the series (or the film)
// from the title, the episode's season from the line. The video goes on in the mini-player, over the page.
public sealed partial class PlayerPage
{
    private Button Heading(TextBlock text, bool episodeLine)
    {
        var button = new Button { Content = text, Classes = { "heading" }, IsVisible = !string.IsNullOrEmpty(text.Text) };
        // VoiceOver reads a button by its content only when it is a string: named after the text it shows. Without one
        // (a film without a line under its title, a title not loaded yet), no button for Tab to stop on.
        text.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBlock.TextProperty) return;
            AutomationProperties.SetName(button, text.Text ?? "");
            button.IsVisible = !string.IsNullOrEmpty(text.Text);
            var episode = PlayingTitle is { Type: "Episode", SeriesId: not null };
            ToolTip.SetTip(button, !episode ? L("Voir la fiche", "View details") : episodeLine ? L("Voir l’épisode dans sa saison", "View the episode in its season") : L("Voir la série", "View show"));
        };
        button.Click += (_, _) => OpenHeading(episodeLine);
        return button;
    }
    private void OpenHeading(bool episodeLine)
    {
        // A part of a title in several files is hidden from the library: its title's page.
        if (_stopped || PlayingTitle is not { } item) return;
        SetMini(true);
        if (item.Type == "Episode" && item.SeriesId is { } series && !episodeLine) _shell.Open(new MediaItem { Id = series, Name = item.SeriesName ?? item.Name, Type = "Series" });
        else _shell.Open(item);
    }
}
