using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Mira.Mac.Playback;

/// <summary>Where mpv draws the video: OpenGL first, software when the window has no OpenGL.</summary>
public interface IVideoSurface
{
    Control View { get; }
    /// <summary>
    /// Connects <paramref name="player"/>'s frames to this view; true once mpv's render context exists. mpv needs it
    /// before a file opens, or it plays without picture.
    /// </summary>
    Task<bool> AttachAsync(MpvPlayer player);
    /// <summary>Frees mpv's render context. Required before <see cref="MpvPlayer.Dispose"/>, which would wait for it forever.</summary>
    void Release();
    /// <summary>Frames drawn so far (checks and diagnostics).</summary>
    long Frames { get; }
}

/// <summary>mpv's render API (libmpv/render.h): the few calls and structures Mira uses.</summary>
internal static unsafe class MpvRender
{
    public const int ParamInvalid = 0, ParamApiType = 1, ParamOpenGlInit = 2, ParamOpenGlFbo = 3, ParamFlipY = 4,
        ParamSwSize = 17, ParamSwFormat = 18, ParamSwStride = 19, ParamSwPointer = 20;
    [StructLayout(LayoutKind.Sequential)] public struct Param { public int Type; public IntPtr Data; }
    // get_proc_address, its context, and the extra_exts field of libmpv 1.x (null, ignored by 2.x).
    [StructLayout(LayoutKind.Sequential)] public struct OpenGlInit { public IntPtr GetProcAddress; public IntPtr Context; public IntPtr ExtraExtensions; }
    [StructLayout(LayoutKind.Sequential)] public struct Fbo { public int Id; public int Width; public int Height; public int InternalFormat; }

    public static IntPtr Create(MpvPlayer player, Param* parameters)
    {
        var create = (delegate* unmanaged[Cdecl]<IntPtr*, IntPtr, Param*, int>)player.Export("mpv_render_context_create");
        IntPtr context;
        var result = create(&context, player.Handle, parameters);
        if (result < 0) throw new InvalidOperationException($"mpv refuse le rendu vidéo ({result}).");
        return context;
    }
    public static void SetUpdateCallback(MpvPlayer player, IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr state) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, delegate* unmanaged[Cdecl]<IntPtr, void>, IntPtr, void>)player.Export("mpv_render_context_set_update_callback"))(context, callback, state);
    public static int Render(MpvPlayer player, IntPtr context, Param* parameters) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, Param*, int>)player.Export("mpv_render_context_render"))(context, parameters);
    public static void Free(IntPtr library, IntPtr context) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(library, "mpv_render_context_free"))(context);
}

/// <summary>The video, drawn by mpv with OpenGL into the window's own surface (Avalonia's OpenGL control).</summary>
public sealed unsafe class VideoView : OpenGlControlBase, IVideoSurface
{
    private MpvPlayer? _player;
    private IntPtr _context, _library;
    private GlInterface? _gl;
    private GCHandle _self;
    private long _frames;
    /// <summary>OpenGL failed here: the player switches to <see cref="SoftwareVideoView"/>.</summary>
    public event Action<string>? Failed;
    public Control View => this;
    public long Frames => Interlocked.Read(ref _frames);

    private TaskCompletionSource<bool>? _ready;
    public Task<bool> AttachAsync(MpvPlayer player)
    {
        _player = player; _library = player.Library;
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestNextFrameRendering();
        return _ready.Task;
    }
    public void Release()
    {
        // Normally done by OnOpenGlDeinit when the view leaves the window; this covers a view still attached.
        _player = null;
        if (_context != IntPtr.Zero) Failed?.Invoke("Le rendu vidéo n’a pas été libéré à temps.");
    }
    protected override void OnOpenGlInit(GlInterface gl) { _gl = gl; Initialized = true; }
    /// <summary>OpenGL started for this view (Avalonia called OnOpenGlInit).</summary>
    public new bool Initialized { get; private set; }
    /// <summary>Why mpv could not draw here, when it could not.</summary>
    public string? Problem { get; private set; }
    protected override void OnOpenGlDeinit(GlInterface gl) => FreeContext();
    private void FreeContext()
    {
        if (_context != IntPtr.Zero) { MpvRender.Free(_library, _context); _context = IntPtr.Zero; }
        if (_self.IsAllocated) _self.Free();
    }
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var (width, height) = ((int)Math.Max(1, Bounds.Width * scaling), (int)Math.Max(1, Bounds.Height * scaling));
        gl.Viewport(0, 0, width, height);
        if (_player is not { Handle: not 0 } player)
        {
            gl.ClearColor(0, 0, 0, 1); gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
            return;
        }
        if (_context == IntPtr.Zero && !CreateContext(player)) { gl.ClearColor(0, 0, 0, 1); gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT); return; }
        var fbo = new MpvRender.Fbo { Id = fb, Width = width, Height = height };
        var flip = 1;
        var parameters = stackalloc MpvRender.Param[3];
        parameters[0] = new() { Type = MpvRender.ParamOpenGlFbo, Data = (IntPtr)(&fbo) };
        parameters[1] = new() { Type = MpvRender.ParamFlipY, Data = (IntPtr)(&flip) };
        parameters[2] = new() { Type = MpvRender.ParamInvalid };
        if (MpvRender.Render(player, _context, parameters) >= 0) Interlocked.Increment(ref _frames);
        // mpv unbinds the framebuffer when it is done; Avalonia shows the one bound on return (black otherwise).
        gl.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, fb);
    }
    /// <summary>The OpenGL renderer, as the driver names it (diagnostics, self-check).</summary>
    public string Renderer { get; private set; } = "";
    private bool CreateContext(MpvPlayer player)
    {
        try
        {
            Renderer = _gl?.GetString(GlConsts.GL_RENDERER) ?? "";
            // A software OpenGL (Mesa's llvmpipe, a Mac without graphics acceleration) draws mpv's half-float
            // intermediate buffers black, and slowly: plain 8-bit buffers and mpv's simple path work there.
            if (Renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || Renderer.Contains("softpipe", StringComparison.OrdinalIgnoreCase) || Renderer.Contains("Software", StringComparison.OrdinalIgnoreCase))
            { player.Set("fbo-format", "rgba8"); player.Set("gpu-dumb-mode", "yes"); }
            _self = GCHandle.Alloc(this);
            var api = Marshal.StringToCoTaskMemUTF8("opengl");
            try
            {
                var init = new MpvRender.OpenGlInit { GetProcAddress = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&GetProcAddress, Context = GCHandle.ToIntPtr(_self) };
                var parameters = stackalloc MpvRender.Param[3];
                parameters[0] = new() { Type = MpvRender.ParamApiType, Data = api };
                parameters[1] = new() { Type = MpvRender.ParamOpenGlInit, Data = (IntPtr)(&init) };
                parameters[2] = new() { Type = MpvRender.ParamInvalid };
                _context = MpvRender.Create(player, parameters);
            }
            finally { Marshal.FreeCoTaskMem(api); }
            MpvRender.SetUpdateCallback(player, _context, &OnUpdate, GCHandle.ToIntPtr(_self));
            _ready?.TrySetResult(true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException)
        {
            if (_self.IsAllocated) _self.Free();
            _player = null;
            Problem = ex.Message + (Renderer.Length > 0 ? $" ({Renderer})" : "");
            _ready?.TrySetResult(false);
            Dispatcher.UIThread.Post(() => Failed?.Invoke(ex.Message));
            return false;
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static IntPtr GetProcAddress(IntPtr state, IntPtr name)
    {
        if (GCHandle.FromIntPtr(state).Target is not VideoView { _gl: { } gl }) return IntPtr.Zero;
        return gl.GetProcAddress(Marshal.PtrToStringUTF8(name) ?? "");
    }
    /// <summary>mpv has a new frame (called on one of its threads): draw it at the next composition.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnUpdate(IntPtr state)
    {
        if (GCHandle.FromIntPtr(state).Target is VideoView view) Dispatcher.UIThread.Post(view.RequestNextFrameRendering, DispatcherPriority.Render);
    }
}

/// <summary>
/// The video drawn by mpv in software into a bitmap, on its own thread: for a window without OpenGL. Uses more of
/// the processor than <see cref="VideoView"/>, but always works.
/// </summary>
public sealed unsafe class SoftwareVideoView : Control, IVideoSurface
{
    private MpvPlayer? _player;
    private IntPtr _context, _library;
    private GCHandle _self;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private WriteableBitmap? _front, _back;
    private PixelSize _size = new(1, 1);
    private long _frames;
    public Control View => this;
    public long Frames => Interlocked.Read(ref _frames);

    public Task<bool> AttachAsync(MpvPlayer player)
    {
        _player = player; _library = player.Library;
        _self = GCHandle.Alloc(this);
        var api = Marshal.StringToCoTaskMemUTF8("sw");
        try
        {
            var parameters = stackalloc MpvRender.Param[2];
            parameters[0] = new() { Type = MpvRender.ParamApiType, Data = api };
            parameters[1] = new() { Type = MpvRender.ParamInvalid };
            _context = MpvRender.Create(player, parameters);
        }
        finally { Marshal.FreeCoTaskMem(api); }
        MpvRender.SetUpdateCallback(player, _context, &OnUpdate, GCHandle.ToIntPtr(_self));
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Mira video" };
        _thread.Start();
        return Task.FromResult(true);
    }
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        lock (_gate) _size = new((int)Math.Max(1, e.NewSize.Width * scaling), (int)Math.Max(1, e.NewSize.Height * scaling));
        _wake.Set();
    }
    private void RenderLoop()
    {
        var dimensions = stackalloc int[2];
        var parameters = stackalloc MpvRender.Param[5];
        var format = Marshal.StringToCoTaskMemUTF8("bgr0");
        try { RenderFrames(dimensions, parameters, format); }
        finally { Marshal.FreeCoTaskMem(format); }
    }
    private void RenderFrames(int* dimensions, MpvRender.Param* parameters, IntPtr format)
    {
        while (true)
        {
            _wake.WaitOne(250);
            if (_stop) return;
            if (_player is not { Handle: not 0 } player || _context == IntPtr.Zero) continue;
            PixelSize size; lock (_gate) size = _size;
            if (_back is null || _back.PixelSize != size) _back = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var buffer = _back.Lock())
            {
                dimensions[0] = size.Width; dimensions[1] = size.Height;
                var stride = (nuint)buffer.RowBytes;
                parameters[0] = new() { Type = MpvRender.ParamSwSize, Data = (IntPtr)dimensions };
                parameters[1] = new() { Type = MpvRender.ParamSwFormat, Data = format };
                parameters[2] = new() { Type = MpvRender.ParamSwStride, Data = (IntPtr)(&stride) };
                parameters[3] = new() { Type = MpvRender.ParamSwPointer, Data = buffer.Address };
                parameters[4] = new() { Type = MpvRender.ParamInvalid };
                if (MpvRender.Render(player, _context, parameters) < 0) continue;
            }
            Interlocked.Increment(ref _frames);
            lock (_gate) (_front, _back) = (_back, _front);
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        lock (_gate) if (_front is { } frame) context.DrawImage(frame, new Rect(Bounds.Size));
    }
    public void Release()
    {
        _stop = true; _wake.Set(); _thread?.Join(2000); _thread = null;
        if (_context != IntPtr.Zero) { MpvRender.Free(_library, _context); _context = IntPtr.Zero; }
        if (_self.IsAllocated) _self.Free();
        _player = null;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnUpdate(IntPtr state)
    {
        if (GCHandle.FromIntPtr(state).Target is SoftwareVideoView view) view._wake.Set();
    }
}
