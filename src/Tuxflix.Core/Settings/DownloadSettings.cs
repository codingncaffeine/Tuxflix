namespace Tuxflix.Core.Settings;

/// <summary>How films and episodes are downloaded for watching without the server.</summary>
public sealed class DownloadSettings
{
    /// <summary>Where new downloads go; none means the data folder's <c>downloads</c>. What is kept stays where it was put.</summary>
    public string? Folder { get; set; }

    /// <summary>How many download at once: 1 or 2. One at a time finishes the first soonest.</summary>
    public int Simultaneous { get; set; } = 1;

    /// <summary>The most all downloads together may take, in bytes a second; 0 for no limit.</summary>
    public long SpeedLimit { get; set; }
}
