using Rimlight.Core.Motion;

namespace Rimlight.Tests;

public class SweepAnimatorTests
{
    [Fact]
    public void Beat_moves_arc_a_quarter_lap_counter_clockwise_after_move_completes()
    {
        var s = new SweepAnimator();
        s.Beat(0);
        s.Sample(1.0);
        Assert.Equal(0.75, s.Phase, 3); // −0.25 wrapped
    }

    [Fact]
    public void Move_is_eased_not_snapped()
    {
        var s = new SweepAnimator();
        s.Beat(0);
        s.Sample(0.02);
        double early = 1 - s.Phase;      // distance travelled so far (counter-clockwise)
        s.Sample(s.MoveSeconds / 2);
        double mid = 1 - s.Phase;
        Assert.InRange(early, 0, 0.01);  // barely moved after 20 ms
        Assert.InRange(mid, 0.11, 0.14); // about half-way at mid-move
    }

    [Fact]
    public void Beat_mid_move_continues_from_current_position()
    {
        var s = new SweepAnimator();
        s.Beat(0);
        s.Sample(0.1);
        double before = s.Phase;
        s.Beat(0.1);
        s.Sample(0.1);
        Assert.Equal(before, s.Phase, 6); // no jump at the moment of the second beat
    }

    [Fact]
    public void Bump_rises_fast_and_decays_slowly()
    {
        var s = new SweepAnimator();
        s.Beat(0);
        s.Sample(0.03);
        Assert.Equal(1.15, s.Bump, 3);   // full bump at end of 30 ms attack
        s.Sample(0.03 + 0.35);
        Assert.InRange(s.Bump, 1.05, 1.06); // ~1/e after one release time constant
        s.Sample(3);
        Assert.InRange(s.Bump, 1.0, 1.001);
    }

    [Fact]
    public void Four_beats_make_one_lap()
    {
        var s = new SweepAnimator();
        for (int i = 0; i < 4; i++) s.Beat(i * 0.625);
        s.Sample(10);
        Assert.True(s.Phase < 1e-6 || s.Phase > 1 - 1e-6);
    }
}
