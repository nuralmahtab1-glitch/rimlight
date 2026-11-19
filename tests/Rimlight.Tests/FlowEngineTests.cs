using Rimlight.Core.Motion;

namespace Rimlight.Tests;

public class FlowEngineTests
{
    /// <summary>Simulates a song: steady beats at <paramref name="bpm"/> with a given energy for <paramref name="seconds"/>.</summary>
    private static double Play(FlowEngine f, double start, double seconds, double bpm, float energy, Action<double>? each = null)
    {
        double t = start, period = 60 / bpm, nextBeat = start;
        while (t < start + seconds)
        {
            if (t >= nextBeat) { f.Beat(t, 0.8); nextBeat += period; }
            f.Update(t, 1 / 60.0, energy, energy, energy, music: true);
            each?.Invoke(t);
            t += 1 / 60.0;
        }
        return t;
    }

    [Fact]
    public void Auto_changes_pattern_every_few_bars()
    {
        var f = new FlowEngine();
        var seen = new HashSet<FlowPattern>();
        Play(f, 0, 60, 120, 0.45f, _ => seen.Add(f.Current));
        Assert.True(seen.Count >= 3, $"only saw {string.Join(",", seen)}");
    }

    [Fact]
    public void Fixed_pattern_never_changes()
    {
        var f = new FlowEngine { Fixed = FlowPattern.Twin };
        Play(f, 0, 40, 120, 0.8f, _ => Assert.Equal(FlowPattern.Twin, f.Current));
    }

    [Fact]
    public void Calm_music_gets_calm_patterns_and_loud_music_busy_ones()
    {
        var calm = new FlowEngine();
        var calmSeen = new HashSet<FlowPattern>();
        Play(calm, 0, 60, 90, 0.15f, t => { if (t > 20) calmSeen.Add(calm.Current); });
        Assert.DoesNotContain(FlowPattern.Chase, calmSeen);
        Assert.DoesNotContain(FlowPattern.Burst, calmSeen);

        var loud = new FlowEngine();
        var loudSeen = new HashSet<FlowPattern>();
        Play(loud, 0, 60, 128, 0.9f, t => { if (t > 20) loudSeen.Add(loud.Current); });
        Assert.Contains(loudSeen, p => p is FlowPattern.Chase or FlowPattern.Burst or FlowPattern.Pulse);
    }

    [Fact]
    public void A_drop_flashes_and_jumps_to_a_high_energy_pattern()
    {
        var f = new FlowEngine();
        double t = Play(f, 0, 25, 120, 0.2f);          // calm verse
        int before = f.DropCount;
        var frame = new FlowFrame();
        float maxFloor = 0;
        Play(f, t, 4, 120, 1.0f, _ => { f.Fill(ref frame); maxFloor = Math.Max(maxFloor, frame.Floor); });
        Assert.Equal(before + 1, f.DropCount);
        Assert.True(maxFloor > 0.8f, $"drop flash floor {maxFloor}");
        Assert.Contains(f.Current, new[] { FlowPattern.Chase, FlowPattern.Burst });
    }

    [Fact]
    public void Ripple_spreads_outward_from_where_the_beat_lands()
    {
        var f = new FlowEngine { Fixed = FlowPattern.Ripple };
        var frame = new FlowFrame();
        f.Update(0, 0.016, 0.5f, 0.5f, 0.5f, true);
        f.Beat(0.0, 1);
        f.Update(0.1, 0.1, 0.5f, 0.5f, 0.5f, true); f.Fill(ref frame);
        float early = Separation(frame);
        f.Update(0.6, 0.5, 0.5f, 0.5f, 0.5f, true); f.Fill(ref frame);
        float later = Separation(frame);
        Assert.True(later > early + 0.1f, $"early {early} later {later}");

        static float Separation(FlowFrame fr)
        {
            var lit = fr.Arcs.Where(a => a.Gain > 0.05f).Select(a => a.Pos).ToArray();
            float d = Math.Abs(lit[0] - lit[1]);
            return Math.Min(d, 1 - d);
        }
    }

    [Fact]
    public void Rise_grows_with_the_bass()
    {
        var f = new FlowEngine { Fixed = FlowPattern.Rise };
        var frame = new FlowFrame();
        for (int i = 0; i < 30; i++) f.Update(i / 60.0, 1 / 60.0, 0.1f, 0, 0, true);
        f.Fill(ref frame);
        float quiet = frame.Arcs[0].Width;
        for (int i = 30; i < 60; i++) f.Update(i / 60.0, 1 / 60.0, 0.9f, 0, 0, true);
        f.Fill(ref frame);
        Assert.True(frame.Arcs[0].Width > quiet + 0.4f, $"quiet {quiet} loud {frame.Arcs[0].Width}");
    }

    [Fact]
    public void Frames_are_always_valid()
    {
        var f = new FlowEngine();
        var frame = new FlowFrame();
        Play(f, 0, 90, 140, 0.6f, _ =>
        {
            f.Fill(ref frame);
            Assert.InRange(frame.Floor, 0f, 1f);
            Assert.InRange(frame.Boost, 0f, 1f);
            Assert.InRange(frame.Rotation, 0f, 1f);
            foreach (var a in frame.Arcs)
            {
                Assert.InRange(a.Pos, 0f, 1f);
                Assert.InRange(a.Gain, 0f, 1f);
                Assert.InRange(a.Width, 0f, 1f);
            }
        });
    }

    [Fact]
    public void Pattern_switches_crossfade_instead_of_snapping()
    {
        var f = new FlowEngine();
        var frame = new FlowFrame();
        FlowPattern last = f.Current;
        double switchedAt = double.NaN;
        int blends = 0, switches = 0;
        Play(f, 0, 60, 120, 0.45f, t =>
        {
            if (f.Current != last) { last = f.Current; switchedAt = t; switches++; }
            if (t - switchedAt is > 0.3 and < 0.32)
            {
                // Mid-crossfade: arcs from both patterns are on screen at partial strength.
                f.Fill(ref frame);
                if (frame.Arcs.Count(a => a.Gain is > 0.15f and < 0.85f) >= 2) blends++;
            }
        });
        Assert.True(switches >= 2);
        Assert.True(blends >= switches - 1, $"{blends} blends for {switches} switches");
    }
}
