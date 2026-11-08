using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// The menu bar of a Mac app, beside Mira's own menu (App.axaml): Édition, Présentation, Aller, Fenêtre, Aide, their keys shown
// as macOS shows them. macOS gives a key the menu has to the menu, never to the window: so an item whose keys Mira
// already reads does no more than press them again where the focus is (Replay), and the key does what it did before
// the menu, the field's copy and paste, ⌘[ that reduces the player, from the keys or from the menu alike.
public sealed partial class MainWindow
{
    private long _fullScreenAt = long.MinValue / 2;
    private NativeMenuItem? _fullScreenItem;

    private void BuildMenuBar()
    {
        var keys = PlatformSettings?.HotkeyConfiguration;
        var command = keys?.CommandModifiers ?? KeyModifiers.Meta;
        var edit = Menu(L("Édition", "Edit"),
            Pressing(L("Annuler", "Undo"), keys?.Undo),
            Pressing(L("Rétablir", "Redo"), keys?.Redo),
            null,
            Pressing(L("Couper", "Cut"), keys?.Cut),
            Pressing(L("Copier", "Copy"), keys?.Copy),
            Pressing(L("Coller", "Paste"), keys?.Paste),
            Pressing(L("Tout sélectionner", "Select All"), keys?.SelectAll));
        // Its name says what it does now, as macOS's own: « Passer en mode plein écran », then « Quitter le mode plein écran ».
        _fullScreenItem = Item(FullScreenName(), new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta), ToggleFullScreen);
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty && _fullScreenItem is { } item) item.Header = FullScreenName(); };
        var view = Menu(L("Présentation", "View"), _fullScreenItem);
        var go = Menu(L("Aller", "Go"),
            Pressing(L("Précédent", "Back"), [new KeyGesture(Key.OemOpenBrackets, command)]),
            Pressing(L("Suivant", "Forward"), [new KeyGesture(Key.OemCloseBrackets, command)]),
            null,
            Item(L("Accueil", "Home"), null, () => GoFromMenu("home")),
            Item(L("Rechercher", "Search"), null, () => GoFromMenu("search")),
            Item(L("Films", "Movies"), null, () => GoFromMenu("Movie")),
            Item(L("Séries", "Shows"), null, () => GoFromMenu("Series")),
            Item(L("Toute la bibliothèque", "Whole library"), null, () => GoFromMenu("library")),
            Item(L("Sur ce Mac", "On this Mac"), null, () => GoFromMenu("offline")));
        var window = Menu(L("Fenêtre", "Window"),
            Item(L("Placer dans le Dock", "Minimize"), new KeyGesture(Key.M, command), () => { if (WindowState != WindowState.FullScreen) WindowState = WindowState.Minimized; }),
            Item(L("Réduire/agrandir", "Zoom"), null, () => { if (WindowState != WindowState.FullScreen) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }),
            null,
            // As in Music or Mail: the window goes, Mira stays in the Dock, a click on its icon brings it back (MainWindow.MacWindow.cs).
            Item(L("Fermer la fenêtre", "Close Window"), new KeyGesture(Key.W, command), Close));
        // The menu macOS names Help (it adds its search field to it): the guide, the shortcuts, a report for a problem.
        var help = Menu(L("Aide", "Help"),
            Item(L("Guide de Mira pour Mac", "Mira for Mac Guide"), null, () => Updates.Open(Guide)),
            Item(L("Raccourcis clavier", "Keyboard Shortcuts"), new KeyGesture(Key.OemQuestion, command), () => { Reopen(); ShowShortcuts(); }),
            Item(L("Nouveautés de Mira", "What’s New in Mira"), null, () => { Reopen(); ShowWhatsNew(Mira.Core.WhatsNew.Recent(Updates.Current)); }),
            null,
            Item(L("Exporter un rapport…", "Export a Report…"), null, () => { Reopen(); _ = ExportReportAsync(); }));
        NativeMenu.SetMenu(this, new NativeMenu { Items = { edit, view, go, window, help } });
    }
    /// <summary>The Mac page of wiki.miracinema.app, in Mira's language: opened in the browser, never by Mira itself.</summary>
    public static Uri Guide => new(IsFrench ? "https://wiki.miracinema.app/fr/mac/" : "https://wiki.miracinema.app/mac/");
    private static NativeMenuItem Menu(string header, params NativeMenuItemBase?[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items) menu.Items.Add(item ?? new NativeMenuItemSeparator());
        return new NativeMenuItem(header) { Menu = menu };
    }
    private static NativeMenuItem Item(string header, KeyGesture? keys, Action act)
    {
        var item = new NativeMenuItem(header) { Gesture = keys };
        item.Click += (_, _) => act();
        return item;
    }
    /// <summary>An item that presses its keys again where the focus is: what they did before the menu took them.</summary>
    private NativeMenuItem Pressing(string header, IReadOnlyList<KeyGesture>? keys) =>
        keys is [var first, ..] ? Item(header, first, () => Replay(first)) : Item(header, null, () => { });
    private void Replay(KeyGesture keys)
    {
        var target = FocusManager?.GetFocusedElement() as Interactive ?? this;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = keys.Key, KeyModifiers = keys.KeyModifiers, Source = target });
    }
    /// <summary>A rail entry from the menu: the player first reduced, as « Précédent » does.</summary>
    private void GoFromMenu(string key)
    {
        if (CurrentSheet is not null) return;
        if (Player is { IsMini: false } player) player.SetMini(true);
        NavigateRoot(key);
    }
    private string FullScreenName() => WindowState == WindowState.FullScreen ? L("Quitter le mode plein écran", "Exit Full Screen") : L("Passer en mode plein écran", "Enter Full Screen");
    /// <summary>⌃⌘F: the player's own (its button changes with it) while it plays in the window, else the window's.</summary>
    public void ToggleFullScreen()
    {
        // The key held repeats the item: once, and not while macOS still slides the window.
        var now = Environment.TickCount64;
        if (now - _fullScreenAt < 800) return;
        _fullScreenAt = now;
        if (Player is { IsMini: false } player) { player.ToggleFullscreen(); return; }
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }
    /// <summary>The menu bar's items by name, as macOS shows them: for the self-check.</summary>
    public IReadOnlyList<(string Menu, string Item, KeyGesture? Keys)> MenuBar() =>
        NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>()
            .SelectMany(menu => menu.Menu?.Items.OfType<NativeMenuItem>().Select(item => (menu.Header ?? "", item.Header ?? "", item.Gesture)) ?? [])
            .ToList() ?? [];
    /// <summary>A menu bar item done as if chosen: for the self-check.</summary>
    public bool ChooseInMenu(string header)
    {
        var item = NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>().SelectMany(menu => menu.Menu?.Items.OfType<NativeMenuItem>() ?? []).FirstOrDefault(x => x.Header == header);
        if (item is null) return false;
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
        return true;
    }
}
