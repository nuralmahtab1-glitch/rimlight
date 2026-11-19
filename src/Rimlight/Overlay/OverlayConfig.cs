using Rimlight.Core.Color;
using Rimlight.Core.Settings;

namespace Rimlight.Overlay;

/// <summary>
/// Immutable snapshot of everything the renderer needs. The UI thread builds a new one on every
/// settings change and hands it to <see cref="OverlayRenderer.Update"/>; the render thread only reads it.
/// </summary>
public sealed record OverlayConfig
{
    public bool Active { get; init; } = true;
    public string Display { get; init; } = "primary";
    public float ThicknessDip { get; init; } = 5;
    public float GlowDip { get; init; } = 37;
    /// <summary>Corner radius in DIPs, or a negative value for "auto" (per display).</summary>
    public float RadiusDip { get; init; } = -1;
    public bool AvoidTaskbar { get; init; }
    public float Brightness { get; init; } = 1f;
    public Lab Primary { get; init; } = OkLab.FromRgb(new Rgb(1f, 0.18f, 0.53f));
    public Lab Secondary { get; init; } = OkLab.FromRgb(new Rgb(0.48f, 0.24f, 1f));
    /// <summary>2–4 colours around the loop (best first). Overrides Primary/Secondary when set.</summary>
    public Lab[]? Colors { get; init; }
    public AnimationMode Animation { get; init; } = AnimationMode.MusicSync;
    public MotionStyle Motion { get; init; } = MotionStyle.Sweep;
    public bool IdleBreathing { get; init; } = true;
    public Rimlight.Core.Motion.AmbientPreset Ambient { get; init; } = Rimlight.Core.Motion.AmbientPreset.Glow;
    /// <summary>How long to cross-fade (in OKLab) when Primary/Secondary change: ~0.8 s for track changes, quick for manual edits.</summary>
    public float ColorFadeSeconds { get; init; } = 0.8f;
    /// <summary>Hide and stop drawing without changing the user's on/off choice (locked, screen off, full-screen app).</summary>
    public bool Suspended { get; init; }
    /// <summary>Battery Saver: cap animation at ~30 fps.</summary>
    public bool LowPower { get; init; }
}
