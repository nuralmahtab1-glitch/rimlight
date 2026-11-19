using Rimlight.Core.Color;

namespace Rimlight.Tests;

public class OkLabTests
{
    [Theory]
    [InlineData("#FF2E88")]
    [InlineData("#7B3CFF")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#22D3EE")]
    public void Round_trips_through_oklab(string hex)
    {
        Assert.True(Rgb.TryParseHex(hex, out var rgb));
        var back = OkLab.ToRgb(OkLab.FromRgb(rgb));
        Assert.Equal(hex, back.ToHex());
    }

    [Fact]
    public void White_has_lightness_one_and_no_chroma()
    {
        var lab = OkLab.FromRgb(new Rgb(1, 1, 1));
        Assert.Equal(1f, lab.L, 3);
        Assert.InRange(lab.Chroma, 0f, 1e-3f);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("#12345")]
    [InlineData("")]
    [InlineData(null)]
    public void Bad_hex_is_rejected(string? hex) => Assert.False(Rgb.TryParseHex(hex, out _));
}
