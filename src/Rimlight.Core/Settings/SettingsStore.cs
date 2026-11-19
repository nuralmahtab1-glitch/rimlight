using System.Text.Json;
using Rimlight.Core.Diagnostics;

namespace Rimlight.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Saves are debounced and atomic
/// (write to temp file, then replace) so a crash mid-write can't corrupt settings.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly Timer _debounce;
    private readonly object _gate = new();
    private AppSettings? _pending;

    public SettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json);
                if (s is not null) return s;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable; using defaults", ex);
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    /// <summary>Schedules a save ~400 ms from now (coalesces rapid slider changes).</summary>
    public void SaveSoon(AppSettings settings)
    {
        lock (_gate) _pending = settings.Clone();
        _debounce.Change(400, Timeout.Infinite);
    }

    public void Flush()
    {
        AppSettings? s;
        lock (_gate) { s = _pending; _pending = null; }
        if (s is null) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Error("Failed to save settings", ex); }
    }

    public void Dispose()
    {
        _debounce.Dispose();
        Flush();
    }
}
