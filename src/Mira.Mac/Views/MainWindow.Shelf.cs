using Avalonia.Controls;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « L'étagère », as on Windows (MainWindow.Shelf.cs there; CollectionShelf of Core): the collections grouped for this
// account, one card each in Films, Séries and Bibliothèque; « Regrouper dans la bibliothèque » in a collection's menu.
public sealed partial class MainWindow
{
    /// <summary>The collections grouped for a session's account, its choices, and when Jellyfin said (read again after <see cref="GroupingAge"/>).</summary>
    private (Session Session, CollectionShelf.Index Index, Dictionary<string, CollectionShelf.Choice> Choices, DateTimeOffset At)? _grouping;
    private static readonly TimeSpan GroupingAge = TimeSpan.FromMinutes(10);
    /// <summary>Read again at the next list (a title watched: the cards' progress), kept meanwhile should Jellyfin not answer.</summary>
    public void ForgetGrouping() { if (_grouping is { } known) _grouping = known with { At = DateTimeOffset.MinValue }; }

    /// <summary>The collections grouped for this account; none when Jellyfin does not say (the titles then show as they are).</summary>
    public async Task<CollectionShelf.Index> GroupingAsync(Session session)
    {
        if (_grouping is { } known && ReferenceEquals(known.Session, session) && DateTimeOffset.UtcNow - known.At < GroupingAge) return known.Index;
        try
        {
            var choices = await session.Client.ShelfChoicesAsync(session.Lifetime);
            var index = await session.Client.ShelvesAsync(choices, session.Lifetime);
            if (!ReferenceEquals(session, Session)) return index;
            _grouping = (session, index, choices, DateTimeOffset.UtcNow);
            TellGroupedOnce(index, choices);
            return index;
        }
        catch (Exception ex) when (ex is OperationCanceledException || Errors.Expected(ex))
        {
            return _grouping is { } kept && ReferenceEquals(kept.Session, session) ? kept.Index : CollectionShelf.Index.None;
        }
    }
    /// <summary>The first time sagas are grouped on their own: said once, with « Annuler », as on Windows.</summary>
    private void TellGroupedOnce(CollectionShelf.Index index, Dictionary<string, CollectionShelf.Choice> choices)
    {
        if (Settings.CollectionsGroupedTold || IsCheckRun) return;
        var automatic = index.Shelves.Where(x => !choices.ContainsKey(x.Box.Id)).ToList();
        if (automatic.Count == 0) return;
        Settings.CollectionsGroupedTold = true;
        try { Profile.SaveSettings(Settings); } catch (Exception ex) when (Errors.Expected(ex)) { }
        Notice(CollectionShelf.AutomaticNotice(automatic.Sum(x => x.Titles.Count), automatic.Count), L("Annuler", "Undo"), () => _ = UngroupAllAsync(automatic.Select(x => x.Box).ToList()), sticky: false);
    }
    private async Task UngroupAllAsync(List<MediaItem> boxes)
    {
        if (Session is not { } session) return;
        try { foreach (var box in boxes) await session.Client.SetShelfChoiceAsync(box.Id, x => x with { Grouped = false }, session.Lifetime); }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); }
        ForgetGrouping(); await RefreshPagesAsync();
    }
    /// <summary>Whether a collection's titles are one card in the library for this account (its choice, else its kind).</summary>
    public bool IsGrouped(MediaItem box)
    {
        if (_grouping is { } known)
        {
            if (known.Choices.TryGetValue(box.Id, out var choice) && choice.Grouped is { } chosen) return chosen;
            if (known.Index.Shelves.Any(x => x.Box.Id == box.Id)) return true;
        }
        return box.Shelf is not null || CollectionShelf.Automatic(box);
    }
    /// <summary>« Regrouper dans la bibliothèque », or « Ne plus regrouper », in a collection's menu.</summary>
    public void AddShelfToggle(ContextMenu menu, MediaItem box)
    {
        if (Session is null) return;
        var grouped = IsGrouped(box);
        menu.Items.Add(Ui.Entry(grouped ? L("Ne plus regrouper dans la bibliothèque", "Stop grouping in the library") : L("Regrouper dans la bibliothèque", "Group in the library"),
            grouped ? "minus" : "library", () => _ = SetGroupedAsync(box, !grouped, undoable: true)));
    }
    private async Task SetGroupedAsync(MediaItem box, bool grouped, bool undoable)
    {
        if (Session is not { } session) return;
        try { await session.Client.SetShelfChoiceAsync(box.Id, x => x with { Grouped = grouped }, session.Lifetime); }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(Errors.Friendly(ex), tone: NoticeTone.Error); return; }
        _grouping = null;
        var index = await GroupingAsync(session);
        var titles = index.Shelves.FirstOrDefault(x => x.Box.Id == box.Id)?.Titles.Count ?? box.Shelf?.Count ?? box.ChildCount ?? 0;
        var said = grouped ? CollectionShelf.GroupedNotice(titles, box.DisplayTitle)
            : L($"Les titres de « {box.DisplayTitle} » sont de nouveau dans la bibliothèque.", $"The titles of “{box.DisplayTitle}” are back in the library.");
        if (undoable) Notice(said, L("Annuler", "Undo"), () => _ = SetGroupedAsync(box, !grouped, undoable: false), sticky: false);
        else Notice(said);
        await ReloadListsAsync();
    }
    /// <summary>The page on screen with the new grouping; the lists kept (Films, Séries…) read it again when they come back.</summary>
    private Task ReloadListsAsync() => RefreshPagesAsync();
}
