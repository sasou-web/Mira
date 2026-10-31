using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// Collections made by hand (TitleCollections), as Mira web and Mac make them: « Ajouter à une collection… » in the menu
// of a film or series (the collections that hold it ticked, a click takes it out, a new one named with it), « Retirer de
// la collection » in a collection's titles, « Renommer… » (an administrator) and « Supprimer la collection… » in a
// collection's menu. Only for an account Jellyfin lets manage collections; never a file moved or copied.
public partial class MainWindow
{
    /// <summary>What the account of a client may do with collections, once Jellyfin said.</summary>
    private (JellyfinClient Client, TitleCollections.Rights Rights)? _collectionRightsOf;
    /// <summary>The title put in collections, or the collection renamed (the other one null).</summary>
    private MediaItem? _collecting, _renaming;
    /// <summary>The collections as Jellyfin listed them when the overlay opened: a name is judged against them at once.</summary>
    private List<MediaItem> _collectionsShown = [];
    private int _collectionVersion;
    private bool _collectionBusy;

    private TitleCollections.Rights CollectionRights => _collectionRightsOf is { } known && ReferenceEquals(known.Client, _client) ? known.Rights : TitleCollections.Rights.None;

    /// <summary>Asked once per account, so that a card's menu, drawn at once, knows what to offer.</summary>
    private async Task LoadCollectionRightsAsync(JellyfinClient client)
    {
        if (_demo || _collectionRightsOf is { } known && ReferenceEquals(known.Client, client)) return;
        try
        {
            var rights = await client.CollectionRightsAsync(_connectionLifetime.Token);
            if (ReferenceEquals(client, _client)) _collectionRightsOf = (client, rights);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex)) { }
    }

    /// <summary>The menu's entries of a film or series: « Ajouter à une collection… », and in a collection's titles « Retirer de la collection ».</summary>
    private void AddCollectionEntries(ContextMenu menu, MediaItem item)
    {
        if (!TitleCollections.Holds(item) || !CollectionRights.Manage) return;
        menu.Items.Add(MenuAction(L("Ajouter à une collection…", "Add to collection…"), "library", () => { ShowCollections(item); return Task.CompletedTask; }));
        if (_catalog.Saga is { } shown) menu.Items.Add(MenuAction(L("Retirer de la collection", "Remove from collection"), "minus", () => PutInCollectionAsync(item, shown.Id, shown.Name, false)));
    }
    /// <summary>The menu's entries of a collection: « Renommer… » for an administrator, « Supprimer la collection… ».</summary>
    private void AddCollectionActions(ContextMenu menu, MediaItem collection)
    {
        var can = CollectionRights;
        if (!can.Manage) return;
        menu.Items.Add(new Separator());
        if (can.Rename) menu.Items.Add(MenuAction(L("Renommer…", "Rename…"), "pencil", () => { ShowRenameCollection(collection); return Task.CompletedTask; }));
        menu.Items.Add(MenuAction(L("Supprimer la collection…", "Delete collection…"), "trash", () => { AskDeleteCollection(collection); return Task.CompletedTask; }));
    }

    // —— « Ajouter à une collection » ——
    private void ShowCollections(MediaItem item)
    {
        _collecting = item; _renaming = null; var version = ++_collectionVersion;
        CollectionKicker.Text = L("AJOUTER À UNE COLLECTION", "ADD TO COLLECTION");
        CollectionTitle.Text = item.DisplayTitle;
        CollectionNote.Text = L("Il va dans les collections cochées : rien n’est copié ni déplacé.", "It goes in the ticked collections: nothing is copied or moved.");
        CollectionList.Children.Clear(); CollectionScroll.Visibility = Visibility.Visible;
        CollectionNameLabel.Text = L("Ou une nouvelle collection avec lui", "Or a new collection with it");
        CollectionName.Text = ""; CollectionSave.Content = ActionLabel("plus", L("Créer", "Create"));
        SayInCollections(L("Jellyfin cherche tes collections…", "Jellyfin is looking for your collections…"));
        OpenCollectionOverlay();
        _ = ListCollectionsAsync(item, version);
    }
    private async Task ListCollectionsAsync(MediaItem item, int version)
    {
        if (_client is not { } client) return;
        try
        {
            var all = client.CollectionsAsync(ct: _connectionLifetime.Token);
            var holding = client.CollectionsOfAsync(item.Id, _connectionLifetime.Token);
            _collectionsShown = await all;
            var inside = await holding;
            if (version != _collectionVersion) return;
            CollectionList.Children.Clear();
            foreach (var collection in TitleCollections.Sorted(_collectionsShown)) CollectionList.Children.Add(CollectionRow(item, collection, inside));
            // Jellyfin 10.9 and 10.10 cannot say which collections hold a title: a click adds it, again harmlessly.
            SayInCollections(_collectionsShown.Count == 0 ? L("Aucune collection pour l’instant : nomme la première.", "No collections yet: name the first one.")
                : inside is null ? L("Ton Jellyfin ne dit pas dans quelles collections il est déjà.", "Your Jellyfin doesn’t say which collections it’s already in.") : "");
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { if (version == _collectionVersion) SayInCollections(Friendly(ex)); }
    }
    /// <summary>A collection: its name and count, ticked when it holds the title; a click puts the title in or takes it out.</summary>
    private Button CollectionRow(MediaItem item, MediaItem collection, HashSet<string>? holding)
    {
        var inside = holding?.Contains(collection.Id) == true;
        var tick = new Icon { Kind = "check", Width = 18, Height = 18, Foreground = Palette.Get("Text"), VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = collection.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var count = collection.ChildCount is int n and > 0 ? Lang.Count(n, "titre", "titres", "title", "titles") : L("Vide", "Empty");
        text.Children.Add(new TextBlock { Text = count, FontSize = 12, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 2, 0, 0) });
        var content = new DockPanel();
        DockPanel.SetDock(tick, Dock.Right); content.Children.Add(tick);
        var glyph = new Icon { Kind = "library", Width = 18, Height = 18, Foreground = Palette.Get("TextBody"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(glyph, Dock.Left); content.Children.Add(glyph); content.Children.Add(text);
        var row = new Button { Style = (Style)FindResource("Quiet"), Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(-10, 0, -10, 2) };
        void Show()
        {
            tick.Visibility = inside ? Visibility.Visible : Visibility.Hidden;
            AutomationProperties.SetName(row, $"{collection.Name}, {count}, {(inside ? L("coché", "ticked") : L("pas coché", "not ticked"))}");
        }
        Show();
        row.Click += async (_, _) =>
        {
            if (_collectionBusy) return;
            _collectionBusy = true;
            try { if (await PutInCollectionAsync(item, collection.Id, collection.Name, !inside, quiet: true)) { inside = !inside; Show(); } }
            finally { _collectionBusy = false; }
        };
        return row;
    }
    /// <summary>
    /// A title put in a collection or taken out of it; said in the overlay when it is open (quiet), else by a notice
    /// whose « Annuler » puts it back. False when Jellyfin refused.
    /// </summary>
    private async Task<bool> PutInCollectionAsync(MediaItem item, string collectionId, string collectionName, bool inside, bool quiet = false)
    {
        if (_client is not { } client) return false;
        try
        {
            await client.SetInCollectionAsync(collectionId, [item.Id], inside, _connectionLifetime.Token);
            var said = inside ? TitleCollections.Added(collectionName) : TitleCollections.Removed(collectionName);
            if (quiet) SayInCollections(said);
            else if (inside) SetNotice(said);
            else SetNotice(said, L("Annuler", "Undo"), () => _ = PutInCollectionAsync(item, collectionId, collectionName, true), sticky: false);
            _ = RefreshAsync(quiet: true);
            return true;
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { return false; }
        catch (Exception ex) when (IsExpected(ex)) { if (quiet) SayInCollections(Friendly(ex), error: true); else SetError(Friendly(ex)); return false; }
    }

    // —— A collection's name: a new one, or one renamed ——
    private void ShowRenameCollection(MediaItem collection)
    {
        _collecting = null; _renaming = collection; var version = ++_collectionVersion;
        CollectionKicker.Text = L("RENOMMER LA COLLECTION", "RENAME COLLECTION");
        CollectionTitle.Text = collection.Name;
        CollectionNote.Text = L("Ses titres restent où ils sont : une collection ne fait que les rassembler.", "Its titles stay where they are: a collection only brings them together.");
        CollectionList.Children.Clear(); CollectionScroll.Visibility = Visibility.Collapsed;
        CollectionNameLabel.Text = L("Nouveau nom", "New name");
        CollectionName.Text = collection.Name; CollectionSave.Content = ActionLabel("check", L("Renommer", "Rename"));
        SayInCollections("");
        OpenCollectionOverlay();
        _ = ReadCollectionsAsync(version);
    }
    /// <summary>The collections a new name is judged against at once (Jellyfin's list is read again before it is sent).</summary>
    private async Task ReadCollectionsAsync(int version)
    {
        if (_client is not { } client) return;
        try { var all = await client.CollectionsAsync(ct: _connectionLifetime.Token); if (version == _collectionVersion) _collectionsShown = all; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
    private void CollectionName_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; _ = SaveCollectionNameAsync(); } }
    private void CollectionName_TextChanged(object sender, TextChangedEventArgs e) { if (CollectionStatus.Tag is "error") SayInCollections(""); }
    private void CollectionSave_Click(object sender, RoutedEventArgs e) => _ = SaveCollectionNameAsync();

    /// <summary>
    /// The name typed, refused at once while it cannot be one (a name or folder already taken: Jellyfin would put the new
    /// collection in the other's place, its titles lost), then sent; Jellyfin's own list is read again before (the client).
    /// </summary>
    private async Task SaveCollectionNameAsync()
    {
        if (_client is not { } client || _collectionBusy) return;
        var name = TitleCollections.Clean(CollectionName.Text);
        if (TitleCollections.NameProblem(name, _collectionsShown, _renaming?.Id) is { } refusal) { SayInCollections(refusal.Text, error: true); CollectionName.Focus(); return; }
        _collectionBusy = true; CollectionSave.IsEnabled = false;
        try
        {
            if (_renaming is { } collection)
            {
                if (name != collection.Name)
                {
                    await client.RenameCollectionAsync(collection.Id, name, _connectionLifetime.Token);
                    if (_catalog.Saga?.Id == collection.Id) { _catalog.Saga = (collection.Id, name); ShowPersonFilter(); }
                    SetNotice(L($"Collection renommée « {name} ».", $"Collection renamed “{name}”."));
                }
            }
            else if (_collecting is { } item)
            {
                var id = await client.CreateCollectionAsync(name, [item.Id], _connectionLifetime.Token);
                SetNotice(L($"Collection « {name} » créée avec « {item.DisplayTitle} ».", $"Collection “{name}” created with “{item.DisplayTitle}”."),
                    L("Voir", "View"), () => _ = ShowSagaAsync(new MediaItem { Id = id, Name = name, Type = "BoxSet" }, ""), sticky: false);
            }
            await CloseCollectionsAsync();
            _ = RefreshAsync(quiet: true);
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SayInCollections(Friendly(ex), error: true); }
        finally { _collectionBusy = false; CollectionSave.IsEnabled = true; }
    }

    // —— « Supprimer la collection… » ——
    private void AskDeleteCollection(MediaItem collection) =>
        SetNotice(TitleCollections.DeleteQuestion(collection.Name), L("Supprimer", "Delete"), () => _ = DeleteCollectionAsync(collection), sticky: true);
    private async Task DeleteCollectionAsync(MediaItem collection)
    {
        if (_client is not { } client) return;
        try
        {
            await client.DeleteCollectionAsync(collection.Id, _connectionLifetime.Token);
            SetNotice(TitleCollections.Deleted(collection.Name));
            // Its titles shown: the page it was opened from comes back.
            if (_catalog.Saga?.Id == collection.Id) { await BackFromPersonAsync(); return; }
            _ = RefreshAsync(quiet: true);
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }

    private void OpenCollectionOverlay()
    {
        RememberFocus(ref _collectionOrigin, CollectionOverlay);
        Motion.Reveal(CollectionOverlay, 260, 0); Motion.Reveal(CollectionCard, 360, 16);
        _ = Dispatcher.BeginInvoke(() =>
        {
            CollectionName.Focus();
            if (_renaming is not null) CollectionName.SelectAll();
        }, DispatcherPriority.Input);
    }
    /// <summary>Said under the name: what was done, or why it was refused (error).</summary>
    private void SayInCollections(string text, bool error = false)
    {
        CollectionStatus.Text = text; CollectionStatus.Tag = error ? "error" : null;
        CollectionStatus.Foreground = Palette.Get(error ? "Danger" : "Muted");
    }
    private async void CollectionClose_Click(object sender, RoutedEventArgs e) => await CloseCollectionsAsync();
    private async Task CloseCollectionsAsync()
    {
        ++_collectionVersion;
        await Motion.HideAsync(CollectionOverlay);
        GiveFocusBack(ref _collectionOrigin);
    }
}
