using Rimlight.Core.Diagnostics;
using Rimlight.Interop;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace Rimlight.Overlay;

/// <summary>
/// The light on one monitor: four edge strips (top and bottom full width including the corners,
/// left and right in between). Each strip renders the same monitor-wide picture through an offset
/// viewport, so the strips join seamlessly. Strip depth follows the band extent, rounded up to steps
/// so dragging a slider doesn't resize the windows every frame.
/// </summary>
internal sealed class MonitorOverlay : IDisposable
{
    private const int Step = 48;

    public MonitorInfo Monitor { get; }
    public OverlayWindow[] Strips { get; } = new OverlayWindow[4];
    private Native.RECT _light;
    private int _depth = -1;

    public MonitorOverlay(MonitorInfo monitor, string windowClass, ID3D11Device device, IDXGIFactory2 factory, IDCompositionDevice dcomp)
    {
        Monitor = monitor;
        var b = monitor.Bounds;
        var placeholder = new Native.RECT { Left = b.Left, Top = b.Top, Right = b.Left + 1, Bottom = b.Top + 1 };
        for (int i = 0; i < 4; i++)
            Strips[i] = new OverlayWindow(placeholder, windowClass, device, factory, dcomp);
        Log.Info($"Overlay created on {monitor.DeviceId} {monitor.Bounds} scale {monitor.Scale:0.##}");
    }

    /// <summary>The window that paces animation for this monitor (top strip always has area).</summary>
    public OverlayWindow Pacer => Strips[0];

    /// <summary>Positions the strips around <paramref name="light"/> (screen px) deep enough for <paramref name="extent"/> px.</summary>
    public void Layout(Native.RECT light, float extent)
    {
        int maxDepth = Math.Max(1, Math.Min(light.Width, light.Height) / 2);
        int depth = Math.Min(maxDepth, (int)Math.Ceiling((extent + 4) / Step) * Step);
        if (depth == _depth && light.Left == _light.Left && light.Top == _light.Top && light.Right == _light.Right && light.Bottom == _light.Bottom)
            return;
        _depth = depth;
        _light = light;

        int midTop = light.Top + depth, midBottom = light.Bottom - depth;
        Strips[0].Resize(R(light.Left, light.Top, light.Right, midTop));                           // top (with corners)
        Strips[1].Resize(R(light.Left, Math.Max(midTop, midBottom), light.Right, light.Bottom));  // bottom (with corners)
        Strips[2].Resize(R(light.Left, midTop, light.Left + depth, midBottom));                    // left
        Strips[3].Resize(R(light.Right - depth, midTop, light.Right, midBottom));                  // right
    }

    private static Native.RECT R(int l, int t, int r, int b) =>
        new() { Left = l, Top = t, Right = Math.Max(l, r), Bottom = Math.Max(t, b) };

    public void SetVisible(bool visible)
    {
        foreach (var s in Strips) s.SetVisible(visible);
    }

    public void ReassertTopmost()
    {
        foreach (var s in Strips) s.ReassertTopmost();
    }

    public void Dispose()
    {
        foreach (var s in Strips) s?.Dispose();
    }
}
