using System.Runtime.InteropServices;
using Rimlight.Core.Diagnostics;

namespace Rimlight.Shell;

/// <summary>
/// Temporarily switches on Windows' own taskbar auto-hide (the taskbar slides away, and comes back when the
/// mouse touches the screen edge), then restores the user's original setting. Uses the standard appbar API,
/// so the taskbar can never get stuck hidden; a marker file lets us restore after a crash.
/// </summary>
public static class TaskbarAutoHide
{
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge;
        public int left, top, right, bottom; public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);

    private const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    private const int ABS_AUTOHIDE = 1;

    private static readonly string Marker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rimlight", "taskbar-autohide-by-rimlight");

    private static bool _active;
    private static System.Windows.Threading.DispatcherTimer? _keepHidden;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    /// <summary>The main taskbar plus the taskbars on other monitors.</summary>
    private static IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        var h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) yield return h;
    }

    // Windows 11 often ignores an auto-hide change made by an app until the user touches the setting, so
    // the taskbar windows are also hidden directly (and re-hidden if Explorer brings them back).
    private static void HideWindows(bool hide)
    {
        foreach (var w in TaskbarWindows())
            if (IsWindowVisible(w) == hide) ShowWindow(w, hide ? 0 /* SW_HIDE */ : 5 /* SW_SHOW */);
    }

    private static APPBARDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = FindWindow("Shell_TrayWnd", null),
    };

    private static bool IsAutoHideOn()
    {
        var d = Data();
        return ((int)SHAppBarMessage(ABM_GETSTATE, ref d).ToUInt32() & ABS_AUTOHIDE) != 0;
    }

    private static void SetAutoHide(bool on)
    {
        var d = Data();
        int state = (int)SHAppBarMessage(ABM_GETSTATE, ref d).ToUInt32();
        d.lParam = new IntPtr(on ? state | ABS_AUTOHIDE : state & ~ABS_AUTOHIDE);
        SHAppBarMessage(ABM_SETSTATE, ref d);
    }

    /// <summary>Hide (auto-hide) the taskbar while lyrics play; restore when they stop.</summary>
    public static void Set(bool hide)
    {
        try
        {
            if (hide && !_active)
            {
                bool userAutoHide = IsAutoHideOn();
                if (!userAutoHide) SetAutoHide(true); // also frees the screen space the taskbar reserved
                _active = true;
                Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
                File.WriteAllText(Marker, userAutoHide ? "user" : "rimlight");
                HideWindows(true);
                _keepHidden ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _keepHidden.Tick -= KeepHidden;
                _keepHidden.Tick += KeepHidden;
                _keepHidden.Start();
                Log.Info("Taskbar hidden (lyrics showing)");
            }
            else if (!hide && _active)
            {
                _keepHidden?.Stop();
                HideWindows(false);
                bool weTurnedOnAutoHide = File.Exists(Marker) && File.ReadAllText(Marker).Trim() == "rimlight";
                if (weTurnedOnAutoHide) SetAutoHide(false);
                _active = false;
                try { File.Delete(Marker); } catch { }
                Log.Info("Taskbar restored");
            }
        }
        catch (Exception ex) { Log.Warn($"Taskbar auto-hide failed: {ex.Message}"); }
    }

    private static void KeepHidden(object? sender, EventArgs e)
    {
        if (_active) HideWindows(true);
    }

    /// <summary>On startup: if a previous run turned auto-hide on and didn't get to undo it, undo it now.</summary>
    public static void RestoreAfterCrash()
    {
        try
        {
            if (!File.Exists(Marker)) return;
            HideWindows(false);
            if (File.ReadAllText(Marker).Trim() != "user") SetAutoHide(false);
            File.Delete(Marker);
            Log.Info("Restored taskbar after an unexpected exit");
        }
        catch (Exception ex) { Log.Warn($"Taskbar restore failed: {ex.Message}"); }
    }
}
