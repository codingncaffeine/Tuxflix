using System.Collections.Concurrent;
using Tuxflix.App.Tv;

namespace Tuxflix.HeadlessTests;

/// <summary>A controller the test presses: events pushed from the test come out on the input thread.</summary>
internal sealed class FakePadSource : IPadSource
{
    private readonly BlockingCollection<PadEvent> _events = [];

    /// <summary>When each pushed event was pushed, by <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public ConcurrentQueue<long> Pushed { get; } = new();

    public bool Opened { get; private set; }

    public bool Open(out string? unavailable)
    {
        Opened = true;
        unavailable = null;
        return true;
    }

    public bool Next(TimeSpan timeout, out PadEvent padEvent) => _events.TryTake(out padEvent, timeout);

    public void Push(PadEvent padEvent)
    {
        Pushed.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
        _events.Add(padEvent);
    }

    public void Dispose() => _events.Dispose();
}
