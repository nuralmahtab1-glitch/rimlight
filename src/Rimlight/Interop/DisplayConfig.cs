using System.Runtime.InteropServices;

namespace Rimlight.Interop;

/// <summary>
/// Windows Display Configuration API (CCD). Maps GDI device names (\\.\DISPLAY1) to the
/// monitor's friendly name (e.g. "DELL U2720Q") and its connector type (built-in panel vs external).
/// </summary>
internal static class DisplayConfig
{
    public readonly record struct Info(string FriendlyName, bool IsInternal);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values that mean "built into the device".
    private const uint TECH_LVDS = 6, TECH_DISPLAYPORT_EMBEDDED = 11, TECH_UDI_EMBEDDED = 13, TECH_INTERNAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public LUID AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public LUID AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology;
        public uint Rotation; public uint Scaling; public uint RefreshNum, RefreshDen;
        public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSourceInfo Source; public PathTargetInfo Target; public uint Flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo { public uint InfoType; public uint Id; public LUID AdapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public uint Type; public uint Size; public LUID AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public Header Header;
        public uint Flags; public uint OutputTechnology;
        public ushort EdidManufactureId; public ushort EdidProductCodeId; public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths,
        ref uint numModes, [Out] ModeInfo[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName req);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetName req);

    /// <summary>GDI device name → info. Empty on any failure (callers fall back gracefully).</summary>
    public static Dictionary<string, Info> Query()
    {
        var result = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return result;
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;

            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new SourceName
                {
                    Header = new Header { Type = GET_SOURCE_NAME, Size = (uint)Marshal.SizeOf<SourceName>(), AdapterId = p.Source.AdapterId, Id = p.Source.Id },
                };
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                var tgt = new TargetName
                {
                    Header = new Header { Type = GET_TARGET_NAME, Size = (uint)Marshal.SizeOf<TargetName>(), AdapterId = p.Target.AdapterId, Id = p.Target.Id },
                };
                bool ok = DisplayConfigGetDeviceInfo(ref tgt) == 0;
                uint tech = ok ? tgt.OutputTechnology : p.Target.OutputTechnology;
                bool isInternal = tech is TECH_INTERNAL or TECH_DISPLAYPORT_EMBEDDED or TECH_UDI_EMBEDDED or TECH_LVDS;
                var name = ok ? tgt.MonitorFriendlyDeviceName?.Trim() : null;
                if (string.IsNullOrEmpty(name)) name = isInternal ? "Built-in display" : "Generic PnP Monitor";

                result.TryAdd(src.GdiDeviceName, new Info(name, isInternal));
            }
        }
        catch (Exception ex) { Rimlight.Core.Diagnostics.Log.Error("DisplayConfig query failed", ex); }
        return result;
    }
}
