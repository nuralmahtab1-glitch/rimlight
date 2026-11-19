using Rimlight.Interop;

namespace Rimlight.Overlay;

/// <summary>One physical display. Rects are in physical pixels (process is Per-Monitor-V2 DPI aware).</summary>
internal sealed record MonitorInfo(
    string DeviceId,          // e.g. \\.\DISPLAY1, stable for the session
    Native.RECT Bounds,
    Native.RECT WorkArea,
    bool IsPrimary,
    float Scale)              // DPI / 96
{
    /// <summary>e.g. "DELL U2720Q", "Built-in display", "Generic PnP Monitor".</summary>
    public string FriendlyName { get; init; } = DeviceId;

    /// <summary>True for a laptop's built-in panel (used to guess rounded corners).</summary>
    public bool IsInternal { get; init; }

    /// <summary>"Auto" corner radius: laptop panels have rounded corners, external monitors are square.</summary>
    public float AutoCornerRadiusDip => IsInternal ? 10f : 0f;
}

internal static class Monitors
{
    public static List<MonitorInfo> Enumerate()
    {
        var names = DisplayConfig.Query();
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
            {
                float scale = 1f;
                if (Native.GetDpiForMonitor(h, 0 /* MDT_EFFECTIVE_DPI */, out var dx, out _) == 0 && dx > 0)
                    scale = dx / 96f;
                names.TryGetValue(mi.szDevice, out var info);
                list.Add(new MonitorInfo(mi.szDevice, mi.rcMonitor, mi.rcWork,
                    (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, scale)
                {
                    FriendlyName = info.FriendlyName ?? "Display",
                    IsInternal = info.IsInternal,
                });
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
