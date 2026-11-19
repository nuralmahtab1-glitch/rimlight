namespace Rimlight.Core.Motion;

/// <summary>
/// Drives the "sweep" look from the reference videos. A lit arc sits at <see cref="Phase"/>
/// (0..1 around the perimeter), and each beat pushes it a step further with an eased move.
/// Each beat also adds a small brightness bump (fast attack, slower release).
/// Pure and deterministic: call <see cref="Beat"/> on beats and <see cref="Sample"/> each frame.
/// </summary>
public sealed class SweepAnimator
{
    /// <summary>How far one beat moves the arc (fraction of a lap). Measured: ~¼ lap per beat.</summary>
    public double StepPerBeat { get; set; } = 0.25;

    /// <summary>Duration of the eased move after a beat (measured ramps were 250–350 ms).</summary>
    public double MoveSeconds { get; set; } = 0.35;

    /// <summary>Direction: −1 = counter-clockwise (what the reference appears to do), +1 = clockwise.</summary>
    public int Direction { get; set; } = -1;

    /// <summary>Beat brightness bump size (reference: ±10–20%).</summary>
    public double BumpAmount { get; set; } = 0.15;
    public double BumpAttackSeconds { get; set; } = 0.03;
    public double BumpReleaseSeconds { get; set; } = 0.35;

    private double _from, _to, _moveStart = double.NegativeInfinity;
    private double _lastBeat = double.NegativeInfinity;
    private double _bumpStrength = 1;

    /// <summary>Arc centre, 0..1 around the perimeter (clockwise from top-left).</summary>
    public double Phase { get; private set; }

    /// <summary>Current brightness multiplier ≥ 1 from beat bumps.</summary>
    public double Bump { get; private set; } = 1;

    /// <summary>
    /// Beat spacing that earns a full <see cref="StepPerBeat"/>. Closer beats (busy drums) move the arc
    /// proportionally less, so the average travel speed stays near one lap per ~2.5 s (as in the reference)
    /// whether a song has a kick every 0.3 s or every 0.6 s.
    /// </summary>
    public double ReferenceBeatSeconds { get; set; } = 0.625;

    /// <summary>Register a beat at time <paramref name="now"/> (seconds). <paramref name="strength"/> 0..1 scales the step and bump.</summary>
    public void Beat(double now, double strength = 1)
    {
        strength = Math.Clamp(strength, 0, 1);
        double sinceLast = now - _lastBeat;
        double tempoScale = double.IsInfinity(sinceLast) ? 1 : Math.Clamp(sinceLast / ReferenceBeatSeconds, 0.35, 1.0);
        Sample(now); // settle current position so a beat mid-move continues smoothly from where it is
        _from = Phase;
        _to = _from + Direction * StepPerBeat * tempoScale * (0.6 + 0.4 * strength);
        _moveStart = now;
        _lastBeat = now;
        _bumpStrength = strength;
    }

    public void Sample(double now)
    {
        // Eased move (smootherstep) from _from to _to.
        double u = MoveSeconds <= 0 ? 1 : Math.Clamp((now - _moveStart) / MoveSeconds, 0, 1);
        double e = u * u * u * (u * (u * 6 - 15) + 10);
        Phase = Wrap(_from + (_to - _from) * e);

        // Bump envelope: linear attack, exponential release.
        double dt = now - _lastBeat;
        double env;
        if (dt < 0 || double.IsInfinity(dt)) env = 0;
        else if (dt < BumpAttackSeconds) env = dt / BumpAttackSeconds;
        else env = Math.Exp(-(dt - BumpAttackSeconds) / BumpReleaseSeconds);
        Bump = 1 + BumpAmount * _bumpStrength * env;
    }

    private static double Wrap(double x) => x - Math.Floor(x);
}
