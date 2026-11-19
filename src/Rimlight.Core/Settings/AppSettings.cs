using System.Text.Json.Serialization;

namespace Rimlight.Core.Settings;

/// <summary>MusicSync follows the music; Idle (shown as "Ambient") runs an ambient effect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnimationMode>))]
public enum AnimationMode { MusicSync, Idle }

/// <summary>How synced lyrics are drawn.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LyricsStyle>))]
public enum LyricsStyle
{
    /// <summary>Floating dark glass card, words fly in, coloured glow.</summary>
    Card,
    /// <summary>The album cover fills the whole screen (live wallpaper or on top), giant bold lyrics over it.</summary>
    Album,
    /// <summary>Big kinetic words with highlight chips, no background.</summary>
    Bold,
    /// <summary>Legacy name for <see cref="Album"/> (kept so old settings files still load).</summary>
    FullScreen,
    /// <summary>One plain line, like subtitles.</summary>
    Subtitle,
    /// <summary>Words appear as stars on a star map, joined by lines; the current word big and glowing.</summary>
    Constellation,
    /// <summary>Your own picture as a living background, karaoke lyrics in its empty side.</summary>
    Picture,
}

[JsonConverter(typeof(JsonStringEnumConverter<LyricsPlacement>))]
public enum LyricsPlacement { Top, Middle, Bottom }

/// <summary>How Music Sync moves the light. Names match <c>FlowPattern</c> (except Auto).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MotionStyle>))]
public enum MotionStyle
{
    /// <summary>Follow the song: pick and change patterns by energy, flash on drops.</summary>
    Auto,
    Sweep, Twin, Mirror, Bounce, Burst, Chase, Pulse, Ripple, Rise,
}

/// <summary>
/// Everything the user can change. Plain data; persisted as JSON in %APPDATA%\Rimlight\settings.json.
/// Slider-style values are stored as 0..100 "levels" and mapped to pixels by <see cref="LightMetrics"/>.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public bool Active { get; set; } = true;

    /// <summary>"primary", "all", or a monitor device id (e.g. \\.\DISPLAY2).</summary>
    public string Display { get; set; } = "primary";

    // Defaults tuned to match the reference videos (see reference/NOTES.md).
    public string PrimaryColor { get; set; } = "#FF2E88";   // hot pink
    public string SecondaryColor { get; set; } = "#7B3CFF"; // violet
    public bool OverrideAlbumColor { get; set; } = false;

    public AnimationMode Animation { get; set; } = AnimationMode.MusicSync;
    public MotionStyle Motion { get; set; } = MotionStyle.Auto;
    /// <summary>Effect used in Ambient mode, and whenever no music is playing in Music Sync.</summary>
    public Rimlight.Core.Motion.AmbientPreset Ambient { get; set; } = Rimlight.Core.Motion.AmbientPreset.Glow;
    /// <summary>How many colours to take from album art: 0 = auto (as many as the cover really has, up to 4).</summary>
    public int AlbumColorCount { get; set; } = 0;
    public bool IdleBreathing { get; set; } = true;

    public double Thickness { get; set; } = 18;   // 0..100 → ~4.9 DIP thin neon core
    public double Glow { get; set; } = 25;        // 0..100 → ~37 DIP short halo (~5% of screen height)
    /// <summary>Guess the corner radius per display (rounded on laptop panels, square on monitors).</summary>
    public bool CornerRadiusAuto { get; set; } = true;
    /// <summary>0..100, used when <see cref="CornerRadiusAuto"/> is off.</summary>
    public double CornerRadius { get; set; } = 20;
    public bool AvoidTaskbar { get; set; } = false;

    public double Brightness { get; set; } = 100;     // 0..100
    public double BeatSensitivity { get; set; } = 50; // 0..100

    // Lyrics (fetched from LRCLIB; only song title/artist/album/length are sent).
    public bool ShowLyrics { get; set; } = true;
    public LyricsStyle LyricsStyle { get; set; } = LyricsStyle.Card;
    public LyricsPlacement LyricsPlacement { get; set; } = LyricsPlacement.Bottom;
    public double LyricsSize { get; set; } = 50; // 0..100
    /// <summary>Album / Constellation: false = live desktop wallpaper behind your windows, true = on top of everything.</summary>
    public bool LyricsOnTop { get; set; } = false;
    /// <summary>Background image for the Picture lyrics style (empty = the bundled default in %APPDATA%\Rimlight\backgrounds).</summary>
    public string LyricsPicture { get; set; } = "";
    /// <summary>Picture style: centre of the lyrics box as a fraction of the screen (X &lt; 0 = automatic placement).</summary>
    public double LyricsBoxX { get; set; } = -1;
    public double LyricsBoxY { get; set; } = 0.5;
    /// <summary>Auto-hide the Windows taskbar while lyrics are on screen.</summary>
    public bool HideTaskbarWithLyrics { get; set; } = true;
    /// <summary>Hide lyrics while the mouse/keyboard is being used; bring them back after 10 s untouched.</summary>
    public bool HideLyricsWhileUsingPc { get; set; } = true;

    public bool LaunchAtLogin { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+L";
    /// <summary>Hide the light while a full-screen video, game or presentation is in front.</summary>
    public bool HideOnFullscreenVideo { get; set; } = false;
    public string PreferredPlayer { get; set; } = "Auto";
    public bool AutoCheckUpdates { get; set; } = true;
    public bool WelcomeShown { get; set; } = false;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>Maps 0..100 slider levels to physical sizes (in DIPs; multiply by DPI scale).</summary>
public static class LightMetrics
{
    /// <summary>Hairline (1 px) → broad band (120 px). Quadratic so the low end has fine control.</summary>
    public static float ThicknessDip(double level)
    {
        var v = (float)Math.Clamp(level / 100.0, 0, 1);
        return 1f + 119f * v * v;
    }

    /// <summary>No glow → big soft wash (~600 px falloff).</summary>
    public static float GlowDip(double level)
    {
        var v = (float)Math.Clamp(level / 100.0, 0, 1);
        return 600f * v * v;
    }

    /// <summary>Square (0) → very round (64 px).</summary>
    public static float CornerRadiusDip(double level)
    {
        var v = (float)Math.Clamp(level / 100.0, 0, 1);
        return 64f * v;
    }
}
