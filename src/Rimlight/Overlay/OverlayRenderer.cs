using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Rimlight.Core.Audio;
using Rimlight.Core.Color;
using Rimlight.Core.Diagnostics;
using Rimlight.Core.Motion;
using Rimlight.Core.Settings;
using Rimlight.Interop;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace Rimlight.Overlay;

/// <summary>
/// Owns the GPU device, the overlay windows (one per target display) and a dedicated render thread.
/// Static glow: renders only when something changes (≈0% CPU).
/// Idle breathing: ~30 fps. Music Sync: one frame per display refresh, paced by the swap chain.
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    private const string WindowClass = "RimlightOverlayWindow";


    [System.Runtime.CompilerServices.InlineArray(4)] private struct Float4 { private float _e; }
    [System.Runtime.CompilerServices.InlineArray(8)] private struct Float8 { private float _e; }

    /// <summary>Must match the HLSL cbuffer layout exactly (240 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderParams
    {
        public float ViewW, ViewH;
        public float RectX, RectY;
        public float RectW, RectH;
        public float Thickness, Glow, Radius, Intensity, Rotation, BandExtent;
        public float C0L, C0a, C0b, C0_;
        public float C1L, C1a, C1b, C1_;
        public float C2L, C2a, C2b, C2_;
        public float C3L, C3a, C3b, C3_;
        public Float4 Weights;
        public float Boost, Floor, ArcSoft, Pad0;
        public Float8 ArcPos;
        public Float8 ArcWid;
        public Float8 ArcGain;
    }

    /// <summary>Per-frame animated values (same for every display). Arcs/colours live in fields below.</summary>
    private readonly record struct FrameState(float Intensity, float Boost, float Floor, float Rotation);

    // Colour cross-fade state (render thread): 4 slots + weights, eased in OKLab.
    private bool _colorsInit;
    private readonly Lab[] _fromC = new Lab[4], _toC = new Lab[4], _curC = new Lab[4];
    private readonly float[] _fromW = new float[4], _toW = new float[4], _curW = new float[4];
    private Lab[] _targetKey = [];
    private double _fadeStart, _fadeSeconds = 0.8;

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile OverlayConfig _config;
    private volatile bool _rebuildRequested = true;
    private volatile bool _stop;

    // Render-thread state
    private Native.WndProc? _wndProc; // kept alive: native code holds a pointer to it
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDXGIFactory2? _factory;
    private IDCompositionDevice? _dcomp;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Buffer? _cb;
    private ID3D11RasterizerState? _raster;
    private readonly List<MonitorOverlay> _windows = new();
    private bool _shown;

    private readonly FlowEngine _flow = new();
    private FlowFrame _flowFrame = new() { Arcs = new Arc[FlowFrame.MaxArcs] };
    private long _lastBeatCount;
    private double _lastFrameTime = MonoClock.Now;
    private double _activity;      // 0 = idle look, 1 = music look; eased so pause/play never snaps
    private float _bassSmooth;
    private FlowPattern _loggedPattern = (FlowPattern)(-1);

    /// <summary>Where the renderer reads live audio analysis from (any thread-safe getter).</summary>
    public Func<AudioFrame>? AudioSource { get; set; }

    public OverlayRenderer(OverlayConfig initial)
    {
        _config = initial;
        _thread = new Thread(Run) { IsBackground = true, Name = "Rimlight.Render" };
        _thread.Start();
    }

    /// <summary>Raised (on the render thread) if the GPU can't be initialised at all.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Raised (on the render thread) after overlays are rebuilt, with the displays found.</summary>
    internal event Action<IReadOnlyList<MonitorInfo>>? DisplaysChanged;

    public void Update(OverlayConfig config)
    {
        _config = config;
        _wake.Set();
    }

    /// <summary>Call when monitors are added/removed/resized or the taskbar moves.</summary>
    public void RebuildDisplays()
    {
        _rebuildRequested = true;
        _wake.Set();
    }

    // ------------------------------------------------------------------ render thread

    private enum Pace { Sleep, Poll, Breathing, VSync }

    /// <summary>Draw every other display refresh. Measured: GPU use 27% -> 8% on an Intel UHD laptop.</summary>
    private const bool HalfRate = true;

    private void Run()
    {
        try
        {
            RegisterWindowClass();
            CreateDevice();
        }
        catch (Exception ex)
        {
            Log.Error("Renderer initialisation failed", ex);
            Failed?.Invoke(ex);
            return;
        }

        OverlayConfig? applied = null;
        bool dirty = true;
        var topmostClock = Stopwatch.StartNew();
        var wakeOnly = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        var wakeAndFrame = new IntPtr[2];

        while (!_stop)
        {
            PumpMessages();
            var cfg = _config;
            var pace = Pace.Sleep;

            try
            {
                if (_rebuildRequested || applied is null || cfg.Display != applied.Display)
                {
                    _rebuildRequested = false;
                    Log.Info("Rebuilding overlays");
                    RebuildWindows(cfg);
                    dirty = true;
                }
                if (!ReferenceEquals(cfg, applied)) dirty = true;
                applied = cfg;

                bool visible = cfg.Active && !cfg.Suspended;
                if (visible != _shown)
                {
                    _shown = visible;
                    foreach (var w in _windows) w.SetVisible(_shown);
                    dirty = true;
                }

                bool render = false;
                if (_shown && _windows.Count > 0)
                {
                    var frame = ComputeFrame(cfg, out bool musicMoving);
                    if (musicMoving) pace = Pace.VSync;                      // music playing / easing: full rate
                    else if (cfg.IdleBreathing && cfg.Ambient == AmbientPreset.Glow) pace = Pace.Breathing; // calm breathing: ~30 fps
                    else if (cfg.Animation == AnimationMode.MusicSync) pace = Pace.Poll; // waiting for music
                    render = dirty || pace is Pace.VSync or Pace.Breathing;
                    if (render)
                    {
                        RenderAll(cfg, frame);
                        dirty = false;
                    }
                }

                if (_shown && topmostClock.ElapsedMilliseconds > 2000)
                {
                    foreach (var w in _windows) w.ReassertTopmost();
                    topmostClock.Restart();
                }
            }
            catch (SharpGen.Runtime.SharpGenException ex) when (IsDeviceLost(ex.ResultCode))
            {
                Log.Warn($"GPU device lost ({ex.ResultCode}); recreating");
                RecreateDevice();
                dirty = true;
                continue;
            }
            catch (Exception ex)
            {
                Log.Error("Render loop error", ex);
                Thread.Sleep(500); // don't spin on a persistent error
            }

            // Wait for the next reason to draw. Settings changes and window messages always wake us.
            switch (pace)
            {
                case Pace.VSync when _windows.Count > 0:
                    // The swap chain signals when it's ready for the next frame (once per refresh).
                    wakeAndFrame[0] = wakeOnly[0];
                    wakeAndFrame[1] = _windows[0].Pacer.FrameWaitHandle;
                    Native.MsgWaitForMultipleObjects(2, wakeAndFrame, false, 100, Native.QS_ALLINPUT);
                    if (HalfRate || cfg.LowPower)
                    {
                        // Skip every other refresh (~30 fps on a 60 Hz screen): cuts GPU load about 3x on integrated graphics. Always on; Battery Saver kept for clarity.
                        Native.MsgWaitForMultipleObjects(2, wakeAndFrame, false, 40, Native.QS_ALLINPUT);
                    }
                    break;
                case Pace.Breathing:
                    Native.MsgWaitForMultipleObjects(1, wakeOnly, false, 33, Native.QS_ALLINPUT);
                    break;
                case Pace.Poll:
                    // Static glow while silent in Music Sync: check for music 10×/s without drawing.
                    Native.MsgWaitForMultipleObjects(1, wakeOnly, false, 100, Native.QS_ALLINPUT);
                    break;
                default:
                    // Static: sleep until something changes; wake every 2 s while visible to re-assert topmost.
                    Native.MsgWaitForMultipleObjects(1, wakeOnly, false, _shown ? 2000u : Native.INFINITE, Native.QS_ALLINPUT);
                    break;
            }
        }

        DestroyWindows();
        DestroyDevice();
    }

    /// <summary>
    /// Blends between the idle look (full ring, optional breathing) and the music look (sweep or pulse)
    /// according to whether music is playing. <paramref name="musicMoving"/> is true while anything is animating
    /// because of music, so the loop knows to run at full frame rate.
    /// </summary>
    private FrameState ComputeFrame(OverlayConfig cfg, out bool musicMoving)
    {
        double now = MonoClock.Now;
        double dt = Math.Clamp(now - _lastFrameTime, 0, 0.25);
        _lastFrameTime = now;
        float brightness = Math.Clamp(cfg.Brightness, 0f, 1f);

        // Is music playing? Loopback delivers nothing when audio is paused, so stale frames mean silence too.
        var a = AudioSource?.Invoke() ?? AudioFrame.Silent;
        bool music = cfg.Animation == AnimationMode.MusicSync && a.Active && now - a.Time < 1.0;

        // Ease in fast (~0.25 s) when music starts, out gently (~0.8 s) when it stops.
        double target = music ? 1 : 0;
        double tau = target > _activity ? 0.25 : 0.8;
        _activity += (target - _activity) * (1 - Math.Exp(-dt / tau));
        if (Math.Abs(_activity - target) < 0.002) _activity = target;
        float act = (float)_activity;

        // Flow engine: which pattern, where the lit arcs are, drops, colour drift.
        _flow.Fixed = cfg.Motion == MotionStyle.Auto ? null : (FlowPattern)Enum.Parse(typeof(FlowPattern), cfg.Motion.ToString());
        if (_windows.Count > 0)
        {
            var b = _windows[0].Monitor.Bounds;
            _flow.Aspect = b.Height > 0 ? (float)b.Width / b.Height : 16f / 9f;
        }
        if (a.BeatCount != _lastBeatCount)
        {
            if (music) _flow.Beat(a.BeatTime, a.BeatStrength);
            _lastBeatCount = a.BeatCount;
        }
        _flow.Update(now, dt, a.Bass, a.Mids, a.Highs, music);
        _flow.Fill(ref _flowFrame);
        if (music && _flowFrame.Pattern != _loggedPattern)
        {
            _loggedPattern = _flowFrame.Pattern;
            Log.Info($"Flow: {_loggedPattern} (energy {_flow.EnergyShort:0.00})");
        }

        // Smooth the bass a little more for the render (audio frames arrive ~every 10–50 ms).
        float bassTarget = music ? a.Bass : 0f;
        _bassSmooth += (bassTarget - _bassSmooth) * (float)(1 - Math.Exp(-dt / 0.04));

        // Ambient look (no music): the selected ambient effect (Glow = steady ring with optional breathing).
        _ambient.Preset = cfg.Ambient;
        _ambient.Breathing = cfg.IdleBreathing;
        _ambient.Aspect = _flow.Aspect;
        _ambient.Fill(now, ref _ambientFrame);
        float idleIntensity = brightness * (cfg.Animation == AnimationMode.MusicSync && cfg.Ambient == AmbientPreset.Glow ? 0.75f : 1f);
        float musicIntensity = brightness * (0.88f + 0.12f * _bassSmooth);

        // Blend the two light patterns: each arc's strength is weighted by how "musical" we currently are,
        // and the strongest 8 go to the GPU. So music starting/stopping cross-fades, never snaps.
        MixArcs(_ambientFrame.Arcs, 1 - act, _flowFrame.Arcs, act);

        // Colour drift: ambient presets rotate at their own speed; music uses the flow engine's drift + kicks.
        double flowDelta = _flowFrame.Rotation - _lastFlowRotation;
        flowDelta -= Math.Round(flowDelta);
        _lastFlowRotation = _flowFrame.Rotation;
        _rotation += _ambient.RotationSpeed * dt * (1 - act) + flowDelta * act;
        _rotation -= Math.Floor(_rotation);

        // Ambient presets can bring their own palette (Rainbow, Fire…); music always uses the album/your colours.
        bool useAmbientColours = act < 0.5 && _ambient.Colors is not null;
        bool fading = UpdateColors(cfg, now, useAmbientColours ? _ambient.Colors : null);

        bool ambientMoving = _ambient.Animated && cfg.Ambient != AmbientPreset.Glow && act < 1f;
        musicMoving = act > 0f || fading || ambientMoving;
        return new FrameState(
            Intensity: Math.Min(1f, idleIntensity + (musicIntensity - idleIntensity) * act),
            Boost: _ambientFrame.Boost * (1 - act) + _flowFrame.Boost * act,
            Floor: _ambientFrame.Floor + (_flowFrame.Floor - _ambientFrame.Floor) * act,
            Rotation: (float)_rotation);
    }

    private readonly AmbientEngine _ambient = new();
    private FlowFrame _ambientFrame = new() { Arcs = new Arc[FlowFrame.MaxArcs] };
    private readonly Arc[] _mixArcs = new Arc[FlowFrame.MaxArcs];
    private readonly List<Arc> _mixScratch = new(2 * FlowFrame.MaxArcs);
    private double _rotation, _lastFlowRotation;
    private bool _wasAmbientColours;

    private void MixArcs(Arc[] a, float wa, Arc[] b, float wb)
    {
        _mixScratch.Clear();
        foreach (var x in a) if (x.Gain * wa > 0.002f) _mixScratch.Add(x with { Gain = x.Gain * wa });
        foreach (var x in b) if (x.Gain * wb > 0.002f) _mixScratch.Add(x with { Gain = x.Gain * wb });
        _mixScratch.Sort((p, q) => q.Gain.CompareTo(p.Gain));
        Array.Clear(_mixArcs);
        for (int i = 0; i < _mixArcs.Length && i < _mixScratch.Count; i++) _mixArcs[i] = _mixScratch[i];
    }

    /// <summary>
    /// Eased OKLab cross-fade of up to four colours whenever the target palette changes (never a hard pop).
    /// The first colour gets the biggest share of the loop; unused slots fade their weight to zero.
    /// </summary>
    private bool UpdateColors(OverlayConfig cfg, double now, Lab[]? overrideColours = null)
    {
        var target = overrideColours ?? (cfg.Colors is { Length: >= 1 } c ? c : [cfg.Primary, cfg.Secondary]);
        bool sourceChanged = (overrideColours is not null) != _wasAmbientColours;
        _wasAmbientColours = overrideColours is not null;
        if (!_colorsInit || !target.AsSpan().SequenceEqual(_targetKey))
        {
            // Snapshot where we are now, then aim for the new palette.
            for (int i = 0; i < 4; i++) { _fromC[i] = _curC[i]; _fromW[i] = _curW[i]; }
            int n = Math.Min(4, target.Length);
            float main = n <= 2 ? 0.6f : 0.4f;
            for (int i = 0; i < 4; i++)
            {
                _toC[i] = target[Math.Min(i, n - 1)];
                _toW[i] = i >= n ? 0 : i == 0 ? main : (1 - main) / Math.Max(1, n - 1);
            }
            if (!_colorsInit)
            {
                for (int i = 0; i < 4; i++) { _fromC[i] = _curC[i] = _toC[i]; _fromW[i] = _curW[i] = _toW[i]; }
                _colorsInit = true;
            }
            _targetKey = target;
            _fadeStart = now;
            // Switching between ambient-preset colours and album/your colours: a slow, smooth fade.
            _fadeSeconds = sourceChanged ? 1.2 : Math.Max(0, cfg.ColorFadeSeconds);
        }
        double u = _fadeSeconds <= 0 ? 1 : Math.Clamp((now - _fadeStart) / _fadeSeconds, 0, 1);
        float e = (float)(u * u * (3 - 2 * u));
        for (int i = 0; i < 4; i++)
        {
            _curC[i] = Lab.Lerp(_fromC[i], _toC[i], e);
            _curW[i] = _fromW[i] + (_toW[i] - _fromW[i]) * e;
        }
        return u < 1;
    }

    private static bool IsDeviceLost(SharpGen.Runtime.Result r) =>
        r == Vortice.DXGI.ResultCode.DeviceRemoved || r == Vortice.DXGI.ResultCode.DeviceReset;

    private void PumpMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    private void RegisterWindowClass()
    {
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = WindowClass,
        };
        if (Native.RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST: return Native.HTTRANSPARENT;
            case Native.WM_MOUSEACTIVATE: return Native.MA_NOACTIVATE;
            case Native.WM_DISPLAYCHANGE:   // resolution / monitor added or removed
            case Native.WM_DPICHANGED:      // display scaling changed
                _rebuildRequested = true;
                return IntPtr.Zero;
            case Native.WM_SETTINGCHANGE when wParam.ToInt64() == Native.SPI_SETWORKAREA:
                _rebuildRequested = true;   // taskbar moved, resized, or auto-hide toggled
                break;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void CreateDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var flags = DeviceCreationFlags.BgraSupport;
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out _device, out _ctx);
        if (hr.Failure)
        {
            Log.Warn($"Hardware D3D11 device unavailable ({hr}); falling back to WARP");
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out _device, out _ctx).CheckError();
        }

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        _factory = adapter.GetParent<IDXGIFactory2>();
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

        var source = LoadShaderSource();
        var profileSuffix = _device.FeatureLevel >= FeatureLevel.Level_11_0 ? "5_0" : "4_0";
        var vsCode = Compiler.Compile(source, "VSMain", "EdgeGlow.hlsl", "vs_" + profileSuffix, ShaderFlags.OptimizationLevel3);
        var psCode = Compiler.Compile(source, "PSMain", "EdgeGlow.hlsl", "ps_" + profileSuffix, ShaderFlags.OptimizationLevel3);
        _vs = _device.CreateVertexShader(vsCode.Span);
        _ps = _device.CreatePixelShader(psCode.Span);
        _cb = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer));
        _raster = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));

        Log.Info($"GPU: {adapter.Description.Description} (feature level {_device.FeatureLevel})");
    }

    private static string LoadShaderSource()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("EdgeGlow.hlsl", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private void RecreateDevice()
    {
        DestroyWindows();
        DestroyDevice();
        try { CreateDevice(); }
        catch (Exception ex) { Log.Error("Device recreation failed", ex); Thread.Sleep(1000); }
        _rebuildRequested = true;
    }

    private void DestroyDevice()
    {
        _raster?.Dispose(); _cb?.Dispose(); _ps?.Dispose(); _vs?.Dispose();
        _dcomp?.Dispose(); _factory?.Dispose(); _ctx?.Dispose(); _device?.Dispose();
        _raster = null; _cb = null; _ps = null; _vs = null; _dcomp = null; _factory = null; _ctx = null; _device = null;
    }

    private void RebuildWindows(OverlayConfig cfg)
    {
        DestroyWindows();
        if (_device is null) return;

        var monitors = Monitors.Enumerate();
        foreach (var m in monitors)
            Log.Info($"Display {m.DeviceId}: \"{m.FriendlyName}\" {m.Bounds} scale {m.Scale:0.##} {(m.IsInternal ? "built-in" : "external")}{(m.IsPrimary ? " primary" : "")}");

        IEnumerable<MonitorInfo> targets = cfg.Display switch
        {
            "all" => monitors,
            "primary" => monitors.Where(m => m.IsPrimary),
            var id => monitors.Where(m => m.DeviceId == id) is var match && match.Any()
                ? match
                : monitors.Where(m => m.IsPrimary), // selected display unplugged → fall back to primary
        };

        foreach (var m in targets)
        {
            try { _windows.Add(new MonitorOverlay(m, WindowClass, _device, _factory!, _dcomp!)); }
            catch (Exception ex) { Log.Error($"Failed to create overlay on {m.DeviceId}", ex); }
        }
        _dcomp!.Commit();

        if (_shown) foreach (var w in _windows) w.SetVisible(true);
        // New windows can receive display/DPI messages as they appear; those describe the state we just
        // built, so swallow them instead of rebuilding again.
        PumpMessages();
        _rebuildRequested = false;
        DisplaysChanged?.Invoke(monitors);
    }

    private void DestroyWindows()
    {
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();
        try { _dcomp?.Commit(); } catch { }
    }

    private void RenderAll(OverlayConfig cfg, FrameState frame)
    {
        bool first = true;
        foreach (var overlay in _windows)
        {
            RenderMonitor(overlay, cfg, frame);
            foreach (var strip in overlay.Strips)
            {
                if (!strip.HasArea) continue;
                // Only the pacing strip waits for the display; the rest present immediately.
                var hr = strip.Present(first ? 1u : 0u);
                first = false;
                if (hr.Failure)
                {
                    if (IsDeviceLost(hr)) throw new SharpGen.Runtime.SharpGenException(hr);
                    Log.Warn($"Present failed on {overlay.Monitor.DeviceId}: {hr}");
                }
            }
        }
    }

    private void RenderMonitor(MonitorOverlay overlay, OverlayConfig cfg, FrameState frame)
    {
        var ctx = _ctx!;
        var m = overlay.Monitor;
        float scale = m.Scale;

        // Light rect inside the window: the full monitor, or the work area when avoiding the taskbar.
        var r = cfg.AvoidTaskbar ? m.WorkArea : m.Bounds;
        float rx = r.Left - m.Bounds.Left, ry = r.Top - m.Bounds.Top;
        float rw = Math.Max(2, r.Width), rh = Math.Max(2, r.Height);

        float thickness = Math.Max(1f, cfg.ThicknessDip * scale);
        float glow = cfg.GlowDip * scale;
        float radius = (cfg.RadiusDip < 0 ? m.AutoCornerRadiusDip : cfg.RadiusDip) * scale;
        radius = Math.Min(radius, Math.Min(rw, rh) * 0.5f);
        float extent = Math.Min(Math.Min(rw, rh) * 0.5f, MathF.Ceiling(Math.Max(thickness + glow + 2f, radius + 2f)));
        overlay.Layout(r, extent);

        // All coordinates are monitor-local; each strip views its part through an offset viewport.
        int mw = Math.Max(1, m.Bounds.Width), mh = Math.Max(1, m.Bounds.Height);
        var p = new ShaderParams
        {
            ViewW = mw, ViewH = mh,
            RectX = rx, RectY = ry, RectW = rw, RectH = rh,
            Thickness = thickness, Glow = glow, Radius = radius,
            Intensity = frame.Intensity,
            Rotation = (0.95f + frame.Rotation) % 1f, // primary starts just before the top-left corner, then drifts
            // (frame.Rotation already blends the ambient preset's drift and the music engine's drift)
            BandExtent = extent,
            C0L = _curC[0].L, C0a = _curC[0].A, C0b = _curC[0].B,
            C1L = _curC[1].L, C1a = _curC[1].A, C1b = _curC[1].B,
            C2L = _curC[2].L, C2a = _curC[2].A, C2b = _curC[2].B,
            C3L = _curC[3].L, C3a = _curC[3].A, C3b = _curC[3].B,
            Boost = frame.Boost, Floor = frame.Floor, ArcSoft = 0.12f,
        };
        for (int i = 0; i < 4; i++) p.Weights[i] = _curW[i];
        for (int i = 0; i < FlowFrame.MaxArcs; i++)
        {
            var arc = _mixArcs[i];
            p.ArcPos[i] = arc.Pos; p.ArcWid[i] = arc.Width; p.ArcGain[i] = arc.Gain;
        }

        ctx.UpdateSubresource(in p, _cb!);
        ctx.RSSetState(_raster);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(null);
        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cb);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _cb);

        foreach (var strip in overlay.Strips)
        {
            if (!strip.HasArea) continue;
            ctx.OMSetRenderTargets(strip.RenderTarget);
            ctx.ClearRenderTargetView(strip.RenderTarget, strip.Clear);
            // Shift the monitor-sized viewport so this strip's pixels line up with the monitor picture.
            ctx.RSSetViewport(m.Bounds.Left - strip.Rect.Left, m.Bounds.Top - strip.Rect.Top, mw, mh);
            ctx.Draw(24, 0);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
        _wake.Dispose();
    }
}
