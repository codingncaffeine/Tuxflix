using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Demo;

/// <summary>
/// Downloads and watch reports in the demo: every film and episode can be downloaded as a real,
/// playable few-second video with range support, one film is withheld the way a server's owner
/// withholds downloads (403), and timeline and scrobble reports are answered and noted.
/// </summary>
public sealed partial class DemoPlexHandler
{
    /// <summary>The film whose owner "does not allow downloads", so the refusal can be seen.</summary>
    public const string WithheldTitle = "Halcyon Drive";

    private static readonly DateTimeOffset FileDate = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly bool DownloadRoutes = Add((handler, segments, query, request) => segments switch
    {
        ["library", "parts", var id, _, _] when query["download"] == "1" => handler.Download(id, request),
        [":", "timeline"] => handler.Note($"timeline {query["ratingKey"]} {query["state"]} {query["time"]}"),
        _ => null,
    });

    private long _cutAfter = -1;
    private bool _cutQuietly;

    /// <summary>The part downloads asked for, in order: "7002 from 0", "7002 withheld".</summary>
    public ConcurrentQueue<string> DownloadRequests { get; } = new();

    /// <summary>The watch reports received, in order: "timeline 1001 stopped 60000", "scrobble 1001".</summary>
    public ConcurrentQueue<string> Writes { get; } = new();

    /// <summary>For tests: watch reports fail as if the server could not be reached.</summary>
    public bool RefuseWrites { get; set; }

    /// <summary>
    /// For tests: the next download answer breaks off after this many bytes, as a dropped connection
    /// does; <paramref name="quietly"/> ends it there as if it were complete, as a proxy that gives up might.
    /// </summary>
    public void CutNextDownloadAfter(long bytes, bool quietly = false)
    {
        _cutQuietly = quietly;
        Interlocked.Exchange(ref _cutAfter, bytes);
    }

    private HttpResponseMessage? Download(string id, HttpRequestMessage request)
    {
        var item = long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partId) && Catalog.FindByPart(partId) is { } film
            ? film
            : Catalog.Find(id);
        if (item is null) return null;
        if (item.Title == WithheldTitle)
        {
            DownloadRequests.Enqueue($"{id} withheld");
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }

        var video = DemoVideo.For(item.RatingKey);
        var total = video.Length;
        var tag = new EntityTagHeaderValue($"\"demo-{item.RatingKey}-{total}\"");
        var (from, to) = (0L, total - 1);
        var ranged = false;

        // If-Range naming another version of the file asks for the whole of this one.
        var sameFile = request.Headers.IfRange?.EntityTag is not { } asked || asked.Tag == tag.Tag;
        if (sameFile && request.Headers.Range?.Ranges.FirstOrDefault() is { } range)
        {
            (from, to) = range.From is { } start ? (start, Math.Min(range.To ?? total - 1, total - 1)) : (Math.Max(0, total - range.To!.Value), total - 1);
            if (from >= total)
            {
                DownloadRequests.Enqueue($"{id} beyond the end");
                var refused = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                refused.Content.Headers.ContentRange = new ContentRangeHeaderValue(total);
                return refused;
            }

            ranged = true;
        }

        DownloadRequests.Enqueue($"{id} from {from}");
        var length = to - from + 1;
        var content = new StreamContent(new PartStream(video, from, length, Interlocked.Exchange(ref _cutAfter, -1), _cutQuietly), 64 * 1024);
        content.Headers.ContentLength = length;
        content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
        content.Headers.LastModified = FileDate;
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = item.Title + ".mkv" };
        if (ranged) content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        var response = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
        response.Headers.ETag = tag;
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    /// <summary>Every episode of a series in order, or of a season.</summary>
    /// <summary>
    /// Notes a watch report, then gives the answer that acts on it: one route for a report that
    /// both the downloads' replay and the viewer's marks rely on, whatever order routes are tried in.
    /// </summary>
    private HttpResponseMessage NotedThen(string write, Func<HttpResponseMessage> answer)
    {
        Note(write);
        return answer();
    }

    private HttpResponseMessage Note(string write)
    {
        if (RefuseWrites) throw new HttpRequestException("The demo server is pretending to be out of reach.");
        Writes.Enqueue(write);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
    }

    /// <summary>A range of a demo video, made as it is read; it can break off early, as a dropped connection does.</summary>
    private sealed class PartStream(DemoVideo video, long from, long length, long cutAfter, bool quietly) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (cutAfter >= 0 && _read >= cutAfter)
            {
                if (quietly) return 0;
                throw new IOException("The demo server dropped the connection.");
            }

            var limit = cutAfter >= 0 ? Math.Min(length, cutAfter) : length;
            var count = (int)Math.Min(buffer.Length, limit - _read);
            if (count <= 0) return 0;
            video.Read(from + _read, buffer[..count]);
            _read += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
