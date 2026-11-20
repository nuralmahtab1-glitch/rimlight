using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rimlight.Core.Audio;
using Rimlight.Core.Diagnostics;
using Rimlight.Core.Lyrics;
using Rimlight.Core.Settings;
using Rimlight.NowPlaying;

namespace Rimlight.Lyrics;

/// <summary>
/// Glue between the current track, LRCLIB lyrics, the playback clock, the beat, and <see cref="LyricsWindow"/>.
/// UI thread only.
/// </summary>
public sealed class LyricsController : IDisposable
{
    /// <summary>Show lyrics slightly early so text appears as the singer starts (render + player latency).</summary>
    private const double Lead = 0.25;

    private readonly LyricsService _service;
    private readonly MediaSessionWatcher _media;
    private readonly Func<AudioFrame> _audio;
    private readonly LyricsWindow _window = new();
    // 20 Hz is plenty to start each word on time (the fly-in animation itself runs at full frame rate).
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };

    private NowPlayingInfo? _track;
    private LyricsTimeline? _timeline;
    private CancellationTokenSource? _fetch;
    private bool _enabled = true, _suspended;
    private string _status = "";

    public LyricsController(LyricsService service, MediaSessionWatcher media, Func<AudioFrame> audio)
    {
        _service = service;
        _media = media;
        _audio = audio;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Short human text for the settings panel ("Lyrics found", "No lyrics for this song", …).</summary>
    public event Action<string>? StatusChanged;

    // Full-screen stage (Album / Constellation); null when a small style is selected.
    private StageWindow? _stage;
    private (StageWindow.Look Look, bool OnTop, double Size, string Picture)? _stageKey;

    public static string DefaultPicture => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rimlight", "backgrounds", "default.png");
    private Color _colA = Color.FromRgb(0xFF, 0x2E, 0x88), _colB = Color.FromRgb(0x7B, 0x3C, 0xFF);
    private ImageSource? _artSharp, _artBlurScreen;

    /// <summary>Whichever display the current style uses.</summary>
    private ILyricsDisplay Display => _stage is not null ? _stage : _window;

    public void ApplySettings(AppSettings s, bool suspended)
    {
        bool wasEnabled = _enabled;
        _enabled = s.ShowLyrics;
        _suspended = suspended;
        _hideWhileUsing = s.HideLyricsWhileUsingPc;

        StageWindow.Look? look = s.LyricsStyle switch
        {
            LyricsStyle.Album or LyricsStyle.FullScreen => StageWindow.Look.Album,
            LyricsStyle.Constellation => StageWindow.Look.Constellation,
            LyricsStyle.Picture or LyricsStyle.ChatPicture or LyricsStyle.ChatBlack => StageWindow.Look.Picture,
            _ => null,
        };
        if (look is { } l && _enabled)
        {
            string pic = string.IsNullOrWhiteSpace(s.LyricsPicture) ? DefaultPicture : s.LyricsPicture;
            StageWindow.PictureBoxPlacement = s.LyricsBoxX < 0 ? null : new System.Windows.Point(s.LyricsBoxX, s.LyricsBoxY);
            StageWindow.MotionFactor = StageWindow.MotionFromLevel(s.BackgroundMotion);
            string placeKey = (s.LyricsBoxX < 0 ? "auto" : $"{s.LyricsBoxX:0.00},{s.LyricsBoxY:0.00}") + $"|m{Math.Round(s.BackgroundMotion)}";
            var key = (l, s.LyricsOnTop, Math.Round(s.LyricsSize), (l == StageWindow.Look.Picture ? pic : "") + "|" + placeKey + (s.LyricsStyle == LyricsStyle.ChatPicture ? "|chat" : s.LyricsStyle == LyricsStyle.ChatBlack ? "|black" : ""));
            if (_stageKey != key)
            {
                // Recreate: transparency/wallpaper mode can only be chosen when a window is created.
                _stage?.Close();
                _stage = new StageWindow(l, s.LyricsOnTop, s.LyricsSize, pic, s.LyricsStyle is LyricsStyle.ChatPicture or LyricsStyle.ChatBlack, s.LyricsStyle == LyricsStyle.ChatBlack);
                _stage.SetColors(_colA, _colB);
                _stage.SetArt(_artSharp, _artBlurScreen);
                _stage.SetTrack(_track?.Title, _track?.Artist);
                _stageKey = key;
                _window.SetShown(false, false);
            }
        }
        else
        {
            _stage?.Close();
            _stage = null;
            _stageKey = null;
            _window.ApplyStyle(s.LyricsStyle, s.LyricsPlacement, s.LyricsSize);
        }

        if (_enabled && !wasEnabled && _track is not null && _timeline is null) OnTrack(_track); // turned on: fetch now
        UpdateTimer();
        if (!_enabled) SetStatus("Lyrics are off");
        else if (_track is null) SetStatus("Play a song to see its lyrics");
    }

    public void SetColors(Color a, Color b)
    {
        _colA = a; _colB = b;
        _window.SetColors(a, b);
        _stage?.SetColors(a, b);
    }

    /// <summary>Called when the track (or its artwork) changes.</summary>
    public void OnTrack(NowPlayingInfo? info)
    {
        if (!double.IsNaN(_demoStart))
        {
            // A preview is playing: keep the cover up to date, apply the real track when the preview ends.
            if (!ReferenceEquals(info?.ArtBgraLarge, _artShown)) { _artShown = info?.ArtBgraLarge; _ = PrepareArtAsync(_artShown); }
            return;
        }
        if (!ReferenceEquals(info?.ArtBgraLarge, _artShown)) { _artShown = info?.ArtBgraLarge; _ = PrepareArtAsync(_artShown); }
        bool sameSong = info is not null && _track is not null && info.TrackKey == _track.TrackKey;
        _track = info;
        _stage?.SetTrack(info?.Title, info?.Artist);

        if (sameSong && (_timeline is not null || _fetch is not null)) { UpdateTimer(); return; }

        _fetch?.Cancel();
        _fetch = null;
        _timeline = null;
        Display.ShowLine(null, 0, null, gap: false);

        if (info is null) { SetStatus("Play a song to see its lyrics"); UpdateTimer(); return; }
        if (!_enabled) { UpdateTimer(); return; }

        SetStatus("Looking up lyrics…");
        var cts = new CancellationTokenSource();
        _fetch = cts;
        _ = FetchAsync(info, cts);
        UpdateTimer();
    }

    private async Task FetchAsync(NowPlayingInfo info, CancellationTokenSource cts)
    {
        try
        {
            var result = await _service.GetAsync(info.Title, info.Artist, info.Album, info.DurationSeconds, cts.Token);
            if (cts.IsCancellationRequested || _track?.TrackKey != info.TrackKey) return;
            _timeline = result.HasLyrics ? new LyricsTimeline(result.Lines) : null;
            SetStatus(result.HasLyrics ? "Lyrics found for this song"
                : result.Instrumental ? "Instrumental: no lyrics"
                : "No synced lyrics for this song");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Lyrics fetch", ex); SetStatus("Couldn't load lyrics"); }
        finally
        {
            if (ReferenceEquals(_fetch, cts)) _fetch = null;
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        if (!double.IsNaN(_demoStart)) return; // demo drives itself
        // The album wallpaper stays up (cover only) even when a song has no lyrics.
        bool want = _enabled && !_suspended && _track is not null && (_timeline is not null || IsAlbumStage);
        if (want && !_timer.IsEnabled) _timer.Start();
        if (!want)
        {
            _timer.Stop();
            _window.SetShown(false, false);
            _stage?.SetShown(false);
            ReportVisible(false);
        }
    }

    private bool _visible;

    /// <summary>Raised when lyrics appear on / disappear from the screen (used to auto-hide the taskbar).</summary>
    public event Action<bool>? VisibleChanged;

    private void ReportVisible(bool visible)
    {
        if (visible == _visible) return;
        _visible = visible;
        VisibleChanged?.Invoke(visible);
    }

    // Album and Picture backgrounds stay up (with the beat) even when a song has no lyrics.
    private bool IsAlbumStage => _stageKey?.Look is StageWindow.Look.Album or StageWindow.Look.Picture;

    /// <summary>Beat pulse for the full-screen wallpaper (cheap there: it's hardware-rendered and opaque).</summary>
    private void StageBeat(bool playing)
    {
        if (_stage is null || _stageKey?.OnTop == true) return;
        var a = _audio();
        double now = MonoClock.Now;
        float flash = playing && a.Active && now - a.Time < 1.0
            ? (float)Math.Exp(-Math.Max(0, now - a.BeatTime) / 0.16) * Math.Max(0.4f, a.BeatStrength)
            : 0f;
        _stage.SetBeat(flash);
    }

    // ------------------------------------------------------------------ demo (preview styles without a song)

    private const string DemoLrc = """
        [00:00.50] Turn the lights down low tonight
        [00:03.20] Every color moving to the beat
        [00:06.00] Feel the bass light up the room
        [00:08.80] Glowing edges all around me
        [00:11.60] We don't stop until the morning
        [00:14.40] This is how the music looks
        [00:17.20]
        """;

    private double _demoStart = double.NaN;
    private byte[]? _artShown;

    /// <summary>Plays sample lyrics (with a simulated 120 BPM beat) so styles can be previewed.</summary>
    public void StartDemo()
    {
        _demoStart = MonoClock.Now;
        _track = new NowPlayingInfo("demo", "Demo", "Rimlight demo", "Rimlight", true, null, null);
        _timeline = new LyricsTimeline(LrcParser.Parse(DemoLrc));
        Display.ShowLine(null, 0, null, gap: false);
        _stage?.SetTrack(_media.Current?.Title ?? "Rimlight", _media.Current?.Artist ?? "Lyrics preview");
        SetStatus("Showing a preview");
        if (!_timer.IsEnabled) _timer.Start();
    }

    private bool DemoTick()
    {
        if (double.IsNaN(_demoStart)) return false;
        double t = MonoClock.Now - _demoStart;
        if (t > 19 || !_enabled || _timeline is null)
        {
            ReportVisible(false);
            _demoStart = double.NaN;
            _timeline = null;
            _track = null;
            UpdateTimer();
            OnTrack(_media.Current);
            return true;
        }
        var at = _timeline.At(t + Lead);
        Display.ShowLine(at.Index >= 0 ? _timeline.Lines[at.Index] : null, at.WordsVisible, _timeline.NextLine(at.Index), at.IsGap);
        Display.SetShown(true, false);
        ReportVisible(true);
        _stage?.SetBeat(_stageKey?.OnTop == true ? 0 : (float)Math.Exp(-(t % 0.5) / 0.16));
        return true;
    }

    // ------------------------------------------------------------------ hide while using the PC

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private const double ComeBackAfterSeconds = 10;
    private bool _hideWhileUsing = true;
    private bool _awayForInput;

    /// <summary>Seconds since the last mouse move / click / key press anywhere in Windows.</summary>
    private static double IdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return double.MaxValue;
        return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
    }

    /// <summary>True while lyrics should step aside because you're using the PC.</summary>
    private bool AwayForInput()
    {
        if (!_hideWhileUsing) return _awayForInput = false;
        double idle = IdleSeconds();
        if (!_awayForInput && idle < 0.3) _awayForInput = true;                    // mouse moved / key pressed
        else if (_awayForInput && idle >= ComeBackAfterSeconds) _awayForInput = false; // untouched for 10 s
        return _awayForInput;
    }

    private void Tick()
    {
        if (DemoTick()) return;
        if (AwayForInput())
        {
            Display.SetShown(false, false);
            ReportVisible(false); // gives the taskbar back too
            return;
        }
        var timeline = _timeline;
        var track = _track;
        var clock = _media.Clock;
        bool clockOk = clock is not null && track is not null && clock.TrackKey == track.TrackKey;

        if (timeline is null || !clockOk)
        {
            // No lyrics (or no position yet): the album wallpaper still shows the cover and pumps to the beat.
            if (IsAlbumStage && track is not null)
            {
                _stage!.SetShown(true);
                ReportVisible(true);
                StageBeat(clock?.Playing ?? true);
            }
            else { Display.SetShown(false, false); ReportVisible(false); }
            return;
        }

        double pos = clock!.Now() + Lead;
        var at = timeline.At(pos);
        var line = at.Index >= 0 ? timeline.Lines[at.Index] : null;
        var next = timeline.NextLine(at.Index);
        // Before the first line: show what's coming as the "next" line, with the ♪ note.
        Display.ShowLine(line, at.WordsVisible, next, at.IsGap);
        Display.SetShown(true, dim: !clock.Playing);
        ReportVisible(true);
        if (track!.DurationSeconds is > 0 and var d) _stage?.SetProgress(clock.Now() / d);
        StageBeat(clock.Playing);

        // No continuous beat pulse on the lyrics: redrawing a transparent window every frame is expensive,
        // and the edge glow already pulses. Words still animate as they appear.
    }

    private void SetStatus(string s)
    {
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>Builds the sharp cover plus two pre-blurred, darkened versions off the UI thread.</summary>
    private async Task PrepareArtAsync(byte[]? bgra)
    {
        if (bgra is null || bgra.Length != MediaSessionWatcher.LargeArtSize * MediaSessionWatcher.LargeArtSize * 4)
        {
            _artSharp = _artBlurScreen = null;
            _window.SetArt(null, null, null);
            _stage?.SetArt(null, null);
            return;
        }
        var (sharp, card, screen) = await Task.Run(() =>
        {
            int n = MediaSessionWatcher.LargeArtSize;
            var s = BitmapSource.Create(n, n, 96, 96, PixelFormats.Pbgra32, null, bgra, n * 4);
            s.Freeze();
            return (s, ArtBlur.Make(bgra, n, 40, 3, 0.45), ArtBlur.Make(bgra, n, 32, 3, 0.7));
        });
        _artSharp = sharp; _artBlurScreen = screen;
        _window.SetArt(sharp, card, screen);
        _stage?.SetArt(sharp, screen);
    }

    public void Dispose()
    {
        _fetch?.Cancel();
        _timer.Stop();
        _window.Close();
        _stage?.Close();
    }
}

/// <summary>Cheap "frosted glass" blur: downsample, box-blur, darken. The GPU upscales it smoothly.</summary>
internal static class ArtBlur
{
    public static BitmapSource Make(byte[] bgra, int size, int small, int radius, double darken)
    {
        // 1) Downsample by block averaging.
        var px = new float[small * small * 3];
        double step = (double)size / small;
        for (int y = 0; y < small; y++)
        for (int x = 0; x < small; x++)
        {
            int x0 = (int)(x * step), x1 = Math.Max(x0 + 1, (int)((x + 1) * step));
            int y0 = (int)(y * step), y1 = Math.Max(y0 + 1, (int)((y + 1) * step));
            double r = 0, g = 0, b = 0; int c = 0;
            for (int yy = y0; yy < y1 && yy < size; yy++)
            for (int xx = x0; xx < x1 && xx < size; xx++)
            {
                int i = (yy * size + xx) * 4;
                b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; c++;
            }
            int o = (y * small + x) * 3;
            px[o] = (float)(r / c); px[o + 1] = (float)(g / c); px[o + 2] = (float)(b / c);
        }
        // 2) Separable box blur, two passes ≈ gaussian.
        for (int pass = 0; pass < 2; pass++) { BoxH(px, small, radius); BoxV(px, small, radius); }
        // 3) Darken so white text stays readable. Bright covers (e.g. mostly white) are darkened more:
        //    aim for an average brightness of about `darken`×110 out of 255.
        double mean = 0;
        for (int i = 0; i < px.Length; i += 3) mean += 0.2126 * px[i] + 0.7152 * px[i + 1] + 0.0722 * px[i + 2];
        mean /= px.Length / 3.0;
        darken = Math.Min(darken, darken * 110 / Math.Max(mean, 1));
        var outPx = new byte[small * small * 4];
        for (int i = 0, j = 0; i < px.Length; i += 3, j += 4)
        {
            outPx[j + 2] = (byte)Math.Clamp(px[i] * darken, 0, 255);
            outPx[j + 1] = (byte)Math.Clamp(px[i + 1] * darken, 0, 255);
            outPx[j] = (byte)Math.Clamp(px[i + 2] * darken, 0, 255);
            outPx[j + 3] = 255;
        }
        var bmp = BitmapSource.Create(small, small, 96, 96, PixelFormats.Bgra32, null, outPx, small * 4);
        bmp.Freeze();
        return bmp;
    }

    private static void BoxH(float[] p, int n, int r)
    {
        var row = new float[n * 3];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            for (int ch = 0; ch < 3; ch++)
            {
                float sum = 0; int c = 0;
                for (int k = -r; k <= r; k++) { int xx = Math.Clamp(x + k, 0, n - 1); sum += p[(y * n + xx) * 3 + ch]; c++; }
                row[x * 3 + ch] = sum / c;
            }
            Array.Copy(row, 0, p, y * n * 3, n * 3);
        }
    }

    private static void BoxV(float[] p, int n, int r)
    {
        var col = new float[n * 3];
        for (int x = 0; x < n; x++)
        {
            for (int y = 0; y < n; y++)
            for (int ch = 0; ch < 3; ch++)
            {
                float sum = 0; int c = 0;
                for (int k = -r; k <= r; k++) { int yy = Math.Clamp(y + k, 0, n - 1); sum += p[(yy * n + x) * 3 + ch]; c++; }
                col[y * 3 + ch] = sum / c;
            }
            for (int y = 0; y < n; y++)
                for (int ch = 0; ch < 3; ch++) p[(y * n + x) * 3 + ch] = col[y * 3 + ch];
        }
    }
}
