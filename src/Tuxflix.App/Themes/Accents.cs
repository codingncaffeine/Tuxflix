using Avalonia;
using Avalonia.Media;

namespace Tuxflix.App.Themes;

/// <summary>An accent colour: selection, focus rings, progress and the small highlights.</summary>
public sealed record Accent(string Key, string Name, Color Main, Color Light, Color Dark, Color Ink)
{
    public override string ToString() => Name;
}

/// <summary>
/// The accent colours on offer, and putting one on: the brushes every view reaches as dynamic
/// resources are replaced, so the whole interface takes the new colour at once. Play keeps the
/// brand's warm ribbon whichever accent is chosen.
/// </summary>
public static class Accents
{
    public static IReadOnlyList<Accent> All { get; } =
    [
        new("orange", "Orange", Color.Parse("#FF9150"), Color.Parse("#FFB27E"), Color.Parse("#E86F32"), Color.Parse("#2A1204")),
        new("sky", "Sky", Color.Parse("#4FB3FF"), Color.Parse("#86CBFF"), Color.Parse("#2A93E6"), Color.Parse("#04182A")),
        new("violet", "Violet", Color.Parse("#A98BFF"), Color.Parse("#C4B0FF"), Color.Parse("#8665F0"), Color.Parse("#170A33")),
        new("coral", "Coral", Color.Parse("#FF6F7D"), Color.Parse("#FF9CA5"), Color.Parse("#E84E5E"), Color.Parse("#2E0710")),
        new("mint", "Mint", Color.Parse("#45D6A4"), Color.Parse("#7FE6C2"), Color.Parse("#22B684"), Color.Parse("#032419")),
    ];

    public static Accent Find(string? key) => All.FirstOrDefault(a => a.Key == key) ?? All[0];

    /// <summary>Puts <paramref name="accent"/> on the application's resources. UI thread.</summary>
    public static void Apply(Accent accent)
    {
        ArgumentNullException.ThrowIfNull(accent);
        if (Application.Current?.Resources is not { } resources) return;
        resources["Color.Accent"] = accent.Main;
        resources["Color.Accent.Light"] = accent.Light;
        resources["Color.Accent.Dark"] = accent.Dark;
        resources["Color.Accent.Ink"] = accent.Ink;
        resources["Brush.Accent"] = new SolidColorBrush(accent.Main);
        resources["Brush.Accent.Ink"] = new SolidColorBrush(accent.Ink);
        resources["Brush.Accent.Soft"] = new SolidColorBrush(WithAlpha(accent.Main, 0x2E));
        resources["Brush.Accent.Softer"] = new SolidColorBrush(WithAlpha(accent.Main, 0x17));
        resources["Brush.Accent.Edge"] = new SolidColorBrush(WithAlpha(accent.Main, 0x66));
        resources["Brush.Accent.Selected"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = [new GradientStop(WithAlpha(accent.Main, 0x33), 0), new GradientStop(WithAlpha(accent.Main, 0x06), 1)],
        };
    }

    private static Color WithAlpha(Color colour, byte alpha) => Color.FromArgb(alpha, colour.R, colour.G, colour.B);
}
