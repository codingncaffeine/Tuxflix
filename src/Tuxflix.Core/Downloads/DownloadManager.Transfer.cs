using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.Core.Downloads;

/// <summary>
/// One transfer: the item's full record, metadata and artwork kept beside the file, then the file
/// itself, resumed with a range where bytes are already on disk.
/// </summary>
/// <remarks>
/// A cut connection, a server error or a minute without a byte is tried again from where the file
/// stops, a few times, waiting longer each time; an attempt that brought bytes starts the count
/// again. A refusal (the owner withholds downloads: 403), a sign-in the server no longer takes, a
/// file the server no longer has and a full disk stop the download with that reason in words. The
/// bytes kept are joined to new ones only if the server still describes the same file (the same
/// length and ETag, and a range answer that starts where the file stops); otherwise it starts over.
/// </remarks>
public sealed partial class DownloadManager
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>Room left on the drive beyond the file, so a download never fills it to the last byte.</summary>
    private const long Headroom = 64L * 1024 * 1024;

    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    private async Task RunAsync(Transfer transfer, PlexServerClient client)
    {
        var state = DownloadState.Done;
        string? error = null;
        var retryable = false;
        try
        {
            await TransferAsync(transfer, client).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (transfer.Token.IsCancellationRequested)
        {
            // Paused, removed, closed or shut down: what it becomes depends on which, below.
            state = DownloadState.Queued;
        }
        catch (ObjectDisposedException)
        {
            // Its server was closed under it: that connection is done with, and the download waits for the next.
            lock (_gate)
            {
                if (_servers.TryGetValue(transfer.Record.ServerId, out var open) && ReferenceEquals(open, client)) _servers.Remove(transfer.Record.ServerId);
            }

            state = DownloadState.Queued;
        }
        catch (DownloadFailedException ex)
        {
            (state, error, retryable) = (DownloadState.Failed, ex.Message, ex.Retryable);
        }
        catch (PlexUnauthorizedException)
        {
            (state, error) = (DownloadState.Failed, $"{transfer.Record.ServerName} no longer accepts this sign-in.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (state, error, retryable) = (DownloadState.Failed, DownloadFolders.Explain(transfer.Record.Folder, ex, Environment.GetEnvironmentVariable), true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Downloading {Describe(transfer.Record)} failed unexpectedly.", ex);
            (state, error, retryable) = (DownloadState.Failed, "The download stopped unexpectedly. The log has the details.", true);
        }

        Finish(transfer, state, error, retryable);
    }

    private void Finish(Transfer transfer, DownloadState state, string? error, bool retryable)
    {
        var record = transfer.Record;
        DownloadRecord? copy = null;
        bool removed;
        lock (_gate)
        {
            _active.Remove(record.Id);
            removed = transfer.Reason == StopReason.Removed;
            if (removed) _deleting.Add(record.Folder);
            if (!removed)
            {
                if (state == DownloadState.Done)
                {
                    record.State = DownloadState.Done;
                    record.Error = null;
                    record.DoneBytes = record.TotalBytes;
                    record.CompletedAt = Time.GetUtcNow().ToUnixTimeSeconds();
                }
                else if (transfer.Reason == StopReason.Paused)
                {
                    record.State = DownloadState.Paused;
                }
                else if (state == DownloadState.Failed && transfer.Reason == StopReason.None)
                {
                    record.State = DownloadState.Failed;
                    record.Error = error;
                    record.Retryable = retryable;
                }
                else
                {
                    // Its server closed, or Tuxflix is closing: it carries on when it can.
                    record.State = DownloadState.Queued;
                }

                copy = Snap(record);
            }
        }

        transfer.Dispose();
        if (removed)
        {
            DeleteFiles(record);
            lock (_gate) _deleting.Remove(record.Folder);
        }
        else
        {
            if (copy!.State == DownloadState.Done) Log.Info($"Downloaded {Describe(copy)}: {Bytes(copy.TotalBytes)}, checked against the server's size.");
            else if (copy.State == DownloadState.Failed) Log.Warn($"Download of {Describe(copy)} stopped: {copy.Error}");
            Changed?.Invoke(copy);
        }

        SaveSoon();
        Pump();
    }

    private async Task TransferAsync(Transfer transfer, PlexServerClient client)
    {
        var record = transfer.Record;
        var token = transfer.Token;
        var failures = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var before = PartialLength(record);
            try
            {
                await PrepareAsync(record, client, token).ConfigureAwait(false);
                await AttemptAsync(transfer, client, token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && IsTransient(ex))
            {
                failures = PartialLength(record) > before ? 1 : failures + 1;
                if (failures >= MaxFailures)
                {
                    throw new DownloadFailedException($"{record.ServerName} stopped answering. Resume to try again.", retryable: true);
                }

                var wait = RetryDelay * Math.Pow(2, failures - 1);
                Log.Info($"Download of {Describe(record)} interrupted ({ex.Message}); trying again in {wait.TotalSeconds:0.##} s from byte {PartialLength(record)}.");
                await Task.Delay(wait, Time, token).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        TransientException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,

        // The client's own time limit for an answer, not a stop that was asked for.
        TaskCanceledException => true,
        _ => false,
    };

    /// <summary>The item's full record, its metadata file and its artwork, unless they are already here.</summary>
    private async Task PrepareAsync(DownloadRecord record, PlexServerClient client, CancellationToken token)
    {
        Directory.CreateDirectory(record.Folder);
        string partKey;
        lock (_gate) partKey = record.PartKey;
        if (partKey.Length > 0 && File.Exists(record.MetadataPath)) return;

        MetadataItem? full;
        try
        {
            full = await client.GetMetadataAsync(record.RatingKey, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            full = null;
        }
        catch (JsonException ex)
        {
            throw new DownloadFailedException("The server's description of this item could not be read: " + ex.Message, retryable: true);
        }

        if (full is null) throw new DownloadFailedException($"{record.ServerName} no longer has this item.", retryable: false);
        var part = full.Media?.FirstOrDefault()?.Part?.FirstOrDefault();
        if (part?.Key is null) throw new DownloadFailedException($"{record.ServerName} lists no file for this item.", retryable: false);

        lock (_gate)
        {
            record.PartKey = part.Key;
            if (!record.TotalConfirmed && part.Size is > 0) record.TotalBytes = part.Size.Value;
            if (record.ViewOffset == 0 && !record.Watched) record.ViewOffset = full.ViewOffset ?? 0;
            record.Duration = full.Duration ?? record.Duration;
        }

        // As the server would answer for the item, so the kept copy reads like the server's own.
        var envelope = new PlexEnvelope { MediaContainer = new MediaContainer { Size = 1, Metadata = [full] } };
        WriteAtomically(record.MetadataPath, JsonSerializer.SerializeToUtf8Bytes(envelope, PlexJsonContext.Default.PlexEnvelope));
        await FetchArtworkAsync(record, full, client, token).ConfigureAwait(false);
    }

    /// <summary>The poster (a series' own for an episode), the backdrop and an episode's still, scaled by the server.</summary>
    private static async Task FetchArtworkAsync(DownloadRecord record, MetadataItem item, PlexServerClient client, CancellationToken token)
    {
        var episode = item.Type == "episode";
        var wanted = new List<(string Name, string? Path, int Width, int Height)>
        {
            ("poster.jpg", episode ? item.GrandparentThumb ?? item.ParentThumb : item.Thumb, 480, 720),
            ("backdrop.jpg", item.Art ?? item.GrandparentArt, 1600, 900),
        };
        if (episode) wanted.Add(("still.jpg", item.Thumb, 640, 360));

        foreach (var (name, path, width, height) in wanted)
        {
            if (string.IsNullOrEmpty(path) || File.Exists(record.ArtworkPath(name))) continue;
            try
            {
                var bytes = await client.GetBytesAsync(client.ImageUri(path, width, height), token).ConfigureAwait(false);
                WriteAtomically(record.ArtworkPath(name), bytes);
            }
            catch (HttpRequestException ex)
            {
                Log.Debug($"Artwork {name} for {Describe(record)} was not kept: {ex.Message}");
            }
        }
    }

    /// <summary>One request for the rest of the file, written as it arrives; returns once the whole file is kept.</summary>
    private async Task AttemptAsync(Transfer transfer, PlexServerClient client, CancellationToken token)
    {
        var record = transfer.Record;
        var have = PartialLength(record);
        string partKey;
        string? validator;
        long knownTotal;
        bool confirmed;
        lock (_gate) (partKey, validator, knownTotal, confirmed) = (record.PartKey, record.Validator, record.TotalBytes, record.TotalConfirmed);

        using var response = await client.RequestPartAsync(partKey, have, validator, token).ConfigureAwait(false);
        var status = response.StatusCode;
        switch (status)
        {
            case HttpStatusCode.Forbidden:
                throw new DownloadFailedException($"The owner of {record.ServerName} does not allow this account to download.", retryable: false);
            case HttpStatusCode.Unauthorized:
                throw new DownloadFailedException($"{record.ServerName} no longer accepts this sign-in.", retryable: false);
            case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                throw new DownloadFailedException($"{record.ServerName} no longer has this file.", retryable: false);
            case HttpStatusCode.RequestedRangeNotSatisfiable when confirmed && have == knownTotal:
                // Every byte was already here: the last attempt ended just before keeping it.
                Keep(record, knownTotal);
                return;
            case HttpStatusCode.RequestedRangeNotSatisfiable:
                Restart(record, "the server's file is shorter than the part kept");
                throw new TransientException("the server's file is shorter than the part kept");
            case >= HttpStatusCode.InternalServerError:
                throw new HttpRequestException($"the server answered {(int)status}", null, status);
            case not (HttpStatusCode.OK or HttpStatusCode.PartialContent):
                throw new DownloadFailedException($"{record.ServerName} refused the download ({(int)status} {response.ReasonPhrase}).", retryable: false);
        }

        long start;
        long? total;
        var answered = response.Headers.ETag?.ToString() ?? response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
        if (status == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange is not { From: { } from, Length: { } length }) throw new TransientException("the server's range answer names no range");
            if (from != have)
            {
                Restart(record, "the server answered from another place in the file");
                throw new TransientException("the server answered from another place in the file");
            }

            (start, total) = (from, length);
        }
        else
        {
            // The whole file: a server that ignores ranges, or one that saw the file change (If-Range).
            if (have > 0) Log.Info($"{record.ServerName} sent all of {Describe(record)} again; starting it over.");
            (start, total) = (0, response.Content.Headers.ContentLength);
        }

        if (start > 0 && ((confirmed && total != knownTotal) || (validator is not null && answered is not null && validator != answered)))
        {
            Restart(record, "the file changed on the server");
            throw new TransientException("the file changed on the server");
        }

        if (total is { } size && FreeSpace(record.Folder) is { } free && free < size - start + Headroom)
        {
            throw new DownloadFailedException($"Not enough free space: it needs {Bytes(size - start)} and the drive has {Bytes(free)}.", retryable: true);
        }

        lock (_gate)
        {
            if (total is { } declared) record.TotalBytes = declared;
            record.TotalConfirmed = total is not null;
            record.Validator = answered ?? record.Validator;
            record.DoneBytes = start;
        }

        var position = await CopyAsync(transfer, response, start, token).ConfigureAwait(false);
        if (total is { } expected && position != expected)
        {
            if (position > expected) Restart(record, "more bytes arrived than the server said");
            throw new TransientException($"the transfer ended at byte {position} of {expected}");
        }

        Keep(record, position);
    }

    /// <summary>Writes the answer's body into the partial file from <paramref name="start"/>, through the speed limit; returns where the file ends.</summary>
    private async Task<long> CopyAsync(Transfer transfer, HttpResponseMessage response, long start, CancellationToken token)
    {
        var record = transfer.Record;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var file = new FileStream(record.PartialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, ChunkSize, FileOptions.Asynchronous);
                await using (file.ConfigureAwait(false))
                {
                    file.SetLength(start);
                    file.Seek(start, SeekOrigin.Begin);
                    var position = start;
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                    while (true)
                    {
                        stall.CancelAfter(Stall);
                        int read;
                        try
                        {
                            read = await body.ReadAsync(buffer.AsMemory(0, ChunkSize), stall.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new TransientException("no bytes arrived for a minute");
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException)
                        {
                            throw new TransientException("the connection was cut: " + ex.Message, ex);
                        }

                        if (read == 0) break;
                        await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        position += read;
                        lock (_gate) record.DoneBytes = position;
                        Tell(transfer);
                        await _limiter.TakeAsync(read, token).ConfigureAwait(false);
                    }

                    await file.FlushAsync(token).ConfigureAwait(false);
                    return position;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The whole file is here and its size is the server's: it takes its own name.</summary>
    private void Keep(DownloadRecord record, long expected)
    {
        var length = PartialLength(record);
        if (length != expected) throw new TransientException($"the file on disk is {length} bytes, not {expected}");
        File.Move(record.PartialPath, record.MediaPath, overwrite: true);
        lock (_gate)
        {
            record.TotalBytes = length;
            record.DoneBytes = length;
        }
    }

    /// <summary>The bytes kept cannot be joined to the server's: they go, and the next attempt starts at the beginning.</summary>
    private void Restart(DownloadRecord record, string why)
    {
        Log.Info($"Download of {Describe(record)} starts over: {why}.");
        if (File.Exists(record.PartialPath)) File.Delete(record.PartialPath);
        lock (_gate)
        {
            record.DoneBytes = 0;
            record.TotalConfirmed = false;
            record.Validator = null;
        }
    }

    /// <summary>Progress, told at most four times a second; the index is saved every few seconds on the way.</summary>
    private void Tell(Transfer transfer)
    {
        var now = Environment.TickCount64;
        if (now - transfer.LastTold < 250) return;
        transfer.LastTold = now;
        Raise(transfer.Record);
        if (now - transfer.LastSaved < 5000) return;
        transfer.LastSaved = now;
        SaveSoon();
    }

    private static long PartialLength(DownloadRecord record) =>
        File.Exists(record.PartialPath) ? new FileInfo(record.PartialPath).Length : 0;

    /// <summary>"812 MB", "4.2 GB".</summary>
    internal static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };

    /// <summary>A failure worth another attempt: a cut, a stall, a server error.</summary>
    private sealed class TransientException(string message, Exception? inner = null) : Exception(message, inner);
}
