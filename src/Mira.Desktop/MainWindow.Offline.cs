using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// « Garder sur ce PC »: films and episodes kept on this PC, Jellyfin's own file, to watch without a connection (on the
/// train), and the « Sur ce PC » page that lists them (<see cref="OfflineCopies"/>). One file at a time, resumed where it
/// stopped, also after Mira was closed.
/// </summary>
public partial class MainWindow
{
    private List<OfflineCopy> _offline = [];
    private readonly Queue<string> _offlinePending = new();
    private (string Id, long Done, long Total)? _offlineProgress;
    private CancellationTokenSource? _offlineCancel;
    private bool _offlineRunning;
    /// <summary>The copy the player opened, for its subtitle files (FileLoaded).</summary>
    private OfflineCopy? _playingCopy;
    /// <summary>The episode rows' « Sur ce PC » line, updated as a copy progresses without drawing the list again.</summary>
    private readonly Dictionary<string, TextBlock> _episodeCopyLabels = [];
    private string DefaultOfflineFolder => Path.Combine(_profile.DirectoryPath, "offline");
    private string OfflineFolder => _settings.OfflineFolder is { Length: > 0 } chosen ? chosen : DefaultOfflineFolder;
    /// <summary>Every folder copies were put in: changing it leaves the earlier copies where they are, still listed.</summary>
    private List<string> OfflineRoots => [DefaultOfflineFolder, .. _settings.OfflineFolders, OfflineFolder];
    private OfflineCopy? CopyOf(string id) => _offline.FirstOrDefault(x => x.Item.Id == id);
    /// <summary>A complete copy whose file is there (a USB drive unplugged: Jellyfin streams it again).</summary>
    private OfflineCopy? KeptCopy(string id) => CopyOf(id) is { Complete: true } copy && File.Exists(copy.VideoPath) ? copy : null;
    private static bool CanKeep(MediaItem item) => item.Type is "Movie" or "Episode" && item.ExtraType is null;

    /// <summary>The copies of the account just opened; those not finished go on.</summary>
    private async Task LoadOfflineAsync(JellyfinClient client)
    {
        var connection = client.Connection; var roots = OfflineRoots;
        var copies = await Task.Run(() => OfflineCopies.List(roots, connection));
        if (!ReferenceEquals(client, _client)) return;
        _offline = copies; _offlinePending.Clear();
        foreach (var copy in copies.Where(x => !x.Complete).OrderBy(x => x.Added)) _offlinePending.Enqueue(copy.Item.Id);
        OfflineChanged(); PumpOffline();
    }
    /// <summary>Another account, the demo or none: this one's copies stay on the disk, out of sight.</summary>
    private void ForgetOfflineState()
    {
        _offlineCancel?.Cancel(); _offlinePending.Clear(); _offline = []; _offlineProgress = null; _playingCopy = null;
        OfflineChanged();
    }

    /// <summary>Keeps these films or episodes on this PC, after a look at the room they need.</summary>
    private async Task KeepOfflineAsync(IReadOnlyList<MediaItem> items)
    {
        if (_demo || _client is not { } client) return;
        var wanted = items.Where(x => CanKeep(x) && CopyOf(x.Id) is null).DistinctBy(x => x.Id).ToList();
        if (wanted.Count == 0) return;
        var folder = OfflineFolder; var sources = new List<(MediaItem Item, MediaSource Source)>();
        try
        {
            foreach (var item in wanted)
            {
                var full = item.MediaSources is { Count: > 0 } ? item : await client.ItemAsync(item.Id);
                if (!ReferenceEquals(client, _client)) return;
                sources.Add((full, full.MediaSources?.FirstOrDefault() ?? throw new IOException("Jellyfin ne donne aucun fichier pour « " + item.DisplayTitle + " ».")));
            }
            var needed = sources.Sum(x => x.Source.Size ?? 0);
            var free = await Task.Run(() => { Directory.CreateDirectory(folder); return OfflineCopies.FreeSpace(folder); });
            if (free is { } room && needed > room - OfflineCopies.Reserve)
            {
                SetNotice($"Pas assez de place pour garder {(wanted.Count == 1 ? "ce titre" : $"ces {wanted.Count} épisodes")} sur ce PC : il faut {OfflineCopies.SizeText(needed)}, il reste {OfflineCopies.SizeText(Math.Max(0, room))}. Libère de la place, ou choisis un autre dossier dans Réglages → Lecture.", "Réglages", () => Settings_Click(this, new()));
                return;
            }
            var now = DateTimeOffset.Now; var connection = client.Connection;
            var planned = await Task.Run(() => sources.Select((x, i) => OfflineCopies.Plan(folder, connection, x.Item, x.Source, now.AddTicks(-i))).ToList());
            if (!ReferenceEquals(client, _client)) return;
            RememberOfflineFolder(folder);
            foreach (var copy in planned) { _offline.Insert(0, copy); _offlinePending.Enqueue(copy.Item.Id); }
            SetNotice(planned.Count == 1 ? $"« {planned[0].Item.DisplayTitle} » arrive sur ce PC. Tu le retrouveras dans « Sur ce PC », dans la barre de gauche." : $"{planned.Count} épisodes arrivent sur ce PC, l’un après l’autre. Tu les retrouveras dans « Sur ce PC », dans la barre de gauche.", null, null, sticky: false);
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(OfflineFailure(ex)); }
        OfflineChanged(); PumpOffline();
    }
    private void RememberOfflineFolder(string folder)
    {
        if (folder == DefaultOfflineFolder || _settings.OfflineFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        _settings.OfflineFolders.Add(folder); TrySaveSettings();
    }
    /// <summary>Fetches the copies waiting, one at a time, while this account stays and Jellyfin answers.</summary>
    private async void PumpOffline()
    {
        if (_offlineRunning || _demo || _client is not { } client) return;
        _offlineRunning = true;
        try
        {
            while (_offlinePending.Count > 0 && ReferenceEquals(client, _client))
            {
                var id = _offlinePending.Peek();
                if (CopyOf(id) is not { Complete: false } copy) { _offlinePending.Dequeue(); continue; }
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_connectionLifetime.Token);
                _offlineCancel = cancel; _offlineProgress = (id, 0, copy.Size); OfflineChanged();
                var progress = new Progress<(long Done, long Total)>(x => { if (_offlineProgress?.Id == id) { _offlineProgress = (id, x.Done, x.Total); OfflineProgressed(); } });
                try
                {
                    var done = await Task.Run(() => OfflineCopies.FetchAsync(client, copy, progress, cancel.Token));
                    if (ReferenceEquals(client, _client) && _offline.IndexOf(copy) is var index and >= 0) _offline[index] = done;
                    if (_offlinePending.TryPeek(out var head) && head == id) _offlinePending.Dequeue();
                }
                // « Retirer » while it came, or the account left: nothing to say.
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    if (_offlinePending.TryPeek(out var head) && head == id && CopyOf(id) is null) _offlinePending.Dequeue();
                    if (!ReferenceEquals(client, _client) || _connectionLifetime.IsCancellationRequested) return;
                }
                catch (OfflineRefusedException ex)
                {
                    // The account may not download at all: the others would be refused the same way.
                    SetNotice(ex.Message, null, null, sticky: true);
                    var refused = _offline.Where(x => !x.Complete).ToList();
                    _offlinePending.Clear(); _offline.RemoveAll(x => !x.Complete);
                    await Task.Run(() => { foreach (var copy2 in refused) OfflineCopies.Delete(copy2); });
                    return;
                }
                // Jellyfin out of reach, or the connection cut: it goes on once Jellyfin answers again (RefreshAsync).
                catch (Exception ex) when (ex is HttpIOException or OperationCanceledException or HttpRequestException { StatusCode: null }) { return; }
                // An answer that will not change by itself (an error of Jellyfin, a full disk): said, and this copy waits for a click.
                catch (Exception ex) when (IsExpected(ex))
                {
                    if (_offlinePending.TryPeek(out var head) && head == id) _offlinePending.Dequeue();
                    SetNotice(OfflineFailure(ex, copy));
                }
                finally { _offlineCancel = null; _offlineProgress = null; }
            }
        }
        finally { _offlineRunning = false; OfflineChanged(); }
    }
    private static string OfflineFailure(Exception ex, OfflineCopy? copy = null) => ex switch
    {
        OfflineRefusedException => ex.Message,
        IOException when FromMira(ex) => ex.Message,
        IOException or UnauthorizedAccessException => $"Mira n’a pas pu écrire {(copy is null ? "le fichier" : $"« {copy.Item.DisplayTitle} »")} sur ce PC : disque plein, débranché ou dossier en lecture seule. Vérifie le dossier dans Réglages → Lecture.",
        HttpRequestException { StatusCode: { } } http => $"{(copy is null ? "" : $"« {copy.Item.DisplayTitle} » n’a pas pu venir sur ce PC. ")}{Friendly(http)}",
        _ => Friendly(ex)
    };
    /// <summary>Removes copies from this PC; one still arriving stops first. Not the one playing.</summary>
    private async Task ForgetOfflineAsync(IReadOnlyList<OfflineCopy> copies)
    {
        if (_playing && copies.Any(x => _playLocation == x.VideoPath)) { SetNotice("Ferme le lecteur avant de retirer de ce PC le titre qu’il lit."); return; }
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
        if (left.Count > 0) SetNotice("Un fichier gardé sur ce PC est encore utilisé par un autre programme : Mira n’a pas pu le retirer. Réessaie après l’avoir fermé.");
        PumpOffline();
    }

    /// <summary>Whatever shows the copies: the rail, the page « Sur ce PC », the title page and its episodes.</summary>
    private void OfflineChanged()
    {
        OfflineNav.Visibility = _offline.Count > 0 && !_demo ? Visibility.Visible : Visibility.Collapsed;
        if (_catalog.View == "offline" && LibraryShell.Visibility == Visibility.Visible && DetailOverlay.Visibility != Visibility.Visible) _ = ShowOfflinePageAsync();
        if (DetailOverlay.Visibility == Visibility.Visible && _detail is { } detail) { ShowOfflineButton(); if (detail.Type == "Series" && _episodes.Count > 0) RenderEpisodes(); }
        if (SettingsOverlay.Visibility == Visibility.Visible) ShowOfflineSettings();
    }
    /// <summary>A few times a second while a file comes: only the words that say how far it is.</summary>
    private void OfflineProgressed()
    {
        if (_catalog.View == "offline" && LibraryShell.Visibility == Visibility.Visible) CatalogDescription.Text = OfflineSummary();
        if (DetailOverlay.Visibility == Visibility.Visible) ShowOfflineButton();
        foreach (var (id, label) in _episodeCopyLabels) label.Text = CopyState(id) ?? "";
    }
    /// <summary>« Sur ce PC », « 42 % », « En attente », « En pause »; null when not kept.</summary>
    private string? CopyState(string id)
    {
        if (CopyOf(id) is not { } copy) return null;
        if (copy.Complete) return "Sur ce PC";
        if (_offlineProgress is { } running && running.Id == id) return running.Total > 0 ? $"{Math.Min(99, running.Done * 100 / running.Total)} %" : "Arrive…";
        return _offlinePending.Contains(id) ? "En attente" : "En pause";
    }
    private string OfflineSummary()
    {
        var kept = _offline.Count(x => x.Complete);
        var parts = new List<string> { kept == 0 ? "Rien de prêt pour l’instant" : $"{kept} titre{(kept > 1 ? "s" : "")} à regarder sans connexion · {OfflineCopies.SizeText(_offline.Where(x => x.Complete).Sum(x => x.Size))}" };
        if (_offlineProgress is { } running && CopyOf(running.Id) is { } copy) parts.Add($"« {copy.Item.DisplayTitle}{(copy.Item.Type == "Episode" ? $" S{copy.Item.ParentIndexNumber ?? 1:00} E{copy.Item.IndexNumber ?? 1:00}" : "")} » arrive : {CopyState(running.Id)}");
        var waiting = _offlinePending.Count(x => x != _offlineProgress?.Id);
        if (waiting > 0) parts.Add($"{waiting} en attente");
        return string.Join("  ·  ", parts);
    }

    /// <summary>The page « Sur ce PC »: every copy, a series' episodes together; a card plays at once, even without Jellyfin.</summary>
    private async Task ShowOfflinePageAsync()
    {
        var items = OfflineCopies.Ordered(_offline).Select(x => x.Item with { UserData = x.Item.UserData with { } }).ToList();
        if (_store is { } store) await Task.Run(() => TryCache(() => store.ApplyLocalProgress(items)));
        if (_catalog.View != "offline") return;
        _catalog.Items = items; _catalog.TotalCount = items.Count; _catalog.Loading = false;
        RenderLibrary();
    }

    /// <summary>The title page's button: keep the film, or the season shown; how far it is; or remove it.</summary>
    private void ShowOfflineButton()
    {
        if (_detail is not { } item || _demo) { OfflineButton.Visibility = Visibility.Collapsed; return; }
        var targets = OfflineTargets();
        OfflineButton.Visibility = targets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (targets.Count == 0) return;
        var copies = targets.Select(x => CopyOf(x.Id)).OfType<OfflineCopy>().ToList();
        var complete = copies.Count(x => x.Complete);
        string icon, label, tip;
        if (copies.Count == 0)
        {
            (icon, label) = ("download", "");
            tip = item.Type == "Series" ? $"Garder la saison {SeasonShown()} sur ce PC ({Episodes(targets.Count)}), pour la regarder sans connexion" : "Garder ce film sur ce PC, pour le regarder sans connexion";
        }
        else if (complete == targets.Count) { (icon, label) = ("check", "Sur ce PC"); tip = $"Sur ce PC · {OfflineCopies.SizeText(copies.Sum(x => x.Size))}. Clique pour le retirer"; }
        else
        {
            icon = "download";
            label = item.Type == "Series" ? $"{complete}/{targets.Count}" + (_offlineProgress is { } running && copies.Any(x => x.Item.Id == running.Id) ? " · " + CopyState(running.Id) : "") : CopyState(item.Id) ?? "";
            tip = "Arrive sur ce PC. Clique pour l’arrêter";
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Icon { Kind = icon, Width = 18, Height = 18 });
        if (label.Length > 0) panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(9, 0, 0, 0) });
        OfflineButton.Content = panel; OfflineButton.ToolTip = tip;
        AutomationProperties.SetName(OfflineButton, label.Length > 0 ? label + ". " + tip : tip);
    }
    private static string Episodes(int count) => count == 1 ? "1 épisode" : $"{count} épisodes";
    private int SeasonShown() => ((SeasonSelector.SelectedItem as ComboBoxItem)?.Tag as EpisodePage)?.Season ?? 1;
    /// <summary>What the button acts on: the film, or every episode of the season shown (not only a slice of a long one).</summary>
    private List<MediaItem> OfflineTargets()
    {
        if (_detail is not { } item) return [];
        if (item.Type == "Series") { var season = SeasonShown(); return _episodes.Where(x => (x.ParentIndexNumber ?? 1) == season && CanKeep(x)).ToList(); }
        return CanKeep(item) ? [item] : [];
    }
    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        var targets = OfflineTargets(); if (targets.Count == 0) return;
        var copies = targets.Select(x => CopyOf(x.Id)).OfType<OfflineCopy>().ToList();
        if (copies.Count == 0) { _ = KeepOfflineAsync(targets); return; }
        var menu = new ContextMenu { PlacementTarget = OfflineButton, Placement = PlacementMode.Bottom };
        var missing = targets.Count - copies.Count;
        if (missing > 0) menu.Items.Add(MenuAction($"Garder {(missing == 1 ? "l’épisode qui manque" : $"les {missing} épisodes qui manquent")}", "download", () => KeepOfflineAsync(targets)));
        var paused = copies.Where(x => !x.Complete && CopyState(x.Item.Id) == "En pause").ToList();
        if (paused.Count > 0) menu.Items.Add(MenuAction("Reprendre", "download", () => { ResumeOffline(paused); return Task.CompletedTask; }));
        var whole = _detail?.Type == "Series" ? "la saison" : "le film";
        menu.Items.Add(MenuAction(copies.All(x => x.Complete) ? $"Retirer {whole} de ce PC · {OfflineCopies.SizeText(copies.Sum(x => x.Size))}" : "Arrêter et retirer de ce PC", "close", () => ForgetOfflineAsync(copies)));
        menu.IsOpen = true;
    }
    private void ResumeOffline(IEnumerable<OfflineCopy> copies)
    {
        foreach (var copy in copies) if (!_offlinePending.Contains(copy.Item.Id)) _offlinePending.Enqueue(copy.Item.Id);
        OfflineChanged(); PumpOffline();
    }
    /// <summary>The entry of a card's or an episode's menu: keep it, resume it, or remove it.</summary>
    private MenuItem? OfflineMenuItem(MediaItem item)
    {
        if (_demo || !CanKeep(item)) return null;
        if (CopyOf(item.Id) is not { } copy) return MenuAction("Garder sur ce PC", "download", () => KeepOfflineAsync([item]));
        if (!copy.Complete && CopyState(item.Id) == "En pause") return MenuAction("Reprendre l’arrivée sur ce PC", "download", () => { ResumeOffline([copy]); return Task.CompletedTask; });
        return MenuAction(copy.Complete ? $"Retirer de ce PC · {OfflineCopies.SizeText(copy.Size)}" : "Arrêter et retirer de ce PC", "close", () => ForgetOfflineAsync([copy]));
    }
    /// <summary>A line under an episode's length on the title page, kept to update as it progresses.</summary>
    private TextBlock EpisodeCopyLabel(MediaItem episode)
    {
        var label = new TextBlock { Text = CopyState(episode.Id) ?? "", FontSize = 11, Foreground = Palette.Get("TextBody"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        _episodeCopyLabels[episode.Id] = label;
        return label;
    }

    private void ShowOfflineSettings()
    {
        var kept = _offline.Where(x => x.Complete).ToList();
        OfflineFolderBox.Text = OfflineFolder;
        OfflineStatus.Text = "Sur la fiche d’un film ou d’une série, le bouton ⤓ garde son fichier d’origine sur ce PC, pour le regarder sans connexion. Ils sont dans « Sur ce PC », dans la barre de gauche."
            + (kept.Count > 0 ? $" Ici : {kept.Count} titre{(kept.Count > 1 ? "s" : "")}, {OfflineCopies.SizeText(kept.Sum(x => x.Size))}." : "");
        OfflineClearButton.Visibility = _offline.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OfflineFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Dossier des titres gardés sur ce PC" };
        if (Directory.Exists(OfflineFolder)) dialog.InitialDirectory = OfflineFolder;
        if (dialog.ShowDialog(this) != true) return;
        _settings.OfflineFolder = string.Equals(dialog.FolderName, DefaultOfflineFolder, StringComparison.OrdinalIgnoreCase) ? "" : dialog.FolderName;
        RememberOfflineFolder(OfflineFolder); TrySaveSettings(); ShowOfflineSettings();
    }
    private void OfflineClear_Click(object sender, RoutedEventArgs e)
    {
        if (_offline.Count == 0) return;
        var all = _offline.ToList();
        SetNotice($"Retirer de ce PC les {all.Count} titre{(all.Count > 1 ? "s" : "")} gardé{(all.Count > 1 ? "s" : "")} ({OfflineCopies.SizeText(all.Sum(x => x.BytesOnDisk))}) ? Ils restent sur Jellyfin.", "Retirer", () => _ = ForgetOfflineAsync(all), sticky: true);
    }
}
