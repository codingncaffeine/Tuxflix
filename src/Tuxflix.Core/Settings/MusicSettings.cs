namespace Tuxflix.Core.Settings;

/// <summary>How the music pages were left: the visualizer's look and which panel shows beside the cover.</summary>
public sealed class MusicSettings
{
    /// <summary>The full-window visualizer's mode, by name (Spectrum, Mirror, Radial, Scope, Particles).</summary>
    public string VisualizerMode { get; set; } = "Spectrum";

    /// <summary>Its colours, by palette name; Album takes them from the cover playing.</summary>
    public string VisualizerPalette { get; set; } = "Album";

    /// <summary>The lyrics in place of the queue beside the cover, when the track has them.</summary>
    public bool ShowLyrics { get; set; }
}
