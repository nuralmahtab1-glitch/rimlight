using Rimlight.Core.Audio;

namespace Rimlight.Tests;

public class BeatDetectorTests
{
    private const int Rate = 48000;

    /// <summary>Synthetic track: a 55 Hz kick with a fast decay on every beat, plus hi-hats and low noise.</summary>
    private static float[] KickTrack(double bpm, double seconds, int seed = 1, double gain = 1.0)
    {
        var rng = new Random(seed);
        var buf = new float[(int)(Rate * seconds)];
        double period = 60.0 / bpm;
        for (int i = 0; i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double sinceKick = t % period;
            double kick = 0.8 * Math.Sin(2 * Math.PI * (55 + 60 * Math.Exp(-sinceKick * 40)) * sinceKick) * Math.Exp(-sinceKick * 12);
            double sinceHat = t % (period / 2);
            double hat = 0.08 * (rng.NextDouble() * 2 - 1) * Math.Exp(-sinceHat * 60);
            double noise = 0.01 * (rng.NextDouble() * 2 - 1);
            buf[i] = (float)(gain * (kick + hat + noise));
        }
        return buf;
    }

    private static List<double> DetectBeats(float[] audio, double sensitivity = 0.5, int chunk = 480)
    {
        var d = new BeatDetector(Rate) { Sensitivity = sensitivity };
        var beats = new List<double>();
        long last = 0;
        d.FrameReady += f => { if (f.BeatCount != last) { last = f.BeatCount; beats.Add(f.BeatTime); } };
        for (int i = 0; i < audio.Length; i += chunk)
            d.Process(audio.AsSpan(i, Math.Min(chunk, audio.Length - i)));
        return beats;
    }

    [Theory]
    [InlineData(120, 0.500)]
    [InlineData(96, 0.625)]
    [InlineData(140, 0.4286)]
    public void Detects_kicks_at_the_right_tempo(double bpm, double expectedInterval)
    {
        var beats = DetectBeats(KickTrack(bpm, 12));
        // Ignore the first second (threshold warm-up).
        var steady = beats.Where(t => t > 1.5).ToList();
        int expected = (int)((12 - 1.5) / expectedInterval);
        Assert.InRange(steady.Count, expected - 2, expected + 1);

        var intervals = steady.Zip(steady.Skip(1), (a, b) => b - a).ToList();
        double median = intervals.OrderBy(x => x).ElementAt(intervals.Count / 2);
        Assert.InRange(median, expectedInterval - 0.03, expectedInterval + 0.03);
        Assert.All(intervals, iv => Assert.InRange(iv, expectedInterval * 0.8, expectedInterval * 1.2));
    }

    [Fact]
    public void Works_regardless_of_volume()
    {
        // Same song, played 20 dB quieter.
        var quiet = DetectBeats(KickTrack(120, 10, gain: 0.1)).Count(t => t > 1.5);
        var loud = DetectBeats(KickTrack(120, 10, gain: 1.0)).Count(t => t > 1.5);
        Assert.InRange(quiet, loud - 2, loud + 2);
    }

    [Fact]
    public void No_beats_and_inactive_in_silence()
    {
        var d = new BeatDetector(Rate);
        d.Process(new float[Rate * 4]);
        Assert.Equal(0, d.Latest.BeatCount);
        Assert.False(d.Latest.Active);
    }

    [Fact]
    public void Goes_inactive_about_two_seconds_after_music_stops()
    {
        var d = new BeatDetector(Rate);
        d.Process(KickTrack(120, 4));
        Assert.True(d.Latest.Active);
        d.Process(new float[(int)(Rate * 1.5)]);
        Assert.True(d.Latest.Active);  // brief gap: still active
        d.Process(new float[(int)(Rate * 1.0)]);
        Assert.False(d.Latest.Active); // > 2 s of silence
    }

    [Fact]
    public void Bass_envelope_pulses_with_the_kick()
    {
        var d = new BeatDetector(Rate);
        var levels = new List<(double t, float bass)>();
        d.FrameReady += f => levels.Add((f.Time, f.Bass));
        d.Process(KickTrack(120, 6));
        var late = levels.Where(l => l.t > 3).Select(l => l.bass).ToList();
        Assert.True(late.Max() > 0.8f, $"peak {late.Max()}");
        Assert.True(late.Min() < late.Max() * 0.7f, $"min {late.Min()} max {late.Max()}");
    }

    [Fact]
    public void Fft_finds_a_pure_tone()
    {
        var fft = new Fft(1024);
        var re = new float[1024]; var im = new float[1024];
        for (int i = 0; i < 1024; i++) re[i] = MathF.Sin(2 * MathF.PI * 64 * i / 1024f);
        fft.Transform(re, im);
        int peak = Enumerable.Range(1, 511).MaxBy(k => re[k] * re[k] + im[k] * im[k]);
        Assert.Equal(64, peak);
    }
}
