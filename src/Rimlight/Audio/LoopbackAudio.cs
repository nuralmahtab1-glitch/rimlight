using NAudio.CoreAudioApi;
using NAudio.Wave;
using Rimlight.Core.Audio;
using Rimlight.Core.Diagnostics;

namespace Rimlight.Audio;

/// <summary>
/// Captures what the PC is playing (WASAPI loopback on the default output device, never the microphone)
/// and runs it through <see cref="BeatDetector"/>. Audio is analysed in memory and immediately discarded.
/// Follows default-device changes (e.g. plugging in headphones) and recovers from device errors.
/// </summary>
public sealed class LoopbackAudio : IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient? _notifications;
    private readonly System.Threading.Timer _restartTimer;
    private WasapiRecorder? _recorder;
    private WaveFormat? _format;
    private BeatDetector? _detector;
    private float[] _mono = Array.Empty<float>();
    private volatile AudioFrame _latest = AudioFrame.Silent;
    private double _sensitivity = 0.5;
    private bool _running;
    private bool _disposed;

    public LoopbackAudio()
    {
        _restartTimer = new System.Threading.Timer(_ => Restart(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _notifications = _enumerator.CreateNotificationClient(true);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) DeviceChanged("default output changed");
            };
            _notifications.DeviceStateChanged += (_, _) => DeviceChanged("device state changed");
            _notifications.DeviceRemoved += (_, _) => DeviceChanged("device removed");
        }
        catch (Exception ex) { Log.Warn($"Audio device notifications unavailable: {ex.Message}"); }
    }

    /// <summary>Latest analysis. Safe to read from any thread.</summary>
    public AudioFrame Latest => _latest;

    /// <summary>Friendly name of the output device being listened to, or null.</summary>
    public string? DeviceName { get; private set; }

    public double Sensitivity
    {
        get => _sensitivity;
        set { _sensitivity = value; lock (_gate) if (_detector is not null) _detector.Sensitivity = value; }
    }

    public void Start()
    {
        lock (_gate) _running = true;
        Restart();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            StopCapture();
        }
        _latest = AudioFrame.Silent;
    }

    private void DeviceChanged(string why)
    {
        if (!_running || _disposed) return;
        Log.Info($"Audio: {why}; restarting capture");
        _restartTimer.Change(400, Timeout.Infinite); // debounce bursts of notifications
    }

    private void Restart()
    {
        lock (_gate)
        {
            if (_disposed || !_running) return;
            StopCapture();
            try
            {
                using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                DeviceName = device.FriendlyName;
                var recorder = new WasapiRecorderBuilder()
                    .WithDevice(device)
                    .WithLoopbackCapture()
                    .WithSharedMode()
                    .WithBufferLength(30) // small buffer = low latency between the beat and the light
                    .Build();
                _format = recorder.WaveFormat;
                _detector = new BeatDetector(_format.SampleRate) { Sensitivity = _sensitivity };
                recorder.DataAvailable += OnData;
                recorder.RecordingStopped += OnStopped;
                recorder.StartRecording();
                _recorder = recorder;
                Log.Info($"Listening to \"{DeviceName}\" ({_format.SampleRate} Hz, {_format.Channels} ch, {_format.BitsPerSample}-bit {_format.Encoding})");
            }
            catch (Exception ex)
            {
                DeviceName = null;
                Log.Warn($"Audio capture unavailable ({ex.GetType().Name}: {ex.Message}); retrying in 5 s");
                if (!_disposed) _restartTimer.Change(5000, Timeout.Infinite);
            }
        }
    }

    private void StopCapture()
    {
        if (_recorder is null) return;
        _recorder.DataAvailable -= OnData;
        _recorder.RecordingStopped -= OnStopped;
        try { _recorder.StopRecording(); } catch { }
        try { _recorder.Dispose(); } catch { }
        _recorder = null;
        _detector = null;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && _running && !_disposed)
        {
            Log.Warn($"Audio capture stopped: {e.Exception.Message}; restarting");
            _restartTimer.Change(1000, Timeout.Infinite);
        }
    }

    private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private byte[] _zeros = Array.Empty<byte>();

    private void OnData<TFlags, TPos1, TPos2>(ReadOnlySpan<byte> buffer, TFlags flags, TPos1 devicePosition, TPos2 qpcPosition)
        where TFlags : struct
    {
        // WASAPI marks packets that should be treated as silence; their bytes are undefined.
        if ((Convert.ToInt32(flags) & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
        {
            if (_zeros.Length < buffer.Length) _zeros = new byte[buffer.Length];
            Analyse(_zeros.AsSpan(0, buffer.Length));
        }
        else Analyse(buffer);
    }

    /// <summary>Converts a packet of interleaved samples to mono floats and analyses it.</summary>
    private void Analyse(ReadOnlySpan<byte> buf)
    {
        var fmt = _format;
        var detector = _detector;
        if (fmt is null || detector is null || buf.IsEmpty) return;
        try
        {
            int ch = Math.Max(1, fmt.Channels);
            int bytesPerSample = fmt.BitsPerSample / 8;
            int frames = buf.Length / (bytesPerSample * ch);
            if (_mono.Length < frames) _mono = new float[frames];

            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
            float inv = 1f / ch;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                int o = f * ch * bytesPerSample;
                for (int c = 0; c < ch; c++, o += bytesPerSample)
                {
                    var s = buf.Slice(o, bytesPerSample);
                    sum += isFloat ? BitConverter.ToSingle(s)
                        : bytesPerSample == 2 ? BitConverter.ToInt16(s) / 32768f
                        : bytesPerSample == 3 ? ((s[0] << 8 | s[1] << 16 | s[2] << 24) >> 8) / 8388608f
                        : BitConverter.ToInt32(s) / 2147483648f;
                }
                _mono[f] = sum * inv;
            }

            double now = MonoClock.Now;
            detector.Process(_mono.AsSpan(0, frames), now);
            _latest = detector.Latest;
            LogSummary(now, _latest);
        }
        catch (Exception ex) { Log.Error("Audio analysis error", ex); }
    }

    private double _summaryStart;
    private long _summaryBeats;
    private float _summaryBassSum;
    private int _summaryFrames;

    /// <summary>Every 10 s while music plays: one log line with beats detected (helps diagnose friends' setups).</summary>
    private void LogSummary(double now, AudioFrame f)
    {
        if (_summaryStart == 0) { _summaryStart = now; _summaryBeats = f.BeatCount; }
        _summaryBassSum += f.Bass; _summaryFrames++;
        if (now - _summaryStart < 10) return;
        long beats = f.BeatCount - _summaryBeats;
        if (f.Active)
            Log.Info($"Audio: {beats} beats in {now - _summaryStart:0.0} s ({beats * 60 / (now - _summaryStart):0} per min), avg bass {_summaryBassSum / Math.Max(1, _summaryFrames):0.00}");
        _summaryStart = now; _summaryBeats = f.BeatCount; _summaryBassSum = 0; _summaryFrames = 0;
    }

    public void Dispose()
    {
        _disposed = true;
        _restartTimer.Dispose();
        try { _notifications?.Dispose(); } catch { }
        lock (_gate) StopCapture();
        _enumerator.Dispose();
    }
}
