using System.IO.Compression;
using Avalonia;
using Avalonia.Headless;
using Tuxflix.App.Classic;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// Classic skins as files: names in any case or folder, sheets borrowed from the base skin, files
/// that are not skins, and zip entries too large to be sheets.
/// </summary>
public sealed class ClassicSkinTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "classic-" + Guid.NewGuid().ToString("N")[..8]);

    public ClassicSkinTests()
    {
        // Decoding a sheet needs a renderer; the headless platform with Skia is enough.
        HeadlessSkia.Ensure();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void SheetsAreFoundInAnyCaseAndFolder()
    {
        var path = Zip("mixed.wsz", ("Skin Folder/MAIN.BMP", Bmp(275, 116)), ("text.Bmp", Bmp(155, 18)), ("VISCOLOR.TXT", "10,20,30, // background\n"u8.ToArray()));

        using var skin = ClassicSkin.Load(path, fallback: null);

        Assert.Equal(new PixelSize(275, 116), skin.Sheet(ClassicSprites.Main)?.PixelSize);
        Assert.Equal(new PixelSize(155, 18), skin.Sheet(ClassicSprites.Text)?.PixelSize);
        Assert.Equal(Avalonia.Media.Color.FromRgb(10, 20, 30), skin.VisColors[0]);
    }

    [Fact]
    public void MissingSheetsAreBorrowedAndOutliveTheBorrower()
    {
        using var fallback = ClassicSkin.Load(Zip("base.wsz", ("main.bmp", Bmp(275, 116)), ("cbuttons.bmp", Bmp(136, 36))), fallback: null);
        var skin = ClassicSkin.Load(Zip("partial.wsz", ("main.bmp", Bmp(275, 116))), fallback);

        Assert.Same(fallback.Sheet(ClassicSprites.Cbuttons), skin.Sheet(ClassicSprites.Cbuttons));
        Assert.NotSame(fallback.Sheet(ClassicSprites.Main), skin.Sheet(ClassicSprites.Main));

        // Letting go of the skin leaves the borrowed sheet usable for the others that borrow it.
        skin.Dispose();
        Assert.Equal(new PixelSize(136, 36), fallback.Sheet(ClassicSprites.Cbuttons)!.PixelSize);
    }

    [Fact]
    public void BalanceFallsBackToTheVolumeSheet()
    {
        using var skin = ClassicSkin.Load(Zip("volume-only.wsz", ("main.bmp", Bmp(275, 116)), ("volume.bmp", Bmp(68, 433))), fallback: null);

        Assert.NotNull(skin.Sheet(ClassicSprites.Volume));
        Assert.Same(skin.Sheet(ClassicSprites.Volume), skin.Sheet(ClassicSprites.Balance));
    }

    [Fact]
    public void AnEntryTooLargeForASheetIsNotLoaded()
    {
        // A real bitmap, readable, but larger than any sheet: without the limit, it would load.
        var large = Bmp(1600, 1000);
        Assert.True(large.Length > 4 * 1024 * 1024);

        using var skin = ClassicSkin.Load(Zip("large.wsz", ("main.bmp", large), ("text.bmp", Bmp(155, 18))), fallback: null);

        Assert.Null(skin.Sheet(ClassicSprites.Main));
        Assert.NotNull(skin.Sheet(ClassicSprites.Text));
    }

    [Fact]
    public async Task ImportRefusesFilesThatAreNotSkins()
    {
        var library = new SkinLibrary(Path.Combine(_folder, "library"));
        var text = Path.Combine(_folder, "notes.wsz");
        await File.WriteAllTextAsync(text, "not a zip", TestContext.Current.CancellationToken);
        var noMain = Zip("no-main.wsz", ("text.bmp", Bmp(155, 18)));

        await Assert.ThrowsAsync<InvalidDataException>(() => library.ImportAsync(text));
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => library.ImportAsync(noMain));
        Assert.Contains("main window", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await library.ListAsync());
    }

    [Fact]
    public async Task ImportKeepsOneCopyOfASkinAndNumbersAnotherOfTheSameName()
    {
        var library = new SkinLibrary(Path.Combine(_folder, "library"));
        var first = Zip("Chrome.wsz", ("main.bmp", Bmp(275, 116)));
        var other = Path.Combine(_folder, "other");
        Directory.CreateDirectory(other);
        var second = Zip(Path.Combine("other", "Chrome.wsz"), ("main.bmp", Bmp(275, 116, shade: 90)));

        Assert.Equal("Chrome.wsz", await library.ImportAsync(first));
        Assert.Equal("Chrome.wsz", await library.ImportAsync(first));
        Assert.Equal("Chrome (2).wsz", await library.ImportAsync(second));
        Assert.Equal(["Chrome", "Chrome (2)"], (await library.ListAsync()).Select(s => s.Label));
    }

    [Fact]
    public async Task TheListPutsTheBaseSkinFirstThenNames()
    {
        var folder = Path.Combine(_folder, "library");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "zeta.wsz", SkinLibrary.BaseFileName, "Alpha.wsz", "notes.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(folder, name), "x", TestContext.Current.CancellationToken);
        }

        var list = await new SkinLibrary(folder).ListAsync();

        Assert.Equal(["Base skin", "Alpha", "zeta"], list.Select(s => s.Label));
        Assert.True(list[0].IsBase);
    }

    [Fact]
    public void VisColorsKeepWinampsForLinesThatDoNotParse()
    {
        var colors = ClassicSkin.ParseVisColors("1,2,3 // background\nbroken line\n4, 5, 6\n");
        var defaults = ClassicSkin.DefaultVisColors();

        Assert.Equal(24, colors.Length);
        Assert.Equal(Avalonia.Media.Color.FromRgb(1, 2, 3), colors[0]);
        Assert.Equal(Avalonia.Media.Color.FromRgb(4, 5, 6), colors[1]);
        Assert.Equal(defaults[2..], colors[2..]);
    }

    [Fact]
    public void ThePlaylistStyleReadsItsTextSection()
    {
        var style = ClassicSkin.ParsePlaylist("[Text]\r\nNormal=#00FF00\r\nCurrent=#FFFFFF\r\nNormalBG=#000000\r\nSelectedBG=#0000C6\r\nFont=Tahoma\r\n");

        Assert.Equal(Avalonia.Media.Color.FromRgb(0, 0xFF, 0), style.Normal);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0, 0, 0xC6), style.SelectedBackground);
        Assert.Equal("Tahoma", style.Font);
        Assert.Equal(PlaylistStyle.Default.Current, ClassicSkin.ParsePlaylist("[Text]\nCurrent=nonsense\n").Current);
    }

    [Fact]
    public void TextTheFontLacksFoldsToTheNearestCharacter()
    {
        Assert.Equal(ClassicSprites.Character('\''), ClassicSprites.Character((char)0x2019));
        Assert.Equal(ClassicSprites.Character('-'), ClassicSprites.Character((char)0x2014));
        Assert.Equal(ClassicSprites.Character('e'), ClassicSprites.Character((char)0xE9));
        Assert.Equal(ClassicSprites.Character('E'), ClassicSprites.Character('e'));
        Assert.Equal(ClassicSprites.Character(' '), ClassicSprites.Character((char)0x2603));
    }

    [Fact]
    public void TheBuiltInPresetsAreWinampsInDecibels()
    {
        Assert.Equal(17, EqPresets.BuiltIn.Count);
        Assert.All(EqPresets.BuiltIn, p =>
        {
            Assert.Equal(10, p.Bands.Length);
            Assert.All(p.Bands, b => Assert.InRange(b, -12, 12));
        });

        // Classical cuts the top: 16 kHz sits at step 16 of 64, well below flat.
        Assert.Equal(-6.6, EqPresets.BuiltIn.Single(p => p.Name == "Classical").Bands[9]);
        Assert.All(EqPresets.Flat.Bands, b => Assert.Equal(0, b));
    }

    private string Zip(string name, params (string Entry, byte[] Data)[] entries)
    {
        var path = Path.Combine(_folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(data);
        }

        return path;
    }

    /// <summary>A 24-bit bitmap of one colour.</summary>
    private static byte[] Bmp(int width, int height, byte shade = 40)
    {
        var row = ((width * 3) + 3) & ~3;
        var size = 54 + (row * height);
        var bytes = new byte[size];
        "BM"u8.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), size);
        BitConverter.TryWriteBytes(bytes.AsSpan(10), 54);
        BitConverter.TryWriteBytes(bytes.AsSpan(14), 40);
        BitConverter.TryWriteBytes(bytes.AsSpan(18), width);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), height);
        BitConverter.TryWriteBytes(bytes.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), (short)24);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = 54 + (y * row) + (x * 3);
                bytes[at] = shade;
                bytes[at + 1] = shade;
                bytes[at + 2] = shade;
            }
        }

        return bytes;
    }
}
