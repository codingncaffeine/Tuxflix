using System.Text.Json;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// </summary>
/// <remarks>
/// A save writes a sibling file and renames it over the old one, so a crash mid-write leaves the
/// previous settings intact rather than half a file. A file that cannot be read is set aside as
/// <c>settings.json.unreadable</c> and the defaults are used; the log says so.
/// </remarks>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();

    private SettingsStore(string path, AppSettings current)
    {
        _path = path;
        Current = current;
    }

    public AppSettings Current { get; }

    public static SettingsStore Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var settings = Read(path) ?? new AppSettings();
        var store = new SettingsStore(path, settings);

        if (string.IsNullOrWhiteSpace(settings.ClientIdentifier))
        {
            settings.ClientIdentifier = Guid.NewGuid().ToString("N");
            store.Save();
        }

        return store;
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + ".new";
                File.WriteAllText(temporary, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Settings could not be saved to {_path}.", ex);
            }
        }
    }

    private static AppSettings? Read(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Settings at {path} could not be read; starting from the defaults.", ex);
            try
            {
                File.Move(path, path + ".unreadable", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                Log.Warn("The unreadable settings file could not be set aside.", moveError);
            }

            return null;
        }
    }
}
