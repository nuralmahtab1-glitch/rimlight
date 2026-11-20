using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Rimlight.Core.Lyrics;
using Rimlight.Core.Settings;

namespace Rimlight.Lyrics;

/// <summary>
/// The chat bubbles of the "Chat screen" look. Two looks (<see cref="ChatBubbleLook"/>):
///  • TwoTone: lines alternate between dark warm pills on the left (cream text, outlined while current) and
///    light blue-grey pills on the right (white text), tails on the outer side; up to 5 bubbles, wide column.
///  • IMessage: the current line is the newest bubble in iMessage blue with a small tail, past lines turn dark
///    grey and lose their tail; about 3 bubbles.
/// Either way a bubble grows word by word as the line is sung, older lines slide up and fade toward the top,
/// and now and then a bubble takes the next sentence too.
/// </summary>
public sealed class ChatBubbleStack : Grid, IChatView
{
    private static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

    // TwoTone
    private static readonly Brush DarkFill = Frozen(Color.FromArgb(0xE6, 0x26, 0x20, 0x1B));
    private static readonly Brush DarkText = Frozen(Color.FromRgb(0xF2, 0xE2, 0xB6));
    private static readonly Brush BlueFill = Frozen(Color.FromArgb(0xF2, 0x8C, 0xA2, 0xCE));
    private static readonly Brush BlueText = Frozen(Color.FromRgb(0xFA, 0xFC, 0xFF));
    private static readonly Brush Outline = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
    // iMessage
    private static readonly Color PastColor = Color.FromArgb(0xDC, 0x2C, 0x2C, 0x2E);
    private static readonly Color CurrentColor = Color.FromArgb(0xF0, 0x0A, 0x84, 0xFF);

    private sealed class Bubble
    {
        public required Grid Root;
        public required Border Body;
        public required SolidColorBrush? Fill;   // iMessage only (animated)
        public required System.Windows.Shapes.Path Tail;
        public required TextBlock Text;
        public required List<LyricLine> Lines;
        /// <summary>Chosen at random when the bubble is made: it takes the next line too (two sentences in one bubble).</summary>
        public required bool Multi;
        public int Shown;
    }

    private readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _slide = new();
    private readonly ScaleTransform _pulse = new(1, 1);
    private readonly DropShadowEffect _glow = new() { BlurRadius = 26, ShadowDepth = 0, Opacity = 0.2, Color = Color.FromRgb(0x8C, 0xA2, 0xCE) };
    private readonly List<Bubble> _bubbles = [];

    private double _size = 1.0;
    private bool _twoTone = true;
    private LyricLine? _line;
    private bool _gap = true, _faded = true, _nextBlue;

    private const int MaxBubbles = 5;

    public ChatBubbleStack()
    {
        _stack.RenderTransformOrigin = new Point(0.5, 1);
        var group = new TransformGroup();
        group.Children.Add(_pulse);
        group.Children.Add(_slide);
        _stack.RenderTransform = group;
        _stack.Effect = _glow;
        // Older lines fade out toward the top.
        _stack.OpacityMask = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.0),
            new GradientStop(Color.FromArgb(0x70, 0, 0, 0), 0.22),
            new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.55),
        }, 90);
        Children.Add(_stack);
        IsHitTestVisible = false;
    }

    /// <param name="size">1.0 = normal text size.</param>
    /// <param name="width">Width of the chat column.</param>
    public void Configure(ChatBubbleLook look, double size, double width)
    {
        _twoTone = look == ChatBubbleLook.TwoTone;
        _size = size;
        _stack.Width = width;
        Clear();
        _line = null; _gap = true; _faded = true; _nextBlue = false;
    }

    public void SetBeat(float flash)
    {
        _pulse.ScaleX = _pulse.ScaleY = 1 + 0.008 * flash;
        _glow.Opacity = 0.15 + 0.3 * flash;
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
            if (_faded) { Clear(); _stack.BeginAnimation(OpacityProperty, null); _stack.Opacity = 1; _faded = false; _nextBlue = false; }
            if (_bubbles.Count > 0 && _bubbles[^1] is { Multi: true, Lines.Count: 1 } open)
                AppendToBubble(open, line);
            else
                AddBubble(line);
        }
        if (_line is null || _gap || _bubbles.Count == 0) return;
        var b = _bubbles[^1];
        var words = b.Lines[^1].Words;
        int n = Math.Min(wordsVisible, words.Length);
        while (b.Shown < n)
        {
            b.Text.Inlines.Add(new Run((b.Shown > 0 ? " " : "") + words[b.Shown]));
            b.Shown++;
        }
    }

    /// <summary>About one bubble in four holds two sentences (random, but the same for the same line).</summary>
    private static bool PicksTwoSentences(LyricLine line) => ((line.Text.Length * 31 + (int)(line.Time * 10)) % 4) == 0;

    private void AppendToBubble(Bubble b, LyricLine line)
    {
        b.Lines.Add(line);
        b.Text.Inlines.Add(new LineBreak());
        b.Shown = 0;
        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24 * _size * 1.4, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void AddBubble(LyricLine line)
    {
        bool blue = _nextBlue;                 // lines alternate sides: left, right, left...
        _nextBlue = !_nextBlue;

        if (_bubbles.Count > 0)
        {
            var prev = _bubbles[^1];
            if (_twoTone) prev.Body.BorderBrush = Brushes.Transparent;   // only the current dark pill is outlined
            else
            {
                prev.Fill?.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(PastColor, TimeSpan.FromMilliseconds(350)));
                prev.Tail.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            }
        }

        SolidColorBrush? anim = _twoTone ? null : new SolidColorBrush(CurrentColor);
        Brush fill = _twoTone ? (blue ? BlueFill : DarkFill) : anim!;
        var text = new TextBlock
        {
            FontFamily = Font,
            FontSize = 24 * _size,
            FontWeight = _twoTone && blue ? FontWeights.Normal : FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _twoTone ? (blue ? BlueText : DarkText) : Brushes.White,
            MaxWidth = _stack.Width * (_twoTone ? 0.9 : 0.92),
        };
        var body = new Border
        {
            Background = fill,
            BorderBrush = _twoTone && !blue ? Outline : Brushes.Transparent,
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(20 * _size),
            Padding = new Thickness(18 * _size, 9 * _size, 18 * _size, 10 * _size),
            Child = text,
        };
        // Tail on the bottom corner nearest the edge the bubble sits on.
        var tail = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(blue
                ? "M 0,0 L 0,12 C 0,10 5,10 9,14 C 3,15 -5,12 -10,5 L -10,0 Z"
                : "M 0,0 L 0,12 C 0,10 -5,10 -9,14 C -3,15 5,12 10,5 L 10,0 Z"),
            Fill = fill,
            HorizontalAlignment = blue ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = blue ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0),
            Stretch = Stretch.None,
        };
        var root = new Grid
        {
            Margin = new Thickness(0, 0, 0, 10 * _size),
            HorizontalAlignment = blue ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(blue ? 1 : 0, 1),
        };
        root.Children.Add(tail);
        root.Children.Add(body);

        var scale = new ScaleTransform(0.6, 0.6);
        root.RenderTransform = scale;
        root.Opacity = 0;

        _stack.Children.Add(root);
        _bubbles.Add(new Bubble { Root = root, Body = body, Fill = anim, Tail = tail, Text = text, Lines = [line], Multi = PicksTwoSentences(line) });
        while (_bubbles.Count > MaxBubbles)
        {
            _stack.Children.Remove(_bubbles[0].Root);
            _bubbles.RemoveAt(0);
        }

        double h = 24 * _size * 1.4 + 19 * _size + 10 * _size;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        if (_bubbles.Count > 1)
            _slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(h, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void FadeAway()
    {
        if (_faded) return;
        _faded = true;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(600));
        anim.Completed += (_, _) => { if (_faded) Clear(); };
        _stack.BeginAnimation(OpacityProperty, anim);
    }

    private void Clear()
    {
        _stack.Children.Clear();
        _bubbles.Clear();
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>What the stage needs from a chat bubble view (the Chat screen has two ways of moving).</summary>
public interface IChatView
{
    void Configure(ChatBubbleLook look, double size, double width);
    void SetBeat(float flash);
    void ShowLine(LyricLine? line, int wordsVisible, bool gap);
}
