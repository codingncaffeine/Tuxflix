using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>Test folders go only after the settings written into them have landed.</summary>
public sealed class TestFolderTests
{
    [Fact]
    public void ATestThatKeepsSettingsDeletesItsFolderOnlyThroughTestFolder()
    {
        var folder = Path.Combine(Root(AppContext.BaseDirectory), "tests", "Tuxflix.HeadlessTests");
        // The helper deletes, and this file names both calls in its search.
        var files = Directory.GetFiles(folder, "*.cs").Where(f => Path.GetFileName(f) is not ("TestFolder.cs" or "TestFolderTests.cs")).ToList();
        Assert.Contains(files, f => File.ReadAllText(f).Contains("TestFolder.Delete(", StringComparison.Ordinal));
        var racing = files
            .Where(f => File.ReadAllText(f) is var text && text.Contains("SettingsStore.Load", StringComparison.Ordinal) && text.Contains("Directory.Delete(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(racing.Count == 0, "Deleting a folder its settings may still be writing to: " + string.Join(", ", racing));
    }

    private static string Root(string from)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tuxflix.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException($"No Tuxflix.slnx above {from}.");
    }
}
