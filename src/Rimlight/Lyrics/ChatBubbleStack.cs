using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Rimlight.Core.Lyrics;

namespace Rimlight.Lyrics;

/// <summary>
/// The chat bubbles themselves (reused by the plain chat window and by the picture look): every lyric line
/// pops up as a message bubble with a tail, older bubbles slide up and dim. Words light up as they are sung.
/// </summary>
public sealed class ChatBubbleStack : Grid
{
    private const int MaxBubbles = 3;
    private static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

    private sealed class Bubble
    {
        public required Grid Root;
        public required Run[] Runs;
        public int Shown;
    }

    private readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _slide = new();
    private readonly ScaleTransform _pulse = new(1, 1);
    private readonly DropShadowEffect _glow = new() { BlurRadius = 36, ShadowDepth = 0, Opacity = 0.3 };
    private readonly List<Bubble> _bubbles = [];

    private bool _blue;
    private double _size = 1.0;
    private LyricLine? _line;
    private bool _gap = true, _faded = true;

    public ChatBubbleStack()
    {
        _stack.RenderTransformOrigin = new Point(0, 1);
        var group = new TransformGroup();
        group.Children.Add(_pulse);
        group.Children.Add(_slide);
        _stack.RenderTransform = group;
        _stack.Effect = _glow;
        Children.Add(_stack);
        IsHitTestVisible = false;
    }

    /// <param name="blue">Blue bubbles (picture look) or dark grey ones.</param>
    /// <param name="size">1.0 = normal text size.</param>
    /// <param name="maxWidth">Widest a bubble may get.</param>
    public void Configure(bool blue, double size, double maxWidth)
    {
        _blue = blue; _size = size;
        _stack.MaxWidth = maxWidth;
        Clear();
        _line = null; _gap = true; _faded = true;
    }

    public void SetColor(Color a) => _glow.Color = a;

    public void SetBeat(float flash)
    {
        _pulse.ScaleX = _pulse.ScaleY = 1 + 0.012 * flash;
        _glow.Opacity = 0.28 + 0.4 * flash;
    }

    public void ShowLine(LyricLine? line, int wordsVisible, bool gap)
    {
        if (!ReferenceEquals(line, _line) || gap != _gap)
        {
            _line = line;
            _gap = gap;
            if (gap || line is null || line.Words.Length == 0)
            {
                FadeAway();
                return;
            }
            if (_faded) { Clear(); _stack.BeginAnimation(OpacityProperty, null); _stack.Opacity = 1; _faded = false; }
            AddBubble(line);
        }
        if (_line is null || _gap || _bubbles.Count == 0) return;
        var b = _bubbles[^1];
        int n = Math.Min(wordsVisible, b.Runs.Length);
        while (b.Shown < n) b.Runs[b.Shown++].Foreground = Brushes.White;
    }

    private void AddBubble(LyricLine line)
    {
        Color fill = _blue ? Color.FromArgb(0xEE, 0x2F, 0x7B, 0xF6) : Color.FromArgb(0xE8, 0x2A, 0x2A, 0x2E);
        var brush = new SolidColorBrush(fill); brush.Freeze();

        var text = new TextBlock
        {
            FontFamily = Font,
            FontSize = 26 * _size,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
        };
        var runs = new Run[line.Words.Length];
        for (int i = 0; i < runs.Length; i++)
        {
            runs[i] = new Run(line.Words[i] + (i < runs.Length - 1 ? " " : "")) { Foreground = Brushes.Transparent };
            text.Inlines.Add(runs[i]);
        }

        var body = new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(22 * _size),
            Padding = new Thickness(20 * _size, 11 * _size, 20 * _size, 12 * _size),
            Child = text,
        };
        // Little tail at the bottom-left corner.
        var tail = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 0,14 C 0,12 -6,12 -10,16 C -3,17 6,14 12,6 L 12,0 Z"),
            Fill = brush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 0),
            Stretch = Stretch.None,
        };
        var root = new Grid
        {
            Margin = new Thickness(0, 0, 0, 12 * _size),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0, 1),
        };
        root.Children.Add(tail);
        root.Children.Add(body);

        var scale = new ScaleTransform(0.6, 0.6);
        root.RenderTransform = scale;
        root.Opacity = 0;

        _stack.Children.Add(root);
        _bubbles.Add(new Bubble { Root = root, Runs = runs });
        while (_bubbles.Count > MaxBubbles)
        {
            _stack.Children.Remove(_bubbles[0].Root);
            _bubbles.RemoveAt(0);
        }
        // Older bubbles drop a touch dimmer as they age.
        for (int i = 0; i < _bubbles.Count - 1; i++)
            _bubbles[i].Root.Opacity = i == _bubbles.Count - 2 ? 0.7 : 0.4;

        _stack.Measure(new Size(_stack.MaxWidth, double.PositiveInfinity));
        double h = root.DesiredSize.Height;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        if (_bubbles.Count > 1)
            _slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(h, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
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
