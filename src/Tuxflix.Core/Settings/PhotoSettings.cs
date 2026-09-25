namespace Tuxflix.Core.Settings;

/// <summary>How photos are shown: the slideshow's pace and order.</summary>
public sealed class PhotoSettings
{
    /// <summary>Seconds each photo stays in a slideshow.</summary>
    public int SlideshowSeconds { get; set; } = 5;

    /// <summary>A slideshow starts in a random order.</summary>
    public bool SlideshowShuffle { get; set; }
}
