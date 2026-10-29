using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Collections made by hand (<see cref="TitleCollections"/>), as Windows and Mira web make them: « Ajouter à une
/// collection… » of a film or series (the collections that hold it ticked, a click takes it out, a new one named with it),
/// or a collection renamed (an administrator). A name is refused before Jellyfin sees it when it is already a collection's
/// name or folder: Jellyfin would put the new one in the other's place, its titles lost.
/// </summary>
public sealed class CollectionWindow : Window
{
    private readonly Session _session;
    private readonly MediaItem? _item, _renamed;
    private readonly TextBox _name = new() { MaxLength = 120 };
    private readonly TextBlock _status = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new() { Spacing = 2 };
    private static readonly IBrush Danger = Ui.Brush("#F3AD99");
    private List<MediaItem> _collections = [];
    private bool _busy;

    /// <summary>What to say once the window closes; whether a collection changed (the pages are drawn again).</summary>
    public string? Said { get; private set; }
    public bool Changed { get; private set; }
    /// <summary>The new name of the collection renamed.</summary>
    public string? NewName { get; private set; }

    /// <param name="item">The film or series put in collections; null with <paramref name="renamed"/>, the collection renamed.</param>
    public CollectionWindow(Session session, MediaItem? item, MediaItem? renamed = null)
    {
        _session = session; _item = item; _renamed = renamed;
        Title = renamed is not null ? L("Renommer la collection", "Rename collection") : L("Ajouter à une collection", "Add to collection");
        Width = 540; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Avalonia.Automation.AutomationProperties.SetName(_name, L("Nom de la collection", "Collection name"));
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_status, Avalonia.Automation.AutomationLiveSetting.Polite);
        _name.Watermark = L("Soirées, À montrer à Léa…", "Movie nights, To show Léa…");
        _name.Text = renamed?.Name ?? "";
        _name.TextChanged += (_, _) => { if (_status.Foreground == Danger) Say(""); };
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SaveAsync(); } };
        var save = Ui.Action(renamed is not null ? L("Renommer", "Rename") : L("Créer", "Create"), renamed is not null ? "check" : "plus", "primary", () => _ = SaveAsync());
        var line = new DockPanel();
        DockPanel.SetDock(save, Dock.Right); save.Margin = new Thickness(10, 0, 0, 0);
        line.Children.Add(save); line.Children.Add(_name);
        var close = Ui.Action(L("Fermer", "Close"), null, "quiet", Close);
        close.HorizontalAlignment = HorizontalAlignment.Left;
        var note = renamed is not null ? L("Ses titres restent où ils sont : une collection ne fait que les rassembler.", "Its titles stay where they are: a collection only brings them together.")
            : L("Il va dans les collections cochées : rien n’est copié ni déplacé.", "It goes in the ticked collections: nothing is copied or moved.");
        var column = Ui.Column(14,
            Ui.Heading(renamed?.Name ?? item?.DisplayTitle ?? "", 24, 1),
            new TextBlock { Text = note, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        if (renamed is null) column.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 320 });
        column.Children.Add(Ui.Text(renamed is not null ? L("Nouveau nom", "New name") : L("Ou une nouvelle collection avec lui", "Or a new collection with it"), 13, FontWeight.SemiBold));
        column.Children.Add(line); column.Children.Add(_status); column.Children.Add(close);
        Content = new Border { Padding = new Thickness(32, 28), Child = column };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Opened += (_, _) => { _name.Focus(); if (renamed is not null) _name.SelectAll(); _ = LoadAsync(); };
    }

    private async Task LoadAsync()
    {
        if (_item is null) Say("");
        else Say(L("Jellyfin cherche tes collections…", "Jellyfin is looking for your collections…"));
        try
        {
            var all = _session.Client.CollectionsAsync();
            var holding = _item is null ? Task.FromResult<HashSet<string>?>(null) : _session.Client.CollectionsOfAsync(_item.Id);
            _collections = await all;
            if (_item is null) return;
            var inside = await holding;
            _list.Children.Clear();
            foreach (var collection in TitleCollections.Sorted(_collections)) _list.Children.Add(Row(_item, collection, inside));
            // Jellyfin 10.9 and 10.10 cannot say which collections hold a title: a click adds it, again harmlessly.
            Say(_collections.Count == 0 ? L("Aucune collection pour l’instant : nomme la première.", "No collections yet: name the first one.")
                : inside is null ? L("Ton Jellyfin ne dit pas dans quelles collections il est déjà.", "Your Jellyfin doesn’t say which collections it’s already in.") : "");
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Say(Errors.Friendly(ex), error: true); }
    }

    /// <summary>A collection: its name and count, ticked when it holds the title; a click puts the title in or takes it out.</summary>
    private Button Row(MediaItem item, MediaItem collection, HashSet<string>? holding)
    {
        var inside = holding?.Contains(collection.Id) == true;
        var count = collection.ChildCount is int n and > 0 ? Lang.Count(n, "titre", "titres", "title", "titles") : L("Vide", "Empty");
        var tick = new Icon("check", 18) { VerticalAlignment = VerticalAlignment.Center };
        var text = Ui.Column(2, Ui.Text(collection.Name, 15, FontWeight.SemiBold), Ui.Text(count, 12, color: Ui.Muted));
        var content = new DockPanel();
        DockPanel.SetDock(tick, Dock.Right); content.Children.Add(tick);
        text.Margin = new Thickness(12, 0, 0, 0);
        var glyph = new Icon("library", 18) { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(glyph, Dock.Left); content.Children.Add(glyph); content.Children.Add(text);
        var row = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        row.Classes.Add("quiet");
        void Show()
        {
            tick.Opacity = inside ? 1 : 0;
            Avalonia.Automation.AutomationProperties.SetName(row, $"{collection.Name}, {count}, {(inside ? L("coché", "ticked") : L("pas coché", "not ticked"))}");
        }
        Show();
        row.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                await _session.Client.SetInCollectionAsync(collection.Id, [item.Id], !inside);
                inside = !inside; Changed = true; Show();
                Say(inside ? TitleCollections.Added(collection.Name) : TitleCollections.Removed(collection.Name));
            }
            catch (Exception ex) when (Errors.Expected(ex)) { Say(Errors.Friendly(ex), error: true); }
            finally { _busy = false; }
        };
        return row;
    }

    /// <summary>The name typed: refused at once while it cannot be one, then sent (Jellyfin's list is read again before, by the client).</summary>
    private async Task SaveAsync()
    {
        if (_busy) return;
        var name = TitleCollections.Clean(_name.Text);
        if (TitleCollections.NameProblem(name, _collections, _renamed?.Id) is { } refusal) { Say(refusal.Text, error: true); _name.Focus(); return; }
        _busy = true;
        try
        {
            if (_renamed is { } collection)
            {
                if (name != collection.Name)
                {
                    await _session.Client.RenameCollectionAsync(collection.Id, name);
                    (Said, NewName, Changed) = (L($"Collection renommée « {name} ».", $"Collection renamed “{name}”."), name, true);
                }
            }
            else if (_item is { } item)
            {
                await _session.Client.CreateCollectionAsync(name, [item.Id]);
                (Said, Changed) = (L($"Collection « {name} » créée avec « {item.DisplayTitle} ».", $"Collection “{name}” created with “{item.DisplayTitle}”."), true);
            }
            Close();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Say(Errors.Friendly(ex), error: true); }
        finally { _busy = false; }
    }

    /// <summary>Said under the name: what was done, or why it was refused (error).</summary>
    private void Say(string text, bool error = false) { _status.Text = text; _status.Foreground = error ? Danger : Ui.Muted; }
}
