using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// The « … » of a title's page (with « Identifier… » of an administrator, MainWindow.Identify.cs), and there the files of
/// a film or series, on the PC of Jellyfin only (elsewhere they are not on this computer): « Afficher dans
/// l'explorateur », and « Mettre à la Corbeille » for an administrator, asked once more, never for good (the Recycle Bin
/// keeps it), then said to Jellyfin, which takes it out of the library (<see cref="TitleFiles"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>What the « … » of the page holds: the title's files, on the PC of Jellyfin; « Identifier… », for an administrator.</summary>
    private (string Id, bool Files, bool Admin) _titleMore;
    /// <summary>
    /// The button beside the others of the page, once its title's place is known: for its files when Jellyfin is on
    /// this PC, for « Identifier… » and « Actualiser les métadonnées » when this account administers Jellyfin.
    /// </summary>
    private async Task ShowFilesButtonAsync(MediaItem item)
    {
        FilesButton.Visibility = Visibility.Collapsed; _titleMore = (item.Id, false, false);
        if (_demo || _client is not { } client) return;
        bool files = false, admin = false;
        try { files = TitleFiles.Location(item) is not null && await JellyfinOnThisPcAsync(client); }
        catch (Exception ex) when (IsExpected(ex)) { }
        try { admin = item.Type is "Movie" or "Series" && await IsAdministratorAsync(client); }
        catch (Exception ex) when (IsExpected(ex)) { }
        if (_detail?.Id != item.Id || !(files || admin)) return;
        _titleMore = (item.Id, files, admin);
        FilesButton.Visibility = Visibility.Visible;
    }

    private void Files_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } item || _titleMore.Id != item.Id) return;
        var menu = new ContextMenu { PlacementTarget = FilesButton, Placement = PlacementMode.Bottom };
        if (_titleMore.Admin) { AddIdentifyActions(menu, item); if (UpgradeMenuItem(item) is { } upgrade) menu.Items.Add(upgrade); }
        if (_titleMore.Files && TitleFiles.Location(item) is { } location)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction(L("Afficher dans l’explorateur", "Show in File Explorer"), "folder", () => { ShowInExplorer(item, location); return Task.CompletedTask; }));
            menu.Items.Add(MenuAction(L("Mettre à la Corbeille…", "Move to the Recycle Bin…"), "trash", () => AskToRecycleAsync(item)));
        }
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    /// <summary>A series' folder opened; a film's file (or disc folder) selected in its folder.</summary>
    private void ShowInExplorer(MediaItem item, string location)
    {
        // By its full path: never an explorer.exe put beside Mira, whose folder anyone may write to.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var series = item.Type == "Series";
        if (!File.Exists(location) && !Directory.Exists(location)) { SetNotice(L($"« {item.DisplayTitle} » n’est plus à sa place sur ce PC : {location}", $"“{item.DisplayTitle}” is no longer where it was on this PC: {location}")); return; }
        try { using var _ = Process.Start(new ProcessStartInfo(explorer, series ? $"\"{location}\"" : $"/select,\"{location}\"")); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { SetNotice(L($"L’explorateur ne s’est pas ouvert. Le titre est ici : {location}", $"File Explorer didn’t open. The title is here: {location}")); }
    }

    /// <summary>What goes, with its size and where it is, asked in the notice; nothing while someone watches it.</summary>
    private async Task AskToRecycleAsync(MediaItem item)
    {
        if (_client is not { } client) return;
        var ct = _connectionLifetime.Token;
        try
        {
            if (await client.IsAdministratorAsync(ct) != true || await client.VirtualFoldersAsync(ct) is not { } libraries)
            { SetNotice(L("Seul un compte administrateur de Jellyfin peut mettre un titre à la Corbeille depuis Mira.", "Only a Jellyfin administrator account can move a title to the Recycle Bin from Mira.")); return; }
            var folders = libraries.Where(x => !LibrarySection.Groups(x.CollectionType)).SelectMany(x => x.Locations).ToList();
            var (target, size) = await Task.Run(() => TitleFiles.ToRemove(item, folders) is { } found ? (found, TitleFiles.Size(found)) : (null, 0L), ct);
            if (target is null) { SetNotice(L($"Mira ne met à la Corbeille que ce qui est dans un dossier de tes bibliothèques, et « {item.DisplayTitle} » n’y est pas (ou plus).", $"Mira only moves to the Recycle Bin what’s in one of your library folders, and “{item.DisplayTitle}” isn’t (or no longer is) there.")); return; }
            var watching = (await client.ViewersAsync(ct)).FirstOrDefault(x => item.Type == "Series" ? x.Series == item.Name : x.Type == "Movie" && x.Name == item.Name);
            if (watching is not null) { SetNotice(L($"« {item.DisplayTitle} » est en lecture sur {watching.Device} ({watching.User}) : réessaie une fois la lecture finie.", $"“{item.DisplayTitle}” is playing on {watching.Device} ({watching.User}): try again once it’s over.")); return; }
            var what = item.Type == "Series" ? L("le dossier de la série, avec tous ses épisodes", "the show’s folder, with all its episodes")
                : Directory.Exists(target) ? L("le dossier du film", "the movie’s folder") : L("le fichier du film", "the movie’s file");
            SetNotice(L($"Mettre « {item.DisplayTitle} » à la Corbeille ? Part {what} : {target} · {OfflineCopies.SizeText(size)}. Tu pourras le remettre depuis la Corbeille de Windows.",
                $"Move “{item.DisplayTitle}” to the Recycle Bin? This takes {what}: {target} · {OfflineCopies.SizeText(size)}. You can put it back from the Windows Recycle Bin."),
                L("Mettre à la Corbeille", "Move to Recycle Bin"), () => _ = RecycleAsync(client, item, target), sticky: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }

    private async Task RecycleAsync(JellyfinClient client, MediaItem item, string target)
    {
        if (!ReferenceEquals(client, _client)) return;
        if (!RecycleBin.Send(target, new WindowInteropHelper(this).Handle))
        {
            SetNotice(File.Exists(target) || Directory.Exists(target)
                ? L($"« {item.DisplayTitle} » n’a pas été mis à la Corbeille (refusé, annulé ou un fichier encore ouvert) : rien ou une partie seulement est partie.", $"“{item.DisplayTitle}” wasn’t moved to the Recycle Bin (refused, cancelled or a file still open): nothing or only part of it went.")
                : L($"« {item.DisplayTitle} » n’était plus à sa place.", $"“{item.DisplayTitle}” was no longer there."));
            return;
        }
        // Jellyfin's own folder monitor would see it too; said at once all the same, as a deletion.
        await client.ReportMediaChangedAsync([], [target]);
        _catalog.Forget(item.Id); RenderLibrary();
        _detailTrail.Forget(item.Id);
        if (_detail?.Id == item.Id && DetailOverlay.Visibility == Visibility.Visible) await BackFromDetailsAsync();
        SetNotice(L($"« {item.DisplayTitle} » est dans la Corbeille. Jellyfin le retire de la bibliothèque dans une minute environ.", $"“{item.DisplayTitle}” is in the Recycle Bin. Jellyfin removes it from the library in about a minute."));
    }
}
