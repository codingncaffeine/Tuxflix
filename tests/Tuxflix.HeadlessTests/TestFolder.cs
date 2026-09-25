using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// A test's own temporary folder. Settings are written on a worker: a folder deleted while a write
/// lands is left behind ("Directory not empty"), or the write fails beneath it.
/// </summary>
internal static class TestFolder
{
    /// <summary>Waits for each settings store's last write, then deletes the folder.</summary>
    public static void Delete(string root, params IEnumerable<SettingsStore> settings)
    {
        foreach (var store in settings) Assert.True(store.Flush(TimeSpan.FromSeconds(10)), "The settings did not finish writing.");
        Directory.Delete(root, recursive: true);
    }
}
