using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Services;

namespace Mira.Desktop;

// « Remplacer par une meilleure version » (TitleUpgrade), as Mira web and Mac ask for it: a film's or an episode's file
// replaced, never a second version kept beside. On the PC of Jellyfin, this Mira notes the download before TorLink gets
// it (never placed as a new title), checks it once finished (one video, readable by Jellyfin's ffprobe, as long as the
// old one), puts it in the old file's place with the old one in the Recycle Bin (TorLinkImporter.ReplaceAsync), waiting
// while anyone watches the title, then copies the old title's state onto the new one once Jellyfin has it. Elsewhere,
// the Downloads page asks the PC of Jellyfin for it, through the relay (MainWindow.ServerDownloads.cs).
public partial class MainWindow
{
    private bool _replacing, _finishingReplacements;
    /// <summary>Jellyfin's ffprobe, found at the first replacement.</summary>
    private string? _ffprobe;
    /// <summary>The title the Downloads page looks for a better version of, and whether one was asked for already.</summary>
    private MediaItem? _upgrade;
    private bool _upgradeSent;

    /// <summary>The account administers Jellyfin, as last asked (<see cref="IsAdministratorAsync"/>).</summary>
    private bool Administers => _adminOf is { } known && ReferenceEquals(known.Client, _client) && known.Admin;

    // —— On the PC of Jellyfin ——

    /// <summary>Why this download cannot replace the title's file, or null once it is noted: TorLink may get it then.</summary>
    private async Task<string?> ExpectReplacementAsync(string hash, string itemId, CancellationToken ct)
    {
        if (_demo || _client is not { } client || _torlink is not { } torlink) return L("Mira n’est pas prêt sur le PC serveur.", "Mira isn’t ready on the server PC.");
        if (!RemoteDownloads.IsItemId(itemId)) return L("Demande inconnue.", "Unknown request.");
        var importer = torlink.Importer;
        // Only a replacement that failed may be asked for again.
        if (importer.Find(hash) is { } placed && placed is not { Replaced: not null, State: TorLinkImportState.Failed }
            || importer.Replacement(hash) is { State: not (ReplacementState.Done or ReplacementState.Failed) })
            return L("Ce téléchargement est déjà passé par ta bibliothèque.", "This download already went through your library.");
        if (importer.Replacements.Any(x => string.Equals(x.ItemId, itemId, StringComparison.OrdinalIgnoreCase) && x.State is ReplacementState.Waiting or ReplacementState.Swapping))
            return L("Une meilleure version de ce titre est déjà en route.", "A better version of this title is already on its way.");
        try
        {
            var item = await client.ItemAsync(itemId, ct);
            var libraries = (await client.VirtualFoldersAsync(ct) ?? []).SelectMany(x => x.Locations).ToList();
            if (TitleUpgrade.Problem(item, libraries) is { } problem) return problem;
            var path = TitleUpgrade.FileOf(item)!;
            if (!File.Exists(path)) return L("Le fichier de ce titre est introuvable sur ce PC.", "This title’s file can’t be found on this PC.");
            // A removable or network drive has no Recycle Bin: the old file would be lost for good.
            if (Path.GetPathRoot(path) is not { Length: > 0 } root || new DriveInfo(root).DriveType != DriveType.Fixed)
                return L("Ce titre est sur un disque sans Corbeille (amovible ou réseau) : Mira ne remplace pas son fichier.", "This title is on a drive without a Recycle Bin (removable or network): Mira doesn’t replace its file.");
            _ffprobe ??= FfProbe.Program(await client.EncoderPathAsync(ct));
            if (_ffprobe is null) return L("L’ffprobe de Jellyfin est introuvable sur ce PC : Mira ne pourrait pas vérifier la nouvelle version.", "Jellyfin’s ffprobe can’t be found on this PC: Mira couldn’t check the new version.");
            importer.ExpectReplacement(new TorLinkReplacement { Id = hash, ItemId = item.Id, Title = TitleUpgrade.Title(item), OldPath = path, Runtime = item.RunTimeTicks ?? 0 });
            return null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { return L("Ce titre n’est plus dans Jellyfin.", "This title is no longer in Jellyfin."); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return Friendly(ex); }
    }

    /// <summary>The replacements TorLink finished: checked and swapped one at a time, each tried again later while its title is watched.</summary>
    private async Task ReplaceDueAsync()
    {
        if (_replacing || _demo || _client is not { } client || _torlink is not { } torlink) return;
        _replacing = true;
        try
        {
            var (importer, state, ct) = (torlink.Importer, torlink.State, _torlinkLifetime.Token);
            var due = (await Task.Run(() => importer.Due(state), ct)).Where(x => x.RetryAt is not { } at || at <= DateTimeOffset.UtcNow).ToList();
            if (due.Count == 0) return;
            _ffprobe ??= FfProbe.Program(await client.EncoderPathAsync(ct));
            var mode = _settings.TorLinkKeepShared ? ImportMode.KeepSeeding : ImportMode.Move;
            var owner = new WindowInteropHelper(this).Handle;
            var probe = _ffprobe;
            foreach (var replacement in due)
            {
                var result = await Task.Run(() => importer.ReplaceAsync(replacement.Id, state, mode, (x, token) => CheckReplacementAsync(client, x, token),
                    (file, token) => probe is null ? Task.FromResult<TimeSpan?>(null) : FfProbe.LengthAsync(probe, file, token),
                    // The Shell on the window's thread: where Windows cannot keep the file, it asks there first.
                    file => Dispatcher.Invoke(() => RecycleBin.Send(file, owner, quiet: true)), ct), ct);
                if (result is { State: ReplacementState.Swapped, NewPath: { } placed })
                    await client.ReportMediaChangedAsync([placed], SamePlace(result) ? [] : [result.OldPath], ct);
            }
        }
        catch (Exception ex) when (ExpectedErrors.Are(ex)) { }
        finally { _replacing = false; }
    }

    /// <summary>
    /// Just before the files change: the title still there with the same file, nobody watching it, and its state for
    /// each account and its collections, kept to copy onto the new title.
    /// </summary>
    private static async Task<ReplacementCheck> CheckReplacementAsync(JellyfinClient client, TorLinkReplacement replacement, CancellationToken ct)
    {
        MediaItem item;
        try { item = await client.ItemAsync(replacement.ItemId, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return new(null, Problem: L("Ce titre n’est plus dans Jellyfin : rien n’a été remplacé.", "This title is no longer in Jellyfin: nothing was replaced."));
        }
        if (!string.Equals(TitleUpgrade.FileOf(item), replacement.OldPath, StringComparison.OrdinalIgnoreCase))
            return new(null, Problem: L("Le fichier de ce titre a changé depuis la demande : rien n’a été remplacé.", "This title’s file changed since it was asked for: nothing was replaced."));
        if ((await client.PlayingIdsAsync(ct)).Contains(item.Id))
            return new(null, Wait: L("Quelqu’un regarde ce titre : il sera remplacé une fois la lecture finie.", "Someone is watching this title: it’ll be replaced once they’re done."));
        var saved = new List<SavedUserData>();
        foreach (var user in await client.UsersAsync(ct) ?? [])
            if (await client.UserDataOfAsync(item.Id, user.Id, ct) is { Kept: true } data) saved.Add(data);
        var collections = await client.CollectionsOfAsync(item.Id, ct);
        return new(new TitleSnapshot(saved, collections?.ToList() ?? []));
    }

    /// <summary>The new file under the old one's name: Jellyfin keeps the same title, and everything that goes with it.</summary>
    private static bool SamePlace(TorLinkReplacement replacement) => string.Equals(replacement.NewPath, replacement.OldPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Once Jellyfin has the new file: the same title read again (same name), or the old title's state copied onto the
    /// new one (another extension, so another title for Jellyfin); given up after a day.
    /// </summary>
    private async Task FinishReplacementsAsync()
    {
        if (_finishingReplacements || _demo || _client is not { } client || _torlink is not { } torlink) return;
        var importer = torlink.Importer;
        var swapped = importer.Replacements.Where(x => x is { State: ReplacementState.Swapped, NewPath: not null }).ToList();
        if (swapped.Count == 0) return;
        _finishingReplacements = true;
        var ct = _torlinkLifetime.Token;
        try
        {
            var moved = swapped.Where(x => !SamePlace(x)).Select(x => x.NewPath!).ToList();
            var found = moved.Count == 0 ? [] : await client.FindIndexedAsync(moved, ct, itself: true);
            foreach (var replacement in swapped)
            {
                if (SamePlace(replacement))
                {
                    await client.RefreshMetadataAsync(replacement.ItemId, ct);
                    importer.UpdateReplacement(replacement with { State = ReplacementState.Done, NewItemId = replacement.ItemId });
                    continue;
                }
                if (found.GetValueOrDefault(replacement.NewPath!) is not { } id)
                {
                    if (DateTimeOffset.UtcNow - replacement.UpdatedAt > TimeSpan.FromDays(1))
                        importer.UpdateReplacement(replacement with { State = ReplacementState.Done, Message = L("Jellyfin n’a pas trouvé la nouvelle version en un jour : son état de lecture n’a pas été recopié.", "Jellyfin didn’t find the new version within a day: its watch state wasn’t copied.") });
                    continue;
                }
                var missed = 0;
                foreach (var data in replacement.Saved?.Users ?? [])
                {
                    try { await client.SetUserDataAsync(id, data, ct); }
                    catch (Exception ex) when (ExpectedErrors.Are(ex) && ex is not OperationCanceledException) { missed++; }
                }
                foreach (var collection in replacement.Saved?.Collections ?? [])
                {
                    try { await client.SetInCollectionAsync(collection, [id], true, ct); }
                    catch (Exception ex) when (ExpectedErrors.Are(ex) && ex is not OperationCanceledException) { missed++; }
                }
                importer.UpdateReplacement(replacement with
                {
                    State = ReplacementState.Done,
                    NewItemId = id,
                    Message = missed > 0 ? L("Une partie de l’état de lecture n’a pas pu être recopiée.", "Part of the watch state couldn’t be copied.") : null
                });
            }
        }
        catch (Exception ex) when (ExpectedErrors.Are(ex)) { }
        finally { _finishingReplacements = false; }
    }

    // —— « Chercher une meilleure version », from a title's menu ——

    /// <summary>The menu's entry of a film or an episode, for an administrator, where TorLink is offered.</summary>
    private MenuItem? UpgradeMenuItem(MediaItem item) => _torlinkEnabled && !_demo && item.Type is "Movie" or "Episode"
        ? MenuAction(L("Chercher une meilleure version", "Find a better version"), "sparkle", () => ShowUpgradeAsync(item)) : null;

    /// <summary>The Downloads page, its search made for this title, its results only those better than its file.</summary>
    private async Task ShowUpgradeAsync(MediaItem title)
    {
        if (_client is not { } client) return;
        MediaItem item;
        // The single-item request: its file and its picture.
        try { item = await client.ItemAsync(title.Id, _connectionLifetime.Token); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); return; }
        _upgrade = item; _upgradeSent = false;
        await ShowTorLinkAsync();
        if (!_serverScreen)
        {
            EndUpgrade();
            SetNotice(L("Choisis l’écran de Mira dans Téléchargements pour chercher une meilleure version.", "Choose Mira’s screen in Downloads to find a better version."));
            return;
        }
        DescribeUpgrade();
        ServerQueryBox.Text = TitleUpgrade.Query(item);
        await SearchServerAsync();
    }

    private void DescribeUpgrade()
    {
        if (_upgrade is not { } item) { ServerUpgradeBanner.Visibility = Visibility.Collapsed; return; }
        ServerUpgradeTitle.Text = L($"Meilleure version de « {TitleUpgrade.Title(item)} »", $"Better version of “{TitleUpgrade.Title(item)}”");
        ServerUpgradeLine.Text = L($"Actuellement : {TitleUpgrade.Line(TitleUpgrade.Current(item))}. Seules les versions meilleures sont montrées. Une fois téléchargée et vérifiée (même durée, lisible par Jellyfin), la nouvelle prend la place de l’ancienne, qui part à la Corbeille du PC serveur.",
            $"Currently: {TitleUpgrade.Line(TitleUpgrade.Current(item))}. Only better versions are shown. Once downloaded and checked (same length, readable by Jellyfin), the new one takes the old one’s place, which goes to the server PC’s Recycle Bin.");
        ServerUpgradeBanner.Visibility = Visibility.Visible;
    }

    /// <summary>Back to the page as it is: every result, downloaded as a new title.</summary>
    private void EndUpgrade()
    {
        if (_upgrade is null) return;
        _upgrade = null; _upgradeSent = false;
        ServerUpgradeBanner.Visibility = Visibility.Collapsed;
    }

    private void ServerUpgradeClose_Click(object sender, RoutedEventArgs e)
    {
        EndUpgrade();
        ServerResults.Children.Clear(); ServerResultsEmpty.Visibility = Visibility.Visible; ServerSearchStatus.Text = "";
        ServerQueryBox.Clear(); ServerQueryBox.Focus();
    }
}
