using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.Player.Analysis;

/// <summary>
/// Decodes a track a second time, silently, into memory: the samples the visualizers analyse.
/// </summary>
/// <remarks>
/// <para>
/// A second libmpv writes raw 48 kHz stereo into a pipe of ours (<c>ao=pcm</c>), far faster than
/// real time, and a reader thread keeps the most recent 45 seconds in a ring. The reader stops
/// taking samples once it is <see cref="AheadSeconds"/> past the listener's position, and mpv, its
/// write blocked, simply waits: the decode is paced by the playback without any timer. Nothing is
/// ever captured from the desktop's audio: this is our own decode of our own file.
/// </para>
/// <para>
/// One decoder serves one track from one starting point; a seek or a new track is a new decoder.
/// Tearing down keeps draining the pipe until mpv is gone, because mpv cannot wind down while
/// blocked writing into a pipe nobody reads.
/// </para>
/// </remarks>
public sealed class ShadowDecoder : IDisposable
{
    public const int Rate = 48000;
    private const double RingSeconds = 45;
    private const double AheadSeconds = 20;

    private readonly short[] _ring;
    private readonly int _capacity;
    private readonly AnonymousPipeServerStream _pipe;
    private readonly Thread _reader;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly MpvPlayer _mpv;
    private long _written;
    private long _wanted;
    private volatile bool _draining;
    private volatile bool _disposed;

    /// <param name="url">The track, as the player plays it.</param>
    /// <param name="start">Where to start, in seconds.</param>
    /// <param name="options">Extra mpv options: the request headers and user agent the server wants.</param>
    public ShadowDecoder(string url, double start, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Start = Math.Max(0, start);
        _capacity = (int)(Rate * RingSeconds);
        _ring = new short[_capacity * 2];
        _wanted = 0;

        _pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var writeEnd = _pipe.ClientSafePipeHandle.DangerousGetHandle().ToInt32();

        var all = new Dictionary<string, string>(options)
        {
            ["vid"] = "no",
            ["audio-display"] = "no",
            ["ao"] = "pcm",
            ["ao-pcm-file"] = $"/proc/self/fd/{writeEnd.ToString(CultureInfo.InvariantCulture)}",
            ["ao-pcm-waveheader"] = "no",
            ["audio-format"] = "s16",
            ["audio-channels"] = "stereo",
            ["audio-samplerate"] = Rate.ToString(CultureInfo.InvariantCulture),
            ["config"] = "no",
            ["terminal"] = "no",
            ["input-default-bindings"] = "no",
            ["load-scripts"] = "no",
            ["ytdl"] = "no",
            ["hr-seek"] = "yes",
            ["cache"] = "yes",
            ["demuxer-max-bytes"] = "16MiB",
        };

        _mpv = new MpvPlayer(all);
        _reader = new Thread(Read) { IsBackground = true, Name = "Tuxflix shadow decode" };
        _reader.Start();
        _mpv.Load(url, Start);
    }

    /// <summary>The time of the first decoded sample, seconds.</summary>
    public double Start { get; }

    /// <summary>How far decoding has got, seconds.</summary>
    public double DecodedUntil => Start + (Interlocked.Read(ref _written) / (double)Rate);

    /// <summary>Where the listener is, so the decoder knows how far ahead it may run. Any thread.</summary>
    public void Follow(double position)
    {
        Interlocked.Exchange(ref _wanted, (long)((position - Start) * Rate));
        _wake.Set();
    }

    /// <summary>
    /// Copies the window of <paramref name="left"/>.Length samples ending at <paramref name="end"/>
    /// seconds, as -1..1 floats; false when that moment is not decoded (yet, or any more).
    /// </summary>
    public bool TryRead(double end, Span<float> left, Span<float> right)
    {
        var count = left.Length;
        var last = (long)Math.Round((end - Start) * Rate);
        var first = last - count;
        var written = Interlocked.Read(ref _written);
        if (first < 0 || last > written || first < written - _capacity) return false;

        for (var i = 0; i < count; i++)
        {
            var slot = (int)((first + i) % _capacity) * 2;
            left[i] = _ring[slot] / 32768f;
            right[i] = _ring[slot + 1] / 32768f;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Keep reading so mpv can finish a blocked write, destroy it, then close our end of the
        // pipe so the reader meets its end.
        _draining = true;
        _wake.Set();
        _mpv.Dispose();
        _pipe.DisposeLocalCopyOfClientHandle();
        _reader.Join(TimeSpan.FromSeconds(2));
        _pipe.Dispose();
        _wake.Dispose();
    }

    private void Read()
    {
        var bytes = new byte[16384];
        var carry = 0;
        try
        {
            while (true)
            {
                // Far enough ahead of the listener: hold, and mpv holds with us.
                while (!_draining && Interlocked.Read(ref _written) - Interlocked.Read(ref _wanted) > (long)(AheadSeconds * Rate))
                {
                    _wake.Wait(50);
                    _wake.Reset();
                }

                var read = _pipe.Read(bytes, carry, bytes.Length - carry);
                if (read <= 0) return;
                if (_draining) continue;

                var available = carry + read;
                var frames = available / 4;
                var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, frames * 4));
                var written = Interlocked.Read(ref _written);
                for (var f = 0; f < frames; f++)
                {
                    var slot = (int)((written + f) % _capacity) * 2;
                    _ring[slot] = samples[f * 2];
                    _ring[slot + 1] = samples[(f * 2) + 1];
                }

                Interlocked.Add(ref _written, frames);

                // A read that ended mid-frame keeps its tail for the next one.
                carry = available - (frames * 4);
                if (carry > 0) Buffer.BlockCopy(bytes, frames * 4, bytes, 0, carry);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!_disposed) Log.Debug($"Shadow decode ended: {ex.Message}");
        }
    }
}
