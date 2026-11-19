using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Rimlight.Core.Lyrics;
using Rimlight.Core.Settings;

namespace Rimlight.Lyrics;

/// <summary>
/// The on-screen lyrics: a click-through, topmost, never-focused window that can take five looks
/// (see <see cref="LyricsStyle"/>). Words of the current line fly in one by one (blur + zoom + rise),
/// the last word gets a coloured highlight chip, lines slide away when they change, and the whole
/// thing pulses gently on the beat. All colours follow the album palette.
/// </summary>
/// <summary>Something that can show synced lyrics.</summary>
public interface ILyricsDisplay
{
    void ShowLine(LyricLine? line, int wordsVisible, LyricLine? next, bool gap);
    void SetShown(bool shown, bool dim);
    void SetBeat(float flash);
}

public sealed class LyricsWindow : Window, ILyricsDisplay
{
    private sealed record Spec(
        double Font, FontWeight Weight, bool Chips, Brush? CardBackground, bool ArtCard, bool Thumb,
        bool Glow, bool FullScreen, bool TextShadow, bool ShowNext, double Corner,
        double FlyScale, double FlyBlur, double FlyRise, int FlyMs);

    private static Spec SpecFor(LyricsStyle s) => s switch
    {
        LyricsStyle.Album => new(28, FontWeights.SemiBold, true, null, true, true, true, false, false, true, 22, 1.25, 10, 12, 220),
        LyricsStyle.Bold => new(84, FontWeights.Bold, true, null, false, false, false, false, true, false, 0, 1.7, 18, 34, 300),
        LyricsStyle.FullScreen => new(92, FontWeights.Bold, true, null, false, false, false, true, true, true, 0, 1.7, 20, 40, 320),
        LyricsStyle.Subtitle => new(24, FontWeights.SemiBold, false, Frozen(Color.FromArgb(0xB4, 0, 0, 0)), false, false, false, false, false, false, 8, 1.0, 0, 6, 150),
        _ => new(30, FontWeights.SemiBold, true, Frozen(Color.FromArgb(0xC8, 0x10, 0x10, 0x14)), false, false, true, false, false, true, 20, 1.25, 10, 12, 220),
    };

    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    private readonly Grid _root = new() { Opacity = 0 };
    private readonly Border _card = new() { RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly Border _thumb = new() { Width = 76, Height = 76, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _textStack = new() { RenderTransformOrigin = new Point(0.5, 0.5), VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _current = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly WrapPanel _outgoing = new() { HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBlock _next = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Opacity = 0.42, Foreground = Brushes.WhiteSmoke, FontFamily = DisplayFont };
    private readonly ScaleTransform _cardScale = new(1, 1);
    private readonly ScaleTransform _textScale = new(1, 1);
    private readonly DropShadowEffect _glow = new() { BlurRadius = 44, ShadowDepth = 0, Opacity = 0.45 };

    private Spec _spec = SpecFor(LyricsStyle.Card);
    private LyricsStyle _style = LyricsStyle.Card;
    private LyricsPlacement _placement = LyricsPlacement.Bottom;
    private double _size = 1.0;
    private Color _a = Color.FromRgb(0xFF, 0x2E, 0x88), _b = Color.FromRgb(0x7B, 0x3C, 0xFF);
    private ImageSource? _art, _artBlurCard, _artBlurScreen;

    private LyricLine? _line;
    private bool _lineIsGap;
    private int _wordsShown;
    private bool _shown, _dim;

    public LyricsWindow()
    {
        Title = "Rimlight Lyrics";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        Left = -10000; Top = -10000;

        var textGrid = new Grid();
        textGrid.Children.Add(_current);
        textGrid.Children.Add(_outgoing);
        _textStack.Children.Add(textGrid);
        _textStack.Children.Add(_next);
        _textStack.RenderTransform = _textScale;

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(_thumb);
        Grid.SetColumn(_textStack, 1);
        content.Children.Add(_textStack);

        _card.Child = content;
        _card.RenderTransform = _cardScale;
        _root.Children.Add(_card);
        Content = _root;

        SizeChanged += (_, _) => Reposition();
        SourceInitialized += (_, _) => MakeClickThrough();
        ApplyStyle(LyricsStyle.Card, LyricsPlacement.Bottom, 50);
    }

    // ------------------------------------------------------------------ configuration

    public void ApplyStyle(LyricsStyle style, LyricsPlacement placement, double sizeLevel)
    {
        _style = style;
        _placement = placement;
        _size = 0.6 + 0.8 * Math.Clamp(sizeLevel / 100.0, 0, 1);
        _spec = SpecFor(style);
        var wa = SystemParameters.WorkArea;

        if (_spec.FullScreen)
        {
            SizeToContent = SizeToContent.Manual;
            _root.Margin = new Thickness(0);
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            // One pixel short of the screen: a topmost window covering the whole monitor makes Windows
            // think a full-screen app is running and silence notifications.
            Height = SystemParameters.PrimaryScreenHeight - 1;
            _card.HorizontalAlignment = HorizontalAlignment.Center;
            _card.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            _root.Margin = new Thickness(44); // room for the glow
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = double.NaN; Height = double.NaN;
            _card.HorizontalAlignment = HorizontalAlignment.Stretch;
            _card.VerticalAlignment = VerticalAlignment.Stretch;
        }

        // Bold: wrap early so a line drops down before it gets long.
        double maxText = _spec.FullScreen ? SystemParameters.PrimaryScreenWidth * 0.8
            : style == LyricsStyle.Bold ? Math.Min(wa.Width * 0.5, 1000)
            : Math.Min(wa.Width * 0.6, 1000);
        _current.MaxWidth = _outgoing.MaxWidth = _next.MaxWidth = maxText;

        _card.CornerRadius = new CornerRadius(_spec.Corner);
        _card.Padding = _spec.CardBackground is null && !_spec.ArtCard ? new Thickness(8) : new Thickness(28, 16, 28, 18);
        _card.MinWidth = _spec.CardBackground is null && !_spec.ArtCard ? 0 : 220;
        _thumb.Visibility = _spec.Thumb ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = _spec.ShowNext ? Visibility.Visible : Visibility.Collapsed;
        _next.FontSize = Math.Max(14, _spec.Font * _size * (_spec.FullScreen ? 0.34 : 0.6));
        _next.FontWeight = FontWeights.SemiBold;
        _next.Margin = new Thickness(0, _spec.Font * _size * 0.2, 0, 0);
        _textStack.Effect = _spec.TextShadow
            ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.85 }
            : null;
        _card.Effect = _spec.Glow ? _glow : null;

        ApplyColors();
        RebuildCurrentLine();
    }

    public void SetColors(Color a, Color b)
    {
        _a = a; _b = b;
        ApplyColors();
    }

    /// <summary>Sharp cover (for the Album thumbnail) and pre-blurred, darkened covers (card / full-screen backgrounds).</summary>
    public void SetArt(ImageSource? sharp, ImageSource? blurCard, ImageSource? blurScreen)
    {
        _art = sharp; _artBlurCard = blurCard; _artBlurScreen = blurScreen;
        ApplyColors();
    }

    private void ApplyColors()
    {
        _glow.Color = _a;
        _card.BorderThickness = _spec.Glow ? new Thickness(1.2) : new Thickness(0);
        _card.BorderBrush = new LinearGradientBrush(_a, _b, 45);

        _card.Background = _spec.ArtCard
            ? (_artBlurCard is not null ? HighQualityBrush(_artBlurCard) : Frozen(Color.FromArgb(0xD0, 0x14, 0x14, 0x18)))
            : _spec.CardBackground ?? Brushes.Transparent;
        _thumb.Background = _art is not null ? HighQualityBrush(_art) : new SolidColorBrush(_a);
        _root.Background = _spec.FullScreen
            ? (_artBlurScreen is not null ? HighQualityBrush(_artBlurScreen) : Frozen(Color.FromArgb(0xF0, 0x08, 0x08, 0x0A)))
            : Brushes.Transparent;

        // Re-tint any highlight chips already on screen.
        foreach (var child in _current.Children.OfType<Border>())
            if (child.Tag is "chip") StyleChip(child);
    }

    private static Brush HighQualityBrush(ImageSource img)
    {
        var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.HighQuality);
        b.Freeze();
        return b;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // ------------------------------------------------------------------ per-frame updates

    /// <summary>Show/hide with a fade; <paramref name="dim"/> fades to half while playback is paused.</summary>
    public void SetShown(bool shown, bool dim)
    {
        if (shown == _shown && dim == _dim) return;
        bool wasShown = _shown;
        _shown = shown; _dim = dim;
        if (shown && !IsVisible) { Show(); Reposition(); }
        var anim = new DoubleAnimation(shown ? (dim ? 0.5 : 1.0) : 0.0, TimeSpan.FromMilliseconds(shown && !wasShown ? 350 : 300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (!shown) anim.Completed += (_, _) => { if (!_shown) Hide(); };
        _root.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Updates the visible line. Call often; it only animates what changed.</summary>
    public void ShowLine(LyricLine? line, int wordsVisible, LyricLine? next, bool gap)
    {
        if (!ReferenceEquals(line, _line) || gap != _lineIsGap)
        {
            SendCurrentAway();
            _line = line;
            _lineIsGap = gap;
            _wordsShown = 0;
            _next.Text = next?.Text ?? "";
            if (gap || line is null || line.Words.Length == 0) ShowGapNote();
        }
        if (_line is null || _lineIsGap) return;
        while (_wordsShown < Math.Min(wordsVisible, _line.Words.Length))
        {
            bool last = _wordsShown == _line.Words.Length - 1;
            _current.Children.Add(MakeWord(_line.Words[_wordsShown], last && _spec.Chips && _line.Words.Length > 1, animate: true));
            _wordsShown++;
        }
    }

    /// <summary>0..1 beat flash: gentle scale + glow pulse.</summary>
    public void SetBeat(float flash)
    {
        if (_spec.Glow)
        {
            _cardScale.ScaleX = _cardScale.ScaleY = 1 + 0.02 * flash;
            _glow.Opacity = 0.35 + 0.4 * flash;
        }
        else
        {
            _textScale.ScaleX = _textScale.ScaleY = 1 + (_spec.FullScreen || _style == LyricsStyle.Bold ? 0.035 : 0.01) * flash;
        }
    }

    private void RebuildCurrentLine()
    {
        _current.Children.Clear();
        _outgoing.Children.Clear();
        if (_line is null || _lineIsGap) { if (_line is not null || _lineIsGap) ShowGapNote(); return; }
        for (int i = 0; i < _wordsShown && i < _line.Words.Length; i++)
            _current.Children.Add(MakeWord(_line.Words[i], i == _line.Words.Length - 1 && _spec.Chips && _line.Words.Length > 1, animate: false));
    }

    private void ShowGapNote()
    {
        var note = new TextBlock
        {
            Text = "♪  ♪  ♪",
            FontFamily = DisplayFont,
            FontSize = _spec.Font * _size * 0.8,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_a),
            Margin = new Thickness(4),
        };
        note.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 0.9, TimeSpan.FromSeconds(1.2))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        _current.Children.Add(note);
    }

    private FrameworkElement MakeWord(string text, bool chip, bool animate)
    {
        double f = _spec.Font * _size;
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = DisplayFont,
            FontSize = f,
            FontWeight = WeightFor(text, chip),
            Foreground = Brushes.WhiteSmoke,
        };
        var b = new Border
        {
            Child = tb,
            Margin = new Thickness(f * 0.12, f * 0.03, f * 0.12, f * 0.03),
            CornerRadius = new CornerRadius(f * 0.16),
            RenderTransformOrigin = new Point(0.5, 0.6),
        };
        if (chip) { b.Tag = "chip"; StyleChip(b); }

        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform(0, 0);
        b.RenderTransform = new TransformGroup { Children = { scale, move } };
        if (!animate) return b;

        var dur = TimeSpan.FromMilliseconds(_spec.FlyMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        b.Opacity = 0;
        b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        if (_spec.FlyScale != 1)
        {
            var s = new DoubleAnimation(_spec.FlyScale, 1, dur) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        }
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_spec.FlyRise * _size, 0, dur) { EasingFunction = ease });
        if (_spec.FlyBlur > 0)
        {
            var blur = new BlurEffect { Radius = _spec.FlyBlur * _size, KernelType = KernelType.Gaussian };
            b.Effect = blur;
            var ba = new DoubleAnimation(_spec.FlyBlur * _size, 0, dur) { EasingFunction = ease };
            ba.Completed += (_, _) => b.Effect = null; // drop the effect once sharp (keeps rendering cheap)
            blur.BeginAnimation(BlurEffect.RadiusProperty, ba);
        }
        return b;
    }

    /// <summary>Bold style mixes weights: little words light, normal words bold, the key word heaviest.</summary>
    private FontWeight WeightFor(string word, bool chip)
    {
        if (_style != LyricsStyle.Bold) return _spec.Weight;
        if (chip) return FontWeights.Black;
        return word.Trim(',', '.', '!', '?', '\'', '"').Length <= 3 ? FontWeights.Light : FontWeights.Bold;
    }

    private void StyleChip(Border b)
    {
        double f = _spec.Font * _size;
        b.Background = new SolidColorBrush(_a);
        b.Padding = new Thickness(f * 0.22, 0, f * 0.22, f * 0.05);
        // Dark text on light chips, white on dark chips.
        double lum = (0.2126 * _a.R + 0.7152 * _a.G + 0.0722 * _a.B) / 255.0;
        if (b.Child is TextBlock tb) tb.Foreground = lum > 0.55 ? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)) : Brushes.White;
    }

    /// <summary>The old line rises and fades out while the new one starts.</summary>
    private void SendCurrentAway()
    {
        _outgoing.BeginAnimation(OpacityProperty, null);
        _outgoing.Children.Clear();
        if (_current.Children.Count == 0) return;
        var words = _current.Children.Cast<UIElement>().ToList();
        _current.Children.Clear();
        foreach (var w in words) _outgoing.Children.Add(w);

        double f = _spec.Font * _size;
        var move = new TranslateTransform(0, 0);
        _outgoing.RenderTransform = move;
        var dur = TimeSpan.FromMilliseconds(Math.Max(160, _spec.FlyMs - 40));
        var fade = new DoubleAnimation(1, 0, dur);
        fade.Completed += (_, _) => _outgoing.Children.Clear();
        _outgoing.BeginAnimation(OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -f * 0.6, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    // ------------------------------------------------------------------ window placement

    private void Reposition()
    {
        if (_spec.FullScreen || !IsLoaded && ActualWidth == 0) return;
        var wa = SystemParameters.WorkArea;
        double w = ActualWidth, h = ActualHeight;
        Left = wa.Left + (wa.Width - w) / 2;
        Top = _placement switch
        {
            LyricsPlacement.Top => wa.Top + wa.Height * 0.05 - 30,
            LyricsPlacement.Middle => wa.Top + (wa.Height - h) / 2,
            _ => wa.Bottom - h - wa.Height * 0.06 + 30,
        };
        Top = Math.Clamp(Top, wa.Top - 44, Math.Max(wa.Top, wa.Bottom - h + 44));
    }

    // ------------------------------------------------------------------ click-through

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private void MakeClickThrough()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
    }
}
