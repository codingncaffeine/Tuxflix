namespace Tuxflix.Player.Analysis;

/// <summary>Tunables for one analysis chain.</summary>
public sealed class AnalyserOptions
{
    public int SampleRate { get; init; } = 48000;

    public int FftSize { get; init; } = 4096;

    public int BandCount { get; init; } = 32;

    /// <summary>30 Hz is below the lowest note most systems reproduce; 16 kHz is above where music carries usable energy.</summary>
    public float MinHz { get; init; } = 30f;

    public float MaxHz { get; init; } = 16000f;

    /// <summary>The dB floor: 60 dB of range across the height of a bar.</summary>
    public float FloorDb { get; init; } = -60f;

    /// <summary>Cosmetic spectral tilt, dB per octave above <see cref="TiltPivotHz"/>. Not a calibration.</summary>
    public float TiltDbPerOctave { get; init; }

    public float TiltPivotHz { get; init; } = 1000f;

    /// <summary>Bar fall rate. Attack is instant: symmetric smoothing misses transients.</summary>
    public float DecayDbPerSecond { get; init; } = 15f;

    /// <summary>How long a cap hangs at a new high before it lets go: the pause that makes it read as an object, not the bar's top edge.</summary>
    public float PeakHoldSeconds { get; init; } = 0.4f;

    /// <summary>Cap acceleration, in fractions of full scale per second squared: from the top it lands in about 0.8 s, visibly gathering speed.</summary>
    public float PeakGravity { get; init; } = 3.0f;

    /// <summary>Below this the display idles instead of twitching on the noise floor.</summary>
    public float SilenceGateDbfs { get; init; } = -55f;

    /// <summary>Samples of history kept for the scope modes.</summary>
    public int WaveformLength { get; init; } = 1024;
}

/// <summary>
/// The analysis chain every visualizer mode shares: window, FFT, geometric bands, tilt, dB,
/// envelope, peak-hold caps and beat detection. One chain for all modes means a mode costs a draw
/// routine, not an analyser, and two modes can never disagree about what the audio did.
/// </summary>
public sealed class SpectrumAnalyser
{
    private readonly AnalyserOptions _o;
    private readonly Fft _fft;
    private readonly float[] _window;
    private readonly float[] _magnitude;
    private readonly float[] _frame;
    private readonly int[] _binLo;
    private readonly int[] _binHi;
    private readonly float[] _binCentre;
    private readonly float[] _centres;
    private readonly float[] _tiltDb;
    private readonly float[] _bandDb;
    private readonly float[] _peakHold;

    // Cap position and velocity in NORMALISED display units, not decibels: gravity only looks
    // like gravity in the space the cap is drawn in.
    private readonly float[] _peakNorm;
    private readonly float[] _peakVel;
    private readonly OnsetDetector _onset;
    private long _sequence;

    public SpectrumAnalyser(AnalyserOptions options)
    {
        _o = options;
        _fft = new Fft(_o.FftSize);
        _magnitude = new float[(_o.FftSize / 2) + 1];
        _frame = new float[_o.FftSize];

        // Hann: without a window, leakage smears every tone across its neighbours.
        _window = new float[_o.FftSize];
        for (var i = 0; i < _o.FftSize; i++) _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (_o.FftSize - 1)));

        var n = _o.BandCount;
        _binLo = new int[n];
        _binHi = new int[n];
        _binCentre = new float[n];
        _centres = new float[n];
        _tiltDb = new float[n];
        _bandDb = new float[n];
        _peakHold = new float[n];
        _peakNorm = new float[n];
        _peakVel = new float[n];

        var binHz = (float)_o.SampleRate / _o.FftSize;
        var maxBin = _o.FftSize / 2;
        var ratio = Math.Log((double)_o.MaxHz / _o.MinHz);
        for (var b = 0; b < n; b++)
        {
            // Geometric edges: an octave takes the same width wherever it sits.
            var lo = (float)(_o.MinHz * Math.Exp(ratio * b / n));
            var hi = (float)(_o.MinHz * Math.Exp(ratio * (b + 1) / n));
            _centres[b] = MathF.Sqrt(lo * hi);
            _binLo[b] = Math.Clamp((int)MathF.Floor(lo / binHz), 1, maxBin);
            _binHi[b] = Math.Clamp((int)MathF.Ceiling(hi / binHz) - 1, 1, maxBin);
            _binCentre[b] = Math.Clamp(_centres[b] / binHz, 1f, maxBin - 1f);
            _tiltDb[b] = _o.TiltDbPerOctave * MathF.Log2(_centres[b] / _o.TiltPivotHz);
            _bandDb[b] = _o.FloorDb;
        }

        _onset = new OnsetDetector(n);
    }

    public AnalyserOptions Options => _o;

    public IReadOnlyList<float> BandCentres => _centres;

    /// <summary>Analyses the most recent window: <paramref name="mono"/> is exactly FftSize samples, oldest first.</summary>
    public void Process(ReadOnlySpan<float> mono, float dt)
    {
        for (var i = 0; i < _o.FftSize; i++) _frame[i] = mono[i] * _window[i];
        _fft.MagnitudeSpectrum(_frame, _magnitude);

        // Hann halves the coherent gain; undo it so a full-scale sine reads 0 dBFS in its band.
        const float HannGain = 2f;
        for (var b = 0; b < _o.BandCount; b++)
        {
            float amp;
            if (_binHi[b] >= _binLo[b])
            {
                // The SUM of power across the band, as a real-time analyser does: a geometric
                // band's width grows with its centre, so summed power renders roughly pink music
                // flat with no correction, and a pure tone reads its true level in any band width.
                var power = 0f;
                for (var k = _binLo[b]; k <= _binHi[b]; k++) power += _magnitude[k] * _magnitude[k];
                amp = MathF.Sqrt(power);
            }
            else
            {
                // A band narrower than one bin: interpolate, or the bass shows as stair steps.
                var k = (int)_binCentre[b];
                var f = _binCentre[b] - k;
                amp = (_magnitude[k] * (1f - f)) + (_magnitude[k + 1] * f);
            }

            var db = Math.Clamp(ToDb(amp * HannGain) + _tiltDb[b], _o.FloorDb, 0f);

            // Instant attack, timed decay.
            var decayed = _bandDb[b] - (_o.DecayDbPerSecond * dt);
            _bandDb[b] = Math.Max(_o.FloorDb, db > decayed ? db : decayed);

            // The cap: rides the bar, hangs when the bar drops away, then falls with gravity and
            // lands on the bar rather than sinking through it.
            var norm = Normalise(_bandDb[b]);
            if (norm >= _peakNorm[b])
            {
                _peakNorm[b] = norm;
                _peakVel[b] = 0f;
                _peakHold[b] = _o.PeakHoldSeconds;
            }
            else if (_peakHold[b] > 0f)
            {
                _peakHold[b] -= dt;
            }
            else
            {
                _peakVel[b] += _o.PeakGravity * dt;
                _peakNorm[b] -= _peakVel[b] * dt;
                if (_peakNorm[b] <= norm)
                {
                    _peakNorm[b] = norm;
                    _peakVel[b] = 0f;
                }
            }
        }

        // The onset detector sees a complete set of bands, on the display's own scale.
        Span<float> bands = stackalloc float[_o.BandCount];
        for (var b = 0; b < _o.BandCount; b++) bands[b] = Normalise(_bandDb[b]);
        _onset.Process(bands, dt);
    }

    /// <summary>Lets the bars and caps fall and the beat decay while nothing is analysed (paused, or between tracks).</summary>
    public void Idle(float dt)
    {
        for (var b = 0; b < _o.BandCount; b++)
        {
            _bandDb[b] = Math.Max(_o.FloorDb, _bandDb[b] - (_o.DecayDbPerSecond * 4f * dt));
            _peakHold[b] = 0f;
            _peakVel[b] += _o.PeakGravity * dt;
            _peakNorm[b] = Math.Max(Normalise(_bandDb[b]), _peakNorm[b] - (_peakVel[b] * dt));
        }

        Span<float> bands = stackalloc float[_o.BandCount];
        for (var b = 0; b < _o.BandCount; b++) bands[b] = Normalise(_bandDb[b]);
        _onset.Process(bands, dt);
    }

    /// <summary>A snapshot for the renderers; the arrays are fresh, so nothing mutates under a reader.</summary>
    public AudioFrame Publish(ReadOnlySpan<float> waveLeft, ReadOnlySpan<float> waveRight, float peakDbfs)
    {
        var n = _o.BandCount;
        var bands = new float[n];
        var peaks = new float[n];
        for (var b = 0; b < n; b++)
        {
            bands[b] = Normalise(_bandDb[b]);
            peaks[b] = Math.Clamp(_peakNorm[b], 0f, 1f);
        }

        var left = waveLeft.ToArray();
        var right = waveRight.ToArray();
        var mono = new float[left.Length];
        double sumL = 0, sumR = 0;
        float maxL = 0, maxR = 0;
        for (var i = 0; i < left.Length; i++)
        {
            mono[i] = 0.5f * (left[i] + right[i]);
            sumL += left[i] * left[i];
            sumR += right[i] * right[i];
            maxL = MathF.Max(maxL, MathF.Abs(left[i]));
            maxR = MathF.Max(maxR, MathF.Abs(right[i]));
        }

        var count = Math.Max(1, left.Length);
        return new AudioFrame
        {
            Bands = bands,
            Peaks = peaks,
            BandCentres = [.. _centres],
            Waveform = mono,
            WaveformLeft = left,
            WaveformRight = right,
            RmsLeft = Normalise(ToDb((float)Math.Sqrt(sumL / count))),
            RmsRight = Normalise(ToDb((float)Math.Sqrt(sumR / count))),
            PeakLeft = Normalise(ToDb(maxL)),
            PeakRight = Normalise(ToDb(maxR)),
            Beat = _onset.Beat,
            BeatIntensity = _onset.Intensity,
            Silent = peakDbfs < _o.SilenceGateDbfs,
            Sequence = ++_sequence,
        };
    }

    public static float ToDb(float amplitude) => amplitude > 1e-7f ? 20f * MathF.Log10(amplitude) : -140f;

    private float Normalise(float db) => Math.Clamp((db - _o.FloorDb) / -_o.FloorDb, 0f, 1f);
}
