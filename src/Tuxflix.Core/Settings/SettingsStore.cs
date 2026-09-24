using System.Text.Json;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// </summary>
/// <remarks>
/// A save captures the settings at once and a worker writes them: the caller, usually the UI
/// thread, never waits for the disk. Saves in quick succession collapse into the latest. The file
/// is written as a sibling and renamed over the old one, so a crash mid-write leaves the previous
/// settings intact rather than half a file. <see cref="Flush"/> waits for the last write, at exit.
/// A file that cannot be read is set aside as <c>settings.json.unreadable</c> and the defaults are
/// used; the log says so. Loading reads the disk and happens at start, before any window.
/// </remarks>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private string? _pending;
    private bool _writing;
    private Task _writer = Task.CompletedTask;

    private SettingsStore(string path, AppSettings current)
    {
        _path = path;
        Current = current;
    }

    public AppSettings Current { get; }

    /// <summary>For tests: runs on the writer after it has taken a snapshot and before it writes it.</summary>
    internal Action? BeforeWrite { get; set; }

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

    /// <summary>Captures the settings now and has a worker write them; returns at once.</summary>
    public void Save()
    {
        // Captured here, in microseconds, so later changes on the caller's thread never race the writer.
        var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);
        lock (_gate)
        {
            _pending = json;
            if (_writing) return;
            _writing = true;
            _writer = Task.Run(WritePending);
        }
    }

    /// <summary>Waits until every save so far is on disk, or <paramref name="limit"/> passes.</summary>
    public bool Flush(TimeSpan limit)
    {
        Task writer;
        lock (_gate) writer = _writer;
        return writer.Wait(limit);
    }

    private void WritePending()
    {
        while (true)
        {
            string json;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _writing = false;
                    return;
                }

                json = _pending;
                _pending = null;
            }

            BeforeWrite?.Invoke();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + ".new";
                File.WriteAllText(temporary, json);
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
