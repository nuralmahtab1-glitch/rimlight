using Rimlight.Core.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace Rimlight.Shell;

/// <summary>
/// Auto-updates from the project's GitHub Releases (Velopack). Checks at most once a day, downloads in the
/// background, and the update installs the next time Rimlight starts (or immediately via "Restart to update").
/// Never force-restarts. Does nothing in development builds that weren't installed by Setup.exe.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/nuralmahtab1-glitch/rimlight";

    // Created lazily: Velopack's startup hook (VelopackApp.Build().Run()) must run before any UpdateManager exists.
    private UpdateManager? _mgrField;
    private UpdateManager _mgr => _mgrField ??= new UpdateManager(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _ready;

    private static readonly string StampFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rimlight", "last-update-check");

    /// <summary>Raised (background thread) when an update has been downloaded; argument = new version.</summary>
    public event Action<string>? UpdateReady;

    public bool IsInstalled { get { try { return _mgr.IsInstalled; } catch { return false; } } }
    public bool HasUpdateReady => _ready is not null;

    public async Task CheckAsync(bool force = false)
    {
        try
        {
            if (!IsInstalled) return;
            if (!force && File.Exists(StampFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(StampFile) < TimeSpan.FromDays(1)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(StampFile)!);
            File.WriteAllText(StampFile, DateTime.UtcNow.ToString("O"));

            var info = await _mgr.CheckForUpdatesAsync();
            if (info is null) { Log.Info("Up to date"); return; }
            Log.Info($"Downloading update {info.TargetFullRelease.Version}");
            await _mgr.DownloadUpdatesAsync(info);
            _ready = info;
            UpdateReady?.Invoke(info.TargetFullRelease.Version.ToString());
        }
        catch (Exception ex) { Log.Warn($"Update check failed: {ex.Message}"); }
    }

    /// <summary>Install the downloaded update and restart Rimlight now.</summary>
    public void RestartToUpdate()
    {
        if (_ready is null) return;
        try { _mgr.ApplyUpdatesAndRestart(_ready.TargetFullRelease); }
        catch (Exception ex) { Log.Error("Applying update failed", ex); }
    }
}
