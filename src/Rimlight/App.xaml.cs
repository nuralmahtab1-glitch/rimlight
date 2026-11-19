using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Win32;
using Rimlight.Audio;
using Rimlight.Core.Color;
using Rimlight.Core.Diagnostics;
using Rimlight.Core.Settings;
using Rimlight.Lyrics;
using Rimlight.NowPlaying;
using Rimlight.Overlay;
using Rimlight.SettingsUI;
using Rimlight.Shell;

namespace Rimlight;

/// <summary>
/// Entry point and lifecycle: settings, the overlay renderer, audio analysis, the tray icon and the flyout.
/// There is no main window — the app lives in the notification area.
/// </summary>
public partial class App : Application
{
    public static string VersionString { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rimlight");

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rimlight", "logs");

    public const string LyricsHotkey = "Ctrl+Alt+K";
    public const string SettingsHotkey = "Ctrl+Alt+O";

    public App()
    {
        // Velopack install/update/uninstall hooks must run before anything else.
        // A downloaded update is applied automatically on the next start.
        // Before an update or uninstall closes Rimlight, give the taskbar back.
        Velopack.VelopackApp.Build()
            .OnBeforeUpdateFastCallback(_ => TaskbarAutoHide.ForceRestore())
            .OnBeforeUninstallFastCallback(_ => TaskbarAutoHide.ForceRestore())
            .Run();
    }

    private readonly Updater _updater = new();
    private MenuItem? _updateItem;

    private SingleInstance? _instance;
    private SettingsStore _store = null!;
    private AppSettings _settings = null!;
    private OverlayRenderer _renderer = null!;
    private LoopbackAudio _audio = null!;
    private HotkeyManager? _hotkey;
    private TaskbarIcon _tray = null!;
    private SettingsFlyout _flyout = null!;
    private SettingsViewModel _vm = null!;
    private MenuItem _toggleItem = null!, _lyricsItem = null!, _lyricsTopItem = null!;
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool _listening;
    private bool? _appliedLaunchAtLogin;
    private long _meterBeatCount;
    private float _meterFlash;

    private MediaSessionWatcher _media = null!;
    private SystemWatcher _system = null!;
    private LyricsController? _lyrics;
    private Palette? _albumPalette;     // colors from the current track's artwork (null = none)
    private float _fadeSeconds = 0.8f;  // cross-fade length for the next color change
    private bool _lastOverride;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one copy: a second launch just opens the running copy's settings.
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        Log.Initialize(LogDirectory);
        Log.Info($"Rimlight {VersionString} starting on {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true; // keep running; never show users a stack trace
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Fatal unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _store = new SettingsStore(SettingsDirectory);
        _settings = _store.Load();
        if (_settings.Version < 2)
        {
            // v2: the light follows the song with many patterns by default.
            _settings.Motion = MotionStyle.Auto;
            _settings.Version = 2;
            _store.SaveSoon(_settings);
        }

        _audio = new LoopbackAudio { Sensitivity = _settings.BeatSensitivity / 100.0 };

        // Lock / screen off / sleep / Battery Saver / full-screen apps.
        _system = new SystemWatcher();
        _system.Changed += OnSystemStateChanged;
        _system.Resumed += () =>
        {
            Log.Info("Resumed: rebuilding overlays and restarting audio");
            _renderer.RebuildDisplays();
            if (_listening) { _audio.Stop(); _audio.Start(); }
        };

        _renderer = new OverlayRenderer(BuildOverlayConfig()) { AudioSource = () => _audio.Latest };
        _renderer.Failed += ex => Dispatcher.BeginInvoke(() =>
            _tray?.ShowNotification("Rimlight", "Couldn't start the graphics engine. See the log for details."));
        _renderer.DisplaysChanged += monitors => Dispatcher.BeginInvoke(() =>
            _vm.SetDisplays(monitors.Select((m, i) => new DisplayOption(m.DeviceId,
                monitors.Count(x => x.FriendlyName == m.FriendlyName) > 1 ? $"{m.FriendlyName} ({i + 1})" : m.FriendlyName))));

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _vm = new SettingsViewModel(_settings, OnSettingsChanged);
        _flyout = new SettingsFlyout(_vm);
        _flyout.QuitRequested += (_, _) => Quit();
        _flyout.VisibilityChangedTo += visible => { if (visible) _meterTimer.Start(); else _meterTimer.Stop(); UpdateMeter(); };
        _meterTimer.Tick += (_, _) => UpdateMeter();

        CreateTrayIcon();

        _hotkey = new HotkeyManager();
        var busy = new List<string>();
        if (!_hotkey.Register(_settings.Hotkey, () => _vm.Active = !_vm.Active)) busy.Add(_settings.Hotkey);
        if (!_hotkey.Register(LyricsHotkey, () => _vm.ShowLyrics = !_vm.ShowLyrics)) busy.Add(LyricsHotkey);
        // Same shortcut opens and closes the panel.
        if (!_hotkey.Register(SettingsHotkey, () => { if (_flyout.IsVisible) _flyout.HideFlyout(); else _flyout.ShowFlyout(); })) busy.Add(SettingsHotkey);
        if (busy.Count > 0)
            _tray.ShowNotification("Rimlight", $"{string.Join(", ", busy)} {(busy.Count == 1 ? "is" : "are")} used by another app, so {(busy.Count == 1 ? "it" : "they")} won't work for Rimlight.");

        ApplyRuntimeSettings();

        // Now playing + album colors.
        _lastOverride = _settings.OverrideAlbumColor;
        _media = new MediaSessionWatcher { PreferredPlayer = _settings.PreferredPlayer };
        _media.Changed += OnNowPlayingChanged;
        _media.PlayersChanged += players => Dispatcher.BeginInvoke(() => _vm.SetPlayers(players));

        // Synced lyrics (LRCLIB).
        _lyrics = new LyricsController(
            new LyricsService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rimlight", "lyrics")),
            _media, () => _audio.Latest);
        _lyrics.StatusChanged += s => Dispatcher.BeginInvoke(() => _vm.LyricsStatus = s);
        // Taskbar slides away while lyrics are on screen (if enabled), and comes back after.
        TaskbarAutoHide.RestoreAfterCrash();
        _lyrics.VisibleChanged += visible => TaskbarAutoHide.Set(visible && _settings.HideTaskbarWithLyrics);
        _lyrics.ApplySettings(_settings, IsSuspended);

        _flyout.PreviewLyricsRequested += (_, _) => _lyrics.StartDemo();

        _ = _media.StartAsync();
        PushColors();

        // Dev/test: --ambient <Effect> runs an ambient effect (not saved).
        int ambArg = Array.FindIndex(e.Args, a => a.Equals("--ambient", StringComparison.OrdinalIgnoreCase));
        if (ambArg >= 0 && ambArg + 1 < e.Args.Length && Enum.TryParse<Rimlight.Core.Motion.AmbientPreset>(e.Args[ambArg + 1], true, out var amb))
        {
            _settings.Animation = AnimationMode.Idle;
            _settings.Ambient = amb;
            _renderer.Update(BuildOverlayConfig());
        }

        // Dev/test: --lyrics-demo <Style> previews a lyrics style immediately.
        int demoArg = Array.FindIndex(e.Args, a => a.Equals("--lyrics-demo", StringComparison.OrdinalIgnoreCase));
        if (demoArg >= 0)
        {
            if (demoArg + 1 < e.Args.Length && Enum.TryParse<LyricsStyle>(e.Args[demoArg + 1], true, out var st))
            {
                _settings.LyricsStyle = st;
                _settings.ShowLyrics = true;
                _settings.LyricsOnTop = e.Args.Contains("--on-top", StringComparer.OrdinalIgnoreCase);
                _lyrics.ApplySettings(_settings, false);
            }
            _lyrics.StartDemo();
        }

        // Auto-update (installed copies only; at most once a day).
        _updater.UpdateReady += version => Dispatcher.BeginInvoke(() =>
        {
            if (_updateItem is not null) { _updateItem.Header = $"Restart to update (v{version})"; _updateItem.Visibility = Visibility.Visible; }
            _tray.ShowNotification("Rimlight update ready", $"Version {version} installs next time Rimlight starts, or right-click the tray icon → Restart to update.");
        });
        if (_settings.AutoCheckUpdates) _ = _updater.CheckAsync();

        _instance.ListenForShowRequests(() => Dispatcher.BeginInvoke(() => _flyout.ShowFlyout()));
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            _flyout.ShowFlyout();
    }

    private void CreateTrayIcon()
    {
        _toggleItem = new MenuItem { InputGestureText = _settings.Hotkey };
        _toggleItem.Click += (_, _) => _vm.Active = !_vm.Active;
        _lyricsItem = new MenuItem { Header = "Lyrics", InputGestureText = LyricsHotkey };
        _lyricsItem.Click += (_, _) => _vm.ShowLyrics = !_vm.ShowLyrics;
        _lyricsTopItem = new MenuItem { Header = "Lyrics over my apps" };
        _lyricsTopItem.Click += (_, _) => _vm.LyricsOnTop = !_vm.LyricsOnTop;
        var settingsItem = new MenuItem { Header = "Settings", InputGestureText = SettingsHotkey };
        settingsItem.Click += (_, _) => _flyout.ShowFlyout();
        var quitItem = new MenuItem { Header = "Quit Rimlight" };
        quitItem.Click += (_, _) => Quit();
        _updateItem = new MenuItem { Header = "Restart to update", Visibility = Visibility.Collapsed };
        _updateItem.Click += (_, _) => { TaskbarAutoHide.Set(false); _updater.RestartToUpdate(); };

        var menu = new ContextMenu();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_lyricsItem);
        menu.Items.Add(_lyricsTopItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(_updateItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);
        menu.Opened += (_, _) => UpdateToggleHeader();

        _tray = new TaskbarIcon
        {
            ToolTipText = "Rimlight",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/Rimlight.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => _flyout.Toggle();
        // Efficiency mode (EcoQoS) would throttle the render thread — keep it off.
        _tray.ForceCreate(enablesEfficiencyMode: false);
        UpdateToggleHeader();
    }

    private void UpdateToggleHeader()
    {
        _toggleItem.Header = _settings.Active ? "Turn light off" : "Turn light on";
        _lyricsItem.IsChecked = _settings.ShowLyrics;
        _lyricsTopItem.IsChecked = _settings.LyricsOnTop;
        _lyricsTopItem.IsEnabled = _settings.ShowLyrics && _settings.LyricsStyle is LyricsStyle.Album or LyricsStyle.FullScreen or LyricsStyle.Constellation;
        _tray.ToolTipText = _settings.Active ? "Rimlight — on" : "Rimlight — off";
    }

    /// <summary>Runs on a background thread: extract the palette off the UI thread, then apply.</summary>
    private void OnNowPlayingChanged(NowPlayingInfo? info)
    {
        Palette? palette = null;
        try { if (info?.ArtBgra64 is { } art) palette = PaletteExtractor.FromBgra(art); }
        catch (Exception ex) { Log.Error("Palette extraction failed", ex); }

        BitmapSource? thumb = null;
        if (info?.ArtBgra96 is { } px && px.Length == 96 * 96 * 4)
        {
            thumb = BitmapSource.Create(96, 96, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, px, 96 * 4);
            thumb.Freeze();
        }

        Dispatcher.BeginInvoke(() =>
        {
            _vm.SetNowPlaying(info?.Title, info?.Artist, info?.PlayerName, info?.IsPlaying ?? false, thumb);
            _lyrics?.OnTrack(info);
            if (palette != _albumPalette)
            {
                _albumPalette = palette;
                if (palette is { } p)
                    Log.Info($"Album colors: {string.Join(" / ", p.Colors.Select(c => OkLab.ToRgb(c).ToHex()))}");
                _fadeSeconds = 0.8f; // track change: smooth cross-fade
                PushColors();
            }
        });
    }

    /// <summary>Re-sends the config (new colors) and updates the panel's gradient preview.</summary>
    private void PushColors()
    {
        _renderer.Update(BuildOverlayConfig());
        var (colors, album) = EffectiveColors();
        static System.Windows.Media.Color ToWpf(Lab c)
        {
            var rgb = OkLab.ToRgb(c);
            return System.Windows.Media.Color.FromRgb((byte)Math.Round(rgb.R * 255), (byte)Math.Round(rgb.G * 255), (byte)Math.Round(rgb.B * 255));
        }
        var wpf = colors.Select(ToWpf).ToArray();
        _vm.SetEffectiveColors(wpf, album);
        _lyrics?.SetColors(wpf[0], wpf[1]);
    }

    private (Lab[] colors, bool fromAlbum) EffectiveColors()
    {
        if (!_settings.OverrideAlbumColor && _albumPalette is { } p)
            return ((_settings.AlbumColorCount is >= 2 and <= 4 ? Pad(p, _settings.AlbumColorCount) : p).Colors, true);
        var primary = Rgb.TryParseHex(_settings.PrimaryColor, out var a) ? a : new Rgb(1f, 0.18f, 0.53f);
        var secondary = Rgb.TryParseHex(_settings.SecondaryColor, out var b) ? b : new Rgb(0.48f, 0.24f, 1f);
        return ([OkLab.FromRgb(primary), OkLab.FromRgb(secondary)], false);

        // Exactly n colours: trim, or (if the cover had fewer) add hue-shifted companions.
        static Palette Pad(Palette p, int n)
        {
            if (p.Count >= n) return p.Take(n);
            var list = p.Colors.ToList();
            while (list.Count < n)
                list.Add(PaletteExtractor.BoostForLight(PaletteExtractor.RotateHue(list[list.Count % p.Count], 40 * list.Count, -0.04f)));
            return new Palette(list.ToArray());
        }
    }

    private void OnSettingsChanged()
    {
        // Switching between album and own colors fades slowly; dragging the color wheel follows quickly.
        _fadeSeconds = _settings.OverrideAlbumColor != _lastOverride ? 0.8f : 0.15f;
        _lastOverride = _settings.OverrideAlbumColor;
        _media.PreferredPlayer = _settings.PreferredPlayer;
        PushColors();
        _store.SaveSoon(_settings);
        UpdateToggleHeader();
        ApplyRuntimeSettings();
    }

    /// <summary>True while the light should be hidden without touching the user's on/off switch.</summary>
    private bool IsSuspended =>
        _system.ShouldRest || (_settings.HideOnFullscreenVideo && _system.IsFullscreenAppActive);

    private void OnSystemStateChanged()
    {
        _vm.SetRestReason(_settings.HideOnFullscreenVideo && _system.IsFullscreenAppActive ? "full-screen app" : null);
        _renderer.Update(BuildOverlayConfig());
        ApplyRuntimeSettings();
    }

    /// <summary>Settings that control services rather than the picture.</summary>
    private void ApplyRuntimeSettings()
    {
        _audio.Sensitivity = _settings.BeatSensitivity / 100.0;
        _vm.SetRestReason(_settings.HideOnFullscreenVideo && _system.IsFullscreenAppActive ? "full-screen app" : null);

        // Only listen to audio when it's actually used: light on, visible, Music Sync.
        bool wantAudio = _settings.Active && !IsSuspended && _settings.Animation == AnimationMode.MusicSync;
        if (wantAudio != _listening)
        {
            _listening = wantAudio;
            if (wantAudio) _audio.Start(); else _audio.Stop();
        }

        _lyrics?.ApplySettings(_settings, IsSuspended);
        if (!_settings.HideTaskbarWithLyrics || !_settings.ShowLyrics) TaskbarAutoHide.Set(false);

        if (_appliedLaunchAtLogin != _settings.LaunchAtLogin)
        {
            _appliedLaunchAtLogin = _settings.LaunchAtLogin;
            StartupManager.Apply(_settings.LaunchAtLogin);
        }
    }

    private void UpdateMeter()
    {
        var a = _audio.Latest;
        bool music = _listening && a.Active && MonoClock.Now - a.Time < 1.0;
        if (a.BeatCount != _meterBeatCount)
        {
            _meterBeatCount = a.BeatCount;
            if (music) _meterFlash = 1f;
        }
        else _meterFlash *= 0.82f;
        _vm.UpdateMeter(_listening, music ? a.Bass : 0f, _meterFlash, music, _audio.DeviceName);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Info("Display settings changed; rebuilding overlays");
        _renderer.RebuildDisplays();
    }

    private OverlayConfig BuildOverlayConfig()
    {
        var s = _settings;
        var (colors, _) = EffectiveColors();
        return new OverlayConfig
        {
            Active = s.Active,
            Display = s.Display,
            ThicknessDip = LightMetrics.ThicknessDip(s.Thickness),
            GlowDip = LightMetrics.GlowDip(s.Glow),
            RadiusDip = s.CornerRadiusAuto ? -1 : LightMetrics.CornerRadiusDip(s.CornerRadius),
            AvoidTaskbar = s.AvoidTaskbar,
            Brightness = (float)(s.Brightness / 100.0),
            Primary = colors[0],
            Secondary = colors[1],
            Colors = colors,
            Animation = s.Animation,
            Motion = s.Motion,
            IdleBreathing = s.IdleBreathing,
            Ambient = s.Ambient,
            ColorFadeSeconds = _fadeSeconds,
            Suspended = IsSuspended,
            LowPower = _system.IsEnergySaver,
        };
    }

    private void Quit()
    {
        Log.Info("Quitting");
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _meterTimer.Stop();
        _hotkey?.Dispose();
        _lyrics?.Dispose();
        TaskbarAutoHide.Set(false); // always give the taskbar back when quitting
        _media?.Dispose();
        _system?.Dispose();
        _tray.Dispose();
        _renderer.Dispose();
        _audio.Dispose();
        _store.Dispose();
        _instance?.Dispose();
        Shutdown();
    }
}
