using System.Runtime.InteropServices;
using Rimlight.Core.Diagnostics;

namespace Rimlight.Lyrics;

/// <summary>
/// Turns a window into a live desktop wallpaper: it sits behind the desktop icons and every app window
/// (the technique used by Wallpaper Engine / Lively). Handles both the classic WorkerW layout and the
/// Windows 11 24H2+ layout where the wallpaper WorkerW lives inside Progman. Falls back to a bottom-most,
/// never-activated window if the shell can't be found.
/// </summary>
internal static class WallpaperHost
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOZORDER = 0x4;
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);

    /// <summary>Primary monitor rect in physical pixels (screen coordinates).</summary>
    public static RECT PrimaryMonitor()
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(new POINT(), 1 /* MONITOR_DEFAULTTOPRIMARY */), ref mi);
        return mi.rcMonitor;
    }

    /// <summary>Attach <paramref name="hwnd"/> behind the desktop icons, covering the primary monitor. Returns false on fallback.</summary>
    public static bool Attach(IntPtr hwnd)
    {
        var mon = PrimaryMonitor();
        try
        {
            var progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                // Ask Explorer to create the wallpaper WorkerW (harmless if it already exists).
                SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0 /* SMTO_NORMAL */, 1000, out _);

                IntPtr host = IntPtr.Zero;
                // Classic layout: a top-level WorkerW right after the one that holds the desktop icons.
                EnumWindows((top, _) =>
                {
                    if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                        host = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    return host == IntPtr.Zero;
                }, IntPtr.Zero);
                // Windows 11 24H2+: the wallpaper WorkerW is a child of Progman.
                bool inProgman = false;
                if (host == IntPtr.Zero)
                {
                    host = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                    inProgman = host != IntPtr.Zero;
                }

                if (inProgman)
                {
                    // Windows 11 24H2+: the desktop (Progman) composes its children itself. Parent to Progman and
                    // slot in just below the icon layer (SHELLDLL_DefView), i.e. above the static wallpaper WorkerW.
                    var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                    int style = GetWindowLong(hwnd, GWL_STYLE);
                    SetWindowLong(hwnd, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
                    SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
                    SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */);
                    SetParent(hwnd, progman);
                    GetWindowRect(progman, out var pr);
                    // On these builds the icon layer also paints the wallpaper opaquely, so anything below it is
                    // hidden. Sit at the top of the desktop instead: still behind every app window.
                    SetWindowPos(hwnd, IntPtr.Zero /* HWND_TOP within Progman */, mon.Left - pr.Left, mon.Top - pr.Top,
                        mon.Right - mon.Left, mon.Bottom - mon.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    Log.Info("Lyrics wallpaper attached (desktop layer, above icons; 24H2 layout)");
                    return true;
                }

                if (host != IntPtr.Zero)
                {
                    int style = GetWindowLong(hwnd, GWL_STYLE);
                    SetWindowLong(hwnd, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
                    SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                    SetParent(hwnd, host);
                    // Child coordinates are relative to the host's top-left.
                    GetWindowRect(host, out var hr);
                    SetWindowPos(hwnd, IntPtr.Zero, mon.Left - hr.Left, mon.Top - hr.Top,
                        mon.Right - mon.Left, mon.Bottom - mon.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOZORDER);
                    Log.Info($"Lyrics wallpaper attached ({(inProgman ? "Progman/WorkerW, 24H2 layout" : "WorkerW")})");
                    return true;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Wallpaper attach failed: {ex.Message}"); }

        // Fallback: a normal window kept at the very bottom of the z-order.
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowPos(hwnd, HWND_BOTTOM, mon.Left, mon.Top, mon.Right - mon.Left, mon.Bottom - mon.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Log.Warn("Desktop wallpaper layer not found; lyrics shown as a bottom-most window instead");
        return false;
    }

    /// <summary>Re-pin a fallback window to the bottom (call if it was raised).</summary>
    public static void SendToBottom(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, 0x1 | 0x2 | SWP_NOACTIVATE);

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    private const uint GW_HWNDPREV = 3;

    /// <summary>
    /// "Behind your apps": a normal top-level window kept directly above the desktop (Progman) — below every
    /// app window, so it shows wherever the desktop would. Works on every Windows 10/11 build.
    /// Call periodically; returns true if it had to move.
    /// </summary>
    public static bool PlaceAboveDesktop(IntPtr hwnd)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) { SendToBottom(hwnd); return true; }
        var above = GetWindow(progman, GW_HWNDPREV);
        if (above == hwnd) return false; // already right above the desktop
        // Insert *after* the window currently above Progman = immediately above Progman.
        var after = above == IntPtr.Zero ? HWND_BOTTOM : above;
        var mon = PrimaryMonitor();
        SetWindowPos(hwnd, after, mon.Left, mon.Top, mon.Right - mon.Left, mon.Bottom - mon.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return true;
    }
}
