using Rimlight.Core.Color;

namespace Rimlight.Core.Motion;

/// <summary>Light effects that run without music.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AmbientPreset>))]
public enum AmbientPreset
{
    Glow, Rainbow, Aurora, Comet, Fire, Ocean, Police, Neon, Heartbeat, Sunset, Candy, Lava,
}

/// <summary>
/// Time-driven light effects (no audio needed). Each preset moves the lit arcs and, for most, brings its own
/// colour palette. Output uses the same <see cref="FlowFrame"/> as the music engine so the renderer can blend
/// between them. Pure and deterministic.
/// </summary>
public sealed class AmbientEngine
{
    public AmbientPreset Preset { get; set; } = AmbientPreset.Glow;
    public bool Breathing { get; set; } = true;
    public float Aspect { get; set; } = 16f / 9f;

    /// <summary>Gradient rotation speed for the current preset (loops per second).</summary>
    public float RotationSpeed => Preset switch
    {
        AmbientPreset.Rainbow => 0.08f,
        AmbientPreset.Aurora => 0.02f,
        AmbientPreset.Ocean => 0.012f,
        AmbientPreset.Sunset => 0.006f,
        AmbientPreset.Candy => 0.05f,
        AmbientPreset.Lava => 0.01f,
        _ => 0.004f,
    };

    /// <summary>True when the effect animates every frame (Glow without breathing is static).</summary>
    public bool Animated => Preset != AmbientPreset.Glow || Breathing;

    /// <summary>The preset's own colours, or null to use the album/user colours.</summary>
    public Lab[]? Colors => Preset switch
    {
        AmbientPreset.Rainbow => Hex("#FF3B3B", "#FFD23B", "#3BFF7A", "#3B8BFF"),
        AmbientPreset.Aurora => Hex("#1DE9B6", "#5CFF8F", "#8A5CFF", "#2D7BFF"),
        AmbientPreset.Fire => Hex("#FF3D00", "#FF8A00", "#FFC400", "#D50000"),
        AmbientPreset.Ocean => Hex("#00B4FF", "#00E5D4", "#2D5BFF", "#7FDBFF"),
        AmbientPreset.Police => Hex("#FF1E2D", "#1E5BFF"),
        AmbientPreset.Heartbeat => Hex("#FF2E4D", "#FF5C8A"),
        AmbientPreset.Sunset => Hex("#FF7A2D", "#FF3D8B", "#9B3DFF", "#2D3DFF"),
        AmbientPreset.Candy => Hex("#FF7AD9", "#7AFFD2", "#B98CFF", "#FFC27A"),
        AmbientPreset.Lava => Hex("#FF2D00", "#FF6A00", "#FF0080"),
        _ => null, // Glow, Comet, Neon: album / your colours
    };

    private static Lab[] Hex(params string[] hex) =>
        hex.Select(h => Rgb.TryParseHex(h, out var c) ? OkLab.FromRgb(c) : default).ToArray();

    public void Fill(double t, ref FlowFrame f)
    {
        f.Arcs ??= new Arc[FlowFrame.MaxArcs];
        Array.Clear(f.Arcs);
        f.Boost = 0;
        f.Rotation = 0;
        int n = 0;
        var arcs = f.Arcs;
        void Add(double pos, float width, float gain)
        {
            if (n < arcs.Length) arcs[n++] = new Arc((float)(pos - Math.Floor(pos)), Math.Clamp(width, 0, 1), Math.Clamp(gain, 0, 1));
        }
        float breathe = Breathing ? 0.85f + 0.15f * (float)Math.Sin(t * 2 * Math.PI / 8.0) : 1f;

        switch (Preset)
        {
            case AmbientPreset.Rainbow:
            case AmbientPreset.Sunset:
                f.Floor = breathe;
                break;

            case AmbientPreset.Aurora:
                // Two wide curtains of light drifting at different speeds over a soft base.
                Add(t * 0.031, 0.45f, 1);
                Add(0.5 - t * 0.019, 0.38f, 0.9f);
                f.Floor = 0.45f;
                break;

            case AmbientPreset.Comet:
            {
                // A bright head with a fading tail racing around.
                double head = t * 0.32;
                Add(head, 0.10f, 1);
                for (int i = 1; i <= 4; i++) Add(head - i * 0.045, 0.09f - i * 0.012f, 0.8f - i * 0.17f);
                f.Floor = 0.04f;
                f.Boost = 0.25f;
                break;
            }

            case AmbientPreset.Fire:
                // Flickering tongues of flame along the whole border.
                for (int i = 0; i < 6; i++)
                {
                    double p = i / 6.0 + 0.04 * Noise(t * 0.7 + i * 3.1);
                    float g = 0.55f + 0.45f * Noise01(t * (2.3 + i * 0.37) + i * 11.7);
                    Add(p, 0.12f + 0.06f * Noise01(t * 1.1 + i), g);
                }
                f.Floor = 0.5f + 0.15f * Noise01(t * 3.7);
                break;

            case AmbientPreset.Ocean:
                // Slow swells rolling around the screen.
                for (int i = 0; i < 3; i++) Add(t * 0.04 + i / 3.0 + 0.05 * Math.Sin(t * 0.6 + i), 0.24f, 0.9f);
                f.Floor = 0.45f;
                break;

            case AmbientPreset.Police:
            {
                // Left half red, right half blue, double flashes alternating (soft edges, not a harsh strobe).
                double phase = t * 1.2 % 1.0;
                bool leftOn = phase < 0.5 && (phase % 0.25) < 0.13;
                bool rightOn = phase >= 0.5 && (phase % 0.25) < 0.13;
                double sway = 0.025 * Math.Sin(t * 5.0); // wig-wag
                Add(EdgeCentre(3) + sway, 0.48f, leftOn ? 1 : 0.08f);
                Add(EdgeCentre(1) - sway, 0.48f, rightOn ? 1 : 0.08f);
                f.Floor = 0.02f;
                f.Rotation = 0;
                break;
            }

            case AmbientPreset.Neon:
            {
                // A steady neon tube that occasionally flickers.
                double cycle = t % 5.3;
                bool flicker = cycle > 4.6 && Noise01(t * 40) > 0.45;
                float hum = 0.9f + 0.1f * Noise01(t * 1.7);           // gentle electric hum
                f.Floor = flicker ? 0.25f : hum;
                Add(t * 0.05, 0.6f, 1);                               // a slightly brighter section slowly travels
                break;
            }

            case AmbientPreset.Heartbeat:
            {
                // Lub-dub at ~66 BPM.
                double beat = t * 1.1 % 1.0;
                double lub = Math.Exp(-Math.Pow((beat - 0.08) / 0.05, 2));
                double dub = 0.7 * Math.Exp(-Math.Pow((beat - 0.30) / 0.05, 2));
                f.Floor = (float)(0.25 + 0.75 * Math.Max(lub, dub));
                f.Boost = (float)(0.4 * Math.Max(lub, dub));
                break;
            }

            case AmbientPreset.Candy:
                Add(t * 0.11, 0.3f, 1);
                Add(t * 0.11 + 0.5, 0.3f, 1);
                f.Floor = 0.35f;
                break;

            case AmbientPreset.Lava:
                // Slow, blobby lava-lamp movement.
                for (int i = 0; i < 3; i++) Add(i / 3.0 + 0.12 * Math.Sin(t * 0.21 + i * 2.1) + 0.05 * Math.Sin(t * 0.53 + i), 0.28f, 0.95f);
                f.Floor = 0.3f;
                break;

            default: // Glow
                f.Floor = breathe;
                break;
        }
    }

    private float EdgeCentre(int edge)
    {
        float w = Aspect, h = 1, l = 2 * (w + h);
        return edge switch { 0 => (w / 2) / l, 1 => (w + h / 2) / l, 2 => (w + h + w / 2) / l, _ => (2 * w + h + h / 2) / l };
    }

    /// <summary>Smooth pseudo-random noise in −1..1 (sum of incommensurate sines).</summary>
    private static double Noise(double x) =>
        (Math.Sin(x * 1.0) + 0.6 * Math.Sin(x * 2.17 + 1.3) + 0.35 * Math.Sin(x * 4.73 + 2.9)) / 1.95;

    private static float Noise01(double x) => (float)(0.5 + 0.5 * Noise(x));
}
