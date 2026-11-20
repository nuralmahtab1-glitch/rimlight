using Rimlight.Core.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Rimlight.NowPlaying;

/// <summary>What's playing right now (immutable snapshot).</summary>
public sealed record NowPlayingInfo(
    string AppId,
    string PlayerName,
    string Title,
    string Artist,
    bool IsPlaying,
    byte[]? ArtBgra64,      // 64×64 BGRA, for palette extraction
    byte[]? ArtBgra96,      // 96×96 BGRA, for the panel thumbnail
    string Album = "",
    double? DurationSeconds = null,
    byte[]? ArtBgraLarge = null) // 320×320 BGRA, for lyric styles that show the cover
{
    public string TrackKey => $"{AppId}|{Title}|{Artist}";
}

/// <summary>Playback position anchor: the track was at <see cref="Position"/> s at clock time <see cref="At"/>.</summary>
public sealed record PlaybackClock(string TrackKey, double Position, double At, bool Playing)
{
    public double Now() => Playing ? Position + (MonoClock.Now - At) : Position;
}

/// <summary>
/// Follows Windows' media sessions (the same source as the volume-key media flyout), so it works with
/// Spotify, Apple Music, Media Player, browsers, etc. Reads title, artist and the artwork thumbnail
/// locally. Picks a session according to the preferred player ("Auto": a playing session, Spotify wins ties).
/// Every failure is caught and logged — music apps opening/closing must never crash Rimlight.
/// </summary>
public sealed class MediaSessionWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly System.Threading.Timer _debounce;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<GlobalSystemMediaTransportControlsSession> _hooked = new();
    private string _preferred = "Auto";
    private string? _lastArtKey;
    private byte[]? _art64, _art96, _artLarge;
    public const int LargeArtSize = 640;
    private bool _disposed;

    public MediaSessionWatcher()
    {
        _debounce = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised (on a background thread) when the track, play state or artwork changes. Null = nothing playing.</summary>
    public event Action<NowPlayingInfo?>? Changed;

    /// <summary>Raised when the set of media apps changes: (appId, friendly name) pairs.</summary>
    public event Action<IReadOnlyList<(string AppId, string Name)>>? PlayersChanged;

    public NowPlayingInfo? Current { get; private set; }

    /// <summary>Latest playback position of the chosen session (null when nothing is playing). Thread-safe read.</summary>
    public PlaybackClock? Clock => _clock;
    private volatile PlaybackClock? _clock;
    private GlobalSystemMediaTransportControlsSession? _chosen;

    public string PreferredPlayer
    {
        get => _preferred;
        set { _preferred = string.IsNullOrWhiteSpace(value) ? "Auto" : value; ScheduleRefresh(50); }
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => { HookSessions(); ScheduleRefresh(150); };
            _manager.CurrentSessionChanged += (_, _) => ScheduleRefresh(150);
            HookSessions();
            await RefreshAsync();
            Log.Info("Media session watcher started");
        }
        catch (Exception ex)
        {
            Log.Warn($"Media sessions unavailable ({ex.Message}); album colors disabled");
        }
    }

    private void HookSessions()
    {
        if (_manager is null) return;
        lock (_gate)
        {
            foreach (var s in _hooked)
            {
                try
                {
                    s.MediaPropertiesChanged -= OnSessionChanged; s.PlaybackInfoChanged -= OnSessionChanged;
                    s.TimelinePropertiesChanged -= OnTimelineChanged;
                }
                catch { }
            }
            _hooked.Clear();
            try
            {
                foreach (var s in _manager.GetSessions())
                {
                    s.MediaPropertiesChanged += OnSessionChanged;
                    s.PlaybackInfoChanged += OnSessionChanged;
                    s.TimelinePropertiesChanged += OnTimelineChanged;
                    _hooked.Add(s);
                }
            }
            catch (Exception ex) { Log.Warn($"Couldn't list media sessions: {ex.Message}"); }
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        // Play/pause changes the clock immediately (lyrics freeze/resume), then a full refresh follows.
        if (ReferenceEquals(sender, _chosen)) UpdateClock(sender);
        ScheduleRefresh(250);
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        if (ReferenceEquals(sender, _chosen)) UpdateClock(sender);
    }

    /// <summary>Re-anchors the playback clock from the session's timeline (seek, play, pause, periodic updates).</summary>
    private void UpdateClock(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var cur = Current;
            if (cur is null) { _clock = null; return; }
            var tl = session.GetTimelineProperties();
            bool playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double pos = (tl.Position - tl.StartTime).TotalSeconds;
            // Position was sampled at LastUpdatedTime; carry it forward to "now" if playing.
            double age = Math.Clamp((DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds, 0, 3600);
            if (tl.LastUpdatedTime.Year < 2000) age = 0; // player doesn't report it
            _clock = new PlaybackClock(cur.TrackKey, Math.Max(0, pos), MonoClock.Now - (playing ? age : 0), playing);
        }
        catch (Exception ex) { Log.Warn($"Timeline read failed: {ex.Message}"); }
    }

    // Players often fire several events per track change (title first, artwork a moment later): coalesce.
    private void ScheduleRefresh(int ms)
    {
        if (!_disposed) _debounce.Change(ms, Timeout.Infinite);
    }

    private async Task RefreshAsync()
    {
        if (_manager is null || _disposed) return;
        if (!await _refreshLock.WaitAsync(0)) { ScheduleRefresh(200); return; }
        try
        {
            var sessions = _manager.GetSessions().ToList();
            PlayersChanged?.Invoke(sessions
                .Select(s => SafeAppId(s))
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => (id, FriendlyName(id)))
                .ToList());

            var session = Choose(sessions);
            _chosen = session;
            if (session is null)
            {
                Publish(null);
                _clock = null;
                return;
            }

            var appId = SafeAppId(session);
            var props = await session.TryGetMediaPropertiesAsync();
            bool playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            string title = props?.Title?.Trim() ?? "";
            string artist = (props?.Artist ?? props?.AlbumArtist ?? "").Trim();
            if (title.Length == 0 && artist.Length == 0)
            {
                Publish(null);
                _clock = null;
                return;
            }
            string album = props?.AlbumTitle?.Trim() ?? "";
            double? duration = null;
            try
            {
                var tl = session.GetTimelineProperties();
                var d = (tl.EndTime - tl.StartTime).TotalSeconds;
                if (d > 1) duration = d;
            }
            catch { }

            // Re-read artwork only when the track changes (or it was missing last time — it can arrive late).
            var artKey = $"{appId}|{title}|{artist}";
            if (artKey != _lastArtKey || _art64 is null)
            {
                (_art64, _art96, _artLarge) = props?.Thumbnail is { } thumb ? await ReadArtAsync(thumb) : (null, null, null);
                _lastArtKey = artKey;
                Log.Info($"Now playing: \"{title}\" by {(artist.Length > 0 ? artist : "?")} in {FriendlyName(appId)}{(_art64 is null ? " (no artwork)" : "")}");
                // Artwork sometimes lags the title; look again shortly.
                if (_art64 is null) ScheduleRefresh(1500);
            }

            var info = new NowPlayingInfo(appId, FriendlyName(appId), title, artist, playing, _art64, _art96, album, duration, _artLarge);
            Publish(info);
            UpdateClock(session);
        }
        catch (Exception ex)
        {
            Log.Warn($"Now-playing refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Publish(NowPlayingInfo? info)
    {
        var prev = Current;
        if (prev == info) return;
        if (prev is not null && info is not null && prev.TrackKey == info.TrackKey && prev.IsPlaying == info.IsPlaying
            && ReferenceEquals(prev.ArtBgra64, info.ArtBgra64) && prev.DurationSeconds == info.DurationSeconds) return;
        Current = info;
        Changed?.Invoke(info);
    }

    private GlobalSystemMediaTransportControlsSession? Choose(List<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (sessions.Count == 0) return null;

        if (!string.Equals(_preferred, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            var match = sessions.FirstOrDefault(s => string.Equals(SafeAppId(s), _preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            // Preferred player not open: fall through to Auto.
        }

        static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }
        static bool IsSpotify(GlobalSystemMediaTransportControlsSession s) => SafeAppId(s).Contains("spotify", StringComparison.OrdinalIgnoreCase);

        var playing = sessions.Where(IsPlaying).ToList();
        return playing.FirstOrDefault(IsSpotify)                 // Spotify wins ties
            ?? playing.FirstOrDefault()
            ?? sessions.FirstOrDefault(IsSpotify)                 // nothing playing: show paused Spotify
            ?? SafeCurrent()
            ?? sessions[0];
    }

    private GlobalSystemMediaTransportControlsSession? SafeCurrent()
    {
        try { return _manager?.GetCurrentSession(); } catch { return null; }
    }

    private static string SafeAppId(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.SourceAppUserModelId ?? ""; } catch { return ""; }
    }

    public static string FriendlyName(string appId)
    {
        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("applemusic") || id.Contains("appleinc")) return "Apple Music";
        if (id.Contains("zunemusic") || id.Contains("microsoft.media.player")) return "Media Player";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("msedge") || id.Contains("microsoftedge")) return "Edge";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("tidal")) return "TIDAL";
        if (id.Contains("deezer")) return "Deezer";
        if (id.Contains("amazon")) return "Amazon Music";
        if (id.Contains("youtube")) return "YouTube Music";
        // "Foo.exe" or "Publisher.App_hash!App" → "Foo" / "App"
        var name = appId;
        int bang = name.LastIndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length == 0 ? "Media app" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static async Task<(byte[]?, byte[]?, byte[]?)> ReadArtAsync(IRandomAccessStreamReference thumb)
    {
        try
        {
            using var stream = await thumb.OpenReadAsync();
            if (stream.Size == 0) return (null, null, null);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return (await Pixels(decoder, 64), await Pixels(decoder, 96), await Pixels(decoder, LargeArtSize));
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read artwork: {ex.Message}");
            return (null, null, null);
        }

        static async Task<byte[]> Pixels(BitmapDecoder d, uint size)
        {
            var transform = new BitmapTransform { ScaledWidth = size, ScaledHeight = size, InterpolationMode = BitmapInterpolationMode.Fant };
            var data = await d.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            return data.DetachPixelData();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Dispose();
        lock (_gate)
        {
            foreach (var s in _hooked)
            {
                try
                {
                    s.MediaPropertiesChanged -= OnSessionChanged; s.PlaybackInfoChanged -= OnSessionChanged;
                    s.TimelinePropertiesChanged -= OnTimelineChanged;
                }
                catch { }
            }
            _hooked.Clear();
        }
    }
}
