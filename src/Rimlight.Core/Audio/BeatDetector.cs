namespace Rimlight.Core.Audio;

/// <summary>Latest analysis result, published by <see cref="BeatDetector"/>. Immutable.</summary>
/// <param name="Time">Clock time (seconds) of the newest analysed audio.</param>
/// <param name="Bass">Smoothed bass level 0..1 (auto-gained, fast attack / slow release).</param>
/// <param name="Mids">Smoothed mid level 0..1.</param>
/// <param name="Highs">Smoothed high level 0..1.</param>
/// <param name="BeatCount">Increments on every detected beat onset.</param>
/// <param name="BeatTime">Clock time of the last beat.</param>
/// <param name="BeatStrength">0..1 strength of the last beat.</param>
/// <param name="Active">False after ~2 s of silence.</param>
public sealed record AudioFrame(double Time, float Bass, float Mids, float Highs,
    long BeatCount, double BeatTime, float BeatStrength, bool Active)
{
    public static readonly AudioFrame Silent = new(0, 0, 0, 0, 0, double.NegativeInfinity, 0, false);
}

/// <summary>
/// Real-time beat and energy analysis of a mono signal.
/// Pipeline: samples → 1024-pt Hann-windowed FFT every 512 samples → band energies (bass 20–150 Hz,
/// mids 150–2 kHz, highs 2–8 kHz) → log-magnitude spectral flux (bass-weighted) → adaptive threshold
/// (rolling mean + k·stddev over ~1 s) → beat onsets. Envelope followers smooth the band levels.
/// Pure and deterministic, so it can be tested with synthetic audio. Not thread-safe: feed from one thread.
/// </summary>
public sealed class BeatDetector
{
    public const int FftSize = 1024;
    public const int Hop = 512;

    private readonly int _sampleRate;
    private readonly Fft _fft = new(FftSize);
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _re = new float[FftSize], _im = new float[FftSize];
    private readonly float[] _prevLog = new float[FftSize / 2];
    private int _ringPos, _sinceHop;

    private readonly int _bassLo, _bassHi, _midHi, _highHi, _fluxHi;

    // Adaptive threshold history (~1 s of onset values).
    private readonly float[] _history;
    private int _histPos, _histCount;
    private float _prevOnset;

    // Auto-gain: slowly decaying peaks so quiet and loud songs both use the full 0..1 range.
    private float _bassPeak = 1e-6f, _midPeak = 1e-6f, _highPeak = 1e-6f;
    private float _bassEnv, _midEnv, _highEnv;

    private long _samplesIn;
    private long _beatCount;
    private double _lastBeat = double.NegativeInfinity;
    private float _lastStrength;
    private double _silentFor;

    public BeatDetector(int sampleRate)
    {
        _sampleRate = sampleRate;
        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1));

        float binHz = (float)sampleRate / FftSize;
        _bassLo = Math.Max(1, (int)(20 / binHz));
        _bassHi = Math.Max(_bassLo + 1, (int)MathF.Ceiling(150 / binHz));
        _midHi = (int)(2000 / binHz);
        _highHi = Math.Min(FftSize / 2 - 1, (int)(8000 / binHz));
        _fluxHi = Math.Min(FftSize / 2 - 1, (int)(4000 / binHz));

        int hopsPerSecond = Math.Max(8, sampleRate / Hop);
        _history = new float[hopsPerSecond];
    }

    /// <summary>0..1. Higher = more beats detected (lower threshold).</summary>
    public double Sensitivity { get; set; } = 0.5;

    /// <summary>Shortest allowed gap between beats.</summary>
    public double MinBeatInterval { get; set; } = 0.18;

    /// <summary>Seconds of silence before <see cref="AudioFrame.Active"/> turns false.</summary>
    public double SilenceTimeout { get; set; } = 2.0;

    /// <summary>Clock time of the first sample fed; Time values in frames are offset from it.</summary>
    public double StartTime { get; set; }

    public AudioFrame Latest { get; private set; } = AudioFrame.Silent;

    /// <summary>Raised on every analysed hop (~86×/s at 44.1 kHz) with the new frame.</summary>
    public event Action<AudioFrame>? FrameReady;

    /// <summary>Process a live chunk whose last sample was captured at clock time <paramref name="endTime"/>.
    /// Keeps frame timestamps aligned with the wall clock even across gaps (paused playback).</summary>
    public void Process(ReadOnlySpan<float> mono, double endTime)
    {
        StartTime = endTime - (double)(_samplesIn + mono.Length) / _sampleRate;
        Process(mono);
    }

    public void Process(ReadOnlySpan<float> mono)
    {
        foreach (var s in mono)
        {
            _ring[_ringPos] = s;
            _ringPos = (_ringPos + 1) & (FftSize - 1);
            _samplesIn++;
            if (++_sinceHop >= Hop)
            {
                _sinceHop = 0;
                if (_samplesIn >= FftSize) AnalyseHop();
            }
        }
    }

    private void AnalyseHop()
    {
        double now = StartTime + (double)_samplesIn / _sampleRate;
        double hopSeconds = (double)Hop / _sampleRate;

        // Window the most recent FftSize samples (oldest first).
        float sumSq = 0;
        for (int i = 0; i < FftSize; i++)
        {
            float v = _ring[(_ringPos + i) & (FftSize - 1)];
            sumSq += v * v;
            _re[i] = v * _window[i];
            _im[i] = 0;
        }
        float rms = MathF.Sqrt(sumSq / FftSize);

        // Digital silence (nothing playing): skip the FFT entirely and just let everything decay.
        if (rms < 1e-6f)
        {
            _silentFor += hopSeconds;
            Level(ref _bassEnv, ref _bassPeak, 0, hopSeconds, 0.015, 0.30);
            Level(ref _midEnv, ref _midPeak, 0, hopSeconds, 0.020, 0.25);
            Level(ref _highEnv, ref _highPeak, 0, hopSeconds, 0.010, 0.15);
            Array.Clear(_prevLog);
            _history[_histPos] = 0;
            _histPos = (_histPos + 1) % _history.Length;
            _histCount = Math.Min(_histCount + 1, _history.Length);
            _prevOnset = 0;
            Latest = new AudioFrame(now, _bassEnv, _midEnv, _highEnv, _beatCount, _lastBeat, _lastStrength, _silentFor < SilenceTimeout);
            FrameReady?.Invoke(Latest);
            return;
        }

        _fft.Transform(_re, _im);

        float bass = 0, mids = 0, highs = 0, lowFlux = 0, otherFlux = 0;
        int lowBins = 0, otherBins = 0;
        for (int bin = 1; bin <= _highHi; bin++)
        {
            float p = _re[bin] * _re[bin] + _im[bin] * _im[bin];
            if (bin >= _bassLo && bin <= _bassHi) bass += p;
            else if (bin <= _midHi) mids += p;
            else highs += p;

            float logMag = MathF.Log(1 + 1000 * MathF.Sqrt(p));
            if (bin <= _fluxHi)
            {
                float d = MathF.Max(0, logMag - _prevLog[bin]);
                if (bin <= _bassHi) { lowFlux += d; lowBins++; }
                else { otherFlux += d; otherBins++; }
            }
            _prevLog[bin] = logMag;
        }
        // Onset strength: kicks (bass flux) drive it. Everything above the bass counts as an average per
        // bin at half weight, so dense hi-hats/snares can't outvote the kick by sheer number of bins.
        float flux = lowFlux + 0.5f * lowBins * (otherBins > 0 ? otherFlux / otherBins : 0);

        // Band levels (amplitude), auto-gained against decaying peaks, then envelope-followed.
        Level(ref _bassEnv, ref _bassPeak, MathF.Sqrt(bass), hopSeconds, 0.015, 0.30);
        Level(ref _midEnv, ref _midPeak, MathF.Sqrt(mids), hopSeconds, 0.020, 0.25);
        Level(ref _highEnv, ref _highPeak, MathF.Sqrt(highs), hopSeconds, 0.010, 0.15);

        // Silence tracking (−70 dBFS).
        bool silentHop = rms < 3e-4f;
        _silentFor = silentHop ? _silentFor + hopSeconds : 0;
        bool active = _silentFor < SilenceTimeout;

        // Adaptive threshold over the last ~1 s of flux values.
        float mean = 0, var = 0, recentMax = 0;
        if (_histCount > 0)
        {
            for (int i = 0; i < _histCount; i++) { mean += _history[i]; recentMax = MathF.Max(recentMax, _history[i]); }
            mean /= _histCount;
            for (int i = 0; i < _histCount; i++) { float d = _history[i] - mean; var += d * d; }
            var /= _histCount;
        }
        float std = MathF.Sqrt(var);
        float k = (float)(2.2 - 1.6 * Math.Clamp(Sensitivity, 0, 1)); // 2.2 (strict) … 0.6 (eager)
        float threshold = mean + k * std;

        bool warmedUp = _histCount >= _history.Length / 2;
        // Also require a decent fraction of the strongest recent onset, so off-beat hi-hats don't count.
        float relFloor = recentMax * (float)(0.45 - 0.25 * Math.Clamp(Sensitivity, 0, 1));
        if (warmedUp && !silentHop && flux > threshold && flux > relFloor && flux >= _prevOnset && flux > 0.5f
            && now - _lastBeat >= MinBeatInterval)
        {
            _beatCount++;
            _lastBeat = now;
            _lastStrength = Math.Clamp((flux - mean) / (MathF.Max(std, 1e-3f) * (k + 2f)), 0.2f, 1f);
        }

        _history[_histPos] = flux;
        _histPos = (_histPos + 1) % _history.Length;
        _histCount = Math.Min(_histCount + 1, _history.Length);
        _prevOnset = flux;

        Latest = new AudioFrame(now, _bassEnv, _midEnv, _highEnv, _beatCount, _lastBeat, _lastStrength, active);
        FrameReady?.Invoke(Latest);
    }

    private static void Level(ref float env, ref float peak, float x, double dt, double attack, double release)
    {
        // Peak decays with a ~6 s time constant, never below a floor (so silence doesn't amplify noise).
        peak = MathF.Max(x, MathF.Max(peak * (float)Math.Exp(-dt / 6.0), 0.5f));
        float target = Math.Clamp(x / peak, 0f, 1f);
        double tau = target > env ? attack : release;
        env += (target - env) * (float)(1 - Math.Exp(-dt / tau));
    }
}
