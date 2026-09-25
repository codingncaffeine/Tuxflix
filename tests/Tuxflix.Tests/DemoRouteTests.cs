using System.Text.RegularExpressions;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>
/// The demo server's routes come from several files, tried in an order nobody chooses: a path two
/// of them answer is answered by whichever comes first, and the other file's answer never runs.
/// </summary>
public sealed partial class DemoRouteTests
{
    [Fact]
    public void NoTwoDemoRouteFilesAnswerTheSamePath()
    {
        var folder = Path.Combine(Up(AppContext.BaseDirectory, "Tuxflix.slnx"), "src", "Tuxflix.Core", "Demo");
        var arms = new List<(string Pattern, string Guard, string Where)>();
        var unread = new List<string>();
        foreach (var file in Directory.GetFiles(folder, "DemoPlexHandler*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (var n = 0; n < lines.Length; n++)
            {
                var where = $"{Path.GetFileName(file)}:{n + 1}";

                // An arm may carry its guard or its arrow on to the next line.
                var line = lines[n];
                while (line.TrimStart().StartsWith('[') && !line.Contains("=>", StringComparison.Ordinal) && n + 1 < lines.Length
                       && lines[n + 1].TrimStart() is var next && (next.StartsWith("=>", StringComparison.Ordinal) || next.StartsWith("when ", StringComparison.Ordinal)))
                {
                    line += " " + lines[++n].Trim();
                }

                if (Arm().Match(line) is { Success: true } arm)
                {
                    // An "or" pattern answers each of its alternatives.
                    foreach (var alternative in Or().Split(arm.Groups["pattern"].Value))
                    {
                        var pattern = Placeholder().Replace(alternative, "_").Replace(" ", string.Empty, StringComparison.Ordinal);
                        arms.Add((pattern, arm.Groups["guard"].Value.Trim(), where));
                    }
                }
                else if (line.TrimStart().StartsWith('[') && line.Contains("=>", StringComparison.Ordinal))
                {
                    // A route this test cannot read would go unchecked: it fails instead.
                    unread.Add(where);
                }
            }
        }

        Assert.NotEmpty(arms);
        Assert.True(unread.Count == 0, "Route lines this test cannot read: " + string.Join(", ", unread));

        // Across files, one answer shadows the other when it has no guard, or the same guard.
        var shadowed = new List<string>();
        foreach (var path in arms.GroupBy(a => a.Pattern).Where(g => g.Select(a => FileOf(a.Where)).Distinct().Count() > 1))
        {
            foreach (var arm in path)
            {
                var others = path.Where(o => FileOf(o.Where) != FileOf(arm.Where)).ToList();
                if (arm.Guard.Length == 0 || others.Any(o => o.Guard.Length == 0 || o.Guard == arm.Guard))
                {
                    shadowed.Add($"{arm.Pattern} {arm.Guard} at {arm.Where}".Replace("  ", " ", StringComparison.Ordinal));
                }
            }
        }

        Assert.True(shadowed.Count == 0, "Answered in more than one file: " + string.Join("; ", shadowed));
    }

    private static string FileOf(string where) => where.Split(':')[0];

    private static string Up(string from, string marker)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, marker))) return dir.FullName;
        }

        throw new InvalidOperationException($"No {marker} above {from}.");
    }

    // A guard may hold "==" but not "=>": the arm's arrow ends it.
    [GeneratedRegex(@"^\s*(?<pattern>\[[^\]]*\](?:\s+or\s+\[[^\]]*\])*)\s*(?<guard>when\s.*?)?\s*=>")]
    private static partial Regex Arm();

    [GeneratedRegex(@"\s+or\s+")]
    private static partial Regex Or();

    [GeneratedRegex(@"var \w+")]
    private static partial Regex Placeholder();
}
