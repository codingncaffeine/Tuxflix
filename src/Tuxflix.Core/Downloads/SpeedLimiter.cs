using System.Diagnostics;

namespace Tuxflix.Core.Downloads;

/// <summary>
/// One allowance of bytes a second shared by every transfer: a token bucket that may go into debt.
/// </summary>
/// <remarks>
/// A transfer takes what it just read and, if the bucket is short, waits for the debt to be paid
/// at the allowed rate; transfers running side by side therefore share the one limit. The bucket
/// holds a quarter of a second at most, so a pause does not bank a burst. The limit is read at
/// every take, so a change applies at once. A limit of zero takes nothing and never waits.
/// </remarks>
public sealed class SpeedLimiter(Func<long> bytesPerSecond)
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _tokens;
    private double _last;

    /// <summary>Accounts for <paramref name="bytes"/> just read, waiting as long as the limit asks.</summary>
    public Task TakeAsync(int bytes, CancellationToken cancellation)
    {
        var rate = bytesPerSecond();
        if (rate <= 0) return Task.CompletedTask;

        double wait;
        lock (_gate)
        {
            var now = _clock.Elapsed.TotalSeconds;
            var capacity = Math.Max(64 * 1024, rate / 4.0);
            _tokens = Math.Min(capacity, _tokens + ((now - _last) * rate));
            _last = now;
            _tokens -= bytes;
            wait = _tokens < 0 ? -_tokens / rate : 0;
        }

        return wait > 0 ? Task.Delay(TimeSpan.FromSeconds(wait), cancellation) : Task.CompletedTask;
    }
}
