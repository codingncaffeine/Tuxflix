using System.Globalization;
using System.IO.Compression;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.App.Classic;

/// <summary>The playlist's colours and font, from a skin's <c>pledit.txt</c>.</summary>
public sealed record PlaylistStyle(Color Normal, Color Current, Color NormalBackground, Color SelectedBackground, string Font)
{
    public static PlaylistStyle Default { get; } = new(
        Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0x00, 0x00, 0xC6), "Arial");
}

/// <summary>
/// A classic (Winamp 2) skin: the sheets of a <c>.wsz</c> file, its visualizer colours and its
/// playlist style.
/// </summary>
/// <remarks>
/// A <c>.wsz</c> is a zip of bitmaps and a few text files. Names are matched without regard to
/// case or folder, as Winamp did, since skins spell <c>Main.bmp</c>, <c>MAIN.BMP</c> and
/// <c>skin/main.bmp</c> alike. A sheet a skin leaves out comes from the fallback skin, again as
/// Winamp did; a skin without <c>balance.bmp</c> uses its volume sheet. Loading reads and decodes
/// on a worker: never on the UI thread.
/// </remarks>
public sealed class ClassicSkin : IDisposable
{
    /// <summary>A sheet is a small bitmap; a zip entry claiming more is not one (and may be a zip bomb).</summary>
    private const long MaxSheetBytes = 4 * 1024 * 1024;

    private const long MaxTextBytes = 64 * 1024;

    private readonly Dictionary<string, Bitmap> _sheets;
    private readonly HashSet<string> _owned;

    private ClassicSkin(string name, Dictionary<string, Bitmap> sheets, HashSet<string> owned, Color[] visColors, PlaylistStyle playlist)
    {
        Name = name;
        _sheets = sheets;
        _owned = owned;
        VisColors = visColors;
        Playlist = playlist;
    }

    public string Name { get; }

    /// <summary>24 colours: 0 background, 1 grid dots, 2-17 the analyser top to bottom, 18-22 the scope, 23 the peaks.</summary>
    public Color[] VisColors { get; }

    public PlaylistStyle Playlist { get; }

    /// <summary>Whether the skin has the extended digits (with a minus sign) of <c>nums_ex.bmp</c>.</summary>
    public bool HasExtendedDigits => _sheets.ContainsKey(ClassicSprites.NumsEx);

    public Bitmap? Sheet(string name) => _sheets.GetValueOrDefault(name);

    /// <summary>A skin with nothing in it: drawing falls back to plain shapes.</summary>
    public static ClassicSkin Empty() => new("None", [], [], DefaultVisColors(), PlaylistStyle.Default);

    /// <summary>Reads a <c>.wsz</c> (or <c>.zip</c>) file. Blocking: call it on a worker.</summary>
    public static ClassicSkin Load(string path, ClassicSkin? fallback)
    {
        if (new FileInfo(path).Length > SkinLibrary.MaxSkinBytes) throw new InvalidDataException("The file is too large to be a classic skin.");
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries
            .Where(e => e.Length > 0)
            .GroupBy(e => Path.GetFileName(e.FullName).ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var sheets = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sheet in ClassicSprites.Sheets)
        {
            if (Find(entries, sheet, ".bmp", ".png") is not { } entry) continue;
            if (entry.Length > MaxSheetBytes)
            {
                Log.Warn($"Classic skin {Path.GetFileName(path)}: {entry.FullName} is too large for a sheet; skipped.");
                continue;
            }

            try
            {
                using var source = entry.Open();
                using var buffer = new MemoryStream();
                CopyAtMost(source, buffer, MaxSheetBytes);
                Imaging.ImageBounds.Check(buffer.ToArray(), Imaging.ImageBounds.SmallPixels);
                buffer.Position = 0;
                sheets[sheet] = new Bitmap(buffer);
                owned.Add(sheet);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                Log.Warn($"Classic skin {Path.GetFileName(path)}: {entry.FullName} could not be read ({ex.Message}).");
            }
        }

        // No balance sheet: Winamp used the volume sheet for both.
        if (!sheets.ContainsKey(ClassicSprites.Balance) && sheets.TryGetValue(ClassicSprites.Volume, out var volume)) sheets[ClassicSprites.Balance] = volume;

        if (fallback is not null)
        {
            foreach (var sheet in ClassicSprites.Sheets)
            {
                if (!sheets.ContainsKey(sheet) && fallback.Sheet(sheet) is { } borrowed) sheets[sheet] = borrowed;
            }
        }

        var vis = Find(entries, "viscolor", ".txt") is { } visEntry ? ParseVisColors(ReadText(visEntry)) : fallback?.VisColors ?? DefaultVisColors();
        var playlist = Find(entries, "pledit", ".txt") is { } plEntry ? ParsePlaylist(ReadText(plEntry)) : fallback?.Playlist ?? PlaylistStyle.Default;
        return new ClassicSkin(Path.GetFileNameWithoutExtension(path), sheets, owned, vis, playlist);
    }

    public void Dispose()
    {
        foreach (var (name, bitmap) in _sheets)
        {
            if (_owned.Contains(name)) bitmap.Dispose();
        }

        _sheets.Clear();
    }

    /// <summary>Winamp's own default analyser colours: red down through orange and yellow to green, white scope, grey peaks.</summary>
    public static Color[] DefaultVisColors() =>
    [
        Color.FromRgb(0, 0, 0), Color.FromRgb(24, 33, 41),
        Color.FromRgb(239, 49, 16), Color.FromRgb(206, 41, 16), Color.FromRgb(214, 90, 0), Color.FromRgb(214, 102, 0),
        Color.FromRgb(214, 115, 0), Color.FromRgb(198, 123, 8), Color.FromRgb(222, 165, 24), Color.FromRgb(214, 181, 33),
        Color.FromRgb(189, 222, 41), Color.FromRgb(148, 222, 33), Color.FromRgb(41, 206, 16), Color.FromRgb(50, 190, 16),
        Color.FromRgb(57, 181, 16), Color.FromRgb(49, 156, 8), Color.FromRgb(41, 148, 0), Color.FromRgb(24, 132, 8),
        Color.FromRgb(255, 255, 255), Color.FromRgb(214, 214, 222), Color.FromRgb(181, 189, 189), Color.FromRgb(160, 170, 175),
        Color.FromRgb(148, 156, 165), Color.FromRgb(150, 150, 150),
    ];

    /// <summary>24 lines of "r,g,b" with anything after them ignored; a missing or unreadable line keeps Winamp's colour.</summary>
    internal static Color[] ParseVisColors(string text)
    {
        var colors = DefaultVisColors();
        var index = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (index >= colors.Length) break;
            var line = raw.Split("//")[0];
            var numbers = line.Split([',', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (int?)n : null)
                .Where(n => n is not null).Select(n => n!.Value).Take(3).ToArray();
            if (numbers.Length < 3) continue;
            colors[index++] = Color.FromRgb((byte)Math.Clamp(numbers[0], 0, 255), (byte)Math.Clamp(numbers[1], 0, 255), (byte)Math.Clamp(numbers[2], 0, 255));
        }

        return colors;
    }

    /// <summary>The <c>[Text]</c> section of <c>pledit.txt</c>: Normal, Current, NormalBG, SelectedBG and Font.</summary>
    internal static PlaylistStyle ParsePlaylist(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        var d = PlaylistStyle.Default;
        return new PlaylistStyle(
            ParseColor(values.GetValueOrDefault("Normal"), d.Normal),
            ParseColor(values.GetValueOrDefault("Current"), d.Current),
            ParseColor(values.GetValueOrDefault("NormalBG"), d.NormalBackground),
            ParseColor(values.GetValueOrDefault("SelectedBG"), d.SelectedBackground),
            values.GetValueOrDefault("Font") is { Length: > 0 } font ? font : d.Font);
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (value is null) return fallback;
        var hex = value.TrimStart('#').Trim();
        if (hex.Length >= 6 && uint.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return fallback;
    }

    /// <summary>The size a zip entry declares is only a claim: the copy stops at the limit whatever it says.</summary>
    private static void CopyAtMost(Stream source, Stream target, long limit)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("The entry is larger than it claims.");
            target.Write(chunk, 0, read);
        }
    }

    private static ZipArchiveEntry? Find(Dictionary<string, ZipArchiveEntry> entries, string name, params string[] extensions) =>
        extensions.Select(ext => entries.GetValueOrDefault(name + ext)).FirstOrDefault(e => e is not null);

    private static string ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxTextBytes) return string.Empty;
        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.Latin1);
        return reader.ReadToEnd();
    }
}
