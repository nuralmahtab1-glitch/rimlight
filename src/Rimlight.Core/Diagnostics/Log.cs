using System.Text;

namespace Rimlight.Core.Diagnostics;

/// <summary>
/// Tiny thread-safe rolling file logger. Keeps rimlight.log under ~1 MB by rotating
/// to rimlight.1.log (one backup). Never throws.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024; // x2 files ≈ 1 MB total
    private static readonly object Gate = new();
    private static string? _path;

    public static string? Directory { get; private set; }

    public static void Initialize(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            Directory = directory;
            _path = Path.Combine(directory, "rimlight.log");
        }
        catch { /* logging must never break the app */ }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId,2}] {message}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        if (_path is null) return;
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var backup = Path.Combine(fi.DirectoryName!, "rimlight.1.log");
                    File.Copy(_path, backup, overwrite: true);
                    File.Delete(_path);
                }
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch { }
        }
    }
}
