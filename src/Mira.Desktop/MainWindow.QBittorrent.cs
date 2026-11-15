using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Private trackers (C411 and the like) through qBittorrent: their torrents never reach TorLink's WebTorrent (qBittorrent
/// won't give a private torrent's metadata to a magnet link, and these trackers only allow known clients). On the PC of
/// Jellyfin, Mira installs qBittorrent when asked (its official installer, checked), runs it under a profile of its own
/// (<see cref="QBittorrentHost"/>), hands it pasted magnet links and .torrent files, shows its downloads beside TorLink's,
/// and places the finished ones in the library by hard link or copy (<see cref="ImportMode.Share"/>): qBittorrent goes on
/// sharing them, for the account's ratio.
/// </summary>
public partial class MainWindow
{
    private QBittorrentHost? _qbHost;
    private QBittorrentHost QbHost => _qbHost ??= new(_profile.DirectoryPath);
    private bool _qbInstalling;
    private DateTime _qbPlacedAt = DateTime.MinValue;
    /// <summary>Links being read or shown in this PC's window right now: never tidied away under it.</summary>
    private readonly HashSet<string> _linksRead = [];

    /// <summary>qBittorrent is installed on this PC (Mira's or the person's own).</summary>
    private static bool QBittorrentInstalled() => QBittorrentHost.Program() is not null;

    /// <summary>
    /// Mira has handed qBittorrent something already: its profile exists. Until then Mira never starts it, even when
    /// the person has qBittorrent for themselves.
    /// </summary>
    private bool QBittorrentUsed() => Directory.Exists(QbHost.ProfileDirectory);

    /// <summary>
    /// Where it downloads: chosen beside the library the first time, then kept (qBittorrent shares from there), until the
    /// library sits on another volume (a drive mounted in its folder): new downloads go beside it then, said once.
    /// </summary>
    private string QBittorrentFolder()
    {
        if (_torlink is { } torlink && QBittorrentHistory.FolderToUse(_settings.QBittorrentFolder, torlink.Libraries) is var folder
            && !string.Equals(folder, _settings.QBittorrentFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (_settings.QBittorrentFolder.Length > 0)
                SetNotice(L($"qBittorrent télécharge maintenant dans « {folder} », sur le disque de ta bibliothèque. Ce qu’il partage déjà reste où il est.", $"qBittorrent now downloads to “{folder}”, on your library’s drive. What it already shares stays where it is."));
            _settings.QBittorrentFolder = folder;
            AutoSaveSettings();
        }
        return _settings.QBittorrentFolder.Length > 0 ? _settings.QBittorrentFolder : QBittorrentHistory.DownloadFolder(new(null, null, null));
    }

    /// <summary>qBittorrent answering Mira, started when needed; null with why not.</summary>
    private async Task<(QBittorrentClient? Client, string? Problem)> QBittorrentAsync(CancellationToken ct)
    {
        if (_demo) return (null, L("Pas de qBittorrent dans la démo.", "No qBittorrent in the demo."));
        if (QbHost.Client is { } running) return (running, null);
        if (!QBittorrentInstalled()) return (null, L("qBittorrent n’est pas installé sur le PC serveur : installe-le dans la page Téléchargements de Mira, sur ce PC.", "qBittorrent isn’t installed on the server PC: install it from Mira’s Downloads page, on that PC."));
        var problem = await QbHost.StartAsync(QBittorrentFolder(), ct);
        return (QbHost.Client, problem);
    }

    /// <summary>
    /// Its downloads in Mira's category, in the words of the downloads page; the finished ones are placed in the library
    /// at most every 15 seconds. Empty when Mira never used it, or when it doesn't answer.
    /// </summary>
    private async Task<List<RelayDownload>> QBittorrentDownloadsAsync(CancellationToken ct)
    {
        if (_demo || !QBittorrentUsed() || !QBittorrentInstalled()) return [];
        var (client, _) = await QBittorrentAsync(ct);
        if (client is null) return [];
        try
        {
            var torrents = await client.TorrentsAsync(ct);
            // A public download stops sharing once finished; a private one when qBittorrent's own limit says (72 h).
            foreach (var done in torrents.Where(QBittorrentDownloads.StopsSharing))
            {
                try { await client.ControlAsync(done.Hash, "pause", ct); }
                catch (HttpRequestException) { }
            }
            if (DateTime.UtcNow - _qbPlacedAt > TimeSpan.FromSeconds(15)) { _qbPlacedAt = DateTime.UtcNow; _ = PlaceQBittorrentAsync(client, torrents); await TidyLinksAsync(client, ct); }
            // A finished one is in the library: listed there (placed), not as a download.
            return torrents.Where(x => !QBittorrentDownloads.Finished(x)).Select(QBittorrentDownloads.Download).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or QBittorrentException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Quit by the person, or restarted: started again at the next look.
            QbHost.Lost();
            return [];
        }
    }

    /// <summary>The finished downloads not placed yet, placed: hard link where the drive allows it, a copy otherwise.</summary>
    private async Task PlaceQBittorrentAsync(QBittorrentClient client, List<QBittorrentTorrent> torrents)
    {
        if (_torlink is not { Ready: true } torlink || !torrents.Any(QBittorrentDownloads.Finished)) return;
        var ct = _torlinkLifetime.Token;
        try
        {
            // Settled ones are never looked at again; a download waiting for its files is, with its files.
            var settled = torlink.Importer.Entries.Where(x => x.State is not (TorLinkImportState.Waiting or TorLinkImportState.Importing)).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var history = await QBittorrentHistory.ReadAsync(torrents, client.FilesAsync, settled, ct);
            var fresh = history.ReadHistory()!.Select(x => x.Id).Where(x => torlink.Importer.Find(x) is null).ToList();
            if (await torlink.Importer.ProcessAsync(history, torlink.Libraries, ImportMode.Share, automatic: false, fresh, ct) == 0) return;
            RenderTorLinkImports();
            if (_client is { } jellyfin && !_demo) await torlink.NotifyAsync(jellyfin, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or QBittorrentException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
    }

    /// <summary>A magnet link or a .torrent into qBittorrent: null once it has it, otherwise why not.</summary>
    private async Task<string?> AddToQBittorrentAsync(string? magnet, byte[]? torrent)
    {
        var ct = _torlinkLifetime.Token;
        var (client, problem) = await QBittorrentAsync(ct);
        if (client is null) return problem ?? L("qBittorrent ne répond pas à Mira.", "qBittorrent isn’t answering Mira.");
        try
        {
            if (!await client.AddAsync(magnet, torrent, QBittorrentFolder(), ct))
            {
                var hash = torrent is not null ? Bencode.ReadFile(torrent)?.InfoHash : RemoteDownloads.InfoHash(magnet);
                // « Fails. »: it has it already, or it isn't a torrent.
                return hash is not null && (await client.TorrentsAsync(ct)).Any(x => x.Hash == hash) ? null : L("qBittorrent a refusé ce téléchargement.", "qBittorrent refused this download.");
            }
            return null;
        }
        catch (QBittorrentException ex) { return ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) { QbHost.Lost(); return L("qBittorrent ne répond pas à Mira.", "qBittorrent isn’t answering Mira."); }
    }

    // ---- A pasted link, read before it is downloaded (« Coller un lien magnet ») -------------------------------

    /// <summary>
    /// What a link holds, read by qBittorrent: added under <see cref="MagnetLinks.LookCategory"/>, stopped as soon as its
    /// metadata comes (nothing of its files downloaded), then its files and where Mira will put them. Nobody sharing it for
    /// <see cref="MagnetLinks.Patience"/>: « silent », taken out again. Never shown among the downloads.
    /// </summary>
    private async Task<AgentLookOutcome> LookWithQBittorrentAsync(string id, string magnet, CancellationToken ct)
    {
        if (RemoteDownloads.InfoHash(magnet) is not { } hash) return new(id, "failed", null, null, null, MagnetLinks.NotMagnet);
        var (client, problem) = await QBittorrentAsync(ct);
        if (client is null) return new(id, "failed", null, null, null, problem ?? L("qBittorrent ne répond pas à Mira.", "qBittorrent isn’t answering Mira."));
        try
        {
            var existing = await client.TorrentAsync(hash, ct);
            if (existing is null && !await client.AddAsync(magnet, null, QBittorrentFolder(), ct, look: true) && await client.TorrentAsync(hash, ct) is null)
                return new(id, "failed", null, null, null, L("qBittorrent a refusé ce lien.", "qBittorrent refused this link."));
            var until = DateTime.UtcNow + MagnetLinks.Patience;
            while (true)
            {
                if (await client.ReadAsync(hash, ct) is { Count: > 0 } files)
                {
                    var name = RemoteDownloads.CleanName((await client.TorrentAsync(hash, ct))?.Name) ?? hash;
                    var place = _torlink is { } torlink ? MagnetLinks.Place(name, files, torlink.Libraries, MediaPlanner.KnownShows(torlink.Importer.Entries)) : null;
                    return new(id, "done", name, files.Take(MagnetLinks.MaxFiles).Select(x => x.Path.Length > MagnetLinks.MaxPath ? x with { Path = x.Path[..MagnetLinks.MaxPath] } : x).ToList(), place, null);
                }
                if (DateTime.UtcNow > until)
                {
                    // Read for this window only: taken out (a download already there stays).
                    if (existing is null || existing.Category == MagnetLinks.LookCategory) await client.ControlAsync(hash, "delete", ct);
                    return new(id, "silent", null, null, null, null);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (QBittorrentException ex) { return new(id, "failed", null, null, null, ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) { QbHost.Lost(); return new(id, "failed", null, null, null, L("qBittorrent ne répond pas à Mira.", "qBittorrent isn’t answering Mira.")); }
    }

    /// <summary>
    /// A link read before, downloaded: only <paramref name="files"/> (null: all of it). Its reading is gone (15 min, or never
    /// read): read again first, the choice applied once its metadata comes. Null once qBittorrent has it, otherwise why not.
    /// </summary>
    private async Task<string?> DownloadLinkAsync(string magnet, IReadOnlyCollection<int>? files)
    {
        var ct = _torlinkLifetime.Token;
        if (RemoteDownloads.InfoHash(magnet) is not { } hash) return MagnetLinks.NotMagnet;
        var (client, problem) = await QBittorrentAsync(ct);
        if (client is null) return problem ?? L("qBittorrent ne répond pas à Mira.", "qBittorrent isn’t answering Mira.");
        try
        {
            var torrent = await client.TorrentAsync(hash, ct);
            if (torrent is not null && torrent.Category != MagnetLinks.LookCategory) return null;
            if (torrent is null)
            {
                if (files is null) return await AddToQBittorrentAsync(magnet, null);
                var read = await LookWithQBittorrentAsync("", magnet, ct);
                if (read.State != "done") return read.Error ?? MagnetLinks.Silent;
            }
            await client.ChooseAsync(hash, files, ct);
            return null;
        }
        catch (QBittorrentException ex) { return ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) { QbHost.Lost(); return L("qBittorrent ne répond pas à Mira.", "qBittorrent isn’t answering Mira."); }
    }

    /// <summary>A link read and closed without downloading it: taken out of qBittorrent at once (otherwise after 15 minutes).</summary>
    private async Task ForgetLinkAsync(string hash)
    {
        if (_demo || QbHost.Client is not { } client) return;
        try
        {
            if (await client.TorrentAsync(hash, CancellationToken.None) is { Category: MagnetLinks.LookCategory }) await client.ControlAsync(hash, "delete", CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or QBittorrentException or OperationCanceledException) { }
    }

    /// <summary>Links read more than 15 minutes ago and never downloaded: taken out of qBittorrent.</summary>
    private async Task TidyLinksAsync(QBittorrentClient client, CancellationToken ct)
    {
        foreach (var (hash, added) in await client.LooksAsync(ct))
            if (DateTimeOffset.UtcNow - added > MagnetLinks.Kept && !_linksRead.Contains(hash)) await client.ControlAsync(hash, "delete", ct);
    }

    /// <summary>
    /// A pause, a resume or a cancel (its files with it) of a download under way in qBittorrent: null when qBittorrent
    /// hasn't it under way (TorLink's, then), "" once done, otherwise why not.
    /// </summary>
    private async Task<string?> ControlQBittorrentAsync(string id, string action)
    {
        if (_demo || !QBittorrentUsed() || QbHost.Client is not { } client) return null;
        var ct = _torlinkLifetime.Token;
        try
        {
            if (!(await client.TorrentsAsync(ct)).Any(x => x.Hash == id && !QBittorrentDownloads.Finished(x))) return null;
            await client.ControlAsync(id, action, ct);
            return "";
        }
        catch (QBittorrentException ex) { return ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) { QbHost.Lost(); return L("qBittorrent ne répond pas à Mira.", "qBittorrent isn’t answering Mira."); }
    }

    /// <summary>What the magnet row says of qBittorrent, and whether its install button shows (on the PC of Jellyfin only).</summary>
    private void DescribeQBittorrent()
    {
        var installed = QBittorrentInstalled();
        QBittorrentInstallButton.Visibility = _serverLocal && !installed && !_qbInstalling ? Visibility.Visible : Visibility.Collapsed;
        // The same button on the page itself, beside « Add a .torrent »: under « Paste a magnet link » alone, nobody found it.
        QBittorrentInstallTop.Visibility = QBittorrentInstallButton.Visibility;
        QBittorrentLine.Text = !_serverLocal
            ? L("Un lien d’un tracker privé, ou un fichier .torrent : le qBittorrent du PC serveur le télécharge. Il arrête de le partager dès la fin, ou après 72 h pour un tracker privé.", "A link from a private tracker, or a .torrent file: the server PC’s qBittorrent downloads it. It stops sharing it once finished, or after 72 hours for a private tracker.")
            : _qbInstalling ? L("Installation de qBittorrent… Windows demande ton autorisation.", "Installing qBittorrent… Windows asks for your permission.")
            : installed ? L("qBittorrent le télécharge sur ce PC (trackers privés compris) et Mira le range dans ta bibliothèque. Le partage s’arrête dès la fin, ou après 72 h pour un tracker privé.", "qBittorrent downloads it on this PC (private trackers included) and Mira sorts it into your library. Sharing stops once it’s finished, or after 72 hours for a private tracker.")
            : L("Pour un tracker privé, il faut qBittorrent : ces trackers refusent TorLink. Mira installe la version officielle, vérifiée, et la pilote pour toi.", "A private tracker needs qBittorrent: these trackers refuse TorLink. Mira installs the official version, checked, and drives it for you.");
    }

    private async void QBittorrentInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_qbInstalling) return;
        _qbInstalling = true; DescribeQBittorrent();
        try
        {
            var problem = await QbHost.InstallAsync(null, _torlinkLifetime.Token);
            SetNotice(problem ?? L("qBittorrent est installé : colle un lien ou ajoute un .torrent.", "qBittorrent is installed: paste a link or add a .torrent."));
        }
        catch (OperationCanceledException) { }
        finally { _qbInstalling = false; DescribeQBittorrent(); }
    }

    // ---- A .torrent file ------------------------------------------------------------------------------

    private async void ServerTorrent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L("Choisir un fichier .torrent", "Choose a .torrent file"), Filter = L("Fichiers torrent|*.torrent|Tous les fichiers|*.*", "Torrent files|*.torrent|All files|*.*") };
        if (dialog.ShowDialog(this) == true) await SendTorrentFileAsync(dialog.FileName);
    }

    private void ServerDownloads_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TorrentDropped(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ServerDownloads_Drop(object sender, DragEventArgs e)
    {
        if (TorrentDropped(e) is not { } path) return;
        e.Handled = true;
        await SendTorrentFileAsync(path);
    }

    private static string? TorrentDropped(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && files[0].EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ? files[0] : null;

    /// <summary>A .torrent, read and checked here, then into this PC's qBittorrent or sent to the server PC's.</summary>
    private async Task SendTorrentFileAsync(string path)
    {
        if (!_serverScreen) return;
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > RemoteDownloads.MaxTorrent) { SetNotice(L("Ce fichier est trop gros pour un .torrent.", "This file is too big for a .torrent.")); return; }
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetNotice(L("Mira n’a pas pu lire ce fichier.", "Mira couldn’t read this file.")); return; }
        if (Bencode.ReadFile(bytes) is not { } torrent) { SetNotice(L("Ce n’est pas un fichier .torrent.", "That’s not a .torrent file.")); return; }
        try
        {
            if (_serverLocal)
            {
                if (await AddToQBittorrentAsync(null, bytes) is { } refused) { SetNotice(refused); return; }
            }
            else if (_client is { } client) await client.TorLinkTorrentAsync(bytes, _connectionLifetime.Token);
            else return;
            // Its row at the top of the right column says it.
            SmoothScroll.Jump(_serverSearchShown ? ServerQueueScroll : ServerRestScroll);
            await LoadServerDownloadsAsync();
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
    }
}
