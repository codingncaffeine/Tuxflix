namespace Tuxflix.Player.Analysis;

/// <summary>
/// One analysis snapshot, published by the analysis thread and read by any visualizer.
/// </summary>
/// <remarks>
/// Immutable and swapped by reference: a renderer never takes a lock on audio and never sees a
/// half-written set of bands. Every level is normalised 0..1 (0 the dB floor, 1 full scale), so a
/// renderer never handles decibels and no mode invents its own scaling.
/// </remarks>
public sealed class AudioFrame
{
    /// <summary>Per-band level after envelope and normalisation.</summary>
    public required float[] Bands { get; init; }

    /// <summary>Per-band peak-hold cap, on the same scale.</summary>
    public required float[] Peaks { get; init; }

    /// <summary>Each band's centre frequency, Hz: a renderer can colour by frequency without re-deriving the layout.</summary>
    public required float[] BandCentres { get; init; }

    /// <summary>Recent mono samples, -1..1, oldest first: the scope modes.</summary>
    public required float[] Waveform { get; init; }

    /// <summary>The same window per channel: a vectorscope plots one against the other, which the mono mix cannot show.</summary>
    public required float[] WaveformLeft { get; init; }

    public required float[] WaveformRight { get; init; }

    public float RmsLeft { get; init; }

    public float RmsRight { get; init; }

    public float PeakLeft { get; init; }

    public float PeakRight { get; init; }

    /// <summary>Set on the window an onset was detected.</summary>
    public bool Beat { get; init; }

    /// <summary>0..1, 1 on a beat and decaying, for a smooth pulse.</summary>
    public float BeatIntensity { get; init; }

    /// <summary>Under the silence gate, or nothing decoded for this moment yet.</summary>
    public bool Silent { get; init; }

    /// <summary>Monotonic count: a renderer can tell a stalled analysis from a static signal.</summary>
    public long Sequence { get; init; }

    public static AudioFrame Empty(int bands, int waveform) => new()
    {
        Bands = new float[bands],
        Peaks = new float[bands],
        BandCentres = new float[bands],
        Waveform = new float[waveform],
        WaveformLeft = new float[waveform],
        WaveformRight = new float[waveform],
        Silent = true,
    };
}
