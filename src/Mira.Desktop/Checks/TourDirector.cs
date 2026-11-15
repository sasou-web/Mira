using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace Mira.Desktop;

/// <summary>
/// Films the real window for the presentation video (--tour, never merged): each scene recorded by ffmpeg (named by
/// MIRA_TOUR_FFMPEG) from the screen while Mira's clocks run slower (<see cref="TimeDilation"/>), the pointer moved and
/// pressed as a mouse would (SendInput), every move of it written down so that the montage draws its own pointer on the
/// film, in step with it. tour.json says, for each scene, its file and how its real time maps to Mira's.
/// </summary>
internal sealed class TourDirector(string output)
{
    private readonly string? _ffmpeg = Environment.GetEnvironmentVariable("MIRA_TOUR_FFMPEG");
    private readonly List<object> _scenes = [];
    private Process? _recorder;
    private Scene? _scene;
    private (double X, double Y) _at;
    private bool _down;

    private sealed class Scene
    {
        public string Name { get; init; } = "";
        public string File { get; init; } = "";
        public string Grab { get; init; } = "";
        public double Factor { get; init; }
        public int Fps { get; init; }
        /// <summary>The wall's time and Mira's when the scene began, in microseconds since 1970.</summary>
        public long RealStart { get; set; }
        public long MiraStart { get; set; }
        public Stopwatch Clock { get; } = new();
        public List<double[]> Pointer { get; } = [];
        public List<object[]> Marks { get; } = [];
    }

    private static long Micros(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    /// <summary>Starts a scene: Mira's clocks at <paramref name="factor"/>, ffmpeg filming the screen.</summary>
    public async Task BeginAsync(string name, double factor = 6, string grab = "gdigrab", int? fps = null)
    {
        if (_scene is not null) await EndAsync();
        TimeDilation.Set(factor);
        var rate = fps ?? (int)Math.Round(90 / factor);
        var file = name + ".mkv";
        _scene = new Scene { Name = name, File = file, Grab = grab, Factor = factor, Fps = rate };
        var input = grab == "ddagrab"
            ? $"-f lavfi -i ddagrab=output_idx=0:framerate={rate}:draw_mouse=0 -vf hwdownload,format=bgra"
            : $"-f gdigrab -framerate {rate} -draw_mouse 0 -i desktop";
        if (_ffmpeg is not null)
        {
            var info = new ProcessStartInfo(_ffmpeg, $"-hide_banner -loglevel warning -y {input} -fps_mode passthrough -copyts -c:v libx264 -preset veryfast -crf 12 -pix_fmt yuv420p -g 60 \"{Path.Combine(output, file)}\"")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true };
            _recorder = Process.Start(info)!;
            var log = Path.Combine(output, name + ".ffmpeg.txt");
            _ = Task.Run(async () => { var text = await _recorder.StandardError.ReadToEndAsync(); await File.WriteAllTextAsync(log, text); });
        }
        // The recorder's first frames, real time.
        await RealWait(1.2);
        _scene.RealStart = Micros(TimeDilation.RealUtc); _scene.MiraStart = Micros(DateTime.UtcNow);
        _scene.Clock.Start();
        Note();
    }

    /// <summary>Ends the scene: the film closed, what it holds written down.</summary>
    public async Task EndAsync(double tail = 300)
    {
        if (_scene is not { } scene) return;
        await Wait(tail);
        Note();
        if (_recorder is { } recorder)
        {
            try { await recorder.StandardInput.WriteAsync('q'); await recorder.StandardInput.FlushAsync(); } catch (IOException) { }
            for (var i = 0; i < 300 && !recorder.HasExited; i++) await Task.Delay(TimeSpan.FromMilliseconds(100 / TimeDilation.Factor));
            if (!recorder.HasExited) recorder.Kill();
            _recorder = null;
        }
        _scenes.Add(new { scene.Name, scene.File, scene.Grab, scene.Factor, scene.Fps, scene.RealStart, scene.MiraStart, Pointer = scene.Pointer, Marks = scene.Marks });
        _scene = null;
        TimeDilation.Set(1);
        await File.WriteAllTextAsync(Path.Combine(output, "tour.json"), JsonSerializer.Serialize(new { Scenes = _scenes }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>A named moment of the scene, for the montage.</summary>
    public void Mark(string what) => _scene?.Marks.Add([_scene.Clock.Elapsed.TotalMilliseconds, what]);

    private void Note() => _scene?.Pointer.Add([_scene.Clock.Elapsed.TotalMilliseconds, Math.Round(_at.X, 1), Math.Round(_at.Y, 1), _down ? 1 : 0]);

    /// <summary>Mira's time (dilated).</summary>
    public static Task Wait(double milliseconds) => Task.Delay(TimeSpan.FromMilliseconds(milliseconds));
    /// <summary>The wall's time.</summary>
    public static Task RealWait(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds / TimeDilation.Factor));

    /// <summary>The pointer to a spot of <paramref name="element"/> (fractions of its size), along a gentle curve.</summary>
    public Task MoveAsync(FrameworkElement element, double x = .5, double y = .5, double milliseconds = 650)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth * x, element.ActualHeight * y));
        return MoveToAsync(point.X, point.Y, milliseconds);
    }

    public async Task MoveToAsync(double x, double y, double milliseconds = 650)
    {
        var from = _at; var clock = Stopwatch.StartNew();
        // Across the line, a little: a hand never goes straight.
        var (dx, dy) = (x - from.X, y - from.Y);
        var bend = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) * .06, 0, 40);
        while (true)
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / milliseconds);
            var e = t < .5 ? 16 * Math.Pow(t, 5) : 1 - Math.Pow(-2 * t + 2, 5) / 2;
            var arc = Math.Sin(Math.PI * e) * bend;
            var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            Place(from.X + dx * e - dy / length * arc, from.Y + dy * e + dx / length * arc);
            if (t >= 1) break;
            await Wait(8);
        }
    }

    /// <summary>Where the pointer is, at once (no motion filmed).</summary>
    public void Place(double x, double y)
    {
        _at = (x, y);
        var (w, h) = (GetSystemMetrics(0), GetSystemMetrics(1));
        Send(new Input { Type = 0, Mouse = new MouseInput { X = (int)Math.Round(x * 65535 / (w - 1)), Y = (int)Math.Round(y * 65535 / (h - 1)), Flags = 0x0001 | 0x8000 } });
        Note();
    }

    public async Task ClickAsync(double hold = 90)
    {
        _down = true; Send(new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0002 } }); Note(); Mark("press");
        await Wait(hold);
        _down = false; Send(new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0004 } }); Note();
    }

    /// <summary>The wheel, a notch at a time (negative = down the page).</summary>
    public async Task WheelAsync(int notches, double gap = 110)
    {
        for (var i = 0; i < Math.Abs(notches); i++)
        {
            Send(new Input { Type = 0, Mouse = new MouseInput { Data = (uint)(notches < 0 ? -120 : 120), Flags = 0x0800 } }); Mark("wheel");
            await Wait(gap);
        }
    }

    public async Task TypeAsync(string text, double each = 85)
    {
        foreach (var c in text)
        {
            Send(new Input { Type = 1, Key = new KeyInput { Scan = (ushort)c, Flags = 0x0004 } }, new Input { Type = 1, Key = new KeyInput { Scan = (ushort)c, Flags = 0x0004 | 0x0002 } });
            await Wait(each * (c == ' ' ? 1.4 : 1));
        }
    }

    public async Task KeyAsync(ushort key, double after = 120)
    {
        Send(new Input { Type = 1, Key = new KeyInput { Virtual = key } }, new Input { Type = 1, Key = new KeyInput { Virtual = key, Flags = 0x0002 } });
        await Wait(after);
    }

    private static void Send(params Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length) throw new InvalidOperationException("Windows refused the input.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Virtual, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; [FieldOffset(8)] public KeyInput Key; }
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
}
