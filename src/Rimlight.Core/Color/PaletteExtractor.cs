namespace Rimlight.Core.Color;

/// <summary>2–4 colors for the light, best first. The primary gets the largest share of the loop.</summary>
public readonly record struct Palette(Lab[] Colors)
{
    public Palette(Lab primary, Lab secondary) : this([primary, secondary]) { }
    public Lab Primary => Colors[0];
    public Lab Secondary => Colors[1];
    public int Count => Colors.Length;

    public bool Equals(Palette other) => Colors.AsSpan().SequenceEqual(other.Colors);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var c in Colors) h.Add(c);
        return h.ToHashCode();
    }

    /// <summary>The first <paramref name="n"/> colors (at least 2).</summary>
    public Palette Take(int n) => new(Colors[..Math.Clamp(n, 2, Colors.Length)]);
}

/// <summary>
/// Picks two light-friendly colors from album art.
/// Pixels → OKLab → k-means (k = 5) → drop near-black / near-white / grey clusters → score by
/// population × chroma → best two distinct clusters → boost lightness/chroma so they read as light
/// on a dark screen. Returns null when the art has no usable color (e.g. black-and-white covers).
/// Pure and deterministic.
/// </summary>
public static class PaletteExtractor
{
    private const int K = 5;
    private const int Iterations = 12;

    /// <summary>Extract from 32-bit BGRA pixels (e.g. a 64×64 thumbnail). Transparent pixels are ignored.</summary>
    public static Palette? FromBgra(ReadOnlySpan<byte> bgra)
    {
        var pts = new List<Lab>(bgra.Length / 4);
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128) continue;
            pts.Add(OkLab.FromRgb(Rgb.FromBytes(bgra[i + 2], bgra[i + 1], bgra[i])));
        }
        return FromLab(pts);
    }

    public static Palette? FromLab(IReadOnlyList<Lab> pts)
    {
        if (pts.Count < 16) return null;
        var (centers, counts) = KMeans(pts);

        var candidates = new List<(Lab c, double score)>();
        for (int k = 0; k < centers.Length; k++)
        {
            if (counts[k] == 0) continue;
            var c = centers[k];
            double pop = (double)counts[k] / pts.Count;
            bool nearBlack = c.L < 0.20;
            bool grey = c.Chroma < 0.035;
            bool nearWhite = c.L > 0.92 && c.Chroma < 0.06;
            if (nearBlack || grey || nearWhite || pop < 0.02) continue;
            // Vivid colors win even when they cover less of the cover; population still matters.
            candidates.Add((c, Math.Pow(pop, 0.7) * c.Chroma));
        }
        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        // Take up to 4 colors that look clearly different from everything already chosen
        // (otherwise extra colors just make the gradient muddy). Minor colors must still be meaningful.
        var chosen = new List<Lab> { candidates[0].c };
        double topScore = candidates[0].score;
        foreach (var (c, score) in candidates.Skip(1))
        {
            if (chosen.Count >= 4) break;
            if (chosen.Count >= 2 && score < topScore * 0.18) break;
            var boosted = BoostForLight(c);
            if (chosen.All(x => Math.Sqrt(Lab.DistanceSq(BoostForLight(x), boosted)) > 0.09)) chosen.Add(c);
        }
        // Only one usable color: derive a companion by rotating the hue a little.
        if (chosen.Count == 1) chosen.Add(RotateHue(chosen[0], 35, -0.06f));

        return new Palette(chosen.Select(BoostForLight).ToArray());
    }

    /// <summary>Lift lightness and chroma so the color glows on a dark screen, then pull back into sRGB gamut.</summary>
    public static Lab BoostForLight(Lab c)
    {
        float l = Math.Clamp(c.L, 0.62f, 0.86f);
        float chroma = c.Chroma;
        float scale = chroma < 1e-4f ? 1f : Math.Max(chroma, 0.13f) / chroma;
        return ClampToGamut(new Lab(l, c.A * scale, c.B * scale));
    }

    /// <summary>Reduces chroma (keeping hue and lightness) until the color fits in sRGB.</summary>
    public static Lab ClampToGamut(Lab c)
    {
        if (InGamut(c)) return c;
        float lo = 0, hi = 1;
        for (int i = 0; i < 16; i++)
        {
            float mid = (lo + hi) / 2;
            if (InGamut(new Lab(c.L, c.A * mid, c.B * mid))) lo = mid; else hi = mid;
        }
        return new Lab(c.L, c.A * lo, c.B * lo);
    }

    private static bool InGamut(Lab c)
    {
        var (r, g, b) = OkLab.ToLinear(c);
        const float e = 1e-4f;
        return r >= -e && r <= 1 + e && g >= -e && g <= 1 + e && b >= -e && b <= 1 + e;
    }

    public static Lab RotateHue(Lab c, float degrees, float dL = 0)
    {
        double h = Math.Atan2(c.B, c.A) + degrees * Math.PI / 180;
        float ch = c.Chroma;
        return new Lab(Math.Clamp(c.L + dL, 0, 1), (float)(ch * Math.Cos(h)), (float)(ch * Math.Sin(h)));
    }

    public static float HueDegrees(Lab c) => (float)((Math.Atan2(c.B, c.A) * 180 / Math.PI + 360) % 360);

    private static (Lab[] centers, int[] counts) KMeans(IReadOnlyList<Lab> pts)
    {
        // k-means++ initialisation with a fixed seed (deterministic results for the same art).
        var rng = new Random(12345);
        var centers = new Lab[K];
        centers[0] = pts[rng.Next(pts.Count)];
        var d2 = new double[pts.Count];
        for (int k = 1; k < K; k++)
        {
            double sum = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < k; j++) best = Math.Min(best, Lab.DistanceSq(pts[i], centers[j]));
                d2[i] = best; sum += best;
            }
            if (sum <= 0) { centers[k] = centers[0]; continue; }
            double r = rng.NextDouble() * sum;
            int pick = 0;
            for (; pick < pts.Count - 1; pick++) { r -= d2[pick]; if (r <= 0) break; }
            centers[k] = pts[pick];
        }

        var assign = new int[pts.Count];
        var counts = new int[K];
        for (int it = 0; it < Iterations; it++)
        {
            var sl = new double[K]; var sa = new double[K]; var sb = new double[K];
            Array.Clear(counts);
            for (int i = 0; i < pts.Count; i++)
            {
                int best = 0; double bd = double.MaxValue;
                for (int k = 0; k < K; k++)
                {
                    double d = Lab.DistanceSq(pts[i], centers[k]);
                    if (d < bd) { bd = d; best = k; }
                }
                assign[i] = best; counts[best]++;
                sl[best] += pts[i].L; sa[best] += pts[i].A; sb[best] += pts[i].B;
            }
            for (int k = 0; k < K; k++)
                if (counts[k] > 0) centers[k] = new Lab((float)(sl[k] / counts[k]), (float)(sa[k] / counts[k]), (float)(sb[k] / counts[k]));
        }
        return (centers, counts);
    }
}
