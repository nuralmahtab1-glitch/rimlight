using Rimlight.Core.Motion;

namespace Rimlight.Tests;

public class AmbientEngineTests
{
    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Every_preset_produces_valid_frames(AmbientPreset preset)
    {
        var e = new AmbientEngine { Preset = preset };
        var f = new FlowFrame();
        for (double t = 0; t < 30; t += 1 / 30.0)
        {
            e.Fill(t, ref f);
            Assert.InRange(f.Floor, 0f, 1f);
            Assert.InRange(f.Boost, 0f, 1f);
            foreach (var a in f.Arcs)
            {
                Assert.InRange(a.Pos, 0f, 1f);
                Assert.InRange(a.Gain, 0f, 1f);
                Assert.InRange(a.Width, 0f, 1f);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Every_animated_preset_actually_changes_over_time(AmbientPreset preset)
    {
        var e = new AmbientEngine { Preset = preset, Breathing = true };
        var f = new FlowFrame();
        var signatures = new HashSet<string>();
        for (double t = 0; t < 12; t += 0.37)
        {
            e.Fill(t, ref f);
            signatures.Add($"{f.Floor:0.00}|{string.Join(",", f.Arcs.Select(a => $"{a.Pos:0.00}:{a.Gain:0.0}"))}");
        }
        Assert.True(signatures.Count > 3, $"{preset} looked static");
    }

    [Fact]
    public void Colour_presets_bring_their_own_palette_and_glow_uses_yours()
    {
        Assert.Null(new AmbientEngine { Preset = AmbientPreset.Glow }.Colors);
        Assert.Equal(4, new AmbientEngine { Preset = AmbientPreset.Rainbow }.Colors!.Length);
        Assert.Equal(2, new AmbientEngine { Preset = AmbientPreset.Police }.Colors!.Length);
    }

    public static IEnumerable<object[]> AllPresets() => Enum.GetValues<AmbientPreset>().Select(p => new object[] { p });
}
