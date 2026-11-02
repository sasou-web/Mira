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

    /// <summary>qBittorrent is installed on this PC (Mira's or the person's own).</summary>
    private static bool QBittorrentInstalled() => QBittorrentHost.Program() is not null;

    /// <summary>
    /// Mira has handed qBittorrent something already: its profile exists. Until then Mira never starts it, even when
    /// the person has qBittorrent for themselves.
    /// </summary>
    private bool QBittorrentUsed() => Directory.Exists(QbHost.ProfileDirectory);

    /// <summary>Where it downloads: chosen beside the library the first time, then kept (qBittorrent shares from there).</summary>
    private string QBittorrentFolder()
    {
        if (_settings.QBittorrentFolder.Length == 0 && _torlink is { } torlink)
        {
            _settings.QBittorrentFolder = QBittorrentHistory.DownloadFolder(torlink.Libraries);
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
            if (DateTime.UtcNow - _qbPlacedAt > TimeSpan.FromSeconds(15)) { _qbPlacedAt = DateTime.UtcNow; _ = PlaceQBittorrentAsync(client, torrents); }
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
        QBittorrentLine.Text = !_serverLocal
            ? L("Un lien d’un tracker privé, ou un fichier .torrent : le qBittorrent du PC serveur le télécharge, et continue de le partager pour ton ratio.", "A link from a private tracker, or a .torrent file: the server PC’s qBittorrent downloads it, and keeps sharing it for your ratio.")
            : _qbInstalling ? L("Installation de qBittorrent… Windows demande ton autorisation.", "Installing qBittorrent… Windows asks for your permission.")
            : installed ? L("qBittorrent le télécharge sur ce PC (trackers privés compris), puis continue de le partager pour ton ratio pendant que Mira le range dans ta bibliothèque.", "qBittorrent downloads it on this PC (private trackers included), then keeps sharing it for your ratio while Mira sorts it into your library.")
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
            SetNotice(_serverLocal ? L($"qBittorrent télécharge « {torrent.Name} » : sa progression s’affiche à droite.", $"qBittorrent is downloading “{torrent.Name}”: its progress shows on the right.")
                : L($"« {torrent.Name} » envoyé au qBittorrent du PC serveur : sa progression s’affiche à droite.", $"“{torrent.Name}” sent to the server PC’s qBittorrent: its progress shows on the right."));
            SmoothScroll.Jump(ServerQueueScroll);
            await LoadServerDownloadsAsync();
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
    }
}
