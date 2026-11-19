namespace Rimlight.Core.Color;

/// <summary>An sRGB color with 0..1 float channels (gamma-encoded).</summary>
public readonly record struct Rgb(float R, float G, float B)
{
    public static Rgb FromBytes(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);

    /// <summary>Parses "#RRGGBB" or "RRGGBB". Returns false on bad input.</summary>
    public static bool TryParseHex(string? hex, out Rgb rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        rgb = FromBytes((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public string ToHex()
    {
        static int Byte(float c) => (int)MathF.Round(Math.Clamp(c, 0f, 1f) * 255f);
        return $"#{Byte(R):X2}{Byte(G):X2}{Byte(B):X2}";
    }
}

/// <summary>A color in the OKLab perceptual space (Björn Ottosson, 2020).</summary>
public readonly record struct Lab(float L, float A, float B)
{
    public float Chroma => MathF.Sqrt(A * A + B * B);

    public static Lab Lerp(Lab x, Lab y, float t) =>
        new(x.L + (y.L - x.L) * t, x.A + (y.A - x.A) * t, x.B + (y.B - x.B) * t);

    public static float DistanceSq(Lab x, Lab y)
    {
        float dl = x.L - y.L, da = x.A - y.A, db = x.B - y.B;
        return dl * dl + da * da + db * db;
    }
}

public static class OkLab
{
    public static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    public static Lab FromRgb(Rgb c) => FromLinear(SrgbToLinear(c.R), SrgbToLinear(c.G), SrgbToLinear(c.B));

    public static Lab FromLinear(float r, float g, float b)
    {
        float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
        float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
        float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;
        l = MathF.Cbrt(l); m = MathF.Cbrt(m); s = MathF.Cbrt(s);
        return new Lab(
            0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    public static (float r, float g, float b) ToLinear(Lab c)
    {
        float l_ = c.L + 0.3963377774f * c.A + 0.2158037573f * c.B;
        float m_ = c.L - 0.1055613458f * c.A - 0.0638541728f * c.B;
        float s_ = c.L - 0.0894841775f * c.A - 1.2914855480f * c.B;
        float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        return (
            +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
            -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
            -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    public static Rgb ToRgb(Lab c)
    {
        var (r, g, b) = ToLinear(c);
        return new Rgb(
            LinearToSrgb(Math.Clamp(r, 0f, 1f)),
            LinearToSrgb(Math.Clamp(g, 0f, 1f)),
            LinearToSrgb(Math.Clamp(b, 0f, 1f)));
    }
}
