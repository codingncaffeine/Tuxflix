using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// What the seek bar shows over the place under the pointer: the time, the chapter's name, and the
/// server's preview picture when it made them.
/// </summary>
/// <remarks>
/// The previews (one BIF file, a few megabytes for a film) are fetched the first time the bar is
/// hovered, not with the film. Pictures are decoded on a worker at the width they are shown; the
/// latest hover wins, and the most recent few are kept. Chapters only name themselves when the
/// file names them: "Chapter 3" says nothing the time does not.
/// </remarks>
public sealed partial class SeekPreviews(PlexServerClient client, MediaPart? part, IReadOnlyList<Chapter> chapters) : ObservableObject, IDisposable
{
    public const int PictureWidth = 240;
    private const int Kept = 48;

    private readonly Dictionary<int, Bitmap> _decoded = [];
    private readonly LinkedList<int> _recent = [];
    private Task<PreviewIndex?>? _index;
    private int _wanted = -1;
    private long _pendingMs;
    private bool _disposed;

    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    [ObservableProperty]
    public partial string Time { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? ChapterTitle { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Picture { get; private set; }

    /// <summary>Whether the server made pictures for this part.</summary>
    public bool HasPictures => part is { Indexes: "sd", Id: > 0 };

    /// <summary>Shows what is at <paramref name="seconds"/>. Called on the UI thread.</summary>
    public void Show(double seconds)
    {
        if (_disposed) return;
        var ms = (long)(Math.Max(0, seconds) * 1000);
        IsShown = true;
        Time = Format.Clock(Math.Max(0, seconds));
        ChapterTitle = chapters.LastOrDefault(c => c.StartTimeOffset <= ms) is { Tag.Length: > 0 } chapter ? chapter.Tag : null;
        if (!HasPictures) return;

        _index ??= LoadAsync();
        if (_index.IsCompletedSuccessfully)
        {
            if (_index.Result is { } index) Want(index, index.IndexAt(ms));
        }
        else
        {
            _pendingMs = ms;
            _ = ShowWhenLoadedAsync();
        }
    }

    public void Hide() => IsShown = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Picture = null;
        foreach (var bitmap in _decoded.Values) bitmap.Dispose();
        _decoded.Clear();
    }

    private async Task<PreviewIndex?> LoadAsync()
    {
        var id = part!.Id;
        try
        {
            var index = await Task.Run(() => client.GetPreviewIndexAsync(id, CancellationToken.None));
            Log.Info(index is null ? "The server's seek previews could not be read." : $"Seek previews: {index.Count} pictures.");
            return index;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException)
        {
            Log.Info($"No seek previews ({ex.Message}).");
            return null;
        }
    }

    private async Task ShowWhenLoadedAsync()
    {
        var index = await _index!;
        if (index is not null && IsShown && !_disposed) Want(index, index.IndexAt(_pendingMs));
    }

    private void Want(PreviewIndex index, int picture)
    {
        if (picture == _wanted) return;
        _wanted = picture;
        if (_decoded.TryGetValue(picture, out var ready))
        {
            Picture = ready;
            Touch(picture);
            return;
        }

        _ = DecodeAsync(picture, index.Picture(picture));
    }

    private async Task DecodeAsync(int picture, ReadOnlyMemory<byte> jpeg)
    {
        Bitmap bitmap;
        try
        {
            bitmap = await Task.Run(() =>
            {
                var bytes = jpeg.ToArray();
                Imaging.ImageBounds.Check(bytes, Imaging.ImageBounds.SmallPixels);
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, PictureWidth);
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            Log.Info($"A seek preview could not be decoded ({ex.Message}).");
            return;
        }

        // Back on the UI thread.
        if (_disposed || _decoded.ContainsKey(picture))
        {
            bitmap.Dispose();
        }
        else
        {
            _decoded[picture] = bitmap;
            Touch(picture);
            Evict();
        }

        if (!_disposed && _wanted == picture && _decoded.TryGetValue(picture, out var shown)) Picture = shown;
    }

    private void Touch(int picture)
    {
        _recent.Remove(picture);
        _recent.AddFirst(picture);
    }

    private void Evict()
    {
        for (var node = _recent.Last; _recent.Count > Kept && node is not null;)
        {
            var previous = node.Previous;
            if (_decoded.TryGetValue(node.Value, out var bitmap) && !ReferenceEquals(bitmap, Picture))
            {
                _decoded.Remove(node.Value);
                _recent.Remove(node);
                bitmap.Dispose();
            }

            node = previous;
        }
    }
}
