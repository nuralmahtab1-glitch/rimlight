using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Rimlight.Core.Lyrics;
using Rimlight.Core.Settings;

namespace Rimlight.Lyrics;

/// <summary>
/// The "Reel" movement of the Chat screen, copied from the reference video (frame by frame):
///  • the whole stack is alive like a hand-held shot: it drifts, tilts and breathes all the time, and jolts on the beat;
///  • a new line starts as an empty, full-width iMessage-blue pill that slides up from below with a motion blur;
///  • its words pop in one by one: small, faint and blurry first, then they grow into place with a little overshoot,
///    and every word keeps floating a tiny bit;
///  • when the next line arrives, the blue pill shrinks to fit its text and turns into a dark see-through pill, while
///    everything above it is smeared by the slide, blurs, fades and rises away; about three are visible.
/// </summary>
public sealed class ReelChatStack : Grid, IChatView
{
    private static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");
    private static readonly Color PastColor = Color.FromArgb(0xC4, 0x10, 0x10, 0x12);
    private static readonly Color CurrentColor = Color.FromArgb(0xF4, 0x0A, 0x84, 0xFF);
    // How an older bubble looks by age (0 = newest): opacity and resting blur radius.
    private static readonly double[] AgeOpacity = [1, 0.9, 0.45, 0.0];
    private static readonly double[] AgeBlur = [0, 0, 4, 10];
    private const int Visible = 3;

    private sealed class Bubble
    {
        public required Grid Root;
        public required Border Body;
        public required SolidColorBrush Fill;
        public required System.Windows.Shapes.Path Tail;
        public required WrapPanel Words;
        public required BlurEffect Blur;
        public required ScaleTransform Scale;
        public required string[] Text;
        public required double FitWidth;
        public int Shown;
    }

    private readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _slide = new();
    private readonly List<Bubble> _bubbles = [];
    // hand-held shake
    private readonly TranslateTransform _shake = new();
    private readonly RotateTransform _tilt = new();
    private readonly ScaleTransform _breath = new();
    private readonly ScaleTransform _jolt = new();
    private double _size = 1.4;
    private LyricLine? _line;
    private bool _gap = true, _faded = true;
    private readonly Random _rng = new();

    private readonly bool _strips;

    /// <param name="strips">Strips look: every line is a dark pill that fits its text and enters whole (no blue pill, no word-by-word).</param>
    public ReelChatStack(bool strips = false)
    {
        _strips = strips;
        IsHitTestVisible = false;
        ClipToBounds = false;
        _stack.RenderTransform = _slide;
        Children.Add(_stack);

        RenderTransformOrigin = new Point(0.3, 0.7);
        RenderTransform = new TransformGroup { Children = { _jolt, _breath, _tilt, _shake } };
        StartShake();
    }

    /// <summary>Endless, slightly random hand-held movement: drift, tilt, breathing.</summary>
    private void StartShake()
    {
        _shake.BeginAnimation(TranslateTransform.XProperty, Wander(9, 5.3));
        _shake.BeginAnimation(TranslateTransform.YProperty, Wander(7, 4.1));
        _tilt.BeginAnimation(RotateTransform.AngleProperty, Wander(0.9, 6.7));
        var breathe = Wander(0.018, 7.9, 1.0);
        _breath.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
    }

    private DoubleAnimationUsingKeyFrames Wander(double amp, double seconds, double centre = 0)
    {
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        const int steps = 7;
        double first = 0;
        for (int i = 0; i <= steps; i++)
        {
            double v = i == steps ? first : centre + (_rng.NextDouble() * 2 - 1) * amp;
            if (i == 0) first = v;
            a.KeyFrames.Add(new SplineDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds * (i + 1) / (steps + 1))),
                new KeySpline(0.45, 0, 0.55, 1)));
        }
        return a;
    }

    public void Configure(ChatBubbleLook look, double size, double width)
    {
        _size = size;
        _stack.Width = width;
        Clear();
        _line = null; _gap = true; _faded = true;
    }

    public void SetBeat(float flash)
    {
        _jolt.ScaleX = _jolt.ScaleY = 1 + 0.02 * flash;
    }

    public void ShowLine(LyricLine? line, int wordsVisible, bool gap)
    {
        if (!ReferenceEquals(line, _line) || gap != _gap)
        {
            _line = line;
            _gap = gap;
            if (gap || line is null || line.Words.Length == 0) { FadeAway(); return; }
            if (_faded) { Clear(); _stack.BeginAnimation(OpacityProperty, null); _stack.Opacity = 1; _faded = false; }
            AddBubble(line);
        }
        if (_line is null || _gap || _bubbles.Count == 0) return;
        var b = _bubbles[^1];
        int n = _strips ? b.Text.Length : Math.Min(wordsVisible, b.Text.Length);
        while (b.Shown < n) AddWord(b, b.Text[b.Shown++]);
    }

    /// <summary>One word pops in: small, faint and blurry, then grows into place with a little overshoot.</summary>
    private void AddWord(Bubble b, string word)
    {
        double fs = 31 * _size;
        if (_strips)
        {
            b.Words.Children.Add(new TextBlock
            {
                Text = word, FontFamily = Font, FontSize = fs, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0.28 * fs, 0),
            });
            return;
        }
        var scale = new ScaleTransform(0.45, 0.45);
        var drift = new TranslateTransform();
        var blur = new BlurEffect { Radius = 7, RenderingBias = RenderingBias.Performance };
        var tb = new TextBlock
        {
            Text = word, FontFamily = Font, FontSize = fs, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0.28 * fs, 0),
            RenderTransformOrigin = new Point(0.5, 0.85),
            RenderTransform = new TransformGroup { Children = { scale, drift } },
            Effect = blur, Opacity = 0,
        };
        b.Words.Children.Add(tb);
        var dur = TimeSpan.FromMilliseconds(300);
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.45, 1, dur) { EasingFunction = pop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.45, 1, dur) { EasingFunction = pop });
        tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        var unblur = new DoubleAnimation(7, 0, dur);
        unblur.Completed += (_, _) => tb.Effect = null;           // no effect once settled: cheaper to draw
        blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
        // the letters never sit completely still
        drift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-1.6, 1.6, TimeSpan.FromSeconds(1.3 + _rng.NextDouble() * 1.2))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(300),
          EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void AddBubble(LyricLine line)
    {
        double dh = 31 * _size * 1.4 + 21 * _size + 12 * _size;   // approx. height of one single-line bubble incl. gap
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(340);

        // The previous bubble stops being "current": shrinks to its text, turns into a dark see-through pill, loses its tail.
        if (_bubbles.Count > 0)
        {
            var prev = _bubbles[^1];
            prev.Fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(PastColor, TimeSpan.FromMilliseconds(320)));
            prev.Tail.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
            prev.Body.BeginAnimation(WidthProperty, new DoubleAnimation(prev.Body.Width, prev.FitWidth, dur) { EasingFunction = ease });
        }

        string[] words = line.Words.ToArray();
        string full = string.Join(" ", words);
        double fs = 31 * _size, maxW = _stack.Width * 0.94;
        double padX = 20 * _size, padTop = 10 * _size, padBot = 11 * _size;

        // How wide the text is on one line (for shrinking later), and how tall it is wrapped (so the bubble never grows).
        var probe = new TextBlock { Text = full, FontFamily = Font, FontSize = fs, FontWeight = FontWeights.SemiBold };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fit = Math.Min(maxW, probe.DesiredSize.Width + 0.28 * fs * words.Length + 2 * padX + 8);
        var ghost = new TextBlock
        {
            Text = full, FontFamily = Font, FontSize = fs, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Opacity = 0,
        };
        var wordsPanel = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

        var fill = new SolidColorBrush(_strips ? PastColor : CurrentColor);
        var body = new Border
        {
            Width = _strips ? fit : maxW,
            Background = fill,
            CornerRadius = new CornerRadius(22 * _size),
            Padding = new Thickness(padX, padTop, padX, padBot),
            Child = new Grid { Children = { ghost, wordsPanel } },
        };
        var tail = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 0,12 C 0,10 -5,10 -9,14 C -3,15 5,12 10,5 L 10,0 Z"),
            Fill = fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 0), Stretch = Stretch.None, Visibility = _strips ? Visibility.Collapsed : Visibility.Visible,
        };
        var blur = new BlurEffect { Radius = 16, RenderingBias = RenderingBias.Performance };
        var scale = new ScaleTransform(0.94, 0.94);
        var root = new Grid
        {
            Margin = new Thickness(0, 0, 0, 12 * _size),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0, 1),
            RenderTransform = scale,
            Effect = blur,
            Opacity = 0,
            Children = { tail, body },
        };
        _stack.Children.Add(root);
        _bubbles.Add(new Bubble { Root = root, Body = body, Fill = fill, Tail = tail, Words = wordsPanel, Blur = blur, Scale = scale, Text = words, FitWidth = fit });

        // New pill: slides up from below out of a motion blur.
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(16, 0, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
        if (_bubbles.Count > 1)
            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dh, 0, dur) { EasingFunction = ease });

        // Older pills: smeared by the slide (blur spike + vertical stretch), then they rest blurrier and fainter by age.
        for (int i = 0; i < _bubbles.Count - 1; i++)
        {
            int age = _bubbles.Count - 1 - i;
            var o = _bubbles[i];
            int a = Math.Min(age, AgeOpacity.Length - 1);
            o.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(AgeOpacity[a], dur) { EasingFunction = ease });
            var smear = new DoubleAnimationUsingKeyFrames();
            smear.KeyFrames.Add(new EasingDoubleKeyFrame(12, KeyTime.FromPercent(0.25)));
            smear.KeyFrames.Add(new EasingDoubleKeyFrame(AgeBlur[a], KeyTime.FromPercent(1.0), ease));
            smear.Duration = dur;
            o.Blur.BeginAnimation(BlurEffect.RadiusProperty, smear);
            var stretch = new DoubleAnimationUsingKeyFrames { Duration = dur };
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromPercent(0.3)));
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), ease));
            o.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, stretch);
        }
        while (_bubbles.Count > Visible + 1)
        {
            _stack.Children.Remove(_bubbles[0].Root);
            _bubbles.RemoveAt(0);
        }
    }

    private void FadeAway()
    {
        if (_faded) return;
        _faded = true;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500));
        anim.Completed += (_, _) => { if (_faded) Clear(); };
        _stack.BeginAnimation(OpacityProperty, anim);
    }

    private void Clear()
    {
        _stack.Children.Clear();
        _bubbles.Clear();
    }
}
