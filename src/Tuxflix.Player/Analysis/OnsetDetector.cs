namespace Tuxflix.Player.Analysis;

/// <summary>
/// Beat detection by spectral flux: the sum of per-band level RISES since the last window,
/// against a rolling median of recent flux.
/// </summary>
/// <remarks>
/// Flux rather than loudness, because loudness cannot tell a beat from a sustained loud passage:
/// a held chord is loud on every window and would fire continuously, while a kick under a quiet
/// passage would never fire. Only rises count. The threshold is a median, not a mean: one crash
/// cymbal drags a mean up far enough to swallow the next several beats, where a median barely moves.
/// </remarks>
public sealed class OnsetDetector
{
    private readonly float[] _previous;
    private readonly float[] _history;
    private readonly float[] _sorted;
    private int _pos;
    private int _filled;
    private float _sinceBeat;

    public OnsetDetector(int bandCount, int historySize = 43)
    {
        _previous = new float[bandCount];
        _history = new float[historySize];
        _sorted = new float[historySize];
    }

    /// <summary>How far above the median the flux must be. Below ~1.3 a groove fires every window; above ~2 only the loudest hits.</summary>
    public float Sensitivity { get; set; } = 1.5f;

    /// <summary>Beats closer than this (240 bpm) are one onset seen twice.</summary>
    public float MinIntervalSeconds { get; set; } = 0.25f;

    /// <summary>1 on a beat, then decaying: a mode that pulses on the beat alone flashes for one frame and reads as a glitch.</summary>
    public float Intensity { get; private set; }

    public bool Beat { get; private set; }

    /// <summary>Feeds one window of normalised band levels.</summary>
    public void Process(ReadOnlySpan<float> bands, float dt)
    {
        var flux = 0f;
        for (var b = 0; b < _previous.Length && b < bands.Length; b++)
        {
            var rise = bands[b] - _previous[b];
            if (rise > 0f) flux += rise;
            _previous[b] = bands[b];
        }

        _sinceBeat += dt;
        Intensity = MathF.Max(0f, Intensity - (dt * 3f));
        Beat = false;

        if (_filled >= _history.Length)
        {
            _history.CopyTo(_sorted, 0);
            Array.Sort(_sorted);
            var median = _sorted[_sorted.Length / 2];

            // The absolute floor stops a quiet passage, where the median is near zero, from
            // making every speck of noise a beat.
            if (flux > median * Sensitivity && flux > 0.05f && _sinceBeat >= MinIntervalSeconds)
            {
                Beat = true;
                Intensity = 1f;
                _sinceBeat = 0f;
            }
        }

        _history[_pos] = flux;
        _pos = (_pos + 1) % _history.Length;
        if (_filled < _history.Length) _filled++;
    }

    public void Reset()
    {
        Array.Clear(_previous);
        Array.Clear(_history);
        _pos = _filled = 0;
        Intensity = 0;
        Beat = false;
    }
}
