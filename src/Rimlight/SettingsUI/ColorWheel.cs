using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rimlight.SettingsUI;

/// <summary>
/// Hue/saturation wheel: angle = hue, distance from centre = saturation (value fixed at full).
/// Click or drag to pick. Bind <see cref="Hue"/> (0..360) and <see cref="Saturation"/> (0..1) two-way.
/// </summary>
public sealed class ColorWheel : FrameworkElement
{
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(
        nameof(Hue), typeof(double), typeof(ColorWheel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
        nameof(Saturation), typeof(double), typeof(ColorWheel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Hue { get => (double)GetValue(HueProperty); set => SetValue(HueProperty, value); }
    public double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }

    private static BitmapSource? _wheel;

    public ColorWheel()
    {
        Cursor = Cursors.Cross;
        Focusable = false;
    }

    private static BitmapSource Wheel => _wheel ??= BuildWheel(256);

    private static BitmapSource BuildWheel(int size)
    {
        var px = new byte[size * size * 4];
        double c = (size - 1) / 2.0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = x - c, dy = y - c;
            double r = Math.Sqrt(dx * dx + dy * dy) / c;
            int i = (y * size + x) * 4;
            if (r > 1.0) continue; // transparent outside
            double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            var col = ColorMath.FromHsv(hue, Math.Min(1, r), 1);
            double edgeAlpha = Math.Clamp((1.0 - r) * c, 0, 1); // anti-aliased rim
            px[i] = col.B; px[i + 1] = col.G; px[i + 2] = col.R; px[i + 3] = (byte)(255 * edgeAlpha);
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        bmp.Freeze();
        return bmp;
    }

    protected override Size MeasureOverride(Size available)
    {
        double s = Math.Min(double.IsInfinity(available.Width) ? 160 : available.Width,
                            double.IsInfinity(available.Height) ? 160 : available.Height);
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;
        dc.DrawImage(Wheel, new Rect(ox, oy, size, size));

        double r = size / 2;
        double a = Hue * Math.PI / 180;
        var p = new Point(ox + r + Math.Cos(a) * Saturation * r, oy + r + Math.Sin(a) * Saturation * r);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3) { Brush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) }, p, 8, 8);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2), p, 7, 7);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Pick(Point pt)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        double r = size / 2;
        double dx = pt.X - ActualWidth / 2, dy = pt.Y - ActualHeight / 2;
        Hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        Saturation = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / r, 0, 1);
    }
}

public static class ColorMath
{
    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (!Rimlight.Core.Color.Rgb.TryParseHex(hex, out var rgb)) return false;
        color = Color.FromRgb((byte)Math.Round(rgb.R * 255), (byte)Math.Round(rgb.G * 255), (byte)Math.Round(rgb.B * 255));
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
