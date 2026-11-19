using Rimlight.Core.Color;

namespace Rimlight.Tests;

public class PaletteExtractorTests
{
    /// <summary>Builds a 64×64 BGRA image from (hex, fraction) bands.</summary>
    private static byte[] Image(params (string hex, double frac)[] bands)
    {
        const int n = 64 * 64;
        var px = new byte[n * 4];
        int i = 0;
        foreach (var (hex, frac) in bands)
        {
            Rgb.TryParseHex(hex, out var c);
            int count = (int)Math.Round(frac * n);
            for (int j = 0; j < count && i < n; j++, i++)
            {
                px[i * 4] = (byte)(c.B * 255); px[i * 4 + 1] = (byte)(c.G * 255);
                px[i * 4 + 2] = (byte)(c.R * 255); px[i * 4 + 3] = 255;
            }
        }
        while (i < n) { px[i * 4 + 3] = 255; i++; } // pad with black
        return px;
    }

    private static float HueDistance(float a, float b) { float d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    [Fact]
    public void Picks_red_then_blue_from_a_red_blue_grey_cover()
    {
        var p = PaletteExtractor.FromBgra(Image(("#E02020", 0.55), ("#2040E0", 0.30), ("#808080", 0.15)));
        Assert.NotNull(p);
        float red = PaletteExtractor.HueDegrees(OkLab.FromRgb(new Rgb(0.88f, 0.13f, 0.13f)));
        float blue = PaletteExtractor.HueDegrees(OkLab.FromRgb(new Rgb(0.13f, 0.25f, 0.88f)));
        Assert.InRange(HueDistance(PaletteExtractor.HueDegrees(p!.Value.Primary), red), 0, 12);
        Assert.InRange(HueDistance(PaletteExtractor.HueDegrees(p.Value.Secondary), blue), 0, 12);
    }

    [Fact]
    public void Black_and_white_cover_has_no_palette()
    {
        Assert.Null(PaletteExtractor.FromBgra(Image(("#000000", 0.5), ("#FFFFFF", 0.3), ("#777777", 0.2))));
    }

    [Fact]
    public void Small_vivid_area_beats_large_dark_background_and_is_brightened()
    {
        // Mostly black cover with a small orange logo (like many hip-hop covers).
        var p = PaletteExtractor.FromBgra(Image(("#0A0A0A", 0.80), ("#C05010", 0.08), ("#202020", 0.12)));
        Assert.NotNull(p);
        float orange = PaletteExtractor.HueDegrees(OkLab.FromRgb(new Rgb(0.75f, 0.31f, 0.06f)));
        Assert.InRange(HueDistance(PaletteExtractor.HueDegrees(p!.Value.Primary), orange), 0, 15);
        Assert.True(p.Value.Primary.L >= 0.62f, $"L = {p.Value.Primary.L}");
    }

    [Fact]
    public void Single_color_cover_still_gets_a_distinct_secondary()
    {
        var p = PaletteExtractor.FromBgra(Image(("#FF2E88", 1.0)));
        Assert.NotNull(p);
        Assert.True(Math.Sqrt(Lab.DistanceSq(p!.Value.Primary, p.Value.Secondary)) > 0.03);
    }

    [Fact]
    public void Output_colors_are_inside_srgb()
    {
        var p = PaletteExtractor.FromBgra(Image(("#00FF00", 0.5), ("#FF00FF", 0.5)))!.Value;
        foreach (var c in new[] { p.Primary, p.Secondary })
        {
            var (r, g, b) = OkLab.ToLinear(c);
            Assert.InRange(r, -0.001f, 1.001f); Assert.InRange(g, -0.001f, 1.001f); Assert.InRange(b, -0.001f, 1.001f);
        }
    }

    [Fact]
    public void Colorful_cover_gives_up_to_four_distinct_colors()
    {
        var p = PaletteExtractor.FromBgra(Image(("#E02020", 0.3), ("#20A040", 0.25), ("#2040E0", 0.25), ("#F0C020", 0.2)))!.Value;
        Assert.Equal(4, p.Count);
        for (int i = 0; i < p.Count; i++)
            for (int j = i + 1; j < p.Count; j++)
                Assert.True(Math.Sqrt(Lab.DistanceSq(p.Colors[i], p.Colors[j])) > 0.09);
    }

    [Fact]
    public void Two_color_cover_does_not_invent_extra_colors()
    {
        var p = PaletteExtractor.FromBgra(Image(("#E02020", 0.6), ("#2040E0", 0.4)))!.Value;
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Is_deterministic()
    {
        var img = Image(("#E02020", 0.4), ("#20A040", 0.35), ("#2040E0", 0.25));
        Assert.Equal(PaletteExtractor.FromBgra(img), PaletteExtractor.FromBgra(img));
    }
}
