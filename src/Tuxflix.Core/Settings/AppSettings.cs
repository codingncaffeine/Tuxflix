using System.Text.Json.Serialization;

namespace Tuxflix.Core.Settings;

/// <summary>
/// Everything Tuxflix remembers between runs. Every property here has a reader; one that
/// nothing reads is a promise the interface cannot keep, so none is added ahead of its use.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// This installation's identity towards Plex, sent as <c>X-Plex-Client-Identifier</c>.
    /// Generated once and never changed, because Plex lists devices by it.
    /// </summary>
    public string ClientIdentifier { get; set; } = string.Empty;

    /// <summary>Where the main window was, restored at the next start.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>Use the desktop's own title bar instead of the one Tuxflix draws.</summary>
    public bool UseSystemTitleBar { get; set; }

    /// <summary>The server opened last, by its machine identifier; the next start reconnects to it.</summary>
    public string? LastServerId { get; set; }

    /// <summary>Width of the library rail in device-independent pixels.</summary>
    public double RailWidth { get; set; } = 288;

    /// <summary>The compact classic player: its skin, its size and which of its windows show.</summary>
    public ClassicPlayerSettings Classic { get; set; } = new();

    /// <summary>The music equalizer, kept between runs.</summary>
    public EqualizerSettings Equalizer { get; set; } = new();

    /// <summary>How films and episodes play: skipping, what comes next, subtitles, sound.</summary>
    public PlaybackSettings Playback { get; set; } = new();

    /// <summary>The music pages: the visualizer and the lyrics.</summary>
    public MusicSettings Music { get; set; } = new();

    /// <summary>Photos: the slideshow.</summary>
    public PhotoSettings Photos { get; set; } = new();
}

public sealed class PlaybackSettings
{
    /// <summary>Jump past an intro the server found, without a press.</summary>
    public bool AutoSkipIntro { get; set; }

    /// <summary>Jump past credits the server found; a film's final credits end it, an episode's bring the next one.</summary>
    public bool AutoSkipCredits { get; set; }

    /// <summary>The next episode starts after a short countdown.</summary>
    public bool AutoPlayNext { get; set; } = true;

    /// <summary>Subtitle size, 1 as the file draws them.</summary>
    public double SubtitleScale { get; set; } = 1;

    /// <summary>Subtitles higher up the picture, clear of a letterbox or a burned-in line.</summary>
    public bool SubtitlesRaised { get; set; }

    /// <summary>Quiet dialogue up and loud moments down: for late at night.</summary>
    public bool NightMode { get; set; }

    /// <summary>Surround sound sent as it is (Dolby, DTS) for an AV receiver to decode; no volume or night mode then.</summary>
    public bool Passthrough { get; set; }

    /// <summary>Surround mixed down to two speakers, for headphones and TVs that mishandle more.</summary>
    public bool Stereo { get; set; }
}

public sealed class ClassicPlayerSettings
{
    /// <summary>The <c>.wsz</c> file in use; none means the classic base skin.</summary>
    public string? Skin { get; set; }

    /// <summary>Screen pixels per skin pixel, 1 to 2: 2 ("double size") suits today's screens.</summary>
    public double Scale { get; set; } = 2;

    public bool ShowEqualizer { get; set; } = true;

    public bool ShowPlaylist { get; set; } = true;

    /// <summary>"Spectrum", "Scope" or "Off", as the small analyser was left.</summary>
    public string Visualizer { get; set; } = "Spectrum";

    /// <summary>The time counts down what is left rather than up.</summary>
    public bool ShowRemaining { get; set; }

    /// <summary>The window stays above the others.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>The main window is rolled up to its title bar ("windowshade").</summary>
    public bool Shaded { get; set; }
}

public sealed class EqualizerSettings
{
    public bool On { get; set; }

    public double Preamp { get; set; }

    public double[] Bands { get; set; } = new double[10];
}

public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
