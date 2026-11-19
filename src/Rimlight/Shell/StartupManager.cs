using Microsoft.Win32;
using Rimlight.Core.Diagnostics;

namespace Rimlight.Shell;

/// <summary>"Launch at login" via the per-user Run key (no admin needed).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Rimlight";

    public static void Apply(bool enabled)
    {
#if DEBUG
        // Dev builds rely on DOTNET_ROOT and would show a ".NET missing" prompt at login. Only installed
        // (self-contained) builds register themselves.
        Log.Info($"Launch at login = {enabled} (not applied in development builds)");
#else
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\" --autostart");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) { Log.Error("Couldn't update launch-at-login", ex); }
#endif
    }
}
