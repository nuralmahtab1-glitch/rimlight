using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using Rimlight.Core.Settings;

namespace Rimlight.SettingsUI;

public sealed record DisplayOption(string Id, string Name);

/// <summary>
/// Binds the flyout controls to <see cref="AppSettings"/>. Every change is applied live
/// (renderer update) and persisted (debounced) via the callback.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _s;
    private readonly Action _changed;
    private float _bassLevel, _beatFlash;
    private string _listeningText = "Starting…";
    private bool _musicDetected;

    public SettingsViewModel(AppSettings settings, Action changed)
    {
        _s = settings;
        _changed = changed;
        Primary = new ColorEditViewModel("Primary", () => _s.PrimaryColor, v => { _s.PrimaryColor = v; ColorChanged(); });
        Secondary = new ColorEditViewModel("Secondary", () => _s.SecondaryColor, v => { _s.SecondaryColor = v; ColorChanged(); });
        ChatColor1 = new ColorEditViewModel("Colour 1", () => _s.ChatColor1, v => { _s.ChatColor1 = v; _changed(); });
        ChatColor2 = new ColorEditViewModel("Colour 2", () => _s.ChatColor2, v => { _s.ChatColor2 = v; _changed(); });
        SelectDisplayCommand = new RelayCommand(p => { if (p is string id) Display = id; });
        SwapColorsCommand = new RelayCommand(_ =>
        {
            (_s.PrimaryColor, _s.SecondaryColor) = (_s.SecondaryColor, _s.PrimaryColor);
            Primary.LoadFromSettings(); Secondary.LoadFromSettings();
            ColorChanged();
        });
        ResetLookCommand = new RelayCommand(_ => ResetLook());
        Displays.Add(new DisplayOption("primary", "Main display"));
        Displays.Add(new DisplayOption("all", "All displays"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------------------------------------------------------------- power

    public bool Active
    {
        get => _s.Active;
        set { if (Set(_s.Active, value, v => _s.Active = v)) Raise(nameof(StatusText)); }
    }

    public string StatusText => !_s.Active ? "Off"
        : _restReason is not null ? $"Hidden · {_restReason}"
        : _s.Animation == AnimationMode.Idle ? $"Active · {AmbientName}"
        : _musicDetected ? "Active · Following the beat"
        : "Active · Waiting for music";

    // ---------------------------------------------------------------- display

    public ObservableCollection<DisplayOption> Displays { get; } = new();
    public ICommand SelectDisplayCommand { get; }

    public string Display
    {
        get => _s.Display;
        set { if (Set(_s.Display, value, v => _s.Display = v)) Raise(nameof(DisplayName)); }
    }

    public string DisplayName => Displays.FirstOrDefault(d => d.Id == _s.Display)?.Name ?? "Main display";

    public void SetDisplays(IEnumerable<DisplayOption> monitors)
    {
        while (Displays.Count > 2) Displays.RemoveAt(2);
        foreach (var m in monitors) Displays.Add(m);
        Raise(nameof(DisplayName));
    }

    // ---------------------------------------------------------------- light shape

    public double Thickness { get => _s.Thickness; set => Set(_s.Thickness, value, v => _s.Thickness = v); }
    public double Glow { get => _s.Glow; set => Set(_s.Glow, value, v => _s.Glow = v); }
    public double Brightness { get => _s.Brightness; set => Set(_s.Brightness, value, v => _s.Brightness = v); }

    public bool CornerRadiusAuto
    {
        get => _s.CornerRadiusAuto;
        set { if (Set(_s.CornerRadiusAuto, value, v => _s.CornerRadiusAuto = v)) Raise(nameof(CornerRadiusManual)); }
    }
    public bool CornerRadiusManual => !_s.CornerRadiusAuto;
    public double CornerRadius { get => _s.CornerRadius; set => Set(_s.CornerRadius, value, v => _s.CornerRadius = v); }

    public bool AvoidTaskbar { get => _s.AvoidTaskbar; set => Set(_s.AvoidTaskbar, value, v => _s.AvoidTaskbar = v); }

    // ---------------------------------------------------------------- colors

    public ColorEditViewModel Primary { get; }
    public ColorEditViewModel Secondary { get; }
    public ICommand SwapColorsCommand { get; }

    private Color[]? _eff;
    private bool _albumColorsActive;

    /// <summary>The colors the light is actually using right now (album colors or the user's).</summary>
    public Brush GradientBrush
    {
        get
        {
            var cols = _eff is { Length: >= 2 } ? _eff : [Primary.Color, Secondary.Color];
            var stops = new GradientStopCollection();
            for (int i = 0; i < cols.Length; i++)
            {
                double mid = (i + 0.5) / cols.Length;
                stops.Add(new GradientStop(cols[i], Math.Max(0, mid - 0.12)));
                stops.Add(new GradientStop(cols[i], Math.Min(1, mid + 0.12)));
            }
            return new LinearGradientBrush(stops, 0);
        }
    }

    public bool AlbumColorsActive => _albumColorsActive;

    public string ColorSourceText => _albumColorsActive
        ? $"Using {(_eff?.Length ?? 2)} colors from the album art"
        : "Using your colors";

    public void SetEffectiveColors(Color[] colors, bool fromAlbum)
    {
        _eff = colors; _albumColorsActive = fromAlbum;
        Raise(nameof(GradientBrush)); Raise(nameof(AlbumColorsActive)); Raise(nameof(ColorSourceText));
    }

    // ---------------------------------------------------------------- now playing

    private string _nowTitle = "Nothing playing";
    private string? _nowSubtitle;
    private ImageSource? _nowThumb;

    public string NowTitle { get => _nowTitle; private set { _nowTitle = value; Raise(); } }
    public string NowSubtitle => _nowSubtitle ?? ListeningText;
    public ImageSource? NowThumb { get => _nowThumb; private set { _nowThumb = value; Raise(); Raise(nameof(HasNowThumb)); } }
    public bool HasNowThumb => _nowThumb is not null;

    public void SetNowPlaying(string? title, string? artist, string? player, bool playing, ImageSource? thumb)
    {
        if (title is null && artist is null)
        {
            NowTitle = "Nothing playing";
            _nowSubtitle = null;
        }
        else
        {
            NowTitle = string.IsNullOrEmpty(title) ? "Unknown track" : title;
            var parts = new[] { artist, player, playing ? null : "Paused" }.Where(p => !string.IsNullOrEmpty(p));
            _nowSubtitle = string.Join(" · ", parts);
        }
        NowThumb = thumb;
        Raise(nameof(NowSubtitle));
    }

    // ---------------------------------------------------------------- preferred player

    public List<(string Id, string Name)> Players { get; } = new() { ("Auto", "Auto (whatever's playing)") };

    public void SetPlayers(IEnumerable<(string Id, string Name)> detected)
    {
        Players.RemoveRange(1, Players.Count - 1);
        foreach (var p in detected) Players.Add(p);
        if (_s.PreferredPlayer != "Auto" && Players.All(p => p.Id != _s.PreferredPlayer))
            Players.Add((_s.PreferredPlayer, Rimlight.NowPlaying.MediaSessionWatcher.FriendlyName(_s.PreferredPlayer) + " (not open)"));
        Raise(nameof(PreferredPlayerName));
    }

    public string PreferredPlayer
    {
        get => _s.PreferredPlayer;
        set { if (Set(_s.PreferredPlayer, value, v => _s.PreferredPlayer = v)) Raise(nameof(PreferredPlayerName)); }
    }

    public string PreferredPlayerName => _s.PreferredPlayer == "Auto" ? "Auto"
        : Rimlight.NowPlaying.MediaSessionWatcher.FriendlyName(_s.PreferredPlayer);

    public bool OverrideAlbumColor
    {
        get => _s.OverrideAlbumColor;
        set => Set(_s.OverrideAlbumColor, value, v => _s.OverrideAlbumColor = v);
    }

    private void ColorChanged()
    {
        Raise(nameof(GradientBrush));
        _changed();
    }

    // ---------------------------------------------------------------- motion

    public bool IsMusicSync
    {
        get => _s.Animation == AnimationMode.MusicSync;
        set { if (value) SetAnimation(AnimationMode.MusicSync); }
    }

    public bool IsIdle
    {
        get => _s.Animation == AnimationMode.Idle;
        set { if (value) SetAnimation(AnimationMode.Idle); }
    }

    private void SetAnimation(AnimationMode mode)
    {
        if (_s.Animation == mode) return;
        _s.Animation = mode;
        Raise(nameof(IsMusicSync)); Raise(nameof(IsIdle)); Raise(nameof(StatusText)); Raise(nameof(AmbientLabel));
        _changed();
    }

    public static IReadOnlyList<(MotionStyle Style, string Name, string Description)> MotionStyles { get; } =
    [
        (MotionStyle.Auto, "Auto · follows the song", "Changes patterns with the music's energy and flashes on drops"),
        (MotionStyle.Sweep, "Sweep", "One big arc travels around the screen"),
        (MotionStyle.Twin, "Twin", "Two arcs chase each other on opposite sides"),
        (MotionStyle.Mirror, "Mirror", "Two arcs rise from the bottom and meet at the top"),
        (MotionStyle.Bounce, "Bounce", "An arc swings back and forth"),
        (MotionStyle.Ripple, "Ripple", "Each beat sends a ripple of light both ways"),
        (MotionStyle.Rise, "Rise", "A VU meter: light climbs the sides with the bass"),
        (MotionStyle.Burst, "Burst", "The middle of each edge flashes on the beat"),
        (MotionStyle.Chase, "Chase", "Fast streaks race around (high energy)"),
        (MotionStyle.Pulse, "Pulse", "The whole border throbs with the bass"),
    ];

    public static IReadOnlyList<(Rimlight.Core.Motion.AmbientPreset Preset, string Name, string Description)> AmbientPresets { get; } =
    [
        (Rimlight.Core.Motion.AmbientPreset.Glow, "Glow", "A steady glow in your colours (with gentle breathing if on)"),
        (Rimlight.Core.Motion.AmbientPreset.Rainbow, "Rainbow", "Every colour flowing around the screen"),
        (Rimlight.Core.Motion.AmbientPreset.Aurora, "Aurora", "Northern-lights curtains drifting in teal, green and violet"),
        (Rimlight.Core.Motion.AmbientPreset.Comet, "Comet", "A bright comet with a tail racing around"),
        (Rimlight.Core.Motion.AmbientPreset.Fire, "Fire", "Flickering flames all along the edges"),
        (Rimlight.Core.Motion.AmbientPreset.Ocean, "Ocean", "Slow blue swells rolling around"),
        (Rimlight.Core.Motion.AmbientPreset.Police, "Police", "Red and blue lights flashing left and right"),
        (Rimlight.Core.Motion.AmbientPreset.Neon, "Neon", "A humming neon tube that sometimes flickers"),
        (Rimlight.Core.Motion.AmbientPreset.Heartbeat, "Heartbeat", "A red glow that beats like a heart"),
        (Rimlight.Core.Motion.AmbientPreset.Sunset, "Sunset", "Orange, pink and purple, slowly shifting"),
        (Rimlight.Core.Motion.AmbientPreset.Candy, "Candy", "Pastel colours spinning around"),
        (Rimlight.Core.Motion.AmbientPreset.Lava, "Lava lamp", "Hot blobs of light slowly wandering"),
    ];

    public Rimlight.Core.Motion.AmbientPreset Ambient
    {
        get => _s.Ambient;
        set { if (Set(_s.Ambient, value, v => _s.Ambient = v)) { Raise(nameof(AmbientName)); Raise(nameof(AmbientDescription)); } }
    }

    public string AmbientName => AmbientPresets.First(a => a.Preset == _s.Ambient).Name;
    public string AmbientDescription => AmbientPresets.First(a => a.Preset == _s.Ambient).Description;
    public string AmbientLabel => _s.Animation == AnimationMode.Idle ? "Effect" : "When no music plays";

    public MotionStyle Motion
    {
        get => _s.Motion;
        set { if (Set(_s.Motion, value, v => _s.Motion = v)) { Raise(nameof(MotionName)); Raise(nameof(MotionDescription)); } }
    }

    public string MotionName => MotionStyles.First(m => m.Style == _s.Motion).Name;
    public string MotionDescription => MotionStyles.First(m => m.Style == _s.Motion).Description;

    // Album colour count: Auto (0) / 2 / 3 / 4
    public bool IsColorsAuto { get => _s.AlbumColorCount == 0; set { if (value) SetColorCount(0); } }
    public bool IsColors2 { get => _s.AlbumColorCount == 2; set { if (value) SetColorCount(2); } }
    public bool IsColors3 { get => _s.AlbumColorCount == 3; set { if (value) SetColorCount(3); } }
    public bool IsColors4 { get => _s.AlbumColorCount == 4; set { if (value) SetColorCount(4); } }

    private void SetColorCount(int n)
    {
        if (_s.AlbumColorCount == n) return;
        _s.AlbumColorCount = n;
        Raise(nameof(IsColorsAuto)); Raise(nameof(IsColors2)); Raise(nameof(IsColors3)); Raise(nameof(IsColors4));
        _changed();
    }

    // ---------------------------------------------------------------- lyrics

    public static IReadOnlyList<(LyricsStyle Style, string Name, string Description)> LyricsStyles { get; } =
    [
        (LyricsStyle.Picture, "Picture", "Your picture comes alive (drift, floating lights), karaoke lyrics in its empty side"),
        (LyricsStyle.Album, "Album wallpaper", "The album cover fills your screen and moves to the beat, giant lyrics over it"),
        (LyricsStyle.Constellation, "Constellation", "Words appear as stars joined by lines, the current word glowing"),
        (LyricsStyle.ChatBlack, "Chat screen", "Black screen with a moving frame of light, each line pops up as a message bubble"),
        (LyricsStyle.Card, "Card", "Floating glass card, words fly in"),
        (LyricsStyle.Bold, "Bold", "Big kinetic words, no background"),
        (LyricsStyle.Subtitle, "Subtitle", "One simple line, good while working"),
    ];

    /// <summary>Album wallpaper and Constellation fill the screen; the others are small overlays.</summary>
    public bool IsStageStyle => _s.LyricsStyle is LyricsStyle.Album or LyricsStyle.FullScreen or LyricsStyle.Constellation or LyricsStyle.Picture or LyricsStyle.ChatBlack;

    public bool IsChatScreen => _s.LyricsStyle == LyricsStyle.ChatBlack;
    public bool IsChatDefaultColors => IsChatScreen && _s.ChatEdgeColors == ChatColorMode.Default;

    public ColorEditViewModel ChatColor1 { get; }
    public ColorEditViewModel ChatColor2 { get; }

    public static IReadOnlyList<(ChatBubbleLook Look, string Name)> ChatBubbleOptions { get; } =
    [
        (ChatBubbleLook.TwoTone, "Two-tone (dark + blue-grey)"),
        (ChatBubbleLook.IMessage, "iMessage blue"),
    ];

    public ChatBubbleLook ChatBubbles
    {
        get => _s.ChatBubbles;
        set { if (Set(_s.ChatBubbles, value, v => _s.ChatBubbles = v)) Raise(nameof(ChatBubblesName)); }
    }
    public string ChatBubblesName => ChatBubbleOptions.First(o => o.Look == _s.ChatBubbles).Name;

    public static IReadOnlyList<(ChatColorMode Mode, string Name)> ChatEdgeOptions { get; } =
    [
        (ChatColorMode.Default, "Default (pink + violet)"),
        (ChatColorMode.Album, "Album colours"),
        (ChatColorMode.White, "White"),
        (ChatColorMode.Sunset, "Sunset (orange + pink)"),
        (ChatColorMode.Ocean, "Ocean (cyan + blue)"),
        (ChatColorMode.Aurora, "Aurora (green + violet)"),
        (ChatColorMode.Ember, "Ember (gold + red)"),
        (ChatColorMode.Candy, "Candy (pink + sky)"),
        (ChatColorMode.LimePop, "Lime pop (lime + cyan)"),
    ];

    public ChatColorMode ChatEdgeColors
    {
        get => _s.ChatEdgeColors;
        set { if (Set(_s.ChatEdgeColors, value, v => _s.ChatEdgeColors = v)) { Raise(nameof(ChatEdgeColorsName)); Raise(nameof(IsChatDefaultColors)); } }
    }
    public string ChatEdgeColorsName => ChatEdgeOptions.First(o => o.Mode == _s.ChatEdgeColors).Name;

    public bool IsPictureStyle => _s.LyricsStyle == LyricsStyle.Picture;
    public static IReadOnlyList<(ChatMotion Motion, string Name)> ChatMotionOptions { get; } =
    [
        (ChatMotion.Classic, "Classic (wide, upper middle)"),
        (ChatMotion.Reel, "Reel (blue pill, words pop in)"),
        (ChatMotion.Strips, "Strips (dark pills, stacked)"),
    ];

    public ChatMotion ChatMotion
    {
        get => _s.ChatMotion;
        set { if (Set(_s.ChatMotion, value, v => _s.ChatMotion = v)) Raise(nameof(ChatMotionName)); }
    }
    public string ChatMotionName => ChatMotionOptions.First(o => o.Motion == _s.ChatMotion).Name;

    // ---- saved presets
    /// <summary>Looks that come with the app. "Purple girl" = the bundled picture with shake 36 and text size 60.</summary>
    private static readonly LookPreset[] BuiltInPresets =
    [
        new LookPreset { Name = "Purple girl", Style = LyricsStyle.Picture, Picture = "", Motion = 36, Size = 60, BuiltIn = true },
    ];

    public IReadOnlyList<LookPreset> Presets => BuiltInPresets.Concat(_s.Presets).ToList();

    public void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || BuiltInPresets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _s.Presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _s.Presets.Add(new LookPreset
        {
            Name = name, Style = _s.LyricsStyle, Picture = _s.LyricsPicture, Motion = _s.BackgroundMotion, Size = _s.LyricsSize,
            OnTop = _s.LyricsOnTop, EdgeColors = _s.ChatEdgeColors, Color1 = _s.ChatColor1, Color2 = _s.ChatColor2,
            Bubbles = _s.ChatBubbles, Motion2 = _s.ChatMotion,
        });
        Raise(nameof(Presets));
        _changed();
    }

    public void ApplyPreset(LookPreset p)
    {
        _s.LyricsStyle = p.Style; _s.LyricsPicture = p.Picture; _s.BackgroundMotion = p.Motion; _s.LyricsSize = p.Size;
        _s.LyricsOnTop = p.OnTop; _s.ChatEdgeColors = p.EdgeColors; _s.ChatColor1 = p.Color1; _s.ChatColor2 = p.Color2;
        _s.ChatBubbles = p.Bubbles; _s.ChatMotion = p.Motion2;
        _s.PicturePresetApplied = true;
        ChatColor1.LoadFromSettings(); ChatColor2.LoadFromSettings();
        Refresh();
        _changed();
    }

    public void DeletePreset(LookPreset p) { _s.Presets.Remove(p); Raise(nameof(Presets)); _changed(); }

    public string LyricsPictureName => string.IsNullOrWhiteSpace(_s.LyricsPicture) ? "Default picture" : System.IO.Path.GetFileName(_s.LyricsPicture);

    public string LyricsPicture
    {
        get => _s.LyricsPicture;
        set { if (Set(_s.LyricsPicture, value, v => _s.LyricsPicture = v)) { Raise(nameof(LyricsPictureName)); Raise(nameof(LyricsPicturePreview)); } }
    }

    /// <summary>The picture actually used (yours or the default), for the placement preview.</summary>
    public string LyricsPicturePreview => string.IsNullOrWhiteSpace(_s.LyricsPicture) ? Rimlight.Lyrics.LyricsController.DefaultPicture : _s.LyricsPicture;

    public double BackgroundMotion { get => _s.BackgroundMotion; set => Set(_s.BackgroundMotion, value, v => _s.BackgroundMotion = v); }

    public double LyricsBoxX => _s.LyricsBoxX;
    public double LyricsBoxY => _s.LyricsBoxY;
    public bool LyricsBoxAuto => _s.LyricsBoxX < 0;

    public void SetLyricsBox(double x, double y)
    {
        _s.LyricsBoxX = x < 0 ? -1 : Math.Clamp(x, 0, 1);
        _s.LyricsBoxY = Math.Clamp(y, 0, 1);
        Raise(nameof(LyricsBoxX)); Raise(nameof(LyricsBoxY)); Raise(nameof(LyricsBoxAuto));
        _changed();
    }

    public bool LyricsOnTop { get => _s.LyricsOnTop; set => Set(_s.LyricsOnTop, value, v => _s.LyricsOnTop = v); }

    public bool HideLyricsWhileUsingPc { get => _s.HideLyricsWhileUsingPc; set => Set(_s.HideLyricsWhileUsingPc, value, v => _s.HideLyricsWhileUsingPc = v); }

    public bool HideTaskbarWithLyrics { get => _s.HideTaskbarWithLyrics; set => Set(_s.HideTaskbarWithLyrics, value, v => _s.HideTaskbarWithLyrics = v); }

    public bool ShowLyrics { get => _s.ShowLyrics; set => Set(_s.ShowLyrics, value, v => _s.ShowLyrics = v); }

    public LyricsStyle LyricsStyle
    {
        get => _s.LyricsStyle;
        set
        {
            if (value == LyricsStyle.Picture && !_s.PicturePresetApplied && string.IsNullOrWhiteSpace(_s.LyricsPicture))
            {
                // Picture style's starting preset (only with the bundled picture, only the first time).
                _s.PicturePresetApplied = true;
                _s.BackgroundMotion = 36;
                _s.LyricsSize = 60;
                Raise(nameof(BackgroundMotion)); Raise(nameof(LyricsSize));
            }
            if (Set(_s.LyricsStyle, value, v => _s.LyricsStyle = v)) { Raise(nameof(LyricsStyleName)); Raise(nameof(LyricsStyleDescription)); Raise(nameof(LyricsPlacementVisible)); Raise(nameof(IsStageStyle)); Raise(nameof(IsPictureStyle)); Raise(nameof(IsChatScreen)); Raise(nameof(IsChatDefaultColors)); } }
    }

    private (LyricsStyle Style, string Name, string Description) CurrentLyricsStyle =>
        LyricsStyles.FirstOrDefault(l => l.Style == (_s.LyricsStyle == LyricsStyle.FullScreen ? LyricsStyle.Album : _s.LyricsStyle));
    public string LyricsStyleName => CurrentLyricsStyle.Name ?? "Card";
    public string LyricsStyleDescription => CurrentLyricsStyle.Description ?? "";
    public bool LyricsPlacementVisible => !IsStageStyle;

    public bool IsLyricsTop { get => _s.LyricsPlacement == LyricsPlacement.Top; set { if (value) SetPlacement(LyricsPlacement.Top); } }
    public bool IsLyricsMiddle { get => _s.LyricsPlacement == LyricsPlacement.Middle; set { if (value) SetPlacement(LyricsPlacement.Middle); } }
    public bool IsLyricsBottom { get => _s.LyricsPlacement == LyricsPlacement.Bottom; set { if (value) SetPlacement(LyricsPlacement.Bottom); } }

    private void SetPlacement(LyricsPlacement p)
    {
        if (_s.LyricsPlacement == p) return;
        _s.LyricsPlacement = p;
        Raise(nameof(IsLyricsTop)); Raise(nameof(IsLyricsMiddle)); Raise(nameof(IsLyricsBottom));
        _changed();
    }

    public double LyricsSize { get => _s.LyricsSize; set => Set(_s.LyricsSize, value, v => _s.LyricsSize = v); }

    private string _lyricsStatus = "Play a song to see its lyrics";
    public string LyricsStatus { get => _lyricsStatus; set { _lyricsStatus = value; Raise(); } }

    public double BeatSensitivity { get => _s.BeatSensitivity; set => Set(_s.BeatSensitivity, value, v => _s.BeatSensitivity = v); }
    public bool IdleBreathing { get => _s.IdleBreathing; set => Set(_s.IdleBreathing, value, v => _s.IdleBreathing = v); }
    public bool SmoothMotion { get => _s.SmoothMotion; set => Set(_s.SmoothMotion, value, v => _s.SmoothMotion = v); }

    // ---------------------------------------------------------------- live audio meter

    public float BassLevel { get => _bassLevel; private set { _bassLevel = value; Raise(); } }
    public float BeatFlash { get => _beatFlash; private set { _beatFlash = value; Raise(); } }
    public string ListeningText
    {
        get => _listeningText;
        private set { if (_listeningText != value) { _listeningText = value; Raise(); if (_nowSubtitle is null) Raise(nameof(NowSubtitle)); } }
    }

    public void UpdateMeter(bool listening, float bass, float beatFlash, bool musicDetected, string? deviceName)
    {
        // Only notify on visible changes, so a quiet meter costs nothing to draw.
        bass = MathF.Round(bass * 50) / 50;
        beatFlash = beatFlash < 0.03f ? 0 : MathF.Round(beatFlash * 30) / 30;
        if (bass != _bassLevel) BassLevel = bass;
        if (beatFlash != _beatFlash) BeatFlash = beatFlash;
        if (_musicDetected != musicDetected) { _musicDetected = musicDetected; Raise(nameof(StatusText)); }
        ListeningText = !listening ? "Not listening (light off or Idle glow)"
            : deviceName is null ? "No sound output device found"
            : musicDetected ? $"Hearing music on {deviceName}"
            : $"Listening to {deviceName} · play something";
    }

    // ---------------------------------------------------------------- general

    public bool LaunchAtLogin { get => _s.LaunchAtLogin; set => Set(_s.LaunchAtLogin, value, v => _s.LaunchAtLogin = v); }
    public bool HideOnFullscreen { get => _s.HideOnFullscreenVideo; set => Set(_s.HideOnFullscreenVideo, value, v => _s.HideOnFullscreenVideo = v); }

    private string? _restReason;
    /// <summary>Why the light is temporarily hidden (e.g. a full-screen app), or null.</summary>
    public void SetRestReason(string? reason)
    {
        if (_restReason == reason) return;
        _restReason = reason;
        Raise(nameof(StatusText));
    }
    public string Hotkey => _s.Hotkey;
    public string Version => App.VersionString;
    public ICommand ResetLookCommand { get; }

    private void ResetLook()
    {
        var d = new AppSettings();
        _s.Thickness = d.Thickness; _s.Glow = d.Glow; _s.Brightness = d.Brightness;
        _s.CornerRadiusAuto = d.CornerRadiusAuto; _s.CornerRadius = d.CornerRadius;
        _s.PrimaryColor = d.PrimaryColor; _s.SecondaryColor = d.SecondaryColor;
        _s.Motion = d.Motion; _s.BeatSensitivity = d.BeatSensitivity; _s.AlbumColorCount = d.AlbumColorCount;
        _s.ChatEdgeColors = d.ChatEdgeColors; _s.ChatColor1 = d.ChatColor1; _s.ChatColor2 = d.ChatColor2; _s.ChatBubbles = d.ChatBubbles; _s.ChatMotion = d.ChatMotion; ChatColor1.LoadFromSettings(); ChatColor2.LoadFromSettings(); _s.LyricsStyle = d.LyricsStyle; _s.LyricsPlacement = d.LyricsPlacement; _s.LyricsSize = d.LyricsSize;
        Primary.LoadFromSettings(); Secondary.LoadFromSettings();
        Refresh();
        _changed();
    }

    /// <summary>Re-reads every property (e.g. after the hotkey toggles Active).</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    private bool Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(value);
        Raise(name!);
        _changed();
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
