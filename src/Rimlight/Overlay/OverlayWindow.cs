using Rimlight.Core.Diagnostics;
using Rimlight.Interop;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace Rimlight.Overlay;

/// <summary>
/// One click-through, topmost, never-activated window covering a strip along one screen edge, with a
/// premultiplied-alpha DirectComposition swap chain as its only content (no GDI redirection surface).
/// Strips (not one screen-sized window) matter: Windows treats a topmost window covering the whole
/// monitor as a "full-screen app" and silences notifications. Render-thread only.
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    public const uint ExStyle =
        Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
        Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | Native.WS_EX_NOREDIRECTIONBITMAP;

    private const SwapChainFlags Flags = SwapChainFlags.FrameLatencyWaitableObject;

    public IntPtr Hwnd { get; }
    /// <summary>Window rect in screen pixels.</summary>
    public Native.RECT Rect { get; private set; }
    public int Width => Math.Max(1, Rect.Width);
    public int Height => Math.Max(1, Rect.Height);
    /// <summary>False when the strip has no area in the current layout (kept hidden).</summary>
    public bool HasArea { get; private set; }

    private readonly ID3D11Device _device;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private ID3D11RenderTargetView? _rtv;
    private bool _wantVisible, _isVisible;

    public OverlayWindow(Native.RECT rect, string windowClass, ID3D11Device device,
        IDXGIFactory2 factory, IDCompositionDevice dcomp)
    {
        _device = device;
        Rect = rect;
        HasArea = rect.Width > 0 && rect.Height > 0;

        Hwnd = Native.CreateWindowEx(ExStyle, windowClass, "Rimlight Overlay", Native.WS_POPUP,
            rect.Left, rect.Top, Width, Height,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");

        // Layered + transparent = mouse input passes straight through to whatever is underneath.
        // Alpha 255 here; real per-pixel alpha comes from the composition swap chain.
        Native.SetLayeredWindowAttributes(Hwnd, 0, 255, Native.LWA_ALPHA);

        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Scaling = Scaling.Stretch,
            Flags = Flags,
        };
        _swapChain = factory.CreateSwapChainForComposition(device, desc, null);

        // Latency of one frame: the render loop waits on this handle, then draws with the freshest audio values.
        using (var sc2 = _swapChain.QueryInterface<IDXGISwapChain2>())
        {
            sc2.MaximumFrameLatency = 1;
            FrameWaitHandle = sc2.FrameLatencyWaitableObject;
        }
        CreateTarget();

        dcomp.CreateTargetForHwnd(Hwnd, true, out _target).CheckError();
        _visual = dcomp.CreateVisual();
        _visual.SetContent(_swapChain);
        _target.SetRoot(_visual);
    }

    private void CreateTarget()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);
    }

    public ID3D11RenderTargetView RenderTarget => _rtv!;

    /// <summary>Signalled when the swap chain can accept the next frame (paces animation to the display).</summary>
    public IntPtr FrameWaitHandle { get; }

    public Vortice.Mathematics.Color4 Clear => new(0, 0, 0, 0);

    /// <summary>Moves/resizes the strip. An empty rect hides it.</summary>
    public void Resize(Native.RECT rect)
    {
        if (rect.Left == Rect.Left && rect.Top == Rect.Top && rect.Width == Rect.Width && rect.Height == Rect.Height) return;
        bool sizeChanged = Math.Max(1, rect.Width) != Width || Math.Max(1, rect.Height) != Height;
        Rect = rect;
        HasArea = rect.Width > 0 && rect.Height > 0;
        if (sizeChanged)
        {
            _rtv?.Dispose();
            _rtv = null;
            _swapChain.ResizeBuffers(2, (uint)Width, (uint)Height, Format.B8G8R8A8_UNorm, Flags).CheckError();
            CreateTarget();
        }
        ApplyVisibility(force: true);
    }

    /// <summary>Sync interval 1 paces to the display; 0 doesn't wait.</summary>
    public SharpGen.Runtime.Result Present(uint syncInterval) => _swapChain.Present(syncInterval, PresentFlags.None);

    public void SetVisible(bool visible)
    {
        _wantVisible = visible;
        ApplyVisibility(force: false);
    }

    private void ApplyVisibility(bool force)
    {
        bool show = _wantVisible && HasArea;
        if (show == _isVisible && !force) return;
        _isVisible = show;
        if (show)
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, Rect.Left, Rect.Top, Width, Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
        else
            Native.ShowWindow(Hwnd, Native.SW_HIDE);
    }

    /// <summary>Other topmost windows (e.g. the taskbar) can end up above us; push back to the top.</summary>
    public void ReassertTopmost()
    {
        if (_isVisible)
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    public void Dispose()
    {
        try
        {
            _rtv?.Dispose();
            _visual.Dispose();
            _target.Dispose();
            _swapChain.Dispose();
            if (FrameWaitHandle != IntPtr.Zero) CloseHandle(FrameWaitHandle);
        }
        catch (Exception ex) { Log.Error("Overlay dispose", ex); }
        Native.DestroyWindow(Hwnd);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
