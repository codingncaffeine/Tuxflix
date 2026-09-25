using System.Globalization;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

/// <summary>
/// The demo library's notification socket: the viewer's changes come back as settled timeline
/// entries, and the playback on the viewer's other device reports where it is every few seconds,
/// as a server does. Everything is raised off the UI thread: on a timer's thread, or on the
/// thread that answered the change.
/// </summary>
public sealed class DemoNotificationSource(DemoCatalog catalog, TimeSpan? interval = null) : INotificationSource
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(5);
    private Timer? _timer;
    private bool _started;
    private bool _disposed;

    public event Action<NotificationContainer>? Received;

    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        catalog.Changed += OnChanged;
        ConnectionChanged?.Invoke(true);
        if (_interval > TimeSpan.Zero) _timer = new Timer(_ => Tick(), null, _interval, _interval);
    }

    /// <summary>Reports the other device's playback as it is now.</summary>
    public void Tick()
    {
        if (!_disposed) Received?.Invoke(new NotificationContainer { Type = "playing", Size = 1, Playing = [catalog.ViewerPlayback()] });
    }

    /// <summary>
    /// A library scan from start to end, as a server reports one: the activity starting, moving
    /// through <paramref name="steps"/> updates and ending, then the library's settled timeline.
    /// </summary>
    public async Task ScanAsync(string sectionKey, string title, int steps, TimeSpan pause, CancellationToken cancellation = default)
    {
        var uuid = "demo-scan-" + sectionKey;
        var section = long.Parse(sectionKey, CultureInfo.InvariantCulture);
        for (var step = 0; step <= steps + 1; step++)
        {
            var ended = step == steps + 1;
            var activity = new ServerActivity
            {
                Uuid = uuid,
                Type = "library.update.section",
                Title = $"Scanning {title}",
                Subtitle = ended ? null : $"Folder {step + 1} of {steps + 1}",
                Progress = Math.Min(100, step * 100 / Math.Max(1, steps)),
                Context = new ActivityContext { LibrarySectionId = section },
            };
            Received?.Invoke(new NotificationContainer
            {
                Type = "activity",
                Size = 1,
                Activities = [new ActivityNotification { Event = step == 0 ? "started" : ended ? "ended" : "updated", Uuid = uuid, Activity = activity }],
            });
            if (!ended && pause > TimeSpan.Zero) await Task.Delay(pause, cancellation).ConfigureAwait(false);
        }

        Received?.Invoke(new NotificationContainer
        {
            Type = "timeline",
            Size = 1,
            Timeline = [new TimelineEntry { Identifier = TimelineEntry.LibraryIdentifier, SectionId = section, ItemId = section, Type = 1, State = TimelineEntry.Settled, MetadataState = "created" }],
        });
    }

    private void OnChanged(IReadOnlyList<DemoChange> changes)
    {
        if (_disposed || changes.Count == 0) return;
        Received?.Invoke(new NotificationContainer
        {
            Type = "timeline",
            Size = changes.Count,
            Timeline =
            [
                .. changes.Select(c => new TimelineEntry
                {
                    Identifier = TimelineEntry.LibraryIdentifier,
                    SectionId = c.SectionId,
                    ItemId = long.TryParse(c.RatingKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null,
                    State = TimelineEntry.Settled,
                    UpdatedAt = catalog.Clock().ToUnixTimeSeconds(),
                }),
            ],
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        catalog.Changed -= OnChanged;
        if (_started) ConnectionChanged?.Invoke(false);
    }
}
