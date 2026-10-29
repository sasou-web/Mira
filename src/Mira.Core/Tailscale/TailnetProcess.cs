using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core.Tailscale;

/// <summary>Where the helper process is, apart from where its node is (<see cref="TailnetStatus"/>).</summary>
public enum TailnetHelper
{
    /// <summary>Never started.</summary>
    Off,
    /// <summary>Being started.</summary>
    Starting,
    /// <summary>Running, its node started (logged in or not).</summary>
    Running,
    /// <summary>It ended or could not start; started again at <see cref="TailnetProcess.NextAttempt"/>.</summary>
    Restarting,
    /// <summary>It cannot run here (missing, blocked, another version): not started again by itself.</summary>
    Failed,
    /// <summary>Stopped by Mira.</summary>
    Stopped,
}

/// <summary>
/// mira-tailscale, the helper that runs Mira's own Tailscale node on Windows and Mac (src/Mira.Tailscale), seen from the
/// app: started from its executable, accepted only when its first line says the protocol, Mira's version and that it can
/// upload no log; then asked in JSON lines, and listened to for its node's status and its proxy. Started again after a
/// crash, a little later each time. Its diagnostics go to a local file of limited size, login pages hidden, and nowhere
/// else. It ends with this object, or by itself with its input when Mira ends.
/// </summary>
public sealed partial class TailnetProcess : IAsyncDisposable
{
    /// <summary>What the app gives the helper.</summary>
    public sealed record Options
    {
        /// <summary>mira-tailscale's executable, checked by the app beforehand.</summary>
        public required string Executable { get; init; }
        /// <summary>None for mira-tailscale; the tests' stand-in takes some.</summary>
        public IReadOnlyList<string> Arguments { get; init; } = [];
        /// <summary>The node's folder: its encrypted state and its lock.</summary>
        public required string Directory { get; init; }
        /// <summary>The device's name on Tailscale (« Mira-PC »), made a DNS label by the helper.</summary>
        public required string Hostname { get; init; }
        /// <summary>Mira's version: only a helper of the same runs.</summary>
        public required string AppVersion { get; init; }
        /// <summary>The helper's diagnostics, kept under <see cref="LogLimit"/> bytes with one older file beside (.1).</summary>
        public required string LogFile { get; init; }
        /// <summary>The 32 bytes that encrypt the node's state, read off the UI thread (a keychain may ask); null when they cannot be read now.</summary>
        public required Func<byte[]?> StateKey { get; init; }
        /// <summary>Tests and the test bench only: their coordination server, and a key that logs in without a browser.</summary>
        public string? ControlUrl { get; init; }
        public string? AuthKey { get; init; }
        /// <summary>Called with each helper just started, before it is asked anything (Windows ties it to Mira's life).</summary>
        public Action<Process>? Started { get; init; }
    }

    /// <summary>The version of the protocol this client speaks (cmd/mira-tailscale).</summary>
    public const int Protocol = 1;
    public const long LogLimit = 256 * 1024;
    /// <summary>The waits before starting a helper that ended again: then every 5 minutes, from the start after 10 minutes without a crash.</summary>
    public static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(10);
    /// <summary>Helpers in a row that end or say nothing before their first line: then not started again by themselves (an antivirus, Gatekeeper).</summary>
    public const int SilentLimit = 2;

    private readonly Options _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _launching = new(1, 1);
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _life = new();
    private readonly object _logGate = new();
    private Launch? _launch;
    private long _nextId;
    private int _crashes, _silent;
    private bool _wanted, _disposed;
    private CancellationTokenSource? _wait;

    public TailnetProcess(Options options) => _options = options;

    public TailnetHelper Helper { get; private set; }
    /// <summary>Why the helper last ended or could not start (<see cref="TailnetCodes.Helper"/>, <see cref="TailnetCodes.Version"/>, <see cref="TailnetCodes.Key"/> or the node's code).</summary>
    public TailnetException? Failure { get; private set; }
    /// <summary>When the helper is started again, while <see cref="Helper"/> is <see cref="TailnetHelper.Restarting"/>.</summary>
    public DateTimeOffset? NextAttempt { get; private set; }
    /// <summary>The node's last status; null while no helper runs.</summary>
    public TailnetStatus? Status { get; private set; }
    /// <summary>The node's proxy; null while no helper runs.</summary>
    public TailnetProxy? Proxy { get; private set; }
    /// <summary>The Tailscale version the helper is built with.</summary>
    public string? TailscaleVersion { get; private set; }
    /// <summary>On a thread-pool thread: <see cref="Helper"/>, <see cref="Failure"/> or <see cref="NextAttempt"/> changed.</summary>
    public event Action? HelperChanged;
    /// <summary>On a thread-pool thread: the node's status changed.</summary>
    public event Action<TailnetStatus>? StatusChanged;
    /// <summary>On a thread-pool thread: the proxy changed (another address and other credentials at each launch), or is gone (null).</summary>
    public event Action<TailnetProxy?>? ProxyChanged;

    /// <summary>
    /// Starts the helper and its node, from its saved state when it has one (logged in or not: <see cref="Status"/> says);
    /// a helper waiting to be started again is started now. Returns once the node runs; throws <see cref="TailnetException"/>
    /// when it cannot, and keeps trying by itself when trying again can help.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _wanted = true; _wait?.Cancel(); }
        await _launching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Helper == TailnetHelper.Running) return;
            await LaunchAsync(ct).ConfigureAwait(false);
        }
        finally { _launching.Release(); }
    }

    /// <summary>The page that logs the node in: open it in a browser; the node joins the tailnet once it is approved there.</summary>
    public async Task<string> LoginAsync(CancellationToken ct = default)
    {
        var answer = await RequestAsync("login", null, TimeSpan.FromSeconds(75), ct).ConfigureAwait(false);
        return answer.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } page ? page : throw new TailnetException(TailnetCodes.Helper, "the helper gave no login page");
    }

    /// <summary>Signs the node out of its tailnet (« logged out » in the admin console); it then waits for a new login.</summary>
    public Task LogoutAsync(CancellationToken ct = default) => RequestAsync("logout", null, TimeSpan.FromSeconds(40), ct);

    /// <summary>
    /// Whether an address (« 100.64.0.2:8096 », « pc.tail1234.ts.net:8096 ») answers through the node, which sends it
    /// nothing but a connection: null when it does, else the code that says why not (the node's, or
    /// <see cref="TailnetCodes.Helper"/> when no helper runs).
    /// </summary>
    public async Task<string?> ProbeAsync(string address, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            await RequestAsync("probe", new() { ["address"] = address, ["timeout"] = timeout.TotalSeconds }, timeout + TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return null;
        }
        catch (TailnetException ex) { return ex.Code; }
    }

    /// <summary>Makes the node look at the network afresh: after the computer woke up, or changed networks.</summary>
    public Task RebindAsync(CancellationToken ct = default) => RequestAsync("rebind", null, TimeSpan.FromSeconds(15), ct);

    /// <summary>Ends the helper: asked to quit, then ended after 5 seconds. Its node keeps its state for the next time.</summary>
    public async ValueTask DisposeAsync()
    {
        Launch? launch;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _wanted = false; _wait?.Cancel(); launch = _launch; _launch = null;
        }
        _life.Cancel();
        if (launch is not null) await EndAsync(launch, quit: true).ConfigureAwait(false);
        bool had;
        lock (_gate) { had = Proxy is not null; Status = null; Proxy = null; }
        if (had) ProxyChanged?.Invoke(null);
        Set(TailnetHelper.Stopped, null, null);
        Fail(new TailnetException(TailnetCodes.Helper, "stopped"));
    }

    /// <summary>One helper process, with what is known of it.</summary>
    private sealed class Launch(Process process, StreamWriter input, TailnetProxy proxy)
    {
        public Process Process { get; } = process;
        public StreamWriter Input { get; } = input;
        /// <summary>The credentials drawn for it; its address comes with the proxy event.</summary>
        public TailnetProxy Proxy { get; } = proxy;
        public TaskCompletionSource<JsonElement> Hello { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public bool Ending { get; set; }
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        Set(TailnetHelper.Starting, null, null);
        Launch? launch = null;
        try
        {
            byte[]? key;
            try { key = await Task.Run(_options.StateKey, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new TailnetException(TailnetCodes.Key, ex.Message, ex); }
            if (key is not { Length: 32 }) throw new TailnetException(TailnetCodes.Key, "the state key cannot be read now");
            launch = Spawn();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            JsonElement hello;
            try { hello = await launch.Hello.Task.WaitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_life.IsCancellationRequested) { throw new TailnetException(TailnetCodes.Helper, "the helper said nothing"); }
            CheckHello(hello);
            _silent = 0;
            await RequestAsync("start", new()
            {
                ["dir"] = _options.Directory, ["hostname"] = _options.Hostname, ["controlURL"] = _options.ControlUrl, ["authKey"] = _options.AuthKey,
                ["stateKey"] = key, ["proxyUser"] = launch.Proxy.User, ["proxyPassword"] = launch.Proxy.Password,
            }, TimeSpan.FromSeconds(60), ct, launch).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_launch, launch)) throw new TailnetException(TailnetCodes.Helper, "the helper ended");
            }
            Set(TailnetHelper.Running, null, null);
        }
        catch (Exception ex) when (ex is TailnetException or Win32Exception or IOException or OperationCanceledException)
        {
            var failure = ex as TailnetException ?? new TailnetException(TailnetCodes.Helper, ex.Message, ex);
            // Started, then ended or silent before its first line.
            if (launch is not null && !launch.Hello.Task.IsCompletedSuccessfully) _silent++;
            if (launch is not null) await EndAsync(launch, quit: false).ConfigureAwait(false);
            lock (_gate) { if (ReferenceEquals(_launch, launch)) _launch = null; }
            if (ex is OperationCanceledException && (ct.IsCancellationRequested || _life.IsCancellationRequested)) { Set(_disposed ? TailnetHelper.Stopped : TailnetHelper.Off, null, null); throw; }
            // What another try cannot change (no executable, blocked, another version, a helper ended at once again and
            // again): said, not tried again by itself.
            if (ex is Win32Exception || failure.Code == TailnetCodes.Version || _silent >= SilentLimit) Set(TailnetHelper.Failed, failure, null);
            else ScheduleRestart(failure);
            throw failure;
        }
    }

    private Launch Spawn()
    {
        var start = new ProcessStartInfo(_options.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.Executable)) ?? "",
        };
        foreach (var argument in _options.Arguments) start.ArgumentList.Add(argument);
        // Tailscale reads its debugging knobs (TS_…) from the environment: the node behaves the same on every computer.
        foreach (var name in start.Environment.Keys.Where(x => x.StartsWith("TS_", StringComparison.OrdinalIgnoreCase)).ToList()) start.Environment.Remove(name);
        var proxy = new TailnetProxy("", "mira", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        var process = Process.Start(start) ?? throw new TailnetException(TailnetCodes.Helper, "the helper did not start");
        var launch = new Launch(process, process.StandardInput, proxy);
        lock (_gate) { _launch = launch; }
        Log($"started (process {process.Id})");
        // Not tied to Mira's life, the helper still ends with its input when Mira does.
        try { _options.Started?.Invoke(process); }
        catch (Exception ex) { Log("not tied to Mira's life: " + ex.Message); }
        _ = Task.Run(() => ReadAsync(launch));
        _ = Task.Run(() => ReadErrorsAsync(launch));
        return launch;
    }

    private void CheckHello(JsonElement hello)
    {
        var protocol = hello.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var number) ? number : 0;
        var version = hello.TryGetProperty("version", out var v) ? v.GetString() : null;
        var uploads = !hello.TryGetProperty("logUpload", out var u) || u.ValueKind != JsonValueKind.False;
        TailscaleVersion = hello.TryGetProperty("tailscale", out var t) ? t.GetString() : null;
        Log($"helper {version}, protocol {protocol}, Tailscale {TailscaleVersion}");
        if (protocol != Protocol || version != _options.AppVersion)
            throw new TailnetException(TailnetCodes.Version, $"helper {version} (protocol {protocol}) for Mira {_options.AppVersion} (protocol {Protocol})");
        // Mira's promise: no log of the node ever goes to Tailscale. A helper that could send some does not run.
        if (uploads) throw new TailnetException(TailnetCodes.Version, "this helper can upload logs to Tailscale");
    }

    private async Task<JsonElement> RequestAsync(string op, Dictionary<string, object?>? fields, TimeSpan timeout, CancellationToken ct, Launch? starting = null)
    {
        Launch launch;
        lock (_gate)
        {
            launch = starting ?? (Helper == TailnetHelper.Running ? _launch : null) ?? throw new TailnetException(TailnetCodes.Helper, "no helper runs");
        }
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            var request = new Dictionary<string, object?> { ["id"] = id, ["op"] = op };
            foreach (var (name, value) in fields ?? []) if (value is not null) request[name] = value;
            await _writing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await launch.Input.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
                await launch.Input.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { throw new TailnetException(TailnetCodes.Helper, "the helper ended", ex); }
            finally { _writing.Release(); }
            JsonElement reply;
            try { reply = await answer.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TailnetException(TailnetCodes.Helper, $"the helper did not answer « {op} »"); }
            if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return reply;
            var code = reply.TryGetProperty("code", out var c) && c.GetString() is { Length: > 0 } given ? given : TailnetCodes.Helper;
            throw new TailnetException(code, reply.TryGetProperty("error", out var e) ? e.GetString() : null);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>The helper's lines: answers by their id, then the events (hello, proxy, status).</summary>
    private async Task ReadAsync(Launch launch)
    {
        try
        {
            while (await launch.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                JsonElement message;
                try { using var document = JsonDocument.Parse(line); message = document.RootElement.Clone(); }
                catch (JsonException) { Log("not a message: " + Shorten(line)); continue; }
                Handle(launch, message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        await EndedAsync(launch).ConfigureAwait(false);
    }

    private void Handle(Launch launch, JsonElement message)
    {
        if (message.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && number > 0)
        {
            if (_pending.TryGetValue(number, out var waiting)) waiting.TrySetResult(message);
            return;
        }
        switch (message.TryGetProperty("event", out var kind) ? kind.GetString() : null)
        {
            case "hello": launch.Hello.TrySetResult(message); break;
            case "proxy":
                if (!message.TryGetProperty("address", out var address) || address.GetString() is not { Length: > 0 } where) break;
                var proxy = launch.Proxy with { Address = where };
                lock (_gate) { if (!ReferenceEquals(_launch, launch)) return; Proxy = proxy; }
                ProxyChanged?.Invoke(proxy);
                break;
            case "status":
                if (!message.TryGetProperty("status", out var body)) break;
                TailnetStatus? status;
                try { status = body.Deserialize<TailnetStatus>(Json.Options); }
                catch (JsonException ex) { Log("unreadable status: " + ex.Message); break; }
                if (status is null) break;
                lock (_gate) { if (!ReferenceEquals(_launch, launch)) return; Status = status; }
                StatusChanged?.Invoke(status);
                break;
            case "error": Log("the helper refused a request: " + (message.TryGetProperty("error", out var error) ? error.GetString() : "")); break;
        }
    }

    /// <summary>The helper ended (its output closed): waiting requests fail, and it is started again unless Mira stopped it.</summary>
    private async Task EndedAsync(Launch launch)
    {
        try { await launch.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
        int? code = launch.Process.HasExited ? launch.Process.ExitCode : null;
        launch.Hello.TrySetException(new TailnetException(TailnetCodes.Helper, $"the helper ended (code {code})"));
        bool current;
        lock (_gate)
        {
            current = ReferenceEquals(_launch, launch);
            if (current) { _launch = null; Status = null; Proxy = null; }
        }
        Log(launch.Ending ? $"ended (code {code})" : $"ended unexpectedly (code {code})");
        if (!current) return;
        ProxyChanged?.Invoke(null);
        var failure = new TailnetException(TailnetCodes.Helper, $"the helper ended (code {code})");
        Fail(failure);
        // A start that was under way says so itself (LaunchAsync); a running helper that ends is started again.
        if (Helper == TailnetHelper.Running && !launch.Ending)
        {
            if (DateTimeOffset.UtcNow - launch.Started >= StableAfter) _crashes = 0;
            ScheduleRestart(failure);
        }
    }

    private void ScheduleRestart(TailnetException failure)
    {
        CancellationTokenSource wait;
        TimeSpan delay;
        lock (_gate)
        {
            if (_disposed || !_wanted) return;
            delay = Retries[Math.Min(_crashes++, Retries.Length - 1)];
            _wait?.Cancel();
            wait = _wait = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        }
        Log($"started again in {delay.TotalSeconds:0} s ({failure.Code})");
        Set(TailnetHelper.Restarting, failure, DateTimeOffset.UtcNow + delay);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await StartAsync(_life.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TailnetException or OperationCanceledException or ObjectDisposedException) { }
        });
    }

    /// <summary>Ends a helper: asked to quit (its node closes and keeps its state), ended after 5 seconds.</summary>
    private async Task EndAsync(Launch launch, bool quit)
    {
        launch.Ending = true;
        try
        {
            if (quit && !launch.Process.HasExited)
            {
                await _writing.WaitAsync().ConfigureAwait(false);
                try { await launch.Input.WriteLineAsync("{\"op\":\"quit\"}").ConfigureAwait(false); await launch.Input.FlushAsync().ConfigureAwait(false); }
                finally { _writing.Release(); }
            }
            launch.Input.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        try { await launch.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { try { launch.Process.Kill(); Log("ended by Mira after 5 s"); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } }
    }

    private void Set(TailnetHelper helper, TailnetException? failure, DateTimeOffset? next)
    {
        lock (_gate)
        {
            if (_disposed && helper != TailnetHelper.Stopped) return;
            Helper = helper; NextAttempt = next;
            if (failure is not null || helper == TailnetHelper.Running) Failure = failure;
        }
        HelperChanged?.Invoke();
    }

    private void Fail(TailnetException failure)
    {
        foreach (var (id, waiting) in _pending) if (_pending.TryRemove(id, out _)) waiting.TrySetException(failure);
    }

    /// <summary>The helper's own diagnostics (already without login pages), a line at a time.</summary>
    private async Task ReadErrorsAsync(Launch launch)
    {
        try
        {
            while (await launch.Process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) Write(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void Log(string text) => Write($"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} Mira: {text}");

    /// <summary>One line into the log; past <see cref="LogLimit"/>, it becomes « .1 » (replacing the one there) and a new one starts.</summary>
    private void Write(string line)
    {
        line = Redact(line);
        lock (_logGate)
        {
            try
            {
                var file = new FileInfo(_options.LogFile);
                if (file.Exists && file.Length > LogLimit) File.Move(file.FullName, file.FullName + ".1", overwrite: true);
                File.AppendAllText(file.FullName, line + Environment.NewLine);
            }
            // A log that cannot be written is a log less, never a node less.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Hides the pages that log a node in: whoever opens one while it is valid can add the node to their own tailnet.</summary>
    public static string Redact(string line) => LoginPage().Replace(line, "<login page>");
    [GeneratedRegex(@"https?://[^\s""'<>]+/(?:a|auth)/[0-9A-Za-z_-]+")] private static partial Regex LoginPage();
    private static string Shorten(string line) => Redact(line.Length > 200 ? line[..200] + "…" : line);
}
