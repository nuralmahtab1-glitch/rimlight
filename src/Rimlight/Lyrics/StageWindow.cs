using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rimlight.Core.Lyrics;

namespace Rimlight.Lyrics;

/// <summary>
/// Full-screen lyrics "stage" with three looks:
///  • Album — the album cover full-screen (drift + zoom, pumps on the beat), giant bold lyrics.
///  • Picture — your own picture as a living background (drift, sway, floating bokeh, light sweeps) with
///    karaoke lyrics placed in the picture's empty side, so they never cover the subject.
///  • Constellation — a deep, drifting night sky; each sung word lands as a star (ripple + sparkle burst),
///    joined by glowing lines; finished constellations recede into the sky as ghosts; shooting stars.
/// Runs behind your apps (hardware-rendered, cheap) or on top of everything (transparent, click-through).
/// </summary>
public sealed class StageWindow : Window, ILyricsDisplay
{
    public enum Look { Album, Constellation, Picture }

    void ILyricsDisplay.SetShown(bool shown, bool dim) => SetShown(shown, dim);

    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");

    private readonly Look _look;
    private readonly bool _onTop;
    private readonly double _w, _h;
    private readonly Grid _root = new() { ClipToBounds = true };
    private readonly Random _rng = new();
    private DispatcherTimer? _zTimer, _fxTimer;

    private Color _a = Color.FromRgb(0xFF, 0x2E, 0x88), _b = Color.FromRgb(0x7B, 0x3C, 0xFF);
    private LyricLine? _line;
    private LyricLine? _nextLine;
    private bool _gap;
    private int _wordsShown;
    private bool _shown, _dim;
    private readonly double _size;
    private readonly (double W, double H) _boxFrac;
    private int _lineSeed;

    /// <summary>Picture style: where the user placed the lyrics box (centre, fractions of the screen), or null = auto.</summary>
    public static Point? PictureBoxPlacement { get; set; }

    /// <summary>Background motion multiplier (0 = still, 1 = default, 1.5 = extra). Set before creating a stage.</summary>
    public static double MotionFactor { get; set; } = 1;
    private static double M => MotionFactor;

    public static double MotionFromLevel(double level) =>
        level <= 50 ? Math.Max(0, level) / 50.0 : 1 + (Math.Min(100, level) - 50) / 100.0;

    /// <summary>Size factor for a 0..100 lyrics Size level (shared with the settings preview).</summary>
    public static double SizeFactor(double sizeLevel) => 0.7 + 0.6 * Math.Clamp(sizeLevel / 100.0, 0, 1);

    /// <summary>Picture-style lyrics box size as fractions of the screen (width of screen width, height of screen height).</summary>
    public static (double W, double H) PictureBoxFraction(double sizeLevel)
    {
        double s = SizeFactor(sizeLevel);
        return (0.34 * s, 0.062 * s * 4.2);
    }

    public StageWindow(Look look, bool onTop, double sizeLevel, string? picturePath = null)
    {
        _look = look;
        _onTop = onTop;
        _size = SizeFactor(sizeLevel);
        _boxFrac = PictureBoxFraction(sizeLevel);

        // On top: fill the work area only, so the taskbar stays reachable. Behind apps: the whole screen.
        var wa = SystemParameters.WorkArea;
        _w = onTop ? wa.Width : SystemParameters.PrimaryScreenWidth;
        _h = onTop ? wa.Height : SystemParameters.PrimaryScreenHeight;

        Title = "Rimlight Stage";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Left = onTop ? wa.Left : 0; Top = onTop ? wa.Top : 0; Width = _w; Height = _h;

        if (onTop)
        {
            // Transparent so your apps show through; click-through.
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
        }
        else
        {
            // Behind apps: an ordinary hardware-rendered window = smooth animation at low CPU.
            Background = Brushes.Black;
        }
        Content = _root;
        _root.Opacity = 0;

        switch (look)
        {
            case Look.Album: BuildAlbum(); break;
            case Look.Picture: BuildPicture(picturePath); break;
            default: BuildConstellation(); break;
        }

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (_onTop) MakeClickThrough(hwnd);
            else
            {
                MakeNoActivate(hwnd);
                WallpaperHost.PlaceAboveDesktop(hwnd);
                _zTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _zTimer.Tick += (_, _) => { if (IsVisible) WallpaperHost.PlaceAboveDesktop(hwnd); };
                _zTimer.Start();
            }
        };
        Closed += (_, _) => { _zTimer?.Stop(); _fxTimer?.Stop(); };
    }

    private double WordSize => _look switch
    {
        Look.Album => _h * 0.095,
        Look.Picture => _h * 0.062,
        _ => _h * 0.024,
    } * _size;

    // ======================================================================================= ALBUM

    private readonly Image _art = new() { Stretch = Stretch.UniformToFill, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _artScale = new(1.1, 1.1);
    private readonly TranslateTransform _artMove = new();
    private readonly Image _artBlur = new() { Stretch = Stretch.UniformToFill };
    private readonly WrapPanel _current = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly WrapPanel _outgoing = new() { HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly TextBlock _next = new() { FontFamily = Display, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Opacity = 0.55, TextWrapping = TextWrapping.Wrap };
    private readonly Border _tagThumb = new() { Width = 56, Height = 56, CornerRadius = new CornerRadius(8) };
    private readonly TextBlock _tagTitle = new() { FontFamily = Display, FontWeight = FontWeights.Bold, FontSize = 18, Foreground = Brushes.White };
    private readonly TextBlock _tagArtist = new() { FontFamily = Display, FontSize = 14, Foreground = Brushes.White, Opacity = 0.75 };

    private void BuildAlbum()
    {
        _root.Children.Add(_artBlur);
        _art.RenderTransform = new TransformGroup { Children = { _artScale, _artMove } };
        RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
        _root.Children.Add(_art);
        _root.Children.Add(new Border
        {
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x20, 0, 0, 0), 0), new(Color.FromArgb(0x40, 0, 0, 0), 0.45), new(Color.FromArgb(0xC8, 0, 0, 0), 1),
            }, 90),
        });
        _root.Children.Add(new Border { Background = new RadialGradientBrush(Colors.Transparent, Color.FromArgb(0x90, 0, 0, 0)) { RadiusX = 0.75, RadiusY = 0.75 } });
        Drift(_artMove, _w * 0.025 * M, _h * 0.02 * M, 23, 31);

        double f = WordSize;
        var textGrid = new Grid { Children = { _current, _outgoing } };
        _current.MaxWidth = _outgoing.MaxWidth = _w * 0.5; // wrap before lines get long
        _next.FontSize = f * 0.32;
        _next.MaxWidth = _w * 0.7;
        _next.Margin = new Thickness(f * 0.08, f * 0.15, 0, 0);
        _root.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(_w * 0.06, 0, 0, _h * 0.12),
            Children = { textGrid, _next },
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.7 },
        });
        _root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(_w * 0.04, _h * 0.05, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _tagThumb, new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _tagTitle, _tagArtist } } },
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.6 },
        });
    }

    private void AddAlbumWord(int i)
    {
        var words = _line!.Words;
        double f = WordSize;
        bool chip = i == words.Length - 1 && words.Length > 1;
        double size = chip ? f * 1.1 : words[i].Length <= 3 ? f * 0.78 : f;
        // Mixed weights: little words light, others bold, the key word heaviest.
        var weight = chip ? FontWeights.Black : words[i].Trim(',', '.', '!', '?', '\'').Length <= 3 ? FontWeights.Light : FontWeights.Bold;
        var tb = new TextBlock { Text = words[i], FontFamily = Display, FontWeight = weight, FontSize = size, Foreground = Brushes.White };
        var b = new Border { Child = tb, Margin = new Thickness(f * 0.06, 0, f * 0.14, f * 0.02), CornerRadius = new CornerRadius(f * 0.14), RenderTransformOrigin = new Point(0.3, 0.7) };
        if (chip)
        {
            b.Background = new SolidColorBrush(_a);
            b.Padding = new Thickness(f * 0.18, 0, f * 0.18, f * 0.06);
            tb.Foreground = Luma(_a) > 0.55 ? new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)) : Brushes.White;
        }
        _current.Children.Add(b);
        FlyIn(b, 1.8, 22, f * 0.5, 320);
    }

    private void SendAlbumLineAway()
    {
        _outgoing.BeginAnimation(OpacityProperty, null);
        _outgoing.Children.Clear();
        var words = _current.Children.Cast<UIElement>().ToList();
        _current.Children.Clear();
        if (words.Count == 0) return;
        foreach (var w in words) _outgoing.Children.Add(w);
        var move = new TranslateTransform();
        _outgoing.RenderTransform = move;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320));
        fade.Completed += (_, _) => _outgoing.Children.Clear();
        _outgoing.BeginAnimation(OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -WordSize * 0.9, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    // ===================================================================================== PICTURE

    private readonly ScaleTransform _picScale = new(1.08, 1.08);
    private readonly RotateTransform _picRot = new();
    private readonly TranslateTransform _picMove = new();
    private readonly Border _picTint = new();
    private readonly StackPanel _karaoke = new();
    private readonly TranslateTransform _karaokeMove = new();
    private readonly TextBlock _kPrev = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _kCurrent = new();
    private readonly TextBlock _kNext = new() { TextWrapping = TextWrapping.Wrap };
    private Brush _kInk = Brushes.White;         // text colour chosen for readability on the picture
    private Color _kGlow = Colors.Black;
    private bool _textOnRight;

    private void BuildPicture(string? path)
    {
        BitmapSource? img = LoadPicture(path, (int)Math.Ceiling(SystemParameters.PrimaryScreenWidth * 1.25));
        var (luma, rightBusier) = img is null ? (0.3, true) : Analyse(img);
        _textOnRight = !rightBusier; // lyrics go on the calmer (emptier) side
        if (PictureBoxPlacement is { } placed) _textOnRight = placed.X > 0.5;
        bool bright = luma > 0.55;
        if (img is not null) PickNeonFromPicture(img);
        _kInk = bright ? new SolidColorBrush(Color.FromRgb(0x1C, 0x0E, 0x3A)) : Brushes.White;
        _kGlow = bright ? Colors.White : Colors.Black;

        // The picture: slow cinematic drift, gentle sway, zoom.
        var pic = new Image { Source = img, Stretch = Stretch.UniformToFill, RenderTransformOrigin = new Point(_textOnRight ? 0.35 : 0.65, 0.5) };
        RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);
        pic.RenderTransform = new TransformGroup { Children = { _picScale, _picRot, _picMove } };
        _root.Children.Add(pic);
        Drift(_picMove, _w * 0.018 * M, _h * 0.014 * M, 27, 35);
        _picRot.BeginAnimation(RotateTransform.AngleProperty, Loop(-0.6 * M, 0.6 * M, 19));

        // Colour tie-in with the music + a soft vignette.
        _root.Children.Add(_picTint);
        _root.Children.Add(new Border { Background = new RadialGradientBrush(Colors.Transparent, Color.FromArgb(0x55, 0, 0, 0)) { RadiusX = 0.85, RadiusY = 0.85 } });

        // Floating bokeh particles rising through the scene.
        var particles = new Canvas();
        for (int i = 0; i < 26; i++)
        {
            double s = 3 + _rng.NextDouble() * (i % 5 == 0 ? 26 : 9);
            var dot = new Ellipse
            {
                Width = s, Height = s,
                Fill = new RadialGradientBrush(Color.FromArgb((byte)(120 + _rng.Next(100)), 255, 255, 255), Color.FromArgb(0, 255, 255, 255)),
                Opacity = 0.35 + _rng.NextDouble() * 0.5,
            };
            Canvas.SetLeft(dot, _rng.NextDouble() * _w);
            var rise = new DoubleAnimation(_h + 40, -60, TimeSpan.FromSeconds(16 + _rng.NextDouble() * 26))
            { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(-_rng.NextDouble() * 30) };
            dot.BeginAnimation(Canvas.TopProperty, rise);
            var sway = new TranslateTransform();
            dot.RenderTransform = sway;
            sway.BeginAnimation(TranslateTransform.XProperty, Loop(-20 - _rng.Next(30), 20 + _rng.Next(30), 4 + _rng.NextDouble() * 5));
            particles.Children.Add(dot);
        }
        _root.Children.Add(particles);

        // Falling petals, tumbling and swaying.
        var petals = new Canvas();
        var petalShape = Geometry.Parse("M 0,6 C 2,0 10,0 12,6 C 10,12 2,12 0,6 Z");
        for (int i = 0; i < 18; i++)
        {
            double s = 9 + _rng.NextDouble() * 10;
            var petal = new System.Windows.Shapes.Path
            {
                Data = petalShape, Width = s, Height = s * 0.7, Stretch = Stretch.Fill,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(150 + _rng.Next(90)), 0xFF, (byte)(0xB0 + _rng.Next(40)), (byte)(0xD0 + _rng.Next(40)))),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            Canvas.SetLeft(petal, _rng.NextDouble() * _w);
            petal.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(-40, _h + 40, TimeSpan.FromSeconds(12 + _rng.NextDouble() * 14))
            { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(-_rng.NextDouble() * 25) });
            var spin = new RotateTransform();
            var sway = new TranslateTransform();
            petal.RenderTransform = new TransformGroup { Children = { spin, sway } };
            spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, _rng.Next(2) == 0 ? 360 : -360, TimeSpan.FromSeconds(4 + _rng.NextDouble() * 5)) { RepeatBehavior = RepeatBehavior.Forever });
            sway.BeginAnimation(TranslateTransform.XProperty, Loop(-40 - _rng.Next(40), 40 + _rng.Next(40), 3 + _rng.NextDouble() * 4));
            petals.Children.Add(petal);
        }
        _root.Children.Add(petals);

        // A light sweep glides across every so often.
        var sweep = new Rectangle
        {
            Width = _w * 0.35, Height = _h * 2.2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Fill = new LinearGradientBrush(new GradientStopCollection { new(Color.FromArgb(0, 255, 255, 255), 0), new(Color.FromArgb(0x26, 255, 255, 255), 0.5), new(Color.FromArgb(0, 255, 255, 255), 1) }, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var sweepMove = new TranslateTransform(-_w * 0.6, 0);
        sweep.RenderTransform = new TransformGroup { Children = { new RotateTransform(18), sweepMove } };
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(14) };
        var sweepAnim = new DoubleAnimation(-_w * 0.6, _w * 1.3, TimeSpan.FromSeconds(3.2)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(sweepAnim, sweep);
        Storyboard.SetTargetProperty(sweepAnim, new PropertyPath("RenderTransform.Children[1].X"));
        sb.Children.Add(sweepAnim);
        _root.Children.Add(sweep);
        sweep.Loaded += (_, _) => sb.Begin();

        _particles = particles;
        _inkColor = bright ? Color.FromRgb(0x1C, 0x0E, 0x3A) : Colors.White;
        double f = WordSize;
        var side = _textOnRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Thickness SideMargin(double top, double bottom) => _textOnRight
            ? new Thickness(0, top, _w * 0.06, bottom) : new Thickness(_w * 0.06, top, 0, bottom);

        // Beat colour flash: a soft glow of the song's colour behind the lyrics column.
        _flash.Width = _w * 0.6; _flash.Height = _h * 0.9;
        _flash.HorizontalAlignment = side;
        _flash.Opacity = 0;
        _root.Children.Add(_flash);

        // Giant faint "echo" of the line's star word behind the lyrics, magazine-style.
        _kEcho.FontFamily = Display; _kEcho.FontWeight = FontWeights.Black; _kEcho.FontSize = f * 3.3;
        _kEcho.Foreground = new SolidColorBrush(_inkColor);
        _kEcho.Opacity = 0; _kEcho.IsHitTestVisible = false;
        _kEcho.HorizontalAlignment = side; _kEcho.VerticalAlignment = VerticalAlignment.Center;
        _kEcho.Margin = SideMargin(0, f * 1.2);
        _kEcho.RenderTransformOrigin = new Point(0.5, 0.5);
        _kEcho.RenderTransform = _echoScale;
        _root.Children.Add(_kEcho);

        // Karaoke lyrics in the empty side, never wider than ~34% of the screen.
        _kPrev.FontFamily = _kNext.FontFamily = Display;
        _kPrev.FontWeight = _kNext.FontWeight = FontWeights.SemiBold;
        _kPrev.FontSize = f * 0.42; _kNext.FontSize = f * 0.48;
        _kPrev.Foreground = _kNext.Foreground = _kInk;
        _kPrev.Opacity = 0.35; _kNext.Opacity = 0.45;
        _kPrev.Margin = new Thickness(0, 0, 0, f * 0.35);
        _kNext.Margin = new Thickness(0, f * 0.35, 0, 0);
        _kPrev.RenderTransform = _prevMove;
        _kCurrent.RenderTransformOrigin = new Point(_textOnRight ? 1 : 0, 0.5);
        _kCurrent.RenderTransform = new TransformGroup { Children = { _curScale, _curTilt, _curMove } };
        _karaoke.Width = _w * _boxFrac.W; // scales with Size; matches the settings preview box
        _karaoke.HorizontalAlignment = side;
        _karaoke.VerticalAlignment = VerticalAlignment.Center;
        _karaoke.Margin = SideMargin(0, 0);
        _karaoke.Children.Add(_kPrev);
        _karaoke.Children.Add(_kCurrent);
        _karaoke.Children.Add(_kNext);
        _karaoke.RenderTransform = _karaokeMove;
        _karaoke.Effect = new DropShadowEffect { Color = _kGlow, BlurRadius = 22, ShadowDepth = 0, Opacity = bright ? 0.55 : 0.75 };
        _root.Children.Add(_karaoke);

        // Manual placement from the settings preview: centre the lyrics box (and its echo) on the chosen spot.
        if (PictureBoxPlacement is { } pos)
        {
            double boxW = _karaoke.Width, boxH = _h * _boxFrac.H;
            double left = Math.Clamp(pos.X * _w - boxW / 2, _w * 0.02, _w * 0.98 - boxW);
            double top = Math.Clamp(pos.Y * _h - boxH / 2, _h * 0.03, _h * 0.97 - boxH);
            foreach (var el in new FrameworkElement[] { _karaoke, _kEcho })
            {
                el.HorizontalAlignment = HorizontalAlignment.Left;
                el.VerticalAlignment = VerticalAlignment.Top;
                el.Margin = new Thickness(left, top, 0, 0);
            }
            _intro.HorizontalAlignment = HorizontalAlignment.Left;
            _intro.Margin = new Thickness(left, 0, 0, 0);
        }

        // Song intro card (big title + artist for a few seconds when a song starts).
        _introTitle.FontFamily = _introArtist.FontFamily = Display;
        _introTitle.FontWeight = FontWeights.Black; _introTitle.FontSize = f * 1.15;
        _introArtist.FontWeight = FontWeights.Light; _introArtist.FontSize = f * 0.5;
        _introTitle.Foreground = _introArtist.Foreground = _kInk;
        _introTitle.TextWrapping = TextWrapping.Wrap;
        _intro.Width = _w * _boxFrac.W; _intro.VerticalAlignment = VerticalAlignment.Center;
        if (PictureBoxPlacement is null) { _intro.HorizontalAlignment = side; _intro.Margin = SideMargin(0, 0); }
        _intro.Children.Add(_introArtist);
        _intro.Children.Add(_introTitle);
        _intro.Opacity = 0;
        _intro.Effect = new DropShadowEffect { Color = _kGlow, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.6 };
        _root.Children.Add(_intro);

        // Now-playing tag with a live progress bar, bottom of the empty side.
        var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _tagTitle.Foreground = _tagArtist.Foreground = _kInk;
        info.Children.Add(_tagTitle);
        info.Children.Add(_tagArtist);
        _progressFill.RenderTransform = _progressScale;
        var bar = new Grid { Height = 3, Margin = new Thickness(0, 10, 0, 0), Children = { new Border { Background = new SolidColorBrush(Color.FromArgb(0x40, _inkColor.R, _inkColor.G, _inkColor.B)), CornerRadius = new CornerRadius(2) }, _progressFill } };
        var tag = new StackPanel
        {
            Width = _w * 0.22, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = SideMargin(0, _h * 0.07),
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { _tagThumb, info } }, bar },
            Effect = new DropShadowEffect { Color = _kGlow, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.5 },
        };
        _root.Children.Add(tag);

        _fx.IsHitTestVisible = false;
        _root.Children.Add(_fx);
    }

    // Picture extras
    private Canvas? _particles;
    private Color _inkColor = Colors.White;
    private readonly Border _flash = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _kEcho = new();
    private readonly ScaleTransform _echoScale = new(1, 1);
    private readonly TranslateTransform _prevMove = new();
    private readonly ScaleTransform _curScale = new(1, 1);
    private readonly RotateTransform _curTilt = new();
    private readonly TranslateTransform _curMove = new();
    private readonly StackPanel _intro = new();
    private readonly TextBlock _introTitle = new(), _introArtist = new();
    private readonly Border _progressFill = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Stretch, RenderTransformOrigin = new Point(0, 0.5) };
    private readonly ScaleTransform _progressScale = new(0, 1);
    private readonly Canvas _fx = new();
    private int _keyIndex = -1;
    private bool _tiltLeft;
    private string? _introFor;

    /// <summary>The line's "star" word: the longest one (ties → later), ignoring punctuation.</summary>
    private static int KeyWord(string[] words)
    {
        int best = -1, len = 0;
        for (int i = 0; i < words.Length; i++)
        {
            int l = words[i].Trim(',', '.', '!', '?', '\'', '"', '(', ')').Length;
            if (l >= len && l >= 4) { best = i; len = l; }
        }
        return best;
    }

    private Color Accent => Blend(_neonA ?? _a, _inkColor == Colors.White ? Colors.White : Colors.Black, 0.12);

    // Neon colours taken from the picture itself (null = use the song's colours).
    private Color? _neonA, _neonB;

    /// <summary>
    /// Palette of the picture (OKLab k-means), then pick the two colours that belong to the picture but stand
    /// furthest from its overall/background colour — so the neon matches the art and still pops off it.
    /// </summary>
    private void PickNeonFromPicture(BitmapSource img)
    {
        try
        {
            var small = new FormatConvertedBitmap(new TransformedBitmap(img, new ScaleTransform(64.0 / img.PixelWidth, 64.0 / img.PixelHeight)), PixelFormats.Bgra32, null, 0);
            var px = new byte[small.PixelWidth * small.PixelHeight * 4];
            small.CopyPixels(px, small.PixelWidth * 4, 0);
            var palette = Rimlight.Core.Color.PaletteExtractor.FromBgra(px);
            if (palette is null) return;

            // Average colour of the picture ≈ what the neon must stand out against.
            double r = 0, g = 0, b = 0; int n = px.Length / 4;
            for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
            var avg = Rimlight.Core.Color.OkLab.FromRgb(new Rimlight.Core.Color.Rgb((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255)));

            var ranked = palette.Value.Colors
                .OrderByDescending(c => Rimlight.Core.Color.Lab.DistanceSq(c, avg) + 0.5 * c.Chroma * c.Chroma)
                .ToList();
            if (ranked.Count == 1) ranked.Add(Rimlight.Core.Color.PaletteExtractor.RotateHue(ranked[0], 30));
            Color ToWpf(Rimlight.Core.Color.Lab c)
            {
                var rgb = Rimlight.Core.Color.OkLab.ToRgb(Rimlight.Core.Color.PaletteExtractor.BoostForLight(c));
                return Color.FromRgb((byte)(rgb.R * 255), (byte)(rgb.G * 255), (byte)(rgb.B * 255));
            }
            _neonA = ToWpf(ranked[0]);
            _neonB = ToWpf(ranked[1]);
            Rimlight.Core.Diagnostics.Log.Info($"Picture neon colours: {_neonA} / {_neonB}");
        }
        catch (Exception ex) { Rimlight.Core.Diagnostics.Log.Warn($"Picture palette failed: {ex.Message}"); }
    }

    private void PictureNewLine(LyricLine? line, LyricLine? next, bool gap)
    {
        double f = WordSize;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // The old line rolls up into the faded line above (with a little blur).
        if (_kCurrent.Tag is string old && old.Length > 0)
        {
            _kPrev.Text = old;
            _prevMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(f * 0.9, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            _kPrev.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0.35, TimeSpan.FromMilliseconds(500)));
        }
        _kCurrent.Children.Clear();
        _kCurrent.Tag = line?.Text;
        _keyIndex = -1;

        if (line is not null && !gap)
        {
            _keyIndex = KeyWord(line.Words);
            for (int i = 0; i < line.Words.Length; i++)
            {
                var w = line.Words[i];
                bool key = i == _keyIndex;
                bool small = w.Trim(',', '.', '!', '?', '\'').Length <= 3;
                // Neon tube: a near-white core tinted with the colour; glows once "lit".
                var tube = key ? (_neonB ?? _b) : (_neonA ?? _a);
                var brush = new SolidColorBrush(Blend(tube, Colors.White, 0.7));
                var tb = new TextBlock
                {
                    Text = w, FontFamily = Display,
                    FontWeight = key ? FontWeights.Black : small ? FontWeights.SemiBold : FontWeights.Heavy,
                    FontStyle = key ? FontStyles.Italic : FontStyles.Normal,
                    FontSize = key ? f * 1.28 : small ? f * 0.82 : f,
                    Foreground = brush,
                    Margin = new Thickness(0, 0, f * 0.22, f * 0.02),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    RenderTransformOrigin = new Point(0.5, 0.75),
                    RenderTransform = new ScaleTransform(1, 1),
                    Opacity = 0.2, // unlit tube
                    Tag = tube,
                };
                _kCurrent.Children.Add(tb);
            }

            // Echo: the star word, huge and faint behind everything.
            if (_keyIndex >= 0)
            {
                _kEcho.Text = line.Words[_keyIndex].Trim(',', '.', '!', '?', '"').ToUpperInvariant();
                _kEcho.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.09, TimeSpan.FromMilliseconds(900)));
                var es = new DoubleAnimation(1.18, 1, TimeSpan.FromMilliseconds(1600)) { EasingFunction = ease };
                _echoScale.BeginAnimation(ScaleTransform.ScaleXProperty, es);
                _echoScale.BeginAnimation(ScaleTransform.ScaleYProperty, es);
            }
            else _kEcho.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
        }
        else
        {
            var note = new TextBlock { Text = "♪  ♪  ♪", FontFamily = Display, FontSize = f * 0.8, Foreground = _kInk };
            note.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 0.85, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            _kCurrent.Children.Add(note);
            _kEcho.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
        }
        _kNext.Text = next?.Text ?? "";
        _kNext.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.45, TimeSpan.FromMilliseconds(600)));

        // The new line rises in, slightly tilted (alternating each line), and un-blurs.
        _tiltLeft = !_tiltLeft;
        _curTilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_tiltLeft ? -3 : 3, _tiltLeft ? -1.2 : 1.2, TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });
        _curMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(f * 0.8, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = ease });
        _kCurrent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));
        var blur = new BlurEffect { Radius = 10 };
        _kCurrent.Effect = blur;
        var unblur = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(420));
        unblur.Completed += (_, _) => { if (ReferenceEquals(_kCurrent.Effect, blur)) _kCurrent.Effect = null; };
        blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
    }

    private void PictureLightWord(int i)
    {
        if (i >= _kCurrent.Children.Count || _kCurrent.Children[i] is not TextBlock tb) return;

        // Neon flicker-on: buzz, catch, buzz, then fully lit with a coloured glow.
        var tube = tb.Tag is Color c0 ? c0 : _a;
        tb.Effect = new DropShadowEffect { Color = tube, BlurRadius = i == _keyIndex ? 34 : 22, ShadowDepth = 0, Opacity = i == _keyIndex ? 1 : 0.85 };
        var flicker = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        foreach (var (t, v) in new[] { (0, 0.2), (60, 1.0), (110, 0.35), (170, 1.0), (230, 0.6), (300, 1.0) })
            flicker.KeyFrames.Add(new DiscreteDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
        flicker.FillBehavior = FillBehavior.HoldEnd;
        tb.BeginAnimation(OpacityProperty, flicker);
        if (tb.RenderTransform is ScaleTransform s)
        {
            var pop = new DoubleAnimation(i == _keyIndex ? 1.25 : 1.12, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        // Sparks fly off the word.
        tb.Dispatcher.BeginInvoke(() =>
        {
            if (!tb.IsLoaded) return;
            var c = tb.TranslatePoint(new Point(tb.ActualWidth / 2, tb.ActualHeight / 2), _fx);
            int n = i == _keyIndex ? 14 : 6;
            for (int k = 0; k < n; k++)
            {
                double ang = _rng.NextDouble() * Math.PI * 2, d = 25 + _rng.NextDouble() * (i == _keyIndex ? 90 : 45);
                double size = 2 + _rng.NextDouble() * 3;
                var spark = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(k % 2 == 0 ? Accent : _inkColor) };
                Canvas.SetLeft(spark, c.X); Canvas.SetTop(spark, c.Y);
                var mv = new TranslateTransform();
                spark.RenderTransform = mv;
                _fx.Children.Add(spark);
                var dur = TimeSpan.FromMilliseconds(500 + _rng.Next(400));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                mv.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Math.Cos(ang) * d, dur) { EasingFunction = ease });
                mv.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Math.Sin(ang) * d - 10, dur) { EasingFunction = ease });
                var fade = new DoubleAnimation(1, 0, dur);
                fade.Completed += (_, _) => _fx.Children.Remove(spark);
                spark.BeginAnimation(OpacityProperty, fade);
            }
        }, DispatcherPriority.Loaded);

        // The star word also makes the echo and the colour flash pulse.
        if (i == _keyIndex)
        {
            _kEcho.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 0.09, TimeSpan.FromMilliseconds(900)));
            _flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(900)));
        }
    }

    private void PictureIntro(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title) || title == _introFor) return;
        _introFor = title;
        _introTitle.Text = title;
        _introArtist.Text = artist ?? "";
        var show = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(4.2) };
        show.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.3))));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.2))));
        _intro.BeginAnimation(OpacityProperty, show);
        // Lyrics step aside while the card is up.
        var hide = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(4.6) };
        hide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.3))));
        hide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.6))));
        hide.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.6))));
        _karaoke.BeginAnimation(OpacityProperty, hide);
    }

    /// <summary>0..1 song progress for the now-playing bar.</summary>
    public void SetProgress(double fraction)
    {
        if (_look == Look.Picture) _progressScale.ScaleX = Math.Clamp(fraction, 0, 1);
    }

    private static BitmapSource? LoadPicture(string? path, int decodeWidth)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = decodeWidth; // keeps memory small even for huge images
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) { Rimlight.Core.Diagnostics.Log.Warn($"Couldn't load lyrics picture: {ex.Message}"); return null; }
    }

    /// <summary>Average brightness, and whether the right half has more detail (= where the subject is).</summary>
    private static (double luma, bool rightBusier) Analyse(BitmapSource img)
    {
        var small = new TransformedBitmap(img, new ScaleTransform(64.0 / img.PixelWidth, 36.0 / img.PixelHeight));
        var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        double sum = 0, edgeL = 0, edgeR = 0;
        double L(int x, int y) { int i = (y * w + x) * 4; return (0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i]) / 255.0; }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double l = L(x, y);
                sum += l;
                if (x + 1 < w && y + 1 < h)
                {
                    double e = Math.Abs(l - L(x + 1, y)) + Math.Abs(l - L(x, y + 1));
                    if (x < w / 2) edgeL += e; else edgeR += e;
                }
            }
        return (sum / (w * h), edgeR >= edgeL);
    }

    // =============================================================================== CONSTELLATION

    private Canvas _active = new();
    private readonly List<(Point P, TextBlock Label, FrameworkElement Marker)> _nodes = new();
    private readonly Queue<Canvas> _ghosts = new();
    private readonly Grid _sky = new();
    private Point _walk;

    private void BuildConstellation()
    {
        if (!_onTop)
            _root.Background = new RadialGradientBrush(Color.FromRgb(0x14, 0x08, 0x18), Color.FromRgb(0x02, 0x02, 0x05)) { RadiusX = 0.85, RadiusY = 0.85 };

        // Three layers of stars drifting at different speeds = depth.
        for (int layer = 0; layer < 3; layer++)
        {
            var c = new Canvas();
            int count = layer == 0 ? 160 : layer == 1 ? 70 : 25;
            for (int i = 0; i < count; i++)
            {
                double s = (layer + 1) * (0.6 + _rng.NextDouble() * 0.9);
                var star = new Ellipse { Width = s, Height = s, Fill = Brushes.White, Opacity = 0.12 + _rng.NextDouble() * (0.25 + layer * 0.2) };
                Canvas.SetLeft(star, _rng.NextDouble() * _w * 1.2 - _w * 0.1);
                Canvas.SetTop(star, _rng.NextDouble() * _h);
                if (i % 3 == 0)
                    star.BeginAnimation(OpacityProperty, new DoubleAnimation(0.05, 0.4 + layer * 0.25, TimeSpan.FromSeconds(1.5 + _rng.NextDouble() * 4))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(-_rng.NextDouble() * 5) });
                c.Children.Add(star);
            }
            var drift = new TranslateTransform();
            c.RenderTransform = drift;
            drift.BeginAnimation(TranslateTransform.XProperty, Loop(0, -_w * (0.02 + layer * 0.03), 70 - layer * 15));
            drift.BeginAnimation(TranslateTransform.YProperty, Loop(0, _h * (0.01 + layer * 0.01), 55 - layer * 10));
            _sky.Children.Add(c);
        }

        // Nebula clouds in the song's colours, drifting and breathing behind the stars.
        for (int i = 0; i < 3; i++)
        {
            double s = _w * (0.55 + _rng.NextDouble() * 0.35);
            var cloud = new Ellipse { Width = s, Height = s * 0.6, Opacity = 0.5 };
            Canvas.SetLeft(cloud, _rng.NextDouble() * _w * 0.7 - s * 0.2);
            Canvas.SetTop(cloud, _rng.NextDouble() * _h * 0.7 - s * 0.15);
            var mv = new TranslateTransform();
            cloud.RenderTransform = mv;
            mv.BeginAnimation(TranslateTransform.XProperty, Loop(-_w * 0.06, _w * 0.06, 30 + i * 9));
            mv.BeginAnimation(TranslateTransform.YProperty, Loop(-_h * 0.04, _h * 0.04, 23 + i * 7));
            cloud.BeginAnimation(OpacityProperty, Loop(0.3, 0.7, 9 + i * 4));
            _nebula.Children.Add(cloud);
        }
        _root.Children.Add(_nebula);
        ColorNebula();
        _root.Children.Add(_sky);
        NewActiveConstellation();

        // Shooting stars every 6–14 s.
        _fxTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _fxTimer.Tick += (_, _) => { ShootingStar(); _fxTimer!.Interval = TimeSpan.FromSeconds(6 + _rng.NextDouble() * 8); };
        _fxTimer.Start();
    }

    private readonly Canvas _nebula = new() { IsHitTestVisible = false };
    private FrameworkElement? _orbit;

    private void ColorNebula()
    {
        int i = 0;
        foreach (var cloud in _nebula.Children.OfType<Ellipse>())
        {
            var c = i++ % 2 == 0 ? _a : _b;
            cloud.Fill = new RadialGradientBrush(Color.FromArgb(_onTop ? (byte)0x22 : (byte)0x38, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
        }
    }

    private void NewActiveConstellation()
    {
        _active = new Canvas { RenderTransformOrigin = new Point(0.5, 0.5) };
        var rot = new RotateTransform(0, _w / 2, _h / 2);
        var move = new TranslateTransform();
        var scale = new ScaleTransform(1, 1, _w / 2, _h / 2);
        _active.RenderTransform = new TransformGroup { Children = { scale, rot, move } };
        // Slow "camera" sway so the constellation is never still.
        rot.BeginAnimation(RotateTransform.AngleProperty, Loop(-1.5, 1.5, 11));
        move.BeginAnimation(TranslateTransform.YProperty, Loop(-_h * 0.008, _h * 0.008, 7));
        _active.Effect = new DropShadowEffect { Color = _a, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.85 };
        _root.Children.Add(_active);
        _nodes.Clear();
        _walk = new Point(_w * (0.18 + _rng.NextDouble() * 0.06), _h * (0.35 + _rng.NextDouble() * 0.25));
    }

    /// <summary>Organic constellation shape: a random walk heading rightwards, wrapping to a new row.</summary>
    private Point NextStarPosition(int i)
    {
        if (i == 0) return _walk;
        double angle = (_rng.NextDouble() - 0.5) * Math.PI * 0.75; // ±67° around "east"
        double dist = _w * (0.07 + _rng.NextDouble() * 0.06);
        var p = new Point(_walk.X + Math.Cos(angle) * dist, _walk.Y + Math.Sin(angle) * dist);
        if (p.X > _w * 0.84) p = new Point(_w * (0.2 + _rng.NextDouble() * 0.1), p.Y + _h * 0.16); // wrap
        p.Y = Math.Clamp(p.Y, _h * 0.18, _h * 0.78);
        _walk = p;
        return p;
    }

    private void AddStar(int i)
    {
        var words = _line!.Words;
        var p = NextStarPosition(i);
        double f = WordSize;

        if (_nodes.Count > 0) AddStrand(_nodes[^1].P, p, 0.75);
        if (_nodes.Count >= 2 && _rng.NextDouble() < 0.35) AddStrand(_nodes[_rng.Next(_nodes.Count - 1)].P, p, 0.25);
        if (_nodes.Count > 0) Demote(_nodes.Count - 1);

        // Ripple ring + sparkle burst where the word lands.
        var ring = new Ellipse { Width = 16, Height = 16, Stroke = new SolidColorBrush(_a), StrokeThickness = 2, RenderTransformOrigin = new Point(0.5, 0.5) };
        Canvas.SetLeft(ring, p.X - 8); Canvas.SetTop(ring, p.Y - 8);
        var rs = new ScaleTransform(1, 1);
        ring.RenderTransform = rs;
        _active.Children.Add(ring);
        var grow = new DoubleAnimation(1, 7, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        rs.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        rs.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var ringFade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(900));
        ringFade.Completed += (_, _) => _active.Children.Remove(ring);
        ring.BeginAnimation(OpacityProperty, ringFade);
        for (int k = 0; k < 7; k++)
        {
            double ang = k / 7.0 * Math.PI * 2 + _rng.NextDouble() * 0.5;
            var spark = new Ellipse { Width = 3, Height = 3, Fill = new SolidColorBrush(Blend(_a, Colors.White, 0.5)) };
            Canvas.SetLeft(spark, p.X - 1.5); Canvas.SetTop(spark, p.Y - 1.5);
            var sm = new TranslateTransform();
            spark.RenderTransform = sm;
            _active.Children.Add(spark);
            double d = 30 + _rng.NextDouble() * 40;
            var dur = TimeSpan.FromMilliseconds(600 + _rng.Next(300));
            sm.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Math.Cos(ang) * d, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            sm.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Math.Sin(ang) * d, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var sf = new DoubleAnimation(1, 0, dur);
            sf.Completed += (_, _) => _active.Children.Remove(spark);
            spark.BeginAnimation(OpacityProperty, sf);
        }

        // The star (glowing diamond) and the word in a colour gradient.
        var marker = new Rectangle { Width = 18, Height = 18, Fill = new SolidColorBrush(Blend(_a, Colors.White, 0.2)), RenderTransformOrigin = new Point(0.5, 0.5) };
        var ms = new ScaleTransform(0, 0);
        marker.RenderTransform = new TransformGroup { Children = { ms, new RotateTransform(45) } };
        Canvas.SetLeft(marker, p.X - 9); Canvas.SetTop(marker, p.Y - 9);
        _active.Children.Add(marker);
        var pop = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 } };
        ms.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ms.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        // Orbit ring circling the word being sung.
        if (_orbit is not null) _active.Children.Remove(_orbit);
        var orbit = new Ellipse
        {
            Width = 52, Height = 52, Stroke = new SolidColorBrush(Blend(_b, Colors.White, 0.3)), StrokeThickness = 1.4,
            StrokeDashArray = new DoubleCollection { 2, 4 }, Opacity = 0.8, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var orot = new RotateTransform();
        orbit.RenderTransform = orot;
        Canvas.SetLeft(orbit, p.X - 26); Canvas.SetTop(orbit, p.Y - 26);
        orot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(4)) { RepeatBehavior = RepeatBehavior.Forever });
        _active.Children.Add(orbit);
        _orbit = orbit;

        var label = new TextBlock
        {
            Text = words[i], FontFamily = Display, FontWeight = FontWeights.Bold, FontSize = f * 2.8,
            Foreground = new LinearGradientBrush(Blend(_a, Colors.White, 0.15), _b, 0),
        };
        Canvas.SetLeft(label, p.X + 16); Canvas.SetTop(label, p.Y - f * 1.7);
        _active.Children.Add(label);
        FlyIn(label, 1.5, 14, f * 0.7, 300);
        _nodes.Add((p, label, marker));
    }

    private void Demote(int idx)
    {
        var (p, tb, marker) = _nodes[idx];
        double f = WordSize;
        tb.BeginAnimation(TextBlock.FontSizeProperty, new DoubleAnimation(tb.FontSize, f, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tb.Foreground = new SolidColorBrush(Blend(_a, Colors.White, 0.45)) { Opacity = 0.8 };
        Canvas.SetTop(tb, p.Y - f * 1.5);
        Canvas.SetLeft(tb, p.X + 9);
        // Big diamond → small twinkling sparkle.
        var star = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 5,0 L 6.2,3.8 10,5 6.2,6.2 5,10 3.8,6.2 0,5 3.8,3.8 Z"),
            Width = 11, Height = 11, Stretch = Stretch.Fill, Fill = new SolidColorBrush(Blend(_a, Colors.White, 0.35)),
        };
        Canvas.SetLeft(star, p.X - 5.5); Canvas.SetTop(star, p.Y - 5.5);
        int at = _active.Children.IndexOf(marker);
        if (at >= 0) { _active.Children.RemoveAt(at); _active.Children.Insert(at, star); } else _active.Children.Add(star);
        star.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 1, TimeSpan.FromSeconds(0.8 + _rng.NextDouble())) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        _nodes[idx] = (p, tb, star);
    }

    private void AddStrand(Point from, Point to, double opacity)
    {
        var line = new Line
        {
            X1 = from.X, Y1 = from.Y, X2 = from.X, Y2 = from.Y, StrokeThickness = 1.6, Opacity = opacity,
            Stroke = new LinearGradientBrush(_a, _b, new Point(0, 0), new Point(1, 0)),
        };
        _active.Children.Insert(0, line);
        var dur = TimeSpan.FromMilliseconds(320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        line.BeginAnimation(Line.X2Property, new DoubleAnimation(from.X, to.X, dur) { EasingFunction = ease });
        line.BeginAnimation(Line.Y2Property, new DoubleAnimation(from.Y, to.Y, dur) { EasingFunction = ease });
        if (opacity < 0.5) return;

        // A comet of light races along the new line, then fades.
        var comet = new Ellipse
        {
            Width = 8, Height = 8, Fill = Brushes.White,
            Effect = new DropShadowEffect { Color = _a, BlurRadius = 18, ShadowDepth = 0, Opacity = 1 },
        };
        _active.Children.Add(comet);
        var travel = TimeSpan.FromMilliseconds(380);
        comet.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(from.X - 4, to.X - 4, travel) { EasingFunction = ease });
        comet.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(from.Y - 4, to.Y - 4, travel) { EasingFunction = ease });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250)) { BeginTime = travel };
        fade.Completed += (_, _) => _active.Children.Remove(comet);
        comet.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The finished constellation recedes into the sky as a faint ghost; a new one starts.</summary>
    private void ResetConstellation()
    {
        if (_nodes.Count > 0)
        {
            var old = _active;
            // Line complete: the whole constellation flashes once before drifting away.
            if (_orbit is not null) { old.Children.Remove(_orbit); _orbit = null; }
            foreach (var l in old.Children.OfType<Line>())
                l.BeginAnimation(Line.StrokeThicknessProperty, new DoubleAnimation(4.5, 1.6, TimeSpan.FromMilliseconds(700)));
            old.Effect = null;
            if (old.RenderTransform is TransformGroup g && g.Children[0] is ScaleTransform sc && g.Children[2] is TranslateTransform tr)
            {
                var dur = TimeSpan.FromMilliseconds(1300);
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.5, dur) { EasingFunction = ease });
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.5, dur) { EasingFunction = ease });
                tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, (_rng.NextDouble() - 0.5) * _w * 0.5, dur) { EasingFunction = ease });
                tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, (_rng.NextDouble() - 0.5) * _h * 0.4, dur) { EasingFunction = ease });
            }
            old.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.18, TimeSpan.FromMilliseconds(1300)));
            _ghosts.Enqueue(old);
            while (_ghosts.Count > 3)
            {
                var gone = _ghosts.Dequeue();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(800));
                fade.Completed += (_, _) => _root.Children.Remove(gone);
                gone.BeginAnimation(OpacityProperty, fade);
            }
        }
        else
        {
            _root.Children.Remove(_active);
        }
        NewActiveConstellation();
    }

    private void ShootingStar()
    {
        if (!_shown || _look != Look.Constellation) return;
        double x0 = _rng.NextDouble() * _w * 0.7, y0 = _rng.NextDouble() * _h * 0.3;
        var streak = new Line
        {
            X1 = 0, Y1 = 0, X2 = -120, Y2 = -45, StrokeThickness = 2,
            Stroke = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
        };
        var mv = new TranslateTransform(x0, y0);
        streak.RenderTransform = mv;
        var c = new Canvas { Children = { streak } };
        _root.Children.Insert(_root.Children.IndexOf(_sky) + 1, c);
        var dur = TimeSpan.FromMilliseconds(900);
        mv.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x0, x0 + _w * 0.35, dur));
        mv.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y0, y0 + _w * 0.13, dur));
        var fade = new DoubleAnimation(1, 0, dur) { BeginTime = TimeSpan.FromMilliseconds(250) };
        fade.Completed += (_, _) => _root.Children.Remove(c);
        streak.BeginAnimation(OpacityProperty, fade);
    }

    // ===================================================================================== INPUTS

    public void SetColors(Color a, Color b)
    {
        _a = a; _b = b;
        if (_look == Look.Constellation)
        {
            if (!_onTop) _root.Background = new RadialGradientBrush(Blend(a, Colors.Black, 0.88), Color.FromRgb(0x02, 0x02, 0x05)) { RadiusX = 0.85, RadiusY = 0.85 };
            if (_active.Effect is DropShadowEffect fx) fx.Color = a;
            ColorNebula();
        }
        if (_look == Look.Picture)
        {
            _picTint.Background = new LinearGradientBrush(Color.FromArgb(0, a.R, a.G, a.B), Color.FromArgb(0x30, a.R, a.G, a.B), 90);
            var fc = _neonA ?? a;
            _flash.Background = new RadialGradientBrush(Color.FromArgb(0x70, fc.R, fc.G, fc.B), Color.FromArgb(0, fc.R, fc.G, fc.B));
            _progressFill.Background = new LinearGradientBrush(a, b, 0);
        }
    }

    public void SetArt(ImageSource? sharp, ImageSource? blur)
    {
        if (_look == Look.Picture)
        {
            _tagThumb.Width = _tagThumb.Height = 46;
            _tagThumb.Background = sharp is not null ? new ImageBrush(sharp) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(_a);
            return;
        }
        if (_look != Look.Album) return;
        _art.Source = sharp;
        _artBlur.Source = blur;
        _tagThumb.Background = sharp is not null ? new ImageBrush(sharp) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(_a);
    }

    public void SetTrack(string? title, string? artist)
    {
        _tagTitle.Text = title ?? "";
        _tagArtist.Text = artist ?? "";
        if (_look == Look.Picture && _shown) PictureIntro(title, artist);
        else if (_look == Look.Picture) _pendingIntro = (title, artist);
    }

    private (string? Title, string? Artist)? _pendingIntro;

    /// <summary>Fade in/out (only animates when the state actually changes). Paused playback dims a little.</summary>
    public void SetShown(bool shown, bool dim = false)
    {
        if (shown == _shown && dim == _dim) return;
        _shown = shown; _dim = dim;
        if (shown && !IsVisible) Show();
        if (shown && _pendingIntro is { } pi) { _pendingIntro = null; PictureIntro(pi.Title, pi.Artist); }
        // Smooth, eased fades: a soft 0.8 s fade-out, a gentler 1.2 s fade back in.
        var anim = new DoubleAnimation(shown ? (dim ? 0.75 : 1) : 0, TimeSpan.FromMilliseconds(shown ? 1200 : 800))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        // Once faded out, hide the window itself — otherwise its background would stay on screen.
        if (!shown) anim.Completed += (_, _) => { if (!_shown) Hide(); };
        _root.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>0..1 beat flash: backgrounds pump, the current star pulses.</summary>
    public void SetBeat(float flash)
    {
        switch (_look)
        {
            case Look.Album: _artScale.ScaleX = _artScale.ScaleY = 1.1 + 0.025 * M * flash; break;
            case Look.Picture:
                _picScale.ScaleX = _picScale.ScaleY = 1.08 + 0.045 * M * flash * flash; // zoom punch on strong beats
                _curScale.ScaleX = _curScale.ScaleY = 1 + 0.03 * M * flash;   // lyrics bump on the beat
                if (_keyIndex >= 0 && _keyIndex < _kCurrent.Children.Count && _kCurrent.Children[_keyIndex] is TextBlock { Effect: DropShadowEffect g })
                    g.BlurRadius = 26 + 22 * flash;                           // star word's neon buzzes
                if (_particles is not null) _particles.Opacity = 0.65 + 0.35 * flash;
                break;
            default:
                if (_nodes.Count > 0) _nodes[^1].Marker.Opacity = 0.65 + 0.35 * flash;
                _nebula.Opacity = 0.75 + 0.25 * flash;   // nebula breathes with the beat
                if (_orbit is not null) _orbit.Opacity = 0.5 + 0.5 * flash;
                break;
        }
    }

    public void ShowLine(LyricLine? line, int wordsVisible, LyricLine? next, bool gap)
    {
        if (!ReferenceEquals(line, _line) || gap != _gap)
        {
            _line = line; _gap = gap; _wordsShown = 0; _nextLine = next;
            _lineSeed = line is null ? 0 : (line.Text.GetHashCode() & 0x7FFFFFFF) ^ (int)(line.Time * 10);
            switch (_look)
            {
                case Look.Album:
                    SendAlbumLineAway();
                    _next.Text = next?.Text ?? "";
                    if (gap || line is null || line.Words.Length == 0) ShowGapNote();
                    break;
                case Look.Picture:
                    PictureNewLine(line, next, gap || line is null || line.Words.Length == 0);
                    break;
                default:
                    ResetConstellation();
                    if (gap || line is null || line.Words.Length == 0) ShowGapNote();
                    break;
            }
        }
        if (_line is null || _gap) return;
        while (_wordsShown < Math.Min(wordsVisible, _line.Words.Length))
        {
            switch (_look)
            {
                case Look.Album: AddAlbumWord(_wordsShown); break;
                case Look.Picture: PictureLightWord(_wordsShown); break;
                default: AddStar(_wordsShown); break;
            }
            _wordsShown++;
        }
    }

    private void ShowGapNote()
    {
        var note = new TextBlock { Text = "♪", FontFamily = Display, FontSize = _look == Look.Album ? WordSize : WordSize * 3, Foreground = new SolidColorBrush(_a) };
        note.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 0.9, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        if (_look == Look.Album) _current.Children.Add(note);
        else { Canvas.SetLeft(note, _w / 2 - WordSize); Canvas.SetTop(note, _h * 0.42); _active.Children.Add(note); }
    }

    // ===================================================================================== HELPERS

    private static DoubleAnimation Loop(double from, double to, double seconds) =>
        new(from, to, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };

    private static void Drift(TranslateTransform t, double dx, double dy, double sx, double sy)
    {
        t.BeginAnimation(TranslateTransform.XProperty, Loop(-dx, dx, sx));
        t.BeginAnimation(TranslateTransform.YProperty, Loop(-dy, dy, sy));
    }

    private static void FlyIn(FrameworkElement el, double scaleFrom, double blur, double rise, int ms)
    {
        var dur = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { scale, move } };
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        var s = new DoubleAnimation(scaleFrom, 1, dur) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, dur) { EasingFunction = ease });
        if (blur > 0 && el.Effect is null)
        {
            var be = new BlurEffect { Radius = blur };
            el.Effect = be;
            var ba = new DoubleAnimation(blur, 0, dur) { EasingFunction = ease };
            ba.Completed += (_, _) => { if (ReferenceEquals(el.Effect, be)) el.Effect = null; };
            be.BeginAnimation(BlurEffect.RadiusProperty, ba);
        }
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private static void MakeClickThrough(IntPtr hwnd) =>
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80 | 0x80000 | 0x08000000);

    private static void MakeNoActivate(IntPtr hwnd) =>
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80 | 0x08000000); // tool window, never takes focus
}
