namespace Tuxflix.Player.Analysis;

/// <summary>
/// In-place iterative radix-2 Cooley-Tukey FFT of real input.
/// </summary>
/// <remarks>
/// Deliberately the plain complex transform with the imaginary part zeroed, rather than the
/// real-input packing trick that halves the work: at 4096 points and sixty windows a second the
/// whole analysis is far inside a frame either way, and the unpacking step is easy to get subtly
/// wrong. Optimise it when a profile says so, not before.
/// </remarks>
public sealed class Fft
{
    private readonly int _n;
    private readonly float[] _cos;
    private readonly float[] _sin;
    private readonly int[] _reverse;
    private readonly float[] _re;
    private readonly float[] _im;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException($"FFT size must be a power of two, got {n}.", nameof(n));

        _n = n;
        var levels = System.Numerics.BitOperations.TrailingZeroCount((uint)n);
        _cos = new float[n / 2];
        _sin = new float[n / 2];
        for (var i = 0; i < n / 2; i++)
        {
            var angle = -2.0 * Math.PI * i / n;
            _cos[i] = (float)Math.Cos(angle);
            _sin[i] = (float)Math.Sin(angle);
        }

        // Bit-reversal permutation, precomputed: the inner loop is hot and the table small.
        _reverse = new int[n];
        for (var i = 0; i < n; i++) _reverse[i] = (int)(ReverseBits((uint)i) >> (32 - levels));

        _re = new float[n];
        _im = new float[n];
    }

    public int Size => _n;

    /// <summary>
    /// Transforms <paramref name="input"/> (N samples) and writes the magnitudes of bins 0..N/2,
    /// normalised so a full-scale sine reads 1.0 in its own bin: that is what makes the dBFS
    /// conversion downstream mean something rather than merely rank.
    /// </summary>
    public void MagnitudeSpectrum(ReadOnlySpan<float> input, Span<float> magnitude)
    {
        if (input.Length != _n) throw new ArgumentException($"Input must be {_n} samples, got {input.Length}.", nameof(input));
        if (magnitude.Length != _n / 2 + 1) throw new ArgumentException($"Magnitude must be {_n / 2 + 1} bins, got {magnitude.Length}.", nameof(magnitude));

        for (var i = 0; i < _n; i++)
        {
            _re[_reverse[i]] = input[i];
            _im[i] = 0f;
        }

        for (var size = 2; size <= _n; size <<= 1)
        {
            var half = size >> 1;
            var step = _n / size;
            for (var i = 0; i < _n; i += size)
            {
                for (int j = i, k = 0; j < i + half; j++, k += step)
                {
                    var l = j + half;
                    var tre = (_re[l] * _cos[k]) - (_im[l] * _sin[k]);
                    var tim = (_re[l] * _sin[k]) + (_im[l] * _cos[k]);
                    _re[l] = _re[j] - tre;
                    _im[l] = _im[j] - tim;
                    _re[j] += tre;
                    _im[j] += tim;
                }
            }
        }

        // DC and Nyquist are not doubled; every other bin carries half its energy in the mirror.
        var scale = 2f / _n;
        magnitude[0] = MathF.Abs(_re[0]) / _n;
        for (var i = 1; i < _n / 2; i++) magnitude[i] = MathF.Sqrt((_re[i] * _re[i]) + (_im[i] * _im[i])) * scale;
        magnitude[_n / 2] = MathF.Abs(_re[_n / 2]) / _n;
    }

    private static uint ReverseBits(uint x)
    {
        x = ((x & 0x55555555u) << 1) | ((x >> 1) & 0x55555555u);
        x = ((x & 0x33333333u) << 2) | ((x >> 2) & 0x33333333u);
        x = ((x & 0x0F0F0F0Fu) << 4) | ((x >> 4) & 0x0F0F0F0Fu);
        x = ((x & 0x00FF00FFu) << 8) | ((x >> 8) & 0x00FF00FFu);
        return (x << 16) | (x >> 16);
    }
}
