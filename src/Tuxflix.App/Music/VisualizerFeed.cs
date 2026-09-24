using System.Diagnostics;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Player.Analysis;

namespace Tuxflix.App.Music;

/// <summary>What is playing and where, as one immutable reading any thread may take.</summary>
/// <param name="Url">The playing file, as the player opened it; null when nothing plays.</param>
/// <param name="Key">The track's identity, so a new track is told from a seek.</param>
/// <param name="Position">Seconds, as of <paramref name="Ticks"/>.</param>
/// <param name="Ticks">A <see cref="Stopwatch"/> timestamp of the reading.</param>
/// <param name="Playing">Moving, so the position runs on from the reading.</param>
public sealed record PlayClock(string? Url, string? Key, double Position, long Ticks, bool Playing)
{
    public static PlayClock Nothing { get; } = new(null, null, 0, 0, false);

    /// <summary>Where playback is now: the reading, run on by the time since it when playing.</summary>
    public double Now => Playing ? Position + Stopwatch.GetElapsedTime(Ticks).TotalSeconds : Position;
}

/// <summary>
/// The analysis every visualizer draws from: frames for the full view (32 bands) and for the
/// classic skins (19 bands, quicker), sixty times a second, on a thread of its own.
/// </summary>
/// <remarks>
/// It follows the music player's clock and reads the samples for that moment from a
/// <see cref="ShadowDecoder"/>, which it starts again on a new track or a seek. It runs only while
/// something is watching (<see cref="Listen"/>), so an album played with no visualizer showing
/// never decodes twice. Frames are immutable and swapped by reference: a renderer reads the latest
/// with no lock and no wait.
/// </remarks>
public sealed class VisualizerFeed : IDisposable
{
    private const int FullWindow = 4096;
    private const int ClassicWindow = 1024;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 60);

    private readonly MusicPlayer _music;
    private readonly Func<Dictionary<string, string>> _options;
    private readonly object _gate = new();
    private Thread? _thread;
    private int _listeners;
    private int _generation;
    private volatile bool _running;
    private volatile AudioFrame _full = AudioFrame.Empty(32, 1024);
    private volatile AudioFrame _classic = AudioFrame.Empty(19, 1024);

    internal VisualizerFeed(MusicPlayer music, Func<Dictionary<string, string>> options)
    {
        _music = music;
        _options = options;
    }

    /// <summary>The latest frame for the full view.</summary>
    public AudioFrame Full => _full;

    /// <summary>The latest frame for the classic skins' small analyser.</summary>
    public AudioFrame Classic => _classic;

    /// <summary>Starts the analysis if it is not running; disposing the lease stops it with the last listener.</summary>
    public IDisposable Listen()
    {
        lock (_gate)
        {
            if (_listeners++ == 0)
            {
                // A thread still winding down from the last listener sees a newer generation and stops.
                _running = true;
                var generation = ++_generation;
                _thread = new Thread(() => Run(generation)) { IsBackground = true, Name = "Tuxflix visualizer feed", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
        }

        return new Lease(this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _listeners = 0;
            _running = false;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_listeners > 0 && --_listeners == 0) _running = false;
        }
    }

    private void Run(int generation)
    {
        var full = new SpectrumAnalyser(new AnalyserOptions { WaveformLength = 1024 });

        // Winamp's analyser answered in a few milliseconds and fell fast: a short window, quick decay, brief caps.
        var classic = new SpectrumAnalyser(new AnalyserOptions
        {
            FftSize = ClassicWindow,
            BandCount = 19,
            MinHz = 40f,
            FloorDb = -54f,
            DecayDbPerSecond = 42f,
            PeakHoldSeconds = 0.25f,
            PeakGravity = 4.0f,
            WaveformLength = 512,
        });

        var left = new float[FullWindow];
        var right = new float[FullWindow];
        var mono = new float[FullWindow];
        ShadowDecoder? shadow = null;
        string? shadowKey = null;
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;

        try
        {
            while (_running && Volatile.Read(ref _generation) == generation)
            {
                var next = last + Tick;
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                var now = clock.Elapsed;
                var dt = (float)Math.Clamp((now - last).TotalSeconds, 0.001, 0.1);
                last = now;

                var reading = _music.Clock;
                var position = reading.Now;
                if (reading.Url is null)
                {
                    shadow?.Dispose();
                    shadow = null;
                    shadowKey = null;
                    Idle(full, classic, dt);
                    continue;
                }

                // A new track, or a jump outside what is decoded: decode afresh from here.
                if (shadow is null || shadowKey != reading.Key
                    || position < shadow.Start + (FullWindow / (double)ShadowDecoder.Rate) - 0.05
                    || position > shadow.DecodedUntil + 2)
                {
                    shadow?.Dispose();
                    shadow = new ShadowDecoder(reading.Url, Math.Max(0, position - 0.25), _options());
                    shadowKey = reading.Key;
                }

                shadow.Follow(position);
                if (!reading.Playing || !shadow.TryRead(position, left, right))
                {
                    Idle(full, classic, dt);
                    continue;
                }

                var peak = 0f;
                for (var i = 0; i < FullWindow; i++)
                {
                    mono[i] = 0.5f * (left[i] + right[i]);
                    peak = MathF.Max(peak, MathF.Abs(mono[i]));
                }

                full.Process(mono, dt);
                classic.Process(mono.AsSpan(FullWindow - ClassicWindow), dt);
                var peakDb = SpectrumAnalyser.ToDb(peak);
                _full = full.Publish(left.AsSpan(FullWindow - 1024), right.AsSpan(FullWindow - 1024), peakDb);
                _classic = classic.Publish(left.AsSpan(FullWindow - 512), right.AsSpan(FullWindow - 512), peakDb);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The visualizer analysis stopped.", ex);
        }
        finally
        {
            shadow?.Dispose();
        }
    }

    private void Idle(SpectrumAnalyser full, SpectrumAnalyser classic, float dt)
    {
        full.Idle(dt);
        classic.Idle(dt);
        _full = full.Publish(ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty, -140f);
        _classic = classic.Publish(ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty, -140f);
    }

    private sealed class Lease(VisualizerFeed feed) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) feed.Release();
        }
    }
}
