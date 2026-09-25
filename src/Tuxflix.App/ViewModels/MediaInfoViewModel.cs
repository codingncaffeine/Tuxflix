using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// What a film or an episode is made of, version by version: the file, its size and length, the
/// picture, every soundtrack and every subtitle, the ones it plays with marked. Plex's "Get info".
/// </summary>
/// <remarks>A tile's record has no streams: the item's full record is read on a worker as the panel opens.</remarks>
public sealed partial class MediaInfoViewModel(ServerSession session, MetadataItem item) : ObservableObject
{
    public string Heading => item.Type == "episode" && Format.EpisodeCode(item) is { Length: > 0 } code
        ? $"{item.GrandparentTitle} · {code} · {item.Title}"
        : item.Year is { } year ? $"{item.Title} ({year.ToString(CultureInfo.InvariantCulture)})" : item.Title;

    public ObservableCollection<MediaVersionViewModel> Versions { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>Why nothing shows: the server did not answer, or the item has no file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    public async Task LoadAsync()
    {
        try
        {
            var full = await Task.Run(() => session.Client.GetItemDetailsAsync(item.RatingKey, CancellationToken.None)) ?? item;
            var media = full.Media ?? [];
            for (var i = 0; i < media.Count; i++) Versions.Add(new MediaVersionViewModel(media[i], i + 1, media.Count));
            if (Versions.Count == 0) Problem = "The server lists no file for this.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlexUnauthorizedException or JsonException)
        {
            Log.Warn("An item's media could not be read.", ex);
            Problem = "The server did not answer.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>One version of an item (a 4K file beside a 1080p one), as lines of a label and a value.</summary>
public sealed class MediaVersionViewModel
{
    public MediaVersionViewModel(Media media, int number, int count)
    {
        ArgumentNullException.ThrowIfNull(media);
        Label = count > 1 ? string.Create(CultureInfo.InvariantCulture, $"VERSION {number} OF {count}") : "FILE";
        Summary = Format.MediaSummary(media);
        var parts = media.Part ?? [];
        var streams = parts.SelectMany(p => p.Stream ?? []).ToList();
        var lines = new List<InfoLine>();
        foreach (var part in parts.Where(p => p.File is not null)) lines.Add(new(parts.Count > 1 ? "Part" : "File", FileName(part.File!)));
        if (parts.Sum(p => p.Size ?? 0) is > 0 and var size) lines.Add(new("Size", DownloadRowViewModel.Bytes(size)));
        if (Format.Runtime(media.Duration) is { Length: > 0 } length) lines.Add(new("Length", length));
        if (media.Container is { Length: > 0 } container) lines.Add(new("Container", container.ToUpperInvariant()));
        if (media.Bitrate is > 0 and var bitrate) lines.Add(new("Bitrate", Bitrate(bitrate)));
        foreach (var video in streams.Where(s => s.StreamType == 1))
        {
            var rate = media.VideoFrameRate is { Length: > 0 } frames ? $" · {frames}" : string.Empty;
            lines.Add(new("Video", Title(video) + rate));
        }

        lines.AddRange(Tracks("Audio", streams.Where(s => s.StreamType == 2).ToList()));
        lines.AddRange(Tracks("Subtitles", streams.Where(s => s.StreamType == 3).ToList()));
        Lines = lines;
    }

    /// <summary>"VERSION 2 OF 2", or "FILE" when there is one.</summary>
    public string Label { get; }

    /// <summary>"4K · HEVC · TrueHD 7.1".</summary>
    public string Summary { get; }

    public bool HasSummary => Summary.Length > 0;

    public IReadOnlyList<InfoLine> Lines { get; }

    /// <summary>A soundtrack or subtitle per line, the kind named on the first; the one it plays with marked.</summary>
    private static IEnumerable<InfoLine> Tracks(string kind, IReadOnlyList<MediaStream> streams)
    {
        if (streams.Count == 0)
        {
            if (kind == "Subtitles") yield return new(kind, "None");
            yield break;
        }

        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            var marks = new List<string>();
            if (stream.Selected) marks.Add("plays with");
            if (stream.IsExternal) marks.Add("separate file");
            var text = marks.Count > 0 ? $"{Title(stream)}  ·  {string.Join(", ", marks)}" : Title(stream);
            yield return new(i == 0 ? kind : string.Empty, text);
        }
    }

    private static string Title(MediaStream stream) =>
        stream.ExtendedDisplayTitle ?? stream.DisplayTitle ?? stream.Title ?? stream.Codec?.ToUpperInvariant() ?? "Unknown";

    /// <summary>The file's own name: a server may keep its files under Windows paths or Unix ones.</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>"66.9 Mbps", "640 kbps".</summary>
    private static string Bitrate(int kilobits) => kilobits >= 1000
        ? (kilobits / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps"
        : kilobits.ToString(CultureInfo.InvariantCulture) + " kbps";
}

/// <summary>A line of the panel: what it is, and its value.</summary>
public sealed record InfoLine(string Label, string Text);
