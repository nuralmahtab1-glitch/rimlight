namespace Rimlight.Core.Motion;

/// <summary>The light patterns. <see cref="FlowEngine"/> in Auto mode switches between them to follow the song.</summary>
public enum FlowPattern { Sweep, Twin, Mirror, Bounce, Burst, Chase, Pulse, Ripple, Rise }

/// <summary>A lit arc on the screen border: centre (0..1 around the loop, clockwise from top-left), width (fraction of the loop), gain (0..1).</summary>
public record struct Arc(float Pos, float Width, float Gain);

/// <summary>Everything the renderer needs from the flow engine for one frame.</summary>
public struct FlowFrame
{
    public const int MaxArcs = 8;
    public Arc[] Arcs;          // MaxArcs entries; unused ones have Gain 0
    public float Floor;         // brightness of the border outside the arcs (0..1)
    public float Boost;         // 0..1 white-hot push on the rim (beats, drops)
    public float Rotation;      // extra gradient rotation (0..1), drifts faster with energy
    public FlowPattern Pattern;
}

/// <summary>
/// Turns beats and energy into moving light. Seven patterns; in Auto mode it reads the song's energy:
/// calm passages get slow, wide patterns, choruses get busy ones, it changes pattern every few bars
/// (cross-fading), and a "drop" (sudden energy jump after a calmer part) fires a full-border flash and
/// jumps to a high-energy pattern. Pure and deterministic — testable without audio hardware.
/// </summary>
public sealed class FlowEngine
{
    /// <summary>Null = Auto (follow the song). Otherwise always this pattern.</summary>
    public FlowPattern? Fixed { get; set; }

    /// <summary>Width/height of the screen (for patterns anchored to edge centres).</summary>
    public float Aspect { get; set; } = 16f / 9f;

    public const int BeatsPerSection = 32;     // ≈ 8 bars, a musical phrase
    private const double CrossfadeSeconds = 0.8;
    private const double MinSectionSeconds = 8.0;

    private readonly SweepAnimator _step = new();
    private readonly Random _rng = new(7);
    private double _now, _lastBeat = double.NegativeInfinity;
    private float _beatEnv;                    // fast beat envelope 0..1
    private float _energyShort, _energyLong;   // ~1.5 s and ~10 s smoothed energy
    private float _dropEnv;                    // drop flash envelope
    private bool _dropArmed = true;
    private double _rotation;
    private double _chasePhase;
    private int _beatsInSection;
    private double _sectionStart;
    private FlowPattern _pattern = FlowPattern.Sweep, _previous = FlowPattern.Sweep;
    private double _switchTime = double.NegativeInfinity;
    private double _prevPhase;                 // phase of the previous pattern during cross-fade
    private int _energyClass = 1;              // 0 calm, 1 medium, 2 intense
    private double _classSince;

    // Ripple: pulses of light that spread both ways from where a beat "lands".
    private readonly List<(float Origin, double Start, float Strength)> _ripples = new();
    // Rise: VU-meter level with peak hold.
    private float _bass, _level, _peak;
    // Sparkle: hi-hat/cymbal transients pop short twinkles.
    private float _highs, _highsSlow;
    private double _lastSparkle;
    private readonly List<(float Pos, double Start)> _sparkles = new();
    // Colour steps on strong beats in intense parts.
    private double _rotKickTarget, _rotKick;

    public FlowPattern Current => Fixed ?? _pattern;
    public float EnergyShort => _energyShort;
    public float EnergyLong => _energyLong;
    public int DropCount { get; private set; }

    /// <summary>Register a beat. <paramref name="strength"/> 0..1.</summary>
    public void Beat(double now, double strength = 1)
    {
        strength = Math.Clamp(strength, 0, 1);
        _now = now;
        _lastBeat = now;
        _beatEnv = Math.Max(_beatEnv, (float)(0.5 + 0.5 * strength));
        _beatsInSection++;

        var p = Current;
        // Step size per pattern: busy patterns move in smaller steps so they don't look frantic.
        _step.StepPerBeat = p switch
        {
            FlowPattern.Sweep => 0.25,
            FlowPattern.Twin => 0.125,
            FlowPattern.Mirror => 0.125,
            FlowPattern.Bounce => 0.125,
            FlowPattern.Burst => 0.03,
            FlowPattern.Ripple or FlowPattern.Rise => 0.0,
            _ => 0.06,
        };
        _step.Direction = p == FlowPattern.Twin && (_beatsInSection / 8) % 2 == 1 ? 1 : -1; // twin reverses every 2 bars
        _step.Beat(now, strength);
        if (p == FlowPattern.Chase) _chasePhase += 0.05 * strength; // kick the streaks forward

        if (p == FlowPattern.Ripple)
        {
            // Land each ripple somewhere new: an edge centre or a corner, never the same twice in a row.
            float origin = (float)(_rng.Next(8) / 8.0 + (_beatsInSection % 2) * 0.03);
            _ripples.Add((origin, now, (float)(0.55 + 0.45 * strength)));
            if (_ripples.Count > 4) _ripples.RemoveAt(0);
        }

        // Strong beats in intense music push the colours a step around the screen.
        if (_energyClass == 2 && strength > 0.6) _rotKickTarget += 0.06;

        if (Fixed is null && _beatsInSection >= BeatsPerSection && now - _sectionStart >= MinSectionSeconds)
            Switch(now, PickFor(_energyClass));
    }

    /// <summary>Advance time. Levels are 0..1 (auto-gained) from the audio analyser.</summary>
    public void Update(double now, double dt, float bass, float mids, float highs, bool music)
    {
        _now = now;
        dt = Math.Clamp(dt, 0, 0.25);
        _step.Sample(now);

        float energy = music ? Math.Clamp(0.6f * bass + 0.25f * mids + 0.15f * highs, 0, 1) : 0;
        _energyShort += (energy - _energyShort) * (float)(1 - Math.Exp(-dt / 1.5));
        _energyLong += (energy - _energyLong) * (float)(1 - Math.Exp(-dt / 10.0));

        _beatEnv *= (float)Math.Exp(-dt / 0.22);
        _dropEnv *= (float)Math.Exp(-dt / 0.6);

        // Gradient drifts around the screen; faster when the music is intense. Beat kicks ease in on top.
        _rotation += dt * (0.004 + 0.03 * _energyShort);
        _rotKick += (_rotKickTarget - _rotKick) * (1 - Math.Exp(-dt / 0.25));

        // VU level: fast attack, slower fall; the peak marker holds then drifts down.
        _bass = music ? bass : 0;
        _level += (_bass - _level) * (float)(1 - Math.Exp(-dt / (_bass > _level ? 0.04 : 0.25)));
        _peak = Math.Max(_level, _peak - (float)dt * 0.35f);

        // Hi-hat / cymbal transient → sparkle.
        _highs = music ? highs : 0;
        _highsSlow += (_highs - _highsSlow) * (float)(1 - Math.Exp(-dt / 0.3));
        if (_highs - _highsSlow > 0.12f && now - _lastSparkle > 0.07 && _energyShort > 0.4f)
        {
            _lastSparkle = now;
            _sparkles.Add(((float)_rng.NextDouble(), now));
            if (_sparkles.Count > 3) _sparkles.RemoveAt(0);
        }
        _ripples.RemoveAll(r => now - r.Start > 2.0);
        _sparkles.RemoveAll(s => now - s.Start > 0.3);

        // Chase streaks move continuously with energy.
        _chasePhase += dt * (0.12 + 0.45 * _energyShort);

        if (Fixed is not null || !music) return;

        // Drop: big jump in short-term energy over a calmer recent past. After one fires, the music must
        // settle back toward its average before another can (no double-drops while energy stays high).
        if (!_dropArmed && _energyShort < _energyLong * 1.15f) _dropArmed = true;
        if (_dropArmed && _energyShort > 0.5f && _energyShort > _energyLong * 1.45f && _energyLong < 0.45f
            && now - _switchTime > 2.0)
        {
            DropCount++;
            _dropArmed = false;
            _dropEnv = 1f;
            Switch(now, _rng.Next(2) == 0 ? FlowPattern.Chase : FlowPattern.Burst);
            return;
        }

        int cls = _energyShort < 0.32f ? 0 : _energyShort > 0.58f ? 2 : 1;
        if (cls != _energyClass) { _energyClass = cls; _classSince = now; }
        // Energy level held for a while and the current pattern doesn't suit it: switch early.
        if (now - _classSince > 3.0 && now - _sectionStart > MinSectionSeconds && !Suits(_pattern, _energyClass))
            Switch(now, PickFor(_energyClass));
    }

    public void Fill(ref FlowFrame f)
    {
        f.Arcs ??= new Arc[FlowFrame.MaxArcs];
        Array.Clear(f.Arcs);
        var cur = Current;
        f.Pattern = cur;

        double x = Math.Clamp((_now - _switchTime) / CrossfadeSeconds, 0, 1);
        float fadeIn = (float)(x * x * (3 - 2 * x));
        int n = 0;
        float floorNew = Emit(cur, (float)_step.Phase, fadeIn, f.Arcs, ref n);
        float floor = floorNew;
        if (fadeIn < 1f && Fixed is null)
        {
            float floorOld = Emit(_previous, (float)_prevPhase, 1 - fadeIn, f.Arcs, ref n);
            floor = floorOld + (floorNew - floorOld) * fadeIn;
        }

        // Sparkles ride on top of the busier patterns, using any free arc slots.
        if (cur is FlowPattern.Chase or FlowPattern.Burst or FlowPattern.Ripple or FlowPattern.Twin or FlowPattern.Pulse)
        {
            foreach (var (pos, start) in _sparkles)
            {
                if (n >= f.Arcs.Length) break;
                float age = (float)(_now - start);
                f.Arcs[n++] = new Arc(pos, 0.035f, Math.Clamp(1 - age / 0.3f, 0, 1));
            }
        }

        f.Floor = Math.Clamp(floor + 0.85f * _dropEnv, 0, 1);
        f.Boost = Math.Clamp(0.3f * _beatEnv + _dropEnv, 0, 1);
        double rot = _rotation + _rotKick;
        f.Rotation = (float)(rot - Math.Floor(rot));
    }

    /// <summary>Adds the arcs for one pattern (gains scaled by <paramref name="weight"/>) and returns its floor.</summary>
    private int _emitCount;

    private float Emit(FlowPattern p, float phase, float weight, Arc[] arcs, ref int n)
    {
        _emitCount = n;
        try { return EmitCore(p, phase, weight, arcs); }
        finally { n = _emitCount; }
    }

    private float EmitCore(FlowPattern p, float phase, float weight, Arc[] arcs)
    {
        float beat = _beatEnv;
        float e = _energyShort;
        void Add(float pos, float width, float gain)
        {
            if (_emitCount < arcs.Length) arcs[_emitCount++] = new Arc(Wrap(pos), width, Math.Clamp(gain * weight, 0, 1));
        }

        switch (p)
        {
            case FlowPattern.Sweep:
                Add(phase, 0.5f, 1);
                return 0.05f;

            case FlowPattern.Twin:
                Add(phase, 0.28f, 1);
                Add(phase + 0.5f, 0.28f, 1);
                return 0.05f;

            case FlowPattern.Mirror:
            {
                // Two arcs leave the bottom centre, climb both sides, meet at the top, and come back.
                float c = EdgeCentre(2);
                float tri = 1 - Math.Abs(2 * Wrap(phase * 2) - 1); // 0 → 1 → 0 as phase advances
                float x = 0.5f * tri;
                Add(c + x, 0.2f, 1);
                Add(c - x, 0.2f, 1);
                return 0.06f;
            }

            case FlowPattern.Bounce:
            {
                // One arc swings between the top-left and bottom-right corners instead of looping.
                float tri = 1 - Math.Abs(2 * Wrap(phase * 2) - 1);
                Add(0.0f + 0.5f * tri, 0.34f, 1);
                return 0.05f;
            }

            case FlowPattern.Burst:
                // Edge centres flash on each beat; a soft base glow stays.
                for (int i = 0; i < 4; i++) Add(EdgeCentre(i) + phase * 0.25f, 0.16f + 0.08f * beat, 0.25f + 0.75f * beat);
                return 0.12f;

            case FlowPattern.Chase:
            {
                float ph = (float)(_chasePhase - Math.Floor(_chasePhase));
                for (int i = 0; i < 4; i++) Add(-(ph + i * 0.25f), 0.08f + 0.04f * e, 0.8f + 0.2f * beat);
                return 0.04f;
            }

            case FlowPattern.Ripple:
                foreach (var (origin, start, strength) in _ripples)
                {
                    float age = (float)(_now - start);
                    float spread = 0.32f * age;                          // loops per second outward
                    float gain = strength * MathF.Exp(-age / 0.7f);
                    Add(origin + spread, 0.09f + 0.05f * age, gain);
                    Add(origin - spread, 0.09f + 0.05f * age, gain);
                }
                return 0.06f;

            case FlowPattern.Rise:
            {
                // VU meter: light climbs both sides from the bottom centre with the bass; a peak dot holds.
                float c = EdgeCentre(2);
                Add(c, 0.06f + 0.9f * _level, 1);
                float pk = 0.03f + 0.45f * _peak;
                Add(c + pk, 0.025f, 0.9f);
                Add(c - pk, 0.025f, 0.9f);
                return 0.04f;
            }

            default: // Pulse: the whole border throbs with the bass/beat
                return Math.Clamp(0.45f + 0.55f * Math.Max(beat, e), 0, 1);
        }
    }

    /// <summary>Perimeter position of an edge's centre: 0 top, 1 right, 2 bottom, 3 left.</summary>
    private float EdgeCentre(int edge)
    {
        float w = Aspect, h = 1, l = 2 * (w + h);
        return edge switch
        {
            0 => (w / 2) / l,
            1 => (w + h / 2) / l,
            2 => (w + h + w / 2) / l,
            _ => (2 * w + h + h / 2) / l,
        };
    }

    private void Switch(double now, FlowPattern next)
    {
        if (next == _pattern && now - _switchTime < 30) next = PickFor(_energyClass, avoid: _pattern);
        _previous = _pattern;
        _prevPhase = _step.Phase;
        _pattern = next;
        _switchTime = now;
        _sectionStart = now;
        _beatsInSection = 0;
    }

    private static readonly FlowPattern[][] Pools =
    [
        [FlowPattern.Sweep, FlowPattern.Bounce, FlowPattern.Mirror, FlowPattern.Rise],                      // calm
        [FlowPattern.Twin, FlowPattern.Mirror, FlowPattern.Ripple, FlowPattern.Bounce, FlowPattern.Rise],   // medium
        [FlowPattern.Chase, FlowPattern.Burst, FlowPattern.Ripple, FlowPattern.Pulse, FlowPattern.Twin],    // intense
    ];

    private static bool Suits(FlowPattern p, int cls) => Pools[cls].Contains(p);

    private FlowPattern PickFor(int cls, FlowPattern? avoid = null)
    {
        var pool = Pools[Math.Clamp(cls, 0, 2)].Where(p => p != (avoid ?? _pattern)).ToArray();
        return pool.Length == 0 ? Pools[cls][0] : pool[_rng.Next(pool.Length)];
    }

    private static float Wrap(float v) => v - MathF.Floor(v);
}
