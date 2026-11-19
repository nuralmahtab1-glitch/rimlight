using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Rimlight.Core.Diagnostics;

namespace Rimlight.Shell;

/// <summary>
/// Watches Windows for moments when the light should rest or rebuild:
/// session lock/unlock, monitor power off/on, sleep/resume, Battery Saver, and full-screen apps
/// (videos, games, presentations). All events are raised on the UI thread.
/// </summary>
public sealed class SystemWatcher : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    // SHQueryUserNotificationState results
    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING { public Guid PowerSetting; public uint DataLength; public byte Data; }

    private readonly Dispatcher _dispatcher;
    private readonly HwndSource _window;
    private readonly IntPtr _displayNotify;
    private readonly DispatcherTimer _fullscreenPoll;

    public SystemWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _window = new HwndSource(new HwndSourceParameters("RimlightPowerEvents") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        var guid = GUID_CONSOLE_DISPLAY_STATE;
        _displayNotify = RegisterPowerSettingNotification(_window.Handle, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        try
        {
            IsEnergySaver = Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
            Windows.System.Power.PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged;
        }
        catch (Exception ex) { Log.Warn($"Battery Saver status unavailable: {ex.Message}"); }

        _fullscreenPoll = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1.5) };
        _fullscreenPoll.Tick += (_, _) => PollFullscreen();
        _fullscreenPoll.Start();
        PollFullscreen();
    }

    /// <summary>Something changed that affects whether/how the light should run.</summary>
    public event Action? Changed;

    /// <summary>The PC woke from sleep/hibernate: GPU and audio devices may need rebuilding.</summary>
    public event Action? Resumed;

    public bool IsLocked { get; private set; }
    public bool IsDisplayOff { get; private set; }
    public bool IsSleeping { get; private set; }
    public bool IsEnergySaver { get; private set; }
    /// <summary>A full-screen app (video, game, presentation) is in front.</summary>
    public bool IsFullscreenAppActive { get; private set; }

    /// <summary>True when nobody can see the light, so it should stop drawing and listening.</summary>
    public bool ShouldRest => IsLocked || IsDisplayOff || IsSleeping;

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect => true,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect => false,
            _ => IsLocked,
        };
        _dispatcher.BeginInvoke(() => Set(() => IsLocked, v => IsLocked = v, locked, locked ? "Session locked: resting" : "Session unlocked"));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (e.Mode == PowerModes.Suspend)
                Set(() => IsSleeping, v => IsSleeping = v, true, "Going to sleep");
            else if (e.Mode == PowerModes.Resume)
            {
                Set(() => IsSleeping, v => IsSleeping = v, false, "Woke from sleep");
                Resumed?.Invoke();
            }
        });
    }

    private void OnEnergySaverChanged(object? sender, object e)
    {
        bool on;
        try { on = Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; }
        catch { return; }
        _dispatcher.BeginInvoke(() => Set(() => IsEnergySaver, v => IsEnergySaver = v, on, on ? "Battery Saver on: lighter animation" : "Battery Saver off"));
    }

    private void PollFullscreen()
    {
        bool fullscreen = false;
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0)
                fullscreen = state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch { }
        Set(() => IsFullscreenAppActive, v => IsFullscreenAppActive = v, fullscreen,
            fullscreen ? "Full-screen app in front" : "Full-screen app gone");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
            {
                bool off = setting.Data == 0; // 0 = off, 1 = on, 2 = dimmed (still visible)
                Set(() => IsDisplayOff, v => IsDisplayOff = v, off, off ? "Display turned off: resting" : "Display on");
            }
        }
        return IntPtr.Zero;
    }

    private void Set(Func<bool> get, Action<bool> set, bool value, string logMessage)
    {
        if (get() == value) return;
        set(value);
        Log.Info(logMessage);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _fullscreenPoll.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { Windows.System.Power.PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged; } catch { }
        if (_displayNotify != IntPtr.Zero) UnregisterPowerSettingNotification(_displayNotify);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
