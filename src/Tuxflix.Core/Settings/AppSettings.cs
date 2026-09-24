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
