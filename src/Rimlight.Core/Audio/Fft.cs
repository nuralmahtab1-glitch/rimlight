namespace Rimlight.Core.Audio;

/// <summary>In-place iterative radix-2 FFT with precomputed twiddles. Allocation-free after construction.</summary>
public sealed class Fft
{
    private readonly int _n;
    private readonly int[] _bitRev;
    private readonly float[] _cos, _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentException("FFT size must be a power of two", nameof(size));
        _n = size;
        _bitRev = new int[size];
        int bits = (int)Math.Log2(size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _bitRev[i] = r;
        }
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / size);
        }
    }

    public int Size => _n;

    /// <summary>Transforms (re, im) in place.</summary>
    public void Transform(float[] re, float[] im)
    {
        for (int i = 0; i < _n; i++)
        {
            int j = _bitRev[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= _n; len <<= 1)
        {
            int half = len >> 1, step = _n / len;
            for (int i = 0; i < _n; i += len)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = _sin[k * step];
                    int a = i + k, b = a + half;
                    float tr = re[b] * wr - im[b] * wi;
                    float ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
            }
        }
    }
}
