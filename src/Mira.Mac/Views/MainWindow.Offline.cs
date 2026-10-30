using System.Net.Http;
using Avalonia.Controls;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Garder sur ce Mac », as Windows' « Garder sur ce PC » (MainWindow.Offline.cs there): films and episodes kept on this
/// Mac, Jellyfin's own file, to watch without a connection (on the train), and the « Sur ce Mac » page that lists them
/// (<see cref="OfflineCopies"/>). One file at a time, resumed where it stopped, also after Mira was closed.
/// </summary>
public sealed partial class MainWindow
{
    private List<OfflineCopy> _offline = [];
    private readonly Queue<string> _offlinePending = new();
    private (string Id, long Done, long Total)? _offlineProgress;
    private CancellationTokenSource? _offlineCancel;
    private bool _offlineRunning;
    /// <summary>Changes to the copies: the page « Sur ce Mac », the title page and the settings redraw what shows them.</summary>
    public event Action? OfflineUpdated;
    /// <summary>A few times a second while a file comes: only the words that say how far it is.</summary>
    public event Action? OfflineProgressed;
    private string DefaultOfflineFolder => Path.Combine(Profile.DirectoryPath, "offline");
    public string OfflineFolder => Settings.OfflineFolder is { Length: > 0 } chosen ? chosen : DefaultOfflineFolder;
    /// <summary>Every folder copies were put in: changing it leaves the earlier copies where they are, still listed.</summary>
    private List<string> OfflineRoots => [DefaultOfflineFolder, .. Settings.OfflineFolders, OfflineFolder];
    public IReadOnlyList<OfflineCopy> OfflineCopiesKept => _offline;
    public OfflineCopy? CopyOf(string id) => _offline.FirstOrDefault(x => x.Item.Id == id);
    /// <summary>A complete copy whose file is there (a disk unplugged: Jellyfin streams it again).</summary>
    public OfflineCopy? KeptCopy(string id) => CopyOf(id) is { Complete: true } copy && File.Exists(copy.VideoPath) ? copy : null;
    public static bool CanKeep(MediaItem item) => item.Type is "Movie" or "Episode" && item.ExtraType is null;

    /// <summary>The copies of the account just opened; those not finished go on.</summary>
    private async Task LoadOfflineAsync(Session session)
    {
        var connection = session.Connection; var roots = OfflineRoots;
        var copies = await Task.Run(() => OfflineCopies.List(roots, connection));
        if (!ReferenceEquals(session, Session)) return;
        _offline = copies; _offlinePending.Clear();
        foreach (var copy in copies.Where(x => !x.Complete).OrderBy(x => x.Added)) _offlinePending.Enqueue(copy.Item.Id);
        OfflineChanged(); PumpOffline();
    }
    /// <summary>Another account or none: this one's copies stay on the disk, out of sight.</summary>
    private void ForgetOfflineState()
    {
        _offlineCancel?.Cancel(); _offlinePending.Clear(); _offline = []; _offlineProgress = null;
        OfflineChanged();
    }

    /// <summary>Keeps these films or episodes on this Mac, after a look at the room they need.</summary>
    public async Task KeepOfflineAsync(IReadOnlyList<MediaItem> items)
    {
        if (Session is not { } session) return;
        var wanted = items.Where(x => CanKeep(x) && CopyOf(x.Id) is null).DistinctBy(x => x.Id).ToList();
        if (wanted.Count == 0) return;
        var folder = OfflineFolder; var sources = new List<(MediaItem Item, MediaSource Source)>();
        try
        {
            foreach (var item in wanted)
            {
                var full = item.MediaSources is { Count: > 0 } ? item : await session.Client.ItemAsync(item.Id);
                if (!ReferenceEquals(session, Session)) return;
                sources.Add((full, full.MediaSources?.FirstOrDefault() ?? throw new IOException(L("Jellyfin ne donne aucun fichier pour « " + item.DisplayTitle + " ».", "Jellyfin has no file for “" + item.DisplayTitle + "”."))));
            }
            var needed = sources.Sum(x => x.Source.Size ?? 0);
            var free = await Task.Run(() => { Directory.CreateDirectory(folder); return OfflineCopies.FreeSpace(folder); });
            if (free is { } room && needed > room - OfflineCopies.Reserve)
            {
                Notice(L($"Pas assez de place pour garder {(wanted.Count == 1 ? "ce titre" : $"ces {wanted.Count} épisodes")} sur ce Mac : il faut {OfflineCopies.SizeText(needed)}, il reste {OfflineCopies.SizeText(Math.Max(0, room))}. Libère de la place, ou choisis un autre dossier dans Réglages → Lecture → Sur ce Mac.", $"Not enough room to keep {(wanted.Count == 1 ? "this title" : $"these {wanted.Count} episodes")} on this Mac: it needs {OfflineCopies.SizeText(needed)}, {OfflineCopies.SizeText(Math.Max(0, room))} is left. Free up some space, or choose another folder in Settings → Playback → On this Mac."), L("Réglages", "Settings"), () => { SettingsPage.OpenAt("playback"); Go("settings"); });
                return;
            }
            var now = DateTimeOffset.Now; var connection = session.Connection;
            var planned = await Task.Run(() => sources.Select((x, i) => OfflineCopies.Plan(folder, connection, x.Item, x.Source, now.AddTicks(-i))).ToList());
            if (!ReferenceEquals(session, Session)) return;
            RememberOfflineFolder(folder);
            foreach (var copy in planned) { _offline.Insert(0, copy); _offlinePending.Enqueue(copy.Item.Id); }
            Notice(planned.Count == 1 ? L($"« {planned[0].Item.DisplayTitle} » arrive sur ce Mac. Tu le retrouveras dans « Sur ce Mac », dans la barre de gauche.", $"“{planned[0].Item.DisplayTitle}” is coming to this Mac. You’ll find it under “On this Mac” in the sidebar.") : L($"{planned.Count} épisodes arrivent sur ce Mac, l’un après l’autre. Tu les retrouveras dans « Sur ce Mac », dans la barre de gauche.", $"{planned.Count} episodes are coming to this Mac, one after the other. You’ll find them under “On this Mac” in the sidebar."));
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Notice(OfflineFailure(ex)); }
        OfflineChanged(); PumpOffline();
    }
    private void RememberOfflineFolder(string folder)
    {
        if (folder == DefaultOfflineFolder || Settings.OfflineFolders.Contains(folder)) return;
        Settings.OfflineFolders.Add(folder); Profile.SaveSettings(Settings);
    }
    /// <summary>Another folder for the next copies; those already kept stay where they are, still listed.</summary>
    public void ChooseOfflineFolder(string folder)
    {
        Settings.OfflineFolder = folder == DefaultOfflineFolder ? "" : folder;
        RememberOfflineFolder(OfflineFolder); Profile.SaveSettings(Settings); OfflineChanged();
    }
    /// <summary>Fetches the copies waiting, one at a time, while this account stays and Jellyfin answers.</summary>
    private async void PumpOffline()
    {
        if (_offlineRunning || Session is not { } session) return;
        _offlineRunning = true;
        try
        {
            while (_offlinePending.Count > 0 && ReferenceEquals(session, Session))
            {
                var id = _offlinePending.Peek();
                if (CopyOf(id) is not { Complete: false } copy) { _offlinePending.Dequeue(); continue; }
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
                _offlineCancel = cancel; _offlineProgress = (id, 0, copy.Size); OfflineChanged();
                var progress = new Progress<(long Done, long Total)>(x => { if (_offlineProgress?.Id == id) { _offlineProgress = (id, x.Done, x.Total); OfflineProgressed?.Invoke(); } });
                try
                {
                    var done = await Task.Run(() => OfflineCopies.FetchAsync(session.Client, copy, progress, cancel.Token));
                    if (ReferenceEquals(session, Session) && _offline.IndexOf(copy) is var index and >= 0) _offline[index] = done;
                    if (_offlinePending.TryPeek(out var head) && head == id) _offlinePending.Dequeue();
                }
                // « Retirer » while it came, or the account left: nothing to say.
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    if (_offlinePending.TryPeek(out var head) && head == id && CopyOf(id) is null) _offlinePending.Dequeue();
                    if (!ReferenceEquals(session, Session) || session.Lifetime.IsCancellationRequested) return;
                }
                catch (OfflineRefusedException ex)
                {
                    // The account may not download at all: the others would be refused the same way.
                    Notice(ex.Message);
                    var refused = _offline.Where(x => !x.Complete).ToList();
                    _offlinePending.Clear(); _offline.RemoveAll(x => !x.Complete);
                    await Task.Run(() => { foreach (var gone in refused) OfflineCopies.Delete(gone); });
                    return;
                }
                // Jellyfin out of reach, or the connection cut: it goes on once Jellyfin answers again (ResumeOffline).
                catch (Exception ex) when (ex is HttpIOException or OperationCanceledException or HttpRequestException { StatusCode: null }) { return; }
                // An answer that will not change by itself (an error of Jellyfin, a full disk): said, and this copy waits for a click.
                catch (Exception ex) when (Errors.Expected(ex))
                {
                    if (_offlinePending.TryPeek(out var head) && head == id) _offlinePending.Dequeue();
                    Notice(OfflineFailure(ex, copy));
                }
                finally { _offlineCancel = null; _offlineProgress = null; }
            }
        }
        finally { _offlineRunning = false; OfflineChanged(); }
    }
    private static string OfflineFailure(Exception ex, OfflineCopy? copy = null) => ex switch
    {
        OfflineRefusedException => ex.Message,
        // Mira's own words (« Pas assez de place… », « Jellyfin ne donne aucun fichier… »).
        IOException when ex.TargetSite?.DeclaringType?.Assembly is { } assembly && (assembly == typeof(JellyfinClient).Assembly || assembly == typeof(MainWindow).Assembly) => ex.Message,
        IOException or UnauthorizedAccessException => L($"Mira n’a pas pu écrire {(copy is null ? "le fichier" : $"« {copy.Item.DisplayTitle} »")} sur ce Mac : disque plein, débranché ou dossier en lecture seule. Vérifie le dossier dans Réglages → Lecture → Sur ce Mac.", $"Mira couldn’t write {(copy is null ? "the file" : $"“{copy.Item.DisplayTitle}”")} to this Mac: the disk is full or unplugged, or the folder is read-only. Check the folder in Settings → Playback → On this Mac."),
        HttpRequestException { StatusCode: { } } http => L($"{(copy is null ? "" : $"« {copy.Item.DisplayTitle} » n’a pas pu venir sur ce Mac. ")}{Errors.Friendly(http)}", $"{(copy is null ? "" : $"“{copy.Item.DisplayTitle}” couldn’t be copied to this Mac. ")}{Errors.Friendly(http)}"),
        _ => Errors.Friendly(ex)
    };
    /// <summary>Removes copies from this Mac; one still arriving stops first. Not the one playing.</summary>
    public async Task ForgetOfflineAsync(IReadOnlyList<OfflineCopy> copies)
    {
        if (Player?.Location is { } playing && copies.Any(x => x.VideoPath == playing)) { Notice(L("Ferme le lecteur avant de retirer de ce Mac le titre qu’il lit.", "Close the player before removing the title it’s playing from this Mac.")); return; }
        foreach (var copy in copies) _offline.Remove(copy);
        if (_offlineProgress is { } running && copies.Any(x => x.Item.Id == running.Id))
        {
            _offlineCancel?.Cancel();
            // The file of the stopped download closes as its task ends.
            for (var i = 0; i < 40 && _offlineProgress?.Id == running.Id; i++) await Task.Delay(50);
        }
        OfflineChanged();
        var left = await Task.Run(async () =>
        {
            var stayed = new List<OfflineCopy>();
            foreach (var copy in copies)
            {
                var deleted = false;
                for (var attempt = 0; attempt < 5 && !(deleted = OfflineCopies.Delete(copy)); attempt++) await Task.Delay(200);
                if (!deleted) stayed.Add(copy);
            }
            return stayed;
        });
        if (left.Count > 0) Notice(L("Un fichier gardé sur ce Mac est encore utilisé par un autre programme : Mira n’a pas pu le retirer. Réessaie après l’avoir fermé.", "A file kept on this Mac is still in use by another program: Mira couldn’t remove it. Try again once it’s closed."));
        PumpOffline();
    }
    public void ResumeOffline(IEnumerable<OfflineCopy> copies)
    {
        foreach (var copy in copies) if (!_offlinePending.Contains(copy.Item.Id)) _offlinePending.Enqueue(copy.Item.Id);
        OfflineChanged(); PumpOffline();
    }
    /// <summary>« Tout retirer » of the settings, asked first: the copies stay on Jellyfin.</summary>
    public void AskForgetAllOffline()
    {
        if (_offline.Count == 0) return;
        var all = _offline.ToList();
        Notice(L($"Retirer de ce Mac les {all.Count} titre{(all.Count > 1 ? "s" : "")} gardé{(all.Count > 1 ? "s" : "")} ({OfflineCopies.SizeText(all.Sum(x => x.BytesOnDisk))}) ? Ils restent sur Jellyfin.", $"Remove {(all.Count == 1 ? "the title" : $"the {all.Count} titles")} kept on this Mac ({OfflineCopies.SizeText(all.Sum(x => x.BytesOnDisk))})? {(all.Count == 1 ? "It stays" : "They stay")} on Jellyfin."), L("Retirer", "Remove"), () => _ = ForgetOfflineAsync(all));
    }

    private void OfflineChanged()
    {
        if (_railButtons.TryGetValue("offline", out var rail)) rail.IsVisible = _offline.Count > 0 && Session is not null;
        OfflineUpdated?.Invoke();
    }
    /// <summary>« Sur ce Mac », « 42 % », « En attente », « En pause »; null when not kept.</summary>
    private static string Paused => L("En pause", "Paused");
    public string? CopyState(string id)
    {
        if (CopyOf(id) is not { } copy) return null;
        if (copy.Complete) return L("Sur ce Mac", "On this Mac");
        if (_offlineProgress is { } running && running.Id == id) return running.Total > 0 ? L($"{Math.Min(99, running.Done * 100 / running.Total)} %", $"{Math.Min(99, running.Done * 100 / running.Total)}%") : L("Arrive…", "Copying…");
        return _offlinePending.Contains(id) ? L("En attente", "Waiting") : Paused;
    }
    public bool CopyPaused(string id) => CopyOf(id) is { Complete: false } && CopyState(id) == Paused;
    public string OfflineSummary()
    {
        var kept = _offline.Count(x => x.Complete);
        var parts = new List<string> { kept == 0 ? L("Rien de prêt pour l’instant", "Nothing ready yet") : L($"{kept} titre{(kept > 1 ? "s" : "")} à regarder sans connexion · {OfflineCopies.SizeText(_offline.Where(x => x.Complete).Sum(x => x.Size))}", $"{kept} {(kept == 1 ? "title" : "titles")} to watch offline · {OfflineCopies.SizeText(_offline.Where(x => x.Complete).Sum(x => x.Size))}") };
        if (_offlineProgress is { } running && CopyOf(running.Id) is { } copy) parts.Add(L($"« {copy.Item.DisplayTitle}{(copy.Item.Type == "Episode" ? $" S{copy.Item.ParentIndexNumber ?? 1:00} E{copy.Item.IndexNumber ?? 1:00}" : "")} » arrive : {CopyState(running.Id)}", $"Copying “{copy.Item.DisplayTitle}{(copy.Item.Type == "Episode" ? $" S{copy.Item.ParentIndexNumber ?? 1:00} E{copy.Item.IndexNumber ?? 1:00}" : "")}”: {CopyState(running.Id)}"));
        var waiting = _offlinePending.Count(x => x != _offlineProgress?.Id);
        if (waiting > 0) parts.Add(L($"{waiting} en attente", $"{waiting} waiting"));
        return string.Join("  ·  ", parts);
    }
    /// <summary>The entry of a card's or an episode's menu: keep it, resume it, or remove it.</summary>
    private MenuItem? OfflineMenuItem(MediaItem item)
    {
        if (!CanKeep(item) || Session is null) return null;
        MenuItem Entry(string label, Action action) { var entry = new MenuItem { Header = label }; entry.Click += (_, _) => action(); return entry; }
        if (CopyOf(item.Id) is not { } copy) return Entry(L("Garder sur ce Mac", "Keep on this Mac"), () => _ = KeepOfflineAsync([item]));
        if (CopyPaused(item.Id)) return Entry(L("Reprendre l’arrivée sur ce Mac", "Resume copying to this Mac"), () => ResumeOffline([copy]));
        return Entry(copy.Complete ? L($"Retirer de ce Mac · {OfflineCopies.SizeText(copy.Size)}", $"Remove from this Mac · {OfflineCopies.SizeText(copy.Size)}") : L("Arrêter et retirer de ce Mac", "Stop and remove from this Mac"), () => _ = ForgetOfflineAsync([copy]));
    }
}
