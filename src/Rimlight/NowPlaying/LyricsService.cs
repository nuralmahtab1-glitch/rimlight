using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rimlight.Core.Diagnostics;
using Rimlight.Core.Lyrics;

namespace Rimlight.NowPlaying;

/// <summary>Synced lyrics for one track (empty when none were found).</summary>
public sealed record LyricsResult(IReadOnlyList<LyricLine> Lines, bool Instrumental)
{
    public static readonly LyricsResult None = new(Array.Empty<LyricLine>(), false);
    public bool HasLyrics => Lines.Count > 0;
}

/// <summary>
/// Fetches time-synced lyrics from LRCLIB (https://lrclib.net — free, open, no account).
/// Only the track title, artist, album and length are sent. Results (including "not found") are cached
/// on disk so each song is looked up once.
/// </summary>
public sealed partial class LyricsService : IDisposable
{
    private static readonly TimeSpan NotFoundRetryAfter = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _cacheDir;

    public LyricsService(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri("https://lrclib.net/"),
            Timeout = TimeSpan.FromSeconds(8),
        };
        // LRCLIB asks clients to identify themselves; no personal data.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Rimlight/{App.VersionString} (desktop lyrics display)");
    }

    public async Task<LyricsResult> GetAsync(string title, string artist, string? album, double? durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return LyricsResult.None;
        var cacheFile = Path.Combine(_cacheDir, Key(artist, title) + ".json");

        // Cache hit (or a recent "not found").
        try
        {
            if (File.Exists(cacheFile))
            {
                var cached = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(cacheFile, ct));
                if (cached is not null && (cached.Synced is not null || DateTime.UtcNow - cached.FetchedUtc < NotFoundRetryAfter))
                    return ToResult(cached);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Lyrics cache unreadable: {ex.Message}"); }

        CacheEntry entry;
        try
        {
            entry = await FetchAsync(title, artist, album, durationSeconds, ct) ?? new CacheEntry(null, false, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Lyrics lookup failed ({ex.GetType().Name}: {ex.Message})");
            return LyricsResult.None; // don't cache network errors
        }

        try { await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(entry), ct); } catch { }
        Log.Info(entry.Synced is not null ? $"Lyrics found for \"{title}\"" : $"No synced lyrics for \"{title}\"");
        return ToResult(entry);
    }

    private async Task<CacheEntry?> FetchAsync(string title, string artist, string? album, double? duration, CancellationToken ct)
    {
        // 1) Exact match (best: uses album + duration to pick the right version).
        var q = $"api/get?track_name={E(title)}&artist_name={E(artist)}";
        if (!string.IsNullOrWhiteSpace(album)) q += $"&album_name={E(album)}";
        if (duration is > 0) q += $"&duration={Math.Round(duration.Value)}";
        var exact = await GetJsonAsync(q, ct);
        if (exact is { } e && Parse(e) is { } found) return found;

        // 2) Search with a cleaned-up title ("Song - Remastered 2011 (feat. X)" → "Song").
        var clean = CleanTitle(title);
        using var search = await GetDocAsync($"api/search?track_name={E(clean)}&artist_name={E(artist)}", ct);
        if (search?.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in search.RootElement.EnumerateArray())
            {
                if (duration is > 0 && item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    && Math.Abs(d.GetDouble() - duration.Value) > 6) continue; // different version
                if (Parse(item) is { Synced: not null } hit) return hit;
            }
        }
        return null;
    }

    private static CacheEntry? Parse(JsonElement e)
    {
        bool instrumental = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        string? synced = e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(synced) && !instrumental) return null;
        return new CacheEntry(string.IsNullOrWhiteSpace(synced) ? null : synced, instrumental, DateTime.UtcNow);
    }

    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var doc = await GetDocAsync(path, ct);
        return doc?.RootElement.Clone();
    }

    private async Task<JsonDocument?> GetDocAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static LyricsResult ToResult(CacheEntry e) =>
        new(LrcParser.Parse(e.Synced), e.Instrumental);

    private static string E(string s) => Uri.EscapeDataString(s.Trim());

    [GeneratedRegex(@"\s*(\(|\[)(feat\.?|ft\.?|with|prod\.?)[^)\]]*(\)|\])|\s+-\s+.*(remaster|version|edit|mix|live|mono|stereo).*$", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    public static string CleanTitle(string title) => Noise().Replace(title, "").Trim();

    private static string Key(string artist, string title)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}"));
        return Convert.ToHexString(bytes)[..20];
    }

    private sealed record CacheEntry(string? Synced, bool Instrumental, DateTime FetchedUtc);

    public void Dispose() => _http.Dispose();
}
