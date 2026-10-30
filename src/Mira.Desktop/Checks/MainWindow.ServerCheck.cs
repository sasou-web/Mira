using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Mira.Core;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Mira for Windows against a real Jellyfin (--server-check, tools/server-check.ps1): the one of tools/web/setup.mjs
    /// on this PC, named by MIRA_CHECK_SERVER (at this PC's network address: a stream from Jellyfin, not its file), _USER
    /// and _PASSWORD; MIRA_STOP and MIRA_START stop and start it (commands for cmd.exe). Sign-in and Home, the file of a
    /// Jellyfin on this PC read directly, a film played as it is then converted under a lower quality, Jellyfin stopped
    /// and started again under it, « Garder sur ce PC » played without Jellyfin, « Regarder ensemble » with a second
    /// device of the account (its requests made here), « Mettre à la Corbeille » on a real library folder. Lines PASS,
    /// INFO and FAIL in result.txt, captures beside it.
    /// </summary>
    private async Task RunServerCheckAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--server-check") + 1]); Directory.CreateDirectory(output);
        var checks = new List<string>(); var step = "start";
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add("PASS " + message); }
        void Info(string message) => checks.Add("INFO " + message);
        async Task Until(string what, Func<bool> test, double seconds)
        {
            step = what;
            for (var timer = Stopwatch.StartNew(); !test(); await Task.Delay(100))
                if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"{what}: not within {seconds} s.");
        }
        async Task<T> UntilServer<T>(string what, Func<Task<T?>> test, double seconds) where T : class
        {
            step = what;
            for (var timer = Stopwatch.StartNew(); ; await Task.Delay(1000))
            {
                try { if (await test() is { } value) return value; }
                catch (Exception ex) when (IsExpected(ex)) { }
                if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"{what}: not within {seconds} s.");
            }
        }
        // The video moves on: two seconds further within 30 s (a runner without a graphics card starts slowly).
        async Task<double> Advancing(string what)
        {
            step = what;
            var before = _mpv?.Number("time-pos") ?? 0; var after = before;
            for (var timer = Stopwatch.StartNew(); after - before < 2; await Task.Delay(250))
            {
                if (timer.Elapsed.TotalSeconds > 30) throw new InvalidOperationException($"{what}: the video does not move on ({before:0.0} s, then {after:0.0} s in 30 s).");
                after = _mpv?.Number("time-pos") ?? 0;
            }
            return after;
        }
        async Task Shot(string name) { await ArtworkAsync(); await CaptureAsync(output, name); }
        Friend? friend = null;
        try
        {
            var server = Environment.GetEnvironmentVariable("MIRA_CHECK_SERVER"); var user = Environment.GetEnvironmentVariable("MIRA_CHECK_USER");
            var password = Environment.GetEnvironmentVariable("MIRA_CHECK_PASSWORD");
            if (_demo || string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || password is null || !_args.Contains("--data")
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MIRA_STOP")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MIRA_START")))
                throw new InvalidOperationException("The server check needs MIRA_CHECK_SERVER, _USER and _PASSWORD, MIRA_STOP and MIRA_START, an isolated --data profile and no demo.");
            _fallbackRefresh.Stop(); _searchTimer.Stop(); Width = 1440; Height = 900;
            _settings.Volume = 0; _settings.AutoNext = false; _settings.AutoSkip = false; _settings.Quality = StreamQuality.Max;

            step = "sign-in";
            Connection connection;
            using (var signIn = new JellyfinClient(new(server, "", user, "", _profile.DeviceId))) connection = await signIn.LoginAsync(user, password);
            _profile.SaveConnection(connection);
            await ActivateConnectionAsync(connection);
            WhatsNewOverlay.Visibility = WelcomeOverlay.Visibility = GuideOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
            var client = _client ?? throw new InvalidOperationException("No connection after signing in.");
            Info($"Jellyfin {await client.ServerVersionAsync()} at {client.Connection.Server}");
            Require(LoginOverlay.Visibility == Visibility.Collapsed && client.Connection.UserName == user, $"signs in to Jellyfin as {user}");
            await Until("Home", () => !_catalog.Loading && _catalog.Items.Count > 0, 30);
            Require(_homeLibraries.Count >= 3 && _catalog.Items.Count >= 4, $"Home shows the server's titles ({_catalog.Items.Count}) and libraries ({string.Join(", ", _homeLibraries.Select(x => x.Name))})");
            await Shot("server-home");

            // The test films, by their folder: Jellyfin may name them after what it finds on the Internet.
            async Task<MediaItem?> FindFilm(string folder)
            {
                foreach (var film in (await client.BrowseAsync("Movie", limit: 200)).Items)
                    if (await client.ItemAsync(film.Id) is { Path: { } path } full && path.Replace('/', '\\').Contains('\\' + folder + '\\', StringComparison.OrdinalIgnoreCase)) return full;
                return null;
            }
            var scanned = false;
            var cut = await UntilServer("the film « Coupure Test » in the library", async () =>
            {
                if (await FindFilm("Coupure Test (2025)") is { RunTimeTicks: > 0 } found) return found;
                if (!scanned) scanned = await client.ScanLibrariesAsync();
                return null;
            }, 180);
            var duration = cut.RunTimeTicks!.Value / 1e7;

            // Jellyfin on this PC by its own address (127.0.0.1): the film's file, read by mpv without Jellyfin.
            step = "the file of a Jellyfin on this PC";
            using (var loopback = new JellyfinClient(connection with { Server = new UriBuilder(client.Connection.Server) { Host = "127.0.0.1" }.Uri.AbsoluteUri }))
            {
                var local = await ServerStreams.OpenAsync(loopback, new ConnectionSpeed(), StreamQuality.Max, cut, new StreamWish(0), _settings.AudioLanguage, ReachableFileAsync);
                Require(!local.Converted && local.Authorization is null && File.Exists(local.Location) && string.Equals(local.Location, cut.Path, StringComparison.OrdinalIgnoreCase),
                    "at 127.0.0.1, Jellyfin's file is read from the disk, without a token");
            }

            step = "the film's page";
            await ShowDetailsAsync(cut);
            await Until("the page's « … » (Jellyfin on this PC)", () => FilesButton.Visibility == Visibility.Visible && _titleMore is { Files: true, Admin: true }, 15);
            Require(true, "the page of a film offers its files and « Identifier… » to an administrator on the PC of Jellyfin");
            await Shot("server-film");

            // Played as it is, from Jellyfin's address: the token in the header, never in the address.
            await PlayAsync(cut); await Until("the film plays as it is", () => _loaded, 40);
            Require(_stream is { Converted: false } file && file.Location.StartsWith(client.Connection.Server, StringComparison.OrdinalIgnoreCase)
                && !file.Location.Contains("key=", StringComparison.OrdinalIgnoreCase) && file.Authorization?.Contains("Token=", StringComparison.Ordinal) == true,
                "the film streams as it is from Jellyfin, its token in mpv's header only");
            await Advancing("the film as it is plays");
            var viewer = await UntilServer("Jellyfin sees this playback", async () => (await client.ViewersAsync()).FirstOrDefault(x => x.Own && x.Name == cut.Name), 30);
            Require(viewer.Method == "DirectPlay", $"Jellyfin's dashboard shows this PC playing the file as it is ({viewer.Method})");
            // A small read-ahead from here on: a stream cut shows in seconds, as on a slow line.
            _mpv!.Set("demuxer-max-bytes", "2MiB"); _mpv.Set("demuxer-readahead-secs", "4"); _mpv.Set("cache-secs", "4");

            // A lower quality: Jellyfin's conversion (HLS), from where the film was.
            var before = _mpv.Number("time-pos");
            await ChooseQualityAsync("1500000");
            await Until("the conversion plays", () => _loaded && _stream is { Converted: true }, 60);
            var converted = _stream!;
            Require(converted.Location.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) && !converted.Location.Contains("key=", StringComparison.OrdinalIgnoreCase) && converted.Bitrate == 1_500_000,
                $"1.5 Mb/s: Jellyfin converts ({string.Join(", ", converted.Reasons)}), its address without the token");
            var at = await Advancing("the conversion plays");
            Require(at >= before - 2, $"the conversion goes on where the file was ({before:0.0} s, then {at:0.0} s)");
            viewer = await UntilServer("Jellyfin sees the conversion", async () => (await client.ViewersAsync()).FirstOrDefault(x => x.Own && x.Name == cut.Name && x.Method == "Transcode"), 30);
            Require(viewer.Conversion is not null, $"Jellyfin's dashboard shows the conversion ({viewer.Conversion?.VideoCodec}, {viewer.Conversion?.Bitrate} b/s)");

            // Jellyfin stopped under the conversion, then started again.
            var cutAt = _mpv!.Number("time-pos");
            await RunServerCommandAsync("MIRA_STOP");
            await Until("the player waits for Jellyfin", () => _wake == WakeState.Waiting && PlayerStatus.Visibility == Visibility.Visible && PlayerStatusText.Text == StreamRecovery.LostText, 90);
            Require(_playing && _session.Item?.Id == cut.Id, $"Jellyfin stopped at {cutAt:0.0} s: the player waits for it and says so, never the end of the film");
            await CaptureAsync(output, "server-cut");
            await RunServerCommandAsync("MIRA_START");
            await Until("the film goes on once Jellyfin is back", () => _loaded && !_recovering && _wake == WakeState.None && _stream is { Converted: true }, 180);
            var resumed = await Advancing("the film goes on once Jellyfin is back");
            Require(resumed >= cutAt - 3 && resumed < duration * LibraryStore.WatchedThreshold, $"Jellyfin back: the conversion opened again where it was ({resumed:0.0} s)");
            Require((await client.ItemAsync(cut.Id)).UserData.Played == false, "the film cut short is not counted as watched");
            await StopPlaybackAsync(); ReturnToLibrary();
            await UntilServer("« Continuer à regarder »", async () => { await RefreshAsync(quiet: true); return _catalog.Resume.FirstOrDefault(x => x.Id == cut.Id); }, 30);
            Require(true, "the film stopped halfway is in « Continuer à regarder »");

            // « Garder sur ce PC »: the file and its subtitles, then played with Jellyfin stopped.
            _settings.Quality = StreamQuality.Auto;
            var dawn = await FindFilm("Aube Test (2021)") ?? throw new InvalidOperationException("The film « Aube Test » is not in the library.");
            await KeepOfflineAsync([dawn]);
            await Until("the copy is kept on this PC", () => CopyOf(dawn.Id) is { Complete: true }, 120);
            var copy = CopyOf(dawn.Id)!;
            Require(File.Exists(copy.VideoPath) && new FileInfo(copy.VideoPath).Length == copy.Size && copy.Subtitles.Count >= 1 && copy.Subtitles.All(x => File.Exists(Path.Combine(copy.Folder, x.File))),
                $"« Garder sur ce PC » keeps the file ({OfflineCopies.SizeText(copy.Size)}) and its subtitles ({copy.Subtitles.Count})");
            await RunServerCommandAsync("MIRA_STOP");
            await PlayAsync(dawn); await Until("the copy plays without Jellyfin", () => _loaded, 30);
            Require(_stream is null && _playingCopy?.Item.Id == dawn.Id && string.Equals(_playLocation, copy.VideoPath, StringComparison.OrdinalIgnoreCase), "Jellyfin stopped: the copy kept on this PC plays");
            await Advancing("the copy plays without Jellyfin");
            await StopPlaybackAsync(); ReturnToLibrary();
            await RunServerCommandAsync("MIRA_START");
            await UntilServer("Jellyfin back", async () => await client.PingAsync() ? client : null, 180);

            // « Regarder ensemble » with another device of the account: its requests made here, as Mira web makes them.
            await Until("Jellyfin's WebSocket open again", () => _listening, 90);
            // Jellyfin answers its ping a moment before it signs anyone in (503 « Jellyfin démarre »): asked again until it does.
            friend = await UntilServer("the second device signs in", async () => await Friend.StartAsync(client.Connection.Server, user, password), 60);
            await Until("the second device listens", () => friend.Listening, 30);
            await EnterGroupAsync(c => c.SyncNewAsync(Together.GroupName(c.Connection.UserName)), invite: false, made: true);
            Require(_group is not null, "« Créer un groupe » makes a SyncPlay group on Jellyfin");
            await friend.JoinAsync(_group!.GroupId);
            await Until("the second device in the group", () => friend.Joined, 20);
            var signs = await FindFilm("Signes Test (2022)") ?? throw new InvalidOperationException("The film « Signes Test » is not in the library.");
            await PlayAsync(signs, chosen: true);
            await Until("the group's title loads here", () => PlayingForGroup && _loaded && _session.Item?.Id == signs.Id, 60);
            await Until("the group plays", () => _mpv is { } mpv && !mpv.Flag("pause"), 40);
            Require(friend.Readied > 0, "Lecture in a group: Jellyfin puts the title on for both devices and plays it once both are ready");
            await Advancing("the group's title plays");
            await friend.SendAsync("Pause");
            await Until("paused by the other device", () => _mpv?.Flag("pause") == true, 15);
            Require(true, "paused on the other device: paused here");
            await CaptureAsync(output, "server-group");
            await friend.SendAsync("Unpause");
            await Until("played again by the other device", () => _mpv?.Flag("pause") == false, 20);
            Require(true, "played on the other device: played here");
            await LeaveGroupAsync(); await StopPlaybackAsync(); ReturnToLibrary();
            await friend.DisposeAsync(); friend = null;

            // « Mettre à la Corbeille » on a film of the library: its folder goes, then Jellyfin forgets it.
            var bin = await FindFilm("Chapitres Web (2024)") ?? throw new InvalidOperationException("The film « Chapitres Web » is not in the library.");
            var binFile = bin.Path!;
            await ShowDetailsAsync(bin);
            await Until("the page's « … » for the Recycle Bin", () => FilesButton.Visibility == Visibility.Visible && _titleMore is { Files: true }, 15);
            step = "Mettre à la Corbeille";
            await AskToRecycleAsync(bin);
            Require(NoticeAction.Visibility == Visibility.Visible && _noticeAction is not null && NoticeText.Text.Contains("Chapitres Web (2024)", StringComparison.Ordinal),
                "« Mettre à la Corbeille… » names the film's folder and asks once more");
            NoticeAction_Click(this, new());
            await Until("the film's folder in the Recycle Bin", () => !File.Exists(binFile) && !Directory.Exists(Path.GetDirectoryName(binFile)), 60);
            await UntilServer("Jellyfin forgets the film", async () => (await client.BrowseAsync("Movie", limit: 200)).Items.Any(x => x.Id == bin.Id) ? null : client, 120);
            Require(true, "the film's folder goes to the Recycle Bin and Jellyfin takes it out of the library");
        }
        catch (Exception ex)
        {
            var state = $"(step: {step}; playing {_playing}, loaded {_loaded}, wake {_wake}, recovering {_recovering}, stream {_stream?.Location ?? "none"}, mpv " +
                (_mpv is { } mpv ? $"time-pos {mpv.Number("time-pos"):0.0}, pause {mpv.Flag("pause")}, paused-for-cache {mpv.Flag("paused-for-cache")}" : "none") + $", failure {_session.Failure ?? "none"}, notice « {NoticeText.Text} »)";
            checks.Add("FAIL " + ex.GetType().Name + ": " + ex.Message + " " + state + "\n" + ex.StackTrace);
            Environment.ExitCode = 1;
        }
        finally
        {
            if (friend is not null) await friend.DisposeAsync();
            await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
            Close();
        }
    }

    /// <summary>MIRA_STOP or MIRA_START of the server check, a command line for cmd.exe (/s: its own quotes kept): done when it ends.</summary>
    private static async Task RunServerCommandAsync(string variable)
    {
        var command = Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException(variable + " is not set.");
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"{command}\"") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException(variable + " did not start.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        if (process.ExitCode != 0) throw new InvalidOperationException($"{variable} ended with {process.ExitCode}.");
    }

    /// <summary>
    /// A second device of the account in a SyncPlay group, as Mira web is (player/sync.js): its WebSocket open, one request
    /// at a time, Buffering then Ready for each title the group puts on, Ready again where a seek of the group lands.
    /// </summary>
    private sealed class Friend : IAsyncDisposable
    {
        private readonly JellyfinClient _client;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _one = new(1, 1);
        private readonly Task _listening;
        private string? _playlistItem;
        public bool Listening { get; private set; }
        public bool Joined { get; private set; }
        public int Readied { get; private set; }
        private Friend(JellyfinClient client)
        {
            _client = client;
            _listening = client.ListenAsync(() => { }, _stop.Token, open => Listening = open, (type, data) => _ = HeardAsync(type, data));
        }
        public static async Task<Friend> StartAsync(string server, string user, string password)
        {
            Connection connection;
            using (var signIn = new JellyfinClient(new(server, "", user, "", "mira-server-check-friend"))) connection = await signIn.LoginAsync(user, password);
            return new Friend(new JellyfinClient(connection));
        }
        public Task JoinAsync(string groupId) => SendAsync("Join", new JsonObject { ["GroupId"] = groupId });
        public async Task SendAsync(string what, JsonObject? body = null)
        {
            await _one.WaitAsync(_stop.Token);
            try { await _client.SyncSendAsync(what, body, _stop.Token); }
            finally { _one.Release(); }
        }
        private async Task HeardAsync(string type, JsonElement data)
        {
            try
            {
                var body = data.TryGetProperty("Data", out var d) ? d : default;
                if (type == "SyncPlayGroupUpdate" && data.TryGetProperty("Type", out var kind))
                {
                    if (kind.GetString() == "GroupJoined") Joined = true;
                    else if (kind.GetString() == "PlayQueue" && Together.QueueItem(body) is { } entry && entry.PlaylistItemId != _playlistItem)
                    {
                        _playlistItem = entry.PlaylistItemId;
                        await SendAsync("Buffering", State(entry.StartTicks, entry.PlaylistItemId));
                        await SendAsync("Ready", State(entry.StartTicks, entry.PlaylistItemId)); Readied++;
                    }
                }
                else if (type == "SyncPlayCommand" && data.TryGetProperty("Command", out var command) && command.GetString() == "Seek" && _playlistItem is { } item
                    && data.TryGetProperty("PositionTicks", out var ticks) && ticks.TryGetInt64(out var position))
                    await SendAsync("Ready", State(position, item));
            }
            catch (Exception ex) when (ExpectedErrors.Are(ex) || ex is OperationCanceledException) { }
        }
        private static JsonObject State(long ticks, string playlistItem) => new()
        {
            ["When"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), ["PositionTicks"] = ticks, ["IsPlaying"] = false, ["PlaylistItemId"] = playlistItem,
        };
        public async ValueTask DisposeAsync()
        {
            if (_stop.IsCancellationRequested) return;
            try { await _client.SyncLeaveAsync(); } catch (Exception ex) when (ExpectedErrors.Are(ex)) { }
            _stop.Cancel();
            try { await _listening; } catch (OperationCanceledException) { }
            _client.Dispose(); _stop.Dispose(); _one.Dispose();
        }
    }
}
